namespace MassTransitPlayground.RegistrationService.Models;

public record RegisterCustomerRequest(
    string FirstName,
    string LastName,
    string Email,
    DateOnly DateOfBirth
);
