using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Vnc.App;

public partial class RemoteWorkspaceWindow : Window
{
    private readonly MainWindow _manager;
    private bool _allowClose;
    private bool _closingInProgress;
    private WindowStyle _previousWindowStyle;
    private WindowState _previousWindowState;
    private bool _isFullScreen;

    public RemoteWorkspaceWindow(MainWindow manager)
    {
        _manager = manager;
        InitializeComponent();
        DarkWindowChrome.Apply(this);
        PreviewKeyDown += (_, e) => _manager.HandleWorkspaceKeyDown(e);
        PreviewKeyUp += (_, e) => _manager.HandleWorkspaceKeyUp(e);
        Closing += RemoteWorkspaceWindow_Closing;
        Closed += (_, _) => _manager.WorkspaceWindowClosed(this);
    }

    public TabControl Tabs => SessionTabs;

    public void AddSession(TabItem tab, string title, Action<TabItem> closeRequested)
    {
        if (!SessionTabs.Items.Contains(tab))
        {
            tab.Header = CreateHeader(tab, title, closeRequested);
            tab.ToolTip = title;
            SessionTabs.Items.Add(tab);
        }

        SessionTabs.SelectedItem = tab;
        EmptyState.Visibility = Visibility.Collapsed;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void RemoveSession(TabItem tab)
    {
        SessionTabs.Items.Remove(tab);
        EmptyState.Visibility = SessionTabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SelectSession(TabItem tab)
    {
        if (SessionTabs.Items.Contains(tab))
        {
            SessionTabs.SelectedItem = tab;
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }
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

    private static UIElement CreateHeader(TabItem tab, string title, Action<TabItem> closeRequested)
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
        close.Click += (_, _) => closeRequested(tab);
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

    private void RemoteWorkspaceWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closingInProgress)
        {
            return;
        }

        _closingInProgress = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        try
        {
            if (SessionTabs.Items.Count > 0 &&
                MessageBox.Show(
                    this,
                    "Fechar a janela encerrará todas as conexões remotas abertas. Deseja continuar?",
                    "Encerrar sessões remotas",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            await _manager.CloseAllWorkspaceSessionsAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Não foi possível encerrar as sessões", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _closingInProgress = false;
        }
    }
}
