namespace MassTransitPlayground.RegistrationService.Domain;

/// <summary>
/// Aggregate root representing a registered customer.
///
/// The customer lifecycle in this system:
///   1. Customer registers → Status = Pending
///   2. KYC command sent to Risk Service (via outbox)
///   3. Risk Service validates and publishes CustomerValidated event
///   4. Registration Service handles event → Status = Validated or Rejected
/// </summary>
public class Customer
{
    public Guid Id { get; private set; }
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public DateOnly DateOfBirth { get; private set; }
    public CustomerStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ValidatedAt { get; private set; }
    public string? ValidationReason { get; private set; }

    private Customer() { }

    public static Customer Create(string firstName, string lastName, string email, DateOnly dateOfBirth)
    {
        return new Customer
        {
            Id = Guid.NewGuid(),
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            DateOfBirth = dateOfBirth,
            Status = CustomerStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Updates the customer status after receiving the KYC result from the Risk Service.
    /// This method is called by the CustomerValidatedConsumer.
    /// </summary>
    public void ApplyValidationResult(bool isApproved, string reason, DateTime validatedAt)
    {
        Status = isApproved ? CustomerStatus.Validated : CustomerStatus.Rejected;
        ValidationReason = reason;
        ValidatedAt = validatedAt;
    }
}
