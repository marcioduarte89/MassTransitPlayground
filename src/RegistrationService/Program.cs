using MassTransit;
using MassTransitPlayground.RegistrationService.Consumers;
using MassTransitPlayground.RegistrationService.Data;
using Microsoft.EntityFrameworkCore;

// ============================================================
// REGISTRATION SERVICE — Program.cs
// ============================================================
// This file configures MassTransit with the SQL transport and
// EF Core outbox for the Registration Service.
//
// DATABASE ARCHITECTURE:
//   MassTransitPlayground_TransportDb (localdb) — Shared transport database.
//     All services point their MT SQL transport here. Contains queue/topic tables.
//   MassTransitPlayground_RegistrationDb (localdb) — Business database (this service only).
//     Contains Customers table + MT outbox tables (InboxState, OutboxMessage, OutboxState).
//
// MESSAGE FLOW:
//   1. POST /api/customers → Controller creates Customer + sends IPerformKyc via EF outbox
//   2. OutboxDeliveryService moves IPerformKyc: RegistrationDb outbox → TransportDb queue
//   3. Risk Service polls TransportDb, processes IPerformKyc, publishes ICustomerValidated
//   4. CustomerValidatedConsumer receives ICustomerValidated → updates Customer in RegistrationDb
// ============================================================

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ------------------------------------------------------------------
// EF CORE — Business Database
// ------------------------------------------------------------------
builder.Services.AddDbContext<RegistrationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("RegistrationDb")));

// ------------------------------------------------------------------
// SQL TRANSPORT OPTIONS — How MT connects to the transport database
// ------------------------------------------------------------------
// MassTransit's SQL transport uses the SqlTransportOptions class (standard
// .NET IOptions<T> pattern) to configure the database connection.
//
// WHY USE SqlTransportOptions INSTEAD OF SETTING IN UsingSqlServer()?
//   MT's SQL transport was designed to separate the WHAT (transport topology)
//   from the WHERE (connection details). The SqlTransportOptions carries the
//   connection info, while UsingSqlServer() carries the topology config.
//
// CONNECTION STRING APPROACH:
//   The simplest approach: set options.ConnectionString directly.
//   MT parses the connection string to extract host, database, credentials, etc.
//
// ALTERNATIVE (individual properties):
//   options.Host = "localhost";
//   options.Database = "MassTransitPlayground_TransportDb";
//   options.Schema = "transport";  // schema where MT creates its tables
//   options.Role = "transport";    // DB role for MT objects
//   options.Username = "masstransit";
//   options.Password = "...";
//   Use this when you need separate admin credentials for schema creation
//   vs. runtime credentials for message reading/writing.
//
// NSB EQUIVALENT:
//   NSB SQL transport: transportConfig.ConnectionString("...");
//   The concept is identical; only the API shape differs.
// ------------------------------------------------------------------
builder.Services.AddOptions<SqlTransportOptions>()
    .Configure(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("MassTransitTransport")!;
    });

// ------------------------------------------------------------------
// SQL TRANSPORT MIGRATION HOSTED SERVICE
// ------------------------------------------------------------------
// This adds an IHostedService that runs BEFORE the bus starts.
// It creates/updates the transport database schema:
//   - transport.Queue table (registered queues and their metadata)
//   - transport.QueueMessage table (actual message storage)
//   - transport.Topic table (event topics)
//   - transport.TopicSubscription table (pub/sub wiring)
//   - Stored procedures and functions for message polling/locking
//
// IMPORTANT: AddSqlServerMigrationHostedService() must be called BEFORE
// AddMassTransit() so the schema exists before the bus tries to start.
//
// In production environments, you may want to manage this schema via a
// dedicated migration step in your CI/CD pipeline rather than running it
// at every application startup. Disable it with:
//   services.AddSqlServerMigrationHostedService(x => x.CreateInfrastructure = false);
//
// NSB EQUIVALENT:
//   NSB uses "installers" to create transport infrastructure:
//   endpointConfig.EnableInstallers(); / await Endpoint.Start(config);
//   Both approaches create the necessary schema on startup automatically.
// ------------------------------------------------------------------
builder.Services.AddSqlServerMigrationHostedService();

