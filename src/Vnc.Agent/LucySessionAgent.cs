using System.IO.Pipes;
using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Vnc.Core;
using Vnc.Platform.Windows;

namespace Vnc.Agent;

public enum AgentIpcMessageType
{
    Hello = 1,
    FrameRequest = 2,
    FrameResponse = 3,
    InputEvent = 4,
    InputAck = 5,
    Heartbeat = 6,
    HeartbeatAck = 7,
    Error = 8,
}

/// <summary>
/// LucySessionAgent: named-pipe listener that owns interactive-session capture and remote input.
/// The Worker Service connects here as the authorized peer; the agent validates the peer identity,
/// serves framebuffer requests through <see cref="IScreenCapture"/> and applies input through <see cref="IRemoteInput"/>.
/// </summary>
public sealed class LucySessionAgent : IDisposable
{
    public const int MaxMessageBytes = 16 * 1024 * 1024;
    public const string DefaultPipeName = "LucyVNCAgent";
    private const int MaxFramebufferWidth = 1920;
    private const long MaxFramebufferPixels = 1920L * 1080;

    private readonly string _pipeName;
    private readonly IScreenCapture _screenCapture;
    private readonly IRemoteInput _remoteInput;
    private readonly string _authorizedWorkerSid;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private volatile CancellationTokenSource? _sessionCancellation;
    private NamedPipeServerStream? _server;
    private Task? _acceptTask;
    private Task? _watchdogTask;
    private bool _running;
    private bool _disposed;
    private bool _sessionFailed;
    private DateTime _lastHeartbeatUtc;

