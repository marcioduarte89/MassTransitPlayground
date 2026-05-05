namespace MassTransitPlayground.Contracts.Commands;

/// <summary>
/// Command sent by the Registration Service to request KYC (Know Your Customer) verification.
///
/// ------------------------------------------------------------------
/// MT vs NSB: Message Contract Design
/// ------------------------------------------------------------------
/// MassTransit strongly recommends defining messages as INTERFACES rather than classes.
/// This is a key philosophical difference from NServiceBus (which typically uses plain classes).
///
/// WHY INTERFACES?
///   1. Polymorphism: A single message can implement multiple interfaces, allowing a consumer
///      to subscribe to a broader contract (e.g., ICustomerEvent) even when receiving a
///      specific one (e.g., ICustomerValidated).
///   2. Structural typing: MT uses anonymous type initializers ({ CustomerId = ... }) to
///      create message instances at publish/send time. MT generates a proxy class at runtime
///      that implements the interface. This keeps your contract code free of implementation details.
///   3. Immutability: Interfaces naturally enforce read-only properties at the contract level,
///      which is a good practice for messages (they should not be mutated after creation).
///
/// HOW DOES MT HANDLE INTERFACE MESSAGES?
///   When you call context.Send<IPerformKyc>(new { CustomerId = ... }), MassTransit uses
///   Castle DynamicProxy (or similar) to generate a concrete class implementing IPerformKyc
///   at runtime. The anonymous object's properties are matched by name to the interface properties.
///
/// NSB EQUIVALENT:
///   In NServiceBus, you would typically write:
///     public class PerformKyc : ICommand { public Guid CustomerId { get; set; } ... }
///   And then: await session.Send(new PerformKyc { CustomerId = ... });
///
/// COMMAND vs EVENT in MassTransit:
///   - Commands (ICommand marker, or by convention) represent an INSTRUCTION to do something.
///     They are sent (Send) to a SPECIFIC endpoint/queue. One sender, one receiver.
///   - Events represent something that HAS HAPPENED. They are published (Publish) to a topic.
///     One publisher, many subscribers.
///   MT does not enforce this distinction at compile time (unlike NSB which has ICommand/IEvent
///   marker interfaces with validation). It is a CONVENTION you enforce in your code.
///   Naming convention: Commands are imperatives ("PerformKyc"), Events are past tense ("CustomerValidated").
/// ------------------------------------------------------------------
/// </summary>
public interface IPerformKyc
{
    /// <summary>
    /// Unique identifier of the customer to be verified.
    /// Used for correlation — this same ID will appear in the CustomerValidated event
    /// so the Registration Service can match the response to the correct customer.
    /// </summary>
    Guid CustomerId { get; }

    string FirstName { get; }
    string LastName { get; }
    string Email { get; }
    DateOnly DateOfBirth { get; }

    /// <summary>
    /// Timestamp when this command was created.
    /// Useful for auditing and detecting stale/expired KYC requests.
    /// </summary>
    DateTime RequestedAt { get; }
}
