using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using Microsoft.Win32;
using Vnc.Service;

namespace Vnc.Tray;

internal sealed class LucyVncServerConfigurationForm : Form
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LucyVNC.Server";
    private const string LocalSystemSid = "S-1-5-18";
    private readonly CheckBox _sharingEnabled = new() { Text = "Habilitar compartilhamento autenticado" };
    private readonly TextBox _bindAddress = new() { Text = IPAddress.Loopback.ToString() };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 5901 };
    private readonly TextBox _windowsUser = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, MaxLength = 8 };
    private readonly CheckBox _serviceAutomatic = new() { Text = "Iniciar o serviço automaticamente com o Windows" };
    private readonly CheckBox _agentAtLogon = new() { Text = "Iniciar LucyVNC Server e Agent no logon (somente conta atual)" };
    private readonly TextBox _diagnostics = new()
    {
        ReadOnly = true,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Height = 105,
    };
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 5000 };

    public LucyVncServerConfigurationForm()
    {
        Text = "LucyVNC Server Configuration";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 660);
        Size = new Size(720, 760);
        Font = new Font("Segoe UI", 9);

        var currentUser = WindowsIdentity.GetCurrent();
        _windowsUser.Text = currentUser.Name;
        _windowsUser.TextChanged += (_, _) => UpdateAgentStartupAvailability();
        BuildLayout();
        LoadConfiguration();
        UpdateAgentStartupAvailability();
        RefreshDiagnostics();
        _statusTimer.Tick += (_, _) => RefreshDiagnostics();
        _statusTimer.Start();
        FormClosed += (_, _) => _statusTimer.Dispose();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "LucyVNC Server",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        });

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        root.Controls.Add(settings);

        AddSetting(settings, "Estado do compartilhamento", _sharingEnabled);
        AddSetting(settings, "Endereço de bind (loopback ou IP privado/VPN)", _bindAddress);
        AddSetting(settings, "Porta TCP", _port);
        AddSetting(settings, "Usuário Windows do Agent", _windowsUser);
        AddSetting(settings, "Nova senha VNC (8 caracteres ASCII)", _password);
        AddSetting(settings, "Inicialização do serviço", _serviceAutomatic);
        AddSetting(settings, "Inicialização do Agent no logon", _agentAtLogon);
        AddSetting(settings, "Diagnóstico", _diagnostics);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        root.Controls.Add(actions);
        AddButton(actions, "Salvar configuração protegida", SaveConfigurationAsync, primary: true);
        AddButton(actions, "Instalar serviço...", InstallServiceAsync);
        AddButton(actions, "Iniciar", () => RunServiceScriptAsync("Start-LucyServerService.ps1"));
        AddButton(actions, "Parar", () => RunServiceScriptAsync("Stop-LucyServerService.ps1"));
        AddButton(actions, "Reiniciar", () => RunServiceScriptAsync("Restart-LucyServerService.ps1"));
        AddButton(actions, "Remover serviço...", UninstallServiceAsync);
        root.Controls.Add(new Label
        {
            Text = "As credenciais são protegidas com DPAPI e ACL de SYSTEM/Administradores. VNC clássico não cifra o tráfego. O acesso fica limitado a loopback ou endereços privados/VPN.",
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 12, 0, 0),
        });
    }

    private static void AddSetting(TableLayoutPanel layout, string label, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 8, 7),
        }, 0, row);
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 4, 0, 4);
        layout.Controls.Add(control, 1, row);
    }

    private static void AddButton(FlowLayoutPanel panel, string text, Func<Task> action, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            UseVisualStyleBackColor = true,
            Padding = new Padding(8, 4, 8, 4),
            BackColor = primary ? Color.FromArgb(95, 55, 190) : SystemColors.Control,
            ForeColor = primary ? Color.White : SystemColors.ControlText,
        };
        button.Click += async (_, _) => await action();
        panel.Controls.Add(button);
    }

    private void LoadConfiguration()
    {
        try
        {
            var configuration = new ServiceConfigurationStore().LoadExisting();
            _sharingEnabled.Checked = configuration.RemoteSharingEnabled;
            _bindAddress.Text = configuration.BindAddress;
            _port.Value = configuration.Port;
            _serviceAutomatic.Checked = configuration.StartAutomatically;
            if (!string.IsNullOrWhiteSpace(configuration.AgentSid))
            {
                var accountSid = new SecurityIdentifier(configuration.AgentSid);
                _windowsUser.Text = ((NTAccount)accountSid.Translate(typeof(NTAccount))).Value;
            }
            _agentAtLogon.Checked = _agentAtLogon.Enabled && IsAgentStartupEnabled();
        }

        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Security.Principal.IdentityNotMappedException or System.Text.Json.JsonException or InvalidDataException)
        {
            _diagnostics.Text = $"Não foi possível carregar a configuração: {exception.Message}";
        }
    }

    private void UpdateAgentStartupAvailability()
    {
        var currentSid = WindowsIdentity.GetCurrent().User;
        try
        {
            var configuredSid = (SecurityIdentifier)new NTAccount(_windowsUser.Text.Trim())
                .Translate(typeof(SecurityIdentifier));
            _agentAtLogon.Enabled = currentSid is not null && configuredSid.Equals(currentSid);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Security.Principal.IdentityNotMappedException or System.Security.SecurityException)
        {
            _agentAtLogon.Enabled = false;
        }

        if (!_agentAtLogon.Enabled)
        {
            _agentAtLogon.Checked = false;
        }
    }

    private async Task SaveConfigurationAsync()
    {
        var password = _password.Text;
        _password.Clear();
        string? requestPath = null;
        try
        {
            if (!IPAddress.TryParse(_bindAddress.Text.Trim(), out var bindAddress) ||
                !ServiceConfiguration.IsAllowedBindAddress(bindAddress))
            {
                throw new InvalidDataException("Informe loopback ou um IP privado/VPN/link-local. Endereços públicos não são aceitos.");
            }

            if (password.Length != 8 || password.Any(character => character > 0x7f))
            {
                throw new InvalidDataException("A senha VNC precisa ter exatamente oito caracteres ASCII.");
            }

            var account = new NTAccount(_windowsUser.Text.Trim());
            var agentSid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            var currentSid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Não foi possível identificar o usuário interativo.");
            if (_agentAtLogon.Checked && !agentSid.Equals(currentSid))
            {
                throw new InvalidDataException("Para registrar o Agent no logon, o usuário autorizado precisa ser o usuário Windows desta sessão.");
            }

            if (!IPAddress.IsLoopback(bindAddress) &&
                MessageBox.Show(
                    $"O LucyVNC Server ficará disponível em {bindAddress}:{_port.Value}. VNC clássico não cifra o tráfego. Continue somente em uma rede confiável/VPN.",
                    "Confirmar compartilhamento",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            var configuration = new ServiceConfiguration
            {
                RemoteSharingEnabled = _sharingEnabled.Checked,
                BindAddress = bindAddress.ToString(),
                Port = (int)_port.Value,
                StartAutomatically = _serviceAutomatic.Checked,
                AuthenticationRequired = true,
                MaxSessions = 1,
                AgentSid = agentSid.Value,
                AgentWorkerSid = LocalSystemSid,
            };
            requestPath = ServiceConfigurationProvisioner.WriteProtectedRequest(
                new ServiceConfigurationRequest(configuration, password));
            await RunElevatedConfigurationAsync(requestPath);

            SetAgentStartupEnabled(_agentAtLogon.Checked);
            if (_agentAtLogon.Checked)
            {
                StartTray();
            }

            _diagnostics.Text = "Configuração protegida aplicada. Confirme abaixo se o Agent autenticou e o listener ficou pronto.";
            RefreshDiagnostics();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, exception.Message, "LucyVNC Server Configuration", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _diagnostics.Text = $"Falha ao salvar/aplicar a configuração: {exception.Message}";
        }
        finally
        {
            password = string.Empty;
            if (requestPath is not null && File.Exists(requestPath))
            {
                ServiceConfigurationProvisioner.DeleteProtectedRequest(requestPath);
            }
        }
    }

    private async Task RunElevatedConfigurationAsync(string requestPath)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "LucyVNC.Server.Worker.exe");
        if (!File.Exists(workerPath))
        {
            ServiceConfigurationProvisioner.DeleteProtectedRequest(requestPath);
            throw new FileNotFoundException("O componente LucyVNC Server Worker não está instalado.", workerPath);
        }

        var startInfo = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--configure");
        startInfo.ArgumentList.Add(requestPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Não foi possível iniciar o provisionador elevado do LucyVNC Server.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"O provisionador do LucyVNC Server terminou com código {process.ExitCode}.");
        }
    }

    private async Task InstallServiceAsync()
    {
        await RunServiceScriptAsync(
            "Install-LucyServerService.ps1",
            "-ExecutablePath",
            Path.Combine(AppContext.BaseDirectory, "LucyVNC.Server.Worker.exe"),
            "-StartupType",
            _serviceAutomatic.Checked ? "Automatic" : "Manual");
    }

    private async Task UninstallServiceAsync()
    {
        if (MessageBox.Show(
                this,
                "Remover o serviço LucyVNC Server? A configuração e a senha protegida serão preservadas.",
                "Remover serviço",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        await RunServiceScriptAsync("Uninstall-LucyServerService.ps1", "-Force");
    }

    private async Task RunServiceScriptAsync(string scriptName, params string[] arguments)
    {
        try
        {
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", scriptName);
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException("O script de gerenciamento do LucyVNC Server não foi instalado.", scriptPath);
            }

            var executable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Não foi possível iniciar a operação elevada do serviço.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"A operação do serviço terminou com código {process.ExitCode}.");
            }

            RefreshDiagnostics();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _diagnostics.Text = $"Operação do serviço falhou: {exception.Message}";
        }
    }

    private void RefreshDiagnostics()
    {
        var service = WindowsServiceStatusProbe.GetDiagnostic();
        var runtimeStore = new ServiceRuntimeStatusStore(new ServiceConfigurationStore().RuntimeStatusPath);
        try
        {
            var runtime = runtimeStore.Read();
            if (runtime is null)
            {
                _diagnostics.Text = $"{service}{Environment.NewLine}Runtime: aguardando estado publicado pelo Worker.";
                return;
            }

            var fresh = DateTimeOffset.UtcNow - runtime.UpdatedUtc <= TimeSpan.FromSeconds(20);
            _diagnostics.Text = $"{service}{Environment.NewLine}Runtime {(fresh ? "atual" : "desatualizado")}: {runtime.State}. {runtime.Detail}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException or InvalidDataException)
        {
            _diagnostics.Text = $"{service}{Environment.NewLine}Diagnóstico de runtime indisponível: {exception.Message}";
        }
    }

    private static bool IsAgentStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string;
    }

    private static void SetAgentStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Não foi possível abrir a chave de inicialização do usuário.");
        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }

        key.DeleteValue("LucyVNC.ServerTray", throwOnMissingValue: false);
        key.DeleteValue("LucyVNC.SessionAgent", throwOnMissingValue: false);
    }

    private static void StartTray()
    {
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Não foi possível iniciar a bandeja LucyVNC Server.");
    }
}