    public LucySessionAgent(
        IScreenCapture screenCapture,
        IRemoteInput remoteInput,
        string authorizedWorkerSid,
        string pipeName = DefaultPipeName,
        TimeSpan? heartbeatTimeout = null)
    {
        _screenCapture = screenCapture ?? throw new ArgumentNullException(nameof(screenCapture));
        _remoteInput = remoteInput ?? throw new ArgumentNullException(nameof(remoteInput));
        _ = new SecurityIdentifier(authorizedWorkerSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _authorizedWorkerSid = authorizedWorkerSid;
        _pipeName = pipeName;
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(30);
    }

    public string PipeName => _pipeName;

    public bool IsRunning => _running;

    public event EventHandler<string>? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            throw new InvalidOperationException("The LucySessionAgent is already running.");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellation = cts;
        var server = NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            4096,
            4096,
            CreatePipeSecurity());
        _server = server;
        _running = true;
        _sessionFailed = false;
        _lastHeartbeatUtc = DateTime.UtcNow;
        StatusChanged?.Invoke(this, $"Agent IPC listener started on pipe {PipeName}.");

        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _watchdogTask = Task.Run(() => HeartbeatWatchdogAsync(cts.Token));
        _acceptTask = AcceptLoopAsync(server, cts.Token, listening);
        return listening.Task;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _cancellation;
            _cancellation = null;
        }

        var server = _server;
        _server = null;
        lock (_sync)
        {
            _running = false;
        }

        cts?.Cancel();
        try
        {
            if (server is not null)
            {
                server.Dispose();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        var acceptTask = _acceptTask;
        var watchdogTask = _watchdogTask;
        _acceptTask = null;
        _watchdogTask = null;
        if (acceptTask is not null)
        {
            await acceptTask.ConfigureAwait(false);
        }

        if (watchdogTask is not null)
        {
            await watchdogTask.ConfigureAwait(false);
        }

        try
        {
            _remoteInput.ReleaseAll();
        }
        finally
        {
            cts?.Dispose();
            StatusChanged?.Invoke(this, "Agent IPC listener stopped.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _remoteInput.Dispose();
        _screenCapture.Dispose();
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream server, CancellationToken cancellationToken, TaskCompletionSource listening)
    {
        listening.TrySetResult();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Agent IPC listener error: {ex.Message}");
                break;
            }

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sessionCts.CancelAfter(TimeSpan.FromSeconds(5));
            _sessionCancellation = sessionCts;
            try
            {
                await HandleClientAsync(server, sessionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sessionCts.IsCancellationRequested)
            {
                // The session timed out or was stopped; keep listening for the next peer.
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Agent IPC client failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    _remoteInput.ReleaseAll();
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke(this, $"Could not release interactive input after IPC disconnect: {ex.Message}");
                }

                _sessionCancellation = null;
                sessionCts.Dispose();
                if (server.IsConnected)
                {
                    try
                    {
                        server.Disconnect();
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }
    }

    private async Task HandleClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        _sessionFailed = false;
        _lastHeartbeatUtc = DateTime.UtcNow;
        StatusChanged?.Invoke(this, "Agent IPC client connected.");
        var authenticated = false;

        while (!cancellationToken.IsCancellationRequested && !_sessionFailed)
        {
            var message = await ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(message);
            var type = document.RootElement.GetProperty("Type").GetString();
            var payload = document.RootElement.TryGetProperty("Payload", out var payloadProperty) && payloadProperty.ValueKind is JsonValueKind.Object
                ? payloadProperty
                : default;

            if (payload.ValueKind is not JsonValueKind.Object)
            {
                await WriteMessageAsync(stream, new { Type = "Error", Payload = new { Message = "Malformed IPC message payload." } }, cancellationToken).ConfigureAwait(false);
                _sessionFailed = true;
                break;
            }

            if (!authenticated && type != "Hello")
            {
                await WriteMessageAsync(stream, new { Type = "Error", Payload = new { Message = "IPC peer must authenticate before requesting desktop access." } }, cancellationToken).ConfigureAwait(false);
                _sessionFailed = true;
                break;
            }

            switch (type)
            {
                case "Hello":
                    if (authenticated)
                    {
                        _sessionFailed = true;
                        break;
                    }

                    authenticated = await HandleHelloAsync((NamedPipeServerStream)stream, cancellationToken).ConfigureAwait(false);
                    break;
                case "FrameRequest":
                    await HandleFrameRequestAsync(stream, payload, cancellationToken).ConfigureAwait(false);
                    break;
                case "InputEvent":
                    await HandleInputEventAsync(stream, payload, cancellationToken).ConfigureAwait(false);
                    break;
                case "Heartbeat":
                    _lastHeartbeatUtc = DateTime.UtcNow;
                    await WriteMessageAsync(stream, new { Type = "HeartbeatAck", Payload = new { Status = "Ready" } }, cancellationToken).ConfigureAwait(false);
                    break;
                case "ReleaseAll":
                    _remoteInput.ReleaseAll();
                    await WriteMessageAsync(stream, new { Type = "ReleaseAllAck", Payload = new { Accepted = true } }, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await WriteMessageAsync(stream, new { Type = "Error", Payload = new { Message = $"Unsupported IPC message {type}." } }, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task<bool> HandleHelloAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var clientSid = ResolveConnectedClientSid(stream);
        if (!IsAuthorizedWorker(clientSid))
        {
            await WriteMessageAsync(stream, new { Type = "HelloAck", Payload = new { Accepted = false } }, cancellationToken).ConfigureAwait(false);
            _sessionFailed = true;
            return false;
        }

        await WriteMessageAsync(stream, new { Type = "HelloAck", Payload = new { Accepted = true, ProtocolVersion = 1 } }, cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(this, "Agent accepted the IPC peer.");
        _lastHeartbeatUtc = DateTime.UtcNow;
        _sessionCancellation?.CancelAfter(Timeout.InfiniteTimeSpan);
        return true;
    }

    private async Task HandleFrameRequestAsync(Stream stream, JsonElement payload, CancellationToken cancellationToken)
    {
        var width = OptionalDimension(payload, "Width");
        var height = OptionalDimension(payload, "Height");
        var frame = CaptureBoundedFrame(width, height);
        await WriteMessageAsync(
            stream,
            new
            {
                Type = "FrameResponse",
                Payload = new
                {
                    Width = frame.Width,
                    Height = frame.Height,
                    Data = Convert.ToBase64String(frame.PixelData),
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    private ScreenFrame CaptureBoundedFrame(int requestedWidth, int requestedHeight)
    {
        if ((requestedWidth == 0) != (requestedHeight == 0))
        {
            throw new InvalidDataException("Framebuffer width and height must both be zero or both be positive.");
        }

        var width = requestedWidth;
        var height = requestedHeight;
        if (width == 0 && _screenCapture is IScreenCaptureBounds boundsProvider)
        {
            (width, height) = boundsProvider.GetPrimaryScreenSize();
        }

        if (width > 0 && height > 0)
        {
            var scale = Math.Min(1d, Math.Min(
                (double)MaxFramebufferWidth / width,
                Math.Sqrt((double)MaxFramebufferPixels / ((long)width * height))));
            width = Math.Max(1, (int)Math.Floor(width * scale));
            height = Math.Max(1, (int)Math.Floor(height * scale));
        }

        var frame = _screenCapture.CapturePrimaryScreen(width, height);
        if ((long)frame.Width * frame.Height > MaxFramebufferPixels ||
            frame.PixelData.Length > MaxMessageBytes * 3 / 4)
        {
            throw new InvalidDataException("The captured framebuffer exceeds the bounded IPC frame size.");
        }

        _remoteInput.SetFramebufferSize(checked((ushort)frame.Width), checked((ushort)frame.Height));
        return frame;
    }

    private Task HandleInputEventAsync(Stream stream, JsonElement payload, CancellationToken cancellationToken)
    {
        var action = payload.GetProperty("Action").GetString();
        if (action == "Key")
        {
            if (!payload.GetProperty("KeySym").TryGetUInt32(out var keySym))
            {
                throw new InvalidDataException("Input key symbol must be an unsigned integer.");
            }

            var pressed = payload.GetProperty("Pressed").GetBoolean();
            _remoteInput.ApplyKeyEvent(keySym, pressed);
        }
        else if (action == "Pointer")
        {
            var x = OptionalCoordinate(payload, "X");
            var y = OptionalCoordinate(payload, "Y");
            var buttons = OptionalButtonMask(payload, "Buttons");
            _remoteInput.ApplyPointerEvent(x, y, buttons);
        }
        else
        {
            throw new InvalidDataException("Unsupported remote input action.");
        }

        return WriteMessageAsync(stream, new { Type = "InputAck", Payload = new { Accepted = true } }, cancellationToken);
    }

    private bool IsAuthorizedWorker(string? sid)
    {
        if (sid is null)
        {
            return false;
        }

        return string.Equals(sid, _authorizedWorkerSid, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveConnectedClientSid(NamedPipeServerStream server)
    {
        string? sid = null;
        server.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value;
        });

        return sid;
    }

    private PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve the LucySessionAgent Windows identity.");
        security.AddAccessRule(new PipeAccessRule(currentSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!string.Equals(currentSid.Value, _authorizedWorkerSid, StringComparison.OrdinalIgnoreCase))
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(_authorizedWorkerSid),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
        }

        return security;
    }

    private static int OptionalDimension(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var property))
        {
            return 0;
        }

        if (!property.TryGetInt32(out var value) || value is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException($"Framebuffer {name} is outside the RFB range.");
        }

        return value;
    }

    private static ushort OptionalCoordinate(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var property) ||
            !property.TryGetUInt16(out var value))
        {
            throw new InvalidDataException($"Pointer coordinate {name} is invalid.");
        }

        return value;
    }

    private static byte OptionalButtonMask(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var property) ||
            !property.TryGetByte(out var value) ||
            (value & 0xe0) != 0)
        {
            throw new InvalidDataException("Pointer button mask is invalid.");
        }

        return value;
    }

    private async Task HeartbeatWatchdogAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sessionCancellation = _sessionCancellation;
                if (sessionCancellation is not null && sessionCancellation.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (sessionCancellation is not null &&
                    DateTime.UtcNow - _lastHeartbeatUtc > _heartbeatTimeout)
                {
                    StatusChanged?.Invoke(this, "LucyAgent IPC heartbeat timeout detected.");
                    sessionCancellation.Cancel();
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task WriteMessageAsync(Stream stream, object envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope);
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length is < 1 or > MaxMessageBytes)
        {
            throw new InvalidDataException("The LucyAgent IPC message exceeds the configured limit.");
        }

        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBuffer, (uint)payload.Length);
        await stream.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
        var length = (lengthBuffer[0] << 24) | (lengthBuffer[1] << 16) | (lengthBuffer[2] << 8) | lengthBuffer[3];
        if (length is < 1 or > MaxMessageBytes)
        {
            throw new InvalidDataException("The LucyAgent IPC message exceeds the configured limit.");
        }

        var buffer = new byte[length];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer);
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The LucyAgent IPC stream ended unexpectedly.");
            }

            offset += read;
        }

        return buffer.Length;
    }
}
