using MassTransit;
using MassTransitPlayground.RiskService.Domain;
using Microsoft.EntityFrameworkCore;

namespace MassTransitPlayground.RiskService.Data;

/// <summary>
/// EF Core DbContext for the Risk Service.
///
/// Like the RegistrationDbContext, this includes MassTransit's outbox tables.
/// The outbox here ensures that:
///   - Saving a KycRecord to the database AND
///   - Publishing the ICustomerValidated event
/// happen atomically in a single database transaction.
///
/// This is the consumer outbox: when PerformKycConsumer calls context.Publish(),
/// MT stores the outgoing event in the OutboxMessage table as part of the consumer's
/// EF transaction. The event is only delivered to the transport if the consumer
/// commits successfully. If the consumer fails, the event is never published.
///
/// See RegistrationDbContext for detailed outbox documentation.
/// </summary>
public class RiskDbContext : DbContext
{
    public RiskDbContext(DbContextOptions<RiskDbContext> options) : base(options) { }

    public DbSet<KycRecord> KycRecords => Set<KycRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<KycRecord>(entity =>
        {
            entity.HasKey(k => k.Id);

            entity.Property(k => k.FirstName)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(k => k.LastName)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(k => k.Email)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(k => k.Reason)
                .IsRequired()
                .HasMaxLength(500);

            entity.HasIndex(k => k.CustomerId);
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
