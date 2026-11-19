namespace M3UPlayer;

internal interface IPlaybackEngine : IDisposable
{
    void Play(string url, Channel channel);
    void Stop();
    void TogglePause();
    void SetVolume(double value);
    void Poll(Action<string> status);
}
