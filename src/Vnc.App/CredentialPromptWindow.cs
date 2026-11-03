using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vnc.Protocol;

namespace Vnc.App;

internal sealed class CredentialPromptWindow : Window
{
    private readonly PasswordBox _passwordBox = new() { MaxLength = 16, MinWidth = 280 };
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
        Icon = Application.Current.MainWindow?.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        DarkWindowChrome.Apply(this);

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
            Text = message ?? "Informe a senha VNC. O protocolo clássico usa no máximo oito caracteres ASCII; LucyVNC Server exige de 6 a 8.",
            Margin = new Thickness(0, 8, 0, 12),
            Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
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

            if (!VncAuthentication.IsValidClassicPassword(_passwordBox.Password))
            {
                MessageBox.Show(this, "A autenticação VNC clássica aceita de 1 a 8 caracteres ASCII imprimíveis. A entrada não será truncada.", "Senha inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
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
