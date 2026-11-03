using System.Diagnostics.Metrics;
using IncidentEntity = DevPulse.Incident.Domain.Incident;

namespace DevPulse.Incident.Application;

/// <summary>Mesures des incidents : ouverts, résolus, et durée jusqu'à la résolution (la base du MTTR).</summary>
public static class IncidentMetrics
{
    public const string MeterName = "DevPulse.Incident";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Opened =
        Meter.CreateCounter<long>("devpulse.incidents.opened", "{incident}", "Incidents ouverts");

    private static readonly Counter<long> Resolved =
        Meter.CreateCounter<long>("devpulse.incidents.resolved", "{incident}", "Incidents résolus, automatiquement ou non");

    private static readonly Histogram<double> TimeToResolve = Meter.CreateHistogram<double>(
        "devpulse.incidents.time_to_resolve", "s", "Durée entre la détection et la résolution",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [30, 60, 120, 300, 600, 1800, 3600, 14400, 86400] });

    public static void RecordOpened(IncidentEntity incident) => Opened.Add(1,
        new KeyValuePair<string, object?>("severity", incident.Severity.ToString()),
        new KeyValuePair<string, object?>("source", incident.Source));

    public static void RecordResolved(IncidentEntity incident)
    {
        var automatic = new KeyValuePair<string, object?>("automatic", incident.ResolvedAutomatically);
        Resolved.Add(1, automatic, new KeyValuePair<string, object?>("severity", incident.Severity.ToString()));
        TimeToResolve.Record(incident.DurationSeconds(incident.ResolvedAt!.Value), automatic);
    }
}
