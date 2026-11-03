using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevPulse.Contracts.Events.Common;

namespace DevPulse.Messaging;

public static class EventSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(IntegrationEvent integrationEvent) =>
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), Options);

    public static IntegrationEvent? Deserialize(ReadOnlySpan<byte> body, Type type) =>
        JsonSerializer.Deserialize(body, type, Options) as IntegrationEvent;

    /// <summary>Clé de routage d'un type d'événement, sans en construire une instance complète.</summary>
    public static string RoutingKeyOf(Type eventType)
    {
        if (!typeof(IntegrationEvent).IsAssignableFrom(eventType) || eventType.IsAbstract)
            throw new ArgumentException($"{eventType.Name} n'est pas un événement concret.", nameof(eventType));

        // EventType et Version sont des constantes de la classe : une instance vide suffit à les lire.
        var empty = (IntegrationEvent)RuntimeHelpers.GetUninitializedObject(eventType);
        return empty.RoutingKey;
    }
}
