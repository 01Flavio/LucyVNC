using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Vnc.Agent;
using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Service;
using Vnc.Viewer;

namespace Vnc.Service.Tests;

public class ServiceWorkerTests
{
    [Fact]
    public async Task WorkerAndAgentProcessesServeFramebufferAndInputToViewer()
    {
        var root = FindRepositoryRoot();
        var processHostPath = Path.Combine(root, "tests", "Vnc.ProcessHost", "bin", "Release", "net8.0-windows", "Vnc.ProcessHost.exe");
        var workerPath = Path.Combine(root, "src", "Vnc.Service", "bin", "Release", "net8.0-windows", "Vnc.Service.exe");
        Assert.True(File.Exists(processHostPath), $"Process host was not built: {processHostPath}");
        Assert.True(File.Exists(workerPath), $"Worker was not built: {workerPath}");

        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The test requires a Windows user SID.");
        var pipeName = $"LucyVNCProcessTest_{Guid.NewGuid():N}";
        var configPath = Path.Combine(Path.GetTempPath(), $"LucyVNC-{Guid.NewGuid():N}", "service.json");
        var store = new ServiceConfigurationStore(configPath);
        var port = GetAvailableLoopbackPort();
        ChildProcess? agent = null;
        ChildProcess? worker = null;

        try
        {
            store.Save(ServiceConfiguration.SafeDefaults with
            {
                RemoteSharingEnabled = true,
                BindAddress = IPAddress.Loopback.ToString(),
                Port = port,
            });

            worker = ChildProcess.Start(
                workerPath,
                [ServiceHostBuilder.ConsoleModeArgument],
                new Dictionary<string, string?>
                {
                    [ServiceHostBuilder.ConfigurationPathEnvironmentVariable] = configPath,
                    [ServiceWorker.VncPasswordEnvironmentVariable] = "testpass",
                    [ServiceHostBuilder.AgentSidEnvironmentVariable] = sid,
                    [ServiceHostBuilder.PipeNameEnvironmentVariable] = pipeName,
                });
            await worker.WaitForLineAsync("READY:WORKER", TimeSpan.FromSeconds(15));
            await worker.WaitForLineContainingAsync(
                "sharing stopped after an IPC operation timed out",
                occurrence: 1,
                timeout: TimeSpan.FromSeconds(15));
            using (var absentAgentProbe = new TcpClient())
            {
                await Assert.ThrowsAnyAsync<SocketException>(async () =>
                    await absentAgentProbe.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3)));
            }

            agent = ChildProcess.Start(processHostPath, ["agent", pipeName, sid]);
            await agent.WaitForLineAsync("READY:AGENT", TimeSpan.FromSeconds(10));
            await worker.WaitForLineContainingAsync(
                "authenticated RFB listener is ready",
                occurrence: 1,
                timeout: TimeSpan.FromSeconds(20));

            await using var viewer = new VncViewerSession(new ConnectionSettings
            {
                Host = IPAddress.Loopback.ToString(),
                Port = port,
                Password = "testpass",
            });
            await viewer.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var frameReceived = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.FramebufferUpdated += (_, args) => frameReceived.TrySetResult(args);
            using var runCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var receiveTask = viewer.RunAsync(runCancellation.Token);
            var frame = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(64, frame.Width);
            Assert.Equal(48, frame.Height);

            await viewer.SendKeyEventAsync('a', true).WaitAsync(TimeSpan.FromSeconds(5));
            await viewer.SendKeyEventAsync('a', false).WaitAsync(TimeSpan.FromSeconds(5));
            await viewer.SendPointerEventAsync(7, 9, 1).WaitAsync(TimeSpan.FromSeconds(5));
            await viewer.SendPointerEventAsync(7, 9, 0).WaitAsync(TimeSpan.FromSeconds(5));
            await agent.WaitForLineAsync("INPUT:KEY:97:True", TimeSpan.FromSeconds(5));
            await agent.WaitForLineAsync("INPUT:POINTER:7:9:1", TimeSpan.FromSeconds(5));

            await viewer.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            runCancellation.Cancel();
            await receiveTask.WaitAsync(TimeSpan.FromSeconds(5));

            await agent.StopAsync(TimeSpan.FromSeconds(10));
            agent.Dispose();
            agent = null;
            await worker.WaitForLineContainingAsync(
                "sharing stopped because the authenticated interactive agent is unavailable",
                occurrence: 1,
                timeout: TimeSpan.FromSeconds(15));

            agent = ChildProcess.Start(processHostPath, ["agent", pipeName, sid]);
            await agent.WaitForLineAsync("READY:AGENT", TimeSpan.FromSeconds(10));
            await worker.WaitForLineContainingAsync(
                "authenticated RFB listener is ready",
                occurrence: 2,
                timeout: TimeSpan.FromSeconds(20));

