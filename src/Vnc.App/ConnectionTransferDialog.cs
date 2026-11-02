using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Vnc.Infrastructure;

namespace Vnc.App;

internal enum ConnectionExportScope
{
    All,
    Selected,
    Favorites,
}

internal enum ConnectionExportFormat
{
    Json,
    Csv,
}

internal sealed record ConnectionExportOptions(ConnectionExportScope Scope, ConnectionExportFormat Format);

internal static class ConnectionTransferDialog
{
    public static ImportConflictPolicy? ShowImportPreview(Window owner, ComputerImportPlan plan)
    {
        var dialog = CreateWindow(owner, "Pré-visualizar importação", 920, 600);
        var root = new DockPanel { Margin = new Thickness(18) };
        var summary = new TextBlock
        {
            Text = $"Válidos: {plan.ValidCount}    Conflitos: {plan.ConflictCount}    Duplicados no arquivo: {plan.DuplicateCount}    Inválidos: {plan.InvalidCount}",
            Foreground = Brush("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(summary, Dock.Top);
        root.Children.Add(summary);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var policy = new ComboBox { Width = 235, SelectedIndex = 0, ToolTip = "Como resolver conflitos de endereço" };
        policy.Items.Add(new ComboBoxItem { Content = "Conflitos: ignorar existentes", Tag = ImportConflictPolicy.Ignore });
        policy.Items.Add(new ComboBoxItem { Content = "Conflitos: atualizar existentes", Tag = ImportConflictPolicy.Update });
        policy.Items.Add(new ComboBoxItem { Content = "Conflitos: criar outra entrada", Tag = ImportConflictPolicy.Create });
        actions.Children.Add(policy);
        var cancel = CreateButton("Cancelar", primary: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        actions.Children.Add(cancel);
        var import = CreateButton("Importar registros válidos", primary: true);
        import.IsDefault = true;
        import.IsEnabled = plan.ValidCount > 0;
        import.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(import);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        var previewRows = plan.Entries.Select(entry => new ImportPreviewRow(
            entry.RecordNumber,
            entry.Status.ToString(),
            entry.Computer?.Name ?? "",
            entry.Computer?.Hostname ?? "",
            entry.Computer?.IpAddress ?? "",
            entry.Computer?.Port.ToString() ?? "",
            entry.Computer?.IsFavorite.ToString() ?? "",
            entry.Message ?? (entry.ExistingMatches.Count == 0
                ? ""
                : $"Conflita com: {string.Join(", ", entry.ExistingMatches.Select(computer => computer.Name))}"))).ToArray();
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            ItemsSource = previewRows,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
        };
        AddColumn(grid, "Registro", nameof(ImportPreviewRow.RecordNumber), 72);
        AddColumn(grid, "Estado", nameof(ImportPreviewRow.Status), 100);
        AddColumn(grid, "Nome", nameof(ImportPreviewRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star));
        AddColumn(grid, "Hostname", nameof(ImportPreviewRow.Hostname), new DataGridLength(1, DataGridLengthUnitType.Star));
        AddColumn(grid, "IP", nameof(ImportPreviewRow.IpAddress), new DataGridLength(1, DataGridLengthUnitType.Star));
        AddColumn(grid, "Porta", nameof(ImportPreviewRow.Port), 65);
        AddColumn(grid, "Favorito", nameof(ImportPreviewRow.Favorite), 80);
        AddColumn(grid, "Detalhes", nameof(ImportPreviewRow.Details), new DataGridLength(2, DataGridLengthUnitType.Star));
        root.Children.Add(grid);

        dialog.Content = root;
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        return (policy.SelectedItem as ComboBoxItem)?.Tag is ImportConflictPolicy selected
            ? selected
            : ImportConflictPolicy.Ignore;
    }

    public static ConnectionExportOptions? ShowExportOptions(Window owner, int selectedCount)
    {
        var hasSelection = selectedCount > 0;
        var dialog = CreateWindow(owner, "Exportar conexões", 420, 230);
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "Computadores para exportar", Foreground = Brush("TextSecondary") });

        var scope = new ComboBox { SelectedIndex = hasSelection ? 1 : 0, Margin = new Thickness(0, 6, 0, 10) };
        scope.Items.Add(new ComboBoxItem { Content = "Todos os computadores", Tag = ConnectionExportScope.All });
        scope.Items.Add(new ComboBoxItem
        {
            Content = hasSelection ? $"Conexões selecionadas ({selectedCount})" : "Selecionados (nenhum disponível)",
            Tag = ConnectionExportScope.Selected,
            IsEnabled = hasSelection,
        });
        scope.Items.Add(new ComboBoxItem { Content = "Somente favoritos", Tag = ConnectionExportScope.Favorites });
        Grid.SetRow(scope, 1);
        root.Children.Add(scope);

        root.Children.Add(new TextBlock
        {
            Text = "Formato",
            Foreground = Brush("TextSecondary"),
        });
        Grid.SetRow(root.Children[^1], 2);

        var format = new ComboBox { SelectedIndex = 0, Margin = new Thickness(0, 6, 0, 10) };
        format.Items.Add(new ComboBoxItem { Content = "JSON LucyVNC (versionado)", Tag = ConnectionExportFormat.Json });
        format.Items.Add(new ComboBoxItem { Content = "CSV com cabeçalho", Tag = ConnectionExportFormat.Csv });
        Grid.SetRow(format, 3);
        root.Children.Add(format);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = CreateButton("Cancelar", primary: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(cancel);
        var save = CreateButton("Salvar arquivo", primary: true);
        save.IsDefault = true;
        save.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        dialog.Content = root;
        if (dialog.ShowDialog() != true ||
            (scope.SelectedItem as ComboBoxItem)?.Tag is not ConnectionExportScope selectedScope ||
            (format.SelectedItem as ComboBoxItem)?.Tag is not ConnectionExportFormat selectedFormat)
        {
            return null;
        }

        return new ConnectionExportOptions(selectedScope, selectedFormat);
    }

    public static OpenFileDialog CreateImportFileDialog() => new()
    {
        Title = "Importar conexões LucyVNC",
        Filter = "JSON ou CSV|*.json;*.csv|JSON LucyVNC|*.json|CSV com cabeçalho|*.csv",
        CheckFileExists = true,
        Multiselect = false,
    };

    public static SaveFileDialog CreateExportFileDialog(ConnectionExportFormat format) => new()
    {
        Title = "Exportar conexões LucyVNC",
        Filter = format == ConnectionExportFormat.Json
            ? "JSON LucyVNC|*.json"
            : "CSV com cabeçalho|*.csv",
        DefaultExt = format == ConnectionExportFormat.Json ? ".json" : ".csv",
        AddExtension = true,
        OverwritePrompt = true,
        FileName = format == ConnectionExportFormat.Json ? "LucyVNC-connections.json" : "LucyVNC-connections.csv",
    };

    public static Button CreateButton(string label, bool primary)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)Application.Current.FindResource(primary ? "PrimaryButton" : "CompactButton"),
        };
        return button;
    }

    private static Window CreateWindow(Window owner, string title, double width, double height) =>
        new()
        {
            Title = title,
            Owner = owner,
            Width = width,
            Height = height,
            MinWidth = width - 80,
            MinHeight = height - 100,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = Brush("Canvas"),
            Foreground = Brush("TextPrimary"),
            FontFamily = new FontFamily("Segoe UI Variable"),
            Icon = owner.Icon,
        };

    private static void AddColumn(DataGrid grid, string header, string binding, DataGridLength width) =>
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new System.Windows.Data.Binding(binding),
            Width = width,
        });

    private static void AddColumn(DataGrid grid, string header, string binding, double width) =>
        AddColumn(grid, header, binding, new DataGridLength(width));

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private sealed record ImportPreviewRow(
        int RecordNumber,
        string Status,
        string Name,
        string Hostname,
        string IpAddress,
        string Port,
        string Favorite,
        string Details);
}
