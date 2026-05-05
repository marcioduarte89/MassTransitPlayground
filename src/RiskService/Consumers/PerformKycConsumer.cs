using MassTransit;
using MassTransitPlayground.Contracts.Commands;
using MassTransitPlayground.Contracts.Events;
using MassTransitPlayground.RiskService.Data;
using MassTransitPlayground.RiskService.Domain;

namespace MassTransitPlayground.RiskService.Consumers;

/// <summary>
/// Handles the <see cref="IPerformKyc"/> command sent by the Registration Service.
/// Performs a dummy KYC check, persists the result, and publishes <see cref="ICustomerValidated"/>.
///
/// ------------------------------------------------------------------
/// RECEIVING COMMANDS vs EVENTS IN MT
/// ------------------------------------------------------------------
/// From a consumer perspective, COMMANDS and EVENTS are handled identically —
/// both implement IConsumer&lt;TMessage&gt;. The distinction is at the SENDER level:
///   - Commands are sent (Send) to a specific queue → one receiver
///   - Events are published (Publish) to a topic → zero or more receivers
///
/// The PerformKycConsumer receives a COMMAND sent directly to its queue
/// ("perform-kyc"). No other service receives this command (point-to-point).
///
/// SENDING vs RECEIVING QUEUE NAMES:
///   The Registration Service sends to: new Uri("queue:perform-kyc")
///   MT created this queue when the Risk Service started and registered
///   PerformKycConsumer → ConfigureEndpoints → queue "perform-kyc".
///
///   IMPORTANT: Both services must agree on the queue name.
///   In practice, this is managed by:
///     1. A shared endpoint name convention (same name formatter across services)
///     2. Or explicit hardcoding (as in our CustomersController)
///     3. Or using EndpointConvention.Map<IPerformKyc>(uri) for routing at startup
///
/// NSB EQUIVALENT:
///   In NSB, the routing table maps IPerformKyc → "risk-service" endpoint.
///   The risk-service endpoint's input queue is named explicitly.
///   MT's auto-convention achieves the same result but from the receiver's perspective.
///
/// ------------------------------------------------------------------
/// CONSUMER OUTBOX — TRANSACTIONAL PUBLISH FROM WITHIN A CONSUMER
/// ------------------------------------------------------------------
/// When this consumer calls context.Publish<ICustomerValidated>(...), MT does NOT
/// immediately send to the transport. Instead, because AddEntityFrameworkOutbox is
/// configured, the message is written to the OutboxMessage table in RiskDb as part
/// of the SAME EF transaction that saves the KycRecord.
///
/// Timeline of a successful Consume() call:
///   1. MT delivers IPerformKyc from transport queue
///   2. MT creates a DI scope → resolves PerformKycConsumer
///   3. Consumer saves KycRecord (EF tracks the change)
///   4. Consumer calls context.Publish<ICustomerValidated>(...) →
///      MT writes the event to OutboxMessage (in-memory EF entity)
///   5. Consumer calls _dbContext.SaveChangesAsync() →
///      EF writes KycRecord AND OutboxMessage to RiskDb atomically
///   6. MT marks the IPerformKyc message as consumed (removes from transport queue)
///   7. OutboxDeliveryService reads OutboxMessage and publishes ICustomerValidated
///      to the transport topic → Registration Service picks it up
///
/// If step 5 fails (DB error):
///   - Neither KycRecord nor OutboxMessage is committed
///   - IPerformKyc is NOT acknowledged → MT retries it
///   - No orphaned events published
///
/// If step 6 fails (transport issue):
///   - The InboxState table records the MessageId, ensuring idempotency on retry
///   - OutboxMessage is already committed → event will be delivered on next poll
///
/// NSB COMPARISON:
///   This is equivalent to NSB's handler outbox:
///   When IHandleMessages<PerformKyc>.Handle() calls session.Publish(new CustomerValidated()),
///   NSB stores that outgoing message in the Outbox table atomically with any DB writes.
///   The delivery semantics are identical.
/// ------------------------------------------------------------------
/// </summary>
public class PerformKycConsumer : IConsumer<IPerformKyc>
{
    private readonly RiskDbContext _dbContext;
    private readonly ILogger<PerformKycConsumer> _logger;

