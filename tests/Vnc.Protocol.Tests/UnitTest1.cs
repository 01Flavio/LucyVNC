using System.Buffers.Binary;
using System.IO;
using System.Text;
using Vnc.Core;
using Vnc.Protocol;

namespace Vnc.Protocol.Tests;

public class RfbProtocolTests
{
    [Theory]
    [InlineData(RfbProtocol.Version3_3)]
    [InlineData(RfbProtocol.Version3_7)]
    [InlineData(RfbProtocol.Version3_8)]
    public void VersionBannerHasExactRfbWireLayout(string version)
    {
        var expected = Encoding.ASCII.GetBytes(version + "\n");
        Assert.Equal(12, expected.Length);
        Assert.Equal(expected, RfbProtocol.EncodeVersion(version));

        using var stream = new OneByteReadStream(expected);
        Assert.Equal(version, RfbProtocol.ReadVersion(stream));
    }

    [Theory]
    [InlineData("RFB 003.003", RfbProtocol.Version3_3)]
    [InlineData("RFB 003.007", RfbProtocol.Version3_7)]
    [InlineData("RFB 003.008", RfbProtocol.Version3_8)]
    [InlineData("RFB 003.889", RfbProtocol.Version3_8)]
    public void VersionNegotiationSelectsSupportedVersion(string peer, string expected) =>
        Assert.Equal(expected, RfbProtocol.NegotiateVersion(peer));

    [Fact]
    public void VncAuthenticationRejectsWrongPasswordAndUsesEightBytePasswordLimit()
    {
        var challenge = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var response = VncAuthentication.ComputeResponse("password", challenge);

        Assert.Equal(16, response.Length);
        Assert.True(VncAuthentication.VerifyResponse("password", challenge, response));
        Assert.False(VncAuthentication.VerifyResponse("password-more", challenge, response));
        Assert.Throws<ArgumentException>(() => VncAuthentication.ComputeResponse("password-more", challenge));
        Assert.False(VncAuthentication.VerifyResponse("passw0rd", challenge, response));
        Assert.False(VncAuthentication.VerifyResponse("password", challenge, response.AsSpan(0, 8)));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("12345", false)]
    [InlineData("123456", true)]
    [InlineData("1234567", true)]
    [InlineData("12345678", true)]
    [InlineData("123456789", false)]
    [InlineData("123456789012", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("12345678901234567", false)]
    [InlineData("abcde\n", false)]
    [InlineData("passwörd", false)]
    public void ServerPasswordPolicyIsSixToEightPrintableAsciiCharacters(string password, bool expected) =>
        Assert.Equal(expected, VncAuthentication.IsValidServerPassword(password));

    [Fact]
    public void PixelFormatUsesBigEndianWireFieldsAndRoundTrips()
    {
        var format = PixelFormat.Standard32Argb8888;
        var bytes = format.ToBytes();

        Assert.Equal(16, bytes.Length);
        Assert.Equal(new byte[] { 0, 255 }, bytes[4..6]);
        Assert.Equal(new byte[] { 0, 255 }, bytes[6..8]);
        Assert.Equal(new byte[] { 0, 255 }, bytes[8..10]);
        Assert.Equal(format, PixelFormat.FromBytes(bytes));
    }

    [Fact]
    public void PointerEventUsesOneByteButtonMaskAndSixBytePayload()
    {
        using var stream = new MemoryStream();
        RfbProtocol.WritePointerEvent(stream, 0x1234, 0xABCD, MouseButton.Left | MouseButton.Right);
        var wire = stream.ToArray();

        Assert.Equal(new byte[] { 5, 5, 0x12, 0x34, 0xAB, 0xCD }, wire);
        stream.Position = 0;
        Assert.Equal(new PointerEvent(0x1234, 0xABCD, MouseButton.Left | MouseButton.Right), RfbProtocol.ReadPointerEvent(stream));
    }

    [Fact]
    public void KeyEventUsesRfbU32KeysymInNetworkByteOrder()
    {
        using var stream = new MemoryStream();
        RfbProtocol.WriteKeyEvent(stream, true, 0xffe1);

        Assert.Equal(new byte[] { 4, 1, 0, 0, 0, 0, 0xff, 0xe1 }, stream.ToArray());
    }

    [Fact]
    public void RawRectangleReadsFormatSpecificBytesWithoutAssumingFourBytesPerPixel()
    {
        var format = new PixelFormat(16, 16, false, true, 31, 63, 31, 11, 5, 0);
        var rawPixelBytes = new byte[] { 0x34, 0x12, 0xcd, 0xab };
        using var wire = new MemoryStream();
        RfbProtocol.WriteFramebufferUpdate(wire, new FramebufferRectangle(0, 0, 2, 1, RfbProtocol.RawEncoding, rawPixelBytes));
        wire.Position = 0;

        var rect = Assert.Single(RfbProtocol.ReadFramebufferUpdate(wire, 2, 1, format));
        Assert.Equal(rawPixelBytes, rect.Data);
        Assert.Equal(0, rect.Encoding);
    }

    [Fact]
    public void CopyRectUsesOnlySourceCoordinatesOnWire()
    {
        using var stream = new MemoryStream();
        RfbProtocol.WriteFramebufferUpdate(
            stream,
            new FramebufferRectangle(8, 9, 10, 11, RfbProtocol.CopyRectEncoding, Array.Empty<byte>(), 2, 3));
        var wire = stream.ToArray();

        Assert.Equal(4 + 12 + 4, wire.Length);
        stream.Position = 0;
        var rect = Assert.Single(RfbProtocol.ReadFramebufferUpdate(
            stream, 32, 32, PixelFormat.Standard32Argb8888));
        Assert.Equal((ushort)2, rect.SourceX);
        Assert.Equal((ushort)3, rect.SourceY);
        Assert.Empty(rect.Data);
    }

    [Fact]
    public void RawRectangleOutsideFramebufferIsRejectedBeforeAllocation()
    {
        using var stream = new MemoryStream();
        RfbProtocol.WriteFramebufferUpdate(stream, new FramebufferRectangle(10, 0, 2, 1, 0, new byte[8]));
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() =>
            RfbProtocol.ReadFramebufferUpdate(stream, 10, 1, PixelFormat.Standard32Argb8888));
    }

