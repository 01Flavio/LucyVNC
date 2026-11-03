using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;
using Vnc.Service;

namespace Vnc.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Could not resolve the LucyServer tray user SID.");
        using var singleInstance = new Mutex(true, $@"Local\LucyVNC.ServerTray.{sid}", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        using var context = new LucyTrayContext();
        Application.Run(context);
    }
}

internal sealed class LucyTrayContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LucyVNC.ServerTray";
    private const string LegacyRunValueName = "LucyVNC.SessionAgent";
    private const string LocalSystemSid = "S-1-5-18";
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _serviceStatusItem;
    private readonly ToolStripMenuItem _agentStatusItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly string _managerPath;
    private readonly string _serviceScriptsPath;
    private readonly string _agentPath;
    private Process? _agentSupervisor;
    private bool _shutdown;

    public LucyTrayContext()
    {
        var applicationDirectory = AppContext.BaseDirectory;
        _managerPath = Path.Combine(applicationDirectory, "LucyViewer.exe");
        _serviceScriptsPath = Path.Combine(applicationDirectory, "Scripts");
        _agentPath = Path.Combine(applicationDirectory, "Agent", "Vnc.Agent.exe");

        var menu = new ContextMenuStrip();
        _serviceStatusItem = AddStatusItem(menu, "Serviço: consultando...");
        _agentStatusItem = AddStatusItem(menu, "Agent: consultando...");
        menu.Items.Add(new ToolStripSeparator());
        var startItem = new ToolStripMenuItem("Iniciar compartilhamento persistente");
        startItem.Click += (_, _) => StartSharing();
        menu.Items.Add(startItem);
        var stopItem = new ToolStripMenuItem("Parar compartilhamento persistente");
        stopItem.Click += (_, _) =>
        {
            if (MessageBox.Show(
                    "Parar o compartilhamento persistente? O serviço poderá ser iniciado novamente pela bandeja ou pelo Manager.",
                    "LucyServer",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                RunServiceScript("Stop-LucyServerService.ps1");
            }
        };
        menu.Items.Add(stopItem);
        menu.Items.Add(new ToolStripSeparator());
        var openManagerItem = new ToolStripMenuItem("Abrir configurações no LucyVNC Manager");
        openManagerItem.Click += (_, _) => OpenManager();
        menu.Items.Add(openManagerItem);
        _autoStartItem = new ToolStripMenuItem("Iniciar bandeja e Agent no logon")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled(),
        };
        _autoStartItem.CheckedChanged += AutoStartCheckedChanged;
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Sair da bandeja (serviço permanece ativo)");
        exitItem.Click += (_, _) => ExitTray();
        menu.Items.Add(exitItem);

        var icon = System.Drawing.Icon.ExtractAssociatedIcon(_managerPath) ?? System.Drawing.SystemIcons.Application;
        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "LucyServer",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenManager();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();
    }

    private static ToolStripMenuItem AddStatusItem(ContextMenuStrip menu, string text)
    {
        var item = new ToolStripMenuItem(text) { Enabled = false };
        menu.Items.Add(item);
        return item;
    }

    private void RefreshStatus()
    {
        TryStartAgentSupervisor();
        _serviceStatusItem.Text = LimitText($"Serviço: {WindowsServiceStatusProbe.GetDiagnostic()}");
        try
        {
            var status = new ServiceRuntimeStatusStore(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "LucyVNC",
                    "Service",
                    "runtime-status.json")).Read();
            if (status is null)
            {
                _agentStatusItem.Text = "Agent/Listener: sem diagnóstico de runtime";
                return;
            }

            var fresh = DateTimeOffset.UtcNow - status.UpdatedUtc <= TimeSpan.FromSeconds(20);
            _agentStatusItem.Text = !fresh
                ? $"Agent/Listener: diagnóstico desatualizado ({status.State})"
                : status.ListenerActive
                    ? $"Agent: autenticado; RFB ativo em {status.BindAddress}:{status.Port}"
                    : $"Agent/Listener: {status.State} — {status.Detail}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException or InvalidDataException)
        {
            _agentStatusItem.Text = LimitText($"Agent/Listener: não foi possível ler o estado ({exception.Message})");
        }
    }

    private void StartSharing()
    {
        TryStartAgentSupervisor();
        RunServiceScript("Start-LucyServerService.ps1");
    }

    private void TryStartAgentSupervisor()
    {
        try
        {
            if (_agentSupervisor is { HasExited: false })
            {
                return;
            }

            _agentSupervisor?.Dispose();
            _agentSupervisor = null;
            var configuration = new ServiceConfigurationStore().LoadExisting();
            var currentSid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(configuration.AgentSid) ||
                !string.Equals(configuration.AgentSid, currentSid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!File.Exists(_agentPath))
            {
                throw new FileNotFoundException("LucyVNC Agent executable was not found.", _agentPath);
            }

            var startInfo = new ProcessStartInfo(_agentPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_agentPath)!,
            };
            startInfo.ArgumentList.Add("--supervise");
            startInfo.ArgumentList.Add("--authorized-worker-sid");
            startInfo.ArgumentList.Add(LocalSystemSid);
            _agentSupervisor = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the interactive Agent supervisor.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException or InvalidOperationException)
        {
            _agentStatusItem.Text = LimitText($"Agent supervisor: {exception.Message}");
        }
    }

    private void RunServiceScript(string scriptName)
    {
        var scriptPath = Path.Combine(_serviceScriptsPath, scriptName);
        if (!File.Exists(scriptPath))
        {
            _agentStatusItem.Text = $"Service action unavailable: {scriptPath}";
            return;
        }

        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(powerShell)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = _serviceScriptsPath,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the elevated service operation.");
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            _agentStatusItem.Text = "Service action cancelled at the UAC prompt.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _agentStatusItem.Text = LimitText($"Service action failed: {exception.Message}");
        }
    }

    private void OpenManager()
    {
        if (!File.Exists(_managerPath))
        {
            _agentStatusItem.Text = $"LucyVNC Manager was not found: {_managerPath}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_managerPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _agentStatusItem.Text = LimitText($"LucyVNC Manager não abriu: {exception.Message}");
        }
    }

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string || key?.GetValue(LegacyRunValueName) is string;
    }

    private void AutoStartCheckedChanged(object? sender, EventArgs e)
    {
        try
        {
            SetAutoStart(_autoStartItem.Checked);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException)
        {
            MessageBox.Show(exception.Message, "LucyServer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _autoStartItem.CheckedChanged -= AutoStartCheckedChanged;
            _autoStartItem.Checked = !_autoStartItem.Checked;
            _autoStartItem.CheckedChanged += AutoStartCheckedChanged;
        }
    }

    private void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");
        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }

        key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
    }

    private void ExitTray()
    {
        _shutdown = true;
        _statusTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        if (!_shutdown)
        {
            ExitTray();
            return;
        }

        _statusTimer.Dispose();
        _agentSupervisor?.Dispose();
        _agentSupervisor = null;
        base.ExitThreadCore();
    }

    private static string LimitText(string value) =>
        value.Length <= 62 ? value : value[..59] + "...";
}
