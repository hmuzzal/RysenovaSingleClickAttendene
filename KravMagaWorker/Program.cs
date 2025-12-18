using KravMagaWorker;


var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();

        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        logging.AddFilter("System.Net.Http", LogLevel.Warning);
        logging.AddFilter("KravMagaWorker.Worker", LogLevel.Warning);
    })
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHttpClient();
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();