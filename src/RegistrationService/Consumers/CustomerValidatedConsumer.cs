using MassTransit;
using MassTransitPlayground.Contracts.Events;
using MassTransitPlayground.RegistrationService.Data;
using Microsoft.EntityFrameworkCore;

namespace MassTransitPlayground.RegistrationService.Consumers;

/// <summary>
/// Handles the <see cref="ICustomerValidated"/> event published by the Risk Service.
///
/// ------------------------------------------------------------------
/// MT CONSUMER: The Core Processing Unit
/// ------------------------------------------------------------------
/// In MassTransit, a consumer is a class that implements IConsumer&lt;TMessage&gt;.
/// It is the equivalent of NServiceBus's IHandleMessages&lt;TMessage&gt;.
///
/// KEY DIFFERENCES FROM NSB:
///
///   NSB:  public class Handler : IHandleMessages<CustomerValidated>
///         {
///             public async Task Handle(CustomerValidated message, IMessageHandlerContext context) { }
///         }
///
///   MT:   public class CustomerValidatedConsumer : IConsumer<ICustomerValidated>
///         {
///             public async Task Consume(ConsumeContext<ICustomerValidated> context) { }
///         }
///
///   The ConsumeContext&lt;T&gt; in MT is equivalent to IMessageHandlerContext in NSB.
///   Both give you access to:
///     - The message (context.Message in MT vs 'message' parameter in NSB)
///     - Publishing/sending further messages (context.Publish, context.Send in MT)
///     - Cancellation token (context.CancellationToken)
///     - Message headers/metadata (context.Headers, context.MessageId, etc.)
///
/// DEPENDENCY INJECTION:
///   MT uses the standard .NET DI container. Dependencies are injected via constructor,
///   exactly like NSB's constructor injection (since NSB 8.x). No special registration needed
///   beyond registering the consumer itself (which AddMassTransit does automatically when
///   you call AddConsumer<T>()).
///
/// ENDPOINT NAMING:
///   When you call cfg.ConfigureEndpoints(context) in Program.cs, MT automatically creates
///   a receive endpoint (queue) for each registered consumer. The queue name is derived from
///   the consumer class name with these transformations:
///     "CustomerValidatedConsumer" → removes "Consumer" suffix → "CustomerValidated"
///                                 → converts to kebab-case → "customer-validated"
///   In the SQL transport, this becomes a queue named "customer-validated" in the
///   transport.Queue table.
///
///   NSB EQUIVALENT:
///     In NSB, you configure endpoints explicitly:
///       endpointConfiguration.DefaultReceiverQueueAddress = "registration-service";
///     All handlers in the same endpoint share the same queue. In MT, by default each
///     consumer type gets its own endpoint unless you group them manually.
///     To group consumers under one endpoint in MT, use:
///       cfg.ReceiveEndpoint("registration-service", e => {
///           e.ConfigureConsumer<CustomerValidatedConsumer>(context);
///       });
///
/// IDEMPOTENCY AND THE INBOX:
///   MT's InboxState table (added to RegistrationDbContext) tracks MessageIds of
///   processed messages. If the same message is delivered again (due to retry/redelivery),
///   MT will detect it via InboxState and skip processing, preventing duplicate side effects.
///   This is the MT equivalent of NSB's Outbox deduplication.
/// ------------------------------------------------------------------
/// </summary>
public class CustomerValidatedConsumer : IConsumer<ICustomerValidated>
{
    private readonly RegistrationDbContext _dbContext;
    private readonly ILogger<CustomerValidatedConsumer> _logger;

    public CustomerValidatedConsumer(RegistrationDbContext dbContext, ILogger<CustomerValidatedConsumer> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ICustomerValidated> context)
    {
        var message = context.Message;

        _logger.LogInformation(
            "Received CustomerValidated event for CustomerId: {CustomerId}. Approved: {IsApproved}, Reason: {Reason}",
            message.CustomerId, message.IsApproved, message.Reason);

        var customer = await _dbContext.Customers
            .FirstOrDefaultAsync(c => c.Id == message.CustomerId, context.CancellationToken);

        if (customer is null)
        {
            // IMPORTANT: In MT, throwing an exception causes the message to be
            // retried (based on the retry policy configured in Program.cs).
            // After all retries are exhausted, the message is moved to the
            // "_error" queue (equivalent to NSB's error queue).
            //
            // NSB COMPARISON:
            //   NSB moves failed messages to the configured error queue after
            //   exhausting immediate and delayed retries.
            //   MT uses a similar two-phase approach:
            //     1. Immediate retries (UseMessageRetry)
            //     2. After immediate retries: delayed retries or error queue
            _logger.LogWarning("Customer {CustomerId} not found. Message will be faulted.", message.CustomerId);
            throw new InvalidOperationException($"Customer {message.CustomerId} not found.");
        }

        customer.ApplyValidationResult(message.IsApproved, message.Reason, message.ValidatedAt);

        await _dbContext.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation(
            "Customer {CustomerId} status updated to {Status}.",
            message.CustomerId, customer.Status);
    }
}