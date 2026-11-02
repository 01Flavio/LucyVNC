using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using Vnc.Agent;
using Vnc.Core;
using Vnc.Platform.Windows;

namespace Vnc.Agent.Tests;

public class LucySessionAgentTests
{
    private const string AuthorizedSid = "S-1-5-21-1004346348-1177238915-682003330-500";

    [Fact]
    public async Task LucySessionAgentAcceptsAuthorizedPeerServesFrameAndForwardsInput()
    {
        var capture = new FakeScreenCapture();
        var input = new FakeRemoteInput();
        var pipeName = $"LucyVNCAgentTest_{Guid.NewGuid():N}";
        using var agent = new LucySessionAgent(capture, input, AuthorizedSid, pipeName);
        await agent.StartAsync();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        var stream = (Stream)client;
        await client.ConnectAsync();
        await WriteMessageAsync(stream, new { Type = "Hello", Payload = new { Sid = AuthorizedSid } });
        using (var helloAck = JsonDocument.Parse(await ReadMessageAsync(stream)))
        {
            Assert.Equal("HelloAck", helloAck.RootElement.GetProperty("Type").GetString());
            Assert.True(helloAck.RootElement.GetProperty("Payload").GetProperty("Accepted").GetBoolean());
        }

        await WriteMessageAsync(stream, new { Type = "FrameRequest", Payload = new { } });
        using (var frameResponse = JsonDocument.Parse(await ReadMessageAsync(stream)))
        {
            Assert.Equal("FrameResponse", frameResponse.RootElement.GetProperty("Type").GetString());
            var payload = frameResponse.RootElement.GetProperty("Payload");
            var width = payload.GetProperty("Width").GetInt32();
            var height = payload.GetProperty("Height").GetInt32();
            Assert.True(width > 0);
            Assert.True(height > 0);
            var data = Convert.FromBase64String(payload.GetProperty("Data").GetString() ?? string.Empty);
            Assert.Equal(64 * 48 * 4, data.Length);
        }

        await WriteMessageAsync(stream, new { Type = "InputEvent", Payload = new { Action = "Key", KeySym = 97, Pressed = true } });
        using (var inputAck = JsonDocument.Parse(await ReadMessageAsync(stream)))
        {
            Assert.Equal("InputAck", inputAck.RootElement.GetProperty("Type").GetString());
            Assert.True(inputAck.RootElement.GetProperty("Payload").GetProperty("Accepted").GetBoolean());
        }

        await WriteMessageAsync(stream, new { Type = "Heartbeat", Payload = new { } });
        using (var heartbeatAck = JsonDocument.Parse(await ReadMessageAsync(stream)))
        {
            Assert.Equal("HeartbeatAck", heartbeatAck.RootElement.GetProperty("Type").GetString());
            Assert.Equal("Ready", heartbeatAck.RootElement.GetProperty("Payload").GetProperty("Status").GetString());
        }

        await agent.StopAsync();
        Assert.False(agent.IsRunning);
    }

    [Fact]
    public async Task LucySessionAgentRejectsUnauthorizedIpcPeerIdentity()
    {
        var capture = new FakeScreenCapture();
        var input = new FakeRemoteInput();
        var pipeName = $"LucyVNCAgentTest_{Guid.NewGuid():N}";
        using var agent = new LucySessionAgent(capture, input, AuthorizedSid, pipeName);
        await agent.StartAsync();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        var stream = (Stream)client;
        await client.ConnectAsync();
        await WriteMessageAsync(stream, new { Type = "Hello", Payload = new { Sid = "S-1-5-21-9999-9999-9999-9999-9999" } });

        var rejected = false;
        try
        {
            using var error = JsonDocument.Parse(await ReadMessageAsync(stream));
            Assert.Equal("Error", error.RootElement.GetProperty("Type").GetString());
            rejected = true;
        }
        catch (Exception)
        {
            // The agent may close the pipe after rejecting; both paths prove rejection.
        }

        Assert.True(rejected);
        await agent.StopAsync();
    }

    private static async Task WriteMessageAsync(Stream stream, object envelope)
    {
        var json = JsonSerializer.Serialize(envelope);
        var payload = Encoding.UTF8.GetBytes(json);
        var lengthBuffer = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(lengthBuffer, (uint)payload.Length);
        await stream.WriteAsync(lengthBuffer);
        await stream.WriteAsync(payload);
    }

    private static async Task<string> ReadMessageAsync(Stream stream)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer);
        var length = (lengthBuffer[0] << 24) | (lengthBuffer[1] << 16) | (lengthBuffer[2] << 8) | lengthBuffer[3];
        var buffer = new byte[length];
        await ReadExactlyAsync(stream, buffer);
        return Encoding.UTF8.GetString(buffer);
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0)
            {
                throw new EndOfStreamException("The agent IPC stream ended unexpectedly.");
            }

            offset += read;
        }

        return buffer.Length;
    }

    private sealed class FakeScreenCapture : IScreenCapture
    {
        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(64, 48, PixelFormat.Standard32Argb8888);
            frame.Fill(10, 20, 30);
            return frame;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRemoteInput : IRemoteInput
    {
        public int KeyCount;

        public void SetFramebufferSize(ushort width, ushort height)
        {
        }

        public void ApplyKeyEvent(uint keySym, bool pressed)
        {
            Interlocked.Increment(ref KeyCount);
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
