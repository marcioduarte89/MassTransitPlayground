# MassTransit Playground

A hands-on learning project for exploring **MassTransit** with the **SQL Server transport**, written by someone with an NServiceBus (NSB) background who wanted to understand how MassTransit works at a deep level — including its transport topology, outbox mechanics, and pub/sub routing.

The project intentionally mirrors patterns from NServiceBus so that the two frameworks can be compared side by side. Every significant design decision includes an NSB comparison in the source code comments.

---

## Table of Contents

1. [Solution Overview](#1-solution-overview)
2. [Architecture](#2-architecture)
3. [Message Contracts](#3-message-contracts)
4. [End-to-End Message Flow](#4-end-to-end-message-flow)
5. [MassTransit SQL Transport — Database Schema](#5-masstransit-sql-transport--database-schema)
6. [Send vs Publish — What Gets Written Where](#6-send-vs-publish--what-gets-written-where)
7. [Queue Naming — The Critical Lesson](#7-queue-naming--the-critical-lesson)
8. [The EF Core Outbox](#8-the-ef-core-outbox)
9. [MassTransit Topology Explained](#9-masstransit-topology-explained)
10. [NSB vs MassTransit Comparison](#10-nsb-vs-masstransit-comparison)
11. [Running the Project](#11-running-the-project)
12. [Lessons Learned and Gotchas](#12-lessons-learned-and-gotchas)

---

## 1. Solution Overview

The solution contains three projects:

| Project | Role |
|---|---|
| `MassTransitPlayground.Contracts` | Shared message contracts (interfaces only) |
| `MassTransitPlayground.RegistrationService` | HTTP API that registers customers and handles KYC results |
| `MassTransitPlayground.RiskService` | Background service that performs KYC validation |

### Databases

| Database | Owner | Purpose |
|---|---|---|
| `MassTransitPlayground_RegistrationDb` | RegistrationService | Customers table + MT outbox tables |
| `MassTransitPlayground_RiskDb` | RiskService | KycRecords table + MT outbox tables |
| `MassTransitPlayground_TransportDb` | **Shared** (both services) | MassTransit SQL transport — queues, topics, messages |

The transport database is the message broker. In this project it replaces RabbitMQ or Azure Service Bus with SQL Server tables. It is the **only shared resource** between the two services. Each service owns its own business database exclusively.

---

## 2. Architecture

```
┌───────────────────────────────────┐         ┌───────────────────────────────────┐
│         RegistrationService       │         │            RiskService            │
│                                   │         │                                   │
│  POST /api/customers              │         │  PerformKycConsumer               │
│    → creates Customer (Pending)   │         │    → performs KYC check           │
│    → sends IPerformKyc (outbox)   │         │    → saves KycRecord              │
│                                   │         │    → publishes ICustomerValidated │
│  CustomerValidatedConsumer        │         │                                   │
│    → updates Customer status      │         │                                   │
│      (Validated / Rejected)       │         │                                   │
│                                   │         │                                   │
│  DB: RegistrationDb               │         │  DB: RiskDb                       │
│    - Customers                    │         │    - KycRecords                   │
│    - OutboxMessage                │         │    - OutboxMessage                │
│    - OutboxState                  │         │    - OutboxState                  │
│    - InboxState                   │         │    - InboxState                   │
└──────────────┬────────────────────┘         └──────────────┬────────────────────┘
               │                                             │
               └──────────────────┬──────────────────────────┘
                                  │
                    ┌─────────────▼─────────────┐
                    │       TransportDb          │
                    │                           │
                    │  transport.Queue          │
                    │  transport.Topic          │
                    │  transport.Message        │
                    │  transport.MessageDelivery│
                    │  transport.QueueSubscription
                    │  transport.TopicSubscription
                    └───────────────────────────┘
```

### Service Identities on the Bus

Each service is identified on the bus by the name of its **receive endpoint** (the queue where its consumer listens):

| Service | Bus Identity (receive endpoint) |
|---|---|
| RegistrationService | `customer-validated` |
| RiskService | `perform-kyc` |

This becomes important when reading `transport.Message` — the `SourceAddress` of every message is the sending service's receive endpoint address.

---

## 3. Message Contracts

All contracts live in the shared `Contracts` project and are defined as **interfaces** (not classes). This is a key MassTransit convention:

```csharp
// Commands/IPerformKyc.cs
public interface IPerformKyc
{
    Guid CustomerId { get; }
    string FirstName { get; }
    string LastName { get; }
    string Email { get; }
    DateOnly DateOfBirth { get; }
    DateTime RequestedAt { get; }
}

// Events/ICustomerValidated.cs
public interface ICustomerValidated
{
    Guid CustomerId { get; }
    bool IsApproved { get; }
    string Reason { get; }
    DateTime ValidatedAt { get; }
}
```

**Why interfaces?**
MassTransit generates proxy classes at runtime (via Castle DynamicProxy). When you call `Send<IPerformKyc>(new { CustomerId = ..., ... })`, MT matches the anonymous object's properties to the interface by name and creates a concrete implementation. This keeps contracts free of implementation details and makes them naturally immutable.

**NSB comparison:** NSB typically uses concrete classes with `ICommand` / `IEvent` marker interfaces. MT uses plain interfaces and distinguishes commands from events purely by convention (Send vs Publish).

### Command vs Event Convention

| | `IPerformKyc` (Command) | `ICustomerValidated` (Event) |
|---|---|---|
| **Naming** | Imperative ("do this") | Past tense ("this happened") |
| **Sent with** | `ISendEndpointProvider.Send()` | `context.Publish()` |
| **Routing** | Directly to a named queue | To a topic, fan-out to all subscribers |
| **Receivers** | Exactly one | Zero or more |
| **NSB equivalent** | `session.Send(new PerformKyc())` | `session.Publish(new CustomerValidated())` |

---

## 4. End-to-End Message Flow

```
1. POST /api/customers
   RegistrationService controller:
     a. Creates Customer entity (Status = Pending)
     b. Calls SendEndpoint.Send<IPerformKyc>()  ← goes into EF outbox (in-memory)
     c. Calls SaveChangesAsync()               ← atomically writes Customer + OutboxMessage to RegistrationDb
   
2. OutboxDeliveryService (background, RegistrationService)
     Polls OutboxMessage in RegistrationDb every 1 second
     Finds undelivered IPerformKyc message
     Writes to TransportDb:
       - transport.Message  (payload)
       - transport.MessageDelivery  (delivery target: perform-kyc queue)
     Marks OutboxMessage as delivered

3. RiskService consumer
     Polls transport.MessageDelivery for its queue ID (perform-kyc)
     Finds the IPerformKyc message
     Resolves PerformKycConsumer from DI
     Calls Consume():
       a. Performs KYC logic
       b. Creates KycRecord entity
       c. Calls context.Publish<ICustomerValidated>()  ← goes into EF outbox (in-memory)
       d. Calls SaveChangesAsync()  ← atomically writes KycRecord + OutboxMessage to RiskDb
       e. Acknowledges IPerformKyc (removes from MessageDelivery)

4. OutboxDeliveryService (background, RiskService)
     Polls OutboxMessage in RiskDb
     Finds undelivered ICustomerValidated event
     Writes to TransportDb:
       - transport.Message  (payload, destination = topic URI)
       - Reads transport.QueueSubscription to find subscribers
       - Creates transport.MessageDelivery for each subscriber (customer-validated queue)

5. RegistrationService consumer
     Polls transport.MessageDelivery for its queue ID (customer-validated)
     Finds the ICustomerValidated message
     Resolves CustomerValidatedConsumer from DI
     Calls Consume():
       a. Looks up Customer by CustomerId
       b. Updates Status to Validated or Rejected
       c. Calls SaveChangesAsync()
       d. Acknowledges ICustomerValidated
```

---

## 5. MassTransit SQL Transport — Database Schema

The transport database (`MassTransitPlayground_TransportDb`) is created and managed by MassTransit via `AddSqlServerMigrationHostedService()`. Both services run this migration on startup; it is idempotent.

All tables live in the `transport` schema.

### transport.Queue

Stores all queue definitions. Every queue has **three rows** — one per type:

```sql
CREATE TABLE [transport].[Queue] (
    [Id]               BIGINT         NOT NULL,
    [Updated]          DATETIME2(7)   NOT NULL,
    [Name]             NVARCHAR(256)  NOT NULL,
    [Type]             TINYINT        NOT NULL,   -- 1=main, 2=error, 3=skipped/dead-letter
    [AutoDelete]       INT            NULL,        -- seconds until auto-delete (NULL = permanent)
    [MaxDeliveryCount] INT            NOT NULL
);
```

| Type value | Meaning | NSB equivalent |
|---|---|---|
| 1 | Main receive queue | Input queue |
| 2 | Error queue | Error queue (configured separately in NSB) |
| 3 | Skipped / dead-letter queue | Poison message queue |

**What you will see after both services start:**

| Name | Type | Created by |
|---|---|---|
| `perform-kyc` | 1, 2, 3 | RiskService (`PerformKycConsumer`) |
| `customer-validated` | 1, 2, 3 | RegistrationService (`CustomerValidatedConsumer`) |
| `PerformKyc` | 1, 2, 3 | SQL transport topic relay queue for `IPerformKyc` |
| `CustomerValidated` | 1, 2, 3 | SQL transport topic relay queue for `ICustomerValidated` |

The `PerformKyc` and `CustomerValidated` PascalCase entries are internal to the SQL transport's topic routing mechanism. They act as relay queues through which published messages pass before being fanned out to subscriber queues via `QueueSubscription`.

### transport.Topic

```sql
CREATE TABLE [transport].[Topic] (
    [Id]      BIGINT         NOT NULL,
    [Updated] DATETIME2(7)   NOT NULL,
    [Name]    NVARCHAR(256)  NOT NULL   -- full namespace:TypeName
);
```

MassTransit creates a topic for **every message type** that any consumer is registered for, regardless of whether the message is a command (Send) or an event (Publish). Topics are named using the full CLR type name:

```
MassTransitPlayground.Contracts.Commands:IPerformKyc
MassTransitPlayground.Contracts.Events:ICustomerValidated
```

### transport.QueueSubscription

Links topics to their subscriber queues. This is what enables fan-out: when a message is published to a topic, the transport reads this table to know which `MessageDelivery` rows to create.

```sql
CREATE TABLE [transport].[QueueSubscription] (
    [Id]            BIGINT          NOT NULL,
    [Updated]       DATETIME2(7)    NOT NULL,
    [SourceId]      BIGINT          NOT NULL,   -- FK to Topic.Id
    [DestinationId] BIGINT          NOT NULL,   -- FK to Queue.Id (the relay queue)
    [SubType]       TINYINT         NOT NULL,
    [RoutingKey]    NVARCHAR(256)   NOT NULL,
    [Filter]        NVARCHAR(1024)  NOT NULL
);
```

After both services start, this table has two rows:

| TopicName | QueueName (Destination) | Who creates it |
|---|---|---|
| `...Commands:IPerformKyc` | `PerformKyc` (relay) | RiskService startup |
| `...Events:ICustomerValidated` | `CustomerValidated` (relay) | RegistrationService startup |

A second level of routing then links `PerformKyc` relay → `perform-kyc` consumer queue, and `CustomerValidated` relay → `customer-validated` consumer queue via `TopicSubscription`.

### transport.Message

Stores the raw message payload. One row per message, regardless of how many queues it is destined for.

```sql
[TransportMessageId]  uniqueidentifier   -- MT-assigned transport ID
[MessageId]           uniqueidentifier   -- business-level message ID
[ContentType]         nvarchar(256)
[MessageType]         nvarchar(max)      -- e.g. urn:message:...:IPerformKyc
[Body]                nvarchar(max)      -- JSON-serialised payload
[SourceAddress]       nvarchar(256)      -- sending service's receive endpoint
[DestinationAddress]  nvarchar(256)      -- queue URI or topic URI
[SentTime]            datetimeoffset
[Headers]             nvarchar(max)
[Host]                nvarchar(max)
```

### transport.MessageDelivery

The actual delivery queue. Consumers poll this table filtered by their `QueueId`. One row per (message, destination queue) pair.

```sql
[QueueId]          bigint             -- FK to transport.Queue.Id (Type=1 queue)
[TransportMessageId] uniqueidentifier -- FK to transport.Message
[DeliveryCount]    int                -- 0 = never attempted
[MaxDeliveryCount] int
[EnqueueTime]      datetimeoffset     -- when to make available (NULL = now)
[ExpirationTime]   datetimeoffset
[LockId]           uniqueidentifier   -- set while a consumer holds the lock
[LockUntil]        datetimeoffset     -- lock expiry
[Priority]         tinyint
```

This table is the heart of the transport. When a consumer calls the polling stored procedure, it:
1. Filters by `QueueId` matching its registered queue
2. Filters `EnqueueTime <= GETUTCDATE()`
3. Filters `LockId IS NULL` (or `LockUntil` is expired)
4. Sets a `LockId` and `LockUntil` to prevent other instances from picking up the same message
5. Returns the row to the consumer

---

## 6. Send vs Publish — What Gets Written Where

This is one of the most important things to understand about MassTransit's SQL transport. **Send and Publish write different destination addresses to `transport.Message`**, and that determines the routing path.

### Send (command routing — point-to-point)

```csharp
// RegistrationService / CustomersController.cs
var endpoint = await _sendEndpointProvider.GetSendEndpoint(new Uri("queue:perform-kyc"));
await endpoint.Send<IPerformKyc>(new { ... });
```

**What gets written to `transport.Message`:**

```
SourceAddress:      db://localhost:1433/customer-validated      ← RegistrationService's identity
DestinationAddress: db://localhost:1433/perform-kyc             ← direct queue URI
MessageType:        urn:message:...:IPerformKyc
```

The destination is a **raw queue address**. The transport resolves `perform-kyc` to a `Queue.Id` (type=1) and inserts directly into `transport.MessageDelivery` with that `QueueId`. No topic lookup, no fan-out. The message goes to exactly one queue.

### Publish (event routing — topic fan-out)

```csharp
// RiskService / PerformKycConsumer.cs
await context.Publish<ICustomerValidated>(new { ... });
```

**What gets written to `transport.Message`:**

```
SourceAddress:      db://localhost:1433/perform-kyc             ← RiskService's identity
DestinationAddress: db://localhost:1433/MassTransitPlayground.Contracts.Events:ICustomerValidated?type=topic
MessageType:        urn:message:...:ICustomerValidated
```

The destination is a **topic URI** (note the `?type=topic` suffix). The transport:
1. Resolves the topic by name in `transport.Topic`
2. Reads `transport.QueueSubscription` to find all subscribers
3. Creates one `transport.MessageDelivery` row per subscriber queue

If two services both subscribed to `ICustomerValidated`, two `MessageDelivery` rows would be created from this single published message — true fan-out.

### Routing comparison table

| Aspect | Send | Publish |
|---|---|---|
| `DestinationAddress` format | `db://host/queue-name` | `db://host/namespace:Type?type=topic` |
| `MessageDelivery` rows created | 1 (hardcoded queue) | 1 per subscriber (from QueueSubscription) |
| Number of receivers | Exactly 1 | 0 to N |
| Topic involved | No | Yes |
| NSB equivalent | `session.Send(msg, new SendOptions())` | `session.Publish(msg)` |

---

## 7. Queue Naming — The Critical Lesson

> **This is the most important practical lesson from building this playground.**

### The problem

MassTransit's `ConfigureEndpoints()` derives queue names from consumer class names using an `IEndpointNameFormatter`. The formatter is resolved from DI. **The default formatter in MassTransit 8.x is `DefaultEndpointNameFormatter`** — it strips the `"Consumer"` suffix but keeps **PascalCase**:

```
PerformKycConsumer  →  "PerformKyc"   (DefaultEndpointNameFormatter)
PerformKycConsumer  →  "perform-kyc"  (KebabCaseEndpointNameFormatter)
```

If you hardcode the destination queue name in your controller (`queue:perform-kyc`) but the consumer is actually registered as `PerformKyc`, **the messages silently queue up in `perform-kyc` and the consumer polls `PerformKyc`** — two completely separate queues, no error, no warning.

This manifests as:
- Messages appearing in `transport.Message` and `transport.MessageDelivery` with `DeliveryCount = 0`
- Consumer never triggered
- No exceptions anywhere

### How we diagnosed it

Running the risk service with `MassTransit: Debug` logging revealed:

```
Create queue: name: PerformKyc                                   ← PascalCase! not perform-kyc
Create topic: MassTransitPlayground.Contracts.Commands:IPerformKyc
Create queue subscription: source: ...IPerformKyc, destination: PerformKyc
```

The consumer was polling `PerformKyc`. RegistrationService was sending to `perform-kyc`. No match.

### The fix

Register the kebab-case formatter **explicitly** on every service that uses `ConfigureEndpoints()`:

```csharp
builder.Services.AddMassTransit(mt =>
{
    mt.SetKebabCaseEndpointNameFormatter();   // ← must be explicit

    mt.AddConsumer<PerformKycConsumer>();
    mt.UsingSqlServer((context, cfg) =>
    {
        cfg.ConfigureEndpoints(context);
    });
});
```

Both services must use the **same formatter** so that the queue name a sender targets matches the queue name the consumer creates. The formatter is a cross-cutting convention across your entire system.

### Startup ordering also matters

A secondary issue we encountered: if the sender's `OutboxDeliveryService` delivers a message to a queue **before** the consumer service has started and created that queue, the SQL transport auto-creates a minimal queue entry (type 1 only). When the consumer service later starts and creates its full queue entry (types 1, 2, 3), both queue entries coexist in `transport.Queue` with different IDs. The `MessageDelivery` row points to the original ID, the consumer polls the new ID → message is orphaned.

**Rule:** Always start the consuming service **before** (or simultaneously with) the producing service, especially after dropping and recreating databases.

---

## 8. The EF Core Outbox

MassTransit provides a transactional outbox pattern built on top of EF Core. It ensures that writing to your business database and dispatching a message are a single atomic operation — you can never have a customer saved without its command sent, or a command sent without the customer being saved.

### Configuration

```csharp
mt.AddEntityFrameworkOutbox<RegistrationDbContext>(outbox =>
{
    outbox.UseSqlServer();
    outbox.UseBusOutbox(busOutbox =>
    {
        busOutbox.MessageDeliveryLimit = 100;
    });
    outbox.QueryDelay = TimeSpan.FromSeconds(1);
});
```

### Tables added to each service's business database

```
InboxState    — tracks MessageIds of processed messages (deduplication / idempotency)
OutboxMessage — stores outgoing messages pending delivery to the transport
OutboxState   — tracks delivery state per outbox session
```

### How it works — the Bus Outbox (controller → transport)

When `UseBusOutbox()` is configured, injected `ISendEndpointProvider` and `IPublishEndpoint` become **outbox-aware**:

```csharp
// Nothing is sent to the transport yet — stored in EF change tracker only
await endpoint.Send<IPerformKyc>(new { ... });

// ATOMIC: Customer row + OutboxMessage row committed together
await _dbContext.SaveChangesAsync();

// Later: OutboxDeliveryService (background) reads OutboxMessage and delivers to TransportDb
```

If `SaveChangesAsync()` throws, **neither** the customer nor the outbox message is persisted. No orphaned messages, no half-committed state.

The `OutboxDeliveryService` (registered automatically by `UseBusOutbox()`) runs every `QueryDelay` (1 second here) and forwards pending `OutboxMessage` rows to the SQL transport.

### How it works — the Consumer Outbox (consumer → transport)

The consumer outbox is automatically active whenever `AddEntityFrameworkOutbox` is configured. You do not need `UseBusOutbox()` for this to work. When a consumer calls `context.Publish()` or `context.Send()` and then commits its `DbContext`, the outgoing messages are stored in `OutboxMessage` atomically with the consumer's business write:

```csharp
// PerformKycConsumer.cs
_dbContext.KycRecords.Add(kycRecord);

// Stored in OutboxMessage in RiskDb, NOT yet sent to transport
await context.Publish<ICustomerValidated>(new { ... });

// ATOMIC: KycRecord + OutboxMessage both committed
await _dbContext.SaveChangesAsync();

// MT then acknowledges the incoming IPerformKyc message
// Later: OutboxDeliveryService delivers the ICustomerValidated event to TransportDb
```

If the consumer throws before `SaveChangesAsync()`, no KycRecord is saved and no event is published — the incoming `IPerformKyc` message is nacked and retried.

### Idempotency via InboxState

When a message is delivered, MT records its `MessageId` in `InboxState` (keyed on `MessageId + ConsumerId`). If the exact same message arrives again (due to a crash-and-retry at just the wrong moment), MT detects the duplicate via `InboxState` and skips processing — preventing double-writes to your business database.

**NSB comparison:** NSB's Outbox feature works identically. `EnableOutbox()` in NSB stores outgoing messages alongside business data. MT's implementation is tightly coupled to EF Core; NSB supports multiple persistence backends.

---

## 9. MassTransit Topology Explained

When `ConfigureEndpoints(context)` runs at service startup, MassTransit automatically creates the full broker topology. You never manually create queues, topics, or subscriptions.

### What `ConfigureEndpoints` does for each registered consumer

For `PerformKycConsumer` (RiskService):
1. Derives queue name: `PerformKycConsumer` → `perform-kyc` (with KebabCaseEndpointNameFormatter)
2. Creates `transport.Queue` rows: `perform-kyc` types 1, 2, 3
3. Creates `transport.Topic` row: `MassTransitPlayground.Contracts.Commands:IPerformKyc`
4. Creates `transport.Queue` rows: `PerformKyc` types 1, 2, 3 (topic relay queue)
5. Creates `transport.QueueSubscription`: `IPerformKyc` topic → `PerformKyc` relay → `perform-kyc` consumer

For `CustomerValidatedConsumer` (RegistrationService):
1. Derives queue name: `CustomerValidatedConsumer` → `customer-validated`
2. Creates `transport.Queue` rows: `customer-validated` types 1, 2, 3
3. Creates `transport.Topic` row: `MassTransitPlayground.Contracts.Events:ICustomerValidated`
4. Creates `transport.Queue` rows: `CustomerValidated` types 1, 2, 3 (topic relay queue)
5. Creates `transport.QueueSubscription`: `ICustomerValidated` topic → `CustomerValidated` relay → `customer-validated` consumer

### Why a topic is created even for commands

MassTransit creates topic infrastructure for **every consumed message type**, regardless of whether you Send or Publish it. This is because the transport layer does not enforce the command/event distinction — that is purely a developer convention at the code level. The topic provides a safety net: if someone accidentally publishes a command instead of sending it, the message will still route correctly.

### The relay queue pattern

```
Publisher calls Publish<ICustomerValidated>()
        │
        ▼
transport.Message  (DestinationAddress = ...ICustomerValidated?type=topic)
        │
        ▼
SQL transport reads transport.QueueSubscription
        │
        ├──► MessageDelivery for customer-validated (RegistrationService subscriber)
        └──► MessageDelivery for any-other-service  (if it also subscribed)
```

The PascalCase queues (`PerformKyc`, `CustomerValidated`) in `transport.Queue` are the **topic relay queues** — internal infrastructure created by the SQL transport to bridge the topic abstraction onto the queue-based storage model. You should not send to them directly.

---

## 10. NSB vs MassTransit Comparison

| Concept | NServiceBus | MassTransit |
|---|---|---|
| **Message contracts** | Concrete classes implementing `ICommand`/`IEvent` | Interfaces; MT generates proxy classes at runtime |
| **Handler registration** | Auto-discovery via assembly scanning | Explicit: `mt.AddConsumer<T>()` |
| **Handler interface** | `IHandleMessages<T>.Handle(msg, context)` | `IConsumer<T>.Consume(ConsumeContext<T>)` |
| **Endpoint naming** | Explicit (`endpointConfig.DefaultReceiverQueueAddress`) | Convention-based (`ConfigureEndpoints` + formatter) |
| **Multiple handlers per endpoint** | Default — all handlers share one queue | Each consumer gets its own queue by default |
| **Sending commands** | `session.Send(new Cmd(), opts)` with routing config | `sendEndpointProvider.GetSendEndpoint(uri).Send<ICmd>(new {...})` |
| **Publishing events** | `session.Publish(new Evt())` | `context.Publish<IEvt>(new {...})` |
| **Outbox** | `endpointConfig.EnableOutbox()` | `mt.AddEntityFrameworkOutbox<TDbContext>()` |
| **Retry — immediate** | `config.Recoverability().Immediate(i => i.NumberOfRetries(3))` | `cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)))` |
| **Retry — delayed** | `config.Recoverability().Delayed(...)` | `cfg.UseDelayedRedelivery(r => r.Intervals(...))` |
| **Error queue** | Configured globally as named queue | Auto-created per consumer: `{name}_error` (type=2) |
| **DI lifetime** | Constructor injection (NSB 8+) | Constructor injection, scoped per message |
| **Transport swap** | Change transport package and config | Change `UsingSqlServer` to `UsingRabbitMq` etc. |
| **Topic/subscription management** | Manual (Azure Service Bus) or auto (some transports) | Always automatic via `ConfigureEndpoints` |

### Key philosophical difference

**NSB** is endpoint-first: you name your endpoint, all handlers in that endpoint share a single input queue, and you configure routing tables to direct messages to endpoints.

**MassTransit** is consumer-first: each consumer type gets its own queue by convention, and the framework derives everything else (queue names, topics, subscriptions) from the registered consumers. You can group consumers under a shared endpoint, but it is opt-in.

---

## 11. Running the Project

### Prerequisites

- .NET 8 SDK
- SQL Server (or Docker: `docker compose up`)
- Visual Studio or Rider

### Startup order

**Always start both services simultaneously** (or RiskService first). If RegistrationService starts and accepts requests before RiskService has created the `perform-kyc` queue, the outbox delivery may create a ghost queue with a different ID.

Use the included `.slnLaunch` profile to start both together in Visual Studio.

### Testing the flow

```http
POST http://localhost:{port}/api/customers
Content-Type: application/json

{
  "firstName": "Jane",
  "lastName": "Doe",
  "email": "jane.doe@example.com",
  "dateOfBirth": "1990-01-15"
}
```

To trigger a rejection, use an email starting with `reject`:

```json
{ "email": "reject@example.com", ... }
```

### Verifying the message flow in SQL

```sql
-- Messages in flight
SELECT * FROM transport.Message ORDER BY SentTime DESC

-- Delivery status
SELECT md.DeliveryCount, md.LockId, q.Name AS Queue
FROM transport.MessageDelivery md
JOIN transport.Queue q ON q.Id = md.QueueId

-- Topic subscriptions
SELECT t.Name AS Topic, q.Name AS Queue, q.Type
FROM transport.QueueSubscription qs
JOIN transport.Topic t ON t.Id = qs.SourceId
JOIN transport.Queue q ON q.Id = qs.DestinationId
```

---

## 12. Lessons Learned and Gotchas

### 1. `KebabCaseEndpointNameFormatter` is not the default

The most impactful lesson. `DefaultEndpointNameFormatter` produces `PerformKyc` (PascalCase). `KebabCaseEndpointNameFormatter` produces `perform-kyc`. If you hardcode queue URIs using kebab-case but forget to call `mt.SetKebabCaseEndpointNameFormatter()`, your messages will be silently orphaned with no errors.

**Always call `mt.SetKebabCaseEndpointNameFormatter()` explicitly** and be consistent across all services.

### 2. `transport.Queue` stores three rows per endpoint — not one

Every queue exists in three flavours (types 1, 2, 3) in the same table. When joining `MessageDelivery` to `Queue`, always filter on `Type = 1` if you want the main receive queue.

### 3. Orphaned messages after dropping databases

When you drop and recreate the transport database, both services must be restarted **before** any messages are sent. The `MessageDelivery.QueueId` is a numeric ID that changes on each database recreation. Stale IDs from a previous run will cause the consumer to poll a queue ID that no longer has any `MessageDelivery` rows.

### 4. The `OutboxDeliveryService` runs independently of consumer endpoints

`UseBusOutbox()` registers an `OutboxDeliveryService` that can start and deliver messages even if consumer endpoints have failed to start. This means you can see messages appear in `transport.MessageDelivery` while the corresponding consumer queue doesn't exist yet. Always check **both** the transport DB (`MessageDelivery`) and the service's business DB (`OutboxMessage`) when debugging stuck messages.

### 5. `IPerformKyc` appearing in `transport.Topic` is expected

Even though `IPerformKyc` is a command (always `Send`-ed, never `Publish`-ed), MassTransit creates topic infrastructure for it. This is because `ConfigureEndpoints` creates full pub/sub topology for all consumed types. It is not a bug.

### 6. Source address reflects the service's receive endpoint, not the sender's temp address

In `transport.Message.SourceAddress`, you will see the **receive endpoint address** of the sending service (e.g., `db://localhost:1433/customer-validated`), not a temporary address. This is the service's "reply-to" address — where fault messages and responses will be sent.

### 7. MassTransit log level `Debug` is invaluable for transport debugging

```json
"MassTransit": "Debug"
```

This logs every queue creation, topic creation, and subscription creation at startup — which is exactly what revealed the `PerformKyc` vs `perform-kyc` naming mismatch in this project. Always enable it when something isn't routing correctly.
