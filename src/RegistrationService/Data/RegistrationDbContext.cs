using MassTransit;
using MassTransitPlayground.RegistrationService.Domain;
using Microsoft.EntityFrameworkCore;

namespace MassTransitPlayground.RegistrationService.Data;

/// <summary>
/// EF Core DbContext for the Registration Service.
///
/// ------------------------------------------------------------------
/// MT OUTBOX INTEGRATION WITH EF CORE
/// ------------------------------------------------------------------
/// MassTransit's EF Core outbox stores messages-to-be-delivered in the SAME database
/// as the business data. This is the key to transactional consistency:
///
///   Customer record + KYC command = ONE database transaction.
///
/// The outbox tables added by MassTransit are:
///
///   1. InboxState   — Tracks messages consumed by THIS service (deduplication).
///                     If the same message is delivered twice (e.g., after a crash and retry),
///                     the InboxState table prevents double-processing.
///                     → NSB equivalent: the Outbox table also handles deduplication in NSB.
///
///   2. OutboxMessage — Stores messages that need to be sent/published, committed atomically
///                      with business data. A background service (OutboxDeliveryService)
///                      reads these rows and forwards them to the transport (SQL transport DB).
///                      → NSB equivalent: NSB's Outbox records table.
///
///   3. OutboxState  — Tracks the state of outbox message batches. Used by the
///                     OutboxDeliveryService to know which message batches have been delivered.
///
///   Without the outbox, we have a "dual write" problem:
///     1. Save customer to DB → success
///     2. Send KYC command to transport → fails (network issue, etc.)
///   Result: customer is saved but KYC never happens → data inconsistency.
///
///   With the outbox:
///     1. Save customer + outbox message → one atomic DB transaction
///     2. If step 1 fails → nothing is saved (clean rollback)
///     3. If step 1 succeeds → background service delivers the message reliably
///     4. If delivery fails → background service retries (with the message safely in DB)
///
/// NSB COMPARISON:
///   This is identical in concept to NServiceBus's Outbox feature. The difference is
///   integration depth: MT's EF outbox is configured directly on the DbContext via
///   extension methods (AddInboxStateEntity, etc.), while NSB's outbox uses its own
///   persistence abstraction (NHibernate, SQL, CosmosDB, etc.).
/// ------------------------------------------------------------------
/// </summary>
public class RegistrationDbContext : DbContext
{
    public RegistrationDbContext(DbContextOptions<RegistrationDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(c => c.Id);

            entity.Property(c => c.FirstName)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(c => c.LastName)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(c => c.Email)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(c => c.Status)
                .HasConversion<string>()
                .HasMaxLength(20);

            entity.Property(c => c.ValidationReason)
                .HasMaxLength(500);

            entity.HasIndex(c => c.Email).IsUnique();
        });

        // ------------------------------------------------------------------
        // REQUIRED: Add MassTransit outbox tables to this DbContext.
        //
        // These three calls add EF entity configurations for the three outbox tables.
        // When you run 'dotnet ef migrations add' these will be included in the migration.
        //
        // IMPORTANT: All three must be added for the outbox to function correctly.
        //   - InboxState:   deduplication of incoming messages (idempotency)
        //   - OutboxMessage: pending outbound messages (not yet delivered to transport)
        //   - OutboxState:  tracks delivery progress per batch
        // ------------------------------------------------------------------
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
