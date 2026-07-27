using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sological.Sms.Core.Entities;
using Sological.Sms.Service.Data;
using Testcontainers.PostgreSql;

namespace Sological.Sms.Tests;

/// <summary>The S1 gate, as a repeatable test: migrations apply clean on an EMPTY database
/// (fresh container per test), a second Migrate is a no-op, and every table round-trips —
/// including the jsonb payload mappings, which only a real PostgreSQL can prove.
/// Integration category: runs locally only, never on hosted CI (standing rule).</summary>
[Trait("Category", "Integration")]
public sealed class MigrationsIntegrationTests : IAsyncLifetime
{
    // Image matches the Azure PSQL flexible-server major.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private SmsDbContext CreateContext()
        => new(DesignTimeDbContextFactory.BuildOptions(_postgres.GetConnectionString()));

    [Fact]
    public async Task Migrations_ApplyClean_OnAnEmptyDatabase_AndAreIdempotent()
    {
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }

        // Second run against the now-migrated database must be a clean no-op
        // (the service migrates on every start).
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }
    }

    [Fact]
    public async Task EveryTable_RoundTrips_IncludingJsonbPayloads()
    {
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();

            var customer = new Customer { Code = "testco", Name = "Test Co" };
            var channel = new Channel { Customer = customer, Key = "TestChannel", Originator = "shared" };
            var message = new Message
            {
                Channel = channel,
                CustomerRef = "r1",
                ToNumber = "+61400000001",
                OriginatorUsed = "shared",
                Body = "hello",
                Parts = 1,
            };
            db.Messages.Add(message);
            db.DeliveryEvents.Add(new DeliveryEvent
            {
                Message = message,
                Provider = "smscentral",
                RawResult = "1",
                RawStatus = "DELIVRD",
                Payload = new Dictionary<string, string> { ["RESULT"] = "1", ["STATUS"] = "DELIVRD" },
            });
            db.DeliveryEvents.Add(new DeliveryEvent
            {
                // Unknown REFERENCE → audit-only row, no message FK (design §5.1).
                Provider = "smscentral",
                Payload = new Dictionary<string, string> { ["REFERENCE"] = "not-ours" },
            });
            db.InboundMessages.Add(new InboundMessage
            {
                Channel = channel,
                FromNumber = "+61400000002",
                Body = "reply",
                ReplyToMessage = message,
                Payload = new Dictionary<string, string> { ["MESSAGE_TEXT"] = "reply" },
            });
            await db.SaveChangesAsync();

            db.InboundParts.Add(new InboundPart
            {
                ChannelId = channel.Id,
                FromNumber = "+61400000002",
                GroupRef = "77",
                PartNo = 1,
                TotalParts = 2,
                BodyFragment = "first half…",
                Dcs = 8,
            });
            db.BillingLedger.Add(new BillingLedgerEntry
            {
                CustomerId = customer.Id,
                ChannelId = channel.Id,
                RefType = LedgerRefType.Message,
                RefId = message.Id,
                Direction = SmsDirection.Outbound,
                Units = 1,
            });
            db.WebhookOutbox.Add(new WebhookOutboxEntry
            {
                ChannelId = channel.Id,
                EventType = WebhookEventType.SmsDelivery,
                Payload = JsonDocument.Parse("""{"event":"sms.delivery","status":"delivered"}"""),
            });
            db.AllowedOriginators.Add(new AllowedOriginator { Originator = "TestSender" });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            (await db.Customers.CountAsync()).Should().Be(1);
            (await db.Channels.SingleAsync()).Key.Should().Be("TestChannel");

            var message = await db.Messages.SingleAsync();
            message.Status.Should().Be(MessageStatus.Queued);

            var events = await db.DeliveryEvents.ToListAsync();
            events.Should().HaveCount(2);
            var dlr = events.Single(x => x.MessageId != null);
            dlr.MessageId.Should().Be(message.Id);
            dlr.Payload["STATUS"].Should().Be("DELIVRD");
            var audit = events.Single(x => x.MessageId == null);
            audit.Payload["REFERENCE"].Should().Be("not-ours", "unknown-reference pushes keep an audit row");

            var inbound = await db.InboundMessages.SingleAsync();
            inbound.ReplyToMessageId.Should().Be(message.Id);
            inbound.DeliveredToCustomerAt.Should().BeNull("NULL means still owed to the customer");

            (await db.InboundParts.CountAsync()).Should().Be(1);
            (await db.BillingLedger.SingleAsync()).Units.Should().Be(1);

            var outbox = await db.WebhookOutbox.SingleAsync();
            outbox.State.Should().Be(WebhookOutboxState.Pending);
            outbox.Payload.RootElement.GetProperty("event").GetString().Should().Be("sms.delivery");

            (await db.AllowedOriginators.SingleAsync()).Originator.Should().Be("TestSender");
            (await db.Channels.SingleAsync()).DuplicateWindowSeconds.Should().Be(3600, "the DB default applies");
        }
    }
}
