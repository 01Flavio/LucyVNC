# Interoperability and functional validation

Validation environment: Windows 11 interactive desktop, .NET 8, 1920×1080 primary display, TightVNC 2.8.88 installed. Results are split by protocol stage; a successful RFB handshake is not evidence that framebuffer updates or remote input work.

## Evidence matrix

| Direction / endpoint | Handshake | Authentication | Initial image | Updates | Mouse | Keyboard | Stability |
|---|---|---|---|---|---|---|---|
| LucyVNC Viewer → LucyVNC Server, automated loopback integration | Pass: RFB 3.8, VNC Security Type 2, ServerInit. | Pass with the test password; wrong password is rejected. | Pass: synthetic Raw framebuffer decoded and published. | Pass: 64×64 changed tile delivered incrementally. | Pass at protocol/input-sink level; pointer coordinates and button masks asserted. | Pass at protocol/input-sink level, including key-down/up. | Pass: unexpected Server stop notifies/disconnects the Viewer; the Server restarts and the Viewer reconnects. A peer closing during handshake and a failed authentication are followed by successful connections. |
| LucyVNC Viewer → LucyVNC Server, interactive SendInput probe | Pass: authenticated loopback session and live framebuffer event. | Pass: dedicated test-only password. | Pass: desktop capture received. | Pass: screen change/update path exercised while the test form was visible. | Pass: cursor moved to the test field; left mouse-down/up activated the dedicated safe button. | Pass: benign `lucy` text, Shift-uppercase, Home/Delete, Left/Right, and Backspace in a dedicated temporary field. | Pass: stopping the Server while Shift was held released it; restarting and typing lowercase `b` succeeded. The probe exited normally. |
| TightVNC Viewer 2.8.88 → LucyVNC interactive probe | Pass: viewer connected and showed the ServerInit desktop name. | Pass: authenticated using the probe-only password. | Pass: live desktop was visible in a captured TightVNC Viewer window. | Pass, limited sample: the probe displayed a changing timestamp; 13 pixels in the timestamp crop changed between two screenshots taken two seconds apart. This proves visible change, not prolonged stability. | **Not confirmed:** an attempt with input enabled did not activate the safe probe button. A cursor-position change was seen during an earlier attempt, but a button event was not independently confirmed. | **Not tested end-to-end:** no keystroke from TightVNC Viewer was verified in the remote test field. | Pass, limited sample: the viewer remained connected through the short screenshot/update run and closed when the test Server stopped. No long-duration soak test was run. |
| LucyVNC Viewer → installed TightVNC Server 2.8.88 on this workstation | Partial only: port 5900 accepted TCP and sent `RFB 003.008`; the connection closed after the Viewer version response, before a security-type list was read. | Not tested: the configured VNC password was not available. No password was guessed or retrieved. | Not tested. | Not tested. | Not tested. | Not tested. | Not established; this probe did not complete RFB negotiation. |
| LucyVNC Viewer or Server ↔ TigerVNC | Not tested: no TigerVNC installation was found. | Not tested. | Not tested. | Not tested. | Not tested. | Not tested. | Not tested. |

**Interoperability conclusion:** TightVNC Viewer 2.8.88 has completed an authenticated connection to the LucyVNC Server and visibly rendered real screen content with a short update sample. TightVNC mouse-button and keyboard control were not proven. LucyVNC Viewer compatibility with TightVNC Server was not proven. Do not treat either direction as fully interoperable until those input and reverse-direction cases pass on a separate Windows machine.

## Reproducing the LucyVNC input test

Run this only in a logged-in, interactive Windows desktop. It moves the local cursor, clicks a dedicated temporary form, and types only harmless test text into that form; it does not target another application.

```powershell
dotnet run --project "tests/Vnc.InteractiveProbe/Vnc.InteractiveProbe.csproj" -c Release
```

The probe starts an authenticated RFB Server bound only to `127.0.0.1`, connects a LucyVNC Viewer, and checks pointer position, left click, text entry, a changed framebuffer tile after input, modifier/special keys, key release during Server shutdown, and reconnection. Expected final output:

```text
PASS: live framebuffer, changed pixels, pointer movement, left click, text entry, Shift/Left/Backspace, key release, and reconnect.
```

The probe also has a manual fixture mode for third-party clients:

```powershell
dotnet run --project "tests/Vnc.InteractiveProbe/Vnc.InteractiveProbe.csproj" -c Release -- --manual
```

It prints a temporary loopback port and the test-only password `lucytest`; connect TightVNC Viewer to `127.0.0.1` and that port using Raw encoding. Close the fixture window to stop the Server. This mode logs received keysyms and pointer events and should not be exposed outside loopback.

## TightVNC Server test procedure

The installed TightVNC Server was already listening on TCP 5900 during this validation, but its authentication credential was not available. The read-only probe did not change service settings, stop the service, retrieve stored secrets, or alter the firewall. To finish the reverse-direction test:

1. On a test Windows machine, configure TightVNC Server with a temporary eight-character ASCII VNC password and start the service. Keep the listener on a private/VPN interface and scope any firewall exception to the Viewer machine.
2. From LucyVNC Viewer, connect to that machine's private/VPN IP and port 5900 with the temporary password.
3. Verify the security negotiation and authenticated ServerInit, then visually confirm the initial image and a changed screen region.
4. In a dedicated harmless text field, verify pointer movement, left click, ordinary text, a modifier, a navigation key, key release, disconnect, and reconnect.
5. Record each result separately in the matrix. Remove the temporary firewall exception and password after the test.

## Two-machine private-network procedure

A second Windows machine was not available for this run, so remote-IP operation has not been claimed as tested. Reproduce it with two Windows machines on the same trusted private LAN or VPN:

1. On the Server machine, identify its LAN/VPN address with `Get-NetIPAddress -AddressFamily IPv4 | Format-Table InterfaceAlias,IPAddress,PrefixLength`. Select the private/VPN adapter; do not use a public address.
2. Start LucyVNC Server with that private interface address as its bind address, a temporary eight-character ASCII password, and the intended port. Confirm the UI shows the exact private address and an active Server status.
3. If Windows Firewall blocks the connection, run an elevated PowerShell on the Server machine, replacing `<VIEWER_PRIVATE_IP>` and the port as needed:

   ```powershell
   New-NetFirewallRule -DisplayName "LucyVNC-Interop-Temp" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5900 -RemoteAddress "<VIEWER_PRIVATE_IP>" -Profile Private
   ```

   LucyVNC does not create firewall rules. Remove the temporary rule after testing:

   ```powershell
   Remove-NetFirewallRule -DisplayName "LucyVNC-Interop-Temp"
   ```

4. On the Viewer machine, connect to the Server machine's private/VPN IP and port. Verify authentication, the live image, a changed region, safe mouse/keyboard input, disconnect/reconnect, and Server stop.
5. Confirm the Server is no longer listening after stop and that no public interface was bound.

## Constraints

- The application rejects public bind addresses and requires VNC authentication. The WPF application defaults to loopback and asks for confirmation before a private non-loopback bind. It does not configure the firewall.
- Only one server session is permitted because Windows input state is shared.
- Classic VNC Authentication does not encrypt the session and has an effective eight-character password limit. Use a trusted VPN/tunnel for non-loopback connections.
- Tight encoding, VeNCrypt/TLS, clipboard, indexed color, and vendor-specific modes are not implemented. The Server advertises Raw only.
