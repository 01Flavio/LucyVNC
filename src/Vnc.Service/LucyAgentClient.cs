using System.IO.Pipes;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
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
    private const string ReleaseAllType = "ReleaseAll";
    private const string ReleaseAllAckType = "ReleaseAllAck";
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(15);

    private readonly string _pipeName;
    private readonly string _expectedAgentSid;
    private readonly SemaphoreSlim _messageLock = new(1, 1);
    private readonly object _sync = new();
    private NamedPipeClientStream? _client;
    private Stream? _stream;
    private bool _connected;

    public LucyAgentClient(string pipeName = "LucyVNCAgent", string expectedAgentSid = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _ = new SecurityIdentifier(expectedAgentSid);
        _pipeName = pipeName;
        _expectedAgentSid = expectedAgentSid;
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
            if (_client is not null)
            {
                throw new InvalidOperationException("Dispose the previous LucyAgent connection before reconnecting.");
            }

            client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            stream = client;
            _client = client;
            _stream = stream;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            var actualServerSid = GetPipeServerSid(client);
            if (!string.Equals(actualServerSid, _expectedAgentSid, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("The named-pipe server Windows identity is not the configured interactive agent.");
            }

            timeoutCts.CancelAfter(ConnectTimeout);
            await WriteMessageAsync(stream, new { Type = HelloType, Payload = new { ProtocolVersion = 1 } }, timeoutCts.Token).ConfigureAwait(false);
            var helloAck = await ReadMessageAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            using var helloDocument = JsonDocument.Parse(helloAck);
            if (helloDocument.RootElement.GetProperty("Type").GetString() != HelloAckType)
            {
                throw new InvalidDataException("The LucyAgent did not acknowledge the IPC hello.");
            }

            if (!helloDocument.RootElement.GetProperty("Payload").GetProperty("Accepted").GetBoolean() ||
                helloDocument.RootElement.GetProperty("Payload").GetProperty("ProtocolVersion").GetInt32() != 1)
            {
                throw new UnauthorizedAccessException("The LucyAgent rejected the IPC peer identity or protocol version.");
            }

            lock (_sync)
            {
                _connected = true;
            }

            return true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public async Task<ScreenFrame> RequestFrameAsync(int width, int height, CancellationToken cancellationToken)
    {
        if (width is < 0 or > ushort.MaxValue || height is < 0 or > ushort.MaxValue ||
            (width == 0) != (height == 0))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must both be zero or valid RFB dimensions.");
        }

        return await ExecuteMessageAsync(async (stream, token) =>
        {
            await WriteMessageAsync(stream, new { Type = FrameRequestType, Payload = new { Width = width, Height = height } }, token).ConfigureAwait(false);
            var response = await ReadMessageAsync(stream, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.GetProperty("Type").GetString() != FrameResponseType)
            {
                throw new InvalidDataException("The LucyAgent did not answer the framebuffer request.");
            }

            var payload = document.RootElement.GetProperty("Payload");
            var frameWidth = payload.GetProperty("Width").GetInt32();
            var frameHeight = payload.GetProperty("Height").GetInt32();
            var frame = new ScreenFrame(frameWidth, frameHeight, PixelFormat.Standard32Argb8888);
            var data = Convert.FromBase64String(payload.GetProperty("Data").GetString() ?? string.Empty);
            if (data.Length != frame.PixelData.Length)
            {
                throw new InvalidDataException("The LucyAgent framebuffer length does not match its dimensions.");
            }

            Buffer.BlockCopy(data, 0, frame.PixelData, 0, data.Length);
            return frame;
        }, cancellationToken).ConfigureAwait(false);
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
        await ExecuteMessageAsync(async (stream, token) =>
        {
            await WriteMessageAsync(stream, new { Type = InputEventType, Payload = payload }, token).ConfigureAwait(false);
            var ack = await ReadMessageAsync(stream, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(ack);
            if (document.RootElement.GetProperty("Type").GetString() != InputAckType)
            {
                throw new InvalidDataException("The LucyAgent did not acknowledge the input event.");
            }

            if (!document.RootElement.GetProperty("Payload").GetProperty("Accepted").GetBoolean())
            {
                throw new InvalidOperationException("The LucyAgent rejected the input event.");
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task ReleaseAllAsync(CancellationToken cancellationToken) =>
        ExecuteMessageAsync(async (stream, token) =>
        {
            await WriteMessageAsync(stream, new { Type = ReleaseAllType, Payload = new { } }, token).ConfigureAwait(false);
            var ack = await ReadMessageAsync(stream, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(ack);
            if (document.RootElement.GetProperty("Type").GetString() != ReleaseAllAckType ||
                !document.RootElement.GetProperty("Payload").GetProperty("Accepted").GetBoolean())
            {
                throw new InvalidDataException("The LucyAgent did not acknowledge input release.");
            }

            return true;
        }, cancellationToken);

    public Task SendHeartbeatAsync(CancellationToken cancellationToken) =>
        ExecuteMessageAsync(async (stream, token) =>
        {
            await WriteMessageAsync(stream, new { Type = HeartbeatType, Payload = new { } }, token).ConfigureAwait(false);
            var ack = await ReadMessageAsync(stream, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(ack);
            if (document.RootElement.GetProperty("Type").GetString() != HeartbeatAckType)
            {
                throw new InvalidDataException("The LucyAgent did not acknowledge the heartbeat.");
            }

            return true;
        }, cancellationToken);

    private async Task<T> ExecuteMessageAsync<T>(
        Func<Stream, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _messageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = _stream;
            if (stream is null || !IsConnected)
            {
                throw new InvalidOperationException("The LucyAgent client is not connected.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MessageTimeout);
            return await operation(stream, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _messageLock.Release();
        }
    }

    private string GetPipeServerSid(NamedPipeClientStream client)
    {
        if (!OperatingSystem.IsWindows() ||
            !GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId))
        {
            throw new UnauthorizedAccessException("Could not resolve the Windows process hosting the LucyAgent pipe.");
        }

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
        {
            throw new UnauthorizedAccessException("Could not inspect the Windows identity hosting the LucyAgent pipe.");
        }

        using (token)
        {
            _ = GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out var requiredLength);
            if (requiredLength == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query the LucyAgent process token.");
            }

            var buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
            try
            {
                if (!GetTokenInformation(token, TokenUserClass, buffer, requiredLength, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the LucyAgent process token.");
                }

                var user = Marshal.PtrToStructure<TokenUser>(buffer);
                return new SecurityIdentifier(user.User.Sid).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
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

    private static SafeProcessHandle OpenProcess(int desiredAccess, bool inheritHandle, uint processId) =>
        OpenProcessNative(desiredAccess, inheritHandle, processId);

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeServerProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcessNative(int desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
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
