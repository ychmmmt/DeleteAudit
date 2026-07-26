using DeleteAudit.Collector;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<DesignOnlyWorker>();

await builder.Build().RunAsync();
