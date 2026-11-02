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

## LucyServer Windows Service groundwork

The .NET Worker Service executable and administrator scripts are included. The worker opens an authenticated loopback RFB listener when remote sharing is enabled and the `LUCYVNC_VNC_PASSWORD` environment variable holds an eight-character ASCII VNC password; without both it keeps no listener open. The interactive-session agent and authenticated IPC are not implemented yet, so installing or starting it does not by itself provide remote desktop sharing. The Manager displays Service Control Manager status; service installation and control are currently manual.

If you explicitly need to validate only the worker's SCM lifecycle, publish the executable:

```powershell
dotnet publish "src/Vnc.Service/Vnc.Service.csproj" -c Release -r win-x64 --self-contained true
```

In an elevated PowerShell session, use the scripts below. Installation is Automatic but leaves the service stopped; start is a separate action. Status reports `RemoteSharingAvailable = false`. Stop and uninstall ask for confirmation. None of the scripts creates or edits firewall rules.

```powershell
.\scripts\Install-LucyServerService.ps1
.\scripts\Get-LucyServerServiceStatus.ps1
.\scripts\Start-LucyServerService.ps1
.\scripts\Stop-LucyServerService.ps1
.\scripts\Uninstall-LucyServerService.ps1
```

Do not register `LucyViewer.exe` as a service: WPF, screen capture, and `SendInput` require the user's interactive session, while a Windows Service normally runs in Session 0. A production LucyServer still needs a user-visible session agent, authenticated and ACL-restricted IPC, service-side VNC listener/authentication, login/logout/session-switch handling, and explicit consent before enabling remote control. Session 0 provides no supported access to the signed-in desktop; secure desktop, UAC, and pre-login control are not implemented.
