using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Server;
using Vnc.Viewer;

namespace Vnc.InteractiveProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var form = new ProbeForm();
        var manualMode = args.Contains("--manual", StringComparer.OrdinalIgnoreCase);
        form.Shown += async (_, _) =>
        {
            if (manualMode)
            {
                var server = new VncServer(
                    new ConnectionSettings
                    {
                        BindAddress = "127.0.0.1",
                        Port = 0,
                        Password = "lucytest",
                    },
                    remoteInput: new LoggingRemoteInput());
                server.Start();
                form.Tag = server;
                form.StartManualUpdates();
                Console.WriteLine($"READY:{server.BoundPort}:lucytest");
                var target = form.ClickTarget.PointToScreen(new Point(form.ClickTarget.Width / 2, form.ClickTarget.Height / 2));
                var bounds = Screen.PrimaryScreen?.Bounds
                    ?? throw new InvalidOperationException("Windows did not report a primary display.");
                Console.WriteLine($"TARGET:{target.X}:{target.Y}:{bounds.Left}:{bounds.Top}:{bounds.Width}:{bounds.Height}");
                _ = Task.Run(async () =>
                {
                    await Console.In.ReadLineAsync();
                    if (!form.IsDisposed)
                    {
                        form.BeginInvoke(form.Close);
                    }
                });
                return;
            }

            await RunProbeAsync(form);
        };
        form.FormClosed += (_, _) => (form.Tag as VncServer)?.Dispose();
        Application.Run(form);
        return Environment.ExitCode;
    }

    private static async Task RunProbeAsync(ProbeForm form)
    {
        try
        {
            form.Activate();
            form.Entry.Focus();
            var inputPoint = form.Entry.PointToScreen(new Point(form.Entry.Width / 2, form.Entry.Height / 2));
            var clickPoint = form.ClickTarget.PointToScreen(new Point(form.ClickTarget.Width / 2, form.ClickTarget.Height / 2));
            var screenBounds = Screen.PrimaryScreen?.Bounds
                ?? throw new InvalidOperationException("Windows did not report a primary display.");

            using var server = new VncServer(new ConnectionSettings
            {
                BindAddress = "127.0.0.1",
                Port = 0,
                Password = "lucytest",
            });
            server.Start();

            await using var viewer = new VncViewerSession(new ConnectionSettings
            {
                Host = "127.0.0.1",
                Port = server.BoundPort,
                Password = "lucytest",
            });
            var framebufferLock = new object();
            byte[]? lastFramebuffer = null;
            var verifyChangedFrame = 0;
            var frameReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changedFrameReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.FramebufferUpdated += (_, args) =>
            {
                if (args.Rectangles.Count > 0)
                {
                    frameReceived.TrySetResult();
                }

                var pixelsChanged = false;
                lock (framebufferLock)
                {
                    var framebuffer = lastFramebuffer ??= new byte[checked(args.Width * args.Height * 4)];
                    foreach (var rectangle in args.Rectangles)
                    {
                        var rowBytes = rectangle.Width * 4;
                        for (var row = 0; row < rectangle.Height; row++)
                        {
                            var sourceOffset = row * rowBytes;
                            var destinationOffset = ((rectangle.Y + row) * args.Width + rectangle.X) * 4;
                            pixelsChanged |= !framebuffer.AsSpan(destinationOffset, rowBytes)
                                .SequenceEqual(rectangle.Data.AsSpan(sourceOffset, rowBytes));
                            Buffer.BlockCopy(rectangle.Data, sourceOffset, framebuffer, destinationOffset, rowBytes);
                        }
                    }
                }

                if (pixelsChanged && Volatile.Read(ref verifyChangedFrame) != 0)
                {
                    changedFrameReceived.TrySetResult();
                }
            };

            var init = await viewer.ConnectAsync();
            var viewerRun = viewer.RunAsync();
            await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var inputX = ToRemoteCoordinate(inputPoint.X, screenBounds.Left, screenBounds.Width, init.Width);
            var inputY = ToRemoteCoordinate(inputPoint.Y, screenBounds.Top, screenBounds.Height, init.Height);
            await viewer.SendPointerEventAsync(inputX, inputY, 0);
            await WaitUntilAsync(() => IsCursorNear(inputPoint), "SendInput did not move the cursor to the test field.");

            await viewer.SendPointerEventAsync(inputX, inputY, 1);
            await viewer.SendPointerEventAsync(inputX, inputY, 0);
            Volatile.Write(ref verifyChangedFrame, 1);
            await SendKeyAsync(viewer, 'l');
            await SendKeyAsync(viewer, 'u');
            await SendKeyAsync(viewer, 'c');
            await SendKeyAsync(viewer, 'y');
            await WaitUntilAsync(() => form.Entry.Text == "lucy", "SendInput did not type the expected safe test text.");
            await changedFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            form.Entry.Clear();
            await viewer.SendPointerEventAsync(inputX, inputY, 1);
            await viewer.SendPointerEventAsync(inputX, inputY, 0);
            await viewer.SendKeyEventAsync(0xffe1, true);
            await SendKeyAsync(viewer, 'a');
            await viewer.SendKeyEventAsync(0xffe1, false);
            await SendKeyAsync(viewer, 'b');
            await SendKeyAsync(viewer, 0xff50);
            await SendKeyAsync(viewer, 0xffff);
            await SendKeyAsync(viewer, 0xff51);
            await SendKeyAsync(viewer, 0xff53);
            await SendKeyAsync(viewer, 0xff08);
            await WaitUntilAsync(() => form.Entry.Text.Length == 0, "Shift, Home, Delete, arrow, or Backspace did not behave as expected.");

            var clickX = ToRemoteCoordinate(clickPoint.X, screenBounds.Left, screenBounds.Width, init.Width);
            var clickY = ToRemoteCoordinate(clickPoint.Y, screenBounds.Top, screenBounds.Height, init.Height);
            await viewer.SendPointerEventAsync(clickX, clickY, 0);
            await WaitUntilAsync(() => IsCursorNear(clickPoint), "SendInput did not move the cursor to the test button.");
            await viewer.SendPointerEventAsync(clickX, clickY, 1);
            await WaitUntilAsync(() => form.MouseDownCount == 1, "The test button did not receive mouse-down.");
            await viewer.SendPointerEventAsync(clickX, clickY, 0);
            await WaitUntilAsync(() => form.MouseUpCount == 1, "The test button did not receive mouse-up.");
            await WaitUntilAsync(() => form.ClickCount == 1, "The test button did not receive a mouse click.");

            form.Entry.Focus();
            await viewer.SendKeyEventAsync(0xffe1, true);
            await server.StopAsync();
            await viewerRun.WaitAsync(TimeSpan.FromSeconds(5));

            form.Entry.Clear();
            form.Activate();
            server.Start();
            form.Entry.Focus();
            await using var reconnectedViewer = new VncViewerSession(new ConnectionSettings
            {
                Host = "127.0.0.1",
                Port = server.BoundPort,
                Password = "lucytest",
            });
            var reconnectFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reconnectedViewer.FramebufferUpdated += (_, args) =>
            {
                if (args.Rectangles.Count > 0)
                {
                    reconnectFrame.TrySetResult();
                }
            };
            await reconnectedViewer.ConnectAsync();
            var reconnectedRun = reconnectedViewer.RunAsync();
            await reconnectFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await SendKeyAsync(reconnectedViewer, 'b');
            await WaitUntilAsync(() => form.Entry.Text == "b", "A disconnected Shift key remained pressed.");
            await reconnectedViewer.DisconnectAsync();
            await reconnectedRun.WaitAsync(TimeSpan.FromSeconds(5));

            Console.WriteLine("PASS: live framebuffer, changed pixels, pointer movement, left click, text entry, Shift/Left/Backspace, key release, and reconnect.");
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            Environment.ExitCode = 1;
        }
        finally
        {
            form.Close();
        }
    }

    private static async Task SendKeyAsync(VncViewerSession viewer, uint keySym)
    {
        await viewer.SendKeyEventAsync(keySym, true);
        await viewer.SendKeyEventAsync(keySym, false);
    }

    private static ushort ToRemoteCoordinate(int screenCoordinate, int screenOrigin, int screenLength, ushort framebufferLength)
    {
        var localCoordinate = Math.Clamp(screenCoordinate - screenOrigin, 0, screenLength - 1);
        return (ushort)((long)localCoordinate * (framebufferLength - 1) / Math.Max(1, screenLength - 1));
    }

    private static bool IsCursorNear(Point expected)
    {
        if (!GetCursorPos(out var cursor))
        {
            throw new InvalidOperationException("Could not read the Windows cursor position.");
        }

        return Math.Abs(cursor.X - expected.X) <= 2 && Math.Abs(cursor.Y - expected.Y) <= 2;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(3))
            {
                throw new TimeoutException(failureMessage);
            }

            await Task.Delay(20);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    private sealed class LoggingRemoteInput : IRemoteInput
    {
        private readonly SendInputController _input = new();

        public void SetFramebufferSize(ushort width, ushort height) => _input.SetFramebufferSize(width, height);

        public void ApplyKeyEvent(uint keySym, bool pressed)
        {
            Console.WriteLine($"KEY:{keySym:X}:{pressed}");
            _input.ApplyKeyEvent(keySym, pressed);
        }

        public void ApplyPointerEvent(ushort x, ushort y, byte buttonMask)
        {
            Console.WriteLine($"POINTER:{x}:{y}:{buttonMask}");
            _input.ApplyPointerEvent(x, y, buttonMask);
        }

        public void ReleaseAll() => _input.ReleaseAll();

        public void Dispose() => _input.Dispose();
    }

    private sealed class ProbeForm : Form
    {
        public ProbeForm()
        {
            Text = "LucyVNC SendInput Interactive Test";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(50, 50);
            Size = new Size(540, 300);
            TopMost = true;
            KeyPreview = true;

            ManualStatusLabel = new Label
            {
                Location = new Point(24, 188),
                AutoSize = true,
                Text = "Image update probe",
            };
            Entry = new TextBox
            {
                Location = new Point(24, 58),
                Size = new Size(450, 34),
                Multiline = true,
                Font = new Font(FontFamily.GenericSansSerif, 16),
            };
            Entry.TextChanged += (_, _) => ManualStatusLabel.Text = $"Test input: {Entry.Text}";
            ClickTarget = new Button
            {
                Location = new Point(24, 120),
                Size = new Size(220, 48),
                Text = "Safe test click",
                TabStop = false,
            };
            ClickTarget.Click += (_, _) =>
            {
                ClickCount++;
                ClickTarget.Text = $"Safe test click: {ClickCount}";
            };
            ClickTarget.MouseDown += (_, _) => MouseDownCount++;
            ClickTarget.MouseUp += (_, _) => MouseUpCount++;

            Controls.Add(new Label
            {
                Location = new Point(24, 22),
                AutoSize = true,
                Text = "Dedicated safe test window: text stays in this temporary field.",
            });
            Controls.Add(Entry);
            Controls.Add(ClickTarget);
            Controls.Add(ManualStatusLabel);
        }

        public TextBox Entry { get; }
        public Button ClickTarget { get; }
        public Label ManualStatusLabel { get; }
        public int ClickCount { get; set; }
        public int MouseDownCount { get; set; }
        public int MouseUpCount { get; set; }

        public void StartManualUpdates()
        {
            var timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (_, _) =>
            {
                ManualStatusLabel.Text = $"Framebuffer update probe: {DateTime.Now:HH:mm:ss.fff}";
            };
            timer.Start();
        }
    }
}
