using System.Text.Json.Serialization;
using DevPulse.Alerting.Api;
using DevPulse.Alerting.Application;
using DevPulse.Alerting.Infrastructure;
using DevPulse.Observability;
using DevPulse.Security.Authentication;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddDevPulseObservability("alerting-service");
builder.Services.AddControllers(options => options.Filters.Add<BusinessErrorFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDevPulseAuthentication();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAlertingInfrastructure();
builder.Services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();
builder.Services.AddHealthChecks().AddDbContextCheck<AlertingDb>("database", tags: ["ready"]);

// Avec plusieurs instances, un message émis par l'une doit atteindre les navigateurs connectés aux autres : Redis relaie.
var signalR = builder.Services.AddSignalR();
var redis = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrWhiteSpace(redis))
    signalR.AddStackExchangeRedis(redis, options => options.Configuration.ChannelPrefix = RedisChannel.Literal("devpulse"));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AlertingDb>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<NotificationsHub>("/hubs/notifications");
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

app.Run();

public partial class Program;
