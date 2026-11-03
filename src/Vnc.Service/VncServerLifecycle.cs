using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Server;

namespace Vnc.Service;

/// <summary>
/// Owns the lifecycle of the authenticated RFB listener started by the LucyServer worker.
/// The worker starts this when remote sharing is explicitly enabled; the listener itself is
/// the shared <see cref="VncServer"/> RFB implementation and therefore requires VNC Authentication.
/// </summary>
public sealed class VncServerLifecycle : IDisposable
{
    private readonly ConnectionSettings _settings;
    private readonly IScreenCapture? _screenCapture;
    private readonly IRemoteInput? _remoteInput;
    private VncServer? _server;
    private bool _disposed;

    public VncServerLifecycle(ConnectionSettings settings, IScreenCapture? screenCapture = null, IRemoteInput? remoteInput = null)
    {
        _settings = settings;
        _screenCapture = screenCapture;
        _remoteInput = remoteInput;
    }

    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _server is { IsRunning: true };

    public int BoundPort => _server?.BoundPort ?? _settings.Port;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            throw new InvalidOperationException("The LucyVNC RFB listener is already running.");
        }

        var server = new VncServer(_settings, _screenCapture, _remoteInput);
        server.StatusChanged += (_, message) => StatusChanged?.Invoke(this, message);
        server.Start();
        _server = server;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var server = _server;
        if (server is null)
        {
            return;
        }

        _server = null;
        try
        {
            await server.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            server.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var server = _server;
        _server = null;
        server?.Dispose();
    }
}
