using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Vnc.App;

internal static class DarkWindowChrome
{
    private const int UseImmersiveDarkMode = 20;
    private const int CaptionColor = 35;
    private const int BorderColor = 34;

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var enabled = 1;
            if (TrySetAttribute(handle, UseImmersiveDarkMode, ref enabled) != 0)
            {
                TrySetAttribute(handle, 19, ref enabled);
            }

            TrySetAttribute(handle, CaptionColor, ref DarkCaption);
            TrySetAttribute(handle, BorderColor, ref DarkBorder);
        };
    }

    private static int DarkCaption = ColorRef(0x100D18);
    private static int DarkBorder = ColorRef(0x382B4B);

    private static int ColorRef(int rgb) =>
        ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static int TrySetAttribute(IntPtr handle, int attribute, ref int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            return -1;
        }
        catch (EntryPointNotFoundException)
        {
            return -1;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
