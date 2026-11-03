using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vnc.App;
using Vnc.Core;
using Vnc.Platform.Windows;
using Vnc.Server;
using Vnc.Viewer;

namespace Vnc.Infrastructure.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiCollection : ICollectionFixture<WpfUiFixture>
{
    public const string Name = "WPF UI";
}

[Collection(WpfUiCollection.Name)]
public sealed class RemoteWorkspaceLifecycleTests
{
    private readonly WpfUiFixture _fixture;

    public RemoteWorkspaceLifecycleTests(WpfUiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClosingTheOnlyTabClosesWorkspaceAndAllowsRepeatedReopen()
    {
        var manager = await _fixture.InvokeAsync(() => new MainWindow());
        var managerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Closed += (_, _) => managerClosed.TrySetResult();
        var previousWorkspace = (RemoteWorkspaceWindow?)null;

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var (workspace, closed) = await CreateWorkspaceAsync(manager);
            Assert.NotSame(previousWorkspace, workspace);
            previousWorkspace = workspace;

            var tab = await AddSessionAsync(workspace, $"single-{cycle}");
            await CloseTabAsync(workspace, tab);
            await closed.WaitAsync(TimeSpan.FromSeconds(5));

            var state = await _fixture.InvokeAsync(() => new
            {
                IsVisible = workspace.IsVisible,
                ManagedWorkspace = GetManagedWorkspace(manager),
            });
            Assert.False(state.IsVisible);
            Assert.Null(state.ManagedWorkspace);
            Assert.False(managerClosed.Task.IsCompleted);
        }
    }

