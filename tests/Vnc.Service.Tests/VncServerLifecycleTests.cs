using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Protocol;
using Vnc.Service;

namespace Vnc.Service.Tests;

public class VncServerLifecycleTests
{
    [Fact]
    public async Task StartAsyncOpensAuthenticatedLoopbackListenerAndStreamsInitialFrame()
    {
        var input = new RecordingInput();
        using var lifecycle = new VncServerLifecycle(Settings(), new FakeScreenCapture(), input);
        await lifecycle.StartAsync();

        Assert.True(lifecycle.IsRunning);
        Assert.True(lifecycle.BoundPort > 0);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, lifecycle.BoundPort);
        using var stream = client.GetStream();
        Assert.Equal(RfbProtocol.Version3_8, RfbProtocol.ReadVersion(stream));
        await stream.WriteAsync(RfbProtocol.EncodeVersion(RfbProtocol.NegotiateVersion(RfbProtocol.Version3_8)));

        var securityTypes = RfbProtocol.ReadSecurityTypes(stream);
        Assert.Contains(SecurityType.VncAuthentication, securityTypes);
        RfbProtocol.WriteSelectedSecurityType(stream, SecurityType.VncAuthentication);
        var challenge = RfbProtocol.ReadChallenge(stream);
        await stream.WriteAsync(VncAuthentication.ComputeResponse("password", challenge));
        Assert.True(RfbProtocol.ReadAuthResult(stream));

        RfbProtocol.WriteClientInit(stream, true);
        var serverInit = RfbProtocol.ReadServerInit(stream);
        Assert.Equal((ushort)64, serverInit.Width);
        Assert.Equal((ushort)48, serverInit.Height);

        RfbProtocol.WriteSetPixelFormat(stream, PixelFormat.Standard32Argb8888);
        RfbProtocol.WriteSetEncodings(stream, RfbProtocol.RawEncoding, RfbProtocol.CopyRectEncoding);
        RfbProtocol.WriteFramebufferUpdateRequest(stream, 0, 0, 64, 48, false);
        var firstRect = Assert.Single(RfbProtocol.ReadFramebufferUpdate(
            stream, serverInit.Width, serverInit.Height, PixelFormat.Standard32Argb8888));
        Assert.Equal(RfbProtocol.RawEncoding, firstRect.Encoding);
        Assert.Equal(64 * 48 * 4, firstRect.Data.Length);
        Assert.Equal(new byte[] { 30, 20, 10, 0 }, firstRect.Data[..4]);

        RfbProtocol.WriteKeyEvent(stream, true, 0x61);
        RfbProtocol.WriteKeyEvent(stream, false, 0x61);
        RfbProtocol.WritePointerEvent(stream, 7, 9, MouseButton.Left);
        RfbProtocol.WritePointerEvent(stream, 7, 9, MouseButton.None);
        await input.WaitForEventsAsync(4);
        Assert.Contains(input.Events, item => item == "key:97:True");
        Assert.Contains(input.Events, item => item == "key:97:False");
        Assert.Contains(input.Events, item => item == "pointer:7:9:1");
        Assert.Contains(input.Events, item => item == "pointer:7:9:0");

        await lifecycle.StopAsync();
        Assert.False(lifecycle.IsRunning);
    }

    [Fact]
    public async Task StartAsyncRejectsWrongVncPasswordWithoutStreamingFrame()
    {
        using var lifecycle = new VncServerLifecycle(Settings(), new FakeScreenCapture(), new RecordingInput());
        await lifecycle.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, lifecycle.BoundPort);
        using var stream = client.GetStream();
        Assert.Equal(RfbProtocol.Version3_8, RfbProtocol.ReadVersion(stream));
        await stream.WriteAsync(RfbProtocol.EncodeVersion(RfbProtocol.NegotiateVersion(RfbProtocol.Version3_8)));
        Assert.Contains(SecurityType.VncAuthentication, RfbProtocol.ReadSecurityTypes(stream));
        RfbProtocol.WriteSelectedSecurityType(stream, SecurityType.VncAuthentication);
        var challenge = RfbProtocol.ReadChallenge(stream);
        await stream.WriteAsync(VncAuthentication.ComputeResponse("bad-pass", challenge));

        Assert.False(RfbProtocol.ReadAuthResult(stream));
        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public async Task StopAsyncStopsRunningListener()
    {
        using var lifecycle = new VncServerLifecycle(Settings(), new FakeScreenCapture(), new RecordingInput());
        await lifecycle.StartAsync();
        Assert.True(lifecycle.IsRunning);

        await lifecycle.StopAsync();
        Assert.False(lifecycle.IsRunning);
    }

    private static ConnectionSettings Settings() => new()
    {
        Port = 0,
        BindAddress = "127.0.0.1",
        Password = "password",
        Width = 64,
        Height = 48,
    };

    private sealed class FakeScreenCapture : IScreenCapture
    {
        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(width, height, PixelFormat.Standard32Argb8888);
            frame.Fill(10, 20, 30);
            return frame;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingInput : IRemoteInput
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly SemaphoreSlim _changed = new(0);

        public IReadOnlyCollection<string> Events => _events.ToArray();

        public void SetFramebufferSize(ushort width, ushort height)
        {
        }

        public void ApplyKeyEvent(uint keySym, bool pressed)
        {
            _events.Enqueue($"key:{keySym}:{pressed}");
            _changed.Release();
        }

        public void ApplyPointerEvent(ushort x, ushort y, byte buttonMask)
        {
            _events.Enqueue($"pointer:{x}:{y}:{buttonMask}");
            _changed.Release();
        }

        public void ReleaseAll()
        {
            _events.Enqueue("release");
            _changed.Release();
        }

        public async Task WaitForEventsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (_events.Count < count)
            {
                await _changed.WaitAsync(timeout.Token);
            }
        }

        public void Dispose()
        {
            _changed.Dispose();
        }
    }
}
