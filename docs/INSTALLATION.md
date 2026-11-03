# Installation and operation

## LucyVNC Viewer

The self-contained WPF application is published separately from the optional LucyServer Worker Service. There is no product installer.

From a Windows PowerShell prompt at the solution root:

```powershell
dotnet restore "LucyVNC.slnx"
dotnet publish "src/Vnc.App/Vnc.App.csproj" -c Release -r win-x64 --self-contained true
Start-Process ".\src\Vnc.App\bin\Release\net8.0-windows\win-x64\publish\LucyViewer.exe"
```

Publishing does not install a service, configure startup, or create Windows Firewall rules. Remove the published directory to remove this development build.

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

The Worker Service and the interactive-session Agent are separate executables. The Agent performs desktop capture and `SendInput` in the logged-in user's session; the Worker owns the RFB listener and connects to the Agent over a SID-restricted, versioned named-pipe protocol. The automated process integration test uses synthetic capture/input and does not prove real desktop capture, real input injection, SCM startup, or service-to-user-session launch. Production service lifecycle management of the Agent is not implemented.

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

For optional SCM lifecycle testing only, publish the executable. Installing or starting this Worker does not configure a production VNC credential or launch an interactive Agent; SCM state alone does not mean desktop sharing is available:

```powershell
dotnet publish "src/Vnc.Service/Vnc.Service.csproj" -c Release -r win-x64 --self-contained true
```

In an elevated PowerShell session, use the scripts below. Installation is Automatic but leaves the service stopped; start is a separate action. No service-to-interactive-user Agent launcher is provided, so SCM installation alone does not provide desktop sharing. Stop and uninstall ask for confirmation. None of the scripts creates or edits firewall rules.

```powershell
.\scripts\Install-LucyServerService.ps1
.\scripts\Get-LucyServerServiceStatus.ps1
.\scripts\Start-LucyServerService.ps1
.\scripts\Stop-LucyServerService.ps1
.\scripts\Uninstall-LucyServerService.ps1
```

Do not register `LucyViewer.exe` or `Vnc.Agent.exe` as a Windows Service: WPF, screen capture, and `SendInput` require the user's interactive session, while a Windows Service normally runs in Session 0. The versioned Service/Agent IPC checks Windows user SIDs and protects the pipe with an ACL, but has only same-user development validation; it is not a binary-signing boundary. The SCM-to-interactive-session Agent launcher, durable service credential management, and login/logout/lock/session-switch lifecycle are not implemented. Session 0 provides no supported access to the signed-in desktop; secure desktop, UAC, and pre-login control are not implemented.
