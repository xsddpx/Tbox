using System.Diagnostics;
using Tbox.Core;

namespace Tbox.Desktop.Models;

public sealed class RecordingTimeline
{
    private readonly Func<TimeSpan> _clock;
    private TimeSpan _started, _accumulated;
    public RecordingSessionState State { get; private set; } = RecordingSessionState.Ready;
    public TimeSpan Elapsed => _accumulated + (State == RecordingSessionState.Recording ? _clock() - _started : TimeSpan.Zero);
    public RecordingTimeline(Func<TimeSpan>? clock = null)
    {
        var stopwatch = Stopwatch.StartNew();
        _clock = clock ?? (() => stopwatch.Elapsed);
    }
    public void Start() { Require(RecordingSessionState.Ready); _started = _clock(); State = RecordingSessionState.Recording; }
    public void Pause() { Require(RecordingSessionState.Recording); _accumulated += _clock() - _started; State = RecordingSessionState.Paused; }
    public void Resume() { Require(RecordingSessionState.Paused); _started = _clock(); State = RecordingSessionState.Recording; }
    public void Stop()
    {
        if (State == RecordingSessionState.Recording) _accumulated += _clock() - _started;
        else Require(RecordingSessionState.Paused);
        State = RecordingSessionState.Reviewing;
    }
    private void Require(RecordingSessionState state)
    { if (State != state) throw new InvalidOperationException($"不能从 {State} 执行此录屏操作。"); }
}
