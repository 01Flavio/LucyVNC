using System.Security.Principal;
using System.Diagnostics;
using Vnc.Agent;
using Vnc.Platform.Windows;

namespace Vnc.Agent;

internal static class Program
{
    private const string AuthorizedSidEnvironmentVariable = "LUCYVNC_AGENT_AUTHORIZED_SID";

    private static async Task Main(string[] args)
    {
        WindowsDpiAwareness.InitializePerMonitorV2();
        EnsureInteractiveSession();
        if (args.Length > 0 && string.Equals(args[0], "--supervise", StringComparison.OrdinalIgnoreCase))
        {
            await RunSupervisorAsync(args);
            return;
        }

        var configuredSid = args.Length switch
        {
            0 => Environment.GetEnvironmentVariable(AuthorizedSidEnvironmentVariable),
            2 when string.Equals(args[0], "--authorized-worker-sid", StringComparison.OrdinalIgnoreCase) => args[1],
            _ => throw new ArgumentException("Expected no arguments or --authorized-worker-sid followed by a Windows SID."),
        };
        var interactiveSid = GetInteractiveUserSid();
        using var singleInstance = new Mutex(initiallyOwned: true, $@"Local\LucyVNC.Agent.{interactiveSid}", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        using var agent = new LucySessionAgent(
            new GdiScreenCapture(),
            new SendInputController(),
            string.IsNullOrWhiteSpace(configuredSid) ? interactiveSid : configuredSid);
        agent.StatusChanged += (_, message) => Console.WriteLine($"AGENT STATUS: {message}");
        using var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            cancellationTokenSource.Cancel();
        };

        await agent.StartAsync(cancellationTokenSource.Token);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Shut the agent down gracefully when a cancellation is requested.
        }

        await agent.StopAsync();
    }

    private static async Task RunSupervisorAsync(string[] args)
    {
        if (args.Length != 3 ||
            !string.Equals(args[1], "--authorized-worker-sid", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Supervisor mode requires --authorized-worker-sid followed by a Windows SID.");
        }

        var interactiveSid = GetInteractiveUserSid();
        _ = new SecurityIdentifier(args[2]);
        using var mutex = new Mutex(initiallyOwned: true, $@"Local\LucyVNC.AgentSupervisor.{interactiveSid}", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve the LucyVNC Agent executable for supervision.");
        var consecutiveFailures = 0;
        while (true)
        {
            using var agent = Process.Start(CreateAgentStartInfo(executable, args[2]))
                ?? throw new InvalidOperationException("Could not start the LucyVNC interactive Agent.");
            var startedAt = DateTimeOffset.UtcNow;
            await agent.WaitForExitAsync();
            if (agent.ExitCode == 0)
            {
                return;
            }

            if (DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMinutes(5))
            {
                consecutiveFailures = 0;
            }

            consecutiveFailures++;
            await Task.Delay(AgentRestartBackoff.GetDelay(consecutiveFailures));
        }
    }

    private static void EnsureInteractiveSession()
    {
        using var process = Process.GetCurrentProcess();
        if (!Environment.UserInteractive || process.SessionId == 0)
        {
            throw new InvalidOperationException("LucyVNC Agent must run in an interactive user session, not Session 0.");
        }
    }

    private static ProcessStartInfo CreateAgentStartInfo(string executable, string authorizedWorkerSid)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        startInfo.ArgumentList.Add("--authorized-worker-sid");
        startInfo.ArgumentList.Add(authorizedWorkerSid);
        return startInfo;
    }

    private static string GetInteractiveUserSid()
    {
        return WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The LucySessionAgent requires an interactive Windows identity.");
    }
}
