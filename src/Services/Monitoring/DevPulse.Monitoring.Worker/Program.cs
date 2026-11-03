using DevPulse.Messaging;
using DevPulse.Monitoring.Infrastructure;
using DevPulse.Monitoring.Worker;
using DevPulse.Observability;

var builder = Host.CreateApplicationBuilder(args);

builder.AddDevPulseObservability("monitoring-worker");
builder.Services.AddOptions<WorkerOptions>().BindConfiguration(WorkerOptions.SectionName);
builder.Services.AddMonitoringPersistence();
builder.Services.AddMonitoringProbing();
builder.Services.AddDevPulseMessaging("monitoring-worker").AddOutbox<MonitoringDb>();
builder.Services.AddHostedService<RegistrySyncService>();
builder.Services.AddHostedService<CheckSchedulerService>();
builder.Services.AddHostedService<RetentionService>();

builder.Build().Run();
