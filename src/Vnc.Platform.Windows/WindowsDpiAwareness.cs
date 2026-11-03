using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Vnc.Platform.Windows;

public static class WindowsDpiAwareness
{
    private static readonly nint PerMonitorAwareV2 = new(-4);
    private static readonly object Sync = new();
    private static volatile bool _initialized;

    public static void InitializePerMonitorV2()
    {
        if (_initialized)
        {
            return;
        }

        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            if (!SetProcessDpiAwarenessContext(PerMonitorAwareV2))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 5 ||
                    !AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorAwareV2))
                {
                    throw new Win32Exception(error, "LucyVNC requires Per-Monitor DPI Awareness V2 for physical desktop capture.");
                }
            }

            _initialized = true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
