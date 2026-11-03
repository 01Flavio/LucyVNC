using Vnc.Agent;
using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Server;

namespace Vnc.ProcessHost;

internal static class Program
{
    private static Task Main(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "agent", StringComparison.OrdinalIgnoreCase)
            ? RunAgentAsync(args)
            : RunServerAsync();

    private static async Task RunAgentAsync(string[] args)
    {
        if (args.Length != 3)
        {
            throw new ArgumentException("Agent mode requires a pipe name and authorized worker SID.");
        }

        var agent = new LucySessionAgent(
            new SyntheticCapture(),
            new RecordingInput(),
            args[2],
            args[1]);
        using (agent)
        {
            await agent.StartAsync();
            Console.WriteLine("READY:AGENT");
            while (await Console.In.ReadLineAsync() is { } line &&
                   !string.Equals(line, "STOP", StringComparison.Ordinal))
            {
            }

            await agent.StopAsync();
        }
    }

    private static async Task RunServerAsync()
    {
        using var server = new VncServer(
            new ConnectionSettings
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                Password = "process1",
                Width = 32,
                Height = 24,
            },
            new SyntheticCapture(),
            new ConsoleInputSink());
        server.Start();
        Console.WriteLine($"READY:{server.BoundPort}");

        while (await Console.In.ReadLineAsync() is { } line &&
               !string.Equals(line, "STOP", StringComparison.Ordinal))
        {
        }

        await server.StopAsync();
    }

    private sealed class SyntheticCapture : IScreenCapture, IScreenCaptureBounds
    {
        public (int Width, int Height) GetPrimaryScreenSize() => (64, 48);

        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(width == 0 ? 64 : width, height == 0 ? 48 : height, PixelFormat.Standard32Argb8888);
            frame.Fill(12, 34, 56);
            return frame;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ConsoleInputSink : IRemoteInput
    {
        public void SetFramebufferSize(ushort width, ushort height)
        {
        }

        public void ApplyKeyEvent(uint keySym, bool pressed)
        {
            Console.WriteLine($"INPUT:KEY:{keySym:x}:{pressed}");
        }

        public void ApplyPointerEvent(ushort x, ushort y, byte buttonMask)
        {
            Console.WriteLine($"INPUT:POINTER:{x}:{y}:{buttonMask}");
        }

        public void ReleaseAll()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingInput : IRemoteInput
    {
        public void SetFramebufferSize(ushort width, ushort height)
        {
        }

        public void ApplyKeyEvent(uint keySym, bool pressed) =>
            Console.WriteLine($"INPUT:KEY:{keySym}:{pressed}");

        public void ApplyPointerEvent(ushort x, ushort y, byte buttonMask) =>
            Console.WriteLine($"INPUT:POINTER:{x}:{y}:{buttonMask}");

        public void ReleaseAll() => Console.WriteLine("INPUT:RELEASE");

        public void Dispose()
        {
        }
    }
}
