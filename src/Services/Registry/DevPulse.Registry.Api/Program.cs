using System.Text.Json.Serialization;
using DevPulse.Observability;
using DevPulse.Registry.Api;
using DevPulse.Registry.Infrastructure;
using DevPulse.Security.Authentication;
using DevPulse.Security.Ssrf;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddDevPulseObservability("registry-service");
builder.Services.AddControllers(options => options.Filters.Add<BusinessErrorFilter>())
    // allowIntegerValues : sinon « environment: 99 » passerait et finirait tel quel en base.
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDevPulseAuthentication();
builder.Services.AddOptions<InternalApiOptions>().BindConfiguration(InternalApiOptions.SectionName);
builder.Services.AddOptions<TargetPolicyOptions>().BindConfiguration(TargetPolicyOptions.SectionName);
builder.Services.AddRegistryInfrastructure();
builder.Services.AddHealthChecks().AddDbContextCheck<RegistryDb>("database", tags: ["ready"]);

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
    await scope.ServiceProvider.GetRequiredService<RegistryDb>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

app.Run();

public partial class Program;
