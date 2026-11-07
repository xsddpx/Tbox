using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Models;

/// <summary>Owns the writer and temporary files until a completed master is transferred to review.</summary>
public sealed class RecordingOperation : IAsyncDisposable
{
    private readonly Func<bool, int, IRegionRecorder> _createRecorder;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _cancel = new();
    private IRegionRecorder? _recorder;
    private bool _disposed, _transferred, _completed;
    public RecordingTimeline Timeline { get; }
    public RecordingWorkspace Workspace { get; }
    public string? Failure => _recorder?.Failure;
    public string? Notice { get; private set; }

    public RecordingOperation(Func<bool, int, IRegionRecorder> createRecorder, RecordingWorkspace? workspace = null, RecordingTimeline? timeline = null)
    { _createRecorder = createRecorder; Workspace = workspace ?? new(); Timeline = timeline ?? new(); }

    public async Task StartAsync(bool audio, int fps)
    {
        RecordingFrameRatePolicy.Validate(fps);
        await _gate.WaitAsync(_cancel.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Timeline.State != RecordingSessionState.Ready) throw new InvalidOperationException("录屏已经开始。");
            try { await StartRecorderAsync(audio, fps); }
            catch (Exception) when (audio && !_cancel.IsCancellationRequested)
            {
                await ReleaseRecorderAsync(); Workspace.ResetMaster();
                await StartRecorderAsync(false, fps);
                Notice = "系统声音不可用，已改为静音录制。";
            }
            _cancel.Token.ThrowIfCancellationRequested(); Timeline.Start();
        }
        catch { await ReleaseRecorderAsync(); throw; }
        finally { _gate.Release(); }
    }

    private async Task StartRecorderAsync(bool audio, int fps)
    {
        _recorder = _createRecorder(audio, fps);
        await _recorder.StartAsync(Workspace.MasterFilePath, _cancel.Token);
    }

    public void Pause() { _recorder!.Pause(); Timeline.Pause(); }
    public void Resume() { _recorder!.Resume(); Timeline.Resume(); }

    public async Task<RecordingOutput> StopAsync()
    {
        await _gate.WaitAsync(_cancel.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Timeline.Stop();
            var result = await (_recorder ?? throw new InvalidOperationException("录屏尚未开始。")).StopAsync(CancellationToken.None);
            await ReleaseRecorderAsync();
            _cancel.Token.ThrowIfCancellationRequested();
            _completed = true;
            return result;
        }
        catch { await ReleaseRecorderAsync(); throw; }
        finally { _gate.Release(); }
    }

    public RecordingWorkspace TransferWorkspace()
    {
        if (_disposed || _transferred || !_completed || _cancel.IsCancellationRequested || _recorder is not null || Timeline.State != RecordingSessionState.Reviewing)
            throw new InvalidOperationException("母版尚未完成。");
        _transferred = true; return Workspace;
    }

    private async Task ReleaseRecorderAsync()
    {
        var recorder = _recorder; _recorder = null;
        if (recorder is not null) await recorder.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Signal immediately, then wait for any start/stop callback before removing its files.
        _cancel.Cancel(); await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true; await ReleaseRecorderAsync();
            if (!_transferred) Workspace.Dispose();
        }
        finally { _gate.Release(); }
    }
}