    public PerformKycConsumer(RiskDbContext dbContext, ILogger<PerformKycConsumer> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<IPerformKyc> context)
    {
        var message = context.Message;

        _logger.LogInformation(
            "Processing KYC for CustomerId: {CustomerId}, Name: {FirstName} {LastName}",
            message.CustomerId, message.FirstName, message.LastName);

        // ------------------------------------------------------------------
        // DUMMY KYC LOGIC
        // ------------------------------------------------------------------
        // In a real system, this would call a third-party KYC provider.
        // Here we use a simple deterministic rule for demonstration:
        //   - Customers with email starting with "reject" are rejected
        //   - All others are approved
        //   - We add a small delay to simulate real-world processing time
        // ------------------------------------------------------------------
        await Task.Delay(TimeSpan.FromMilliseconds(500), context.CancellationToken);

        var isApproved = !message.Email.StartsWith("reject", StringComparison.OrdinalIgnoreCase);
        var reason = isApproved
            ? "All KYC checks passed. Identity verified."
            : "KYC rejected: email domain flagged by compliance rules.";

        var kycRecord = KycRecord.Create(
            message.CustomerId,
            message.FirstName,
            message.LastName,
            message.Email,
            message.DateOfBirth,
            isApproved,
            reason);

        _dbContext.KycRecords.Add(kycRecord);

        // ------------------------------------------------------------------
        // PUBLISHING AN EVENT FROM WITHIN A CONSUMER
        // ------------------------------------------------------------------
        // We use ConsumeContext.Publish() (NOT IBus.Publish()) to publish the
        // ICustomerValidated event.
        //
        // WHY ConsumeContext INSTEAD OF IBus?
        //   ConsumeContext.Publish() is outbox-aware: the event is stored in the
        //   OutboxMessage table and delivered atomically with the SaveChangesAsync() call.
        //   IBus.Publish() would bypass the consumer outbox in some configurations.
        //
        //   Always prefer ConsumeContext for all messaging within a consumer.
        //   ConsumeContext gives you:
        //     - context.Publish()      — publish an event (outbox-aware)
        //     - context.Send()         — send a command (outbox-aware)
        //     - context.RespondAsync() — reply to a request/response caller
        //     - context.Forward()      — forward the current message to another endpoint
        //
        // MT MESSAGE INITIALIZER SYNTAX:
        //   We pass an anonymous object to Publish<ICustomerValidated>(new { ... }).
        //   MT uses reflection/source generators to match the anonymous object's
        //   properties to the ICustomerValidated interface properties by name.
        //   This is the "message initializer" pattern — cleaner than creating a
        //   concrete class implementing the interface.
        //
        //   Property name matching is case-insensitive and handles common patterns:
        //     CustomerId → CustomerId ✓
        //     customerId → CustomerId ✓ (camelCase → PascalCase)
        //
        // NSB EQUIVALENT:
        //   await context.Publish(new CustomerValidated
        //   {
        //       CustomerId = message.CustomerId,
        //       IsApproved = isApproved,
        //       ...
        //   });
        //   NSB uses concrete classes, MT prefers interfaces + anonymous initializers.
        // ------------------------------------------------------------------
        await context.Publish<ICustomerValidated>(new
        {
            message.CustomerId,
            IsApproved = isApproved,
            Reason = reason,
            ValidatedAt = DateTime.UtcNow
        }, context.CancellationToken);

        // Atomically commits KycRecord + OutboxMessage (ICustomerValidated event)
        await _dbContext.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation(
            "KYC completed for CustomerId: {CustomerId}. Approved: {IsApproved}",
            message.CustomerId, isApproved);
    }
}
