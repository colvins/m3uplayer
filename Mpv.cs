using System.Runtime.InteropServices;
namespace M3UPlayer;

internal sealed class Mpv : IPlaybackEngine
{
    private IntPtr handle;
    private IntPtr library;
    private bool active;
    [StructLayout(LayoutKind.Sequential)]
    private struct Event { public int Id, Error; public ulong Reply; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct EndFile { public int Reason, Error; }

    public Mpv(uint window, bool headless = false, string? libraryPath = null)
    {
        library = NativeLibrary.Load(libraryPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"));
        try
        {
            BindApi();
            handle = mpv_create();
            if (handle == IntPtr.Zero) throw new InvalidOperationException("mpv_create");
            foreach (var (key, value) in new (string, string)[] {
                ("demuxer-lavf-o",libraryPath == null ? "allowed_extensions=ALL,allowed_segment_extensions=ALL,extension_picky=0" : "allowed_extensions=ALL"), ("config","no"), ("idle","yes"), ("osc","no"), ("input-default-bindings","no"),
                ("input-vo-keyboard","no"), ("ytdl","no"), ("load-scripts","no"), ("tls-verify","yes"),
                ("hls-bitrate","max"), ("cache","yes"), ("cache-secs","24"), ("demuxer-max-bytes","256MiB"), ("cache-pause-wait","3"), ("network-timeout","15"),
                ("vo",headless ? "null" : "gpu"), ("ao",headless ? "null" : "wasapi"),
                ("hwdec",headless ? "no" : "d3d11va"), ("gpu-context", "d3d11"),
                ("audio-display","no"), ("osd-level","0")
            }) { var result = mpv_set_option_string(handle, key, value); if (!(result == -5 && key is ("osc" or "ytdl"))) Check(result); }
            if (!headless) Check(mpv_set_option_string(handle, "wid", window.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Check(mpv_initialize(handle));
        }
        catch { Dispose(); throw; }
    }
    public void Play(string url, Channel channel)
    {
        Set("user-agent", channel.UserAgent ?? "mpv");
        Set("referrer", channel.Referrer ?? "");

        Set("pause", "no");
        Command("loadfile", url, "replace");
    }
    public void SetVolume(double value) => Set("volume", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public void TogglePause() => Command("cycle", "pause");
    public void Stop() => Command("stop");
    public void Set(string key, string value) => Check(mpv_set_property_string(handle, key, value));
    public string? Get(string key)
    {
        var ptr = mpv_get_property_string(handle, key);
        if (ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(ptr); }
        finally { mpv_free(ptr); }
    }
    public void Command(params string[] args)
    {
        var pointers = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            Check(mpv_command(handle, pointers));
        }
        finally { foreach (var ptr in pointers) Marshal.FreeCoTaskMem(ptr); }
    }
    public void Poll(Action<string> status)
    {
        for (var i = 0; i < 64; i++)
        {
            var e = Marshal.PtrToStructure<Event>(mpv_wait_event(handle, 0));
            switch (e.Id)
            {
                case 0: i = 64; break;
                case 6: active = false; status("正在连接频道…"); break;
                case 21: active = true; break;
                case 7:
                    active = false;
                    var end = Marshal.PtrToStructure<EndFile>(e.Data);
                    if (end.Reason == 4) status("播放失败：无法打开或解码该频道。");
                    else if (end.Reason == 0) status("播放已结束，可点击重试。");
                    break;
            }
        }
        if (active)
        {
            var height = Get("video-out-params/h");
            var decoder = Get("hwdec-current");
            var detail = height == null ? "" : $" · {height}p";
            if (!string.IsNullOrEmpty(decoder) && decoder != "no") detail += " · 硬件解码";
            var cache = Get("demuxer-cache-duration");
            if (double.TryParse(cache, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                detail += $" · 缓冲 {seconds:F1} 秒";
            status((Get("paused-for-cache") == "yes" ? "正在缓冲…" : Get("pause") == "yes" ? "已暂停" : "正在播放") + detail);
        }
    }
    private static void Check(int code) { if (code < 0) throw new InvalidOperationException($"mpv error {code}"); }
    public void Dispose()
    {
        var instance = handle; handle = IntPtr.Zero;
        if (instance != IntPtr.Zero) mpv_terminate_destroy(instance);
        if (library != IntPtr.Zero) { NativeLibrary.Free(library); library = IntPtr.Zero; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr mpv_create_delegate();
    private mpv_create_delegate mpv_create = null!;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int mpv_initialize_delegate(IntPtr instance);
    private mpv_initialize_delegate mpv_initialize = null!;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void mpv_terminate_destroy_delegate(IntPtr instance);
    private mpv_terminate_destroy_delegate mpv_terminate_destroy = null!;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr mpv_wait_event_delegate(IntPtr instance, double timeout);
    private mpv_wait_event_delegate mpv_wait_event = null!;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void mpv_free_delegate(IntPtr pointer);
    private mpv_free_delegate mpv_free = null!;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int mpv_command_delegate(IntPtr instance, IntPtr[] args);
    private mpv_command_delegate mpv_command = null!;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int mpv_set_option_string_delegate(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    private mpv_set_option_string_delegate mpv_set_option_string = null!;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int mpv_set_property_string_delegate(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    private mpv_set_property_string_delegate mpv_set_property_string = null!;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr mpv_get_property_string_delegate(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    private mpv_get_property_string_delegate mpv_get_property_string = null!;

    private void BindApi()
    {
        T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        mpv_create = Load<mpv_create_delegate>(nameof(mpv_create));
        mpv_initialize = Load<mpv_initialize_delegate>(nameof(mpv_initialize));
        mpv_terminate_destroy = Load<mpv_terminate_destroy_delegate>(nameof(mpv_terminate_destroy));
        mpv_wait_event = Load<mpv_wait_event_delegate>(nameof(mpv_wait_event));
        mpv_free = Load<mpv_free_delegate>(nameof(mpv_free));
        mpv_command = Load<mpv_command_delegate>(nameof(mpv_command));
        mpv_set_option_string = Load<mpv_set_option_string_delegate>(nameof(mpv_set_option_string));
        mpv_set_property_string = Load<mpv_set_property_string_delegate>(nameof(mpv_set_property_string));
        mpv_get_property_string = Load<mpv_get_property_string_delegate>(nameof(mpv_get_property_string));
    }
}
