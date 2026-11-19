# 轻播 · M3U Player

Windows 11 x64 简单直播播放器，C# / WPF / MDK。

## 0.4.2

使用 MDK 0.40.1，优先 D3D11 硬件解码，并保留 FFmpeg 软件解码。针对香港「無綫新聞台」保留应用内的备用 DNS，仅在连接失败时向 Google DNS 查询域名。

## 使用

解压整个发布包后打开 `M3UPlayer.exe`，需要安装 .NET 10 Desktop Runtime x64。点击“添加订阅”粘贴自己的链接，或“导入文件”选择 M3U/M3U8。也支持拖入文件。单击频道播放，支持分组、搜索、暂停、停止、重试、音量和全屏。

- F11 切换全屏，Esc 退出全屏，空格暂停/继续。
- 上次列表会保存在 `%LOCALAPPDATA%\LightM3UPlayer`，启动时恢复但不自动播放；可点击“重试”播放上次频道。
- 订阅和频道地址仅保存在上述本机目录中，可能包含私人凭证，请勿分享该目录。
- 请保留发布文件夹内全部文件，MDK 和 mpv 原生库是必需的。
- 不保证每个第三方直播源一直在线；第一版不包含 EPG、录制或自动重连。

## 构建

安装 .NET 10 SDK，将 MDK 0.40.1 Windows x64 SDK 中的 mdk.dll、ffmpeg-9.dll、libass.dll 放入 engine/mdk，将 mpv Windows x64 构建 bcde6b93cc 的 libmpv-2.dll 放入 engine，再运行以下命令。

```powershell
dotnet publish M3UPlayer.csproj -c Release -r win-x64 --self-contained false -p:DebugType=None -o dist/LightM3UPlayer
dotnet run --project tests/ParserTests.csproj
```


[.NET Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) · [MDK 0.40.1](https://github.com/wang-bin/mdk-sdk/releases/tag/v0.40.1) · [mpv Windows builds](https://github.com/shinchiro/mpv-winbuild-cmake/releases)
