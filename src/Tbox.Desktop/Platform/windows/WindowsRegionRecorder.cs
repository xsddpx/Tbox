#if WINDOWS
using Tbox.App;
using Windows.Graphics;

namespace Tbox.Desktop.Platform;

internal sealed class WindowsRegionRecorder : IRegionRecorder
{
    private readonly ScreenRegionRecorder _recorder;
    private readonly bool _audio;
    public WindowsRegionRecorder(RecordingArea area, bool audio, int fps)
    {
        _audio = audio;
        _recorder = new ScreenRegionRecorder(new RectInt32((int)Math.Round(area.Bounds.Left), (int)Math.Round(area.Bounds.Top),
            area.PixelWidth, area.PixelHeight), audio, fps);
    }
    public async Task StartAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _recorder.StartAsync(path);
        cancellationToken.ThrowIfCancellationRequested();
    }
    public void Pause() => _recorder.Pause();
    public void Resume() => _recorder.Resume();
    public async Task<RecordingOutput> StopAsync(CancellationToken cancellationToken) =>
        new(await _recorder.StopAsync(), _audio, 0);
    public void Dispose() => _recorder.Dispose();
}
#endif
