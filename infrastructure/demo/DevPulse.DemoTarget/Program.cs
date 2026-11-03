// Fausse application à surveiller, pour la démonstration et le test de bout en bout.
// POST /mode/ok, /mode/slow ou /mode/down change ce que répond GET /health.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var mode = "ok";

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

app.Run();
