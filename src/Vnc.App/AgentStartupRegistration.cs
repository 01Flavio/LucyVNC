using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Vnc.App;

internal static class AgentStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LucyVNC.ServerTray";
    private const string LegacyValueName = "LucyVNC.SessionAgent";

    public static bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(ValueName) is string || runKey?.GetValue(LegacyValueName) is string;
    }

    public static void SetEnabled(bool enabled, string trayExecutablePath)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");
        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            runKey.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Path.GetFullPath(trayExecutablePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The LucyServer tray executable was not found.", executable);
        }

        runKey.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
        runKey.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    public static void StartNow(string trayExecutablePath)
    {
        var executable = Path.GetFullPath(trayExecutablePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The LucyServer tray executable was not found.", executable);
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        using var agentProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the LucyServer tray application.");
    }
}
