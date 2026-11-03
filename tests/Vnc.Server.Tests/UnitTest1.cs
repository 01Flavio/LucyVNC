using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Protocol;
using Vnc.Server;

namespace Vnc.Server.Tests;

public class ServerTests
{
    [Theory]
    [InlineData(0x61u, 0x41)]
    [InlineData(0xFF51u, 0x25)]
    [InlineData(0xFFE1u, 0xA0)]
    [InlineData(0xFFBEu, 0x70)]
    [InlineData(0xFFD5u, 0x87)]
    public void RfbKeySymsMapToWindowsVirtualKeys(uint keySym, ushort expectedVirtualKey)
    {
        Assert.True(RfbInputMapper.TryGetVirtualKey(keySym, out var actual));
        Assert.Equal(expectedVirtualKey, actual);
    }

    [Fact]
    public void UnsupportedRemoteKeySymIsNotSilentlyMapped()
    {
        Assert.False(RfbInputMapper.TryGetVirtualKey(0x110000, out _));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.2.3.4", true)]
    [InlineData("172.31.0.1", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("169.254.2.3", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void ServerBindAddressIsLimitedToLoopbackPrivateOrLinkLocalRanges(string address, bool expected) =>
        Assert.Equal(expected, VncServer.IsAllowedBindAddress(IPAddress.Parse(address)));

    [Fact]
    public void ServerWildcardBindRequiresExplicitConfirmation()
    {
        Assert.False(VncServer.IsAllowedBindAddress(IPAddress.Any));
        Assert.True(VncServer.IsAllowedBindAddress(IPAddress.Any, allowAllInterfaces: true));
        Assert.Throws<ArgumentException>(() => new VncServer(new ConnectionSettings
        {
            Port = 0,
            BindAddress = IPAddress.Any.ToString(),
            Password = "password",
            Width = 64,
            Height = 48,
        }, new FakeScreenCapture(), new RecordingInput()));
    }

    [Theory]
    [InlineData(RfbProtocol.Version3_3)]
    [InlineData(RfbProtocol.Version3_7)]
    [InlineData(RfbProtocol.Version3_8)]
    public async Task ServerAuthenticatesAndStreamsRawFramebufferForSupportedVersions(string version)
    {
        var input = new RecordingInput();
        using var server = new VncServer(
            Settings(),
            new FakeScreenCapture(),
            input);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.BoundPort);
        using var stream = client.GetStream();
        Assert.Equal(RfbProtocol.Version3_8, RfbProtocol.ReadVersion(stream));
        await stream.WriteAsync(RfbProtocol.EncodeVersion(RfbProtocol.NegotiateVersion(version)));

        if (version == RfbProtocol.Version3_3)
        {
            Assert.Equal((uint)SecurityType.VncAuthentication, RfbProtocol.ReadUInt32(stream));
        }
        else
        {
            var securityTypes = RfbProtocol.ReadSecurityTypes(stream);
            Assert.Contains(SecurityType.VncAuthentication, securityTypes);
            RfbProtocol.WriteSelectedSecurityType(stream, SecurityType.VncAuthentication);
        }

        var challenge = RfbProtocol.ReadChallenge(stream);
        var response = VncAuthentication.ComputeResponse("password", challenge);
        await stream.WriteAsync(response);
        Assert.True(RfbProtocol.ReadAuthResult(stream, version == RfbProtocol.Version3_8));

        RfbProtocol.WriteClientInit(stream, true);
        var serverInit = RfbProtocol.ReadServerInit(stream);
        Assert.Equal((ushort)64, serverInit.Width);
        Assert.Equal((ushort)48, serverInit.Height);

        RfbProtocol.WriteSetPixelFormat(stream, PixelFormat.Standard32Argb8888);
        RfbProtocol.WriteSetEncodings(stream, RfbProtocol.RawEncoding);
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
    }

    [Fact]
    public async Task ServerRejectsIncorrectVncPassword()
    {
        using var server = new VncServer(Settings(), new FakeScreenCapture(), new RecordingInput());
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.BoundPort);
        using var stream = client.GetStream();
        Assert.Equal(RfbProtocol.Version3_8, RfbProtocol.ReadVersion(stream));
        await stream.WriteAsync(RfbProtocol.EncodeVersion(RfbProtocol.Version3_8));
        Assert.Contains(SecurityType.VncAuthentication, RfbProtocol.ReadSecurityTypes(stream));
        RfbProtocol.WriteSelectedSecurityType(stream, SecurityType.VncAuthentication);
        var challenge = RfbProtocol.ReadChallenge(stream);
        await stream.WriteAsync(VncAuthentication.ComputeResponse("bad-pass", challenge));

        Assert.False(RfbProtocol.ReadAuthResult(stream));
        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public void ServerRequiresSixToEightCharacterPasswordAndExplicitBindAddress()
    {
        Assert.Throws<ArgumentException>(() => new VncServer(new ConnectionSettings
        {
            Password = "short",
            BindAddress = "127.0.0.1",
        }, new FakeScreenCapture(), new RecordingInput()));
        Assert.Throws<ArgumentException>(() => new VncServer(new ConnectionSettings
        {
            Password = "password9",
            BindAddress = "127.0.0.1",
        }, new FakeScreenCapture(), new RecordingInput()));
        foreach (var password in new[] { "123456", "1234567", "12345678" })
        {
            using var server = new VncServer(new ConnectionSettings
            {
                Password = password,
                BindAddress = "127.0.0.1",
            }, new FakeScreenCapture(), new RecordingInput());
        }
        Assert.Throws<ArgumentException>(() => new VncServer(new ConnectionSettings
        {
            Password = "password",
            BindAddress = "localhost",
        }, new FakeScreenCapture(), new RecordingInput()));
        Assert.Throws<ArgumentException>(() => new VncServer(new ConnectionSettings
        {
            Password = "password",
            BindAddress = "8.8.8.8",
        }, new FakeScreenCapture(), new RecordingInput()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VncServer(new ConnectionSettings
        {
            Password = "password",
            BindAddress = "127.0.0.1",
            MaxSessions = 2,
        }, new FakeScreenCapture(), new RecordingInput()));
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
