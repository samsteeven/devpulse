using DevPulse.Contracts.Events.Common;

namespace DevPulse.Contracts.Events.AI;

/// <summary>Proposition d'analyse. Purement consultative : rien n'est déclenché automatiquement à partir de son contenu.</summary>
public sealed record AnalysisCompletedV1 : IntegrationEvent
{
    public override string EventType => "ai.analysis.completed";
    public override int Version => 1;

    public required Guid IncidentId { get; init; }

    /// <summary><c>Rules</c> (règles locales) ou <c>Llm</c> (modèle externe).</summary>
    public required string Provider { get; init; }
    public string? Model { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<string> ProbableCauses { get; init; }
    public required IReadOnlyList<string> SuggestedActions { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}
