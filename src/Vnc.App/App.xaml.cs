using System.Windows;

namespace Vnc.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private void App_Startup(object sender, StartupEventArgs e)
    {
        var files = e.Args.Length > 1 &&
            string.Equals(e.Args[0], "--import-vnc", StringComparison.OrdinalIgnoreCase)
                ? e.Args.Skip(1).ToArray()
                : e.Args.Where(argument => string.Equals(
                    System.IO.Path.GetExtension(argument),
                    ".vnc",
                    StringComparison.OrdinalIgnoreCase)).ToArray();
        var window = new MainWindow(files);
        MainWindow = window;
        window.Show();
    }
}
