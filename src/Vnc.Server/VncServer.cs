using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Protocol;

namespace Vnc.Server;

public sealed class VncServer : IDisposable
{
    private const int DiffTileSize = 64;
    private readonly ConnectionSettings _settings;
    private readonly IScreenCapture _screenCapture;
    private readonly IRemoteInput _remoteInput;
    private readonly SemaphoreSlim _sessionSlots;
    private readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private Task? _listenerTask;
    private bool _disposed;

    public VncServer(ConnectionSettings settings, IScreenCapture? screenCapture = null, IRemoteInput? remoteInput = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ValidateSettings(settings);
        _screenCapture = screenCapture ?? new GdiScreenCapture();
        _remoteInput = remoteInput ?? new SendInputController();
        _sessionSlots = new SemaphoreSlim(settings.MaxSessions, settings.MaxSessions);
    }

    public event EventHandler<string>? StatusChanged;
    public bool IsRunning => _listenerTask is { IsCompleted: false };
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _settings.Port;

    public static bool IsAllowedBindAddress(IPAddress address, bool allowAllInterfaces = false)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Equals(IPAddress.Any))
        {
            return allowAllInterfaces;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xfe) == 0xfc ||
                   bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80;
        }

        return false;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            throw new InvalidOperationException("VNC server is already running.");
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Parse(_settings.BindAddress), _settings.Port);
        _listener.Start();
        _listenerTask = Task.Run(() => ListenLoopAsync(_listener, _cancellation.Token));
        StatusChanged?.Invoke(this, $"Server listening on {_settings.BindAddress}:{BoundPort}. RFB traffic is not encrypted.");
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        _cancellation.Cancel();
        _listener?.Stop();
        foreach (var client in _clients.Keys)
        {
            client.Dispose();
        }

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        var clientTasks = _clients.Values.ToArray();
        if (clientTasks.Length > 0)
        {
            await Task.WhenAll(clientTasks).ConfigureAwait(false);
        }

        _listenerTask = null;
        _listener = null;
        _cancellation.Dispose();
        _cancellation = null;
        StatusChanged?.Invoke(this, "Server stopped.");
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();

    private async Task ListenLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            client.NoDelay = true;
            if (!_sessionSlots.Wait(0))
            {
                StatusChanged?.Invoke(this, $"Rejected connection from {client.Client.RemoteEndPoint}: session limit reached.");
                client.Dispose();
                continue;
            }

            var task = HandleClientAsync(client, cancellationToken);
            _clients.TryAdd(client, task);
            _ = task.ContinueWith(
                completedTask =>
                {
                    _clients.TryRemove(client, out var removedTask);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCancellation)
    {
        var remoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown peer";
        using (client)
        using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation))
        using (var stream = client.GetStream())
        {
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var token = handshakeTimeout.Token;
            try
            {
                await RfbProtocol.WriteRfbVersionAsync(stream, RfbProtocol.Version3_8, token).ConfigureAwait(false);
                var peerVersion = await RfbProtocol.ReadVersionAsync(stream, token).ConfigureAwait(false);
                var version = RfbProtocol.NegotiateVersion(peerVersion);
                var legacy = version == RfbProtocol.Version3_3;
                if (legacy)
                {
                    await WriteUInt32Async(stream, (uint)SecurityType.VncAuthentication, token).ConfigureAwait(false);
                }
                else
                {
                    await stream.WriteAsync(new byte[] { 1, (byte)SecurityType.VncAuthentication }, token).ConfigureAwait(false);
                }

                if (!legacy)
                {
                    var selected = await ReadByteAsync(stream, token).ConfigureAwait(false);
                    if (selected != (byte)SecurityType.VncAuthentication)
                    {
                        StatusChanged?.Invoke(this, $"Rejected connection from {client.Client.RemoteEndPoint}: unsupported security selection.");
                        return;
                    }
                }

                var challenge = RfbProtocol.CreateChallenge();
                await stream.WriteAsync(challenge, token).ConfigureAwait(false);
                var response = new byte[16];
                await RfbProtocol.ReadExactlyAsync(stream, response, token).ConfigureAwait(false);
                var authenticated = VncAuthentication.VerifyResponse(_settings.Password, challenge, response);
                await WriteUInt32Async(stream, authenticated ? 0u : 1u, token).ConfigureAwait(false);
                if (!authenticated)
                {
                    if (version == RfbProtocol.Version3_8)
                    {
                        var reason = System.Text.Encoding.UTF8.GetBytes("Authentication failed");
                        await WriteUInt32Async(stream, (uint)reason.Length, token).ConfigureAwait(false);
                        await stream.WriteAsync(reason, token).ConfigureAwait(false);
                    }

                    StatusChanged?.Invoke(this, $"Authentication failed for {client.Client.RemoteEndPoint}.");
                    return;
                }

                StatusChanged?.Invoke(this, $"Authenticated viewer connected: {client.Client.RemoteEndPoint}.");
                handshakeTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                _ = await ReadByteAsync(stream, serverCancellation).ConfigureAwait(false);
                var initialFrame = _screenCapture.CapturePrimaryScreen(_settings.Width, _settings.Height);
                if (initialFrame.PixelData.Length > _settings.FramebufferLimitBytes)
                {
                    throw new InvalidDataException("Captured framebuffer exceeds the configured memory limit.");
                }

                var width = checked((ushort)initialFrame.Width);
                var height = checked((ushort)initialFrame.Height);
                _remoteInput.SetFramebufferSize(width, height);
                var pixelFormat = PixelFormat.Standard32Argb8888;
                RfbProtocol.WriteServerInit(stream, width, height, pixelFormat, Environment.MachineName);
                var previousFrame = initialFrame.PixelData.ToArray();
                await ClientMessageLoopAsync(stream, initialFrame, previousFrame, pixelFormat, serverCancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested || token.IsCancellationRequested)
            {
            }
            catch (IOException ex)
            {
                StatusChanged?.Invoke(this, $"I/O ended for {remoteEndPoint}: {ex.Message}");
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidDataException ex)
            {
                StatusChanged?.Invoke(this, $"Closed malformed RFB session from {remoteEndPoint}: {ex.Message}");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"RFB session from {remoteEndPoint} failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    _remoteInput.ReleaseAll();
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke(this, $"Could not release remote input for {remoteEndPoint}: {ex.Message}");
                }

                _sessionSlots.Release();
                StatusChanged?.Invoke(this, $"Viewer disconnected: {remoteEndPoint}.");
            }
        }
    }

    private async Task ClientMessageLoopAsync(
        NetworkStream stream,
        ScreenFrame initialFrame,
        byte[] previousFrame,
        PixelFormat pixelFormat,
        CancellationToken cancellationToken)
    {
        var supportsRaw = false;
        var minimumCaptureInterval = TimeSpan.FromSeconds(1d / _settings.MaxFramesPerSecond);
        var lastCapture = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            var messageType = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            switch (messageType)
            {
                case 0:
                    await SkipAsync(stream, 3, cancellationToken).ConfigureAwait(false);
                    var formatBytes = new byte[16];
                    await RfbProtocol.ReadExactlyAsync(stream, formatBytes, cancellationToken).ConfigureAwait(false);
                    pixelFormat = PixelFormat.FromBytes(formatBytes);
                    if (!pixelFormat.TrueColor)
                    {
                        throw new InvalidDataException("Indexed-colour pixel formats are not supported.");
                    }
                    break;
                case 1:
                    await SkipAsync(stream, 1 + 2, cancellationToken).ConfigureAwait(false);
                    var colors = await RfbProtocol.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                    await SkipAsync(stream, colors * 6, cancellationToken).ConfigureAwait(false);
                    break;
                case 2:
                    await SkipAsync(stream, 1, cancellationToken).ConfigureAwait(false);
                    var encodingCount = await RfbProtocol.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                    for (var index = 0; index < encodingCount; index++)
                    {
                        var encoding = await RfbProtocol.ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
                        supportsRaw |= encoding == RfbProtocol.RawEncoding;
                    }
                    break;
                case 3:
                    var incremental = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false) != 0;
                    var requestX = await RfbProtocol.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                    var requestY = await RfbProtocol.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                    var requestWidth = await RfbProtocol.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                    var requestHeight = await RfbProtocol.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                    var request = new FramebufferRequest(requestX, requestY, requestWidth, requestHeight, incremental);
                    var elapsed = Stopwatch.GetElapsedTime(lastCapture);
                    if (elapsed < minimumCaptureInterval)
                    {
                        await Task.Delay(minimumCaptureInterval - elapsed, cancellationToken).ConfigureAwait(false);
                    }

                    var currentFrame = _screenCapture.CapturePrimaryScreen(initialFrame.Width, initialFrame.Height);
                    lastCapture = Stopwatch.GetTimestamp();
                    var rectangles = supportsRaw
                        ? BuildUpdate(request, currentFrame, previousFrame, pixelFormat)
                        : Array.Empty<FramebufferRectangle>();
                    await RfbProtocol.WriteFramebufferUpdateAsync(stream, rectangles, cancellationToken).ConfigureAwait(false);
                    currentFrame.PixelData.CopyTo(previousFrame, 0);
                    break;
                case 4:
                    var pressed = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false) != 0;
                    await SkipAsync(stream, 2, cancellationToken).ConfigureAwait(false);
                    var keySym = await RfbProtocol.ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
                    if (!_settings.ViewOnly)
                    {
                        _remoteInput.ApplyKeyEvent(keySym, pressed);
                    }
                    break;
                case 5:
                    var buttonMask = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                    var pointerX = await RfbProtocol.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                    var pointerY = await RfbProtocol.ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                    if (!_settings.ViewOnly)
                    {
                        _remoteInput.ApplyPointerEvent(pointerX, pointerY, buttonMask);
                    }
                    break;
                case 6:
                    await SkipAsync(stream, 3, cancellationToken).ConfigureAwait(false);
                    var textLength = await RfbProtocol.ReadBoundedLengthAsync(stream, RfbProtocol.MaximumDesktopNameBytes, cancellationToken).ConfigureAwait(false);
                    await SkipAsync(stream, textLength, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported client message type {messageType}.");
            }
        }
    }

    private static IReadOnlyList<FramebufferRectangle> BuildUpdate(
        FramebufferRequest request,
        ScreenFrame current,
        byte[] previous,
        PixelFormat pixelFormat)
    {
        var x = request.X;
        var y = request.Y;
        var right = Math.Min((int)current.Width, (int)request.X + request.Width);
        var bottom = Math.Min((int)current.Height, (int)request.Y + request.Height);
        var left = Math.Clamp((int)x, 0, current.Width);
        var top = Math.Clamp((int)y, 0, current.Height);
        if (right <= left || bottom <= top)
        {
            return Array.Empty<FramebufferRectangle>();
        }

        if (!request.Incremental)
        {
            return new[] { CreateRawRectangle(current, left, top, right - left, bottom - top, pixelFormat) };
        }

        var changed = new List<FramebufferRectangle>();
        var minX = right;
        var minY = bottom;
        var maxX = left;
        var maxY = top;
        for (var tileY = top; tileY < bottom; tileY += DiffTileSize)
        {
            for (var tileX = left; tileX < right; tileX += DiffTileSize)
            {
                var tileWidth = Math.Min(DiffTileSize, right - tileX);
                var tileHeight = Math.Min(DiffTileSize, bottom - tileY);
                if (!HasChanges(current.PixelData, previous, current.Width, tileX, tileY, tileWidth, tileHeight))
                {
                    continue;
                }

                minX = Math.Min(minX, tileX);
                minY = Math.Min(minY, tileY);
                maxX = Math.Max(maxX, tileX + tileWidth);
                maxY = Math.Max(maxY, tileY + tileHeight);
                if (changed.Count < RfbProtocol.MaximumRectanglesPerUpdate)
                {
                    changed.Add(CreateRawRectangle(current, tileX, tileY, tileWidth, tileHeight, pixelFormat));
                }
            }
        }

        if (changed.Count > 0 && changed.Count >= RfbProtocol.MaximumRectanglesPerUpdate)
        {
            return new[] { CreateRawRectangle(current, minX, minY, maxX - minX, maxY - minY, pixelFormat) };
        }

        return changed;
    }

    private static bool HasChanges(byte[] current, byte[] previous, int strideWidth, int x, int y, int width, int height)
    {
        var rowBytes = width * 4;
        for (var row = 0; row < height; row++)
        {
            var offset = ((y + row) * strideWidth + x) * 4;
            if (!current.AsSpan(offset, rowBytes).SequenceEqual(previous.AsSpan(offset, rowBytes)))
            {
                return true;
            }
        }

        return false;
    }

    private static FramebufferRectangle CreateRawRectangle(ScreenFrame frame, int x, int y, int width, int height, PixelFormat format)
    {
        var bytesPerPixel = format.BytesPerPixel;
        var data = new byte[checked(width * height * bytesPerPixel)];
        var index = 0;
        for (var row = 0; row < height; row++)
        {
            var sourceOffset = ((y + row) * frame.Width + x) * 4;
            for (var column = 0; column < width; column++)
            {
                var blue = frame.PixelData[sourceOffset + column * 4];
                var green = frame.PixelData[sourceOffset + column * 4 + 1];
                var red = frame.PixelData[sourceOffset + column * 4 + 2];
                var pixel = ((uint)red * format.RedMax / 255u << format.RedShift)
                    | ((uint)green * format.GreenMax / 255u << format.GreenShift)
                    | ((uint)blue * format.BlueMax / 255u << format.BlueShift);
                for (var byteIndex = 0; byteIndex < bytesPerPixel; byteIndex++)
                {
                    var shift = format.BigEndian ? (bytesPerPixel - 1 - byteIndex) * 8 : byteIndex * 8;
                    data[index++] = (byte)(pixel >> shift);
                }
            }
        }

        return new FramebufferRectangle((ushort)x, (ushort)y, (ushort)width, (ushort)height, RfbProtocol.RawEncoding, data);
    }

    private static async ValueTask<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        await RfbProtocol.ReadExactlyAsync(stream, value, cancellationToken).ConfigureAwait(false);
        return value[0];
    }

    private static async Task SkipAsync(Stream stream, int byteCount, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(byteCount, 4096)];
        while (byteCount > 0)
        {
            var count = Math.Min(byteCount, buffer.Length);
            await RfbProtocol.ReadExactlyAsync(stream, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            byteCount -= count;
        }
    }

    private static async Task WriteUInt32Async(Stream stream, uint value, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSettings(ConnectionSettings settings)
    {
        if (!IPAddress.TryParse(settings.BindAddress, out var bindAddress))
        {
            throw new ArgumentException("Bind address must be an explicit IPv4 or IPv6 address.", nameof(settings));
        }

        if (!IsAllowedBindAddress(bindAddress, settings.AllowAllInterfaces))
        {
            throw new ArgumentException(
                "Only loopback, private/VPN, link-local, or explicitly confirmed IPv4 wildcard bind addresses are allowed.",
                nameof(settings));
        }

        if (settings.Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.Port));
        }

        if (settings.Password.Length != 8 || settings.Password.Any(character => character > 0x7f))
        {
            throw new ArgumentException("VNC Authentication requires an exactly eight-character ASCII password in this application.", nameof(settings));
        }

        if (settings.MaxSessions != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.MaxSessions), "Only one session is supported because Windows input state is shared.");
        }

        if (settings.MaxFramesPerSecond is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.MaxFramesPerSecond));
        }

        if (settings.FramebufferLimitBytes is < 1024 or > RfbProtocol.MaximumFramebufferBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.FramebufferLimitBytes));
        }

        if ((settings.Width == 0) != (settings.Height == 0) ||
            settings.Width is < 0 or > ushort.MaxValue || settings.Height is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentException("Capture width and height must both be zero (native size) or valid positive dimensions.", nameof(settings));
        }

        if (settings.Width > 0 && (long)settings.Width * settings.Height * 4 > settings.FramebufferLimitBytes)
        {
            throw new ArgumentException("Requested framebuffer dimensions exceed the configured memory limit.", nameof(settings));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _remoteInput.Dispose();
        _screenCapture.Dispose();
        _sessionSlots.Dispose();
        _disposed = true;
    }
}
