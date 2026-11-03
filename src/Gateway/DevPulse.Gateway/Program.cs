using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using DevPulse.Observability;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddDevPulseObservability("api-gateway");
builder.Services.AddDevPulseAuthentication();
builder.Services.AddHealthChecks();
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Limite par adresse, en mémoire pour l'instant : elle passera sur Redis quand la passerelle aura plusieurs instances.
var authPermitLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitPerMinute", 20);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermitLimit, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

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
