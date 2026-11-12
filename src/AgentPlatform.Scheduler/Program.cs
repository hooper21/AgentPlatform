using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.DependencyInjection;
using AgentPlatform.Scheduler;
using AgentPlatform.Scheduler.Composition;

// Консольный сервис-планировщик: cron-запуски агентов без входящего сообщения.
// Управление расписаниями — через Controller API (scheduled_runs).
var builder = Host.CreateApplicationBuilder(args);

// PostgreSQL: scheduled_runs, сессии, сообщения, чекпоинты и конфигурации агентов.
builder.Services.AddPlatformInfrastructure(PlatformInfrastructureOptions.FromEnvironment());

// Порты Runtime, LLM Gateway и cron-исполнитель.
builder.Services.AddSchedulerRuntime(builder.Configuration);

builder.Services.AddHostedService<SchedulerWorker>();

var host = builder.Build();
host.Run();

