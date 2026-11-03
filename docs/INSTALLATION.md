# Installation and operation

## LucyVNC Setup 2.4.1

Publish all four win-x64 components to isolated directories and compile the installer with Inno Setup 6.7.3:

```powershell
dotnet restore "LucyVNC.slnx" -r win-x64
dotnet publish "src/Vnc.App/Vnc.App.csproj" -c Release -r win-x64 --self-contained true -o "artifacts/publish/manager"
dotnet publish "src/Vnc.Service/Vnc.Service.csproj" -c Release -r win-x64 --self-contained true -o "artifacts/publish/server-worker"
dotnet publish "src/Vnc.Agent/Vnc.Agent.csproj" -c Release -r win-x64 --self-contained true -o "artifacts/publish/agent"
dotnet publish "src/Vnc.Tray/Vnc.Tray.csproj" -c Release -r win-x64 --self-contained true -o "artifacts/publish/server-ui"
& "C:\Users\Zero\AppData\Local\Programs\Inno Setup 6\ISCC.exe" "installer\LucyVNC.iss"
```

The output is `artifacts\LucyVNC-Setup-2.4.1-x64.exe`. The component page offers **LucyVNC Manager / Viewer**, **LucyVNC Server**, or both. Manager-only installation contains no Server Worker, Agent, tray/configuration executable, or Server service dependency.

When Server is selected, the setup page asks for:

- Sharing enabled/disabled, network mode (default: all IPv4 interfaces, `0.0.0.0`; alternatives: this computer or a specific local interface), and TCP port (default `5900`).
- Classic VNC password of 6–8 printable ASCII characters and the authorized Windows user.
- Automatic or manual service startup and optional tray/Agent startup at that user's logon.
- Separate explicit consent to install or update the Windows service, and an optional choice to start sharing after setup.

The setup lists available IPv4 interface addresses and adapter names where available, validates the selected IP/network scope, port availability, password, and Windows account before continuing, and checks the final configuration with the Server provisioner immediately before protected provisioning. An existing LucyVNC Server listener using the same address/port is allowed during an update; other conflicts are rejected. Selecting all interfaces sets the listener to `0.0.0.0`, requires a separately unchecked confirmation box, and presents an additional security prompt before continuing. This listens on local IPv4 interfaces only; it does not configure firewall, routing, a router, or UPnP. VNC classic traffic is unencrypted, so use a trusted LAN or VPN and avoid exposing it directly to the Internet. Agent logon startup is restricted to the currently signed-in account; the selected account must match that session when this option is enabled. Service install/update is skipped unless Server is selected and the install-consent checkbox is accepted. Setup never adds firewall rules.

The current Server supports only classic RFB Security Type 2. DES uses at most the first eight password characters, so LucyVNC Server accepts 6–8 printable ASCII characters and rejects longer input; it does not offer a 16-character password mode. TLS/VeNCrypt and a modern authenticated transport are pending. To protect traffic, keep the listener on loopback or use a separately configured, trusted VPN tunnel. Compatibility with traditional VNC clients is retained through the standard classic security type.

The password is not placed in process arguments, installer logs, or plaintext files. During setup it is passed from the password control through the installer process environment to the Server provisioner. The provisioner clears the environment value, creates a DPAPI-protected request with a restricted ACL, and stores the durable credential as DPAPI LocalMachine ciphertext with SYSTEM/Administrators ACLs. The protected request is deleted after application. `LucyVNC.Server.exe` and its configuration interface can also be used later to manage the Server independently.

Upgrades retain the product AppId and protected settings. If the Server service already exists, setup requires the Server component and explicit install/update consent before changing its executable path. Full uninstall asks before stopping/removing the Windows service; protected settings and credentials under `%ProgramData%\LucyVNC\Service` are retained. The uninstall is cancelled if the service cannot be removed, preventing deletion of its executable while registered. No real service install/uninstall is part of automated tests.

## LucyVNC Manager / Viewer

Launch `LucyVNC.Manager.exe`. The Manager is for outgoing VNC connections only: saved computers, favorites, recent connections, concurrent Viewer sessions, import, and export. It does not configure, install, start, stop, or supervise LucyVNC Server.

The Manager stores non-secret connection data in `%LOCALAPPDATA%\LucyVNC\lucy.db`. Password remembering is opt-in and uses the signed-in user's Windows Credential Manager. JSON/CSV import and export contain no credentials.

