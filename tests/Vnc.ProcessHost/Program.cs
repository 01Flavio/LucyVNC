using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Server;

namespace Vnc.ProcessHost;

internal static class Program
{
    private static async Task Main()
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

    private sealed class SyntheticCapture : IScreenCapture
    {
        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(width, height, PixelFormat.Standard32Argb8888);
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
}
