using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Server;
using Vnc.Viewer;

namespace Vnc.Integration.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task WindowsDpiAwarenessCanBeInitializedInAnIsolatedProcess()
    {
        var hostPath = FindProcessHost();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = hostPath,
                Arguments = "dpi-probe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        Assert.True(process.Start());

        var output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var error = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(process.ExitCode == 0, error);
        Assert.Equal("DPI:PER_MONITOR_V2", output.Trim());
    }

    [Fact]
    public async Task MultipleViewerSessionsKeepFramebufferAndInputIsolated()
    {
        var firstInput = new RecordingInput();
        var secondInput = new RecordingInput();
        using var firstServer = new VncServer(new ConnectionSettings
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Password = "first001",
            Width = 32,
            Height = 24,
        }, new ColoredScreenCapture(11, 22, 33), firstInput);
        using var secondServer = new VncServer(new ConnectionSettings
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Password = "second01",
            Width = 32,
            Height = 24,
        }, new ColoredScreenCapture(44, 55, 66), secondInput);
        firstServer.Start();
        secondServer.Start();
        var firstServerClientDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstServer.StatusChanged += (_, message) =>
        {
            if (message.StartsWith("Viewer disconnected:", StringComparison.Ordinal))
            {
                firstServerClientDisconnected.TrySetResult();
            }
        };

        await using var firstViewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "localhost",
            Port = firstServer.BoundPort,
            Password = "first001",
        });
        await using var secondViewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = secondServer.BoundPort,
            Password = "second01",
        });
        var firstFrame = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFrame = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondContinuedAfterPeerClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFrameCount = 0;
        firstViewer.FramebufferUpdated += (_, args) => firstFrame.TrySetResult(args);
        secondViewer.FramebufferUpdated += (_, args) =>
        {
            secondFrame.TrySetResult(args);
            if (Interlocked.Increment(ref secondFrameCount) >= 2)
            {
                secondContinuedAfterPeerClose.TrySetResult();
            }
        };
        await firstViewer.ConnectAsync();
        await secondViewer.ConnectAsync();
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var firstRun = firstViewer.RunAsync(firstCancellation.Token);
        var secondRun = secondViewer.RunAsync(secondCancellation.Token);

        Assert.Equal(new byte[] { 33, 22, 11, 0 }, Assert.Single((await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(5))).Rectangles).Data[..4]);
        Assert.Equal(new byte[] { 66, 55, 44, 0 }, Assert.Single((await secondFrame.Task.WaitAsync(TimeSpan.FromSeconds(5))).Rectangles).Data[..4]);
        await firstViewer.SendKeyEventAsync(0x61, true);
        await secondViewer.SendKeyEventAsync(0x62, true);
        await firstInput.WaitForEventsAsync(1);
        await secondInput.WaitForEventsAsync(1);
        Assert.Contains("key:61:True", firstInput.Events);
        Assert.DoesNotContain("key:62:True", firstInput.Events);
        Assert.Contains("key:62:True", secondInput.Events);
        Assert.DoesNotContain("key:61:True", secondInput.Events);

        firstCancellation.Cancel();
        await firstViewer.DisconnectAsync();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
        await firstServerClientDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(firstViewer.IsConnected);
        Assert.True(secondViewer.IsConnected);
        await secondContinuedAfterPeerClose.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using var reconnectedViewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = firstServer.BoundPort,
            Password = "first001",
        });
        var reconnectedFrame = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        reconnectedViewer.FramebufferUpdated += (_, args) => reconnectedFrame.TrySetResult(args);
        await reconnectedViewer.ConnectAsync();
        using var reconnectedCancellation = new CancellationTokenSource();
        var reconnectedRun = reconnectedViewer.RunAsync(reconnectedCancellation.Token);
        Assert.NotEmpty((await reconnectedFrame.Task.WaitAsync(TimeSpan.FromSeconds(5))).Rectangles);

        reconnectedCancellation.Cancel();
        await reconnectedViewer.DisconnectAsync();
        await reconnectedRun.WaitAsync(TimeSpan.FromSeconds(5));
        secondCancellation.Cancel();
        await secondViewer.DisconnectAsync();
        await secondRun.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ViewerCommunicatesWithAuthenticatedServerInSeparateProcess()
    {
        var hostPath = FindProcessHost();
        Assert.True(File.Exists(hostPath), $"Process test host was not built: {hostPath}");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        Assert.True(process.Start());
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.StartsWith("READY:", ready);
            var port = int.Parse(ready!["READY:".Length..]);

            await using var viewer = new VncViewerSession(new ConnectionSettings
            {
                Host = "127.0.0.1",
                Port = port,
                Password = "process1",
            });
            var firstUpdate = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.FramebufferUpdated += (_, args) => firstUpdate.TrySetResult(args);
            var init = await viewer.ConnectAsync();
            Assert.Equal((ushort)32, init.Width);
            Assert.Equal((ushort)24, init.Height);
            using var cancellation = new CancellationTokenSource();
            var run = viewer.RunAsync(cancellation.Token);

            var frame = await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new byte[] { 56, 34, 12, 0 }, Assert.Single(frame.Rectangles).Data[..4]);
            await viewer.SendKeyEventAsync(0x61, true);
            Assert.Equal("INPUT:KEY:61:True", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await viewer.SendPointerEventAsync(9, 7, 1);
            Assert.Equal("INPUT:POINTER:9:7:1", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));

            cancellation.Cancel();
            await viewer.DisconnectAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("STOP");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task ServerAndViewerExchangeFramebufferAndRemoteInput()
    {
        var input = new RecordingInput();
        using var server = new VncServer(new ConnectionSettings
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Password = "password",
            Width = 96,
            Height = 72,
        }, new SyntheticScreenCapture(), input);
        server.Start();

        var viewerSettings = new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = server.BoundPort,
            Password = "password",
        };
        await using var viewer = new VncViewerSession(viewerSettings);
        var update = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FramebufferUpdated += (_, args) => update.TrySetResult(args);
        var serverInit = await viewer.ConnectAsync();
        Assert.Equal((ushort)96, serverInit.Width);
        Assert.Equal((ushort)72, serverInit.Height);

        using var cancellation = new CancellationTokenSource();
        var run = viewer.RunAsync(cancellation.Token);
        var frame = await update.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rectangle = Assert.Single(frame.Rectangles);
        Assert.Equal((ushort)96, rectangle.Width);
        Assert.Equal((ushort)72, rectangle.Height);
        Assert.Equal(new byte[] { 70, 40, 20, 0 }, rectangle.Data[..4]);

        await viewer.SendKeyEventAsync(0xffe1, true);
        await viewer.SendKeyEventAsync(0xffe1, false);
        await viewer.SendPointerEventAsync(42, 19, 5);
        await viewer.SendPointerEventAsync(42, 19, 0);
        await input.WaitForEventsAsync(4);
        Assert.Contains(input.Events, item => item == "key:ffe1:True");
        Assert.Contains(input.Events, item => item == "pointer:42:19:5");

        cancellation.Cancel();
        await viewer.DisconnectAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IncrementalFramebufferRequestOnlyPublishesChangedTile()
    {
        using var server = new VncServer(new ConnectionSettings
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Password = "password",
            Width = 128,
            Height = 128,
        }, new ChangingScreenCapture());
        server.Start();
        var viewerSettings = new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = server.BoundPort,
            Password = "password",
        };
        await using var viewer = new VncViewerSession(viewerSettings);
        var updates = new ConcurrentQueue<FramebufferUpdatedEventArgs>();
        var receivedTwoUpdates = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FramebufferUpdated += (_, args) =>
        {
            updates.Enqueue(args);
            if (updates.Count >= 2)
            {
                receivedTwoUpdates.TrySetResult();
            }
        };
        await viewer.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var run = viewer.RunAsync(cancellation.Token);
        await receivedTwoUpdates.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((ushort)128, Assert.Single(updates.First().Rectangles).Width);
        var changed = Assert.Single(updates.Skip(1).First().Rectangles);
        Assert.Equal((ushort)0, changed.X);
        Assert.Equal((ushort)0, changed.Y);
        Assert.Equal((ushort)64, changed.Width);
        Assert.Equal((ushort)64, changed.Height);

        cancellation.Cancel();
        await viewer.DisconnectAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ViewerReconstructsEveryPixelOfAFullHdFramebuffer()
    {
        const ushort width = 1920;
        const ushort height = 1080;
        using var server = new VncServer(new ConnectionSettings
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Password = "password",
            Width = width,
            Height = height,
        }, new PatternScreenCapture(), new RecordingInput());
        server.Start();

        await using var viewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = server.BoundPort,
            Password = "password",
        });
        var updateReceived = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FramebufferUpdated += (_, args) => updateReceived.TrySetResult(args);
        var serverInit = await viewer.ConnectAsync();
        Assert.Equal(width, serverInit.Width);
        Assert.Equal(height, serverInit.Height);

        using var cancellation = new CancellationTokenSource();
        var run = viewer.RunAsync(cancellation.Token);
        var update = await updateReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reconstructed = new byte[width * height * 4];
        foreach (var rectangle in update.Rectangles)
        {
            Assert.True((uint)rectangle.X + rectangle.Width <= width);
            Assert.True((uint)rectangle.Y + rectangle.Height <= height);
            var rowBytes = rectangle.Width * 4;
            for (var row = 0; row < rectangle.Height; row++)
            {
                var sourceOffset = row * rowBytes;
                var destinationOffset = ((rectangle.Y + row) * width + rectangle.X) * 4;
                Buffer.BlockCopy(rectangle.Data, sourceOffset, reconstructed, destinationOffset, rowBytes);
            }
        }

        var expected = PatternScreenCapture.CreateExpectedFrame(width, height);
        Assert.Equal(expected, reconstructed);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, reconstructed[..4]);
        var topRight = (width - 1) * 4;
        Assert.Equal(new byte[] { 0x7f, 0, 0x7f, 0 }, reconstructed[topRight..(topRight + 4)]);
        var bottomLeft = (height - 1) * width * 4;
        Assert.Equal(new byte[] { 0, 0x37, 0x37, 0 }, reconstructed[bottomLeft..(bottomLeft + 4)]);
        var bottomRight = ((height - 1) * width + width - 1) * 4;
        Assert.Equal(new byte[] { 0x7f, 0x37, 0xb6, 0 }, reconstructed[bottomRight..(bottomRight + 4)]);

        cancellation.Cancel();
        await viewer.DisconnectAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ViewerReconnectionWorksAfterUnexpectedServerShutdown()
    {
        using var server = new VncServer(new ConnectionSettings
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Password = "password",
            Width = 32,
            Height = 24,
        }, new SyntheticScreenCapture());
        server.Start();

        var reconnectSettings = new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = server.BoundPort,
            Password = "password",
        };
        await using var viewer = new VncViewerSession(reconnectSettings);
        var disconnectCount = 0;
        viewer.Disconnected += (_, _) => Interlocked.Increment(ref disconnectCount);
        var updates = 0;
        var secondUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FramebufferUpdated += (_, _) =>
        {
            if (Interlocked.Increment(ref updates) == 2)
            {
                secondUpdate.TrySetResult();
            }
        };

        await viewer.ConnectAsync();
        using var firstRunCancellation = new CancellationTokenSource();
        var firstRun = viewer.RunAsync(firstRunCancellation.Token);
        await WaitForUpdateAsync(() => Volatile.Read(ref updates) >= 1);
        await server.StopAsync();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(viewer.IsConnected);
        Assert.Equal(1, Volatile.Read(ref disconnectCount));

        server.Start();
        reconnectSettings.Port = server.BoundPort;
        await viewer.ConnectAsync();
        using var secondRunCancellation = new CancellationTokenSource();
        var secondRun = viewer.RunAsync(secondRunCancellation.Token);
        await secondUpdate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        secondRunCancellation.Cancel();
        await viewer.DisconnectAsync();
        await secondRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(server.IsRunning);
        await server.StopAsync();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task ServerContinuesAcceptingConnectionsAfterViewerAuthenticationFailure()
    {
        using var server = new VncServer(new ConnectionSettings
        {
            Port = 0,
            BindAddress = "127.0.0.1",
            Password = "password",
            Width = 32,
            Height = 24,
        }, new SyntheticScreenCapture());
        server.Start();

        await using (var invalidViewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = server.BoundPort,
            Password = "bad-pass",
        }))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => invalidViewer.ConnectAsync());
        }

        await using var validViewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = server.BoundPort,
            Password = "password",
        });
        var receivedFrame = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        validViewer.FramebufferUpdated += (_, args) => receivedFrame.TrySetResult(args);
        await validViewer.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var run = validViewer.RunAsync(cancellation.Token);
        Assert.NotEmpty((await receivedFrame.Task.WaitAsync(TimeSpan.FromSeconds(5))).Rectangles);

        cancellation.Cancel();
        await validViewer.DisconnectAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ViewerCanRetryAfterPeerClosesDuringRfbHandshake()
    {
        using var incompletePeer = new TcpListener(IPAddress.Loopback, 0);
        incompletePeer.Start();
        var port = ((IPEndPoint)incompletePeer.LocalEndpoint).Port;
        var accepted = Task.Run(async () =>
        {
            using var client = await incompletePeer.AcceptTcpClientAsync();
        });

        await using var viewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = port,
            Password = "password",
        });
        await Assert.ThrowsAsync<EndOfStreamException>(() => viewer.ConnectAsync());
        await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        incompletePeer.Stop();

        using var server = new VncServer(new ConnectionSettings
        {
            BindAddress = "127.0.0.1",
            Port = port,
            Password = "password",
            Width = 32,
            Height = 24,
        }, new SyntheticScreenCapture());
        server.Start();

        var frameReceived = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FramebufferUpdated += (_, args) => frameReceived.TrySetResult(args);
        await viewer.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var run = viewer.RunAsync(cancellation.Token);
        Assert.NotEmpty((await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Rectangles);

        cancellation.Cancel();
        await viewer.DisconnectAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForUpdateAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("Timed out waiting for a framebuffer update.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ChangingScreenCapture : IScreenCapture
    {
        private int _captures;

        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(width, height, PixelFormat.Standard32Argb8888);
            frame.Fill(20, 40, 70);
            if (Interlocked.Increment(ref _captures) >= 3)
            {
                frame.PixelData[0] = 90;
            }
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
            _events.Enqueue($"key:{keySym:x}:{pressed}");
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

        public void Dispose() => _changed.Dispose();
    }

    private static string FindProcessHost()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LucyVNC.slnx")))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            throw new DirectoryNotFoundException("Could not locate LucyVNC.slnx from the integration-test output directory.");
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        return Path.Combine(root.FullName, "tests", "Vnc.ProcessHost", "bin", configuration, "net8.0-windows", "Vnc.ProcessHost.exe");
    }

    private sealed class SyntheticScreenCapture : IScreenCapture
    {
        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(width, height, PixelFormat.Standard32Argb8888);
            frame.Fill(20, 40, 70);
            return frame;
        }

        public void Dispose()
        {
        }
    }

    private sealed class PatternScreenCapture : IScreenCapture
    {
        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(width, height, PixelFormat.Standard32Argb8888);
            CreateExpectedFrame(width, height).CopyTo(frame.PixelData, 0);
            return frame;
        }

        public void Dispose()
        {
        }

        public static byte[] CreateExpectedFrame(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * width + x) * 4;
                    pixels[offset] = (byte)x;
                    pixels[offset + 1] = (byte)y;
                    pixels[offset + 2] = (byte)(x + y);
                }
            }

            return pixels;
        }
    }

    private sealed class ColoredScreenCapture(byte red, byte green, byte blue) : IScreenCapture
    {
        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(width, height, PixelFormat.Standard32Argb8888);
            frame.Fill(red, green, blue);
            return frame;
        }

        public void Dispose()
        {
        }
    }
}
