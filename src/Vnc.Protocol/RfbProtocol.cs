using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Vnc.Core;

namespace Vnc.Protocol;

public static class RfbProtocol
{
    public const string Version3_3 = "RFB 003.003";
    public const string Version3_7 = "RFB 003.007";
    public const string Version3_8 = "RFB 003.008";
    public const int RawEncoding = 0;
    public const int CopyRectEncoding = 1;
    public const int MaximumDesktopNameBytes = 1024 * 1024;
    public const int MaximumFramebufferBytes = 64 * 1024 * 1024;
    public const int MaximumRectanglesPerUpdate = 4096;

    public static byte[] EncodeVersion(string version)
    {
        var normalized = version.TrimEnd('\r', '\n', '\0');
        if (normalized.Length != 11 || !normalized.StartsWith("RFB ", StringComparison.Ordinal))
        {
            throw new ArgumentException("RFB version must use the 11-character RFB banner format.", nameof(version));
        }

        return Encoding.ASCII.GetBytes(normalized + "\n");
    }

    public static string ReadVersion(Stream stream)
    {
        var bytes = new byte[12];
        ReadExactly(stream, bytes);
        var version = Encoding.ASCII.GetString(bytes).TrimEnd('\0', '\n', '\r');
        ValidateVersion(version);
        return version;
    }

