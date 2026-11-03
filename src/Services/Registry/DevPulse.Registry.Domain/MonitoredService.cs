namespace DevPulse.Registry.Domain;

public sealed class DomainException(string message) : Exception(message);

public enum ServiceEnvironment
{
    Production,
    Staging,
    Development,
}

/// <summary>Un service surveillé appartient à une seule organisation.</summary>
public sealed class MonitoredService
{
    public const int MinIntervalSeconds = 30;
    public const int MaxIntervalSeconds = 3600;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 30;

    private MonitoredService() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = "";
    public ServiceEnvironment Environment { get; private set; }
    public string BaseUrl { get; private set; } = "";
    public string HealthCheckPath { get; private set; } = "";
    public int CheckIntervalSeconds { get; private set; }
    public int TimeoutSeconds { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static MonitoredService Create(
        Guid organizationId, string name, ServiceEnvironment environment, string baseUrl,
        string healthCheckPath, int checkIntervalSeconds, int timeoutSeconds, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty) throw new DomainException("L'organisation est obligatoire.");

        var service = new MonitoredService { Id = Guid.NewGuid(), OrganizationId = organizationId, Enabled = true, CreatedAt = now };
        service.Configure(name, environment, baseUrl, healthCheckPath, checkIntervalSeconds, timeoutSeconds, now);
        return service;
    }

    public void Update(
        string name, ServiceEnvironment environment, string baseUrl, string healthCheckPath,
        int checkIntervalSeconds, int timeoutSeconds, bool enabled, DateTimeOffset now)
    {
        Configure(name, environment, baseUrl, healthCheckPath, checkIntervalSeconds, timeoutSeconds, now);
        Enabled = enabled;
    }

    private void Configure(
        string name, ServiceEnvironment environment, string baseUrl, string healthCheckPath,
        int checkIntervalSeconds, int timeoutSeconds, DateTimeOffset now)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 2 or > 100) throw new DomainException("Le nom doit faire de 2 à 100 caractères.");

        if (checkIntervalSeconds is < MinIntervalSeconds or > MaxIntervalSeconds)
            throw new DomainException($"L'intervalle doit être compris entre {MinIntervalSeconds} et {MaxIntervalSeconds} secondes.");

        if (timeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            throw new DomainException($"Le délai doit être compris entre {MinTimeoutSeconds} et {MaxTimeoutSeconds} secondes.");

        if (timeoutSeconds >= checkIntervalSeconds)
            throw new DomainException("Le délai doit être inférieur à l'intervalle de vérification.");

        // « //hote/x » et « \\hote\x » sont des URL relatives au schéma : combinées à la base, elles changeraient d'hôte.
        healthCheckPath = healthCheckPath?.Trim() ?? "";
        if (!healthCheckPath.StartsWith('/') || healthCheckPath.StartsWith("//") || healthCheckPath.Contains('\\') || healthCheckPath.Length > 500)
            throw new DomainException("Le chemin de vérification doit commencer par un seul « / ».");

        Name = name;
        Environment = environment;
        BaseUrl = baseUrl?.Trim().TrimEnd('/') ?? "";
        HealthCheckPath = healthCheckPath;
        CheckIntervalSeconds = checkIntervalSeconds;
        TimeoutSeconds = timeoutSeconds;
        UpdatedAt = now;
    }

    public string TargetUrl => BaseUrl + HealthCheckPath;
}
