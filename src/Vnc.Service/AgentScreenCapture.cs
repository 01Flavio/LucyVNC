using Vnc.Core;
using Vnc.Platform.Windows;

namespace Vnc.Service;

/// <summary>
/// IScreenCapture adapter that requests frames from the LucySessionAgent over authenticated IPC.
/// </summary>
public sealed class AgentScreenCapture : IScreenCapture
{
    private readonly LucyAgentClient _client;

    public AgentScreenCapture(LucyAgentClient client)
    {
        _client = client;
    }

    public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
    {
        return _client.RequestFrameAsync(width, height, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
