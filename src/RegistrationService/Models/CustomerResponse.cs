using MassTransitPlayground.RegistrationService.Domain;

namespace MassTransitPlayground.RegistrationService.Models;

public record CustomerResponse(
    Guid Id,
    string FirstName,
    string LastName,
    string Email,
    string Status,
    DateTime CreatedAt,
    DateTime? ValidatedAt,
    string? ValidationReason
)
{
    public static CustomerResponse FromDomain(Customer customer) => new(
        customer.Id,
        customer.FirstName,
        customer.LastName,
        customer.Email,
        customer.Status.ToString(),
        customer.CreatedAt,
        customer.ValidatedAt,
        customer.ValidationReason
    );
}
