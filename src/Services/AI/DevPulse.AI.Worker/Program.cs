using DevPulse.AI.Application;
using DevPulse.AI.Infrastructure;
using DevPulse.Contracts.Events.Incident;
using DevPulse.Contracts.Messaging;
using DevPulse.Messaging;
using DevPulse.Observability;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.AddDevPulseObservability("ai-worker");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<AiOptions>()
    .BindConfiguration(AiOptions.SectionName)
    .Validate(o => o.Provider is RuleBasedProvider.ProviderName or OpenAiCompatibleProvider.ProviderName, "Ai:Provider vaut Rules ou Llm.")
    .Validate(o => o.Provider != OpenAiCompatibleProvider.ProviderName || (Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _) && o.Model.Length > 0),
        "Ai:BaseUrl et Ai:Model sont obligatoires avec Ai:Provider=Llm.")
    .ValidateOnStart();

builder.Services.AddSingleton<RuleBasedProvider>();
builder.Services.AddHttpClient<OpenAiCompatibleProvider>((provider, client) =>
{
    var options = provider.GetRequiredService<IOptions<AiOptions>>().Value;
    if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl)) client.BaseAddress = baseUrl;
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});
builder.Services.AddScoped<IAIAnalysisProvider>(provider =>
    provider.GetRequiredService<IOptions<AiOptions>>().Value.Provider == OpenAiCompatibleProvider.ProviderName
        ? provider.GetRequiredService<OpenAiCompatibleProvider>()
        : provider.GetRequiredService<RuleBasedProvider>());
builder.Services.AddScoped<AnalysisService>();

builder.Services.AddDevPulseMessaging("ai-worker")
    .AddConsumer(EventTopology.AiIncidentCreated, c => c.Handle<IncidentCreatedV1, IncidentCreatedHandler>());

builder.Build().Run();