    [Fact]
    public async Task ClosingManagerAlsoClosesWorkspaceWithoutPrompting()
    {
        var manager = await _fixture.InvokeAsync(() => new MainWindow());
        var (workspace, workspaceClosed) = await CreateWorkspaceAsync(manager);
        await AddSessionAsync(workspace, "manager-shutdown");
        var managerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Closed += (_, _) => managerClosed.TrySetResult();

        await _fixture.InvokeAsync(manager.Close);

        await workspaceClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await managerClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await _fixture.InvokeAsync(() => workspace.IsVisible));
    }

    [Fact]
    public async Task EmptyWorkspaceCanBeClosedBeforeDeferredAutomaticCloseRuns()
    {
        var manager = await _fixture.InvokeAsync(() => new MainWindow());
        var (workspace, closed) = await CreateWorkspaceAsync(manager);
        var tab = await AddSessionAsync(workspace, "last-tab");

        await _fixture.InvokeAsync(() =>
        {
            workspace.RemoveSession(tab);
            workspace.Close();
        });

        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await _fixture.InvokeAsync(() => GetManagedWorkspace(manager)));
    }

    [Fact]
    public async Task OpeningWorkspaceWithoutASelectedSessionReturnsToComputerSelection()
    {
        var manager = await _fixture.InvokeAsync(() => new MainWindow());
        var openWorkspace = typeof(MainWindow).GetMethod(
            "OpenWorkspace_Click",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await _fixture.InvokeAsync(() =>
            openWorkspace.Invoke(manager, [manager, new RoutedEventArgs()]));

        var state = await _fixture.InvokeAsync(() => new
        {
            ManagedWorkspace = GetManagedWorkspace(manager),
            ComputerPage = GetWindowElement<Grid>(manager, "ComputerPage").Visibility,
            PageSubtitle = GetWindowElement<TextBlock>(manager, "PageSubtitle").Text,
        });
        Assert.Null(state.ManagedWorkspace);
        Assert.Equal(Visibility.Visible, state.ComputerPage);
        Assert.Equal("Selecione um destino ou conecte-se por IP ou hostname.", state.PageSubtitle);
    }

    [Fact]
    public async Task ClosingOneOfTwoTabsKeepsWorkspaceUntilTheLastTabCloses()
    {
        var manager = await _fixture.InvokeAsync(() => new MainWindow());
        var (workspace, closed) = await CreateWorkspaceAsync(manager);
        var first = await AddSessionAsync(workspace, "first");
        await _fixture.InvokeAsync(() => workspace.WindowState = WindowState.Maximized);
        var second = await AddSessionAsync(workspace, "second");

        Assert.Equal(WindowState.Maximized, await _fixture.InvokeAsync(() => workspace.WindowState));
        await CloseTabAsync(workspace, first);
        await Task.Delay(100);

        var state = await _fixture.InvokeAsync(() => (workspace.IsVisible, workspace.Tabs.Items.Count));
        Assert.True(state.IsVisible);
        Assert.Equal(1, state.Count);
        Assert.Same(second, await _fixture.InvokeAsync(() => workspace.Tabs.Items[0]));
        Assert.False(closed.IsCompleted);

        await CloseTabAsync(workspace, second);
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await _fixture.InvokeAsync(() => GetManagedWorkspace(manager)));
    }

    [Fact]
    public async Task DirectCloseCanBeCancelledAndDoesNotDisconnectSessions()
    {
        var manager = await _fixture.InvokeAsync(() => new MainWindow());
        var (workspace, closed) = await CreateWorkspaceAsync(manager);
        var tab = await AddSessionAsync(workspace, "keep-open");

        await _fixture.InvokeAsync(() =>
        {
            _fixture.ClickDialogButtonWhenShown("Não");
            workspace.Close();
        });
        await Task.Delay(250);

        var state = await _fixture.InvokeAsync(() => (workspace.IsVisible, workspace.Tabs.Items.Count));
        Assert.True(state.IsVisible);
        Assert.Equal(1, state.Count);
        Assert.False(closed.IsCompleted);

        await CloseTabAsync(workspace, tab);
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DirectCloseConfirmationClosesWorkspaceWithoutReentrantClose()
    {
        var manager = await _fixture.InvokeAsync(() => new MainWindow());
        var (workspace, closed) = await CreateWorkspaceAsync(manager);
        await AddSessionAsync(workspace, "close-confirmed");

        await _fixture.InvokeAsync(() =>
        {
            _fixture.ClickDialogButtonWhenShown("Sim");
            workspace.Close();
        });

        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await _fixture.InvokeAsync(() => GetManagedWorkspace(manager)));
    }

    [Fact]
    public void LifecycleSourceGuardsAgainstEmptyWindowCreationAndCloseReentrancy()
    {
        var managerSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiAssets", "MainWindow.xaml.cs"));
        var workspaceSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UiAssets", "RemoteWorkspaceWindow.xaml.cs"));

        Assert.Contains("if (_workspace is null || !_workspace.CanAcceptSessions)", managerSource, StringComparison.Ordinal);
        Assert.Contains("_workspace.ActivateWorkspace();", managerSource, StringComparison.Ordinal);
        Assert.Contains("if (_workspace is { CanAcceptSessions: true, HasSessions: true })", managerSource, StringComparison.Ordinal);
        Assert.Contains("if (state.CloseTask is not null)", managerSource, StringComparison.Ordinal);
        Assert.Contains("_ = CloseSessionAsync(sessionState, removeTab: true);", managerSource, StringComparison.Ordinal);
        Assert.Contains("await CloseSessionAsync(state, removeTab: true);", managerSource, StringComparison.Ordinal);
        Assert.Contains("_workspace = null;", managerSource, StringComparison.Ordinal);
        Assert.Contains("if (isEmpty && !_closingInProgress && !_allowClose && !_isClosed)", workspaceSource, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CloseIfEmpty));", workspaceSource, StringComparison.Ordinal);
        Assert.Contains("new Action(() => _ = ConfirmCloseAsync())", workspaceSource, StringComparison.Ordinal);
        Assert.Contains("if (!HasSessions)", workspaceSource, StringComparison.Ordinal);
        Assert.Contains("if (_allowClose || _isClosed)", workspaceSource, StringComparison.Ordinal);
        Assert.Contains("state.RenderQueue.Dispose();", managerSource, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Background", managerSource, StringComparison.Ordinal);
        Assert.Contains("AskToSaveComputer(state.Host, state.Port)", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait(", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".Result", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.Invoke(", managerSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sim", true)]
    [InlineData("Não", false)]
    public async Task SaveComputerPromptRespondsWhileFramebufferUpdatesAreCoalesced(string buttonText, bool expectedYes)
    {
        var manager = await _fixture.InvokeAsync(() =>
        {
            var window = new Window { Title = "Test owner" };
            window.Show();
            return window;
        });
        var renderCount = 0;
        var renderedDuringDialog = false;
        FramebufferRenderUpdate? lastUpdate = null;
        var queue = await _fixture.InvokeAsync(() => new FramebufferRenderQueue(
            Dispatcher.CurrentDispatcher,
            update =>
            {
                Interlocked.Increment(ref renderCount);
                renderedDuringDialog = Application.Current.Windows
                    .OfType<Window>()
                    .Any(window => window.Title == "Salvar computador" && window.IsVisible);
                lastUpdate = update;
            },
            exception => throw exception));

        _fixture.ClickDialogButtonWhenShown(
            "Salvar computador",
            buttonText,
            () =>
            {
                for (var index = 0; index < 250; index++)
                {
                    var value = (byte)index;
                    queue.Enqueue(new FramebufferUpdatedEventArgs(
                        8,
                        8,
                        [new FramebufferRectangle(
                            (ushort)(index % 8),
                            (ushort)(index / 8 % 8),
                            1,
                            1,
                            0,
                            [value, value, value, 0])]));
                }
            },
            TimeSpan.FromMilliseconds(50));

        var result = await _fixture.InvokeAsync(() =>
            LucyDialog.Show(
                manager,
                "Conexão autenticada com 127.0.0.1:5901. Salvar como computador conhecido?",
                "Salvar computador",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question));

        Assert.Equal(expectedYes ? MessageBoxResult.Yes : MessageBoxResult.No, result);
        Assert.True(renderedDuringDialog);
        Assert.Equal(1, renderCount);
        Assert.NotNull(lastUpdate);
        Assert.Equal((byte)249, lastUpdate!.Pixels[((7 * 8) + 1) * 4]);
        queue.Dispose();
        await _fixture.InvokeAsync(manager.Close);
    }

    [Fact]
    public async Task ClosingWorkspaceDisposesQueuedFramebufferWork()
    {
        var manager = await _fixture.InvokeAsync(() => new MainWindow());
        var (workspace, closed) = await CreateWorkspaceAsync(manager);
        await AddSessionAsync(workspace, "render-pending");
        var rendered = 0;
        var queue = await _fixture.InvokeAsync(() => new FramebufferRenderQueue(
            Dispatcher.CurrentDispatcher,
            _ => Interlocked.Increment(ref rendered),
            exception => throw exception));

        await _fixture.InvokeAsync(() =>
        {
            queue.Enqueue(new FramebufferUpdatedEventArgs(
                2,
                2,
                [new FramebufferRectangle(0, 0, 2, 2, 0, new byte[16])]));
            queue.Dispose();
            workspace.RemoveSession((TabItem)workspace.Tabs.Items[0]);
        });

        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Equal(0, rendered);
    }

    [Fact]
    public async Task AuthenticatedViewerKeepsDeliveringFramesWhileSaveComputerPromptIsOpen()
    {
        var manager = await _fixture.InvokeAsync(() => new MainWindow());
        var promptMethod = typeof(MainWindow).GetMethod(
            "AskToSaveComputer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var renderedDuringDialog = 0;
        var queue = await _fixture.InvokeAsync(() => new FramebufferRenderQueue(
            Dispatcher.CurrentDispatcher,
            _ =>
            {
                if (Application.Current.Windows
                    .OfType<Window>()
                    .Any(window => window.Title == "Salvar computador" && window.IsVisible))
                {
                    Interlocked.Increment(ref renderedDuringDialog);
                }
            },
            exception => throw exception));
        using var server = new VncServer(
            new ConnectionSettings
            {
                Port = 0,
                BindAddress = "127.0.0.1",
                Password = "testpass",
                Width = 32,
                Height = 24,
            },
            new WpfUiFixture.ChangingScreenCapture());
        server.Start();

        await using var viewer = new VncViewerSession(new ConnectionSettings
        {
            Host = "127.0.0.1",
            Port = server.BoundPort,
            Password = "testpass",
        });
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FramebufferUpdated += (_, update) =>
        {
            queue.Enqueue(update);
            firstFrame.TrySetResult();
        };
        await viewer.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var runTask = viewer.RunAsync(cancellation.Token);
        await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _fixture.ClickDialogButtonWhenShown(
            "Salvar computador",
            "Sim",
            delayBeforeClick: TimeSpan.FromMilliseconds(350));
        var result = await _fixture.InvokeAsync(() =>
            (MessageBoxResult)promptMethod.Invoke(manager, ["127.0.0.1", server.BoundPort])!);

        Assert.Equal(MessageBoxResult.Yes, result);
        Assert.True(renderedDuringDialog > 0);
        Assert.True(viewer.IsConnected);
        cancellation.Cancel();
        await viewer.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Dispose();
    }

    [Fact]
    public async Task DelayedFramebufferRenderCoalescesFramesThatArriveDuringRendering()
    {
        using var releaseFirstRender = new ManualResetEventSlim();
        var firstRenderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRenderCompleted = new TaskCompletionSource<FramebufferRenderUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderCount = 0;
        var queue = await _fixture.InvokeAsync(() => new FramebufferRenderQueue(
            Dispatcher.CurrentDispatcher,
            update =>
            {
                if (Interlocked.Increment(ref renderCount) == 1)
                {
                    firstRenderStarted.TrySetResult();
                    if (!releaseFirstRender.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Test did not release its first render.");
                    }
                }
                else
                {
                    secondRenderCompleted.TrySetResult(update);
                }
            },
            exception => throw exception));

        await _fixture.InvokeAsync(() => queue.Enqueue(new FramebufferUpdatedEventArgs(
            4,
            4,
            [new FramebufferRectangle(0, 0, 4, 4, 0, new byte[64])])));
        await firstRenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 200; index++)
        {
            var value = (byte)index;
            queue.Enqueue(new FramebufferUpdatedEventArgs(
                4,
                4,
                [new FramebufferRectangle(1, 1, 1, 1, 0, [value, value, value, 0])]));
        }

        releaseFirstRender.Set();
        var finalUpdate = await secondRenderCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, renderCount);
        Assert.Equal((byte)199, finalUpdate.Pixels[0]);
        queue.Dispose();
    }

    private async Task<(RemoteWorkspaceWindow Workspace, Task Closed)> CreateWorkspaceAsync(MainWindow manager)
    {
        return await _fixture.InvokeAsync(() =>
        {
            var workspace = new RemoteWorkspaceWindow(manager);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.Closed += (_, _) => closed.TrySetResult();
            SetManagedWorkspace(manager, workspace);
            return (workspace, closed.Task);
        });
    }

    private Task<TabItem> AddSessionAsync(RemoteWorkspaceWindow workspace, string title) =>
        _fixture.InvokeAsync(() =>
        {
            var tab = new TabItem { Content = new TextBlock { Text = title } };
            workspace.AddSession(tab, title, workspace.RemoveSession);
            return tab;
        });

    private Task CloseTabAsync(RemoteWorkspaceWindow workspace, TabItem tab) =>
        _fixture.InvokeAsync(() =>
        {
            var header = Assert.IsType<StackPanel>(tab.Header);
            var close = Assert.Single(header.Children.OfType<Button>());
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, close));
        });

    private static RemoteWorkspaceWindow? GetManagedWorkspace(MainWindow manager) =>
        (RemoteWorkspaceWindow?)typeof(MainWindow)
            .GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager);

    private static void SetManagedWorkspace(MainWindow manager, RemoteWorkspaceWindow workspace) =>
        typeof(MainWindow)
            .GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, workspace);

    private static T GetWindowElement<T>(MainWindow manager, string name) where T : FrameworkElement =>
        (T)typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
}

