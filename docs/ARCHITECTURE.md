# Architecture and security boundaries

## Implemented in this build

- `LucyViewer.exe` is the WPF desktop application. `MainWindow` is the LucyVNC Manager and shows computers and the open-session list; `RemoteWorkspaceWindow` is a separate taskbar/Alt+Tab window containing the remote desktop tabs.
- Each open tab retains its own `VncViewerSession`, framebuffer bitmap, input state and continuous receive loop. Selecting another tab changes only which framebuffer is displayed; it does not stop or recreate the other session. Closing a tab disposes only its session. The Manager keeps disconnected sessions visible for explicit reconnection.
- The Remote Workspace closes after the last tab is removed and the Manager clears its window reference on `Closed`. A new connection creates a fresh Workspace. Closing the Workspace with tabs open asks once for confirmation; accepted session cleanup is deferred until the cancelled WPF `Closing` event has returned. An empty Workspace action in the Manager returns to computer selection instead of opening a blank window.
- `Vnc.Infrastructure.LocalComputerStore` creates `%LOCALAPPDATA%\LucyVNC\lucy.db` and applies schema migrations through version 2. The schema stores computers, favorites, groups, display preferences, sort preference, connection history, and only a validated Credential Manager target reference. Password values are rejected by the store.
- The Manager asks for a VNC password when no endpoint credential is available. Remembering it is an explicit opt-in and uses the signed-in user's Windows Credential Manager; the endpoint key includes normalized host and port. A remembered password rejected by the peer is deleted and replaced only after another user prompt. Passwords are never logged or written to SQLite.
- Manager and Workspace use the shared dark-purple WPF resource dictionary. The computer list is presented as selectable cards; its connect actions use the same session de-duplication and lifecycle path as double-click.
- `Vnc.Protocol` remains the shared RFB implementation. `Vnc.Viewer` owns each outbound TCP connection; individual session state and input are isolated.
- `Vnc.Server` remains a foreground server library. `Vnc.Platform.Windows` uses GDI and `SendInput` from the process's interactive Windows session.
- The Manager session list is not a framebuffer host. `RemoteWorkspaceWindow` owns presentation tabs and is separate from the Manager; an individual session tab cannot yet be detached into a second workspace window.
- Classic VNC authentication remains unencrypted and has the protocol's effective eight-character ASCII password limit. Windows Credential Manager protects stored credentials at rest for the current Windows user; it does not encrypt the RFB network traffic.

## Not yet implemented

- `LucyServer` Worker Service and SCM status/control.
- `LucySessionAgent`, service-to-agent launch/attach behavior, and an authenticated, ACL-restricted local IPC protocol.
- Durable server configuration/secret protection appropriate to a service identity.
- Service-side incoming session audit records (peer, authentication result, start/end and duration).
- Service install/remove scripts, optional Viewer/Server product selection, and a single product installer.
- Service lifecycle, IPC identity/ACL, migration of legacy configuration, and interactive agent tests.
- Detaching a single session tab into a different independent workspace window and re-grouping it elsewhere. The current workspace window itself can be moved between monitors.
- WPF lifecycle tests exercise closing the only tab, retaining a two-tab Workspace until its last tab closes, repeated reopen cycles, close-confirmation cancellation/acceptance, manager shutdown, empty-window actions, and maximized-state preservation. These tests use synthetic tabs; live VNC connection teardown remains covered by the separate network integration tests for simultaneous sessions, framebuffer/input isolation, continued delivery to the remaining session after a peer closes, and reconnect to the released endpoint. Credential target normalization, schema migration, database rejection of secret-like values, and an isolated Windows Credential Manager write/read/delete round-trip have automated coverage.

These are deliberate gaps, not working features behind a UI. The Acessos page labels the current Server as foreground-only and reports that the Windows Service is not installed. No service, scheduled task, startup entry, or firewall rule is created.

## Required design constraints for the service phase

1. Keep the network listener and RFB protocol independent from the Viewer UI.
2. The service must not call GDI capture or `SendInput` from Session 0. A separately launched user-session agent must perform those operations only after an explicit user enable action.
3. Restrict the agent IPC endpoint to the intended service and logged-in user/session. Authenticate the peer identity in addition to using a random pipe name; bound message sizes and reject malformed or stale requests.
4. Keep remote sharing disabled until the user enables it. Do not implement secure-desktop capture, UAC bypass, hidden persistence, or silent administrative changes.
5. Store no passwords in logs. If persistent credentials are later offered, require explicit opt-in and use Windows Credential Manager or DPAPI with an ACL appropriate to the service identity.
6. Preserve loopback/private/VPN interface authorization, required authentication, current session limit policy, and deterministic release of pressed input on every disconnect and service stop.

The current foreground Server is suitable only for interactive, explicit testing; it is not a substitute for the planned service and session-agent model.
