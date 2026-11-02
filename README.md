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
7. **Acessos** currently exposes the legacy foreground server for the interactive desktop. It is off by default, requires a VNC password on each start, binds to loopback by default, limits connections to one, and does not create firewall rules.

The local database is `%LOCALAPPDATA%\LucyVNC\lucy.db`. It stores computers, sort/display preferences, and connection history; it has no password column. Schema migrations are tracked with SQLite `user_version`.

## Security and implementation status

- The RFB server requires classic VNC Authentication and restricts binds to loopback or permitted private/VPN/link-local ranges. VNC Authentication does **not** encrypt traffic and only provides an effective eight-character ASCII password.
- The legacy server runs in the current interactive process; it is not a Windows Service. The `LucyServer` Worker Service, interactive `LucySessionAgent`, authenticated/restricted IPC, service installation/removal scripts, and product installer have **not** been implemented in this build. No service is installed or started automatically.
- Session 0 capture/input, secure desktop, UAC bypass, unattended control, automatic firewall changes, TLS/VeNCrypt, Tight encoding, clipboard, and multi-monitor capture are not supported.
- Real Windows desktop input validation remains a separate opt-in check: `dotnet run --project "tests/Vnc.InteractiveProbe/Vnc.InteractiveProbe.csproj" -c Release`.
- Moving/detaching a single session tab into another independent workspace window is not implemented. Sessions can be moved between monitors by moving the whole Remote Workspace window.

See [installation and operation](docs/INSTALLATION.md), [architecture and current boundaries](docs/ARCHITECTURE.md), [protocol scope](docs/PROTOCOL.md), and [interoperability evidence](docs/INTEROPERABILITY.md).
