using Microsoft.EntityFrameworkCore;
using Sological.Sms.Service.Data;

namespace Sological.Sms.Tests;

/// <summary>Model-only checks — no database. Guards the engineering-guide rule that every
/// entity/index change ships its migration in the same commit, and that the naming
/// convention renders exactly the design §3 tables.</summary>
public class ModelTests
{
    private static SmsDbContext CreateContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    [Fact]
    public void Model_HasNoPendingChanges_SoEveryEntityChangeShipsItsMigration()
    {
        using var context = CreateContext();
        context.Database.HasPendingModelChanges().Should().BeFalse(
            "any entity/index change must ship its EF migration in the same commit");
    }

    [Fact]
    public void Model_MapsExactlyTheDesignTables_InSnakeCase()
    {
        using var context = CreateContext();
        var tables = context.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .ToHashSet();

        tables.Should().BeEquivalentTo(
        [
            "customers",
            "channels",
            "messages",
            "delivery_events",
            "inbound_messages",
            "inbound_parts",
            "billing_ledger",
            "webhook_outbox",
            "allowed_originators", // S2: the ACMA sender-ID whitelist
        ]);
    }
}
