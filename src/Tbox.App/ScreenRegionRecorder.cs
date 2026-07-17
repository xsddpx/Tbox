using ScreenRecorderLib;
using Tbox.Core;
using Windows.Graphics;

namespace Tbox.App;

internal sealed class ScreenRegionRecorder : IDisposable
{
    private readonly Recorder _recorder;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    internal ScreenRegionRecorder(RectInt32 region, bool includeSystemAudio, int framesPerSecond)
    {
        framesPerSecond = RecordingFrameRatePolicy.Validate(framesPerSecond);
        var display = ScreenCaptureService.GetDisplayInfo(region);
        var relativeX = region.X - display.Bounds.X;
        var relativeY = region.Y - display.Bounds.Y;
        var source = new DisplayRecordingSource(display.DeviceName)
        {
            RecorderApi = RecorderApi.WindowsGraphicsCapture,
            IsBorderRequired = false,
            IsCursorCaptureEnabled = true,
            SourceRect = new ScreenRect(relativeX, relativeY, region.Width, region.Height),
            OutputSize = new ScreenSize(region.Width, region.Height),
            Stretch = StretchMode.Fill
        };
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = [source]
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(region.Width, region.Height),
                Stretch = StretchMode.Fill
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.CBR,
                    EncoderProfile = H264Profile.Main
                },
                Bitrate = RecordingRegionPolicy.CalculateVideoBitrate(
                    region.Width,
                    region.Height,
                    framesPerSecond),
                Framerate = framesPerSecond,
                IsFixedFramerate = true,
                IsHardwareEncodingEnabled = true,
                IsLowLatencyEnabled = false,
                IsThrottlingDisabled = false,
                IsFragmentedMp4Enabled = false,
                IsMp4FastStartEnabled = true
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = includeSystemAudio,
                IsOutputDeviceEnabled = includeSystemAudio,
                IsInputDeviceEnabled = false,
                Bitrate = AudioBitrate.bitrate_128kbps,
                Channels = AudioChannels.Stereo
            },
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = true,
                IsMouseClicksDetected = false
            },
            LogOptions = new LogOptions { IsLogEnabled = false }
        };

        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnStatusChanged += OnStatusChanged;
        _recorder.OnRecordingComplete += OnRecordingComplete;
        _recorder.OnRecordingFailed += OnRecordingFailed;
    }

    internal RecorderStatus Status => _recorder.Status;

    internal async Task StartAsync(string outputPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recorder.Record(outputPath);
        await _started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recorder.Pause();
    }

    internal void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recorder.Resume();
    }

    internal async Task<string> StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recorder.Status is not RecorderStatus.Idle and not RecorderStatus.Finishing)
        {
            _recorder.Stop();
        }
        return await _completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs e)
    {
        if (e.Status == RecorderStatus.Recording) _started.TrySetResult();
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs e)
    {
        _started.TrySetResult();
        _completed.TrySetResult(e.FilePath);
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        var exception = new InvalidOperationException(e.Error);
        _started.TrySetException(exception);
        _completed.TrySetException(exception);
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (_recorder.Status is RecorderStatus.Recording or RecorderStatus.Paused)
            {
                _recorder.Stop();
            }
        }
        catch
        {
            // Best-effort stop during window teardown.
        }
        _recorder.OnStatusChanged -= OnStatusChanged;
        _recorder.OnRecordingComplete -= OnRecordingComplete;
        _recorder.OnRecordingFailed -= OnRecordingFailed;
        _recorder.Dispose();
        _disposed = true;
    }
}
