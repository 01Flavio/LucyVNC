using System.IO;
using System.Windows.Threading;
using System.Diagnostics;
using Vnc.Core;
using Vnc.Viewer;

namespace Vnc.App;

internal sealed class FramebufferRenderQueue : IDisposable
{
    private readonly object _sync = new();
    private readonly Dispatcher _dispatcher;
    private readonly Action<FramebufferRenderUpdate> _render;
    private readonly Action<Exception> _renderFailed;
    private byte[]? _pixels;
    private int _width;
    private int _height;
    private DirtyRectangle? _dirty;
    private bool _renderQueued;
    private bool _disposed;

    public FramebufferRenderQueue(
        Dispatcher dispatcher,
        Action<FramebufferRenderUpdate> render,
        Action<Exception> renderFailed)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _renderFailed = renderFailed ?? throw new ArgumentNullException(nameof(renderFailed));
    }

    public void Enqueue(FramebufferUpdatedEventArgs update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var scheduleRender = false;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (update.Width == 0 || update.Height == 0)
            {
                throw new InvalidDataException("A framebuffer update must have non-zero dimensions.");
            }

            if (_pixels is null || _width != update.Width || _height != update.Height)
            {
                _width = update.Width;
                _height = update.Height;
                _pixels = new byte[checked(_width * _height * 4)];
                _dirty = new DirtyRectangle(0, 0, _width, _height);
            }

            foreach (var rectangle in update.Rectangles)
            {
                ApplyRectangle(rectangle);
            }

            if (_dirty is null)
            {
                return;
            }

            if (!_renderQueued)
            {
                _renderQueued = true;
                scheduleRender = true;
            }
        }

        if (scheduleRender)
        {
            ScheduleRender();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _pixels = null;
            _dirty = null;
        }
    }

    private void ApplyRectangle(FramebufferRectangle rectangle)
    {
        var right = checked(rectangle.X + rectangle.Width);
        var bottom = checked(rectangle.Y + rectangle.Height);
        var expectedLength = checked(rectangle.Width * rectangle.Height * 4);
        if (rectangle.Width == 0 || rectangle.Height == 0 ||
            right > _width || bottom > _height ||
            rectangle.Data.Length != expectedLength)
        {
            throw new InvalidDataException("The framebuffer rectangle is outside the current image or has an invalid pixel buffer.");
        }

        var rowBytes = rectangle.Width * 4;
        for (var row = 0; row < rectangle.Height; row++)
        {
            var sourceOffset = row * rowBytes;
            var destinationOffset = ((rectangle.Y + row) * _width + rectangle.X) * 4;
            Buffer.BlockCopy(rectangle.Data, sourceOffset, _pixels!, destinationOffset, rowBytes);
        }

        var updated = new DirtyRectangle(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        _dirty = _dirty is { } current ? current.Union(updated) : updated;
    }

    private void ScheduleRender()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            lock (_sync)
            {
                _renderQueued = false;
            }

            Trace.WriteLine("[LucyVNC UI] Dispatcher is shutting down; pending framebuffer rendering was discarded.");
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Drain));
        }
        catch (InvalidOperationException exception)
        {
            lock (_sync)
            {
                _renderQueued = false;
            }

            Trace.WriteLine($"[LucyVNC UI] Could not schedule framebuffer rendering: {exception.Message}");
        }
    }

    private void Drain()
    {
        FramebufferRenderUpdate? update = null;
        lock (_sync)
        {
            if (_disposed)
            {
                _renderQueued = false;
                return;
            }

            if (_dirty is { } dirty && _pixels is not null)
            {
                var rowBytes = checked(dirty.Width * 4);
                var pixels = new byte[checked(rowBytes * dirty.Height)];
                for (var row = 0; row < dirty.Height; row++)
                {
                    var sourceOffset = ((dirty.Y + row) * _width + dirty.X) * 4;
                    Buffer.BlockCopy(_pixels, sourceOffset, pixels, row * rowBytes, rowBytes);
                }

                update = new FramebufferRenderUpdate(_width, _height, dirty.X, dirty.Y, dirty.Width, dirty.Height, pixels);
                _dirty = null;
            }
        }

        if (update is not null)
        {
            try
            {
                _render(update);
            }
            catch (Exception exception)
            {
                try
                {
                    _renderFailed(exception);
                }
                catch (Exception reportingException)
                {
                    Trace.WriteLine($"[LucyVNC UI] Could not report framebuffer rendering failure: {reportingException.Message}");
                }
            }
        }

        var scheduleAnother = false;
        lock (_sync)
        {
            if (_disposed || _dirty is null)
            {
                _renderQueued = false;
            }
            else
            {
                scheduleAnother = true;
            }
        }

        if (scheduleAnother)
        {
            ScheduleRender();
        }
    }

    private readonly record struct DirtyRectangle(int X, int Y, int Width, int Height)
    {
        public DirtyRectangle Union(DirtyRectangle other)
        {
            var left = Math.Min(X, other.X);
            var top = Math.Min(Y, other.Y);
            var right = Math.Max(X + Width, other.X + other.Width);
            var bottom = Math.Max(Y + Height, other.Y + other.Height);
            return new DirtyRectangle(left, top, right - left, bottom - top);
        }
    }
}

internal sealed record FramebufferRenderUpdate(
    int FramebufferWidth,
    int FramebufferHeight,
    int X,
    int Y,
    int Width,
    int Height,
    byte[] Pixels);
