using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vnc.Core;
using PixelFormat = Vnc.Core.PixelFormat;

namespace Vnc.Platform.Windows;

public interface IScreenCapture : IDisposable
{
    ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0);
}

public interface IScreenCaptureBounds
{
    (int Width, int Height) GetPrimaryScreenSize();
}

public sealed class GdiScreenCapture : IScreenCapture, IScreenCaptureBounds
{
    public GdiScreenCapture()
    {
        WindowsDpiAwareness.InitializePerMonitorV2();
    }

    public (int Width, int Height) GetPrimaryScreenSize()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Windows did not report a primary display.");
        return (bounds.Width, bounds.Height);
    }

    public ScreenFrame CapturePrimaryScreen(int width = 0, int height = 0)
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Windows did not report a primary display.");
        var targetWidth = width == 0 ? bounds.Width : width;
        var targetHeight = height == 0 ? bounds.Height : height;
        if (targetWidth is <= 0 or > ushort.MaxValue || targetHeight is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Requested capture dimensions must be in the RFB range 1..65535.");
        }

        using var source = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        using var target = targetWidth == bounds.Width && targetHeight == bounds.Height
            ? (Bitmap)source.Clone()
            : new Bitmap(targetWidth, targetHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        if (targetWidth != bounds.Width || targetHeight != bounds.Height)
        {
            using var graphics = Graphics.FromImage(target);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight));
        }

        var frame = new ScreenFrame(targetWidth, targetHeight, PixelFormat.Standard32Argb8888);
        var bits = target.LockBits(new Rectangle(0, 0, targetWidth, targetHeight), ImageLockMode.ReadOnly, target.PixelFormat);
        try
        {
            var rowBytes = checked(targetWidth * 4);
            var buffer = new byte[checked(rowBytes * targetHeight)];
            for (var row = 0; row < targetHeight; row++)
            {
                var sourceRow = bits.Stride >= 0
                    ? IntPtr.Add(bits.Scan0, row * bits.Stride)
                    : IntPtr.Add(bits.Scan0, (targetHeight - 1 - row) * -bits.Stride);
                Marshal.Copy(sourceRow, buffer, row * rowBytes, rowBytes);
            }

            for (var index = 0; index < buffer.Length; index += 4)
            {
                frame.PixelData[index] = buffer[index];
                frame.PixelData[index + 1] = buffer[index + 1];
                frame.PixelData[index + 2] = buffer[index + 2];
                frame.PixelData[index + 3] = 0;
            }
        }
        finally
        {
            target.UnlockBits(bits);
        }

        return frame;
    }

    public void Dispose()
    {
    }
}
