using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Vnc.Core;
using Vnc.Infrastructure;
using Vnc.Platform.Windows;
using Vnc.Server;
using Vnc.Viewer;

namespace Vnc.App;

public partial class MainWindow : Window
{
    private readonly LocalComputerStore _store = LocalComputerStore.CreateDefault();
    private readonly ICredentialVault _credentialVault = new WindowsCredentialVault();
    private readonly Dictionary<Guid, ViewerTabState> _sessions = new();
    private readonly HashSet<string> _connectingEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private RemoteWorkspaceWindow? _workspace;
    private VncServer? _server;
    private Guid? _editingComputerId;
    private string _currentPage = "computers";
    private bool _storeReady;
    private bool _refreshingGroups;
    private bool _allowClose;
    private bool _isShuttingDown;

    public MainWindow()
    {
        InitializeComponent();
        DarkWindowChrome.Apply(this);
        UpdateNavigationSelection(_currentPage);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _store.InitializeAsync();
            _storeReady = true;
            DatabaseStatus.Text = "Banco SQLite inicializado; senhas consentidas ficam no Windows Credential Manager.";
            var savedSort = await _store.GetPreferenceAsync("computer-sort");
            var sortItem = SortBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, savedSort));
            if (sortItem is not null)
            {
                SortBox.SelectedItem = sortItem;
            }

            await RefreshComputersAsync();
        }
        catch (Exception exception)
        {
            DatabaseStatus.Text = $"Falha ao inicializar o banco: {exception.Message}";
            MessageBox.Show(this, exception.Message, "Falha no banco local", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string page })
        {
            return;
        }

        _currentPage = page;
        UpdateNavigationSelection(page);
        ComputerPage.Visibility = page is "computers" or "favorites" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "recent" ? Visibility.Visible : Visibility.Collapsed;
        AccessPage.Visibility = page == "access" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        SessionsPage.Visibility = page == "sessions" ? Visibility.Visible : Visibility.Collapsed;

        PageTitle.Text = page switch
        {
            "recent" => "Recentes",
            "favorites" => "Favoritos",
            "access" => "Acessos",
            "settings" => "Configurações",
            "sessions" => "Sessões remotas",
            _ => "Computadores",
        };
        PageSubtitle.Text = page switch
        {
            "recent" => "Histórico de conexões autenticadas.",
            "favorites" => "Acesso rápido aos computadores favoritos.",
            "access" => "Compartilhamento nesta sessão e estado do serviço.",
            "settings" => "Preferências e segurança locais.",
            "sessions" => "Cada conexão mantém seu próprio ciclo de vida.",
            _ => "Gerencie destinos e abra sessões independentes.",
        };
        SearchContainer.Visibility = page is "computers" or "favorites" ? Visibility.Visible : Visibility.Collapsed;
        await RefreshCurrentPageAsync();
    }

    private void UpdateNavigationSelection(string selectedPage)
    {
        var navigation = new[]
        {
            ComputersNavigation, RecentNavigation, FavoritesNavigation,
            AccessNavigation, SettingsNavigation, SessionsNavigation,
        };
        foreach (var button in navigation)
        {
            var selected = string.Equals(button.Tag as string, selectedPage, StringComparison.Ordinal);
            button.Background = selected ? (Brush)FindResource("SurfaceElevated") : Brushes.Transparent;
            button.BorderBrush = selected ? (Brush)FindResource("Primary") : Brushes.Transparent;
            button.BorderThickness = new Thickness(1);
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            if (button.Content is StackPanel stack && stack.Children.OfType<System.Windows.Shapes.Path>().FirstOrDefault() is { } icon)
            {
                icon.Fill = selected ? (Brush)FindResource("Accent") : (Brush)FindResource("TextSecondary");
            }
        }
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        await RefreshComputersAsync();
    }

    private void DirectHostBox_TextChanged(object sender, TextChangedEventArgs e) =>
        DirectHostPlaceholder.Visibility = string.IsNullOrEmpty(DirectHostBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private async void GroupFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingGroups && IsLoaded)
        {
            await RefreshComputersAsync();
        }
    }

    private async void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _storeReady)
        {
            try
            {
                if (SortBox.SelectedItem is ComboBoxItem { Tag: string sort })
                {
                    await _store.SetPreferenceAsync("computer-sort", sort);
                }

                await RefreshComputersAsync();
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, exception.Message, "Não foi possível salvar as preferências", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async Task RefreshCurrentPageAsync()
    {
        if (_currentPage == "recent")
        {
            try
            {
                var history = await _store.GetHistoryAsync();
                HistoryGrid.ItemsSource = history;
                HistoryEmptyState.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                ShowDatabaseError(exception);
            }
        }
        else if (_currentPage is "computers" or "favorites")
        {
            await RefreshComputersAsync();
        }
        else if (_currentPage == "sessions")
        {
            RefreshSessionList();
        }
    }

    private async Task RefreshComputersAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            var storedComputers = await _store.GetComputersAsync(
                SearchBox.Text,
                favoritesOnly: _currentPage == "favorites");
            var selectedGroup = (GroupFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
            _refreshingGroups = true;
            GroupFilterBox.Items.Clear();
            GroupFilterBox.Items.Add(new ComboBoxItem { Content = "Todos os grupos", Tag = string.Empty });
            foreach (var group in storedComputers
                         .Select(item => item.Group)
                         .Where(group => !string.IsNullOrWhiteSpace(group))
                         .Distinct(StringComparer.CurrentCultureIgnoreCase)
                         .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase))
            {
                GroupFilterBox.Items.Add(new ComboBoxItem { Content = group, Tag = group });
            }

            GroupFilterBox.SelectedItem = GroupFilterBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, selectedGroup, StringComparison.CurrentCultureIgnoreCase))
                ?? GroupFilterBox.Items[0];
            _refreshingGroups = false;
            selectedGroup = (GroupFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (!string.IsNullOrWhiteSpace(selectedGroup))
            {
                storedComputers = storedComputers
                    .Where(item => string.Equals(item.Group, selectedGroup, StringComparison.CurrentCultureIgnoreCase))
                    .ToArray();
            }

            var sorted = SortBox.SelectedItem is ComboBoxItem { Tag: "recent" }
                ? storedComputers.OrderByDescending(item => item.LastConnectedUtc).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                : SortBox.SelectedItem is ComboBoxItem { Tag: "group" }
                    ? storedComputers.OrderBy(item => item.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                    : storedComputers.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
            var records = sorted.ToList();
            ComputerGrid.ItemsSource = records;
            ComputersEmptyState.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            ShowDatabaseError(exception);
        }
    }

    private void NewComputer_Click(object sender, RoutedEventArgs e)
    {
        _editingComputerId = null;
        EditorTitle.Text = "Novo computador";
        NameBox.Clear();
        HostnameBox.Clear();
        IpAddressBox.Clear();
        ComputerPortBox.Text = "5900";
        GroupBox.Clear();
        FavoriteBox.IsChecked = false;
        ViewPreferenceBox.SelectedIndex = 0;
        ComputerGrid.SelectedItem = null;
        RemoveCredentialButton.IsEnabled = false;
        NameBox.Focus();
    }

    private void ComputerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComputerGrid.SelectedItem is not SavedComputer computer)
        {
            return;
        }

        _editingComputerId = computer.Id;
        EditorTitle.Text = "Editar computador";
        NameBox.Text = computer.Name;
        HostnameBox.Text = computer.Hostname ?? string.Empty;
        IpAddressBox.Text = computer.IpAddress ?? string.Empty;
        ComputerPortBox.Text = computer.Port.ToString();
        GroupBox.Text = computer.Group ?? string.Empty;
        FavoriteBox.IsChecked = computer.IsFavorite;
        RemoveCredentialButton.IsEnabled = true;
        ViewPreferenceBox.SelectedItem = ViewPreferenceBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, computer.ViewPreference)) ?? ViewPreferenceBox.Items[0];
    }

    private async void SaveComputer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(ComputerPortBox.Text, out var port) || port is < 1 or > 65535)
            {
                throw new ArgumentException("Informe uma porta entre 1 e 65535.");
            }

            var hostname = NormalizeOptional(HostnameBox.Text);
            var address = NormalizeOptional(IpAddressBox.Text);
            if (string.IsNullOrWhiteSpace(hostname) && string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("Informe um hostname ou endereço IP.");
            }

            if (address is not null && !IPAddress.TryParse(address, out _))
            {
                throw new ArgumentException("O endereço IP não é válido.");
            }

            var oldRecord = _editingComputerId.HasValue
                ? await _store.GetComputerAsync(_editingComputerId.Value)
                : null;
            var viewPreference = (ViewPreferenceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "fit";
            var computer = new SavedComputer(
                _editingComputerId ?? Guid.NewGuid(),
                Required(NameBox.Text, "Informe um nome amigável."),
                hostname,
                address,
                port,
                NormalizeOptional(GroupBox.Text),
                FavoriteBox.IsChecked == true,
                oldRecord?.LastConnectedUtc,
                oldRecord?.LastResult,
                viewPreference,
                oldRecord?.CredentialTarget);

            var duplicate = (await _store.GetComputersAsync())
                .FirstOrDefault(existing => existing.Id != computer.Id && SameEndpoint(existing, computer));
            if (duplicate is not null)
            {
                var answer = MessageBox.Show(
                    this,
                    $"Esse destino já está cadastrado como “{duplicate.Name}”. Atualizar esse computador e preservar o nome existente?",
                    "Destino duplicado",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }

                computer = computer with
                {
                    Id = duplicate.Id,
                    Name = duplicate.Name,
                    LastConnectedUtc = duplicate.LastConnectedUtc,
                    LastResult = duplicate.LastResult,
                    IsFavorite = computer.IsFavorite || duplicate.IsFavorite,
                    CredentialTarget = computer.CredentialTarget ?? duplicate.CredentialTarget,
                };
            }

            await _store.SaveComputerAsync(computer);
            await RefreshComputersAsync();
            ComputerGrid.SelectedItem = (await _store.GetComputersAsync())
                .FirstOrDefault(item => item.Id == computer.Id);
            RemoveCredentialButton.IsEnabled = true;
            EditorTitle.Text = "Computador salvo";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or SqliteException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Não foi possível salvar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DuplicateComputer_Click(object sender, RoutedEventArgs e)
    {
        if (ComputerGrid.SelectedItem is not SavedComputer selected)
        {
            ShowNoSelection();
            return;
        }

        var duplicate = selected with
        {
            Id = Guid.NewGuid(),
            Name = $"{selected.Name} (cópia)",
            LastConnectedUtc = null,
            LastResult = null,
            IsFavorite = false,
        };
        _editingComputerId = null;
        EditorTitle.Text = "Duplicar computador";
        NameBox.Text = duplicate.Name;
        HostnameBox.Text = duplicate.Hostname ?? string.Empty;
        IpAddressBox.Text = duplicate.IpAddress ?? string.Empty;
        ComputerPortBox.Text = duplicate.Port.ToString();
        GroupBox.Text = duplicate.Group ?? string.Empty;
        FavoriteBox.IsChecked = false;
        ViewPreferenceBox.SelectedItem = ViewPreferenceBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, duplicate.ViewPreference)) ?? ViewPreferenceBox.Items[0];
    }

    private async void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ComputerGrid.SelectedItem is not SavedComputer selected)
        {
            ShowNoSelection();
            return;
        }

        try
        {
            await _store.SaveComputerAsync(selected with { IsFavorite = !selected.IsFavorite });
            await RefreshComputersAsync();
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            ShowDatabaseError(exception);
        }
    }

    private async void DeleteComputer_Click(object sender, RoutedEventArgs e)
    {
        if (ComputerGrid.SelectedItem is not SavedComputer selected)
        {
            ShowNoSelection();
            return;
        }

        if (MessageBox.Show(this, $"Excluir “{selected.Name}”?", "Confirmar exclusão", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            try
            {
                await _store.DeleteComputerAsync(selected.Id);
                NewComputer_Click(sender, e);
                await RefreshComputersAsync();
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                ShowDatabaseError(exception);
            }
        }
    }

    private async void RemoveSavedCredential_Click(object sender, RoutedEventArgs e)
    {
        if (ComputerGrid.SelectedItem is not SavedComputer computer)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"Remover a credencial VNC lembrada para “{computer.Name}” deste usuário Windows?",
                "Remover credencial",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _credentialVault.Delete(computer.CredentialTarget ?? WindowsCredentialVault.CreateViewerTarget(computer.ConnectionHost, computer.Port));
            var updated = computer with { CredentialTarget = null };
            await _store.SaveComputerAsync(updated);
            await RefreshComputersAsync();
            ComputerGrid.SelectedItem = updated;
            RemoveCredentialButton.IsEnabled = false;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Não foi possível remover a credencial", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ComputerGrid.SelectedItem is not SavedComputer computer)
        {
            ShowNoSelection();
            return;
        }

        await ConnectAsync(computer.ConnectionHost, computer.Port, string.Empty, computer);
    }

    private async void ConnectCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SavedComputer computer })
        {
            ComputerGrid.SelectedItem = computer;
            await ConnectAsync(computer.ConnectionHost, computer.Port, string.Empty, computer);
        }
    }

    private async void ConnectAddress_Click(object sender, RoutedEventArgs e)
    {
        var host = DirectHostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this, "Informe um hostname ou endereço IP.", "Destino necessário", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(DirectPortBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Informe uma porta entre 1 e 65535.", "Porta inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var knownComputer = (await _store.GetComputersAsync())
            .FirstOrDefault(item => item.Port == port && string.Equals(item.ConnectionHost, host, StringComparison.OrdinalIgnoreCase));
        await ConnectAsync(host, port, string.Empty, knownComputer);
    }

    private async Task ConnectAsync(string host, int port, string password, SavedComputer? knownComputer)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "O destino ou a porta não são válidos.", "Falha de conexão", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existing = _sessions.Values.FirstOrDefault(state =>
            WindowsCredentialVault.CreateViewerTarget(state.Host, state.Port) ==
            WindowsCredentialVault.CreateViewerTarget(host, port));
        if (existing is not null)
        {
            if (existing.Session.IsConnected)
            {
                OpenSessionInWorkspace(existing);
                return;
            }

            var reconnect = MessageBox.Show(
                this,
                $"Já existe uma sessão desconectada para {existing.DisplayName}. Reconectar essa sessão em vez de criar outra?",
                "Sessão existente",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (reconnect == MessageBoxResult.Yes)
            {
                await CloseSessionAsync(existing, removeTab: true);
            }
            else
            {
                return;
            }
        }

        var endpointKey = WindowsCredentialVault.CreateViewerTarget(host, port);
        if (!_connectingEndpoints.Add(endpointKey))
        {
            MessageBox.Show(this, "Uma conexão para esse destino já está em andamento.", "Conexão em andamento", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var actualPassword = password;
            var rememberPassword = false;
            var usingSavedCredential = false;
            if (string.IsNullOrEmpty(actualPassword))
            {
                actualPassword = _credentialVault.Read(endpointKey) ?? string.Empty;
                usingSavedCredential = !string.IsNullOrEmpty(actualPassword);
                if (!usingSavedCredential)
                {
                    if (!PromptForPassword(host, port, out actualPassword, out rememberPassword))
                    {
                        return;
                    }
                }
            }

            var timer = Stopwatch.StartNew();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var session = new VncViewerSession(new ConnectionSettings
                {
                    Host = host,
                    Port = port,
                    Password = actualPassword,
                    ViewOnly = false,
                });
                var state = new ViewerTabState(session, host, port, knownComputer);
                try
                {
                    state.SetStatus("Conectando e autenticando...");
                    var init = await session.ConnectAsync();
                    state.StartedUtc = DateTimeOffset.UtcNow;
                    state.CreateWorkspace(init.Width, init.Height);
                    _sessions.Add(state.Id, state);
                    state.IsConnected = true;
                    OpenSessionInWorkspace(state);
                    state.SetStatus($"Autenticado em {init.DesktopName} ({init.Width}x{init.Height}) em {timer.ElapsedMilliseconds} ms.");
                    session.FramebufferUpdated += (_, args) => RenderFramebuffer(state, args);
                    session.StatusChanged += (_, message) => Dispatcher.BeginInvoke(() => state.SetStatus(message));
                    state.RunTask = MonitorViewerSessionAsync(state);
                    await TrackAuthenticatedConnectionAsync(state);
                    if (rememberPassword)
                    {
                        await SaveCredentialAfterAuthenticationAsync(endpointKey, actualPassword, knownComputer);
                    }

                    await RefreshCurrentPageAsync();
                    return;
                }
                catch (Exception exception) when (
                    usingSavedCredential &&
                    attempt == 0 &&
                    exception is InvalidOperationException &&
                    exception.Message.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
                {
                    await CloseSessionAsync(state, removeTab: true);
                    _credentialVault.Delete(endpointKey);
                    await TryRecordConnectionAsync(host, port, DateTimeOffset.UtcNow, timer.ElapsedMilliseconds, "Falha: autenticação rejeitada");
                    usingSavedCredential = false;
                    if (!PromptForPassword(host, port, out actualPassword, out rememberPassword, "A senha lembrada foi rejeitada. Informe outra senha para este destino."))
                    {
                        return;
                    }
                }
                catch (Exception exception)
                {
                    await CloseSessionAsync(state, removeTab: true);
                    await TryRecordConnectionAsync(host, port, DateTimeOffset.UtcNow, timer.ElapsedMilliseconds, $"Falha: {exception.GetType().Name}");
                    MessageBox.Show(this, exception.Message, "Falha de conexão", MessageBoxButton.OK, MessageBoxImage.Error);
                    await RefreshCurrentPageAsync();
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Não foi possível acessar as credenciais do Windows", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _connectingEndpoints.Remove(endpointKey);
        }
    }

    private bool PromptForPassword(string host, int port, out string password, out bool rememberPassword, string? message = null)
    {
        var prompt = new CredentialPromptWindow($"{host}:{port}", message) { Owner = this };
        var accepted = prompt.ShowDialog() == true;
        password = accepted ? prompt.Password : string.Empty;
        rememberPassword = accepted && prompt.RememberPassword;
        return accepted;
    }

    private async Task SaveCredentialAfterAuthenticationAsync(string target, string password, SavedComputer? computer)
    {
        try
        {
            _credentialVault.Write(target, password);
            if (computer is not null)
            {
                await _store.SaveComputerAsync(computer with { CredentialTarget = target });
                await RefreshComputersAsync();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"A conexão foi autenticada, mas não foi possível lembrar a credencial: {exception.Message}", "Credencial não salva", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task MonitorViewerSessionAsync(ViewerTabState state)
    {
        try
        {
            await state.Session.RunAsync(state.Cancellation.Token);
        }
        catch (Exception exception)
        {
            _ = Dispatcher.BeginInvoke(() => state.SetStatus($"Sessão encerrada por erro: {exception.Message}"));
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(() => OnSessionDisconnected(state));
        }
    }

    private void OpenSessionInWorkspace(ViewerTabState state)
    {
        if (_workspace is null)
        {
            _workspace = new RemoteWorkspaceWindow(this) { Owner = this };
        }

        _workspace.AddSession(state.Tab, state.DisplayName, tab =>
        {
            if (tab.Tag is ViewerTabState sessionState)
            {
                _ = CloseSessionAsync(sessionState, removeTab: true);
            }
        });
        RefreshSessionList();
    }

    private void RenderFramebuffer(ViewerTabState state, FramebufferUpdatedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_sessions.ContainsKey(state.Id) || state.Bitmap is null)
            {
                return;
            }

            if (state.Bitmap.PixelWidth != e.Width || state.Bitmap.PixelHeight != e.Height)
            {
                state.Bitmap = new WriteableBitmap(e.Width, e.Height, 96, 96, PixelFormats.Bgr32, null);
                state.RemoteImage.Source = state.Bitmap;
            }

            foreach (var rectangle in e.Rectangles)
            {
                state.Bitmap.WritePixels(
                    new Int32Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
                    rectangle.Data,
                    rectangle.Width * 4,
                    0);
            }

            state.RecordFrame();
        });
    }

    private async Task CloseSessionAsync(ViewerTabState state, bool removeTab)
    {
        if (state.IsClosing)
        {
            return;
        }

        state.IsClosing = true;
        state.PointerTimer.Stop();
        state.Cancellation.Cancel();
        var wasConnected = state.Session.IsConnected;
        try
        {
            if (state.Session.IsConnected)
            {
                await state.Session.ReleaseAllInputAsync();
                await state.Session.DisconnectAsync();
            }

            if (state.RunTask is not null)
            {
                try
                {
                    await state.RunTask;
                }
                catch (Exception exception)
                {
                    state.SetStatus($"Sessão encerrada: {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            state.SetStatus($"Desconexão: {exception.Message}");
        }
        finally
        {
            if (state.HistoryEntryId.HasValue)
            {
                try
                {
                    await _store.CompleteConnectionAsync(
                        state.HistoryEntryId.Value,
                        Math.Max(0, (long)(DateTimeOffset.UtcNow - state.StartedUtc).TotalMilliseconds),
                        wasConnected ? "Desconectada" : "Conexão encerrada");
                }
                catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
                {
                    state.SetStatus($"Não foi possível atualizar o histórico: {exception.Message}");
                }
            }

            try
            {
                await state.Session.DisposeAsync();
            }
            catch (Exception exception)
            {
                state.SetStatus($"Falha ao liberar a sessão: {exception.Message}");
            }
            finally
            {
                state.Cancellation.Dispose();
                _sessions.Remove(state.Id);
                if (removeTab)
                {
                    _workspace?.RemoveSession(state.Tab);
                }

                RefreshSessionList();
            }
        }
    }

    private async Task<long?> TryRecordConnectionAsync(
        string host,
        int port,
        DateTimeOffset connectedUtc,
        long durationMilliseconds,
        string result)
    {
        try
        {
            return await _store.RecordConnectionAsync(host, port, connectedUtc, durationMilliseconds, result);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"A conexão continua ativa, mas o histórico não pôde ser salvo: {exception.Message}",
                "Falha no histórico local", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private async Task TrackAuthenticatedConnectionAsync(ViewerTabState state)
    {
        try
        {
            state.HistoryEntryId = await _store.RecordConnectionAsync(
                state.Host, state.Port, state.StartedUtc, 0, "Autenticada");
            await RefreshCurrentPageAsync();

            var computers = await _store.GetComputersAsync();
            var matching = computers.FirstOrDefault(item =>
                item.Port == state.Port && EndpointMatches(item, state.Host));
            if (matching is not null)
            {
                await _store.SaveComputerAsync(matching with
                {
                    LastConnectedUtc = state.StartedUtc,
                    LastResult = "Autenticada",
                });
            }
            else if (MessageBox.Show(
                         this,
                         $"Conexão autenticada com {state.Host}:{state.Port}. Salvar como computador conhecido?",
                         "Salvar computador",
                         MessageBoxButton.YesNo,
                         MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var ip = IPAddress.TryParse(state.Host, out _) ? state.Host : null;
                var hostname = ip is null ? state.Host : null;
                await _store.SaveComputerAsync(new SavedComputer(
                    Guid.NewGuid(), state.Host, hostname, ip, state.Port, null, false,
                    state.StartedUtc, "Autenticada", "fit"));
            }

            await RefreshComputersAsync();
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"A sessão está ativa, mas os dados locais não puderam ser atualizados: {exception.Message}",
                "Falha no banco local", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SessionDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ViewerTabState state })
        {
            _ = CloseSessionAsync(state, removeTab: true);
        }
    }

    private void OnSessionDisconnected(ViewerTabState state)
    {
        if (!_sessions.ContainsKey(state.Id) || state.IsClosing)
        {
            return;
        }

        state.IsConnected = false;
        state.SetStatus("Desconectada — use Reconectar para tentar novamente.");
        _ = ObserveInputSendAsync(state, state.Session.ReleaseAllInputAsync());
        RefreshSessionList();
    }

    private void RefreshSessionList()
    {
        if (!IsLoaded)
        {
            return;
        }

        SessionList.ItemsSource = _sessions.Values
            .OrderByDescending(state => state.StartedUtc)
            .ToList();
        SessionsEmptyState.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewerTabState selected })
        {
            OpenSessionInWorkspace(selected);
            return;
        }

        if (SessionList.SelectedItem is ViewerTabState session)
        {
            OpenSessionInWorkspace(session);
            return;
        }

        if (_workspace is not null)
        {
            _workspace.Show();
            if (_workspace.WindowState == WindowState.Minimized)
            {
                _workspace.WindowState = WindowState.Normal;
            }

            _workspace.Activate();
        }
        else
        {
            _workspace = new RemoteWorkspaceWindow(this) { Owner = this };
            _workspace.Show();
        }
    }

    private void CloseSelectedSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is ViewerTabState state)
        {
            _ = CloseSessionAsync(state, removeTab: true);
        }
    }

    private async void ReconnectSelectedSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not ViewerTabState state)
        {
            ShowNoSelection();
            return;
        }

        if (state.Session.IsConnected)
        {
            OpenSessionInWorkspace(state);
            return;
        }

        var host = state.Host;
        var port = state.Port;
        var computer = state.Computer;
        await CloseSessionAsync(state, removeTab: true);
        await ConnectAsync(host, port, string.Empty, computer);
    }

    public void WorkspaceSessionSelected(TabItem tab)
    {
        if (tab.Tag is ViewerTabState state)
        {
            SessionList.SelectedItem = state;
        }
    }

    public void WorkspaceWindowClosed(RemoteWorkspaceWindow window)
    {
        if (ReferenceEquals(_workspace, window))
        {
            _workspace = null;
        }
    }

    public async Task CloseAllWorkspaceSessionsAsync()
    {
        foreach (var state in _sessions.Values.ToArray())
        {
            await CloseSessionAsync(state, removeTab: true);
        }
    }

    public void HandleWorkspaceKeyDown(KeyEventArgs e) => MainWindow_PreviewKeyDown(this, e);
    public void HandleWorkspaceKeyUp(KeyEventArgs e) => MainWindow_PreviewKeyUp(this, e);

    private void ViewOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ViewerTabState state })
        {
            state.ViewOnly = state.ViewOnlyCheckBox.IsChecked == true;
            state.SetStatus(state.ViewOnly ? "Somente visualização ativado." : "Controle remoto ativado.");
            if (state.ViewOnly)
            {
                _ = ObserveInputSendAsync(state, state.Session.ReleaseAllInputAsync());
                state.PointerButtons = 0;
            }
        }
    }

    private void FitToWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ViewerTabState state })
        {
            state.ShowFit();
            _ = PersistViewPreferenceAsync(state, "fit");
        }
    }

    private void ActualSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ViewerTabState state })
        {
            state.ShowActualSize();
            _ = PersistViewPreferenceAsync(state, "100%");
        }
    }

    private async Task PersistViewPreferenceAsync(ViewerTabState state, string preference)
    {
        if (state.Computer is null)
        {
            return;
        }

        try
        {
            var computer = await _store.GetComputerAsync(state.Computer.Id);
            if (computer is not null)
            {
                await _store.SaveComputerAsync(computer with { ViewPreference = preference });
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            state.SetStatus($"Não foi possível salvar a preferência de escala: {exception.Message}");
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => _workspace?.ToggleFullScreen();

    private void RemoteImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Image { Tag: ViewerTabState state } || !state.CanSendInput)
        {
            return;
        }

        state.LatestPointerPosition = e.GetPosition(state.RemoteImage);
        state.PointerDirty = true;
    }

    private async void PointerTimer_Tick(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer { Tag: ViewerTabState state } ||
            !state.PointerDirty || state.PointerSendInProgress || !state.CanSendInput)
        {
            return;
        }

        state.PointerDirty = false;
        state.PointerSendInProgress = true;
        try
        {
            var (x, y) = MapRemoteCoordinates(state, state.LatestPointerPosition);
            await state.Session.SendPointerEventAsync(x, y, state.PointerButtons);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            state.SetStatus($"Mouse: {exception.Message}");
        }
        finally
        {
            state.PointerSendInProgress = false;
        }
    }

    private void RemoteImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image { Tag: ViewerTabState state } || !state.CanSendInput)
        {
            return;
        }

        state.RemoteImage.Focus();
        state.RemoteImage.CaptureMouse();
        state.LatestPointerPosition = e.GetPosition(state.RemoteImage);
        state.PointerDirty = false;
        state.PointerButtons |= ButtonMask(e.ChangedButton);
        var (x, y) = MapRemoteCoordinates(state, state.LatestPointerPosition);
        _ = ObserveInputSendAsync(state, state.Session.SendPointerEventAsync(x, y, state.PointerButtons));
        e.Handled = true;
    }

    private void RemoteImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image { Tag: ViewerTabState state } || !state.CanSendInput)
        {
            return;
        }

        state.LatestPointerPosition = e.GetPosition(state.RemoteImage);
        state.PointerDirty = false;
        state.PointerButtons &= (byte)~ButtonMask(e.ChangedButton);
        var (x, y) = MapRemoteCoordinates(state, state.LatestPointerPosition);
        _ = ObserveInputSendAsync(state, state.Session.SendPointerEventAsync(x, y, state.PointerButtons));
        if (state.PointerButtons == 0)
        {
            state.RemoteImage.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    private void RemoteImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Image { Tag: ViewerTabState state } || !state.CanSendInput)
        {
            return;
        }

        var (x, y) = MapRemoteCoordinates(state, e.GetPosition(state.RemoteImage));
        var mask = e.Delta > 0 ? (byte)8 : (byte)16;
        _ = ObserveInputSendAsync(state, SendWheelAsync(state, x, y, mask));
        e.Handled = true;
    }

    private static async Task SendWheelAsync(ViewerTabState state, ushort x, ushort y, byte mask)
    {
        await state.Session.SendPointerEventAsync(x, y, (byte)(state.PointerButtons | mask));
        await state.Session.SendPointerEventAsync(x, y, state.PointerButtons);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var state = _sessions.Values.FirstOrDefault(session => session.RemoteImage.IsKeyboardFocused);
        if (state is null || !state.CanSendInput || e.IsRepeat)
        {
            return;
        }

        var keySym = ToKeySym(e.Key == Key.System ? e.SystemKey : e.Key);
        if (keySym.HasValue)
        {
            _ = ObserveInputSendAsync(state, state.Session.SendKeyEventAsync(keySym.Value, true));
            e.Handled = true;
        }
    }

    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var state = _sessions.Values.FirstOrDefault(session => session.RemoteImage.IsKeyboardFocused);
        if (state is null || !state.CanSendInput)
        {
            return;
        }

        var keySym = ToKeySym(e.Key == Key.System ? e.SystemKey : e.Key);
        if (keySym.HasValue)
        {
            _ = ObserveInputSendAsync(state, state.Session.SendKeyEventAsync(keySym.Value, false));
            e.Handled = true;
        }
    }

    private void RemoteImage_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Image { Tag: ViewerTabState state } && state.Session.IsConnected)
        {
            _ = ObserveInputSendAsync(state, state.Session.ReleaseAllInputAsync());
        }
    }

    private static (ushort X, ushort Y) MapRemoteCoordinates(ViewerTabState state, Point point)
    {
        if (state.Session.Width == 0 || state.Session.Height == 0 ||
            state.RemoteImage.ActualWidth <= 0 || state.RemoteImage.ActualHeight <= 0)
        {
            return (0, 0);
        }

        var scale = Math.Min(state.RemoteImage.ActualWidth / state.Session.Width, state.RemoteImage.ActualHeight / state.Session.Height);
        var displayedWidth = state.Session.Width * scale;
        var displayedHeight = state.Session.Height * scale;
        var offsetX = (state.RemoteImage.ActualWidth - displayedWidth) / 2;
        var offsetY = (state.RemoteImage.ActualHeight - displayedHeight) / 2;
        var x = (int)Math.Floor((point.X - offsetX) / scale);
        var y = (int)Math.Floor((point.Y - offsetY) / scale);
        return ((ushort)Math.Clamp(x, 0, state.Session.Width - 1), (ushort)Math.Clamp(y, 0, state.Session.Height - 1));
    }

    private static byte ButtonMask(MouseButton button) => button switch
    {
        MouseButton.Left => 1,
        MouseButton.Middle => 2,
        MouseButton.Right => 4,
        _ => 0,
    };

    private static uint? ToKeySym(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return (uint)('a' + (int)key - (int)Key.A);
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return (uint)('0' + (int)key - (int)Key.D0);
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return (uint)('0' + (int)key - (int)Key.NumPad0);
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return 0xffbeu + (uint)((int)key - (int)Key.F1);
        }

        return key switch
        {
            Key.Back => 0xff08,
            Key.Tab => 0xff09,
            Key.Return => 0xff0d,
            Key.Escape => 0xff1b,
            Key.Space => 0x20,
            Key.Home => 0xff50,
            Key.Left => 0xff51,
            Key.Up => 0xff52,
            Key.Right => 0xff53,
            Key.Down => 0xff54,
            Key.PageUp => 0xff55,
            Key.PageDown => 0xff56,
            Key.End => 0xff57,
            Key.Insert => 0xff63,
            Key.Delete => 0xffff,
            Key.LeftShift or Key.RightShift => 0xffe1,
            Key.LeftCtrl or Key.RightCtrl => 0xffe3,
            Key.LeftAlt or Key.RightAlt => 0xffe9,
            Key.CapsLock => 0xffe5,
            Key.NumLock => 0xff7f,
            Key.Oem1 => ';',
            Key.OemPlus => '=',
            Key.OemComma => ',',
            Key.OemMinus => '-',
            Key.OemPeriod => '.',
            Key.Oem2 => '/',
            Key.Oem3 => '`',
            Key.Oem4 => '[',
            Key.Oem5 => '\\',
            Key.Oem6 => ']',
            Key.Oem7 => '\'',
            Key.Add => '+',
            Key.Subtract => '-',
            Key.Multiply => '*',
            Key.Divide => '/',
            Key.Decimal => '.',
            _ => null,
        };
    }

    private async Task ObserveInputSendAsync(ViewerTabState state, Task sendTask)
    {
        try
        {
            await sendTask;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            state.SetStatus($"Entrada remota: {exception.Message}");
        }
    }

    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        if (_server?.IsRunning == true)
        {
            return;
        }

        if (!IPAddress.TryParse(BindAddressBox.Text.Trim(), out var bindAddress))
        {
            ServerStatus.Text = "Endereço de bind inválido. Informe um IP específico.";
            return;
        }

        if (!VncServer.IsAllowedBindAddress(bindAddress))
        {
            ServerStatus.Text = "Use loopback, um endereço privado/VPN ou um endereço link-local. Endereços públicos não são permitidos.";
            return;
        }

        if (!int.TryParse(ServerPortBox.Text, out var port) || port is < 1 or > 65535)
        {
            ServerStatus.Text = "Informe uma porta TCP entre 1 e 65535.";
            return;
        }

        var password = ServerPasswordBox.Password;
        if (password.Length != 8 || password.Any(character => character > 0x7f))
        {
            ServerStatus.Text = "A senha deve conter exatamente oito caracteres ASCII.";
            return;
        }

        if (!IPAddress.IsLoopback(bindAddress) &&
            MessageBox.Show(
                this,
                $"O acesso ficará disponível em {bindAddress}:{port}. VNC não cifra a sessão. Continue somente em uma rede confiável/VPN.",
                "Confirmar compartilhamento",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var settings = new ConnectionSettings
        {
            BindAddress = bindAddress.ToString(),
            Port = port,
            Password = password,
            ViewOnly = ServerViewOnlyCheckBox.IsChecked == true,
            MaxSessions = 1,
        };
        VncServer? server = null;
        try
        {
            server = new VncServer(settings);
            server.StatusChanged += (_, message) => Dispatcher.BeginInvoke(() => ServerStatus.Text = message);
            server.Start();
            _server = server;
            StartServerButton.IsEnabled = false;
            StopServerButton.IsEnabled = true;
            ServerViewOnlyCheckBox.IsEnabled = false;
            ServerStatus.Text = $"Ativo em {settings.BindAddress}:{server.BoundPort}.";
        }
        catch (Exception exception)
        {
            ServerStatus.Text = $"Não foi possível iniciar: {exception.Message}";
            server?.Dispose();
            _server = null;
        }
        finally
        {
            ServerPasswordBox.Clear();
        }
    }

    private async void StopServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StopServerAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Não foi possível parar o servidor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StopServerAsync()
    {
        var server = _server;
        if (server is null)
        {
            return;
        }

        _server = null;
        try
        {
            await server.StopAsync();
        }
        finally
        {
            server.Dispose();
            StartServerButton.IsEnabled = true;
            StopServerButton.IsEnabled = false;
            ServerViewOnlyCheckBox.IsEnabled = true;
            ServerStatus.Text = "Desativado";
        }
    }

    private void ShowNoSelection() =>
        MessageBox.Show(this, "Selecione um computador primeiro.", "Nenhum computador selecionado", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowDatabaseError(Exception exception)
    {
        DatabaseStatus.Text = $"Falha no banco local: {exception.Message}";
        MessageBox.Show(this, exception.Message, "Falha no banco local", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string Required(string value, string error) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(error) : value.Trim();

    private static string? NormalizeOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool SameEndpoint(SavedComputer left, SavedComputer right) =>
        left.Port == right.Port &&
        ((left.Hostname is not null && right.Hostname is not null && string.Equals(left.Hostname, right.Hostname, StringComparison.OrdinalIgnoreCase)) ||
         (left.IpAddress is not null && right.IpAddress is not null && string.Equals(left.IpAddress, right.IpAddress, StringComparison.OrdinalIgnoreCase)) ||
         (left.Hostname is not null && right.IpAddress is not null && string.Equals(left.Hostname, right.IpAddress, StringComparison.OrdinalIgnoreCase)) ||
         (left.IpAddress is not null && right.Hostname is not null && string.Equals(left.IpAddress, right.Hostname, StringComparison.OrdinalIgnoreCase)));

    private static bool EndpointMatches(SavedComputer computer, string host) =>
        string.Equals(computer.Hostname, host, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(computer.IpAddress, host, StringComparison.OrdinalIgnoreCase);

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (!_isShuttingDown)
        {
            _isShuttingDown = true;
            _ = ShutdownAsync();
        }
    }

    private async Task ShutdownAsync()
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            await CloseSessionAsync(session, removeTab: true);
        }

        if (_server is not null)
        {
            await StopServerAsync();
        }

        if (_workspace is not null)
        {
            _workspace.CloseFromManager();
        }

        _allowClose = true;
        await Dispatcher.InvokeAsync(new Action(Close), DispatcherPriority.Background);
    }

    private sealed class ViewerTabState : INotifyPropertyChanged
    {
        private string _displayStatus = "Conectando...";
        private bool _isConnected;

        public ViewerTabState(VncViewerSession session, string host, int port, SavedComputer? computer)
        {
            Session = session;
            Host = host;
            Port = port;
            Computer = computer;
            RemoteImage = new Image
            {
                Stretch = Stretch.Uniform,
                Focusable = true,
            };
            RenderOptions.SetBitmapScalingMode(RemoteImage, BitmapScalingMode.LowQuality);
            RemoteImage.Tag = this;
            RemoteImage.MouseMove += AppRemoteImage_MouseMove;
            RemoteImage.MouseDown += AppRemoteImage_MouseDown;
            RemoteImage.MouseUp += AppRemoteImage_MouseUp;
            RemoteImage.MouseWheel += AppRemoteImage_MouseWheel;
            RemoteImage.LostKeyboardFocus += AppRemoteImage_LostKeyboardFocus;
            Tab = new TabItem { Header = DisplayName, Content = CreateContent(), Tag = this };
            Tab.DataContext = this;
            PointerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16), Tag = this };
            PointerTimer.Tick += PointerTimer_Tick;
            PointerTimer.Start();
        }

        public Guid Id { get; } = Guid.NewGuid();
        public VncViewerSession Session { get; }
        public string Host { get; }
        public int Port { get; }
        public string DisplayName => Computer?.Name ?? Host;
        public string Endpoint => $"{Host}:{Port}";
        public string ConnectedAtDisplay => StartedUtc == default ? string.Empty : StartedUtc.ToLocalTime().ToString("g");
        public string DisplayStatus
        {
            get => _displayStatus;
            private set
            {
                if (_displayStatus == value)
                {
                    return;
                }

                _displayStatus = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayStatus)));
            }
        }
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (_isConnected == value)
                {
                    return;
                }

                _isConnected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayStatus)));
            }
        }
        public SavedComputer? Computer { get; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public CancellationTokenSource Cancellation { get; } = new();
        public TabItem Tab { get; }
        public Image RemoteImage { get; }
        public DispatcherTimer PointerTimer { get; }
        public CheckBox ViewOnlyCheckBox { get; private set; } = null!;
        public TextBlock StatusText { get; private set; } = null!;
        public WriteableBitmap? Bitmap { get; set; }
        public Task? RunTask { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public long? HistoryEntryId { get; set; }
        public bool IsClosing { get; set; }
        public bool ViewOnly { get; set; }
        public byte PointerButtons { get; set; }
        public Point LatestPointerPosition { get; set; }
        public bool PointerDirty { get; set; }
        public bool PointerSendInProgress { get; set; }
        public bool CanSendInput => !IsClosing && !ViewOnly && Session.IsConnected;

        public void SetStatus(string message)
        {
            DisplayStatus = message;
            if (StatusText is not null)
            {
                StatusText.Text = message;
            }
        }

        public void CreateWorkspace(ushort width, ushort height)
        {
            Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            RemoteImage.Source = Bitmap;
            if (Computer?.ViewPreference == "100%")
            {
                ShowActualSize();
            }
            else
            {
                ShowFit();
            }
        }

        public void ShowFit()
        {
            ActualHost.Content = null;
            ActualHost.Visibility = Visibility.Collapsed;
            RemoteImage.Stretch = Stretch.Uniform;
            FitHost.Children.Clear();
            FitHost.Children.Add(RemoteImage);
            FitHost.Visibility = Visibility.Visible;
        }

        public void ShowActualSize()
        {
            FitHost.Children.Clear();
            FitHost.Visibility = Visibility.Collapsed;
            RemoteImage.Stretch = Stretch.None;
            ActualHost.Content = RemoteImage;
            ActualHost.Visibility = Visibility.Visible;
        }

        public void RecordFrame()
        {
            _frameCount++;
            var now = Stopwatch.GetTimestamp();
            if (now - _lastFpsUpdate >= Stopwatch.Frequency)
            {
                var fps = _frameCount * (double)Stopwatch.Frequency / (now - _lastFpsUpdate);
                SetStatus($"Conectado · {fps:F1} FPS");
                _frameCount = 0;
                _lastFpsUpdate = now;
            }
        }

        private int _frameCount;
        private long _lastFpsUpdate = Stopwatch.GetTimestamp();
        private Grid FitHost { get; set; } = null!;
        private ScrollViewer ActualHost { get; set; } = null!;

        private UIElement CreateContent()
        {
            var root = new Grid { Background = (Brush)Application.Current.FindResource("Canvas") };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = new DockPanel
            {
                Background = (Brush)Application.Current.FindResource("Surface"),
                LastChildFill = true,
                Margin = new Thickness(0, 0, 0, 6),
            };
            var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            var disconnect = CreateToolbarButton("Desconectar", "Encerrar esta sessão", "IconDisconnect", this);
            disconnect.Click += AppSessionDisconnect_Click;
            var fit = CreateToolbarButton("Ajustar", "Ajustar a imagem à janela", "IconFit", this);
            fit.Click += AppFitToWindow_Click;
            var actual = CreateToolbarButton("100%", "Exibir em escala 100%", "IconComputer", this);
            actual.Click += AppActualSize_Click;
            var fullScreen = CreateToolbarButton("Tela cheia", "Alternar tela cheia", "IconFullscreen", this);
            fullScreen.Click += AppFullScreen_Click;
            ViewOnlyCheckBox = new CheckBox
            {
                Content = "Somente visualização",
                Tag = this,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 10, 0),
                ToolTip = "Bloquear mouse e teclado remotos",
            };
            ViewOnlyCheckBox.Checked += AppViewOnly_Changed;
            ViewOnlyCheckBox.Unchecked += AppViewOnly_Changed;
            var cad = CreateToolbarButton("Ctrl+Alt+Del", "Indisponível neste protocolo", "IconKeyboard", this);
            cad.IsEnabled = false;
            cad.ToolTip = "Ctrl+Alt+Del não é suportado por este servidor";
            actions.Children.Add(disconnect);
            actions.Children.Add(fit);
            actions.Children.Add(actual);
            actions.Children.Add(fullScreen);
            actions.Children.Add(ViewOnlyCheckBox);
            actions.Children.Add(cad);
            DockPanel.SetDock(actions, Dock.Left);
            toolbar.Children.Add(actions);

            var statusPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10, 0, 12, 0),
            };
            statusPanel.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = (Brush)Application.Current.FindResource("Success"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            StatusText = new TextBlock
            {
                Text = "Conectando...",
                Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            };
            statusPanel.Children.Add(StatusText);
            DockPanel.SetDock(statusPanel, Dock.Right);
            toolbar.Children.Add(statusPanel);
            root.Children.Add(toolbar);

            FitHost = new Grid();
            ActualHost = new ScrollViewer
            {
                Visibility = Visibility.Collapsed,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            FitHost.Children.Add(RemoteImage);
            var imageHosts = new Grid();
            imageHosts.Children.Add(FitHost);
            imageHosts.Children.Add(ActualHost);
            var border = new Border
            {
                Background = (Brush)Application.Current.FindResource("Canvas"),
                BorderBrush = (Brush)Application.Current.FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(4),
                Child = imageHosts,
            };
            Grid.SetRow(border, 1);
            root.Children.Add(border);
            return root;
        }

        private static Button CreateToolbarButton(string label, string toolTip, string iconKey, ViewerTabState state)
        {
            var button = new Button
            {
                Style = (Style)Application.Current.FindResource("CompactButton"),
                Tag = state,
                ToolTip = toolTip,
                Padding = new Thickness(8, 5, 9, 5),
            };
            System.Windows.Automation.AutomationProperties.SetName(button, toolTip);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new System.Windows.Shapes.Path
            {
                Data = (Geometry)Application.Current.FindResource(iconKey),
                Width = 13,
                Height = 13,
                Fill = (Brush)Application.Current.FindResource("Accent"),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
            });
            content.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            });
            button.Content = content;
            return button;
        }

        private void AppSessionDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ViewerTabState state } && Application.Current.MainWindow is MainWindow window)
            {
                window.SessionDisconnect_Click(sender, e);
            }
        }

        private void AppFitToWindow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ViewerTabState state } && Application.Current.MainWindow is MainWindow window)
            {
                window.FitToWindow_Click(sender, e);
            }
        }

        private void AppActualSize_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ViewerTabState state } && Application.Current.MainWindow is MainWindow window)
            {
                window.ActualSize_Click(sender, e);
            }
        }

        private void AppFullScreen_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow window)
            {
                window.FullScreen_Click(sender, e);
            }
        }

        private void AppViewOnly_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ViewerTabState state } && Application.Current.MainWindow is MainWindow window)
            {
                window.ViewOnly_Changed(sender, e);
            }
        }

        private void AppRemoteImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow window)
            {
                window.RemoteImage_MouseMove(sender, e);
            }
        }

        private void AppRemoteImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow window)
            {
                window.RemoteImage_MouseDown(sender, e);
            }
        }

        private void AppRemoteImage_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow window)
            {
                window.RemoteImage_MouseUp(sender, e);
            }
        }

        private void AppRemoteImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow window)
            {
                window.RemoteImage_MouseWheel(sender, e);
            }
        }

        private void AppRemoteImage_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow window)
            {
                window.RemoteImage_LostKeyboardFocus(sender, e);
            }
        }

        private void PointerTimer_Tick(object? sender, EventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow window)
            {
                window.PointerTimer_Tick(sender, e);
            }
        }
    }
}
