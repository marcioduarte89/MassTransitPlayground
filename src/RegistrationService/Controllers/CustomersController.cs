using MassTransit;
using MassTransitPlayground.Contracts.Commands;
using MassTransitPlayground.RegistrationService.Data;
using MassTransitPlayground.RegistrationService.Domain;
using MassTransitPlayground.RegistrationService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MassTransitPlayground.RegistrationService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CustomersController : ControllerBase
{
    private readonly RegistrationDbContext _dbContext;

    // ------------------------------------------------------------------
    // IPublishEndpoint vs ISendEndpointProvider vs IBus
    // ------------------------------------------------------------------
    // MassTransit provides three main interfaces for sending/publishing messages:
    //
    //   IPublishEndpoint  — For publishing EVENTS (one-to-many, fan-out via topics).
    //                       Inject this when you only publish events.
    //
    //   ISendEndpointProvider — For sending COMMANDS (one-to-one, directly to a queue).
    //                           Inject this when you only send commands.
    //
    //   IBus              — Implements both of the above. Use when you need both.
    //                       However, prefer the more specific interfaces for testability.
    //
    // OUTBOX INTEGRATION:
    //   When you configure AddEntityFrameworkOutbox<TDbContext>(o => o.UseBusOutbox()),
    //   the DI-injected IPublishEndpoint and ISendEndpointProvider become OUTBOX-AWARE.
    //   Calling Publish() or GetSendEndpoint().Send() does NOT immediately send to the
    //   transport. Instead, the message is stored in the OutboxMessage EF entity
    //   (in-memory, as part of the current DbContext change tracker).
    //   The message is only committed to the outbox table when SaveChangesAsync() succeeds.
    //   The OutboxDeliveryService background service then reads from the outbox table
    //   and forwards messages to the SQL transport.
    //
    // NSB EQUIVALENT:
    //   In NServiceBus 8+, you inject IMessageSession to send/publish from outside
    //   message handlers. The NSB outbox works similarly — messages enqueued via
    //   IMessageSession within a transaction are stored in the outbox table atomically.
    //
    // WHY NOT IBus IN CONTROLLERS?
    //   IBus represents the raw bus — it bypasses the outbox in some configurations.
    //   Always prefer IPublishEndpoint/ISendEndpointProvider in controllers and services
    //   to ensure messages flow through the outbox correctly.
    // ------------------------------------------------------------------
    private readonly ISendEndpointProvider _sendEndpointProvider;
    private readonly ILogger<CustomersController> _logger;

    public CustomersController(
        RegistrationDbContext dbContext,
        ISendEndpointProvider sendEndpointProvider,
        ILogger<CustomersController> logger)
    {
        _dbContext = dbContext;
        _sendEndpointProvider = sendEndpointProvider;
        _logger = logger;
    }

    /// <summary>
    /// Registers a new customer and initiates the KYC process.
    ///
    /// TRANSACTIONAL FLOW:
    ///   1. Create Customer entity (Status = Pending)
    ///   2. Obtain the send endpoint for the Risk Service queue
    ///   3. Send the IPerformKyc command — this writes to the EF outbox (in-memory)
    ///   4. SaveChangesAsync() commits BOTH the customer row AND the outbox message atomically
    ///   5. The OutboxDeliveryService background worker picks up the outbox message and
    ///      delivers it to the SQL transport (the MassTransitTransport database)
    ///   6. The Risk Service polls the transport and processes the command
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CustomerResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterCustomerRequest request, CancellationToken cancellationToken)
    {
        var existingCustomer = await _dbContext.Customers
            .FirstOrDefaultAsync(c => c.Email == request.Email, cancellationToken);

        if (existingCustomer is not null)
            return Conflict(new { message = $"A customer with email '{request.Email}' already exists." });

        var customer = Customer.Create(request.FirstName, request.LastName, request.Email, request.DateOfBirth);
        _dbContext.Customers.Add(customer);

        // ------------------------------------------------------------------
        // SENDING A COMMAND TO THE RISK SERVICE
        // ------------------------------------------------------------------
        // We use ISendEndpointProvider to get the endpoint for the Risk Service queue.
        //
        // The URI "queue:perform-kyc" references the queue that MassTransit automatically
        // creates for the PerformKycConsumer in the Risk Service. The queue name follows
        // MT's convention: consumer class name without "Consumer" suffix, in kebab-case.
        //   "PerformKycConsumer" → "perform-kyc"
        //
        // ENDPOINT CONVENTION (ALTERNATIVE APPROACH):
        //   You can also pre-configure the destination at startup using EndpointConvention:
        //     EndpointConvention.Map<IPerformKyc>(new Uri("queue:perform-kyc"));
        //   Then simply call: await _sendEndpointProvider.Send<IPerformKyc>(new { ... });
        //   This is cleaner but requires a startup configuration step.
        //
        // NSB EQUIVALENT:
        //   In NSB, you configure routing in the transport config:
        //     routing.RouteToEndpoint(typeof(PerformKyc), "risk-service");
        //   Then simply: await session.Send(new PerformKyc { ... });
        //
        // SQL TRANSPORT NOTE:
        //   "queue:perform-kyc" is a URI scheme specific to MT's SQL transport.
        //   Other transports use different schemes: "rabbitmq://host/queue" for RabbitMQ,
        //   "sb://namespace.servicebus.windows.net/queue" for Azure Service Bus.
        //   MT abstracts this so your business code is transport-agnostic.
        // ------------------------------------------------------------------
        var endpoint = await _sendEndpointProvider.GetSendEndpoint(new Uri("queue:perform-kyc"));

        await endpoint.Send<IPerformKyc>(new
        {
            customer.Id,
            CustomerId = customer.Id,
            customer.FirstName,
            customer.LastName,
            customer.Email,
            customer.DateOfBirth,
            RequestedAt = DateTime.UtcNow
        }, cancellationToken);

        // This single SaveChangesAsync() atomically commits:
        //   - The new Customer row in the Customers table
        //   - The IPerformKyc command in the OutboxMessage table
        // If this call fails, NEITHER is persisted. No orphaned messages, no lost data.
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Customer {CustomerId} registered. KYC command enqueued via outbox.", customer.Id);

        return CreatedAtAction(nameof(GetById), new { id = customer.Id }, CustomerResponse.FromDomain(customer));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CustomerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (customer is null)
            return NotFound();

        return Ok(CustomerResponse.FromDomain(customer));
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<CustomerResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var customers = await _dbContext.Customers
            .AsNoTracking()
            .Select(c => CustomerResponse.FromDomain(c))
            .ToListAsync(cancellationToken);

        return Ok(customers);
    }
}
