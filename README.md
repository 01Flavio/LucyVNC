# LucyVNC

LucyVNC consists of two independently installable products: **LucyVNC Manager / Viewer** for outgoing remote sessions and **LucyVNC Server** for authenticated incoming desktop sharing. The Manager has no runtime dependency on Server components.

## Solution

- `src/Vnc.App` — WPF LucyVNC Manager / Viewer; published app name is `LucyVNC.Manager.exe`
- `src/Vnc.Core` — shared framebuffer, pixel-format, and connection models
- `src/Vnc.Protocol` — RFB negotiation, VNC Authentication, message framing, Raw and CopyRect encodings
- `src/Vnc.Platform.Windows` — interactive desktop capture and Windows `SendInput`
- `src/Vnc.Server` — authenticated RFB server library
- `src/Vnc.Viewer` — RFB Viewer client
- `src/Vnc.Infrastructure` — versioned SQLite schema, computer records, preferences, and history
- `src/Vnc.Service` — independent Windows Service Worker and protected configuration
- `src/Vnc.Agent` — interactive-session desktop capture and input agent
- `src/Vnc.Tray` — LucyVNC Server notification-area app and LucyVNC Server Configuration
- `tests/*` — protocol, server, Viewer, integration, and SQLite tests

## Build, test, and publish

Run these commands from the repository root in PowerShell:

```powershell
dotnet restore "LucyVNC.slnx"
dotnet build "LucyVNC.slnx" -c Release
dotnet test "LucyVNC.slnx" -c Release
dotnet publish "src/Vnc.App/Vnc.App.csproj" -c Release -r win-x64 --self-contained true -o "artifacts/publish/manager"
dotnet publish "src/Vnc.Service/Vnc.Service.csproj" -c Release -r win-x64 --self-contained true -o "artifacts/publish/server-worker"
dotnet publish "src/Vnc.Agent/Vnc.Agent.csproj" -c Release -r win-x64 --self-contained true -o "artifacts/publish/agent"
dotnet publish "src/Vnc.Tray/Vnc.Tray.csproj" -c Release -r win-x64 --self-contained true -o "artifacts/publish/server-ui"
& "C:\Users\Zero\AppData\Local\Programs\Inno Setup 6\ISCC.exe" "installer\LucyVNC.iss"
```

The self-contained Manager / Viewer is published to:

```text
artifacts\publish\manager\LucyVNC.Manager.exe
```

## Use

1. Open **Computadores** and add a friendly name, hostname or IP address, port, optional group, and display preference. A hostname and IP are independently optional; at least one is required.
2. Select a saved computer and enter its VNC password in the top bar, or enter a destination directly. Passwords are never saved.
3. After successful authentication, LucyVNC records the destination and offers to add an unknown endpoint to the computer list. Existing friendly names are preserved.
4. Each connection opens an independent tab in the separate **Remote Workspace** window. The window has its own taskbar/Alt+Tab entry; switching tabs leaves each session's RFB receive loop running. The Manager lists open sessions and their status.
5. Each tab has independent connect/disconnect and view-only state, framebuffer, pointer/key state, scaling and full-screen controls. **Ctrl+Alt+Del** is explicitly shown as unavailable. A disconnected session remains listed and can be reconnected using the password field.
6. Closing a tab disconnects only that session. The Remote Workspace closes automatically after its last tab is removed and is recreated when another session opens; closing the Workspace directly with tabs open asks for confirmation. The Manager remains open.
7. Use **Importar conexões** and **Exportar conexões** to transfer non-secret computer settings in JSON or CSV. Import is previewed before applying; endpoint conflicts default to ignore. Export supports all entries, the extended-selection set, or favorites.

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

The Manager also imports one or many `.vnc` profiles, including a selected folder's top-level profiles, and opens an individual `.vnc` file directly. The supported profile shape is INI-style text with `Host` and optional `Port`, `ConnectionName`/`Name`, and `Group` key/value options. `Host` may be a DNS name or IP, optionally using `host:display`, `host::tcp-port`, or bracketed IPv6 with `::tcp-port`. Credentials/password options and unsupported settings are ignored and identified by option name only; profiles are never executed. `.vnc` export contains only the destination, port, and connection name, so it cannot preserve groups, favorites, Viewer preferences, or credentials. Imports are limited to 1 MB per `.vnc` file, 10 MB total, and 1,000 selected files; folder scans are not recursive.

## Security and implementation status

- The RFB server requires classic VNC Authentication and restricts binds to loopback or permitted private/VPN/link-local ranges; `0.0.0.0` is allowed only after explicit confirmation and means all local IPv4 interfaces, not automatic Internet exposure. VNC Authentication does **not** encrypt traffic and only provides an effective eight-character ASCII password. Use a trusted LAN or VPN; firewall and routing remain separately administered.
- `Vnc.Service` connects to `Vnc.Agent` over a local named pipe. The Agent ACL permits its session user and configured Worker identity, then verifies the actual impersonated pipe-client SID; the Worker also checks the pipe-server process SID and negotiates IPC version before accepting capture/input requests. The Worker opens no RFB listener until the Agent is authenticated and responsive, and closes the listener on Agent loss before retrying. IPC authenticates Windows user identities, not signed executable identities; processes running as the same Windows user remain within the trust boundary.
- LucyVNC Server Configuration, available from the Server tray, owns server settings and SCM actions. Configuration defaults to sharing disabled, loopback, port 5900, mandatory authentication, and one session. It lists available eligible IPv4 interfaces and checks local port availability before saving. Protected settings live under `%ProgramData%\LucyVNC\Service`; the password uses DPAPI LocalMachine with an ACL for SYSTEM/Administrators. Server actions require UAC; no firewall rules are created.
- LucyVNC Setup offers Manager / Viewer, Server, or both. The Server component has its own setup page for bind, port, password, authorized Windows user, service startup, and Agent logon startup. Service installation is gated by explicit consent. The password is passed to the provisioner only in process environment, never command-line arguments; it is protected before being persisted. Viewer-only selection does not install Worker, Agent, Server tray, or Windows service.
- The standalone Server app provides its own tray, configuration window, SCM controls, and Agent supervisor. It has no dependency on Manager; closing its tray does not stop the service or Agent. The Worker publishes a non-secret runtime status and opens the listener only after authenticating the Agent and confirming the interactive `Default` desktop is available.
- The process integration test exercises Worker and Agent as separate processes with a synthetic framebuffer/input sink, verifies there is no listener before Agent startup, proves framebuffer and pointer/key event delivery, then stops/restarts the Agent and reconnects. It does **not** prove real desktop capture or actual Windows `SendInput`.
- Production SCM-to-interactive-Agent authorization, actual desktop capture/input, logon/reboot startup, and lock/unlock recovery require manual validation with an installed Server. SCM/runtime status does not by itself prove remote desktop capture or input.
- Session 0 capture/input, secure desktop, UAC bypass, unattended control, automatic firewall changes, TLS/VeNCrypt, Tight encoding, clipboard, and multi-monitor capture are not supported.
- Real Windows desktop input validation remains a separate opt-in check: `dotnet run --project "tests/Vnc.InteractiveProbe/Vnc.InteractiveProbe.csproj" -c Release`.
- Moving/detaching a single session tab into another independent workspace window is not implemented. Sessions can be moved between monitors by moving the whole Remote Workspace window.

See [installation and operation](docs/INSTALLATION.md), [architecture and current boundaries](docs/ARCHITECTURE.md), [protocol scope](docs/PROTOCOL.md), and [interoperability evidence](docs/INTEROPERABILITY.md).
