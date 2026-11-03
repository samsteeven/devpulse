using System.Diagnostics.Metrics;
using DevPulse.Monitoring.Domain;

namespace DevPulse.Monitoring.Infrastructure;

/// <summary>
/// Mesures du moteur de surveillance. La plus importante est le nombre de vérifications : s'il tombe à zéro, plus
/// rien n'est surveillé, et aucune erreur ne le dit.
/// </summary>
public static class MonitoringMetrics
{
    public const string MeterName = "DevPulse.Monitoring";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Checks =
        Meter.CreateCounter<long>("devpulse.checks", "{check}", "Vérifications effectuées, par état constaté");

    private static readonly Histogram<double> CheckDuration =
        Meter.CreateHistogram<double>("devpulse.check.duration", "ms", "Temps de réponse mesuré par les sondes");

    private static readonly Counter<long> Outages =
        Meter.CreateCounter<long>("devpulse.outages.detected", "{outage}", "Pannes confirmées (sondes et agents silencieux)");

    private static readonly Counter<long> Heartbeats =
        Meter.CreateCounter<long>("devpulse.agents.heartbeats", "{heartbeat}", "Battements d'agents acceptés");

    public static void RecordCheck(ProbeResult result)
    {
        var status = new KeyValuePair<string, object?>("status", result.Status.ToString());
        Checks.Add(1, status, new KeyValuePair<string, object?>("failure", result.Failure.ToString()));
        CheckDuration.Record(result.ResponseTimeMs, status);
    }

    public static void RecordOutage(string source) => Outages.Add(1, new KeyValuePair<string, object?>("source", source));

    public static void RecordHeartbeat() => Heartbeats.Add(1);
}
