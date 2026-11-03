# Installation and operation

## Self-contained application and installer

The repository includes an Inno Setup definition for the Manager, Worker, Agent, and service scripts. To build the x64 installer, publish each executable before compiling the installer:

```powershell
dotnet restore "LucyVNC.slnx"
dotnet publish "src/Vnc.App/Vnc.App.csproj" -c Release -r win-x64 --self-contained true
dotnet publish "src/Vnc.Service/Vnc.Service.csproj" -c Release -r win-x64 --self-contained true
dotnet publish "src/Vnc.Agent/Vnc.Agent.csproj" -c Release -r win-x64 --self-contained true
dotnet publish "src/Vnc.Tray/Vnc.Tray.csproj" -c Release -r win-x64 --self-contained true
& "C:\Users\Zero\AppData\Local\Programs\Inno Setup 6\ISCC.exe" "installer\LucyVNC.iss"
```

The installer output is written to `artifacts\LucyVNC-Setup-2.3.1-x64.exe`. Installing the application does not install/start LucyServer, register the Agent, or create firewall rules. Launch `LucyViewer.exe` from the installed application directory.

To publish only the WPF Manager for development:

```powershell
dotnet publish "src/Vnc.App/Vnc.App.csproj" -c Release -r win-x64 --self-contained true
Start-Process ".\src\Vnc.App\bin\Release\net8.0-windows\win-x64\publish\LucyViewer.exe"
```

Publishing does not install a service, configure startup, or create Windows Firewall rules. Remove the published directory to remove a development build.