    public static async ValueTask<string> ReadVersionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[12];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        var version = Encoding.ASCII.GetString(bytes).TrimEnd('\0', '\n', '\r');
        ValidateVersion(version);
        return version;
    }

    public static string NegotiateVersion(string peerVersion)
    {
        ValidateVersion(peerVersion);
        var minor = int.Parse(peerVersion.AsSpan(8, 3), System.Globalization.CultureInfo.InvariantCulture);
        return minor >= 8 ? Version3_8 : minor >= 7 ? Version3_7 : Version3_3;
    }

    public static bool IsVersionAtLeast(string version, int minor)
    {
        ValidateVersion(version);
        return int.Parse(version.AsSpan(8, 3), System.Globalization.CultureInfo.InvariantCulture) >= minor;
    }

    public static void WriteRfbVersion(Stream stream, string version) => stream.Write(EncodeVersion(version));

    public static async ValueTask WriteRfbVersionAsync(Stream stream, string version, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(EncodeVersion(version), cancellationToken).ConfigureAwait(false);
    }

    public static void WriteSecurityTypes(Stream stream, params SecurityType[] types)
    {
        if (types.Length is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(types));
        }

        stream.WriteByte((byte)types.Length);
        foreach (var type in types)
        {
            stream.WriteByte((byte)type);
        }
    }

    public static SecurityType[] ReadSecurityTypes(Stream stream)
    {
        var count = ReadByte(stream);
        var values = new SecurityType[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = (SecurityType)ReadByte(stream);
        }

        return values;
    }

    public static void WriteLegacySecurityType(Stream stream, SecurityType type) => WriteUInt32(stream, (uint)type);

    public static SecurityType ReadLegacySecurityType(Stream stream)
    {
        var value = ReadUInt32(stream);
        if (value > byte.MaxValue)
        {
            throw new InvalidDataException("Invalid RFB 3.3 security type.");
        }

        return (SecurityType)value;
    }

    public static void WriteSelectedSecurityType(Stream stream, SecurityType securityType) => stream.WriteByte((byte)securityType);
    public static SecurityType ReadSelectedSecurityType(Stream stream) => (SecurityType)ReadByte(stream);

    public static void WriteAuthResult(Stream stream, bool accepted, bool rfb38 = true)
    {
        WriteUInt32(stream, accepted ? 0u : 1u);
        if (!accepted && rfb38)
        {
            var reason = Encoding.UTF8.GetBytes("Authentication failed");
            WriteUInt32(stream, (uint)reason.Length);
            stream.Write(reason);
        }
    }

    public static bool ReadAuthResult(Stream stream, bool rfb38 = true)
    {
        var status = ReadUInt32(stream);
        if (status == 0)
        {
            return true;
        }

        if (rfb38)
        {
            var reasonLength = ReadBoundedLength(stream, MaximumDesktopNameBytes);
            var reason = new byte[reasonLength];
            ReadExactly(stream, reason);
        }

        return false;
    }

    public static byte[] CreateChallenge()
    {
        var challenge = new byte[16];
        RandomNumberGenerator.Fill(challenge);
        return challenge;
    }

    public static void WriteChallenge(Stream stream, byte[] challenge)
    {
        if (challenge.Length != 16)
        {
            throw new ArgumentException("VNC challenge must be exactly 16 bytes.", nameof(challenge));
        }

        stream.Write(challenge);
    }

    public static byte[] ReadChallenge(Stream stream)
    {
        var challenge = new byte[16];
        ReadExactly(stream, challenge);
        return challenge;
    }

    public static async ValueTask<byte[]> ReadChallengeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var challenge = new byte[16];
        await ReadExactlyAsync(stream, challenge, cancellationToken).ConfigureAwait(false);
        return challenge;
    }

    public static void WriteClientInit(Stream stream, bool shared) => stream.WriteByte(shared ? (byte)1 : (byte)0);
    public static bool ReadClientInit(Stream stream) => ReadByte(stream) != 0;

    public static void WriteServerInit(Stream stream, ushort width, ushort height, PixelFormat pixelFormat, string desktopName)
    {
        WriteUInt16(stream, width);
        WriteUInt16(stream, height);
        stream.Write(pixelFormat.ToBytes());
        var nameBytes = Encoding.UTF8.GetBytes(desktopName);
        WriteUInt32(stream, (uint)nameBytes.Length);
        stream.Write(nameBytes);
    }

    public static ServerInit ReadServerInit(Stream stream, int maximumFramebufferBytes = MaximumFramebufferBytes)
    {
        var width = ReadUInt16(stream);
        var height = ReadUInt16(stream);
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("ServerInit dimensions must be positive.");
        }

        var formatBytes = new byte[16];
        ReadExactly(stream, formatBytes);
        var format = PixelFormat.FromBytes(formatBytes);
        var byteCount = checked((long)width * height * format.BytesPerPixel);
        if (byteCount > maximumFramebufferBytes)
        {
            throw new InvalidDataException("Framebuffer exceeds the configured size limit.");
        }

        var nameLength = ReadBoundedLength(stream, MaximumDesktopNameBytes);
        var nameBytes = new byte[nameLength];
        ReadExactly(stream, nameBytes);
        return new ServerInit(width, height, format, Encoding.UTF8.GetString(nameBytes));
    }

    public static async ValueTask<ServerInit> ReadServerInitAsync(
        Stream stream,
        int maximumFramebufferBytes,
        CancellationToken cancellationToken)
    {
        var width = await ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
        var height = await ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("ServerInit dimensions must be positive.");
        }

        var formatBytes = new byte[16];
        await ReadExactlyAsync(stream, formatBytes, cancellationToken).ConfigureAwait(false);
        var format = PixelFormat.FromBytes(formatBytes);
        var byteCount = checked((long)width * height * format.BytesPerPixel);
        if (byteCount > maximumFramebufferBytes)
        {
            throw new InvalidDataException("Framebuffer exceeds the configured size limit.");
        }

        var nameLength = await ReadBoundedLengthAsync(stream, MaximumDesktopNameBytes, cancellationToken).ConfigureAwait(false);
        var nameBytes = new byte[nameLength];
        await ReadExactlyAsync(stream, nameBytes, cancellationToken).ConfigureAwait(false);
        return new ServerInit(width, height, format, Encoding.UTF8.GetString(nameBytes));
    }

    public static void WriteSetPixelFormat(Stream stream, PixelFormat pixelFormat)
    {
        stream.WriteByte(0);
        stream.Write(new byte[3]);
        stream.Write(pixelFormat.ToBytes());
    }

    public static void WriteSetEncodings(Stream stream, params int[] encodings)
    {
        if (encodings.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(encodings));
        }

        stream.WriteByte(2);
        stream.WriteByte(0);
        WriteUInt16(stream, (ushort)encodings.Length);
        foreach (var encoding in encodings)
        {
            WriteInt32(stream, encoding);
        }
    }

    public static int[] ReadSetEncodings(Stream stream)
    {
        if (ReadByte(stream) != 2)
        {
            throw new InvalidDataException("Expected SetEncodings message.");
        }

        _ = ReadByte(stream);
        var count = ReadUInt16(stream);
        var result = new int[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = ReadInt32(stream);
        }

        return result;
    }

    public static void WriteFramebufferUpdateRequest(Stream stream, ushort x, ushort y, ushort width, ushort height, bool incremental)
    {
        stream.WriteByte(3);
        stream.WriteByte(incremental ? (byte)1 : (byte)0);
        WriteUInt16(stream, x);
        WriteUInt16(stream, y);
        WriteUInt16(stream, width);
        WriteUInt16(stream, height);
    }

    public static FramebufferRequest ReadFramebufferUpdateRequest(Stream stream)
    {
        if (ReadByte(stream) != 3)
        {
            throw new InvalidDataException("Expected FramebufferUpdateRequest.");
        }

        var incremental = ReadByte(stream) != 0;
        return new FramebufferRequest(ReadUInt16(stream), ReadUInt16(stream), ReadUInt16(stream), ReadUInt16(stream), incremental);
    }

    public static void WriteKeyEvent(Stream stream, bool pressed, uint keyCode)
    {
        stream.WriteByte(4);
        stream.WriteByte(pressed ? (byte)1 : (byte)0);
        stream.Write(new byte[2]);
        WriteUInt32(stream, keyCode);
    }

    public static KeyEvent ReadKeyEvent(Stream stream)
    {
        if (ReadByte(stream) != 4)
        {
            throw new InvalidDataException("Expected KeyEvent.");
        }

        var pressed = ReadByte(stream) != 0;
        _ = ReadUInt16(stream);
        return new KeyEvent(pressed, ReadUInt32(stream));
    }

    public static void WritePointerEvent(Stream stream, ushort x, ushort y, MouseButton buttonMask)
    {
        stream.WriteByte(5);
        stream.WriteByte((byte)buttonMask);
        WriteUInt16(stream, x);
        WriteUInt16(stream, y);
    }

    public static PointerEvent ReadPointerEvent(Stream stream)
    {
        if (ReadByte(stream) != 5)
        {
            throw new InvalidDataException("Expected PointerEvent.");
        }

        var buttons = (MouseButton)ReadByte(stream);
        return new PointerEvent(ReadUInt16(stream), ReadUInt16(stream), buttons);
    }

    public static void WriteFramebufferUpdate(Stream stream, params FramebufferRectangle[] rectangles)
    {
        if (rectangles.Length > MaximumRectanglesPerUpdate)
        {
            throw new ArgumentOutOfRangeException(nameof(rectangles));
        }

        ValidateOutboundRectangles(rectangles);

        stream.WriteByte(0);
        stream.WriteByte(0);
        WriteUInt16(stream, (ushort)rectangles.Length);
        foreach (var rect in rectangles)
        {
            WriteRectangleHeader(stream, rect);
            if (rect.Encoding == RawEncoding)
            {
                stream.Write(rect.Data);
            }
            else if (rect.Encoding == CopyRectEncoding)
            {
                WriteUInt16(stream, rect.SourceX);
                WriteUInt16(stream, rect.SourceY);
            }
            else
            {
                throw new InvalidDataException($"Unsupported framebuffer encoding {rect.Encoding}.");
            }
        }
    }

    public static FramebufferRectangle[] ReadFramebufferUpdate(
        Stream stream,
        ushort framebufferWidth,
        ushort framebufferHeight,
        PixelFormat pixelFormat,
        int maximumFramebufferBytes = MaximumFramebufferBytes)
    {
        if (ReadByte(stream) != 0)
        {
            throw new InvalidDataException("Expected FramebufferUpdate.");
        }

        _ = ReadByte(stream);
        var count = ReadUInt16(stream);
        if (count > MaximumRectanglesPerUpdate)
        {
            throw new InvalidDataException("Framebuffer update contains too many rectangles.");
        }

        var rects = new FramebufferRectangle[count];
        long totalBytes = 0;
        for (var index = 0; index < count; index++)
        {
            var header = ReadRectangleHeader(stream);
            ValidateRectangle(header.X, header.Y, header.Width, header.Height, framebufferWidth, framebufferHeight);
            if (header.Encoding == RawEncoding)
            {
                totalBytes = checked(totalBytes + (long)header.Width * header.Height * pixelFormat.BytesPerPixel);
                if (totalBytes > maximumFramebufferBytes)
                {
                    throw new InvalidDataException("Framebuffer update exceeds the configured size limit.");
                }

                var data = new byte[checked(header.Width * header.Height * pixelFormat.BytesPerPixel)];
                ReadExactly(stream, data);
                rects[index] = header with { Data = data };
            }
            else if (header.Encoding == CopyRectEncoding)
            {
                var sourceX = ReadUInt16(stream);
                var sourceY = ReadUInt16(stream);
                ValidateRectangle(sourceX, sourceY, header.Width, header.Height, framebufferWidth, framebufferHeight);
                rects[index] = header with { Data = Array.Empty<byte>(), SourceX = sourceX, SourceY = sourceY };
            }
            else
            {
                throw new InvalidDataException($"Unsupported framebuffer encoding {header.Encoding}.");
            }
        }

        return rects;
    }

    public static async ValueTask<FramebufferRectangle[]> ReadFramebufferUpdateAsync(
        Stream stream,
        ushort framebufferWidth,
        ushort framebufferHeight,
        PixelFormat pixelFormat,
        int maximumFramebufferBytes,
        CancellationToken cancellationToken,
        bool messageTypeAlreadyRead = false)
    {
        if (!messageTypeAlreadyRead && await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Expected FramebufferUpdate.");
        }

        _ = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        var count = await ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
        if (count > MaximumRectanglesPerUpdate)
        {
            throw new InvalidDataException("Framebuffer update contains too many rectangles.");
        }

        var rects = new FramebufferRectangle[count];
        long totalBytes = 0;
        for (var index = 0; index < count; index++)
        {
            var x = await ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
            var y = await ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
            var width = await ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
            var height = await ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
            var encoding = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
            ValidateRectangle(x, y, width, height, framebufferWidth, framebufferHeight);
            if (encoding == RawEncoding)
            {
                totalBytes = checked(totalBytes + (long)width * height * pixelFormat.BytesPerPixel);
                if (totalBytes > maximumFramebufferBytes)
                {
                    throw new InvalidDataException("Framebuffer update exceeds the configured size limit.");
                }

                var data = new byte[checked(width * height * pixelFormat.BytesPerPixel)];
                await ReadExactlyAsync(stream, data, cancellationToken).ConfigureAwait(false);
                rects[index] = new FramebufferRectangle(x, y, width, height, encoding, data);
            }
            else if (encoding == CopyRectEncoding)
            {
                var sourceX = await ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                var sourceY = await ReadUInt16Async(stream, cancellationToken).ConfigureAwait(false);
                ValidateRectangle(sourceX, sourceY, width, height, framebufferWidth, framebufferHeight);
                rects[index] = new FramebufferRectangle(x, y, width, height, encoding, Array.Empty<byte>(), sourceX, sourceY);
            }
            else
            {
                throw new InvalidDataException($"Unsupported framebuffer encoding {encoding}.");
            }
        }

        return rects;
    }

    public static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public static ushort ReadUInt16(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[2];
        ReadExactly(stream, bytes);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    public static async ValueTask<uint> ReadUInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    public static async ValueTask<int> ReadBoundedLengthAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        var length = await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (length > maximum)
        {
            throw new InvalidDataException("RFB message length exceeds the configured limit.");
        }

        return (int)length;
    }

    public static async ValueTask<ushort> ReadUInt16Async(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[2];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    public static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public static uint ReadUInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadExactly(stream, bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    public static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public static int ReadInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadExactly(stream, bytes);
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    public static async ValueTask<int> ReadInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    public static void ReadExactly(Stream stream, byte[] buffer) => ReadExactly(stream, buffer.AsSpan());

    public static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException("Stream ended unexpectedly while reading RFB data.");
            }

            offset += read;
        }
    }

    public static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Stream ended unexpectedly while reading RFB data.");
            }

            offset += read;
        }
    }

    public static async ValueTask WriteFramebufferUpdateAsync(Stream stream, IReadOnlyList<FramebufferRectangle> rectangles, CancellationToken cancellationToken)
    {
        if (rectangles.Count > MaximumRectanglesPerUpdate)
        {
            throw new ArgumentOutOfRangeException(nameof(rectangles));
        }

        ValidateOutboundRectangles(rectangles);

        var updateHeader = new byte[4];
        updateHeader[0] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(updateHeader.AsSpan(2), (ushort)rectangles.Count);
        await stream.WriteAsync(updateHeader, cancellationToken).ConfigureAwait(false);
        foreach (var rect in rectangles)
        {
            var rectangleHeader = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(rectangleHeader.AsSpan(0, 2), rect.X);
            BinaryPrimitives.WriteUInt16BigEndian(rectangleHeader.AsSpan(2, 2), rect.Y);
            BinaryPrimitives.WriteUInt16BigEndian(rectangleHeader.AsSpan(4, 2), rect.Width);
            BinaryPrimitives.WriteUInt16BigEndian(rectangleHeader.AsSpan(6, 2), rect.Height);
            BinaryPrimitives.WriteInt32BigEndian(rectangleHeader.AsSpan(8, 4), rect.Encoding);
            await stream.WriteAsync(rectangleHeader, cancellationToken).ConfigureAwait(false);
            if (rect.Encoding == RawEncoding)
            {
                await stream.WriteAsync(rect.Data, cancellationToken).ConfigureAwait(false);
            }
            else if (rect.Encoding == CopyRectEncoding)
            {
                var source = new byte[4];
                BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(0, 2), rect.SourceX);
                BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(2, 2), rect.SourceY);
                await stream.WriteAsync(source, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidDataException($"Unsupported framebuffer encoding {rect.Encoding}.");
            }
        }
    }

    private static void WriteRectangleHeader(Stream stream, FramebufferRectangle rect)
    {
        WriteUInt16(stream, rect.X);
        WriteUInt16(stream, rect.Y);
        WriteUInt16(stream, rect.Width);
        WriteUInt16(stream, rect.Height);
        WriteInt32(stream, rect.Encoding);
    }

    private static FramebufferRectangle ReadRectangleHeader(Stream stream)
    {
        return new FramebufferRectangle(
            ReadUInt16(stream),
            ReadUInt16(stream),
            ReadUInt16(stream),
            ReadUInt16(stream),
            ReadInt32(stream),
            Array.Empty<byte>());
    }

    private static void ValidateRectangle(ushort x, ushort y, ushort width, ushort height, ushort framebufferWidth, ushort framebufferHeight)
    {
        if (width == 0 || height == 0 || (uint)x + width > framebufferWidth || (uint)y + height > framebufferHeight)
        {
            throw new InvalidDataException($"Framebuffer rectangle ({x},{y},{width},{height}) is outside screen {framebufferWidth}x{framebufferHeight}.");
        }
    }

    private static void ValidateOutboundRectangles(IReadOnlyList<FramebufferRectangle> rectangles)
    {
        long totalRawBytes = 0;
        foreach (var rectangle in rectangles)
        {
            ValidateOutboundRectangle(rectangle);
            if (rectangle.Encoding == RawEncoding)
            {
                totalRawBytes = checked(totalRawBytes + rectangle.Data.Length);
                if (totalRawBytes > MaximumFramebufferBytes)
                {
                    throw new InvalidDataException("Framebuffer update exceeds the configured size limit.");
                }
            }
        }
    }

    private static void ValidateOutboundRectangle(FramebufferRectangle rectangle)
    {
        if (rectangle.Width == 0 || rectangle.Height == 0 ||
            (uint)rectangle.X + rectangle.Width > ushort.MaxValue ||
            (uint)rectangle.Y + rectangle.Height > ushort.MaxValue)
        {
            throw new InvalidDataException("Framebuffer rectangle dimensions or coordinates are outside the RFB range.");
        }

        var pixelCount = (long)rectangle.Width * rectangle.Height;
        if (rectangle.Encoding == RawEncoding)
        {
            if (rectangle.Data.Length % pixelCount != 0 ||
                rectangle.Data.Length / pixelCount is not (1 or 2 or 4))
            {
                throw new InvalidDataException("Raw framebuffer payload length does not match its rectangle dimensions.");
            }
        }
        else if (rectangle.Encoding == CopyRectEncoding)
        {
            if (rectangle.Data.Length != 0 ||
                (uint)rectangle.SourceX + rectangle.Width > ushort.MaxValue ||
                (uint)rectangle.SourceY + rectangle.Height > ushort.MaxValue)
            {
                throw new InvalidDataException("CopyRect source coordinates or payload are invalid.");
            }
        }
        else
        {
            throw new InvalidDataException($"Unsupported framebuffer encoding {rectangle.Encoding}.");
        }
    }

    private static int ReadBoundedLength(Stream stream, int maximum)
    {
        var length = ReadUInt32(stream);
        if (length > maximum)
        {
            throw new InvalidDataException("RFB message length exceeds the configured limit.");
        }

        return (int)length;
    }

    private static byte ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value >= 0 ? (byte)value : throw new EndOfStreamException("Stream ended unexpectedly while reading RFB data.");
    }

    private static async ValueTask<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        await ReadExactlyAsync(stream, value, cancellationToken).ConfigureAwait(false);
        return value[0];
    }

    private static void ValidateVersion(string version)
    {
        if (version.Length != 11 || !version.StartsWith("RFB ", StringComparison.Ordinal) ||
            version[4] != '0' || version[5] != '0' || version[6] != '3' ||
            version[7] != '.' || !int.TryParse(version.AsSpan(8, 3), out var minor) || minor < 3)
        {
            throw new InvalidDataException($"Unsupported RFB version banner: {version}");
        }
    }
}