// ------------------------------------------------------------------
// MASSTRANSIT CONFIGURATION
// ------------------------------------------------------------------
// AddMassTransit is the single registration point for all MT services.
// It registers:
//   - IBus (the main bus interface — implements IPublishEndpoint and ISendEndpointProvider)
//   - IPublishEndpoint (scoped, outbox-aware — for publishing events)
//   - ISendEndpointProvider (scoped, outbox-aware — for sending commands)
//   - Hosted services: IBusControl (bus lifecycle) and OutboxDeliveryService
//
// NSB EQUIVALENT:
//   NSB: var endpointConfiguration = new EndpointConfiguration("registration-service");
//        var endpoint = await Endpoint.Start(endpointConfiguration);
//        services.AddSingleton<IMessageSession>(endpoint);
//   MT wraps all of this into a DI-first fluent API. The bus starts and stops
//   automatically with the .NET Generic Host lifecycle (IHostedService).
// ------------------------------------------------------------------
builder.Services.AddMassTransit(mt =>
{
    mt.SetKebabCaseEndpointNameFormatter();

    // ------------------------------------------------------------------
    // CONSUMER REGISTRATION
    // ------------------------------------------------------------------
    // AddConsumer<T> registers the consumer with MT's DI container.
    // MT will:
    //   1. Resolve the consumer from DI per message received (scoped lifetime)
    //   2. Determine which message type(s) the consumer handles
    //   3. Generate a queue name from the consumer class name (kebab-case)
    //   4. Subscribe to the appropriate topic (for events like ICustomerValidated)
    //
    // The inline configuration lambda allows us to configure middleware
    // (retry, circuit breaker, etc.) specific to this consumer without
    // needing a separate ConsumerDefinition<T> class.
    //
    // CONSUMER LIFETIME:
    //   MT creates a new consumer instance PER MESSAGE (scoped by default).
    //   This is why you can safely inject scoped services (like DbContext) via
    //   constructor injection. MT creates a new DI scope for each message.
    //   → Same behaviour as NSB's handler instantiation model.
    //
    // NSB EQUIVALENT:
    //   NSB auto-discovers IHandleMessages<T> implementations in the assembly.
    //   MT requires explicit registration. You can use assembly scanning:
    //     mt.AddConsumers(typeof(Program).Assembly);
    //   but explicit registration is preferred for clarity and control.
    // ------------------------------------------------------------------
    mt.AddConsumer<CustomerValidatedConsumer>(cfg =>
    {
        // ------------------------------------------------------------------
        // RETRY POLICY
        // ------------------------------------------------------------------
        // UseMessageRetry configures IMMEDIATE retries — the message is retried
        // in-process without being re-queued to the transport. Fast, but occupies
        // the consumer thread for the retry duration.
        //
        // The retry pipeline is middleware applied to the consumer's receive
        // pipeline. When the consumer throws, the middleware catches it,
        // waits for the interval, then calls Consume() again.
        //
        // After all retries fail:
        //   MT moves the message to a "_error" sub-queue:
        //   "customer-validated" → "customer-validated_error"
        //   MT also publishes a Fault<ICustomerValidated> event that other
        //   consumers can subscribe to for alerting/monitoring purposes.
        //
        // DELAYED REDELIVERY (second-level retries):
        //   For longer retry intervals (minutes/hours), use UseDelayedRedelivery:
        //     cfg.UseDelayedRedelivery(r => r.Intervals(
        //         TimeSpan.FromMinutes(5),
        //         TimeSpan.FromMinutes(15),
        //         TimeSpan.FromMinutes(30)));
        //   This re-queues the message with a delivery time in the future,
        //   freeing the consumer thread. The SQL transport supports this natively.
        //
        // NSB COMPARISON:
        //   NSB: config.Recoverability()
        //              .Immediate(i => i.NumberOfRetries(3))
        //              .Delayed(d => d.NumberOfRetries(3).TimeIncrease(TimeSpan.FromSeconds(5)));
        //   MT: cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
        //       cfg.UseDelayedRedelivery(r => r.Interval(3, TimeSpan.FromMinutes(5)));
        //   Both NSB's error queue and MT's "_error" queue serve the same purpose.
        // ------------------------------------------------------------------
        cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
    });

    // ------------------------------------------------------------------
    // EF CORE OUTBOX
    // ------------------------------------------------------------------
    // Configures transactional messaging using the RegistrationDbContext.
    // Messages sent/published via IPublishEndpoint or ISendEndpointProvider
    // (injected into controllers/services) are written to the OutboxMessage
    // table as part of the EF change tracker. They are committed atomically
    // when SaveChangesAsync() is called.
    //
    // OutboxDeliveryService (a background IHostedService) periodically reads
    // pending OutboxMessage rows and forwards them to the SQL transport DB.
    //
    // CONSUMER OUTBOX (implicit):
    //   When a consumer publishes/sends within Consume(), MT automatically
    //   stores those outgoing messages in the OutboxMessage table and only
    //   delivers them if the consumer completes successfully (no exception).
    //   If the consumer throws, the outgoing messages are discarded.
    //   This is the "consumer outbox" behaviour and works without UseBusOutbox().
    //
    // BUS OUTBOX (explicit via UseBusOutbox()):
    //   Extends the outbox behaviour to code OUTSIDE consumers — controllers,
    //   background jobs, etc. Messages are held until SaveChangesAsync() commits.
    //
    // IDEMPOTENCY (InboxState):
    //   The InboxState table records the MessageId of every successfully
    //   processed message. If the same message arrives again (e.g., retry after
    //   a crash at the wrong moment), MT detects it and skips processing.
    //   This provides at-least-once delivery with idempotent consumption.
    //
    // NSB COMPARISON:
    //   NSB Outbox: endpointConfig.EnableOutbox(); (requires persistence configured)
    //   Concept is identical. MT's implementation is tightly coupled to EF Core
    //   while NSB supports multiple persistence backends (NHibernate, EF, SQL, etc.).
    //
    // QueryDelay: How often the OutboxDeliveryService polls for pending messages.
    //   Lower value = more responsive but more DB load. Default is 1 second.
    // ------------------------------------------------------------------
    mt.AddEntityFrameworkOutbox<RegistrationDbContext>(outbox =>
    {
        outbox.UseSqlServer();
        outbox.UseBusOutbox(busOutbox =>
        {
            busOutbox.MessageDeliveryLimit = 100;
        });
        outbox.QueryDelay = TimeSpan.FromSeconds(1);
    });

    // ------------------------------------------------------------------
    // SQL SERVER TRANSPORT
    // ------------------------------------------------------------------
    // UsingSqlServer() configures the SQL transport topology.
    // The connection details come from SqlTransportOptions (registered above).
    //
    // MT TRANSPORT COMPARISON:
    //   All MT transports share the same API shape:
    //     UsingRabbitMq((context, cfg) => { cfg.Host("localhost"); ... })
    //     UsingAzureServiceBus((context, cfg) => { cfg.Host("sb://..."); ... })
    //     UsingSqlServer((context, cfg) => { ... })
    //   This makes it easy to swap transports by changing one line, while
    //   keeping all consumer/saga code unchanged.
    //
    // NSB EQUIVALENT:
    //   NSB: var transport = endpointConfig.UseTransport<SqlServerTransport>();
    //   In NSB the transport is tightly coupled to the endpoint configuration.
    //   In MT, the transport is configured separately, making multi-transport
    //   setups (e.g., SQL + RabbitMQ) easier to reason about.
    // ------------------------------------------------------------------
    mt.UsingSqlServer((context, cfg) =>
    {
        // ------------------------------------------------------------------
        // ConfigureEndpoints — Convention-Based Wiring
        // ------------------------------------------------------------------
        // Automatically creates receive endpoints (queues) for all registered
        // consumers and wires them up to their respective consumers.
        //
        // For each registered consumer, MT:
        //   1. Derives the endpoint name using the configured name formatter
        //      KebabCaseEndpointNameFormatter (registered via SetKebabCaseEndpointNameFormatter above)
        //      "CustomerValidatedConsumer" → removes "Consumer" → "CustomerValidated"
        //                                 → kebab-case → "customer-validated"
        //   2. Creates/verifies the queue in the transport DB
        //   3. For event consumers (subscribing to published messages):
        //      Creates a topic for the message type and a subscription linking
        //      the topic to this service's queue
        //   4. Applies retry policies, concurrency limits, etc.
        //
        // GROUPING CONSUMERS UNDER ONE ENDPOINT:
        //   By default, each consumer gets its own endpoint (queue). To group
        //   multiple consumers under one queue (like NSB's single input queue):
        //     cfg.ReceiveEndpoint("registration-service", e =>
        //     {
        //         e.ConfigureConsumer<CustomerValidatedConsumer>(context);
        //         e.ConfigureConsumer<AnotherConsumer>(context);
        //     });
        //   This is useful when you want to maintain NSB-style single-endpoint topology.
        //
        // NSB COMPARISON:
        //   In NSB, all handlers in an endpoint share the same input queue.
        //   The queue name is the endpoint name you configure upfront.
        //   MT's default is one queue per consumer type — more granular, but
        //   both approaches are valid depending on your needs.
        // ------------------------------------------------------------------
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

// ------------------------------------------------------------------
// EF CORE DATABASE MIGRATION ON STARTUP
// ------------------------------------------------------------------
// Auto-applies pending EF migrations on startup (development convenience).
// Creates the RegistrationDb with:
//   - dbo.Customers table
//   - MassTransit outbox tables: InboxState, OutboxMessage, OutboxState
//
// Production recommendation: run migrations in CI/CD, not at startup.
// ------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RegistrationDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Run();
