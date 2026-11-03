using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using DevPulse.Agent;
using Microsoft.Extensions.Options;

// Agent DevPulse : envoie un battement et les mesures de la machine à intervalle régulier.
//   DevPulse__Url=https://devpulse.example.com  DevPulse__AgentKey=dpa_…  dotnet DevPulse.Agent.dll
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<AgentOptions>()
    .BindConfiguration(AgentOptions.SectionName)
    .Validate(o => Uri.TryCreate(o.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || o.AllowInsecureHttp),
        "DevPulse:Url doit être une URL https (AllowInsecureHttp=true seulement en local).")
    .Validate(o => o.AgentKey.StartsWith("dpa_", StringComparison.Ordinal), "DevPulse:AgentKey est obligatoire (clé affichée à la création de l'agent).")
    .Validate(o => o.IntervalSeconds is >= 10 and <= 3600, "DevPulse:IntervalSeconds doit être compris entre 10 et 3600.")
    .ValidateOnStart();

builder.Services.AddSingleton<ISystemMetrics>(_ => OperatingSystem.IsWindows() ? new WindowsMetrics() : new LinuxMetrics());
builder.Services.AddHttpClient<AgentWorker>((provider, client) =>
{
    var options = provider.GetRequiredService<IOptions<AgentOptions>>().Value;
    client.BaseAddress = new Uri(options.Url.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Agent", options.AgentKey);
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"DevPulse-Agent/{AgentWorker.Version}");
});
builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentWorker>());

builder.Build().Run();

namespace DevPulse.Agent
{
    public sealed class AgentOptions
    {
        public const string SectionName = "DevPulse";

        /// <summary>Adresse de la passerelle DevPulse.</summary>
        public string Url { get; set; } = "";
        public string AgentKey { get; set; } = "";
        public int IntervalSeconds { get; set; } = 30;

        /// <summary>Accepte http : uniquement pour la démonstration locale, la clé passerait sinon en clair.</summary>
        public bool AllowInsecureHttp { get; set; }
    }

    public sealed class AgentWorker(HttpClient http, ISystemMetrics metrics, IOptions<AgentOptions> options, IHostApplicationLifetime lifetime, ILogger<AgentWorker> logger)
        : BackgroundService
    {
        public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromSeconds(options.Value.IntervalSeconds);
            metrics.Read(); // référence pour la mesure du processeur
            logger.LogInformation("Agent démarré : un battement toutes les {Interval} s vers {Url}", interval.TotalSeconds, http.BaseAddress);

            using var timer = new PeriodicTimer(interval);
            var failures = 0;
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var snapshot = metrics.Read();
                    using var response = await http.PostAsJsonAsync("api/agents/ingest", new
                    {
                        hostname = Environment.MachineName,
                        operatingSystem = RuntimeInformation.OSDescription,
                        agentVersion = Version,
                        intervalSeconds = (int)interval.TotalSeconds,
                        cpuPercent = Math.Round(snapshot.CpuPercent, 1),
                        memoryPercent = Math.Round(snapshot.MemoryPercent, 1),
                        diskPercent = Math.Round(snapshot.DiskPercent, 1),
                        uptimeSeconds = snapshot.UptimeSeconds,
                    }, stoppingToken);

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        // Clé révoquée ou fausse : insister ne servirait à rien, l'agent s'arrête.
                        logger.LogCritical("Clé refusée par DevPulse (révoquée ou invalide) : arrêt de l'agent");
                        lifetime.StopApplication();
                        return;
                    }

                    response.EnsureSuccessStatusCode();
                    if (failures > 0) logger.LogInformation("Liaison rétablie après {Failures} échec(s)", failures);
                    failures = 0;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    // Pas de rattrapage : le battement suivant suffit, et DevPulse tolère trois battements manqués.
                    failures++;
                    logger.LogWarning("Battement non transmis ({Failures} de suite) : {Error}", failures, e.Message);
                }
            }
        }
    }
}
