using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vnc.Core;

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
            .ConfigureServices(services =>
            {
                services.AddSingleton(serviceProvider => new ServiceConfigurationStore());
                services.AddHostedService<ServiceWorker>();
            });
}

internal sealed class ServiceWorker : BackgroundService
{
    internal const string VncPasswordEnvironmentVariable = "LUCYVNC_VNC_PASSWORD";

    private readonly ILogger<ServiceWorker> _logger;
    private readonly ServiceConfigurationStore _configurationStore;
    private VncServerLifecycle? _lifecycle;

    public ServiceWorker(ILogger<ServiceWorker> logger, ServiceConfigurationStore configurationStore)
    {
        _logger = logger;
        _configurationStore = configurationStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        VncServerLifecycle? lifecycle = null;
        try
        {
            var configuration = _configurationStore.LoadOrCreate();
            var diagnostic = ServiceSharingController.Evaluate(configuration);
            _logger.LogInformation(
                "LucyVNC service state: {State}. {Diagnostic}",
                diagnostic.State,
                diagnostic.Message);

            if (diagnostic.State == ServiceSharingState.Disabled)
            {
                return;
            }

            var password = ResolveVncPassword();
            if (password is null)
            {
                _logger.LogWarning(
                    "LucyVNC remote sharing is enabled but the {EnvironmentVariable} variable is missing or is not an eight-character ASCII VNC password. The RFB listener was not opened.",
                    VncPasswordEnvironmentVariable);
                return;
            }

            var agentClient = new LucyAgentClient(
                pipeName: "LucyVNCAgent",
                agentSid: Environment.GetEnvironmentVariable("LUCYVNC_AGENT_SID") ?? string.Empty);
            var agentConnected = await agentClient.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            if (!agentConnected)
            {
                _logger.LogWarning(
                    "The LucyServer worker could not reach the LucySessionAgent. The RFB listener was not opened.");
                return;
            }

            lifecycle = new VncServerLifecycle(
                CreateConnectionSettings(configuration, password),
                new AgentScreenCapture(agentClient),
                new AgentRemoteInput(agentClient));
            lifecycle.StatusChanged += (_, message) => _logger.LogInformation("LucyVNC RFB listener status: {Message}", message);
            await lifecycle.StartAsync(CancellationToken.None).ConfigureAwait(false);
            _lifecycle = lifecycle;
            _logger.LogInformation(
                "LucyVNC RFB listener started on {BindAddress}:{Port}.",
                configuration.BindAddress,
                lifecycle.BoundPort);
        }
        catch (Exception exception) when (exception is IOException
                                         or UnauthorizedAccessException
                                         or System.Security.SecurityException
                                         or System.Text.Json.JsonException
                                         or InvalidOperationException
                                         or ArgumentException
                                         or ObjectDisposedException
                                         or SocketException)
        {
            _logger.LogError(
                "LucyVNC service could not start the authenticated RFB listener: {Reason}",
                exception.Message);
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            var runningLifecycle = _lifecycle;
            _lifecycle = null;
            if (runningLifecycle is not null)
            {
                await runningLifecycle.StopAsync(CancellationToken.None).ConfigureAwait(false);
                runningLifecycle.Dispose();
            }
        }
    }

    internal static string? ResolveVncPassword()
    {
        var password = Environment.GetEnvironmentVariable(VncPasswordEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return password.Length == 8 && password.All(character => character <= 0x7f) ? password : null;
    }

    private static ConnectionSettings CreateConnectionSettings(ServiceConfiguration configuration, string password)
    {
        return new ConnectionSettings
        {
            BindAddress = configuration.BindAddress,
            Port = configuration.Port,
            Password = password,
            MaxSessions = 1,
            Width = 0,
            Height = 0,
        };
    }
}
