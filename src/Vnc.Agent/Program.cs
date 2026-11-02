using System.Security.Principal;
using Vnc.Agent;
using Vnc.Platform.Windows;

namespace Vnc.Agent;

internal static class Program
{
    private const string AuthorizedSidEnvironmentVariable = "LUCYVNC_AGENT_AUTHORIZED_SID";

    private static async Task Main()
    {
        var configuredSid = Environment.GetEnvironmentVariable(AuthorizedSidEnvironmentVariable);
        var agent = new LucySessionAgent(
            new GdiScreenCapture(),
            new SendInputController(),
            string.IsNullOrWhiteSpace(configuredSid) ? GetInteractiveUserSid() : configuredSid);
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
    }

    private static string GetInteractiveUserSid()
    {
        return WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The LucySessionAgent requires an interactive Windows identity.");
    }
}
