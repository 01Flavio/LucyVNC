# LucyVNC

LucyVNC is a Windows VNC Viewer and foreground Server built around a shared RFB implementation. The WPF application now includes a local computer manager, SQLite-backed history and preferences, and independent Viewer sessions.

## Solution

- `src/Vnc.App` — WPF LucyVNC Manager and independent Remote Workspace window; published app name is `LucyViewer.exe`
- `src/Vnc.Core` — shared framebuffer, pixel-format, and connection models
- `src/Vnc.Protocol` — RFB negotiation, VNC Authentication, message framing, Raw and CopyRect encodings
- `src/Vnc.Platform.Windows` — interactive desktop capture and Windows `SendInput`
- `src/Vnc.Server` — authenticated RFB server library
- `src/Vnc.Viewer` — RFB Viewer client
- `src/Vnc.Infrastructure` — versioned SQLite schema, computer records, preferences, and history
- `src/Vnc.Service` — Windows Service host and safe configuration/status groundwork for LucyServer
- `tests/*` — protocol, server, Viewer, integration, and SQLite tests

## Build, test, and publish

Run these commands from the repository root in PowerShell:

```powershell
dotnet restore "LucyVNC.slnx"
dotnet build "LucyVNC.slnx" -c Release
dotnet test "LucyVNC.slnx" -c Release
dotnet publish "src/Vnc.App/Vnc.App.csproj" -c Release -r win-x64 --self-contained true
```

The self-contained Viewer is published to:

```text
src\Vnc.App\bin\Release\net8.0-windows\win-x64\publish\LucyViewer.exe
```

## Use

1. Open **Computadores** and add a friendly name, hostname or IP address, port, optional group, and display preference. A hostname and IP are independently optional; at least one is required.
2. Select a saved computer and enter its VNC password in the top bar, or enter a destination directly. Passwords are never saved.
3. After successful authentication, LucyVNC records the destination and offers to add an unknown endpoint to the computer list. Existing friendly names are preserved.
4. Each connection opens an independent tab in the separate **Remote Workspace** window. The window has its own taskbar/Alt+Tab entry; switching tabs leaves each session's RFB receive loop running. The Manager lists open sessions and their status.
5. Each tab has independent connect/disconnect and view-only state, framebuffer, pointer/key state, scaling and full-screen controls. **Ctrl+Alt+Del** is explicitly shown as unavailable. A disconnected session remains listed and can be reconnected using the password field.
6. Closing a tab disconnects only that session. The Remote Workspace closes automatically after its last tab is removed and is recreated when another session opens; closing the Workspace directly with tabs open asks for confirmation. The Manager remains open.
7. **Acessos** exposes the legacy foreground server for the interactive desktop. It is off by default, requires a VNC password on each start, binds to loopback by default, limits connections to one, and does not create firewall rules.
8. Use **Importar conexões** and **Exportar conexões** to transfer non-secret computer settings in JSON or CSV. Import is previewed before applying; endpoint conflicts default to ignore. Export supports all entries, the extended-selection set, or favorites.

The local database is `%LOCALAPPDATA%\LucyVNC\lucy.db`. It stores computers, sort/display preferences, and connection history; it has no password column. Schema migrations are tracked with SQLite `user_version`.

## Connection transfer files

The native JSON format is versioned and contains only portable, non-secret connection settings:

```json
{
  "schemaVersion": 1,
  "computers": [
    {
      "name": "Notebook",
      "hostname": "notebook.example.local",
      "ipAddress": "192.168.15.13",
      "port": 5900,
      "group": "Office",
      "isFavorite": true,
      "viewPreference": "fit"
    }
  ]
}
```

`hostname` and `ipAddress` are each optional, but at least one must be present. `group` is optional; `viewPreference` is `fit` or `100%`. CSV uses the header `name,hostname,ipAddress,port,group,isFavorite,viewPreference`; `name` and `port` are required, and `isFavorite` accepts `true`/`false` (empty means false). Both formats support quoted CSV fields and validate host, port, name, and view preference before import. Same normalized hostname/IP and port within an import file are marked duplicate; aliases matching existing entries are conflicts. Import choices are ignore, update, or create. Updates preserve local favorite state, history, and Credential Manager reference; imports never write or replace credentials. JSON is capped at 10 MB by the Manager. Export deliberately excludes database IDs, connection history, and all credential/secret data. To migrate a computer, transfer this file and enter or explicitly remember its VNC password again on the destination Windows account.

## Security and implementation status

- The RFB server requires classic VNC Authentication and restricts binds to loopback or permitted private/VPN/link-local ranges. VNC Authentication does **not** encrypt traffic and only provides an effective eight-character ASCII password.
- The legacy VNC server runs in the current interactive process. The `Vnc.Service` Worker Service host, safe configuration store, SCM status diagnostic, and opt-in install/start/stop/uninstall scripts are present, but it now opens an authenticated loopback RFB listener when remote sharing is explicitly enabled; the listener reuses the shared RFB server and requires classic VNC Authentication. The interactive-session agent and authenticated, ACL-restricted IPC are not implemented. Installing or starting this worker therefore does not provide remote desktop sharing. No service is installed or started automatically, and the scripts do not change the firewall.
- Service configuration defaults to sharing disabled, loopback bind, required authentication, port 5900, and a one-session limit. The configuration file is stored under `%ProgramData%\LucyVNC\Service` with access restricted to SYSTEM and Administrators; it contains no password. A requested non-loopback bind must be an explicit private/VPN/link-local IP. When remote sharing is enabled and the `LUCYVNC_VNC_PASSWORD` environment variable holds an eight-character ASCII VNC password, the worker opens the authenticated RFB listener on the configured loopback address and port; without both the worker keeps no listener open.
- From an elevated PowerShell prompt, the optional scripts are `scripts\Install-LucyServerService.ps1`, `scripts\Start-LucyServerService.ps1`, `scripts\Stop-LucyServerService.ps1`, `scripts\Get-LucyServerServiceStatus.ps1`, and `scripts\Uninstall-LucyServerService.ps1`. Publish `src\Vnc.Service\Vnc.Service.csproj` for `win-x64` before installing. Installation is Automatic but leaves the service stopped and configures bounded SCM recovery; it does not install or activate sharing. These scripts have not been run as part of ordinary build/test validation.
- Session 0 capture/input, secure desktop, UAC bypass, unattended control, automatic firewall changes, TLS/VeNCrypt, Tight encoding, clipboard, and multi-monitor capture are not supported.
- Real Windows desktop input validation remains a separate opt-in check: `dotnet run --project "tests/Vnc.InteractiveProbe/Vnc.InteractiveProbe.csproj" -c Release`.
- Moving/detaching a single session tab into another independent workspace window is not implemented. Sessions can be moved between monitors by moving the whole Remote Workspace window.

See [installation and operation](docs/INSTALLATION.md), [architecture and current boundaries](docs/ARCHITECTURE.md), [protocol scope](docs/PROTOCOL.md), and [interoperability evidence](docs/INTEROPERABILITY.md).
