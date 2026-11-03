using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using DevPulse.Observability;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddDevPulseObservability("api-gateway");
builder.Services.AddDevPulseAuthentication();
builder.Services.AddHealthChecks();
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Derrière un répartiteur (ALB), l'adresse vue est la sienne : X-Forwarded-For donne celle du client, mais seulement
// si le réseau du répartiteur est déclaré de confiance. Sans configuration, l'en-tête est ignoré (impossible à usurper).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:TrustedNetworks").Get<string[]>() ?? [])
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
});

// Limite par adresse, en mémoire : elle vaut pour une instance de passerelle. Avec plusieurs instances, la limite
// effective est multipliée d'autant (choix assumé, voir README).
var authPermitLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitPerMinute", 20);
var agentPermitLimit = builder.Configuration.GetValue("RateLimiting:AgentPermitPerMinute", 120);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermitLimit, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("agent", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = agentPermitLimit, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseForwardedHeaders();
app.Use((context, next) =>
{
    // Une valeur reçue n'est reprise que si elle a la forme attendue : elle finit dans les journaux de tous les services.
    var received = context.Request.Headers["X-Correlation-Id"].ToString();
    var correlationId = CorrelationId.IsValid(received) ? received : Guid.NewGuid().ToString("N");
    context.Request.Headers["X-Correlation-Id"] = correlationId;

    var headers = context.Response.Headers;
    headers["X-Correlation-Id"] = correlationId;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    return next();
});

app.UseRateLimiter();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapReverseProxy();

app.Run();

internal static partial class CorrelationId
{
    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex Format();

    public static bool IsValid(string value) => Format().IsMatch(value);
}

public partial class Program;
