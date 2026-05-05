namespace MassTransitPlayground.RiskService.Domain;

/// <summary>
/// Persistence record of a KYC (Know Your Customer) validation performed by the Risk Service.
/// Each KycRecord corresponds to one IPerformKyc command processed.
/// </summary>
public class KycRecord
{
    public Guid Id { get; private set; }

    /// <summary>The customer ID from the Registration Service. Used for correlation.</summary>
    public Guid CustomerId { get; private set; }

    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public DateOnly DateOfBirth { get; private set; }
    public bool IsApproved { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTime ProcessedAt { get; private set; }

    private KycRecord() { }

    public static KycRecord Create(
        Guid customerId,
        string firstName,
        string lastName,
        string email,
        DateOnly dateOfBirth,
        bool isApproved,
        string reason)
    {
        return new KycRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            DateOfBirth = dateOfBirth,
            IsApproved = isApproved,
            Reason = reason,
            ProcessedAt = DateTime.UtcNow
        };
    }
}
