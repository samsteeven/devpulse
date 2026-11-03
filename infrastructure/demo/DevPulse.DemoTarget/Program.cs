using System.Collections.Concurrent;

// Fausse application à surveiller, pour la démonstration et le test de bout en bout.
// POST /mode/ok, /mode/slow ou /mode/down change ce que répond GET /health.
// POST /webhooks/{boite} reçoit des alertes et GET /webhooks/{boite} les relit, avec leur heure de réception :
// de quoi mesurer le délai de notification de bout en bout.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var mode = "ok";
var inboxes = new ConcurrentDictionary<string, ConcurrentQueue<ReceivedWebhook>>();

app.MapGet("/health", async () =>
{
    switch (Volatile.Read(ref mode))
    {
        case "down":
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        case "slow":
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            return Results.Ok(new { status = "slow" });
        default:
            return Results.Ok(new { status = "ok" });
    }
});

app.MapPost("/mode/{value:regex(^(ok|slow|down)$)}", (string value) =>
{
    Volatile.Write(ref mode, value);
    return Results.Ok(new { mode = value });
});

app.MapPost("/webhooks/{inbox:regex(^[a-z0-9-]{{1,64}}$)}", async (string inbox, HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    var queue = inboxes.GetOrAdd(inbox, _ => new ConcurrentQueue<ReceivedWebhook>());
    if (queue.Count < 100)
        queue.Enqueue(new ReceivedWebhook(DateTimeOffset.UtcNow, request.Headers["X-DevPulse-Event"].ToString(),
            request.Headers["X-DevPulse-Signature"].ToString(), request.Headers["X-DevPulse-Timestamp"].ToString(), body));
    return Results.Ok();
});

app.MapGet("/webhooks/{inbox}", (string inbox) =>
    Results.Ok(inboxes.TryGetValue(inbox, out var queue) ? queue.ToArray() : []));

app.Run();

internal sealed record ReceivedWebhook(DateTimeOffset ReceivedAt, string Event, string Signature, string Timestamp, string Body);
