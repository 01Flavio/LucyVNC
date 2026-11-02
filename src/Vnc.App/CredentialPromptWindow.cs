using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Vnc.App;

internal sealed class CredentialPromptWindow : Window
{
    private readonly PasswordBox _passwordBox = new() { MaxLength = 8, MinWidth = 280 };
    private readonly CheckBox _rememberCheckBox = new()
    {
        Content = "Lembrar no Windows Credential Manager",
        Margin = new Thickness(0, 10, 0, 8),
    };

    public CredentialPromptWindow(string endpoint, string? message = null)
    {
        Title = "Credenciais VNC";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(20, 17, 27));
        Foreground = new SolidColorBrush(Color.FromRgb(242, 237, 248));

        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock
        {
            Text = $"Conectar a {endpoint}",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = message ?? "Informe a senha VNC. O protocolo clássico aceita no máximo oito caracteres.",
            Margin = new Thickness(0, 8, 0, 12),
            Foreground = new SolidColorBrush(Color.FromRgb(180, 171, 194)),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(_passwordBox);
        content.Children.Add(_rememberCheckBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var cancel = new Button { Content = "Cancelar", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var connect = new Button { Content = "Conectar", MinWidth = 100, IsDefault = true };
        connect.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(_passwordBox.Password))
            {
                MessageBox.Show(this, "Informe a senha VNC.", "Senha necessária", MessageBoxButton.OK, MessageBoxImage.Warning);
                _passwordBox.Focus();
                return;
            }

            if (_passwordBox.Password.Length > 8 || _passwordBox.Password.Any(character => character > 0x7f))
            {
                MessageBox.Show(this, "A autenticação VNC clássica aceita até oito caracteres ASCII.", "Senha inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
                _passwordBox.Focus();
                return;
            }

            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(connect);
        content.Children.Add(buttons);
        Content = content;
        Loaded += (_, _) => _passwordBox.Focus();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
    }

    public string Password => _passwordBox.Password;
    public bool RememberPassword => _rememberCheckBox.IsChecked == true;
}
