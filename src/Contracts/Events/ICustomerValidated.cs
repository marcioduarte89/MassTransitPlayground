namespace MassTransitPlayground.Contracts.Events;

/// <summary>
/// Event published by the Risk Service after completing KYC validation.
///
/// ------------------------------------------------------------------
/// MT vs NSB: Events and the Publish/Subscribe Model
/// ------------------------------------------------------------------
/// In MassTransit, events are published using IBus.Publish() or ConsumeContext.Publish().
/// MT automatically:
///   1. Creates a TOPIC for the message type (in SQL transport: a row in transport.Topic table).
///   2. Creates a SUBSCRIPTION linking the topic to any endpoint that has a consumer for this type.
///
/// This is transparent — you never manually create topics or subscriptions.
/// MT handles topology at startup when it configures the broker.
///
/// NSB EQUIVALENT:
///   In NSB with Azure Service Bus, you configure subscriptions via routing:
///     transport.SubscribeTo<CustomerValidated>();
///   In NSB with MSMQ, you use pub/sub with explicit subscription management.
///   In MT, all of this is automatic based on your registered consumers.
///
/// SQL TRANSPORT TOPOLOGY:
///   When using the SQL transport, topics and subscriptions are rows in database tables:
///     - transport.Topic: one row per message type (e.g., "massTransitPlayground.contracts.events:ICustomerValidated")
///     - transport.TopicSubscription: one row linking the topic to a receiving queue
///   MT creates these automatically when both services start.
///
/// NAMING CONVENTION:
///   By default, MT derives the topic name from the message type's full namespace + type name,
///   formatted as "namespace:TypeName" in lowercase with dots replacing namespace separators.
///   Example: "massTransitPlayground.contracts.events:ICustomerValidated"
///   This ensures messages from different namespaces don't collide even with the same type name.
/// ------------------------------------------------------------------
/// </summary>
public interface ICustomerValidated
{
    /// <summary>
    /// The customer ID from the original IPerformKyc command.
    /// This is how the Registration Service correlates the response back to the right customer.
    ///
    /// NSB COMPARISON:
    ///   NSB saga correlation works similarly — you define a CorrelationId property and map it
    ///   to the saga's ID. In MT, for simple consumer-to-consumer correlation (no saga),
    ///   you manually use a business key like CustomerId to look up the correct record.
    /// </summary>
    Guid CustomerId { get; }

    /// <summary>
    /// Indicates whether the customer passed the KYC check.
    /// </summary>
    bool IsApproved { get; }

    /// <summary>
    /// Human-readable reason for the KYC outcome (approved or rejected).
    /// </summary>
    string Reason { get; }

    /// <summary>
    /// Timestamp when the KYC validation was completed by the Risk Service.
    /// </summary>
    DateTime ValidatedAt { get; }
}
