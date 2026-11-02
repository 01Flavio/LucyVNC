using System.Buffers.Binary;
using System.Net.Sockets;
using Vnc.Core;
using Vnc.Protocol;

namespace Vnc.Viewer;

public sealed class FramebufferUpdatedEventArgs : EventArgs
{
    public FramebufferUpdatedEventArgs(ushort width, ushort height, IReadOnlyList<FramebufferRectangle> rectangles)
    {
        Width = width;
        Height = height;
        Rectangles = rectangles;
    }

    public ushort Width { get; }
    public ushort Height { get; }
    public IReadOnlyList<FramebufferRectangle> Rectangles { get; }
}

public sealed class VncViewerSession : IAsyncDisposable
{
    private readonly ConnectionSettings _settings;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _disconnectLock = new(1, 1);
    private readonly HashSet<uint> _pressedKeys = new();
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private byte[]? _framebuffer;
    private CancellationTokenSource? _sessionCancellation;
    private ushort _width;
    private ushort _height;
    private bool _disposed;
    private byte _buttonMask;

    public VncViewerSession(ConnectionSettings? settings = null)
    {
        _settings = settings ?? new ConnectionSettings();
        ValidateSettings(_settings);
    }

    public event EventHandler<FramebufferUpdatedEventArgs>? FramebufferUpdated;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? Disconnected;

    public bool IsConnected => _stream is not null && _tcpClient?.Connected == true;
    public ushort Width => _width;
    public ushort Height => _height;

