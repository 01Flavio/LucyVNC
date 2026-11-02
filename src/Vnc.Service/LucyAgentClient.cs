using System.IO.Pipes;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Vnc.Core;
using Vnc.Protocol;

namespace Vnc.Service;

/// <summary>
/// LucyAgentClient: named-pipe client used by the LucyServer worker to reach the LucySessionAgent.
/// It authenticates the agent, requests framebuffer frames, forwards input events and tracks heartbeats.
/// </summary>
public sealed class LucyAgentClient : IDisposable
{
    public const int MaxMessageBytes = 16 * 1024 * 1024;
    private const string HelloType = "Hello";
    private const string HelloAckType = "HelloAck";
    private const string FrameRequestType = "FrameRequest";
    private const string FrameResponseType = "FrameResponse";
    private const string InputEventType = "InputEvent";
    private const string InputAckType = "InputAck";
    private const string HeartbeatType = "Heartbeat";
    private const string HeartbeatAckType = "HeartbeatAck";

    private readonly string _pipeName;
    private readonly string _agentSid;
    private readonly object _sync = new();
    private NamedPipeClientStream? _client;
    private Stream? _stream;
    private bool _connected;

    public LucyAgentClient(string pipeName = "LucyVNCAgent", string agentSid = "")
    {
        _pipeName = pipeName;
        _agentSid = agentSid;
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _connected;
            }
        }
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        NamedPipeClientStream client;
        Stream stream;
        lock (_sync)
        {
            client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            stream = client;
            _client = client;
            _stream = stream;
        }

        try
        {
            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using (timeoutCts)
            {
                await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            }

            await WriteMessageAsync(stream, new { Type = HelloType, Payload = new { Sid = _agentSid } }, cancellationToken).ConfigureAwait(false);
            var helloAck = await ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            using var helloDocument = JsonDocument.Parse(helloAck);
            if (helloDocument.RootElement.GetProperty("Type").GetString() != HelloAckType)
            {
                throw new InvalidDataException("The LucyAgent did not acknowledge the IPC hello.");
            }

            if (!helloDocument.RootElement.GetProperty("Payload").GetProperty("Accepted").GetBoolean())
            {
                throw new InvalidDataException("The LucyAgent rejected the IPC peer identity.");
            }

            lock (_sync)
            {
                _connected = true;
            }

            return true;
        }
        catch
        {
            lock (_sync)
            {
                _connected = false;
            }

            throw;
        }
    }

    public async Task<ScreenFrame> RequestFrameAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("The LucyAgent client is not connected.");
        await WriteMessageAsync(stream, new { Type = FrameRequestType, Payload = new { } }, cancellationToken).ConfigureAwait(false);
        var response = await ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response);
        if (document.RootElement.GetProperty("Type").GetString() != FrameResponseType)
        {
            throw new InvalidDataException("The LucyAgent did not answer the framebuffer request.");
        }

        var payload = document.RootElement.GetProperty("Payload");
        var width = payload.GetProperty("Width").GetInt32();
        var height = payload.GetProperty("Height").GetInt32();
        var data = Convert.FromBase64String(payload.GetProperty("Data").GetString() ?? string.Empty);
        var frame = new ScreenFrame(width, height, PixelFormat.Standard32Argb8888);
        Buffer.BlockCopy(data, 0, frame.PixelData, 0, Math.Min(data.Length, frame.PixelData.Length));
        return frame;
    }

    public Task SendInputAsync(uint keySym, bool pressed, CancellationToken cancellationToken)
    {
        return SendInputAsyncCore(new { Action = "Key", KeySym = keySym, Pressed = pressed }, cancellationToken);
    }

    public Task SendPointerAsync(ushort x, ushort y, byte buttons, CancellationToken cancellationToken)
    {
        return SendInputAsyncCore(new { Action = "Pointer", X = x, Y = y, Buttons = buttons }, cancellationToken);
    }

    private async Task SendInputAsyncCore(object payload, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("The LucyAgent client is not connected.");
        await WriteMessageAsync(stream, new { Type = InputEventType, Payload = payload }, cancellationToken).ConfigureAwait(false);
        var ack = await ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(ack);
        if (document.RootElement.GetProperty("Type").GetString() != InputAckType)
        {
            throw new InvalidDataException("The LucyAgent did not acknowledge the input event.");
        }

        if (!document.RootElement.GetProperty("Payload").GetProperty("Accepted").GetBoolean())
        {
            throw new InvalidOperationException("The LucyAgent rejected the input event.");
        }
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("The LucyAgent client is not connected.");
        await WriteMessageAsync(stream, new { Type = HeartbeatType, Payload = new { } }, cancellationToken).ConfigureAwait(false);
        var ack = await ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(ack);
        if (document.RootElement.GetProperty("Type").GetString() != HeartbeatAckType)
        {
            throw new InvalidDataException("The LucyAgent did not acknowledge the heartbeat.");
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

    public void Dispose()
    {
        lock (_sync)
        {
            var client = _client;
            _client = null;
            _stream = null;
            _connected = false;
            client?.Dispose();
        }
    }
}
