namespace DevPulse.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>Faux dans les tests d'API : l'outbox se remplit, rien ne part et rien n'est consommé.</summary>
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "guest";
    public string Password { get; set; } = "";
    public string VirtualHost { get; set; } = "/";

    /// <summary>Amazon MQ n'accepte que AMQPS (port 5671).</summary>
    public bool UseTls { get; set; }

    /// <summary>Délai avant une nouvelle tentative, porté par une file d'attente à durée de vie.</summary>
    public int RetryDelaySeconds { get; set; } = 5;

    /// <summary>Au-delà, le message part en file morte avec la dernière erreur.</summary>
    public int MaxAttempts { get; set; } = 5;

    public ushort Prefetch { get; set; } = 10;

    public int OutboxPollMilliseconds { get; set; } = 1000;
    public int OutboxBatchSize { get; set; } = 50;
}
