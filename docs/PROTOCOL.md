# RFB protocol implementation

LucyVNC implements the RFB message flow needed for an authenticated, single-screen Windows viewer/server. The implementation follows the wire layouts in [RFC 6143](https://www.rfc-editor.org/rfc/rfc6143) for the messages and encodings listed below; it is not a complete implementation of every optional RFB feature.

## Handshake and authentication

- The Server sends the initial 12-byte version banner. The Viewer replies with the negotiated version. Versions 3.3, 3.7, and 3.8 are supported.
- RFB 3.3 uses the four-byte security type; 3.7/3.8 use the security-type list and one-byte client selection.
- The Server offers only Security Type 2 (VNC Authentication). The 16-byte challenge-response uses the protocol's DES key-bit reversal and an eight-byte password key. LucyVNC Server requires 6–8 printable ASCII characters; the Viewer accepts 1–8 printable ASCII characters to remain compatible with legacy peers. Inputs longer than eight characters are rejected by the authentication implementation, never silently truncated.
- Authentication status and the RFB 3.8 failure reason are serialized as big-endian values. There is no unauthenticated Server mode. The Viewer permits Security Type None only when explicitly configured to do so.
- After successful authentication, the client/server initialization messages carry dimensions, the 16-byte PixelFormat, and a UTF-8 desktop name. Framebuffer dimensions and payloads are bounded before allocation.

## Pixel and framebuffer messages

- Integer fields in RFB message headers are big-endian. Pixel payload byte order follows the negotiated PixelFormat.
- The Viewer requests 32-bit little-endian TrueColor and decodes each update into a persistent BGRX framebuffer. It applies Raw and CopyRect rectangles and raises update events for the WPF renderer.
- The Server accepts TrueColor 8-, 16-, or 32-bit PixelFormats and converts captured BGRX pixels to the requested channel maxima, shifts, and endianness. Indexed-color formats are rejected.
- Raw payload length is the rectangle pixel count multiplied by the negotiated bytes per pixel. CopyRect contains only the source X/Y coordinates after its rectangle header.
- The Server advertises Raw only; it does not generate CopyRect. For each requested framebuffer region, it captures the primary display, compares 64×64 tiles for incremental requests, and sends changed rectangles (or an aggregated changed region when there are too many). Capture is capped at 20 frames per second by default.
- The Viewer requests another incremental update after applying each update, keeping the display live without a polling timer on the UI thread.

## Input and client messages

PointerEvent uses a one-byte button mask followed by big-endian X/Y coordinates. KeyEvent uses a one-byte down flag, two padding bytes, and a four-byte keysym. The Server dispatches those events to the Windows `SendInput` implementation unless server-side view-only is enabled. The Viewer serializes outgoing messages, maps pointer coordinates to the remote framebuffer, and releases pressed keys/buttons on disconnect.

SetPixelFormat, SetEncodings, FramebufferUpdateRequest, KeyEvent, PointerEvent, and ClientCutText are parsed using their specified fixed headers and bounded variable payloads. Color-map messages are consumed, but indexed-color operation is not enabled.

## Security and known limitations

Classic VNC Authentication proves knowledge of a short password but does not encrypt RFB traffic. It uses DES challenge-response and is not suitable for exposure to untrusted networks. Use loopback or a trusted VPN/SSH tunnel. The Server accepts only loopback, RFC1918, CGNAT, IPv6 unique-local, or link-local bind addresses; `0.0.0.0` requires explicit user confirmation and binds all local IPv4 interfaces. It does not change firewall policy. TLS/VeNCrypt and a modern password mechanism that uses every character are not implemented; do not enter a password longer than eight characters or assume the session is encrypted.

TLS/VeNCrypt, Tight encoding, clipboard transfer, indexed color, multi-monitor capture, server-side CopyRect generation, and durable Windows service management are outside the current implementation.
