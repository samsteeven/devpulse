using DevPulse.Identity.Api;
using DevPulse.Identity.Infrastructure;
using DevPulse.Observability;
using DevPulse.Security.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddDevPulseObservability("identity-service");
builder.Services.AddControllers(options => options.Filters.Add<BusinessErrorFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDevPulseAuthentication();
builder.Services.AddOptions<JwtOptions>()
    .Validate(o => !string.IsNullOrWhiteSpace(o.PrivateKey), "Jwt:PrivateKey est obligatoire pour Identity.")
    .ValidateOnStart();
builder.Services.AddIdentityInfrastructure();
builder.Services.AddOptions<DevPulse.Identity.Api.Controllers.AuthCookieOptions>().BindConfiguration(DevPulse.Identity.Api.Controllers.AuthCookieOptions.SectionName);
builder.Services.AddHealthChecks().AddDbContextCheck<IdentityDb>("database", tags: ["ready"]);

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
    await scope.ServiceProvider.GetRequiredService<IdentityDb>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

app.Run();

public partial class Program;