public sealed class WpfUiFixture : IDisposable
{
    private readonly Thread _uiThread;
    private readonly TaskCompletionSource<Dispatcher> _dispatcherReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Application? _application;

    public WpfUiFixture()
    {
        _uiThread = new Thread(() =>
        {
            var app = new global::Vnc.App.App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _application = app;
            _dispatcherReady.TrySetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "LucyVNC WPF lifecycle tests",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> callback) =>
        _dispatcherReady.Task.GetAwaiter().GetResult().InvokeAsync(callback).Task;

    public Task InvokeAsync(Action callback) =>
        InvokeAsync(() =>
        {
            callback();
            return true;
        });

    public void ClickDialogButtonWhenShown(string buttonText) =>
        ClickDialogButtonWhenShown("Encerrar sessões remotas", buttonText);

    public void ClickDialogButtonWhenShown(
        string dialogTitle,
        string buttonText,
        Action? beforeClick = null,
        TimeSpan? delayBeforeClick = null)
    {
        var dispatcher = _dispatcherReady.Task.GetAwaiter().GetResult();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (sender, _) =>
        {
            var dialog = _application!.Windows
                .OfType<Window>()
                .FirstOrDefault(window => string.Equals(window.Title, dialogTitle, StringComparison.Ordinal));
            if (dialog is null)
            {
                return;
            }

            var button = FindVisualChildren<Button>(dialog)
                .FirstOrDefault(candidate => string.Equals(candidate.Content as string, buttonText, StringComparison.Ordinal));
            if (button is null)
            {
                return;
            }

            ((DispatcherTimer)sender!).Stop();
            beforeClick?.Invoke();
            if (delayBeforeClick is { } delay && delay > TimeSpan.Zero)
            {
                var delayedClick = new DispatcherTimer(delay, DispatcherPriority.Background, (clickSender, _) =>
                {
                    ((DispatcherTimer)clickSender!).Stop();
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                }, dispatcher);
                delayedClick.Start();
            }
            else
            {
                dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button))));
            }
        }, dispatcher);
        timer.Start();
    }

    internal sealed class ChangingScreenCapture : IScreenCapture
    {
        private int _captureCount;

        public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
        {
            var frame = new ScreenFrame(
                width == 0 ? 32 : width,
                height == 0 ? 24 : height,
                Vnc.Core.PixelFormat.Standard32Argb8888);
            frame.Fill(30, 60, 90);
            frame.PixelData[0] = (byte)Interlocked.Increment(ref _captureCount);
            return frame;
        }

        public void Dispose()
        {
        }
    }

    public void Dispose()
    {
        if (_dispatcherReady.Task.IsCompletedSuccessfully)
        {
            _dispatcherReady.Task.Result.BeginInvokeShutdown(DispatcherPriority.Send);
            _uiThread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
