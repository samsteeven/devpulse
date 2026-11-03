using System.Threading.Channels;
using DevPulse.Monitoring.Application;
using Microsoft.Extensions.Options;

namespace DevPulse.Monitoring.Worker;

public sealed class WorkerOptions
{
    public const string SectionName = "Monitoring";

    public int ConcurrentProbes { get; set; } = 16;
    public int SyncIntervalSeconds { get; set; } = 30;
    public int ClaimIntervalMilliseconds { get; set; } = 1000;
    public int RetentionDays { get; set; } = 7;
}

/// <summary>Recopie périodiquement la liste des services actifs depuis Registry. Registry indisponible : on garde la dernière liste connue.</summary>
public sealed class RegistrySyncService(
    IServiceScopeFactory scopes, IRegistryClient registry, IOptions<WorkerOptions> options,
    TimeProvider clock, ILogger<RegistrySyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SyncIntervalSeconds), clock);
        do
        {
            try
            {
                var active = await registry.GetActiveServicesAsync(stoppingToken);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ITargetStore>().SynchronizeAsync(active, clock.GetUtcNow(), stoppingToken);
                logger.LogDebug("Synchronisation avec Registry : {Count} service(s) actif(s)", active.Count);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Synchronisation avec Registry impossible ; la dernière liste connue reste utilisée");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Le planificateur réserve les cibles arrivées à échéance et les place dans une file bornée ; un nombre fixe de
/// sondes la consomme. Il ne fait aucun appel HTTP lui-même, et ne réserve jamais plus que ce que la file peut prendre :
/// si les cibles répondent lentement, le retard s'accumule en base, pas en mémoire.
/// </summary>
public sealed class CheckSchedulerService(
    IServiceScopeFactory scopes, ITargetProber prober, IOptions<WorkerOptions> options,
    TimeProvider clock, ILogger<CheckSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var capacity = settings.ConcurrentProbes * 2;
        var queue = Channel.CreateBounded<CheckAssignment>(new BoundedChannelOptions(capacity) { SingleWriter = true });

        var probes = Enumerable.Range(0, settings.ConcurrentProbes)
            .Select(_ => Task.Run(() => ConsumeAsync(queue.Reader, stoppingToken), CancellationToken.None))
            .ToList();

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settings.ClaimIntervalMilliseconds), clock);
        try
        {
            do
            {
                var free = capacity - queue.Reader.Count;
                if (free <= 0) continue;

                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var claimed = await scope.ServiceProvider.GetRequiredService<ITargetStore>().ClaimDueAsync(free, clock.GetUtcNow(), stoppingToken);
                    foreach (var assignment in claimed) await queue.Writer.WriteAsync(assignment, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "Réservation des vérifications impossible");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        finally
        {
            queue.Writer.TryComplete();
            await Task.WhenAll(probes);
        }
    }

    private async Task ConsumeAsync(ChannelReader<CheckAssignment> reader, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var assignment in reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var result = await prober.ProbeAsync(assignment.TargetUrl, assignment.Timeout, stoppingToken);

                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ITargetStore>().RecordAsync(assignment.ServiceId, result, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Une vérification ratée ne doit pas tuer la sonde. La cible revient à sa prochaine échéance.
                    logger.LogError(e, "Vérification du service {ServiceId} en erreur", assignment.ServiceId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Supprime l'historique trop ancien : une vérification par minute et par service, sans purge, finit par peser.</summary>
public sealed class RetentionService(
    IServiceScopeFactory scopes, IOptions<WorkerOptions> options, TimeProvider clock, ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var limit = clock.GetUtcNow().AddDays(-options.Value.RetentionDays);
                var deleted = await scope.ServiceProvider.GetRequiredService<ITargetStore>().PurgeOlderThanAsync(limit, stoppingToken);
                if (deleted > 0) logger.LogInformation("Purge : {Deleted} vérification(s) supprimée(s)", deleted);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Purge de l'historique impossible");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Repère les agents qui ne donnent plus de nouvelles, toutes les 15 secondes.</summary>
public sealed class AgentWatchService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AgentWatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var silent = await scope.ServiceProvider.GetRequiredService<IAgentStore>().DetectSilentAsync(clock.GetUtcNow(), stoppingToken);
                if (silent > 0) logger.LogWarning("{Count} agent(s) silencieux", silent);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Surveillance des agents impossible");
            }
        }
    }
}