    [Fact]
    public async Task OutboundFramebufferUpdateRejectsMalformedPayloadBeforeWriting()
    {
        var rectangle = new FramebufferRectangle(0, 0, 2, 2, RfbProtocol.RawEncoding, new byte[15]);
        using var syncStream = new MemoryStream();
        using var asyncStream = new MemoryStream();

        Assert.Throws<InvalidDataException>(() => RfbProtocol.WriteFramebufferUpdate(syncStream, rectangle));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await RfbProtocol.WriteFramebufferUpdateAsync(asyncStream, [rectangle], CancellationToken.None));

        Assert.Empty(syncStream.ToArray());
        Assert.Empty(asyncStream.ToArray());
    }

    [Fact]
    public async Task OutboundFramebufferUpdateSurfacesTransmissionFailure()
    {
        await using var stream = new FailingWriteStream();
        var rectangle = new FramebufferRectangle(0, 0, 1, 1, RfbProtocol.RawEncoding, new byte[4]);

        await Assert.ThrowsAsync<IOException>(async () =>
            await RfbProtocol.WriteFramebufferUpdateAsync(stream, [rectangle], CancellationToken.None));
    }

    [Fact]
    public void UpdateCountAndLengthsAreNetworkByteOrder()
    {
        using var stream = new MemoryStream();
        RfbProtocol.WriteFramebufferUpdate(stream, new FramebufferRectangle(1, 2, 1, 1, 0, new byte[4]));
        var wire = stream.ToArray();

        Assert.Equal((byte)0, wire[0]);
        Assert.Equal((byte)0, wire[1]);
        Assert.Equal((byte)0, wire[2]);
        Assert.Equal((byte)1, wire[3]);
        Assert.Equal((byte)0, wire[4]);
        Assert.Equal((byte)1, wire[5]);
        Assert.Equal((byte)0, wire[6]);
        Assert.Equal((byte)2, wire[7]);
        Assert.Equal((byte)0, wire[8]);
        Assert.Equal((byte)1, wire[9]);
        Assert.Equal((byte)0, wire[10]);
        Assert.Equal((byte)1, wire[11]);
        Assert.Equal((byte)0, wire[12]);
        Assert.Equal((byte)0, wire[13]);
        Assert.Equal((byte)0, wire[14]);
        Assert.Equal((byte)0, wire[15]);
    }

    private sealed class OneByteReadStream(byte[] content) : MemoryStream(content)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Simulated transmission failure."));
    }
}
