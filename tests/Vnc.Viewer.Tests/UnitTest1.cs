using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Protocol;
using Vnc.Server;
using Vnc.Viewer;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Buffers.Binary;

namespace Vnc.Viewer.Tests;

public class ViewerSessionTests
{
    [Fact]
    public async Task ViewerDecodesAndPublishesContinuousFramebufferUpdates()
    {
        var settings = new ConnectionSettings
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Password = "password",
            Width = 64,
            Height = 48,
        };
        using var server = new VncServer(settings, new ChangingScreenCapture());
        server.Start();

        await using var viewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = server.BoundPort,
            Password = "password",
        });
        var serverMessages = new ConcurrentQueue<string>();
        var viewerMessages = new ConcurrentQueue<string>();
        server.StatusChanged += (_, message) => serverMessages.Enqueue(message);
        viewer.StatusChanged += (_, message) => viewerMessages.Enqueue(message);
        var firstUpdate = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FramebufferUpdated += (_, args) => firstUpdate.TrySetResult(args);
        var init = await viewer.ConnectAsync();
        Assert.Equal((ushort)64, init.Width);
        Assert.Equal((ushort)48, init.Height);
        using var cancellation = new CancellationTokenSource();
        var runTask = viewer.RunAsync(cancellation.Token);

        FramebufferUpdatedEventArgs update;
        try
        {
            update = await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"{string.Join(Environment.NewLine, serverMessages.Concat(viewerMessages))}{Environment.NewLine}Viewer task: {runTask.Status}{Environment.NewLine}{runTask.Exception}");
            throw;
        }
        var rectangle = Assert.Single(update.Rectangles);
        Assert.Equal(0, rectangle.X);
        Assert.Equal(0, rectangle.Y);
        Assert.Equal((ushort)64, rectangle.Width);
        Assert.Equal((ushort)48, rectangle.Height);
        Assert.Equal(new byte[] { 90, 20, 10, 0 }, rectangle.Data[..4]);

        cancellation.Cancel();
        await viewer.DisconnectAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ViewerNeverFallsBackToUnauthenticatedServerByDefault()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peerTask = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            using var stream = peer.GetStream();
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("RFB 003.008\n"));
            _ = RfbProtocol.ReadVersion(stream);
            await stream.WriteAsync(new byte[] { 1, (byte)SecurityType.None });
            await Task.Delay(100);
        });
        await using var viewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = port,
            Password = "password",
            AllowNullAuth = false,
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewer.ConnectAsync());
        await peerTask;
    }

    [Fact]
    public async Task ViewerAppliesCopyRectToItsPersistentFramebuffer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            await RfbProtocol.WriteRfbVersionAsync(stream, RfbProtocol.Version3_8, timeout.Token);
            Assert.Equal(RfbProtocol.Version3_8, await RfbProtocol.ReadVersionAsync(stream, timeout.Token));
            RfbProtocol.WriteSecurityTypes(stream, SecurityType.VncAuthentication);
            Assert.Equal(SecurityType.VncAuthentication, (SecurityType)await ReadByteAsync(stream, timeout.Token));
            var challenge = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
            await stream.WriteAsync(challenge, timeout.Token);
            var response = new byte[16];
            await RfbProtocol.ReadExactlyAsync(stream, response, timeout.Token);
            Assert.True(VncAuthentication.VerifyResponse("password", challenge, response));
            RfbProtocol.WriteAuthResult(stream, accepted: true);
            Assert.True(await ReadByteAsync(stream, timeout.Token) != 0);
            RfbProtocol.WriteServerInit(stream, 2, 1, PixelFormat.Standard32Argb8888, "copy-rect-test");

            Assert.Equal((byte)0, await ReadByteAsync(stream, timeout.Token));
            await SkipAsync(stream, 19, timeout.Token);
            Assert.Equal((byte)2, await ReadByteAsync(stream, timeout.Token));
            var encodingHeader = new byte[3];
            await RfbProtocol.ReadExactlyAsync(stream, encodingHeader, timeout.Token);
            var encodingCount = BinaryPrimitives.ReadUInt16BigEndian(encodingHeader.AsSpan(1));
            await SkipAsync(stream, encodingCount * 4, timeout.Token);
            Assert.Equal((byte)3, await ReadByteAsync(stream, timeout.Token));
            await SkipAsync(stream, 9, timeout.Token);
            await RfbProtocol.WriteFramebufferUpdateAsync(
                stream,
                new[] { new FramebufferRectangle(0, 0, 2, 1, RfbProtocol.RawEncoding, new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 }) },
                timeout.Token);

            Assert.Equal((byte)3, await ReadByteAsync(stream, timeout.Token));
            await SkipAsync(stream, 9, timeout.Token);
            await RfbProtocol.WriteFramebufferUpdateAsync(
                stream,
                new[] { new FramebufferRectangle(1, 0, 1, 1, RfbProtocol.CopyRectEncoding, Array.Empty<byte>(), 0, 0) },
                timeout.Token);
        });

        await using var viewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = port,
            Password = "password",
        });
        var updateCount = 0;
        var copiedUpdate = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FramebufferUpdated += (_, args) =>
        {
            if (Interlocked.Increment(ref updateCount) == 2)
            {
                copiedUpdate.TrySetResult(args);
            }
        };
        await viewer.ConnectAsync(timeout.Token);
        using var cancellation = new CancellationTokenSource();
        var runTask = viewer.RunAsync(cancellation.Token);
        var update = await copiedUpdate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var copiedRectangle = Assert.Single(update.Rectangles);
        Assert.Equal(RfbProtocol.RawEncoding, copiedRectangle.Encoding);
        Assert.Equal((ushort)1, copiedRectangle.X);
        Assert.Equal(new byte[] { 1, 2, 3, 0 }, copiedRectangle.Data);

        cancellation.Cancel();
        await viewer.DisconnectAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task SkipAsync(Stream stream, int byteCount, CancellationToken cancellationToken)
    {
        var buffer = new byte[byteCount];
        await RfbProtocol.ReadExactlyAsync(stream, buffer, cancellationToken);
    }

    private static async Task<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await RfbProtocol.ReadExactlyAsync(stream, buffer, cancellationToken);
        return buffer[0];
    }

    private sealed class ChangingScreenCapture(int defaultWidth = 64, int defaultHeight = 48) : IScreenCapture
    {
        private int _captureCount;

        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(width == 0 ? defaultWidth : width, height == 0 ? defaultHeight : height, PixelFormat.Standard32Argb8888);
            frame.Fill(10, 20, 30);
            if (Interlocked.Increment(ref _captureCount) > 1)
            {
                frame.PixelData[0] = 90;
            }

            return frame;
        }

        public void Dispose()
        {
        }
    }
}
