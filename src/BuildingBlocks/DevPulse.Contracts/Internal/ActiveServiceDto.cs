namespace DevPulse.Contracts.Internal;

/// <summary>Service à surveiller, tel que Registry l'expose au worker de monitoring (route interne).</summary>
public sealed record ActiveServiceDto(
    Guid ServiceId,
    Guid OrganizationId,
    string Name,
    string Environment,
    string TargetUrl,
    int CheckIntervalSeconds,
    int TimeoutSeconds);
