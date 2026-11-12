using AgentPlatform.Scheduler;

// Консольный сервис-планировщик: cron-запуски агентов без входящего сообщения.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<SchedulerWorker>();

var host = builder.Build();
host.Run();

