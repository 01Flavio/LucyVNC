using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vnc.Service;

internal static class Program
{
    public static Task Main(string[] args) =>
        ServiceHostBuilder.Create(args)
            .Build()
            .RunAsync();
}

public static class ServiceHostBuilder
{
    public static IHostBuilder Create(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService(options => options.ServiceName = WindowsServiceStatusProbe.ServiceName)
            .ConfigureServices(services => services.AddHostedService<ServiceWorker>());
}

internal sealed class ServiceWorker : BackgroundService
{
    private readonly ILogger<ServiceWorker> _logger;
    private readonly ServiceConfigurationStore _configurationStore = new();

    public ServiceWorker(ILogger<ServiceWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var configuration = _configurationStore.LoadOrCreate();
            var diagnostic = ServiceSharingController.Evaluate(configuration);
            _logger.LogInformation(
                "LucyVNC service state: {State}. {Diagnostic}",
                diagnostic.State,
                diagnostic.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
        {
            _logger.LogError("LucyVNC service refused to start sharing because configuration could not be safely loaded: {Reason}", exception.Message);
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
    }
}
