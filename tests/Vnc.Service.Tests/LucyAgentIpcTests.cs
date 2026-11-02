using System.Collections.Concurrent;
using System.IO.Pipes;
using Vnc.Agent;
using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Service;

namespace Vnc.Service.Tests;

public class LucyAgentIpcTests
{
    private const string AuthorizedSid = "S-1-5-21-1004346348-1177238915-682003330-500";

    [Fact]
    public async Task LucyAgentClientConnectsToLucySessionAgentAndExchangesFrameAndInput()
    {
        var pipeName = $"LucyVNCAgentIpc_{Guid.NewGuid():N}";
        using var agent = new LucySessionAgent(new FakeScreenCapture(), new FakeRemoteInput(), AuthorizedSid, pipeName);
        await agent.StartAsync();

        var client = new LucyAgentClient(pipeName, AuthorizedSid);
        await client.ConnectAsync(CancellationToken.None);
        Assert.True(client.IsConnected);

        var frame = await client.RequestFrameAsync(CancellationToken.None);
        Assert.Equal(64, frame.Width);
        Assert.Equal(48, frame.Height);
        Assert.Equal(64 * 48 * 4, frame.PixelData.Length);
        Assert.Equal(new byte[] { 30, 20, 10, 0 }, frame.PixelData[..4]);

        await client.SendInputAsync(0x61, true, CancellationToken.None);
        await client.SendInputAsync(0x62, false, CancellationToken.None);
        await client.SendHeartbeatAsync(CancellationToken.None);

        client.Dispose();
        await agent.StopAsync();
    }

    private sealed class FakeScreenCapture : IScreenCapture
    {
        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(width <= 0 ? 64 : width, height <= 0 ? 48 : height, PixelFormat.Standard32Argb8888);
            frame.Fill(10, 20, 30);
            return frame;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRemoteInput : IRemoteInput
    {
        private readonly ConcurrentQueue<string> _events = new();

        public void SetFramebufferSize(ushort width, ushort height)
        {
        }

        public void ApplyKeyEvent(uint keySym, bool pressed)
        {
            _events.Enqueue($"key:{keySym}:{pressed}");
        }

        public void ApplyPointerEvent(ushort x, ushort y, byte buttonMask)
        {
        }

        public void ReleaseAll()
        {
        }

        public void Dispose()
        {
        }
    }
}
