using System.Windows;

namespace Vnc.App;

internal static class MessageBox
{
    public static MessageBoxResult Show(
        Window owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image) =>
        LucyDialog.Show(owner, message, caption, buttons, image);
}
