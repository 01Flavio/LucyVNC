using System.IO.Pipes;
using System.Buffers.Binary;
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

    private readonly string _pipeName;
    private readonly IScreenCapture _screenCapture;
    private readonly IRemoteInput _remoteInput;
    private readonly string _authorizedSid;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private volatile CancellationTokenSource? _sessionCancellation;
    private NamedPipeServerStream? _server;
    private bool _running;
    private bool _disposed;
    private bool _sessionFailed;
    private DateTime _lastHeartbeatUtc;

    public LucySessionAgent(
        IScreenCapture screenCapture,
        IRemoteInput remoteInput,
        string authorizedSid,
        string pipeName = DefaultPipeName,
        TimeSpan? heartbeatTimeout = null)
    {
        _screenCapture = screenCapture;
        _remoteInput = remoteInput;
        _authorizedSid = authorizedSid;
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

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken.None);
        _cancellation = cts;
        var server = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.None,
            4096,
            4096);
        _server = server;
        _running = true;
        _sessionFailed = false;
        _lastHeartbeatUtc = DateTime.UtcNow;
        StatusChanged?.Invoke(this, $"Agent IPC listener started on pipe {PipeName}.");

        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => HeartbeatWatchdogAsync(cts.Token));
        _ = AcceptLoopAsync(server, cts.Token, listening);
        return listening.Task;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
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
        finally
        {
            cts?.Dispose();
            StatusChanged?.Invoke(this, "Agent IPC listener stopped.");
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
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
                _sessionCancellation = null;
                sessionCts.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        _sessionFailed = false;
        _lastHeartbeatUtc = DateTime.UtcNow;
        StatusChanged?.Invoke(this, "Agent IPC client connected.");

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

            switch (type)
            {
                case "Hello":
                    await HandleHelloAsync(stream, payload, cancellationToken).ConfigureAwait(false);
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
                default:
                    await WriteMessageAsync(stream, new { Type = "Error", Payload = new { Message = $"Unsupported IPC message {type}." } }, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task HandleHelloAsync(Stream stream, JsonElement payload, CancellationToken cancellationToken)
    {
        var sid = payload.GetProperty("Sid").GetString();
        if (!IsAuthorized(sid))
        {
            await WriteMessageAsync(stream, new { Type = "Error", Payload = new { Message = "Agent rejected the IPC peer identity." } }, cancellationToken).ConfigureAwait(false);
            _sessionFailed = true;
            throw new IOException("Unauthorized IPC peer.");
        }

        await WriteMessageAsync(stream, new { Type = "HelloAck", Payload = new { Sid = sid, Accepted = true } }, cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(this, "Agent accepted the IPC peer.");
    }

    private async Task HandleFrameRequestAsync(Stream stream, JsonElement payload, CancellationToken cancellationToken)
    {
        try
        {
            var frame = _screenCapture.CapturePrimaryScreen(
                payload.TryGetProperty("Width", out var widthProperty) && widthProperty.ValueKind is JsonValueKind.Number ? widthProperty.GetInt32() : 0,
                payload.TryGetProperty("Height", out var heightProperty) && heightProperty.ValueKind is JsonValueKind.Number ? heightProperty.GetInt32() : 0);
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
        catch (Exception ex)
        {
            await WriteMessageAsync(stream, new { Type = "Error", Payload = new { Message = $"Frame capture failed: {ex.Message}" } }, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task HandleInputEventAsync(Stream stream, JsonElement payload, CancellationToken cancellationToken)
    {
        var action = payload.GetProperty("Action").GetString();
        if (action == "Key")
        {
            var keySym = (uint)payload.GetProperty("KeySym").GetInt32();
            var pressed = payload.GetProperty("Pressed").GetBoolean();
            _remoteInput.ApplyKeyEvent(keySym, pressed);
        }
        else
        {
            var x = (ushort)payload.GetProperty("X").GetInt32();
            var y = (ushort)payload.GetProperty("Y").GetInt32();
            var buttons = (byte)payload.GetProperty("Buttons").GetInt32();
            _remoteInput.ApplyPointerEvent(x, y, buttons);
        }

        return WriteMessageAsync(stream, new { Type = "InputAck", Payload = new { Accepted = true } }, cancellationToken);
    }

    private bool IsAuthorized(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return false;
        }

        return string.Equals(sid, _authorizedSid, StringComparison.OrdinalIgnoreCase);
    }

    private async Task HeartbeatWatchdogAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var sessionCancellation = _sessionCancellation;
            if (sessionCancellation is not null && sessionCancellation.IsCancellationRequested)
            {
                return;
            }

            if (DateTime.UtcNow - _lastHeartbeatUtc > _heartbeatTimeout)
            {
                StatusChanged?.Invoke(this, "LucyAgent IPC heartbeat timeout detected.");
                _sessionCancellation?.Cancel();
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteMessageAsync(Stream stream, object envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope);
        var payload = Encoding.UTF8.GetBytes(json);
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
