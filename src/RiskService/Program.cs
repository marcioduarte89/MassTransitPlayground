using MassTransit;
using MassTransitPlayground.RiskService.Consumers;
using MassTransitPlayground.RiskService.Data;
using Microsoft.EntityFrameworkCore;

// ============================================================
// RISK SERVICE — Program.cs
// ============================================================
// This file mirrors the RegistrationService configuration with
// two key differences:
//   1. Uses RiskDbContext instead of RegistrationDbContext
//   2. Registers PerformKycConsumer (command handler) instead of
//      CustomerValidatedConsumer (event handler)
//
// DATABASE ARCHITECTURE:
//   MassTransitPlayground_TransportDb (localdb) — SAME shared transport DB as RegistrationService.
//   MassTransitPlayground_RiskDb (localdb) — Business database for this service.
//     Contains KycRecords table + MT outbox tables.
//
// KEY INSIGHT: Services Do NOT Share Business Databases
//   RegistrationDb  ←→  [MassTransitTransport]  ←→  RiskDb
//   The transport DB is the ONLY shared resource. Each service owns its data.
//   This is the "database-per-service" pattern common in microservices architecture.
//   MassTransit (the message bus) is what makes cross-service communication possible
//   without direct database coupling.
// ============================================================

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<RiskDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("RiskDb")));

// ------------------------------------------------------------------
// SQL TRANSPORT OPTIONS
// ------------------------------------------------------------------
// Both services point to the SAME transport database (MassTransitPlayground_TransportDb).
// This is how they communicate: RegistrationService writes to the transport,
// RiskService reads from it, and vice versa.
//
// The transport database is the message bus — analogous to:
//   - A RabbitMQ broker (RabbitMQ transport)
//   - An Azure Service Bus namespace (ASB transport)
//   - An Amazon SQS/SNS account (SQS transport)
// In the SQL transport, the "broker" is just a set of tables in SQL Server.
// ------------------------------------------------------------------
builder.Services.AddOptions<SqlTransportOptions>()
    .Configure(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("MassTransitTransport")!;
    });

// ------------------------------------------------------------------
// SQL TRANSPORT MIGRATION HOSTED SERVICE
// ------------------------------------------------------------------
// Both services add this migration service. MT is idempotent — if the
// schema already exists (created by RegistrationService on its first startup),
// this service will detect it and skip creation.
//
// This is safe to run on every startup in both services.
// ------------------------------------------------------------------
builder.Services.AddSqlServerMigrationHostedService();

builder.Services.AddMassTransit(mt =>
{
    mt.SetKebabCaseEndpointNameFormatter();

    // ------------------------------------------------------------------
    // COMMAND CONSUMER REGISTRATION
    // ------------------------------------------------------------------
    // PerformKycConsumer handles IPerformKyc commands.
    // MT will:
    //   1. Create a queue named "perform-kyc" in the transport DB
    //   2. Poll that queue for messages (SQL transport polling)
    //   3. For each IPerformKyc message, resolve PerformKycConsumer from DI
    //      and call Consume()
    //
    // COMMAND ROUTING — HOW THE SENDER KNOWS THE QUEUE NAME:
    //   The Registration Service sends to "queue:perform-kyc".
    //   The Risk Service creates the queue "perform-kyc" (from PerformKycConsumer name).
    //   The two sides are loosely coupled by naming convention.
    //
    //   A more robust approach is to publish the queue name as part of a
    //   shared service discovery mechanism. For this playground, the convention
    //   is sufficient.
    //
    // COMMAND vs EVENT CONSUMER — TOPOLOGY DIFFERENCE:
    //   For COMMAND consumers (like this one):
    //     MT creates a QUEUE. Senders directly target this queue by URI.
    //     No topic/subscription is created for the IPerformKyc contract.
    //   For EVENT consumers (like CustomerValidatedConsumer):
    //     MT creates a TOPIC for the event type AND a subscription linking
    //     the topic to the consumer's queue. Publishers publish to the topic;
    //     MT routes to all subscribed queues.
    //
    //   This is why commands use Send (targeted) and events use Publish (broadcast).
    // ------------------------------------------------------------------
    mt.AddConsumer<PerformKycConsumer>(cfg =>
    {
        cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
    });

    // ------------------------------------------------------------------
    // EF CORE OUTBOX FOR RISK SERVICE
    // ------------------------------------------------------------------
    // The outbox here ensures that publishing ICustomerValidated is atomic
    // with saving the KycRecord. See PerformKycConsumer for detailed flow.
    //
    // We use UseBusOutbox() here as well (consistent with RegistrationService),
    // even though this service doesn't have controllers that publish messages.
    // It's a good practice to have it configured in case we add such code later.
    //
    // The consumer outbox (for Consume()-originated publishes) is ALWAYS active
    // when AddEntityFrameworkOutbox is configured — UseBusOutbox() only adds
    // the outbox behaviour for code outside consumers.
    // ------------------------------------------------------------------
    mt.AddEntityFrameworkOutbox<RiskDbContext>(outbox =>
    {
        outbox.UseSqlServer();
        outbox.UseBusOutbox(busOutbox =>
        {
            busOutbox.MessageDeliveryLimit = 100;
        });
        outbox.QueryDelay = TimeSpan.FromSeconds(1);
    });

    mt.UsingSqlServer((context, cfg) =>
    {
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RiskDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Run();
