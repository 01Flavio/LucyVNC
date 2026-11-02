# Installation and operation

## Current development build

LucyVNC currently publishes the WPF Viewer only. There is no product installer and no install/remove script for a Windows Service in this build.

From a Windows PowerShell prompt at the solution root:

```powershell
dotnet restore "LucyVNC.slnx"
dotnet publish "src/Vnc.App/Vnc.App.csproj" -c Release -r win-x64 --self-contained true
Start-Process ".\src\Vnc.App\bin\Release\net8.0-windows\win-x64\publish\LucyViewer.exe"
```

Publishing does not install a service, configure startup, or create Windows Firewall rules. Remove the published directory to remove this development build.

## Outgoing Viewer connections

1. Register a computer with a hostname or an IP address (either one is sufficient), port, friendly name, optional group, and display preference.
2. Double-click the computer or choose **Conectar**. If no remembered credential exists for that normalized host and port, enter the VNC password in the prompt.
3. Password saving is opt-in in that prompt. If selected, the credential is saved in the current Windows user's Credential Manager only after authentication succeeds. It is never stored in SQLite. Use **Remover credencial lembrada** on the selected computer to delete it.
4. Use **Recentes** for authenticated Viewer history, **Favoritos** for marked computers, and the per-session toolbar to disconnect, choose scaling, or enable view-only.

The password dialog accepts the classic VNC maximum of eight ASCII characters. A stored credential rejected by a server is removed; LucyVNC prompts for a replacement once and does not retry indefinitely. VNC Authentication does not encrypt the network session, even when a password is remembered. Prefer loopback or a trusted LAN/VPN.

## Foreground incoming access

The **Acessos** page operates the existing foreground Server in the logged-in interactive process. Starting it requires an explicit user action and a password of exactly eight ASCII characters. The default bind address is `127.0.0.1`; a non-loopback private/VPN address requires confirmation. One incoming session is allowed because Windows input state is shared.

Do not expose classic VNC directly to an untrusted network. The protocol does not encrypt framebuffer or input traffic. Use loopback or a trusted VPN/tunnel and configure any firewall rule manually and narrowly. LucyVNC does not make firewall changes.

## Future Windows Service deployment

Service deployment is intentionally unavailable until the service/agent boundary is implemented and tested. Do not register `LucyViewer.exe` as a service: WPF, screen capture, and `SendInput` require the user's interactive session, while a Windows Service normally runs in Session 0. A future installer must separately install the service and Viewer, use a user-visible interactive agent, authenticate and ACL its local IPC, and require explicit consent before enabling remote control.