            await using var reconnectedViewer = new VncViewerSession(new ConnectionSettings
            {
                Host = IPAddress.Loopback.ToString(),
                Port = port,
                Password = "testpass",
            });
            await reconnectedViewer.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var reconnectedFrame = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            reconnectedViewer.FramebufferUpdated += (_, args) => reconnectedFrame.TrySetResult(args);
            using var reconnectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reconnectTask = reconnectedViewer.RunAsync(reconnectCancellation.Token);
            Assert.Equal(64, (await reconnectedFrame.Task.WaitAsync(TimeSpan.FromSeconds(10))).Width);
            await reconnectedViewer.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            reconnectCancellation.Cancel();
            await reconnectTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try
            {
                if (worker is not null)
                {
                    await worker.StopAsync(TimeSpan.FromSeconds(15));
                }
            }
            finally
            {
                worker?.Dispose();

                try
                {
                    if (agent is not null)
                    {
                        await agent.StopAsync(TimeSpan.FromSeconds(10));
                    }
                }
                finally
                {
                    agent?.Dispose();

                    var configDirectory = Path.GetDirectoryName(configPath)!;
                    if (Directory.Exists(configDirectory))
                    {
                        Directory.Delete(configDirectory, recursive: true);
                    }
                }
            }
        }
    }

    [Fact]
    public async Task WorkerRequiresAuthenticatedAgentAndServesViewerOverLoopback()
    {
        var oldPassword = Environment.GetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable);
        var oldAgentSid = Environment.GetEnvironmentVariable(ServiceHostBuilder.AgentSidEnvironmentVariable);
        var oldPipeName = Environment.GetEnvironmentVariable(ServiceHostBuilder.PipeNameEnvironmentVariable);
        var pipeName = $"LucyVNCWorker_{Guid.NewGuid():N}";
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The test requires a Windows user SID.");
        var input = new RecordingInput();
        using var agent = new LucySessionAgent(new FakeScreenCapture(), input, sid, pipeName);
        var configPath = Path.Combine(Path.GetTempPath(), $"LucyVNC-{Guid.NewGuid():N}", "service.json");
        var store = new ServiceConfigurationStore(configPath);
        var port = GetAvailableLoopbackPort();
        ServiceWorker? worker = null;
        var agentStarted = false;

        Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, "testpass");
        Environment.SetEnvironmentVariable(ServiceHostBuilder.AgentSidEnvironmentVariable, sid);
        Environment.SetEnvironmentVariable(ServiceHostBuilder.PipeNameEnvironmentVariable, pipeName);
        try
        {
            store.Save(ServiceConfiguration.SafeDefaults with
            {
                RemoteSharingEnabled = true,
                BindAddress = IPAddress.Loopback.ToString(),
                Port = port,
            });
            await agent.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            agentStarted = true;

            worker = new ServiceWorker(NullLogger<ServiceWorker>.Instance, store);
            await worker.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            await using var viewer = new VncViewerSession(new ConnectionSettings
            {
                Host = IPAddress.Loopback.ToString(),
                Port = port,
                Password = "testpass",
            });
            await viewer.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var frameReceived = new TaskCompletionSource<FramebufferUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.FramebufferUpdated += (_, args) => frameReceived.TrySetResult(args);
            using var runCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var receiveTask = viewer.RunAsync(runCancellation.Token);
            var frame = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(64, frame.Width);
            Assert.Equal(48, frame.Height);

            await viewer.SendKeyEventAsync('a', true).WaitAsync(TimeSpan.FromSeconds(5));
            await viewer.SendKeyEventAsync('a', false).WaitAsync(TimeSpan.FromSeconds(5));
            await viewer.SendPointerEventAsync(7, 9, 1).WaitAsync(TimeSpan.FromSeconds(5));
            await viewer.SendPointerEventAsync(7, 9, 0).WaitAsync(TimeSpan.FromSeconds(5));
            await input.WaitForEventsAsync(4).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("key:97:True", input.Events);
            Assert.Contains("pointer:7:9:1", input.Events);

            await viewer.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            runCancellation.Cancel();
            await receiveTask.WaitAsync(TimeSpan.FromSeconds(5));
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            worker = null;
            Assert.True(input.ReleaseAllCount > 0);
        }
        finally
        {
            if (worker is not null)
            {
                await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }

            if (agentStarted)
            {
                await agent.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, oldPassword);
            Environment.SetEnvironmentVariable(ServiceHostBuilder.AgentSidEnvironmentVariable, oldAgentSid);
            Environment.SetEnvironmentVariable(ServiceHostBuilder.PipeNameEnvironmentVariable, oldPipeName);
            if (Directory.Exists(Path.GetDirectoryName(configPath)))
            {
                Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveVncPasswordAcceptsExactlyEightAsciiCharacters()
    {
        Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, "password", EnvironmentVariableTarget.Process);
        try
        {
            Assert.Equal("password", ServiceWorker.ResolveVncPassword());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, null, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void ResolveVncPasswordRejectsMissingOrInvalidValues()
    {
        Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, string.Empty, EnvironmentVariableTarget.Process);
        Assert.Null(ServiceWorker.ResolveVncPassword());

        Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, "short", EnvironmentVariableTarget.Process);
        Assert.Null(ServiceWorker.ResolveVncPassword());

        Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, "123456789", EnvironmentVariableTarget.Process);
        Assert.Null(ServiceWorker.ResolveVncPassword());
    }

    private static int GetAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LucyVNC.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate LucyVNC.slnx from the service test output directory.");
    }

    private sealed class ChildProcess : IDisposable
    {
        private readonly Process _process;
        private readonly string _label;
        private readonly ConcurrentQueue<string> _output = new();
        private readonly SemaphoreSlim _outputChanged = new(0);

        private ChildProcess(Process process, string label)
        {
            _process = process;
            _label = label;
            _process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    _output.Enqueue(args.Data);
                    _outputChanged.Release();
                }
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    _output.Enqueue($"STDERR: {args.Data}");
                    _outputChanged.Release();
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public static ChildProcess Start(
            string executablePath,
            IEnumerable<string> arguments,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (environment is not null)
            {
                foreach (var (name, value) in environment)
                {
                    startInfo.Environment[name] = value;
                }
            }

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start process {executablePath}.");
            var executableName = Path.GetFileNameWithoutExtension(executablePath);
            var label = executableName switch
            {
                "Vnc.Service" => "WORKER",
                "Vnc.ProcessHost" when startInfo.ArgumentList.FirstOrDefault() == "agent" => "AGENT",
                "Vnc.ProcessHost" => "SERVER",
                _ => executableName,
            };

            return new ChildProcess(process, label);
        }

        public async Task WaitForLineAsync(string expectedLine, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (!_output.Contains(expectedLine))
            {
                await _outputChanged.WaitAsync(cancellation.Token);
            }
        }

        public async Task WaitForLineContainingAsync(string text, int occurrence, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (_output.Count(line => line.Contains(text, StringComparison.Ordinal)) < occurrence)
            {
                await _outputChanged.WaitAsync(cancellation.Token);
            }
        }

        public async Task StopAsync(TimeSpan timeout)
        {
            if (_process.HasExited)
            {
                return;
            }

            try
            {
                await _process.StandardInput.WriteLineAsync("STOP");
                await _process.StandardInput.FlushAsync();
                await _process.WaitForExitAsync().WaitAsync(timeout);
                try
                {
                    await WaitForLineAsync($"STOPPED:{_label}", TimeSpan.FromSeconds(2));
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Process {_label} (PID {_process.Id}) exited without completing its graceful shutdown. Output:{Environment.NewLine}{string.Join(Environment.NewLine, _output)}");
                }
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Process {_label} (PID {_process.Id}) did not stop within {timeout}. Output:{Environment.NewLine}{string.Join(Environment.NewLine, _output)}",
                    exception);
            }

            if (_process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Process {_process.Id} exited with code {_process.ExitCode}: {string.Join(Environment.NewLine, _output)}");
            }
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }

            _outputChanged.Dispose();
            _process.Dispose();
        }
    }

    private sealed class FakeScreenCapture : IScreenCapture, IScreenCaptureBounds
    {
        public (int Width, int Height) GetPrimaryScreenSize() => (64, 48);

        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(width == 0 ? 64 : width, height == 0 ? 48 : height, PixelFormat.Standard32Argb8888);
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
        private int _releaseAllCount;

        public IReadOnlyCollection<string> Events => _events.ToArray();
        public int ReleaseAllCount => Volatile.Read(ref _releaseAllCount);

        public void SetFramebufferSize(ushort width, ushort height)
        {
        }

        public void ApplyKeyEvent(uint keySym, bool pressed)
        {
            Record($"key:{keySym}:{pressed}");
        }

        public void ApplyPointerEvent(ushort x, ushort y, byte buttonMask)
        {
            Record($"pointer:{x}:{y}:{buttonMask}");
        }

        public void ReleaseAll()
        {
            Interlocked.Increment(ref _releaseAllCount);
            Record("release");
        }

        public async Task WaitForEventsAsync(int count)
        {
            while (_events.Count < count)
            {
                await _changed.WaitAsync();
            }
        }

        private void Record(string value)
        {
            _events.Enqueue(value);
            _changed.Release();
        }

        public void Dispose() => _changed.Dispose();
    }
}
