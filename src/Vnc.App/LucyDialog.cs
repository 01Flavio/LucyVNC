using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Vnc.App;

internal static class LucyDialog
{
    public static MessageBoxResult Show(
        Window owner,
        string message,
        string caption,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new Window
        {
            Title = caption,
            Owner = owner,
            Width = 480,
            MinWidth = 390,
            MaxWidth = 560,
            MaxHeight = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brush("Canvas"),
            Foreground = Brush("TextPrimary"),
            FontFamily = new FontFamily("Segoe UI Variable"),
            WindowStyle = WindowStyle.SingleBorderWindow,
        };
        dialog.Icon = owner.Icon;
        DarkWindowChrome.Apply(dialog);

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var severity = GetSeverity(image);
        var icon = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = severity,
            Child = new TextBlock
            {
                Text = GetGlyph(image),
                Foreground = Brush("TextPrimary"),
                FontWeight = FontWeights.Bold,
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        heading.Children.Add(icon);
        heading.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0),
        });
        root.Children.Add(heading);

        var messageView = new ScrollViewer
        {
            MaxHeight = 330,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
                LineHeight = 21,
            },
        };
        Grid.SetRow(messageView, 1);
        root.Children.Add(messageView);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        Grid.SetRow(buttonPanel, 2);

        var result = GetDefaultResult(buttons);
        var resultWasSelected = false;
        var buttonDefinitions = GetButtons(buttons);
        foreach (var (label, buttonResult, isPrimary) in buttonDefinitions)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 88,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = buttonResult == result,
                IsCancel = buttonResult == MessageBoxResult.Cancel,
                Style = (Style)Application.Current.FindResource(isPrimary ? "PrimaryButton" : "CompactButton"),
            };
            button.Click += (_, _) =>
            {
                result = buttonResult;
                resultWasSelected = true;
                dialog.DialogResult = buttonResult is MessageBoxResult.OK or MessageBoxResult.Yes;
            };
            buttonPanel.Children.Add(button);
        }

        root.Children.Add(buttonPanel);
        dialog.Content = root;
        dialog.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                result = buttons switch
                {
                    MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
                    MessageBoxButton.YesNo => MessageBoxResult.No,
                    _ => MessageBoxResult.OK,
                };
                dialog.DialogResult = false;
            }
        };
        dialog.ShowDialog();
        if (!resultWasSelected && buttons is MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel)
        {
            return MessageBoxResult.Cancel;
        }

        return result;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private static Brush GetSeverity(MessageBoxImage image) => image switch
    {
        MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => Brush("Error"),
        MessageBoxImage.Warning or MessageBoxImage.Exclamation => Brush("Warning"),
        _ => Brush("Primary"),
    };

    private static string GetGlyph(MessageBoxImage image) => image switch
    {
        MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => "!",
        MessageBoxImage.Warning or MessageBoxImage.Exclamation => "!",
        MessageBoxImage.Question => "?",
        _ => "i",
    };

    private static MessageBoxResult GetDefaultResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => MessageBoxResult.OK,
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.YesNoCancel => MessageBoxResult.No,
        _ => MessageBoxResult.OK,
    };

    private static IReadOnlyList<(string Label, MessageBoxResult Result, bool Primary)> GetButtons(MessageBoxButton buttons) =>
        buttons switch
        {
            MessageBoxButton.OK => [("OK", MessageBoxResult.OK, true)],
            MessageBoxButton.OKCancel =>
            [
                ("Cancelar", MessageBoxResult.Cancel, false),
                ("OK", MessageBoxResult.OK, true),
            ],
            MessageBoxButton.YesNo =>
            [
                ("Não", MessageBoxResult.No, false),
                ("Sim", MessageBoxResult.Yes, true),
            ],
            MessageBoxButton.YesNoCancel =>
            [
                ("Cancelar", MessageBoxResult.Cancel, false),
                ("Não", MessageBoxResult.No, false),
                ("Sim", MessageBoxResult.Yes, true),
            ],
            _ => [("OK", MessageBoxResult.OK, true)],
        };
}
