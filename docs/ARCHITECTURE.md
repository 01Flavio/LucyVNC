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
- `Vnc.Service` is a separate .NET 8 Worker Service host registered under SCM name `LucyVNCService`. Its current worker loads validated configuration, emits a redacted status diagnostic, and remains alive for SCM lifecycle testing. `ServiceSharingController` intentionally refuses all sharing when enabled because it has no attached interactive desktop agent; it never binds a socket. The Manager queries SCM state but does not install, start, stop, or configure the service.
- Service configuration defaults to `RemoteSharingEnabled = false`, loopback, port 5900, mandatory authentication, and one session. The JSON file is created under `%ProgramData%\LucyVNC\Service\service.json`; ACLs are restricted to SYSTEM and Administrators and reparse-point paths/files are rejected. There is currently no service-side password storage, listener, authentication-attempt limiter, or live VNC session diagnostics.
- `scripts\Install-LucyServerService.ps1` installs the published executable as Automatic but leaves it stopped and configures at most three delayed SCM restart attempts. Start, stop, status, and uninstall are separate explicit administrator scripts; they warn that no VNC sharing is available and never alter firewall rules. The scripts were syntax-checked only and have not been executed against SCM.
- `Vnc.Infrastructure.SavedComputerImportExportService` reads/writes version-1 JSON (`schemaVersion`, `computers`) and header-based CSV. It validates each record, normalizes endpoint aliases for duplicate/conflict detection, previews an import plan, and applies creates/updates in one SQLite transaction. Conflicts default to Ignore. Updates replace portable settings but retain local favorite/history/credential fields; import records cannot provide Credential Manager references. Export supports all records, favorites, and selected IDs, and excludes IDs, history, and all secrets. The Manager limits selected import files to 10 MiB.
- The Manager session list is not a framebuffer host. `RemoteWorkspaceWindow` owns presentation tabs and is separate from the Manager; an individual session tab cannot yet be detached into a second workspace window.
- Classic VNC authentication remains unencrypted and has the protocol's effective eight-character ASCII password limit. Windows Credential Manager protects stored credentials at rest for the current Windows user; it does not encrypt the RFB network traffic.

## Not yet implemented

- `LucySessionAgent`, service-to-agent launch/attach behavior, and an authenticated, ACL-restricted local IPC protocol.
- VNC listener, authentication/session lifecycle in the service, authentication-attempt limiting, and service-side session diagnostics. The current Worker Service is not a usable remote desktop server.
- Durable service credential handling, audit records, an integrated installer, and Manager actions for installing/starting/stopping/restarting the service. Service scripts are manual and require elevation.
- Session 0 capture/input, secure-desktop access, UAC bypass, pre-login control, agent login/logout/lock/session-switch lifecycle, and a real IPC identity/ACL test suite.
- Detaching a single session tab into a different independent workspace window and re-grouping it elsewhere. The current workspace window itself can be moved between monitors.
- WPF lifecycle tests exercise closing the only tab, retaining a two-tab Workspace until its last tab closes, repeated reopen cycles, close-confirmation cancellation/acceptance, manager shutdown, empty-window actions, and maximized-state preservation. These tests use synthetic tabs; live VNC connection teardown remains covered by the separate network integration tests for simultaneous sessions, framebuffer/input isolation, continued delivery to the remaining session after a peer closes, and reconnect to the released endpoint. Credential target normalization, schema migration, database rejection of secret-like values, and an isolated Windows Credential Manager write/read/delete round-trip have automated coverage.

These are deliberate gaps, not working features behind a UI. The Acessos page labels the current Server as foreground-only and reports the SCM state of the separate worker. No service, scheduled task, startup entry, or firewall rule is created automatically. The Windows Service scripts can install/start the worker only after an administrator explicitly runs them; that worker still does not share a desktop.

## Required design constraints for the service phase

1. Keep the network listener and RFB protocol independent from the Viewer UI.
2. The service must not call GDI capture or `SendInput` from Session 0. A separately launched user-session agent must perform those operations only after an explicit user enable action.
3. Restrict the agent IPC endpoint to the intended service and logged-in user/session. Authenticate the peer identity in addition to using a random pipe name; bound message sizes and reject malformed or stale requests.
4. Keep remote sharing disabled until the user enables it. Do not implement secure-desktop capture, UAC bypass, hidden persistence, or silent administrative changes.
5. Store no passwords in logs. If persistent credentials are later offered, require explicit opt-in and use Windows Credential Manager or DPAPI with an ACL appropriate to the service identity.
6. Preserve loopback/private/VPN interface authorization, required authentication, current session limit policy, and deterministic release of pressed input on every disconnect and service stop.

## Connection import/export schema

Native JSON is an object with `schemaVersion: 1` and a `computers` array. Each object contains only `name`, optional `hostname`, optional `ipAddress`, `port`, optional `group`, `isFavorite`, and `viewPreference` (`fit` or `100%`). At least one of hostname or IP is required. CSV has the columns `name,hostname,ipAddress,port,group,isFavorite,viewPreference`; `name` and `port` are mandatory, and an empty favorite value means false.

Import planning reports invalid records, repeated endpoints within the file, and conflicts with saved endpoints. Endpoint matching is by any normalized hostname/IP alias plus port, so a saved hostname paired with the same imported IP can be a conflict when the saved record contains both aliases. A plan applies atomically to SQLite; conflict policy is Ignore by default, with explicit Update and Create options. Update preserves the target record's favorite flag, last connection history, and stored Credential Manager target. Export includes settings only and is suitable for migrating records, not credentials.

The current foreground Server is suitable only for interactive, explicit testing; it is not a substitute for the planned service and session-agent model.
