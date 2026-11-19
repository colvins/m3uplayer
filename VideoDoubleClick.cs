using System.Runtime.InteropServices;

namespace M3UPlayer;

// Native video children can own their own window thread. Observe only clicks
// targeting the current video HWND or its descendants without consuming input.
internal sealed class VideoDoubleClick : IDisposable
{
    private readonly Func<IntPtr> surface;
    private readonly Action toggle;
    private readonly Hook callback;
    private IntPtr hook;
    private IntPtr lastSurface;
    private NativePoint lastPoint;
    private uint lastTime;

    public VideoDoubleClick(Func<IntPtr> surface, Action toggle)
    {
        this.surface = surface;
        this.toggle = toggle;
        callback = Observe;
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
    }

    private IntPtr Observe(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message == (IntPtr)0x0201)
        {
            var click = Marshal.PtrToStructure<MouseEvent>(data);
            var window = surface();
            var target = window == IntPtr.Zero ? IntPtr.Zero : WindowFromPoint(click.Point);
            if (window != IntPtr.Zero && (target == window || IsChild(window, target)))
            {
                if (lastSurface == window && unchecked(click.Time - lastTime) <= GetDoubleClickTime()
                    && Math.Abs(click.Point.X - lastPoint.X) <= Math.Max(1, GetSystemMetrics(36) / 2)
                    && Math.Abs(click.Point.Y - lastPoint.Y) <= Math.Max(1, GetSystemMetrics(37) / 2))
                {
                    lastSurface = IntPtr.Zero;
                    toggle();
                }
                else
                {
                    lastSurface = window;
                    lastPoint = click.Point;
                    lastTime = click.Time;
                }
            }
            else lastSurface = IntPtr.Zero;
        }
        return CallNextHookEx(hook, code, message, data);
    }

    public void Dispose()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEvent { public NativePoint Point; public uint Data, Flags, Time; public IntPtr ExtraInfo; }
    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, Hook proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