public sealed record ServerInit(ushort Width, ushort Height, PixelFormat PixelFormat, string DesktopName);
public sealed record FramebufferRequest(ushort X, ushort Y, ushort Width, ushort Height, bool Incremental);
public sealed record KeyEvent(bool Pressed, uint KeyCode);
public sealed record PointerEvent(ushort X, ushort Y, MouseButton Buttons);

[Flags]
public enum MouseButton : byte
{
    None = 0,
    Left = 1,
    Middle = 2,
    Right = 4,
}

public static class VncAuthentication
{
    public const int MaximumClassicPasswordLength = 8;
    public const int MinimumServerPasswordLength = 6;

    public static bool IsValidClassicPassword(string? password) =>
        !string.IsNullOrEmpty(password) &&
        password.Length <= MaximumClassicPasswordLength &&
        password.All(character => character is >= (char)0x20 and <= (char)0x7e);

    public static bool IsValidServerPassword(string? password) =>
        IsValidClassicPassword(password) && password!.Length >= MinimumServerPasswordLength;

    public static byte[] ComputeResponse(string password, ReadOnlySpan<byte> challenge)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!IsValidClassicPassword(password))
        {
            throw new ArgumentException(
                "Classic VNC Authentication accepts printable ASCII passwords of at most eight characters.",
                nameof(password));
        }
        if (challenge.Length != 16)
        {
            throw new ArgumentException("Challenge must be exactly 16 bytes.", nameof(challenge));
        }

        var key = BuildKey(password);
        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        using var encryptor = des.CreateEncryptor(key, null);
        var block = challenge.ToArray();
        return encryptor.TransformFinalBlock(block, 0, block.Length);
    }

    public static bool VerifyResponse(string password, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> response)
    {
        if (!IsValidClassicPassword(password) || response.Length != 16)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(ComputeResponse(password, challenge), response);
    }

    private static byte[] BuildKey(string password)
    {
        var input = Encoding.ASCII.GetBytes(password);
        var key = new byte[8];
        for (var index = 0; index < Math.Min(input.Length, key.Length); index++)
        {
            key[index] = ReverseBits(input[index]);
        }

        return key;
    }

    private static byte ReverseBits(byte value)
    {
        var result = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return (byte)result;
    }
}
