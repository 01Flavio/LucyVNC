using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vnc.Core;

namespace Vnc.Service;

internal static class Program
{
    public static Task Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--configure", StringComparison.OrdinalIgnoreCase))
        {
            var serviceRestarted = ServiceConfigurationProvisioner.ApplyProtectedRequest(args[1]);
            Console.WriteLine(serviceRestarted
                ? "LucyVNC Server configuration was saved securely and the running service was restarted."
                : "LucyVNC Server configuration was saved securely. Start the service to apply it.");
            return Task.CompletedTask;
        }

        return args.Contains(ServiceHostBuilder.ConsoleModeArgument, StringComparer.OrdinalIgnoreCase)
            ? RunConsoleAsync(args)
            : ServiceHostBuilder.Create(args).Build().RunAsync();
    }

    private static async Task RunConsoleAsync(string[] args)
    {
        using var host = ServiceHostBuilder.Create(args).Build();
        await host.StartAsync().ConfigureAwait(false);
        Console.WriteLine("READY:WORKER");
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line &&
               !string.Equals(line, "STOP", StringComparison.Ordinal))
        {
        }

        Console.WriteLine("STOPPING:WORKER");
        await host.StopAsync().ConfigureAwait(false);
        Console.WriteLine("STOPPED:WORKER");
    }
}

public static class ServiceHostBuilder
{
    internal const string ConfigurationPathEnvironmentVariable = "LUCYVNC_SERVICE_CONFIG";
    internal const string AgentSidEnvironmentVariable = "LUCYVNC_AGENT_SID";
    internal const string PipeNameEnvironmentVariable = "LUCYVNC_AGENT_PIPE";
    internal const string ConsoleModeArgument = "--console";

    public static IHostBuilder Create(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args);
        if (!args.Contains(ConsoleModeArgument, StringComparer.OrdinalIgnoreCase))
        {
            builder.UseWindowsService(options => options.ServiceName = WindowsServiceStatusProbe.ServiceName);
        }

        return builder.ConfigureServices(services =>
            {
                services.AddSingleton(_ => new ServiceConfigurationStore(
                    Environment.GetEnvironmentVariable(ConfigurationPathEnvironmentVariable)));
                services.AddHostedService<ServiceWorker>();
            });
    }
}

internal sealed class ServiceWorker : BackgroundService
{
    internal const string VncPasswordEnvironmentVariable = "LUCYVNC_VNC_PASSWORD";

    private readonly ILogger<ServiceWorker> _logger;
    private readonly ServiceConfigurationStore _configurationStore;
    private readonly ServiceRuntimeStatusStore _runtimeStatusStore;
    private VncServerLifecycle? _lifecycle;
    private ServiceConfiguration? _configuration;