Connection settings can be transferred from **Importar conexões** / **Exportar conexões** in the Manager. The files are configuration-only and contain no VNC passwords or Credential Manager data. The version-1 JSON shape and CSV columns are documented in the root [README](../README.md#connection-transfer-files).

## Outgoing Viewer connections

1. Register a computer with a hostname or an IP address (either one is sufficient), port, friendly name, optional group, and display preference.
2. Double-click the computer or choose **Conectar**. If no remembered credential exists for that normalized host and port, enter the VNC password in the prompt.
3. Password saving is opt-in in that prompt. If selected, the credential is saved in the current Windows user's Credential Manager only after authentication succeeds. It is never stored in SQLite. Use **Remover credencial lembrada** on the selected computer to delete it.
4. Use **Recentes** for authenticated Viewer history, **Favoritos** for marked computers, and the per-session toolbar to disconnect, choose scaling, or enable view-only.

The password dialog accepts the classic VNC maximum of eight ASCII characters. A stored credential rejected by a server is removed; LucyVNC prompts for a replacement once and does not retry indefinitely. VNC Authentication does not encrypt the network session, even when a password is remembered. Prefer loopback or a trusted LAN/VPN.

## Foreground incoming access

The **Acessos** page operates the existing foreground Server in the logged-in interactive process. Starting it requires an explicit user action and a password of exactly eight ASCII characters. The default bind address is `127.0.0.1`; a non-loopback private/VPN address requires confirmation. One incoming session is allowed because Windows input state is shared.

Do not expose classic VNC directly to an untrusted network. The protocol does not encrypt framebuffer or input traffic. Use loopback or a trusted VPN/tunnel and configure any firewall rule manually and narrowly. LucyVNC does not make firewall changes.

## LucyServer Worker and interactive Agent

The Worker Service and the interactive-session Agent are separate executables. The Agent performs desktop capture and `SendInput` in the logged-in user's session; the Worker owns the RFB listener and connects to the Agent over a versioned named-pipe protocol with Windows SID checks. The Manager's **Acessos** page can save the service configuration through UAC, install/start/stop/restart/uninstall the service through explicit elevated actions, and display SCM diagnostics. A running service is restarted after configuration is saved. The password is stored as DPAPI LocalMachine ciphertext in ProgramData with an ACL for SYSTEM and Administrators; it is not shown back in the Manager. The automated process integration test uses synthetic capture/input and does not prove real desktop capture, real input injection, or operation of an installed service.

The service can be configured for Automatic or Manual startup and is left stopped after installation. To share the interactive desktop, sign in to the intended Windows user, configure the service in the Manager, optionally enable **Iniciar bandeja e Agent no logon**, install the service, and start it. The independent LucyServer tray app launches an Agent supervisor in that user's session and restarts the Agent with bounded backoff after an unexpected failure. Closing the Manager or exiting the tray does not stop the Agent supervisor or service. The supervisor is not managed by SCM and does not broker logon, logout, lock, or user switches. The Worker publishes a runtime status so SCM `Running` is distinguishable from an authenticated Agent and active listener. The Worker opens no listener without the configured Agent and an available interactive `Default` desktop; it closes the listener when that desktop becomes unavailable. Although the Worker is intended to authenticate as LocalSystem to the session Agent, **that identity boundary has not been validated with a real installed service**. Do not treat successful installation or SCM `Running` status as proof that remote desktop sharing is operational.

The LucyServer tray menu displays SCM and runtime status, starts/stops persistent sharing through UAC-approved scripts, opens Manager settings, and can configure the tray to start at the current user's logon. **Sair da bandeja** only exits the tray application. It does not stop the Windows service or its Agent supervisor. The Manager's own tray icon is a separate feature.

Service lifecycle actions in the Manager invoke UAC. The equivalent scripts in `scripts\` also require an elevated PowerShell session. Installation is reversible, leaves the service stopped, and does not configure a firewall. Uninstalling the application is blocked while LucyServer remains installed; remove the service first from **Acessos** or with `Uninstall-LucyServerService.ps1`.

For a controlled **development-only** local test, publish/build the service and Agent first. This run starts the real Agent, which can move the local cursor and type into the interactive desktop when a connected Viewer sends input. Use a disposable test session with no sensitive application focused. VNC transport is unencrypted; this procedure binds only loopback and does not create firewall rules or install a service.

Create a user-only configuration. By default sharing is disabled; the explicit switch below enables the loopback test listener:

```powershell
.\scripts\New-LucyServerDevelopmentConfig.ps1 -EnableSharing
```

The script refuses to replace an existing configuration unless `-Force` is supplied. Review the existing file before using that option.

In **PowerShell window 1**, start the real interactive Agent:

```powershell
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$env:LUCYVNC_AGENT_AUTHORIZED_SID = $sid
& ".\src\Vnc.Agent\bin\Release\net8.0-windows\Vnc.Agent.exe"
```

In **PowerShell window 2**, set the matching Worker identity, enter a temporary eight-character ASCII VNC password, and run console mode:

```powershell
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$env:LUCYVNC_AGENT_SID = $sid
$env:LUCYVNC_AGENT_PIPE = "LucyVNCAgent"
$env:LUCYVNC_SERVICE_CONFIG = Join-Path $env:LOCALAPPDATA "LucyVNC\Development\service.json"
$securePassword = Read-Host "Temporary VNC password (8 ASCII characters)" -AsSecureString
$passwordBuffer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $env:LUCYVNC_VNC_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordBuffer)
    & ".\src\Vnc.Service\bin\Release\net8.0-windows\Vnc.Service.exe" --console
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordBuffer)
    $securePassword.Dispose()
    Remove-Item Env:\LUCYVNC_VNC_PASSWORD -ErrorAction SilentlyContinue
}
```

Connect LucyViewer to `127.0.0.1:5901`. Stop the Worker by entering `STOP` in window 2, then stop the Agent with Ctrl+C in window 1. The password exists in the Worker process environment only; do not capture or share diagnostic output from a test containing sensitive information. Do not use this procedure on a production desktop.

For optional SCM lifecycle testing, publish the executable. The Manager's protected configuration flow stores a durable credential before service startup; the development-only environment variable shown above is an alternative for console tests. SCM state alone does not mean desktop sharing is available:

```powershell
dotnet publish "src/Vnc.Service/Vnc.Service.csproj" -c Release -r win-x64 --self-contained true
```

In an elevated PowerShell session, the scripts below provide the same service lifecycle operations as the Manager. Installation is Automatic but leaves the service stopped; start is a separate action. No service-to-interactive-user Agent launcher is provided, so SCM installation alone does not provide desktop sharing. Stop and uninstall ask for confirmation. None of the scripts creates or edits firewall rules.

```powershell
.\scripts\Install-LucyServerService.ps1
.\scripts\Get-LucyServerServiceStatus.ps1
.\scripts\Start-LucyServerService.ps1
.\scripts\Stop-LucyServerService.ps1
.\scripts\Uninstall-LucyServerService.ps1
```

Do not register `LucyViewer.exe` or `Vnc.Agent.exe` as a Windows Service: WPF, screen capture, and `SendInput` require the user's interactive session, while a Windows Service normally runs in Session 0. The Service/Agent IPC checks Windows user SIDs and protects the pipe with an ACL, but this is not a binary-signing boundary. The SCM-to-interactive-session Agent identity relationship and session lifecycle are not validated. Session 0 provides no supported access to the signed-in desktop; secure desktop, UAC, and pre-login control are not implemented.
