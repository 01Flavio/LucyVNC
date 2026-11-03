using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Vnc.Platform.Windows;

public interface IRemoteInput : IDisposable
{
    void SetFramebufferSize(ushort width, ushort height);
    void ApplyKeyEvent(uint keySym, bool pressed);
    void ApplyPointerEvent(ushort x, ushort y, byte buttonMask);
    void ReleaseAll();
}

public static class ScreenCoordinateMapper
{
    public static Point MapFramebufferPoint(
        ushort x,
        ushort y,
        ushort framebufferWidth,
        ushort framebufferHeight,
        Rectangle screenBounds)
    {
        if (framebufferWidth == 0 || framebufferHeight == 0 || screenBounds.Width <= 0 || screenBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framebufferWidth), "Framebuffer and screen dimensions must be positive.");
        }

        var remoteX = Math.Clamp((int)x, 0, framebufferWidth - 1);
        var remoteY = Math.Clamp((int)y, 0, framebufferHeight - 1);
        var targetX = screenBounds.Left + (int)((long)remoteX * (screenBounds.Width - 1) / Math.Max(1, framebufferWidth - 1));
        var targetY = screenBounds.Top + (int)((long)remoteY * (screenBounds.Height - 1) / Math.Max(1, framebufferHeight - 1));
        return new Point(targetX, targetY);
    }
}

public static class RfbInputMapper
{
    private static readonly IReadOnlyDictionary<uint, ushort> SpecialKeys = new Dictionary<uint, ushort>
    {
        [0xff08] = 0x08, [0xff09] = 0x09, [0xff0d] = 0x0d, [0xff1b] = 0x1b,
        [0xff50] = 0x24, [0xff51] = 0x25, [0xff52] = 0x26, [0xff53] = 0x27,
        [0xff54] = 0x28, [0xff55] = 0x21, [0xff56] = 0x22, [0xff57] = 0x23,
        [0xff63] = 0x2d, [0xffff] = 0x2e, [0xffe1] = 0xa0, [0xffe2] = 0xa1,
        [0xffe3] = 0xa2, [0xffe4] = 0xa3, [0xffe9] = 0xa4, [0xffea] = 0xa5,
        [0xffeb] = 0x5b, [0xffec] = 0x5c, [0xffe5] = 0x14, [0xff7f] = 0x90,
        [0xffe7] = 0x5b, [0xffe8] = 0x5c, [0xff13] = 0x13, [0xff14] = 0x91,
    };

    public static bool TryGetVirtualKey(uint keySym, out ushort virtualKey)
    {
        if (SpecialKeys.TryGetValue(keySym, out virtualKey))
        {
            return true;
        }

        if (keySym is >= 0x61 and <= 0x7a)
        {
            virtualKey = (ushort)(keySym - 0x20);
            return true;
        }

        if (keySym is >= 0x41 and <= 0x5a or >= 0x30 and <= 0x39)
        {
            virtualKey = (ushort)keySym;
            return true;
        }

        if (keySym is >= 0xffbe and <= 0xffe5)
        {
            virtualKey = (ushort)(0x70 + keySym - 0xffbe);
            return virtualKey <= 0x87;
        }

        if (keySym <= char.MaxValue)
        {
            var mapped = VkKeyScan((char)keySym);
            if (mapped != -1)
            {
                virtualKey = (ushort)(mapped & 0xff);
                return true;
            }
        }

        virtualKey = 0;
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char ch);
}

public sealed class SendInputController : IRemoteInput
{
    private readonly HashSet<uint> _pressedKeySyms = new();
    private readonly Rectangle _screenBounds;
    private byte _buttonMask;
    private ushort _framebufferWidth;
    private ushort _framebufferHeight;
    private bool _disposed;

    public SendInputController()
    {
        _screenBounds = Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Windows did not report a primary display.");
    }

    public void SetFramebufferSize(ushort width, ushort height)
    {
        if (width == 0 || height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        _framebufferWidth = width;
        _framebufferHeight = height;
    }

    public void ApplyKeyEvent(uint keySym, bool pressed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!RfbInputMapper.TryGetVirtualKey(keySym, out var virtualKey))
        {
            throw new InvalidDataException($"Unsupported remote keysym 0x{keySym:X}.");
        }

        SendKeyboard(virtualKey, !pressed);
        if (pressed)
        {
            _pressedKeySyms.Add(keySym);
        }
        else
        {
            _pressedKeySyms.Remove(keySym);
        }
    }

    public void ApplyPointerEvent(ushort x, ushort y, byte buttonMask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = ScreenCoordinateMapper.MapFramebufferPoint(x, y, _framebufferWidth, _framebufferHeight, _screenBounds);
        if (!SetCursorPos(target.X, target.Y))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not move the Windows pointer.");
        }

        var pressed = (byte)(buttonMask & ~_buttonMask);
        var released = (byte)(_buttonMask & ~buttonMask);
        ApplyMouseButton(pressed, 1, MouseEventLeftDown);
        ApplyMouseButton(released, 1, MouseEventLeftUp);
        ApplyMouseButton(pressed, 2, MouseEventMiddleDown);
        ApplyMouseButton(released, 2, MouseEventMiddleUp);
        ApplyMouseButton(pressed, 4, MouseEventRightDown);
        ApplyMouseButton(released, 4, MouseEventRightUp);
        if ((pressed & 8) != 0)
        {
            SendMouse(MouseEventWheel, unchecked((uint)120));
        }
        if ((pressed & 16) != 0)
        {
            SendMouse(MouseEventWheel, unchecked((uint)-120));
        }

        _buttonMask = (byte)(buttonMask & 7);
    }

    public void ReleaseAll()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var keySym in _pressedKeySyms.ToArray())
        {
            if (RfbInputMapper.TryGetVirtualKey(keySym, out var virtualKey))
            {
                SendKeyboard(virtualKey, true);
            }
        }

        _pressedKeySyms.Clear();
        ApplyMouseButton(_buttonMask, 1, MouseEventLeftUp);
        ApplyMouseButton(_buttonMask, 2, MouseEventMiddleUp);
        ApplyMouseButton(_buttonMask, 4, MouseEventRightUp);
        _buttonMask = 0;
    }

    private static void ApplyMouseButton(byte changed, byte mask, uint flag)
    {
        if ((changed & mask) != 0)
        {
            SendMouse(flag, 0);
        }
    }

    private static void SendKeyboard(ushort virtualKey, bool keyUp)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyboardEventKeyUp : 0,
                },
            },
        };
        Send(input);
    }

    private static void SendMouse(uint flags, uint data = 0)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    MouseData = data,
                    Flags = flags,
                },
            },
        };
        Send(input);
    }

    private static void Send(Input input)
    {
        if (SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected a remote input event.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseAll();
        _disposed = true;
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyboardEventKeyUp = 0x0002;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
