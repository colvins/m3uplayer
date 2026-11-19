using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace M3UPlayer;

// Slots are from mdk/c/Player.h in the bundled MDK 0.40.1 x64 SDK.
internal sealed class Mdk : IPlaybackEngine
{
    private IntPtr api;
    private readonly IntPtr player;
    private readonly Panel surface;
    private readonly Func<bool> canResize;
    private bool active;
    private int surfaceWidth, surfaceHeight;

    public Mdk(Panel surface, Func<bool> canResize)
    {
        this.surface = surface;
        this.canResize = canResize;
        MDK_setLogLevel(0);
        api = mdkPlayerAPI_new();
        if (api == IntPtr.Zero) throw new InvalidOperationException("MDK player creation failed.");
        player = Marshal.ReadIntPtr(api);
        try
        {
            var window = surface.Handle;
            var renderer = new D3D11RenderApi { Type = 4, Buffers = 2 };
            Bind<RenderApi>(56)(player, ref renderer, window);
            RefreshSurface();
            surface.SizeChanged += ResizeSurface;
            Bind<Decoders>(11)(player, ["D3D11", "FFmpeg", null]);
        }
        catch { DetachResizeHandler(); Dispose(); throw; }
    }

    public void Play(string url, Channel channel)
    {
        Stop();
        Bind<SetMedia>(3)(player, url);
        var http = Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https";
        // Keep the normal startup threshold while letting HTTP streams read ahead.
        Bind<BufferRange>(47)(player, http ? 1000 : -1, http ? 25000 : -1, false);
        var option = Bind<Property>(49);
        option(player, "avformat.user_agent", channel.UserAgent ?? "mpv");
        option(player, "avformat.referer", channel.Referrer ?? "");
        option(player, "avformat.allowed_extensions", "ALL");
        option(player, "avformat.allowed_segment_extensions", "ALL");
        option(player, "avformat.extension_picky", "0");
        active = true;
        Bind<SetState>(15)(player, 1);
    }

    public void SetVolume(double value) => Bind<Volume>(2)(player, (float)(value / 100));
    public void TogglePause()
    {
        if (active) Bind<SetState>(15)(player, Bind<GetState>(16)(player) == 2 ? 1 : 2);
    }
    public void Stop()
    {
        if (!active) return;
        active = false;
        Bind<SetState>(15)(player, 0);
        Bind<WaitFor>(18)(player, 0, 1500);
    }
    public void Poll(Action<string> status)
    {
        if (!active) return;
        var media = Bind<GetState>(19)(player);
        if ((media & unchecked((int)0x80000000)) != 0)
        {
            active = false;
            status("播放失败：无法打开频道");
        }
        else if (Bind<GetState>(16)(player) == 2) status("已暂停");
        else if ((media & 2) != 0) status("正在连接频道…");
        else if ((media & 24) != 0) status("正在缓冲…");
        else if ((media & 64) != 0 && Bind<GetState>(16)(player) == 0)
        {
            active = false;
            status("播放已结束，可点击重试");
        }
        else if ((media & 256) != 0) status("正在播放");
    }
    private void ResizeSurface(object? sender, EventArgs e)
    {
        if (canResize()) RefreshSurface();
    }
    public void RefreshSurface()
    {
        var width = surface.Width;
        var height = surface.Height;
        if (api == IntPtr.Zero || width <= 0 || height <= 0 || (width == surfaceWidth && height == surfaceHeight)) return;
        Bind<NativeSurface>(21)(player, surface.Handle, width, height, 0);
        surfaceWidth = width;
        surfaceHeight = height;
    }
    private T Bind<T>(int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(api, slot * IntPtr.Size));
    public void DetachResizeHandler() => surface.SizeChanged -= ResizeSurface;
    public void Dispose()
    {
        if (api == IntPtr.Zero) return;
        mdkPlayerAPI_delete(ref api);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11RenderApi
    {
        public int Type;
        public IntPtr Context, RenderTarget, Reserved1, Reserved2;
        public byte Debug;
        public int Buffers, Adapter;
        public float FeatureLevel;
        public IntPtr Vendor;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderApi(IntPtr player, ref D3D11RenderApi renderer, IntPtr window);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeSurface(IntPtr player, IntPtr window, int width, int height, int type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Decoders(IntPtr player, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] names);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetMedia(IntPtr player, [MarshalAs(UnmanagedType.LPUTF8Str)] string url);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Property(IntPtr player, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetState(IntPtr player, int state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetState(IntPtr player);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool WaitFor(IntPtr player, int state, int timeoutMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Volume(IntPtr player, float volume);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BufferRange(IntPtr player, long minMs, long maxMs, [MarshalAs(UnmanagedType.I1)] bool drop);
    [DllImport("mdk.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mdkPlayerAPI_new();
    [DllImport("mdk.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mdkPlayerAPI_delete(ref IntPtr api);
    [DllImport("mdk.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void MDK_setLogLevel(int level);
}
