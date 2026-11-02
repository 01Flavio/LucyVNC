using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Vnc.App;

public partial class RemoteWorkspaceWindow : Window
{
    private readonly MainWindow _manager;
    private readonly Dictionary<TabItem, RoutedEventHandler> _closeHandlers = new();
    private bool _allowClose;
    private bool _closingInProgress;
    private bool _emptyClosePending;
    private bool _isClosed;
    private WindowStyle _previousWindowStyle;
    private WindowState _previousWindowState;
    private bool _isFullScreen;

    public RemoteWorkspaceWindow(MainWindow manager)
    {
        _manager = manager;
        InitializeComponent();
        DarkWindowChrome.Apply(this);
        PreviewKeyDown += Manager_PreviewKeyDown;
        PreviewKeyUp += Manager_PreviewKeyUp;
        Closing += RemoteWorkspaceWindow_Closing;
        Closed += RemoteWorkspaceWindow_Closed;
    }

    public TabControl Tabs => SessionTabs;
    public bool HasSessions => SessionTabs.Items.Count > 0;
    public bool CanAcceptSessions => !_allowClose && !_closingInProgress && !_isClosed;

    public void AddSession(TabItem tab, string title, Action<TabItem> closeRequested)
    {
        ObjectDisposedException.ThrowIf(!CanAcceptSessions, this);

        if (!SessionTabs.Items.Contains(tab))
        {
            _emptyClosePending = false;
            RoutedEventHandler closeHandler = (_, _) => closeRequested(tab);
            tab.Header = CreateHeader(tab, title, closeHandler);
            tab.ToolTip = title;
            _closeHandlers.Add(tab, closeHandler);
            SessionTabs.Items.Add(tab);
        }

        SessionTabs.SelectedItem = tab;
        EmptyState.Visibility = Visibility.Collapsed;
        ActivateWorkspace();
    }

    public void RemoveSession(TabItem tab)
    {
        if (!SessionTabs.Items.Contains(tab))
        {
            return;
        }

        SessionTabs.Items.Remove(tab);
        if (_closeHandlers.Remove(tab, out var closeHandler) &&
            tab.Header is Panel header &&
            header.Children.OfType<Button>().FirstOrDefault() is { } closeButton)
        {
            closeButton.Click -= closeHandler;
        }

        var isEmpty = !HasSessions;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (isEmpty && !_closingInProgress && !_allowClose && !_isClosed)
        {
            QueueCloseWhenEmpty();
        }
    }

    public void SelectSession(TabItem tab)
    {
        if (CanAcceptSessions && SessionTabs.Items.Contains(tab))
        {
            SessionTabs.SelectedItem = tab;
            ActivateWorkspace();
        }
    }

    public void ActivateWorkspace()
    {
        if (!CanAcceptSessions || !HasSessions)
        {
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    internal sealed class ConnectionStateBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var resource = value is bool connected
                ? connected ? "Success" : "Error"
                : "TextSecondary";
            return (Brush)Application.Current.FindResource(resource);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public void CloseFromManager()
    {
        if (_allowClose || _isClosed)
        {
            return;
        }

        _emptyClosePending = false;
        _allowClose = true;
        Close();
    }

    public void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        if (_isFullScreen)
        {
            _previousWindowStyle = WindowStyle;
            _previousWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _previousWindowStyle;
            WindowState = _previousWindowState;
        }
    }

    private void QueueCloseWhenEmpty()
    {
        _emptyClosePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CloseIfEmpty));
    }

    private void CloseIfEmpty()
    {
        if (!_emptyClosePending)
        {
            return;
        }

        _emptyClosePending = false;
        if (!CanAcceptSessions || HasSessions)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private static UIElement CreateHeader(TabItem tab, string title, RoutedEventHandler closeHandler)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var status = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = (Brush)Application.Current.FindResource("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        status.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Tag.IsConnected")
        {
            Source = tab,
            Converter = new ConnectionStateBrushConverter(),
        });
        header.Children.Add(status);
        header.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
            MaxWidth = 210,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var close = new Button
        {
            Content = new Path
            {
                Data = (Geometry)Application.Current.FindResource("IconClose"),
                Width = 11,
                Height = 11,
                Fill = (Brush)Application.Current.FindResource("TextSecondary"),
                Stretch = Stretch.Uniform,
            },
            Style = (Style)Application.Current.FindResource("IconButton"),
            Padding = new Thickness(5),
            MinWidth = 24,
            MinHeight = 24,
            Margin = new Thickness(0),
            Tag = tab,
            ToolTip = "Fechar esta sessão",
        };
        System.Windows.Automation.AutomationProperties.SetName(close, $"Fechar sessão {title}");
        close.Click += closeHandler;
        header.Children.Add(close);
        return header;
    }

    private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionTabs.SelectedItem is TabItem selected)
        {
            Title = $"LucyVNC — {selected.ToolTip as string ?? "Sessão remota"}";
            _manager.WorkspaceSessionSelected(selected);
        }
        else
        {
            Title = "LucyVNC — Sessões remotas";
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Manager_PreviewKeyDown(object sender, KeyEventArgs e) => _manager.HandleWorkspaceKeyDown(e);

    private void Manager_PreviewKeyUp(object sender, KeyEventArgs e) => _manager.HandleWorkspaceKeyUp(e);

    private void RemoteWorkspaceWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _emptyClosePending = false;
        PreviewKeyDown -= Manager_PreviewKeyDown;
        PreviewKeyUp -= Manager_PreviewKeyUp;
        Closing -= RemoteWorkspaceWindow_Closing;
        Closed -= RemoteWorkspaceWindow_Closed;
        foreach (var (tab, closeHandler) in _closeHandlers)
        {
            if (tab.Header is Panel header && header.Children.OfType<Button>().FirstOrDefault() is { } closeButton)
            {
                closeButton.Click -= closeHandler;
            }
        }

        _closeHandlers.Clear();
        _manager.WorkspaceWindowClosed(this);
    }

    private void RemoteWorkspaceWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _isClosed)
        {
            return;
        }

        if (!HasSessions)
        {
            _emptyClosePending = false;
            _allowClose = true;
            return;
        }

        e.Cancel = true;
        if (_closingInProgress)
        {
            return;
        }

        _closingInProgress = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => _ = ConfirmCloseAsync()));
    }

    private async Task ConfirmCloseAsync()
    {
        try
        {
            if (!HasSessions)
            {
                CloseFromManager();
                return;
            }

            if (MessageBox.Show(
                    this,
                    "Fechar a janela encerrará todas as conexões remotas abertas. Deseja continuar?",
                    "Encerrar sessões remotas",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            await Dispatcher.InvokeAsync(
                    _manager.CloseAllWorkspaceSessionsAsync,
                    DispatcherPriority.Background)
                .Task
                .Unwrap();
            CloseFromManager();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Não foi possível encerrar as sessões", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _closingInProgress = false;
            if (!_allowClose && !HasSessions && !_isClosed)
            {
                QueueCloseWhenEmpty();
            }
        }
    }
}
