using System.Security.Principal;
using System.Security.Cryptography;
using System.Net;
using System.Windows.Forms;
using Microsoft.Win32;
using Vnc.Service;
using Vnc.Protocol;

namespace Vnc.Tray;

internal static class ServerSetupProvisioner
{
    public const string PasswordEnvironmentVariable = "LUCYVNC_SETUP_PASSWORD";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LucyVNC.Server";

    public static int Run(string[] args)
    {
        string? password = null;
        try
        {
            if (args.Length != 8 ||
                !string.Equals(args[0], "--validate-setup", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(args[0], "--provision-setup", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Expected a LucyVNC Server setup validation or provisioning request.");
            }

            var configuration = CreateConfiguration(args, out var agentAtLogon);
            password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
            Environment.SetEnvironmentVariable(PasswordEnvironmentVariable, null, EnvironmentVariableTarget.Process);
            if (password is null || !VncAuthentication.IsValidServerPassword(password))
            {
                throw new InvalidDataException(
                    "A senha do LucyVNC Server precisa ter de 6 a 8 caracteres ASCII imprimíveis. A autenticação VNC clássica não usa mais de oito.");
            }

            if (string.Equals(args[0], "--validate-setup", StringComparison.OrdinalIgnoreCase))
            {
                ValidatePortAvailability(configuration);
                return 0;
            }

            ValidatePortAvailability(configuration);
            var requestPath = ServiceConfigurationProvisioner.WriteProtectedRequest(
                new ServiceConfigurationRequest(configuration, password));
            try
            {
                _ = ServiceConfigurationProvisioner.ApplyProtectedRequest(requestPath);
            }
            finally
            {
                if (File.Exists(requestPath))
                {
                    ServiceConfigurationProvisioner.DeleteProtectedRequest(requestPath);
                }
            }

            SetAgentStartupEnabled(agentAtLogon);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(
                exception.Message,
                "LucyVNC Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (!string.IsNullOrEmpty(password))
            {
                CryptographicOperations.ZeroMemory(System.Text.Encoding.ASCII.GetBytes(password));
            }
            password = string.Empty;
        }
    }

    private static ServiceConfiguration CreateConfiguration(string[] args, out bool agentAtLogon)
    {
        if (!bool.TryParse(args[1], out var sharingEnabled) ||
            !int.TryParse(args[3], out var port) ||
            !bool.TryParse(args[5], out var startAutomatically) ||
            !bool.TryParse(args[6], out agentAtLogon) ||
            !bool.TryParse(args[7], out var allInterfacesConfirmed))
        {
            throw new InvalidDataException("As opções de instalação do LucyVNC Server são inválidas.");
        }

        var agentSid = (SecurityIdentifier)new NTAccount(args[4]).Translate(typeof(SecurityIdentifier));
        if (agentAtLogon)
        {
            var currentSid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Não foi possível identificar o usuário Windows desta sessão.");
            if (!agentSid.Equals(currentSid))
            {
                throw new InvalidDataException("O início do Agent no logon exige que o usuário autorizado seja o usuário Windows desta sessão.");
            }
        }

        var configuration = new ServiceConfiguration
        {
            RemoteSharingEnabled = sharingEnabled,
            BindAddress = args[2],
            AllInterfacesConfirmed = allInterfacesConfirmed,
            Port = port,
            StartAutomatically = startAutomatically,
            AuthenticationRequired = true,
            MaxSessions = 1,
            AgentSid = agentSid.Value,
            AgentWorkerSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        };
        configuration.Validate();
        return configuration;
    }

    private static void ValidatePortAvailability(ServiceConfiguration configuration)
    {
        var path = new ServiceConfigurationStore().RuntimeStatusPath;
        if (File.Exists(path))
        {
            var status = new ServiceRuntimeStatusStore(path).Read();
            if (status is not null &&
                status.ListenerActive &&
                string.Equals(status.BindAddress, configuration.BindAddress, StringComparison.OrdinalIgnoreCase) &&
                status.Port == configuration.Port)
            {
                return;
            }
        }

        if (!ServiceNetworkBinding.IsPortAvailable(
                IPAddress.Parse(configuration.BindAddress),
                configuration.Port))
        {
            throw new InvalidDataException(
                $"A porta TCP {configuration.Port} não está disponível em {configuration.BindAddress}. Verifique conflitos com outro servidor e escolha outra porta.");
        }
    }

    private static void SetAgentStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");
        if (enabled)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "LucyVNC.Server.exe");
            key.SetValue(RunValueName, $"\"{executable}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }

        key.DeleteValue("LucyVNC.ServerTray", throwOnMissingValue: false);
        key.DeleteValue("LucyVNC.SessionAgent", throwOnMissingValue: false);
    }
}
