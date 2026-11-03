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
- `src/Vnc.Service` — Windows Service host, protected configuration, and status/control support for LucyServer
- `src/Vnc.Agent` — interactive-session desktop capture and input agent
- `src/Vnc.Tray` — independent LucyServer notification-area app and per-user Agent supervisor launcher
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
7. **Acessos** exposes both the legacy foreground server and LucyServer configuration/service controls. The foreground server is off by default, requires a VNC password on each start, binds to loopback by default, limits connections to one, and does not create firewall rules.
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
- `Vnc.Service` connects to `Vnc.Agent` over a local named pipe. The Agent ACL permits its session user and configured Worker identity, then verifies the actual impersonated pipe-client SID; the Worker also checks the pipe-server process SID and negotiates IPC version before accepting capture/input requests. The Worker opens no RFB listener until the Agent is authenticated and responsive, and closes the listener on Agent loss before retrying. IPC authenticates Windows user identities, not signed executable identities; processes running as the same Windows user remain within the trust boundary.
- The Manager can save LucyServer configuration through UAC. Sharing defaults to disabled, bind defaults to loopback, authentication is mandatory, port defaults to 5901, and the session limit is one. Production settings are stored under `%ProgramData%\LucyVNC\Service`; the password is stored separately using DPAPI LocalMachine and an ACL for SYSTEM/Administrators. The Manager does not read the password back. Bind addresses are limited to loopback or explicit private/VPN/link-local IPs; public addresses are rejected. Saving settings restarts the service if SCM reports it running.
- Installing, starting, stopping, restarting, and removing the Windows service are available in the Manager and require explicit UAC authorization. The service can be configured as Automatic or Manual and is left stopped after installation; installation does not create firewall rules. The separate LucyServer tray app can start with the user's logon and launches a single Agent supervisor for the configured user. The supervisor uses bounded backoff and restarts the Agent after failures; closing the Manager or exiting the tray does not stop the Worker, Agent, or active VNC sessions.
- The Worker publishes a non-secret runtime status file. The Manager and LucyServer tray distinguish SCM `Running` from the Agent-authenticated, RFB-listener-ready state. The Worker does not open the RFB listener until an authorized Agent is connected and reports the interactive `Default` desktop available; it closes the listener after the desktop becomes unavailable (for example, on lock), then retries Agent connection.
- The process integration test exercises Worker and Agent as separate processes with a synthetic framebuffer/input sink, verifies there is no listener before Agent startup, proves framebuffer and pointer/key event delivery, then stops/restarts the Agent and reconnects. It does **not** prove real desktop capture or actual Windows `SendInput`.
- LucyServer's production SCM-to-interactive-Agent pipe authorization and lifecycle have not been validated with an installed service. The Manager's and tray's SCM/runtime status are diagnostic only and do not prove real desktop capture or input. The optional Inno Setup installer packages the Viewer, Worker, Agent, independent tray app, and service scripts; compiling it does not install or start the service.
- Session 0 capture/input, secure desktop, UAC bypass, unattended control, automatic firewall changes, TLS/VeNCrypt, Tight encoding, clipboard, and multi-monitor capture are not supported.
- Real Windows desktop input validation remains a separate opt-in check: `dotnet run --project "tests/Vnc.InteractiveProbe/Vnc.InteractiveProbe.csproj" -c Release`.
- Moving/detaching a single session tab into another independent workspace window is not implemented. Sessions can be moved between monitors by moving the whole Remote Workspace window.

See [installation and operation](docs/INSTALLATION.md), [architecture and current boundaries](docs/ARCHITECTURE.md), [protocol scope](docs/PROTOCOL.md), and [interoperability evidence](docs/INTEROPERABILITY.md).