    public ServiceWorker(ILogger<ServiceWorker> logger, ServiceConfigurationStore configurationStore)
    {
        _logger = logger;
        _configurationStore = configurationStore;
        _runtimeStatusStore = new ServiceRuntimeStatusStore(configurationStore.RuntimeStatusPath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ServiceConfiguration configuration;
        try
        {
            configuration = _configurationStore.LoadOrCreate();
            _configuration = configuration;
            var diagnostic = ServiceSharingController.Evaluate(configuration);
            _logger.LogInformation(
                "LucyVNC service state: {State}. {Diagnostic}",
                diagnostic.State,
                diagnostic.Message);
        }
        catch (Exception exception) when (exception is IOException
                                         or UnauthorizedAccessException
                                         or System.Security.SecurityException
                                         or System.Text.Json.JsonException
                                         or InvalidOperationException)
        {
            WriteRuntimeStatus("ConfigurationError", false, false, null, null, exception.Message);
            _logger.LogError(
                "LucyVNC service could not load a safe configuration: {Reason}",
                exception.Message);
            return;
        }

        if (ServiceSharingController.Evaluate(configuration).State == ServiceSharingState.Disabled)
        {
            WriteRuntimeStatus("SharingDisabled", false, false, configuration.BindAddress, configuration.Port, "Persistent sharing is disabled in the protected configuration.");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        string? password;
        try
        {
            password = new ServiceCredentialStore(_configurationStore.CredentialPath).Load();
        }
        catch (FileNotFoundException)
        {
            password = ResolveVncPassword();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException or InvalidDataException)
        {
            WriteRuntimeStatus("CredentialUnavailable", false, false, configuration.BindAddress, configuration.Port, exception.Message);
            _logger.LogError("LucyVNC service credential could not be loaded: {Reason}. No listener was opened.", exception.Message);
            return;
        }

        if (password is null)
        {
            WriteRuntimeStatus("CredentialUnavailable", false, false, configuration.BindAddress, configuration.Port, "No valid protected VNC password is configured.");
            _logger.LogWarning(
                "LucyVNC remote sharing is enabled but no valid protected VNC password is configured. No listener was opened.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            LucyAgentClient? agentClient = null;
            VncServerLifecycle? lifecycle = null;
            var agentAuthenticated = false;
            WriteRuntimeStatus("WaitingForAgent", false, false, configuration.BindAddress, configuration.Port, "Waiting for the configured interactive-session Agent.");
            try
            {
                var agentSid = string.IsNullOrWhiteSpace(configuration.AgentSid)
                    ? Environment.GetEnvironmentVariable(ServiceHostBuilder.AgentSidEnvironmentVariable)
                    : configuration.AgentSid;
                if (string.IsNullOrWhiteSpace(agentSid))
                {
                    throw new InvalidOperationException(
                        $"{ServiceHostBuilder.AgentSidEnvironmentVariable} must identify the expected interactive-agent Windows SID.");
                }

                _ = new SecurityIdentifier(agentSid);
                agentClient = new LucyAgentClient(
                    Environment.GetEnvironmentVariable(ServiceHostBuilder.PipeNameEnvironmentVariable) ?? "LucyVNCAgent",
                    agentSid);
                await agentClient.ConnectAsync(stoppingToken).ConfigureAwait(false);
                agentAuthenticated = true;
                WriteRuntimeStatus("AgentAuthenticated", true, false, configuration.BindAddress, configuration.Port, "Authenticated interactive Agent; starting RFB listener.");

                lifecycle = new VncServerLifecycle(
                    CreateConnectionSettings(configuration, password),
                    new AgentScreenCapture(agentClient),
                    new AgentRemoteInput(agentClient));
                lifecycle.StatusChanged += (_, message) => _logger.LogInformation("LucyVNC RFB listener status: {Message}", message);
                await lifecycle.StartAsync(stoppingToken).ConfigureAwait(false);
                _lifecycle = lifecycle;
                _logger.LogInformation(
                    "LucyVNC authenticated RFB listener is ready on {BindAddress}:{Port}; desktop capture and input are provided by the verified interactive agent.",
                    configuration.BindAddress,
                    lifecycle.BoundPort);
                WriteRuntimeStatus("Ready", true, true, configuration.BindAddress, lifecycle.BoundPort, "RFB listener is active and the authenticated interactive Agent is available.");

                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    if (!await agentClient.SendHeartbeatAsync(stoppingToken).ConfigureAwait(false))
                    {
                        WriteRuntimeStatus("DesktopUnavailable", true, false, configuration.BindAddress, lifecycle.BoundPort, "Interactive desktop is locked or unavailable; the RFB listener is closing.");
                        throw new InvalidOperationException("The interactive desktop is locked or unavailable. The RFB listener will be closed.");
                    }

                    WriteRuntimeStatus("Ready", true, true, configuration.BindAddress, lifecycle.BoundPort, "RFB listener is active and the authenticated interactive Agent is available.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException exception)
            {
                _logger.LogWarning(
                    "LucyVNC sharing stopped after an IPC operation timed out: {Reason}. The listener will remain closed until the agent reconnects.",
                    exception.Message);
            }
            catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or System.Security.SecurityException
                                             or System.Text.Json.JsonException
                                             or InvalidOperationException
                                             or ArgumentException
                                             or ObjectDisposedException
                                             or SocketException
                                             or Win32Exception)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    var state = exception.Message.Contains("desktop", StringComparison.OrdinalIgnoreCase)
                        ? "DesktopUnavailable"
                        : "AgentUnavailable";
                    WriteRuntimeStatus(state, agentAuthenticated, false, configuration.BindAddress, configuration.Port, exception.Message);
                }

                _logger.LogWarning(
                    "LucyVNC sharing stopped because the authenticated interactive agent is unavailable: {Reason}. The listener will remain closed until the agent reconnects.",
                    exception.Message);
            }
            finally
            {
                _lifecycle = null;
                if (lifecycle is not null)
                {
                    try
                    {
                        await lifecycle.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        lifecycle.Dispose();
                    }
                }

                agentClient?.Dispose();
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            }
        }

        WriteRuntimeStatus("Stopped", false, false, configuration.BindAddress, configuration.Port, "LucyVNC Server Worker stopped.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        var configuration = _configuration;
        WriteRuntimeStatus(
            "Stopped",
            false,
            false,
            configuration?.BindAddress,
            configuration?.Port,
            "LucyVNC Server Worker stopped.");
    }

    private void WriteRuntimeStatus(
        string state,
        bool agentAuthenticated,
        bool listenerActive,
        string? bindAddress,
        int? port,
        string detail)
    {
        try
        {
            _runtimeStatusStore.Write(
                new ServiceRuntimeStatus(
                    state,
                    agentAuthenticated,
                    listenerActive,
                    bindAddress,
                    port,
                    DateTimeOffset.UtcNow,
                    detail),
                _configuration?.AgentSid);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
        {
            _logger.LogWarning("Could not publish LucyVNC Server runtime status: {Reason}", exception.Message);
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
            MaxFramesPerSecond = 10,
            FramebufferLimitBytes = 16 * 1024 * 1024,
            Width = 0,
            Height = 0,
        };
    }
}
