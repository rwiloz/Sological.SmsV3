using Microsoft.EntityFrameworkCore;
using Sological.Sms.Core.Entities;

namespace Sological.Sms.Service.Data;

/// <summary>The one store all three planes are mediated by (design §1). Tables and
/// indexes follow design §3; snake_case naming comes from the naming-convention plugin,
/// enum values are stored lowercase.</summary>
public class SmsDbContext(DbContextOptions<SmsDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<DeliveryEvent> DeliveryEvents => Set<DeliveryEvent>();
    public DbSet<InboundMessage> InboundMessages => Set<InboundMessage>();
    public DbSet<InboundPart> InboundParts => Set<InboundPart>();
    public DbSet<BillingLedgerEntry> BillingLedger => Set<BillingLedgerEntry>();
    public DbSet<WebhookOutboxEntry> WebhookOutbox => Set<WebhookOutboxEntry>();
    public DbSet<AllowedOriginator> AllowedOriginators => Set<AllowedOriginator>();
    public DbSet<UpstreamBreaker> UpstreamBreakers => Set<UpstreamBreaker>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<CustomerStatus>().HaveConversion<LowercaseEnumConverter<CustomerStatus>>().HaveMaxLength(16);
        configurationBuilder.Properties<ChannelStatus>().HaveConversion<LowercaseEnumConverter<ChannelStatus>>().HaveMaxLength(16);
        configurationBuilder.Properties<UpstreamProvider>().HaveConversion<LowercaseEnumConverter<UpstreamProvider>>().HaveMaxLength(16);
        configurationBuilder.Properties<MessageStatus>().HaveConversion<LowercaseEnumConverter<MessageStatus>>().HaveMaxLength(16);
        configurationBuilder.Properties<LedgerRefType>().HaveConversion<LowercaseEnumConverter<LedgerRefType>>().HaveMaxLength(16);
        configurationBuilder.Properties<SmsDirection>().HaveConversion<LowercaseEnumConverter<SmsDirection>>().HaveMaxLength(16);
        configurationBuilder.Properties<WebhookOutboxState>().HaveConversion<LowercaseEnumConverter<WebhookOutboxState>>().HaveMaxLength(16);
        configurationBuilder.Properties<WebhookEventType>().HaveConversion<WebhookEventTypeConverter>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(32);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<Channel>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64);
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Originator).HasMaxLength(20);
            // Explicit names: the convention would render these api_key1_hash / api_key2_hash.
            e.Property(x => x.ApiKey1Hash).HasColumnName("api_key_1_hash").HasMaxLength(128);
            e.Property(x => x.ApiKey2Hash).HasColumnName("api_key_2_hash").HasMaxLength(128);
            e.Property(x => x.WebhookUrl).HasMaxLength(500);
            e.Property(x => x.WebhookSecretName).HasMaxLength(100);
            // Real DB default so raw-SQL channel seeding can omit it (design §6.1a: 1h default).
            e.Property(x => x.DuplicateWindowSeconds).HasDefaultValue(3600);
            e.HasOne(x => x.Customer).WithMany(x => x.Channels)
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.Property(x => x.CustomerRef).HasMaxLength(64);
            e.Property(x => x.ToNumber).HasMaxLength(20);
            e.Property(x => x.OriginatorUsed).HasMaxLength(20);
            e.Property(x => x.ErrorCode).HasMaxLength(32);
            e.Property(x => x.UpstreamId).HasMaxLength(64);
            e.Property(x => x.ClaimedBy).HasMaxLength(64);
            // Deliberately NOT unique — ref-uniqueness is enforced per-surface in the API
            // layer (409 on the new surface only; S5 legacy accepts duplicates).
            e.HasIndex(x => new { x.ChannelId, x.CustomerRef });
            // The dispatch/DLR working set: partial on non-terminal statuses (design §3).
            e.HasIndex(x => x.Status)
                .HasDatabaseName("ix_messages_status_nonterminal")
                .HasFilter("status IN ('queued', 'submitting', 'sent')");
            e.HasOne(x => x.Channel).WithMany()
                .HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeliveryEvent>(e =>
        {
            e.Property(x => x.RawResult).HasMaxLength(16);
            e.Property(x => x.RawStatus).HasMaxLength(32);
            e.Property(x => x.RawDescription).HasMaxLength(500);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            // The breaker's windowed count of uncorrelated DRs (public-surface slice).
            e.HasIndex(x => x.ReceivedAt)
                .HasDatabaseName("ix_delivery_events_unmatched")
                .HasFilter("message_id IS NULL");
            e.HasOne(x => x.Message).WithMany()
                .HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UpstreamBreaker>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(200);
            e.Property(x => x.AlertError).HasMaxLength(500);
            // The latch: ONE active (un-re-armed) row per upstream; history stays.
            e.HasIndex(x => x.Upstream)
                .IsUnique()
                .HasDatabaseName("ix_upstream_breakers_active")
                .HasFilter("re_armed_at IS NULL");
        });

        modelBuilder.Entity<InboundMessage>(e =>
        {
            e.Property(x => x.FromNumber).HasMaxLength(20);
            e.Property(x => x.ToNumber).HasMaxLength(20);
            e.Property(x => x.UpstreamId).HasMaxLength(64);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ChannelId, x.ReceivedAt });
            e.HasOne(x => x.Channel).WithMany()
                .HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReplyToMessage).WithMany()
                .HasForeignKey(x => x.ReplyToMessageId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InboundPart>(e =>
        {
            e.HasKey(x => new { x.ChannelId, x.FromNumber, x.GroupRef, x.PartNo });
            e.Property(x => x.FromNumber).HasMaxLength(20);
            e.Property(x => x.GroupRef).HasMaxLength(32);
            e.HasOne<Channel>().WithMany()
                .HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BillingLedgerEntry>(e =>
        {
            e.HasIndex(x => new { x.CustomerId, x.OccurredAt }); // monthly rollups (S7)
            e.HasOne<Customer>().WithMany()
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Channel>().WithMany()
                .HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AllowedOriginator>(e =>
        {
            e.HasKey(x => x.Originator);
            e.Property(x => x.Originator).HasMaxLength(20);
            e.Property(x => x.Description).HasMaxLength(200);
        });

        modelBuilder.Entity<WebhookOutboxEntry>(e =>
        {
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.ClaimedBy).HasMaxLength(64);
            // The egress worker's scan: pending rows due for an attempt.
            e.HasIndex(x => new { x.State, x.NextAttemptAt })
                .HasDatabaseName("ix_webhook_outbox_pending")
                .HasFilter("state = 'pending'");
            e.HasOne(x => x.Channel).WithMany()
                .HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
