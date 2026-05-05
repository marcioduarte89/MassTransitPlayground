namespace MassTransitPlayground.RegistrationService.Domain;

public enum CustomerStatus
{
    /// <summary>KYC has not yet been completed.</summary>
    Pending = 0,

    /// <summary>Customer passed KYC — fully registered.</summary>
    Validated = 1,

    /// <summary>Customer failed KYC — registration rejected.</summary>
    Rejected = 2
}