The Manager can open a `.vnc` file directly, import multiple selected files, or import top-level `.vnc` files from a selected folder. It supports INI-like profiles with `Host` plus optional `Port`, `ConnectionName`/`Name`, and `Group` key/value fields; common DNS/IP values, `host:display`, `host::tcp-port`, and bracketed IPv6 are recognized. It does not execute profile contents or import credentials; unsupported options are reported by key name only. Folder import is non-recursive and limited to 1,000 files, 1 MB per profile, and 10 MB total. Native JSON import requires `schemaVersion: 1`; JSON, CSV, and individual `.vnc` exports omit passwords and credentials. `.vnc` exports preserve only endpoint, port, and name.

## LucyVNC Server

After Server setup, launch **LucyVNC Server** from its shortcut or notification area. The tray displays SCM and runtime readiness. Double-clicking the tray icon or choosing **Abrir LucyVNC Server Configuration** opens the Server's own configuration window. The configuration window can save protected settings and perform install/start/stop/restart/uninstall operations with explicit UAC authorization. The Server application is independent of LucyVNC Manager.

The Windows Service owns networking and RFB. `LucyVNC.Agent.exe` performs capture and input in the authorized user's interactive session. The standalone Server app launches a single Agent supervisor for that user and retries with bounded backoff. Exiting the tray only exits the tray application; it does not stop the Worker or Agent. On service start, the RFB listener remains closed until the configured Agent authenticates over the SID-checked named pipe and reports an available interactive `Default` desktop.

Locking or switching away from the supported desktop closes the listener. Secure Desktop, UAC, logon-screen capture, pre-login access, and Session 0 capture/input are intentionally unsupported. VNC Authentication does not encrypt RFB traffic; use loopback or a trusted LAN/VPN, and do not expose classic VNC directly to an untrusted network.

The service may be configured Automatic or Manual. Automatic service start at boot does not make desktop sharing available before the authorized user logs in. To validate actual behavior manually:

1. Install only **LucyVNC Server** and select an authorized account that will log in interactively.
2. Enable sharing, use a temporary 6–8-character password, keep bind at `127.0.0.1` for the first check, and consent to service installation.
3. Optionally select Agent/tray startup at logon and start sharing after setup.
4. Verify the tray reports the Agent authenticated and RFB listener ready. Connect a Viewer to `127.0.0.1:5900`; validate image refresh and non-destructive mouse/keyboard input.
5. Close the tray and confirm the Viewer session continues. Reopen Server Configuration and check that it reports the actual service/runtime status.
6. Lock/unlock the interactive session and verify listener closure/recovery. Reboot and confirm automatic service start and Agent/tray startup after the authorized user logs in.
7. Test a private LAN/VPN address only after the loopback test. Use a specific interface for a single network or explicitly confirm `0.0.0.0` for simultaneous LAN/VPN listening; configure firewall rules manually and narrowly if required.

These are manual acceptance steps, not results implied by build/test/publish. Do not install a real service or change firewall settings without authorization.

## Development-only local Worker/Agent test

The process integration tests use synthetic capture and input. For an opt-in real desktop test, use a disposable interactive desktop, keep the listener bound to loopback, and do not focus a sensitive application. The test can move the cursor and inject input if a Viewer sends events.

Create a user-only development configuration:

```powershell
.\scripts\New-LucyServerDevelopmentConfig.ps1 -EnableSharing
```

Start the Agent and Worker in separate PowerShell windows using their current Release output names:

```powershell
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$env:LUCYVNC_AGENT_AUTHORIZED_SID = $sid
& ".\src\Vnc.Agent\bin\Release\net8.0-windows\LucyVNC.Agent.exe"
```

```powershell
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$env:LUCYVNC_AGENT_SID = $sid
$env:LUCYVNC_AGENT_PIPE = "LucyVNCAgent"
$env:LUCYVNC_SERVICE_CONFIG = Join-Path $env:LOCALAPPDATA "LucyVNC\Development\service.json"
$securePassword = Read-Host "Temporary VNC password (6-8 printable ASCII characters)" -AsSecureString
$passwordBuffer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $env:LUCYVNC_VNC_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordBuffer)
    & ".\src\Vnc.Service\bin\Release\net8.0-windows\LucyVNC.Server.Worker.exe" --console
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordBuffer)
    $securePassword.Dispose()
    Remove-Item Env:\LUCYVNC_VNC_PASSWORD -ErrorAction SilentlyContinue
}
```

Connect LucyVNC Viewer to `127.0.0.1:5901`. Enter `STOP` in the Worker window and then stop the Agent with Ctrl+C. This does not install or start a Windows Service.
