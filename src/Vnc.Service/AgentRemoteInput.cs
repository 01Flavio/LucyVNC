using Vnc.Platform.Windows;

namespace Vnc.Service;

/// <summary>
/// IRemoteInput adapter that forwards keyboard and pointer events to the LucySessionAgent over authenticated IPC.
/// </summary>
public sealed class AgentRemoteInput : IRemoteInput
{
    private readonly LucyAgentClient _client;

    public AgentRemoteInput(LucyAgentClient client)
    {
        _client = client;
    }

    public void SetFramebufferSize(ushort width, ushort height)
    {
    }

    public void ApplyKeyEvent(uint keySym, bool pressed)
    {
        _client.SendInputAsync(keySym, pressed, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void ApplyPointerEvent(ushort x, ushort y, byte buttonMask)
    {
        _client.SendPointerAsync(x, y, buttonMask, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void ReleaseAll()
    {
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
