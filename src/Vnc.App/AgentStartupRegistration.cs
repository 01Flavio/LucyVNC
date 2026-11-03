using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Vnc.App;

internal static class AgentStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LucyVNC.SessionAgent";
    private const string LocalSystemSid = "S-1-5-18";

    public static bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled, string agentExecutablePath)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");
        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Path.GetFullPath(agentExecutablePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The LucyVNC interactive Agent executable was not found.", executable);
        }

        var escapedExecutable = executable.Replace("'", "''", StringComparison.Ordinal);
        var command = $"powershell.exe -NoProfile -WindowStyle Hidden -Command \"Start-Process -FilePath '{escapedExecutable}' -ArgumentList '--supervise --authorized-worker-sid {LocalSystemSid}' -WindowStyle Hidden\"";
        runKey.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public static void StartNow(string agentExecutablePath)
    {
        var executable = Path.GetFullPath(agentExecutablePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The LucyVNC interactive Agent executable was not found.", executable);
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        startInfo.ArgumentList.Add("--supervise");
        startInfo.ArgumentList.Add("--authorized-worker-sid");
        startInfo.ArgumentList.Add(LocalSystemSid);
        using var agentProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the LucyVNC interactive Agent.");
    }
}
