using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DevPulse.Alerting.Application;
using DevPulse.Alerting.Domain;
using DevPulse.Contracts.Events.Incident;
using DevPulse.Contracts.Events.Monitoring;
using DevPulse.Contracts.Messaging;
using DevPulse.Messaging;
using DevPulse.Security.Ssrf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace DevPulse.Alerting.Infrastructure;

public sealed class AlertingDb(DbContextOptions<AlertingDb> options) : DbContext(options)
{
    public DbSet<AlertChannel> Channels => Set<AlertChannel>();
    public DbSet<AlertDelivery> Deliveries => Set<AlertDelivery>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.AddMessagingTables();

        model.Entity<AlertChannel>(e =>
        {
            e.ToTable("alert_channels");
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.Name).HasMaxLength(AlertChannel.MaxNameLength);
            e.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Url).HasMaxLength(1000);
            e.Property(c => c.SigningSecret).HasMaxLength(64);
            // Liste courte et fermée : stockée en texte (« IncidentCreated,IncidentResolved »), lisible en base.
            e.Property(c => c.Events)
                .HasConversion(
                    v => string.Join(',', v),
                    v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<AlertEvent>).ToList(),
                    new ValueComparer<List<AlertEvent>>((a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, HashCode.Combine), v => v.ToList()))
                .HasMaxLength(100);
            e.Property(c => c.Environments).HasColumnType("jsonb");
            e.HasIndex(c => c.OrganizationId);
        });

        model.Entity<AlertDelivery>(e =>
        {
            e.ToTable("alert_deliveries");
            e.Property(d => d.Id).ValueGeneratedNever();
            e.Property(d => d.Event).HasConversion<string>().HasMaxLength(30);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.ServiceName).HasMaxLength(100);
            e.Property(d => d.Payload).HasColumnType("jsonb");
            e.Property(d => d.LastError).HasMaxLength(500);
            e.Ignore(d => d.LatencyMilliseconds);
            e.HasIndex(d => d.NextAttemptAt).HasFilter("status = 'Pending'");
            e.HasIndex(d => new { d.OrganizationId, d.CreatedAt });
            // Supprimer un canal supprime son historique d'envois.
            e.HasOne<AlertChannel>().WithMany().HasForeignKey(d => d.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class AlertingDbDesignTimeFactory : IDesignTimeDbContextFactory<AlertingDb>
{
    public AlertingDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AlertingDb>()
        .UseNpgsql("Host=design-time").UseSnakeCaseNamingConvention().Options);
}

public sealed class AlertChannelRepository(AlertingDb db) : IAlertChannelRepository
{
    public async Task<IReadOnlyList<AlertChannel>> ListAsync(Guid organizationId, CancellationToken ct) =>
        await db.Channels.Where(c => c.OrganizationId == organizationId).OrderBy(c => c.Name).ToListAsync(ct);

    public Task<AlertChannel?> FindAsync(Guid organizationId, Guid id, CancellationToken ct) =>
        db.Channels.FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Id == id, ct);

    public void Add(AlertChannel channel) => db.Channels.Add(channel);
    public void Remove(AlertChannel channel) => db.Channels.Remove(channel);
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class DeliveryRepository(AlertingDb db) : IDeliveryRepository
{
    public async Task<IReadOnlyList<(AlertDelivery Delivery, string ChannelName)>> ListAsync(Guid organizationId, int limit, CancellationToken ct)
    {
        var rows = await (
            from delivery in db.Deliveries.AsNoTracking()
            join channel in db.Channels.AsNoTracking() on delivery.ChannelId equals channel.Id
            where delivery.OrganizationId == organizationId
            orderby delivery.CreatedAt descending
            select new { delivery, channel.Name }).Take(limit).ToListAsync(ct);
        return rows.Select(r => (r.delivery, r.Name)).ToList();
    }

    public void Add(AlertDelivery delivery) => db.Deliveries.Add(delivery);
}

/// <summary>
/// Envoi HTTP, par le même client protégé contre la SSRF que les sondes : le contrôle porte sur l'adresse réellement
/// contactée. Signature : HMAC-SHA256 de « horodatage.corps », pour que le destinataire rejette un message rejoué ou modifié.
/// </summary>
public sealed class HttpAlertSender(IHttpClientFactory clients, TimeProvider clock) : IAlertSender
{
    public const string ClientName = "alerts";

    public async Task<SendResult> SendAsync(OutgoingAlert alert, CancellationToken ct)
    {
        if (!Uri.TryCreate(alert.Url, UriKind.Absolute, out var uri)) return new SendResult(false, null, "URL invalide");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(alert.Payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.UserAgent.ParseAdd("DevPulse-Alerts/1.0");

        if (alert.Kind == ChannelKind.Webhook)
        {
            var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            request.Headers.Add("X-DevPulse-Event", alert.Event.ToString());
            request.Headers.Add("X-DevPulse-Delivery", alert.DeliveryId.ToString());
            request.Headers.Add("X-DevPulse-Timestamp", timestamp);
            request.Headers.Add("X-DevPulse-Signature", "sha256=" + Sign(alert.SigningSecret, timestamp, alert.Payload));
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)response.StatusCode;
            return response.IsSuccessStatusCode ? new SendResult(true, code, null) : new SendResult(false, code, $"HTTP {code}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SendResult(false, null, "Délai dépassé (10 s)");
        }
        catch (HttpRequestException e)
        {
            var blocked = e.InnerException is BlockedTargetException;
            return new SendResult(false, null, blocked ? "Adresse refusée par la politique de sécurité" : "Connexion impossible");
        }
    }

    public static string Sign(string secret, string timestamp, string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
}

/// <summary>Envoie les alertes en attente. Plusieurs instances se partagent le travail (SKIP LOCKED).</summary>
public sealed class DeliveryDispatcher(
    IServiceScopeFactory scopes, IAlertSender sender, DeliverySignal signal, TimeProvider clock, ILogger<DeliveryDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var processed = 0;
                try
                {
                    processed = await DispatchBatchAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Envoi des alertes interrompu");
                }

                if (processed == 0) await signal.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AlertingDb>();
        var now = clock.GetUtcNow();

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction;
        List<AlertDelivery> due;
        using (SuppressInstrumentationScope.Begin())
        {
            transaction = await db.Database.BeginTransactionAsync(ct);
            due = await db.Deliveries.FromSqlInterpolated($"""
                SELECT * FROM alert_deliveries
                WHERE status = 'Pending' AND next_attempt_at <= {now}
                ORDER BY next_attempt_at
                LIMIT 20
                FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            if (due.Count == 0)
            {
                await transaction.DisposeAsync();
                return 0;
            }
        }

        await using (transaction)
        {
            var channelIds = due.Select(d => d.ChannelId).Distinct().ToList();
            var channels = await db.Channels.AsNoTracking().Where(c => channelIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

            foreach (var delivery in due)
            {
                var channel = channels[delivery.ChannelId];
                var stopwatch = Stopwatch.StartNew();
                var result = await sender.SendAsync(new OutgoingAlert(delivery.Id, channel.Kind, channel.Url, channel.SigningSecret, delivery.Event, delivery.Payload), ct);

                if (result.Success)
                {
                    delivery.MarkSent(result.StatusCode ?? 200, clock.GetUtcNow());
                    logger.LogInformation("Alerte {Event} envoyée à {Channel} ({Latency} ms après le fait, envoi {Elapsed} ms)",
                        delivery.Event, channel.Name, delivery.LatencyMilliseconds, stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    delivery.MarkFailed(result.StatusCode, result.Error ?? "Échec", clock.GetUtcNow());
                    logger.LogWarning("Alerte {Event} vers {Channel} en échec (tentative {Attempts}) : {Error}",
                        delivery.Event, channel.Name, delivery.Attempts, result.Error);
                }
            }

            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            // Après validation : une mesure ne doit pas compter un envoi dont l'état n'a pas été enregistré.
            foreach (var delivery in due) AlertingMetrics.RecordAttempt(delivery, channels[delivery.ChannelId].Kind);
            return due.Count;
        }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddAlertingInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<AlertingDb>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Database"))
            .UseSnakeCaseNamingConvention());

        services.AddOptions<TargetPolicyOptions>().BindConfiguration(TargetPolicyOptions.SectionName);
        services.AddHttpClient(HttpAlertSender.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider => SafeHttpHandler.Create(provider.GetRequiredService<IOptions<TargetPolicyOptions>>().Value));

        services.AddSingleton<DeliverySignal>();
        services.AddSingleton<IAlertSender, HttpAlertSender>();
        services.AddScoped<IAlertChannelRepository, AlertChannelRepository>();
        services.AddScoped<IDeliveryRepository, DeliveryRepository>();
        services.AddScoped<AlertChannelService>();
        services.AddHostedService<DeliveryDispatcher>();

        services.AddDevPulseMessaging("alerting-service")
            .AddConsumer<AlertingDb>(EventTopology.AlertingIncidentEvents, c => c
                .Handle<IncidentCreatedV1, IncidentEventsHandler>()
                .Handle<IncidentAcknowledgedV1, IncidentEventsHandler>()
                .Handle<IncidentResolvedV1, IncidentEventsHandler>()
                .Handle<IncidentAnalyzedV1, IncidentEventsHandler>())
            .AddConsumer<AlertingDb>(EventTopology.AlertingServiceStatus, c => c
                .Handle<ServiceStatusChangedV1, ServiceStatusHandler>());
        return services;
    }
}