    public async Task<ServerInit> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected)
        {
            throw new InvalidOperationException("The viewer is already connected.");
        }

        var client = new TcpClient();
        _tcpClient = client;
        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _sessionCancellation.Token;
        try
        {
            StatusChanged?.Invoke(this, "Connecting...");
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var handshakeToken = handshakeTimeout.Token;
            await client.ConnectAsync(_settings.Host, _settings.Port, handshakeToken).ConfigureAwait(false);
            client.NoDelay = true;
            _stream = client.GetStream();

            var serverVersion = await RfbProtocol.ReadVersionAsync(_stream, handshakeToken).ConfigureAwait(false);
            var negotiatedVersion = RfbProtocol.NegotiateVersion(serverVersion);
            await RfbProtocol.WriteRfbVersionAsync(_stream, negotiatedVersion, handshakeToken).ConfigureAwait(false);

            var usesLegacySecurity = negotiatedVersion == RfbProtocol.Version3_3;
            var securityType = usesLegacySecurity
                ? await RfbProtocol.ReadUInt32Async(_stream, handshakeToken).ConfigureAwait(false) is var legacyType && legacyType <= byte.MaxValue
                    ? (SecurityType)legacyType
                    : throw new InvalidDataException("Invalid RFB 3.3 security type.")
                : await ReadSelectedSecurityTypeAsync(_stream, negotiatedVersion, handshakeToken).ConfigureAwait(false);

            if (securityType == SecurityType.VncAuthentication)
            {
                var challenge = await RfbProtocol.ReadChallengeAsync(_stream, handshakeToken).ConfigureAwait(false);
                var response = VncAuthentication.ComputeResponse(_settings.Password, challenge);
                await _stream.WriteAsync(response, handshakeToken).ConfigureAwait(false);
            }
            else if (securityType != SecurityType.None || !_settings.AllowNullAuth)
            {
                throw new InvalidOperationException($"Server selected unsupported or disallowed security type {securityType}.");
            }

            if (securityType != SecurityType.None || !usesLegacySecurity)
            {
                var accepted = await RfbProtocol.ReadUInt32Async(_stream, handshakeToken).ConfigureAwait(false);
                if (accepted != 0)
                {
                    if (negotiatedVersion == RfbProtocol.Version3_8)
                    {
                        var reasonLength = await RfbProtocol.ReadBoundedLengthAsync(_stream, RfbProtocol.MaximumDesktopNameBytes, handshakeToken).ConfigureAwait(false);
                        var reason = new byte[reasonLength];
                        await RfbProtocol.ReadExactlyAsync(_stream, reason, handshakeToken).ConfigureAwait(false);
                        throw new InvalidOperationException($"VNC authentication failed: {System.Text.Encoding.UTF8.GetString(reason)}");
                    }

                    throw new InvalidOperationException("VNC authentication failed.");
                }
            }

            await _stream.WriteAsync(new[] { _settings.SharedDesktop ? (byte)1 : (byte)0 }, handshakeToken).ConfigureAwait(false);
            var serverInit = await RfbProtocol.ReadServerInitAsync(_stream, _settings.FramebufferLimitBytes, handshakeToken).ConfigureAwait(false);
            _width = serverInit.Width;
            _height = serverInit.Height;
            _framebuffer = new byte[checked(_width * _height * 4)];

            RfbProtocol.WriteSetPixelFormat(_stream, PixelFormat.Standard32Argb8888);
            RfbProtocol.WriteSetEncodings(_stream, RfbProtocol.RawEncoding, RfbProtocol.CopyRectEncoding);
            RfbProtocol.WriteFramebufferUpdateRequest(_stream, 0, 0, _width, _height, false);
            StatusChanged?.Invoke(this, $"Connected to {serverInit.DesktopName} ({_width}x{_height}). RFB is not encrypted.");
            return serverInit;
        }
        catch
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _stream is null || _framebuffer is null)
        {
            throw new InvalidOperationException("ConnectAsync must complete before RunAsync.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCancellation!.Token);
        var token = linked.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var messageType = await ReadByteAsync(_stream, token).ConfigureAwait(false);
                switch (messageType)
                {
                    case 0:
                        var rectangles = await RfbProtocol.ReadFramebufferUpdateAsync(
                            _stream, _width, _height, PixelFormat.Standard32Argb8888,
                            _settings.FramebufferLimitBytes, token, messageTypeAlreadyRead: true).ConfigureAwait(false);
                        var rendered = ApplyRectangles(rectangles, PixelFormat.Standard32Argb8888);
                        FramebufferUpdated?.Invoke(this, new FramebufferUpdatedEventArgs(_width, _height, rendered));
                        await SendFramebufferUpdateRequestAsync(true, token).ConfigureAwait(false);
                        break;
                    case 1:
                        await SkipAsync(_stream, 1 + 2 + (int)(await RfbProtocol.ReadUInt16Async(_stream, token).ConfigureAwait(false) * 6), token).ConfigureAwait(false);
                        break;
                    case 2:
                        StatusChanged?.Invoke(this, "Remote server sent a bell.");
                        break;
                    case 3:
                        await SkipAsync(_stream, 3, token).ConfigureAwait(false);
                        var cutTextLength = await RfbProtocol.ReadBoundedLengthAsync(_stream, RfbProtocol.MaximumDesktopNameBytes, token).ConfigureAwait(false);
                        await SkipAsync(_stream, cutTextLength, token).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported server message type {messageType}.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            StatusChanged?.Invoke(this, "Connection closed by the remote server.");
        }
        finally
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    public Task SendKeyEventAsync(uint keyCode, bool pressed, CancellationToken cancellationToken = default)
    {
        if (_settings.ViewOnly)
        {
            return Task.CompletedTask;
        }

        if (pressed)
        {
            _pressedKeys.Add(keyCode);
        }
        else
        {
            _pressedKeys.Remove(keyCode);
        }

        return SendAsync(stream => RfbProtocol.WriteKeyEvent(stream, pressed, keyCode), cancellationToken);
    }

    public Task SendPointerEventAsync(ushort x, ushort y, byte buttonMask, CancellationToken cancellationToken = default)
    {
        if (_settings.ViewOnly)
        {
            return Task.CompletedTask;
        }

        _buttonMask = buttonMask;
        return SendAsync(stream => RfbProtocol.WritePointerEvent(stream, x, y, (MouseButton)buttonMask), cancellationToken);
    }

    public async Task ReleaseAllInputAsync(CancellationToken cancellationToken = default)
    {
        if (_settings.ViewOnly || _stream is null)
        {
            _pressedKeys.Clear();
            _buttonMask = 0;
            return;
        }

        var keys = _pressedKeys.ToArray();
        foreach (var key in keys)
        {
            await SendKeyEventAsync(key, false, cancellationToken).ConfigureAwait(false);
        }

        if (_buttonMask != 0)
        {
            await SendPointerEventAsync(0, 0, 0, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync()
    {
        await _disconnectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _sessionCancellation?.Cancel();
            if (_stream is not null)
            {
                try
                {
                    await ReleaseAllInputAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException)
                {
                }
            }

            _stream?.Dispose();
            _stream = null;
            _tcpClient?.Dispose();
            _tcpClient = null;
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            _framebuffer = null;
            if (!_disposed)
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _disconnectLock.Release();
        }
    }

    private async Task SendFramebufferUpdateRequestAsync(bool incremental, CancellationToken cancellationToken)
    {
        await SendAsync(stream => RfbProtocol.WriteFramebufferUpdateRequest(stream, 0, 0, _width, _height, incremental), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(Action<Stream> write, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Viewer is not connected.");
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var payload = new MemoryStream(16);
            write(payload);
            await stream.WriteAsync(payload.GetBuffer().AsMemory(0, checked((int)payload.Length)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private IReadOnlyList<FramebufferRectangle> ApplyRectangles(IReadOnlyList<FramebufferRectangle> rectangles, PixelFormat format)
    {
        var output = new List<FramebufferRectangle>(rectangles.Count);
        foreach (var rectangle in rectangles)
        {
            var decoded = rectangle.Encoding == RfbProtocol.RawEncoding
                ? DecodeRaw(rectangle, format)
                : CopyRectangle(rectangle);
            CopyToFramebuffer(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, decoded);
            output.Add(rectangle with { Data = decoded, Encoding = RfbProtocol.RawEncoding });
        }

        return output;
    }

    private byte[] DecodeRaw(FramebufferRectangle rectangle, PixelFormat format)
    {
        var bytesPerPixel = format.BytesPerPixel;
        var pixels = new byte[checked(rectangle.Width * rectangle.Height * 4)];
        for (var index = 0; index < rectangle.Width * rectangle.Height; index++)
        {
            var source = index * bytesPerPixel;
            var value = bytesPerPixel switch
            {
                1 => rectangle.Data[source],
                2 => format.BigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(rectangle.Data.AsSpan(source, 2))
                    : BinaryPrimitives.ReadUInt16LittleEndian(rectangle.Data.AsSpan(source, 2)),
                4 => format.BigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(rectangle.Data.AsSpan(source, 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(rectangle.Data.AsSpan(source, 4)),
                _ => throw new InvalidDataException("Unsupported pixel width.")
            };

            var destination = index * 4;
            if (format.TrueColor)
            {
                pixels[destination] = ScaleChannel(value, format.BlueShift, format.BlueMax);
                pixels[destination + 1] = ScaleChannel(value, format.GreenShift, format.GreenMax);
                pixels[destination + 2] = ScaleChannel(value, format.RedShift, format.RedMax);
            }
            else
            {
                var luminance = (byte)(value & 0xff);
                pixels[destination] = pixels[destination + 1] = pixels[destination + 2] = luminance;
            }
            pixels[destination + 3] = 0;
        }

        return pixels;
    }

    private byte[] CopyRectangle(FramebufferRectangle rectangle)
    {
        var copy = new byte[checked(rectangle.Width * rectangle.Height * 4)];
        var rowBytes = rectangle.Width * 4;
        for (var row = 0; row < rectangle.Height; row++)
        {
            var sourceOffset = ((rectangle.SourceY + row) * _width + rectangle.SourceX) * 4;
            Buffer.BlockCopy(_framebuffer!, sourceOffset, copy, row * rowBytes, rowBytes);
        }

        return copy;
    }

    private void CopyToFramebuffer(ushort x, ushort y, ushort width, ushort height, byte[] pixels)
    {
        var rowBytes = width * 4;
        for (var row = 0; row < height; row++)
        {
            var destinationOffset = ((y + row) * _width + x) * 4;
            Buffer.BlockCopy(pixels, row * rowBytes, _framebuffer!, destinationOffset, rowBytes);
        }
    }

    private static byte ScaleChannel(uint pixel, byte shift, ushort maximum)
    {
        var channel = (pixel >> shift) & maximum;
        return (byte)((channel * 255u + maximum / 2u) / maximum);
    }

    private async ValueTask<SecurityType> ReadSelectedSecurityTypeAsync(Stream stream, string protocolVersion, CancellationToken cancellationToken)
    {
        var count = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            if (protocolVersion != RfbProtocol.Version3_8)
            {
                throw new InvalidOperationException("The server offered no RFB security types.");
            }

            var reasonLength = await RfbProtocol.ReadBoundedLengthAsync(stream, RfbProtocol.MaximumDesktopNameBytes, cancellationToken).ConfigureAwait(false);
            var reason = new byte[reasonLength];
            await RfbProtocol.ReadExactlyAsync(stream, reason, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Server offered no security types: {System.Text.Encoding.UTF8.GetString(reason)}");
        }

        var securityTypes = new byte[count];
        await RfbProtocol.ReadExactlyAsync(stream, securityTypes, cancellationToken).ConfigureAwait(false);
        if (securityTypes.Contains((byte)SecurityType.VncAuthentication))
        {
            await stream.WriteAsync(new[] { (byte)SecurityType.VncAuthentication }, cancellationToken).ConfigureAwait(false);
            return SecurityType.VncAuthentication;
        }

        if (securityTypes.Contains((byte)SecurityType.None))
        {
            if (!_settings.AllowNullAuth)
            {
                throw new InvalidOperationException("The server only offers unauthenticated access, which is disabled.");
            }

            await stream.WriteAsync(new[] { (byte)SecurityType.None }, cancellationToken).ConfigureAwait(false);
            return SecurityType.None;
        }

        throw new InvalidOperationException("The server offers no supported RFB security type.");
    }

    private static async ValueTask<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[1];
        await RfbProtocol.ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return bytes[0];
    }

    private static async Task SkipAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(4096, count)];
        while (count > 0)
        {
            var size = Math.Min(buffer.Length, count);
            await RfbProtocol.ReadExactlyAsync(stream, buffer.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
            count -= size;
        }
    }

    private static void ValidateSettings(ConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port is < 1 or > 65535)
        {
            throw new ArgumentException("A valid host and TCP port are required.", nameof(settings));
        }

        if (settings.FramebufferLimitBytes is < 1024 or > RfbProtocol.MaximumFramebufferBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.FramebufferLimitBytes));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        _disconnectLock.Dispose();
    }
}
