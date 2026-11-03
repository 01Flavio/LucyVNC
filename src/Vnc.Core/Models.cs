using System.Buffers.Binary;

namespace Vnc.Core;

public enum SecurityType : byte
{
    None = 1,
    VncAuthentication = 2,
}

public sealed record PixelFormat(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColor,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift)
{
    public int BytesPerPixel => BitsPerPixel / 8;

    public static PixelFormat Standard32BgrX888 => Standard32Argb8888;

    public static PixelFormat Standard32Argb8888 => new(
        BitsPerPixel: 32,
        Depth: 24,
        BigEndian: false,
        TrueColor: true,
        RedMax: 255,
        GreenMax: 255,
        BlueMax: 255,
        RedShift: 16,
        GreenShift: 8,
        BlueShift: 0);

    public byte[] ToBytes()
    {
        Validate();
        var buffer = new byte[16];
        buffer[0] = BitsPerPixel;
        buffer[1] = Depth;
        buffer[2] = BigEndian ? (byte)1 : (byte)0;
        buffer[3] = TrueColor ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), RedMax);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6, 2), GreenMax);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8, 2), BlueMax);
        buffer[10] = RedShift;
        buffer[11] = GreenShift;
        buffer[12] = BlueShift;
        return buffer;
    }

    public static PixelFormat FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16)
        {
            throw new ArgumentException("Pixel format payload must be 16 bytes.", nameof(bytes));
        }

        var format = new PixelFormat(
            bytes[0],
            bytes[1],
            bytes[2] != 0,
            bytes[3] != 0,
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(4, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(6, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(8, 2)),
            bytes[10],
            bytes[11],
            bytes[12]);
        format.Validate();
        return format;
    }

    public void Validate()
    {
        if (BitsPerPixel is not (8 or 16 or 32) || Depth == 0 || Depth > BitsPerPixel)
        {
            throw new InvalidDataException("Unsupported RFB pixel format.");
        }

        if (TrueColor && (RedMax == 0 || GreenMax == 0 || BlueMax == 0))
        {
            throw new InvalidDataException("True-colour channel maxima must be non-zero.");
        }

        if (TrueColor &&
            (RedShift + ChannelBits(RedMax) > BitsPerPixel ||
             GreenShift + ChannelBits(GreenMax) > BitsPerPixel ||
             BlueShift + ChannelBits(BlueMax) > BitsPerPixel))
        {
            throw new InvalidDataException("True-colour channel shifts exceed the selected pixel width.");
        }
    }

    private static int ChannelBits(ushort maximum) => 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)maximum);
}

public sealed record FramebufferRectangle(
    ushort X,
    ushort Y,
    ushort Width,
    ushort Height,
    int Encoding,
    byte[] Data,
    ushort SourceX = 0,
    ushort SourceY = 0);

public sealed class ScreenFrame
{
    public ScreenFrame(int width, int height, PixelFormat pixelFormat)
    {
        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Framebuffer dimensions must be in the range 1..65535.");
        }

        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        PixelData = new byte[checked(width * height * 4)];
    }

    public int Width { get; }
    public int Height { get; }
    public PixelFormat PixelFormat { get; }
    public byte[] PixelData { get; }

    public void Fill(byte red, byte green, byte blue)
    {
        for (var index = 0; index < PixelData.Length; index += 4)
        {
            PixelData[index] = blue;
            PixelData[index + 1] = green;
            PixelData[index + 2] = red;
            PixelData[index + 3] = 0;
        }
    }
}

public sealed class ConnectionSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5900;
    public string Password { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public bool SharedDesktop { get; set; } = true;
    public bool AllowNullAuth { get; set; }
    public bool ViewOnly { get; set; }
    public int MaxSessions { get; set; } = 1;
    public int MaxFramesPerSecond { get; set; } = 20;
    public int FramebufferLimitBytes { get; set; } = 64 * 1024 * 1024;
    public string BindAddress { get; set; } = "127.0.0.1";
    public bool AllowAllInterfaces { get; set; }
    public bool IsServerEnabled { get; set; }
}
