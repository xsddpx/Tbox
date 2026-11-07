using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class RecordingTests
{
    [Fact]
    public void PauseDoesNotAccumulateOutputTimeAndStopFreezesClock()
    {
        var now = TimeSpan.Zero; var timeline = new RecordingTimeline(() => now);
        timeline.Start(); now += TimeSpan.FromSeconds(2); timeline.Pause();
        now += TimeSpan.FromSeconds(100); Assert.Equal(TimeSpan.FromSeconds(2), timeline.Elapsed);
        timeline.Resume(); now += TimeSpan.FromSeconds(3); timeline.Stop(); now += TimeSpan.FromSeconds(50);
        Assert.Equal(TimeSpan.FromSeconds(5), timeline.Elapsed);
        Assert.Equal(RecordingSessionState.Reviewing, timeline.State);
        Assert.Throws<InvalidOperationException>(timeline.Resume);
    }

    [Fact]
    public async Task AudioFailureReleasesWriterBeforeRetryingSilently()
    {
        using var temporary = new TemporaryDirectory();
        var audio = new FakeRecorder { FailStart = true }; var silent = new FakeRecorder();
        await using var operation = new RecordingOperation((enabled, fps) =>
        {
            Assert.Equal(25, fps);
            if (!enabled) Assert.True(audio.Disposed);
            return enabled ? audio : silent;
        }, new RecordingWorkspace(temporary.Path));
        await operation.StartAsync(true, 25);
        Assert.NotNull(operation.Notice); Assert.True(silent.Started);
        var output = await operation.StopAsync(); Assert.False(output.HasSystemAudio);
        Assert.True(silent.Disposed);
        using var transferred = operation.TransferWorkspace();
        await operation.DisposeAsync(); Assert.True(File.Exists(output.FilePath));
        Assert.Throws<InvalidOperationException>(() => operation.TransferWorkspace());
    }

    [Fact]
    public async Task CancellingPendingStartWaitsForWriterReleaseBeforeDeletingWorkspace()
    {
        using var temporary = new TemporaryDirectory();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new FakeRecorder { StartSignal = start.Task };
        var operation = new RecordingOperation((_, _) => recorder, new RecordingWorkspace(temporary.Path));
        var pending = operation.StartAsync(false, 10);
        var cancelled = operation.DisposeAsync().AsTask();
        Assert.False(cancelled.IsCompleted); Assert.True(Directory.Exists(operation.Workspace.SessionDirectory));
        start.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await cancelled;
        Assert.True(recorder.Disposed); Assert.False(Directory.Exists(operation.Workspace.SessionDirectory));
    }

    [Fact]
    public async Task FailedStopCannotTransferAnUnfinishedMaster()
    {
        using var temporary = new TemporaryDirectory();
        var recorder = new FakeRecorder { FailStop = true };
        await using var operation = new RecordingOperation((_, _) => recorder, new RecordingWorkspace(temporary.Path));
        await operation.StartAsync(false, 10);
        await Assert.ThrowsAsync<IOException>(() => operation.StopAsync());
        Assert.True(recorder.Disposed);
        Assert.Throws<InvalidOperationException>(() => operation.TransferWorkspace());
    }

    [Fact]
    public async Task CancellingPendingStopWaitsForCompletionAndDoesNotTransferFiles()
    {
        using var temporary = new TemporaryDirectory();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new FakeRecorder { StopSignal = stop.Task };
        var operation = new RecordingOperation((_, _) => recorder, new RecordingWorkspace(temporary.Path));
        await operation.StartAsync(false, 10);
        var pending = operation.StopAsync(); var cancelled = operation.DisposeAsync().AsTask();
        Assert.False(cancelled.IsCompleted); stop.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); await cancelled;
        Assert.False(Directory.Exists(operation.Workspace.SessionDirectory));
    }

    [Fact]
    public async Task CancelledOverwritePreservesDestinationAndDeletesStage()
    {
        using var temporary = new TemporaryDirectory();
        var source = System.IO.Path.Combine(temporary.Path, "master.mp4"); var target = System.IO.Path.Combine(temporary.Path, "saved.mp4");
        await File.WriteAllTextAsync(source, "new recording"); await File.WriteAllTextAsync(target, "existing recording");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecordingExporter.SaveAtomicallyAsync(source, target, new CancellationToken(true)));
        Assert.Equal("existing recording", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(temporary.Path, ".tbox-save-*"));
        await RecordingExporter.SaveAtomicallyAsync(source, target, CancellationToken.None);
        Assert.Equal("new recording", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public void StartupCleanupDeletesOnlyExpiredOwnedSessions()
    {
        using var temporary = new TemporaryDirectory();
        using var old = new RecordingWorkspace(temporary.Path); using var recent = new RecordingWorkspace(temporary.Path);
        var unrelated = System.IO.Path.Combine(temporary.Path, "user-folder"); Directory.CreateDirectory(unrelated);
        var now = DateTime.UtcNow;
        Directory.SetLastWriteTimeUtc(old.SessionDirectory, now.AddHours(-25)); Directory.SetLastWriteTimeUtc(unrelated, now.AddDays(-3));
        RecordingWorkspace.CleanupExpired(temporary.Path, now);
        Assert.False(Directory.Exists(old.SessionDirectory)); Assert.True(Directory.Exists(recent.SessionDirectory)); Assert.True(Directory.Exists(unrelated));
    }

    private sealed class FakeRecorder : IRegionRecorder
    {
        public Task StartSignal = Task.CompletedTask, StopSignal = Task.CompletedTask;
        public bool FailStart, FailStop, Started, Disposed;
        private string _path = "";
        public async Task StartAsync(string path, CancellationToken cancellationToken)
        {
            _path = path; await File.WriteAllTextAsync(path, "master"); await StartSignal;
            cancellationToken.ThrowIfCancellationRequested();
            if (FailStart) throw new IOException("audio unavailable"); Started = true;
        }
        public void Pause() { }
        public void Resume() { }
        public async Task<RecordingOutput> StopAsync(CancellationToken cancellationToken)
        { await StopSignal; if (FailStop) throw new IOException("write failed"); return new(_path, false, 1); }
        public void Dispose() { Assert.True(File.Exists(_path)); Disposed = true; }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tbox-recording-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
