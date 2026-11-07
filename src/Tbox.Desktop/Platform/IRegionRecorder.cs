namespace Tbox.Desktop.Platform;

public sealed record RecordingArea(DesktopPlacement Bounds, int PixelWidth, int PixelHeight);
public sealed record RecordingOutput(string FilePath, bool HasSystemAudio, double DurationSeconds);

public interface IRegionRecorder : IDisposable, IAsyncDisposable
{
    string? Failure => null;
    Task StartAsync(string path, CancellationToken cancellationToken);
    void Pause();
    void Resume();
    Task<RecordingOutput> StopAsync(CancellationToken cancellationToken);
    ValueTask IAsyncDisposable.DisposeAsync() => new(Task.Run(Dispose));
}
