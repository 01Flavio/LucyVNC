using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
            SessionTabs.Items.Add(tab);
        }

        SessionTabs.SelectedItem = tab;
        EmptyState.Visibility = Visibility.Collapsed;
        Show();
        WindowState = WindowState.Normal;
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
            WindowState = WindowState.Normal;
            Activate();
        }
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
        header.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        var close = new Button
        {
            Content = "×",
            Padding = new Thickness(5, 0, 5, 1),
            MinWidth = 22,
            Margin = new Thickness(0),
            Tag = tab,
            ToolTip = "Fechar esta sessão",
        };
        close.Click += (_, _) => closeRequested(tab);
        header.Children.Add(close);
        return header;
    }

    private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionTabs.SelectedItem is TabItem selected)
        {
            _manager.WorkspaceSessionSelected(selected);
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
