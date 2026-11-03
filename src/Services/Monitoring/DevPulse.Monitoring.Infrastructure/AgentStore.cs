using DevPulse.Contracts.Events.Monitoring;
using DevPulse.Contracts.Messaging;
using DevPulse.Monitoring.Application;
using DevPulse.Monitoring.Domain;
using Microsoft.EntityFrameworkCore;

namespace DevPulse.Monitoring.Infrastructure;

/// <summary>
/// Agents du monitoring hybride. Un agent silencieux produit les mêmes événements qu'un service en panne
/// (source « Agent ») : incidents, alertes et analyse fonctionnent sans code propre aux agents.
/// </summary>
public sealed class AgentStore(MonitoringDb db, IOutbox outbox, TimeProvider clock) : IAgentStore
{
    public async Task<AgentCreated> RegisterAsync(Guid organizationId, AgentRegistrationRequest request, CancellationToken ct)
    {
        var (agent, key) = Agent.Register(organizationId, request.Name, request.Environment, clock.GetUtcNow());
        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        return new AgentCreated(AgentResponse.From(agent), key);
    }

    public async Task<IReadOnlyList<AgentResponse>> ListAsync(Guid organizationId, CancellationToken ct) =>
        (await db.Agents.AsNoTracking().Where(a => a.OrganizationId == organizationId && a.RevokedAt == null).OrderBy(a => a.Name).ToListAsync(ct))
            .Select(AgentResponse.From).ToList();

    public async Task<bool> RevokeAsync(Guid organizationId, Guid agentId, CancellationToken ct)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.OrganizationId == organizationId && a.Id == agentId && a.RevokedAt == null, ct);
        if (agent is null) return false;
        agent.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AgentSamplePoint>?> SamplesAsync(Guid organizationId, Guid agentId, DateTimeOffset since, CancellationToken ct)
    {
        if (!await db.Agents.AnyAsync(a => a.OrganizationId == organizationId && a.Id == agentId, ct)) return null;
        return await db.AgentSamples.AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.AgentId == agentId && s.At >= since)
            .OrderBy(s => s.At)
            .Select(s => new AgentSamplePoint(s.At, s.CpuPercent, s.MemoryPercent, s.DiskPercent))
            .ToListAsync(ct);
    }

    public async Task<IngestOutcome> IngestAsync(string? key, AgentReportRequest request, CancellationToken ct)
    {
        if (Agent.ParseKey(key) is not var (agentId, secret)) return IngestOutcome.Unauthorized;

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null || !agent.Accepts(secret)) return IngestOutcome.Unauthorized;

        var report = new AgentReport(request.Hostname, request.OperatingSystem ?? "", request.AgentVersion ?? "", request.IntervalSeconds,
            request.CpuPercent, request.MemoryPercent, request.DiskPercent, request.UptimeSeconds, clock.GetUtcNow());
        var change = agent.Report(report);
        db.AgentSamples.Add(AgentSample.From(agent, report));
        Publish(agent, change, report.ReceivedAt);

        await db.SaveChangesAsync(ct);
        MonitoringMetrics.RecordHeartbeat();
        return IngestOutcome.Accepted;
    }

    public async Task<int> DetectSilentAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Filtre large en base, décision exacte dans le domaine (le seuil dépend de l'intervalle de chaque agent).
        var candidates = await db.Agents
            .Where(a => a.RevokedAt == null && a.SilentSince == null && a.LastSeenAt != null && a.LastSeenAt < now.AddSeconds(-60))
            .ToListAsync(ct);

        var silent = 0;
        foreach (var agent in candidates)
        {
            if (!agent.CheckSilence(now, out var change)) continue;
            Publish(agent, change, now);
            silent++;
        }

        if (silent > 0) await db.SaveChangesAsync(ct);
        return silent;
    }

    private void Publish(Agent agent, TargetChange change, DateTimeOffset at)
    {
        if (!change.StatusChanged && !change.OutageStarted && !change.OutageEnded) return;
        var correlationId = Guid.NewGuid().ToString("N");

        if (change.StatusChanged)
            outbox.Enqueue(new ServiceStatusChangedV1
            {
                Producer = MonitoringEvents.Producer, OrganizationId = agent.OrganizationId, CorrelationId = correlationId,
                ServiceId = agent.Id, ServiceName = agent.Name, Environment = agent.Environment,
                PreviousStatus = change.Previous.ToString(), CurrentStatus = change.Current.ToString(),
                Failure = change.Current == HealthStatus.Down ? "AgentSilent" : null, ChangedAt = at,
            });

        if (change.OutageStarted) MonitoringMetrics.RecordOutage(MonitoringSources.Agent);
        if (change.OutageStarted)
            outbox.Enqueue(new ServiceDownDetectedV1
            {
                Producer = MonitoringEvents.Producer, OrganizationId = agent.OrganizationId, CorrelationId = correlationId,
                ServiceId = agent.Id, ServiceName = agent.Name, Environment = agent.Environment, Source = MonitoringSources.Agent,
                FailureCount = agent.MissedBeats(at), Failure = "AgentSilent", DetectedAt = at,
            });

        if (change.EndedOutageSince is { } since)
            outbox.Enqueue(new ServiceRecoveredV1
            {
                Producer = MonitoringEvents.Producer, OrganizationId = agent.OrganizationId, CorrelationId = correlationId,
                ServiceId = agent.Id, ServiceName = agent.Name, Environment = agent.Environment, DownSince = since, RecoveredAt = at,
            });
    }
}
