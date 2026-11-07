import AppKit
import AVFoundation
import ScreenCaptureKit

private func mediaError(_ message: String) -> NSError {
    NSError(domain: "Tbox.Recording", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
}

private final class ScreenRecording: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    let path: String
    let fps: Int
    let audioEnabled: Bool
    private let writer: AVAssetWriter
    private let video: AVAssetWriterInput
    private let audio: AVAssetWriterInput?
    private let adaptor: AVAssetWriterInputPixelBufferAdaptor
    private let queue = DispatchQueue(label: "Tbox.recording.samples")
    private var stream: SCStream?
    private var timer: DispatchSourceTimer?
    private var latest: CVPixelBuffer?
    private var origin: CMTime?
    private var pauseStart: CMTime?
    private var pausedDuration = CMTime.zero
    private var frames: Int64 = 0
    private var finished = false
    private var failure: String?

    init(path: String, width: Int, height: Int, fps: Int, audioEnabled: Bool) throws {
        self.path = path; self.fps = fps; self.audioEnabled = audioEnabled
        writer = try AVAssetWriter(outputURL: URL(fileURLWithPath: path), fileType: .mp4)
        writer.shouldOptimizeForNetworkUse = true
        video = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: width, AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: min(20_000_000, max(2_000_000, Int(Double(width * height * fps) * 0.12))),
                AVVideoExpectedSourceFrameRateKey: fps, AVVideoMaxKeyFrameIntervalKey: fps * 2,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel]
        ])
        video.expectsMediaDataInRealTime = true
        adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: video, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: width, kCVPixelBufferHeightKey as String: height
        ])
        guard writer.canAdd(video) else { throw mediaError("无法创建 H.264 编码器。") }
        writer.add(video)
        if audioEnabled {
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: [AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 128000])
            input.expectsMediaDataInRealTime = true
            guard writer.canAdd(input) else { throw mediaError("系统声音编码不可用。") }
            writer.add(input); audio = input
        } else { audio = nil }
        super.init()
    }

    func start(region: CGRect, width: Int, height: Int) async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { CGDisplayBounds($0.displayID).contains(region) }) else {
            throw mediaError("录屏区域已不在当前显示器中。")
        }
        let bounds = CGDisplayBounds(display.displayID)
        let excluded = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        let filter = SCContentFilter(display: display, excludingApplications: excluded, exceptingWindows: [])
        let config = SCStreamConfiguration()
        config.sourceRect = CGRect(x: region.minX - bounds.minX, y: region.minY - bounds.minY, width: region.width, height: region.height)
        config.width = width; config.height = height; config.pixelFormat = kCVPixelFormatType_32BGRA
        config.colorSpaceName = CGColorSpace.sRGB; config.showsCursor = true
        config.minimumFrameInterval = CMTime(value: 1, timescale: Int32(fps)); config.queueDepth = 5
        config.capturesAudio = audioEnabled; config.excludesCurrentProcessAudio = true
        config.sampleRate = 48000; config.channelCount = 2
        let capture = SCStream(filter: filter, configuration: config, delegate: self)
        try capture.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        if audioEnabled { try capture.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue) }
        guard writer.startWriting() else { throw writer.error ?? mediaError("无法开始写入录屏文件。") }
        writer.startSession(atSourceTime: .zero)
        stream = capture
        do { try await capture.startCapture() }
        catch { writer.cancelWriting(); stream = nil; throw error }
        queue.sync {
            let clock = DispatchSource.makeTimerSource(queue: queue)
            clock.schedule(deadline: .now(), repeating: 1.0 / Double(fps), leeway: .milliseconds(2))
            clock.setEventHandler { [weak self] in self?.writeVideo() }
            timer = clock; clock.resume()
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sample: CMSampleBuffer, of type: SCStreamOutputType) {
        guard !finished, failure == nil, sample.isValid else { return }
        if type == .screen {
            guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
                  let status = attachments.first?[.status] as? Int, status == SCFrameStatus.complete.rawValue,
                  let image = sample.imageBuffer else { return }
            latest = image
            if origin == nil { origin = sample.presentationTimeStamp }
        } else if type == .audio, let audio = audio, audio.isReadyForMoreMediaData,
                  let origin = origin, pauseStart == nil {
            let offset = origin + pausedDuration
            if sample.presentationTimeStamp < offset { return }
            var count = 0
            guard CMSampleBufferGetSampleTimingInfoArray(sample, entryCount: 0, arrayToFill: nil, entriesNeededOut: &count) == noErr, count > 0 else { return }
            var timing = Array(repeating: CMSampleTimingInfo(), count: count)
            guard CMSampleBufferGetSampleTimingInfoArray(sample, entryCount: count, arrayToFill: &timing, entriesNeededOut: nil) == noErr else { return }
            for index in timing.indices {
                timing[index].presentationTimeStamp = timing[index].presentationTimeStamp - offset
                if timing[index].decodeTimeStamp.isValid { timing[index].decodeTimeStamp = timing[index].decodeTimeStamp - offset }
            }
            var adjusted: CMSampleBuffer?
            let result = timing.withUnsafeBufferPointer { values in
                CMSampleBufferCreateCopyWithNewTiming(allocator: kCFAllocatorDefault, sampleBuffer: sample,
                    sampleTimingEntryCount: count, sampleTimingArray: values.baseAddress!, sampleBufferOut: &adjusted)
            }
            if result == noErr, let adjusted = adjusted, !audio.append(adjusted) { failure = writer.error?.localizedDescription ?? "系统声音写入失败。" }
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async { if !self.finished { self.failure = error.localizedDescription } }
    }

    private func writeVideo() {
        guard !finished, failure == nil, pauseStart == nil, let image = latest, let origin = origin else { return }
        let elapsed = max(0, (CMClockGetTime(CMClockGetHostTimeClock()) - origin - pausedDuration).seconds)
        let target = Int64(floor(elapsed * Double(fps)))
        if target - frames > Int64(fps * 5) { failure = "编码速度不足，请降低帧率或减小录制范围。"; return }
        // Duplicate a static screen at the chosen rate, including quiet periods at the end.
        while frames <= target && video.isReadyForMoreMediaData {
            if !adaptor.append(image, withPresentationTime: CMTime(value: frames, timescale: Int32(fps))) {
                failure = writer.error?.localizedDescription ?? "录屏帧写入失败。"; return
            }
            frames += 1
        }
    }

    func pause() { queue.sync { if !finished && pauseStart == nil { writeVideo(); pauseStart = CMClockGetTime(CMClockGetHostTimeClock()) } } }
    func resume() { queue.sync { if let started = pauseStart { pausedDuration = pausedDuration + CMClockGetTime(CMClockGetHostTimeClock()) - started; pauseStart = nil } } }
    func errorMessage() -> String? { queue.sync { failure } }

    func stop() async throws -> [String: Any] {
        if let stream = stream { try? await stream.stopCapture() }
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                self.timer?.cancel(); self.timer = nil
                if !self.finished { self.writeVideo() }
                self.finished = true; self.stream = nil; self.latest = nil
                if let failure = self.failure { self.writer.cancelWriting(); continuation.resume(throwing: mediaError(failure)); return }
                guard self.frames > 0 else { self.writer.cancelWriting(); continuation.resume(throwing: mediaError("未收到可用的录屏画面。")); return }
                self.video.markAsFinished(); self.audio?.markAsFinished()
                self.writer.finishWriting {
                    if self.writer.status == .completed {
                        continuation.resume(returning: ["filePath": self.path, "hasSystemAudio": self.audioEnabled,
                            "durationSeconds": Double(self.frames) / Double(self.fps)])
                    } else { continuation.resume(throwing: self.writer.error ?? mediaError("录屏文件未能完成。")) }
                }
            }
        }
    }

    func cancelWriter() {
        queue.sync {
            finished = true; timer?.cancel(); timer = nil; latest = nil
            if writer.status == .writing { writer.cancelWriting() }
        }
    }

    func cancel() async {
        cancelWriter()
        if let stream = stream { try? await stream.stopCapture() }
        queue.sync { stream = nil }
    }
}

private final class StartReply: @unchecked Sendable {
    let done = DispatchSemaphore(value: 0)
    let lock = NSLock()
    var recording: ScreenRecording?
    var error: String?
    var expired = false
}

@_cdecl("tbox_record_start")
public func recordStart(_ path: UnsafePointer<CChar>?, _ left: Double, _ top: Double, _ width: Double, _ height: Double,
                        _ pixelWidth: Int32, _ pixelHeight: Int32, _ fps: Int32, _ audio: Int32) -> UnsafeMutablePointer<CChar>? {
    guard let path = path, pixelWidth >= 16, pixelHeight >= 16, pixelWidth % 2 == 0, pixelHeight % 2 == 0,
          [5, 10, 15, 25, 60].contains(Int(fps)) else { return response(["error": "录屏参数无效。"]) }
    let destination = String(cString: path)
    let reply = StartReply()
    Task.detached {
        var recording: ScreenRecording?
        do {
            let created = try ScreenRecording(path: destination, width: Int(pixelWidth), height: Int(pixelHeight), fps: Int(fps), audioEnabled: audio != 0)
            recording = created
            try await created.start(region: CGRect(x: left, y: top, width: width, height: height), width: Int(pixelWidth), height: Int(pixelHeight))
            let expired = reply.lock.withLock {
                if !reply.expired { reply.recording = created }
                return reply.expired
            }
            if expired { await created.cancel() }
        } catch {
            if let recording = recording { await recording.cancel() }
            reply.lock.withLock { reply.error = error.localizedDescription }
        }
        reply.done.signal()
    }
    if reply.done.wait(timeout: .now() + 30) != .success {
        reply.lock.lock(); reply.expired = true; let late = reply.recording; reply.recording = nil; reply.lock.unlock()
        if let late = late { Task { await late.cancel() } }
        return response(["error": "开始录屏超时，请检查系统权限。"])
    }
    guard let recording = reply.recording else { return response(["error": reply.error ?? "开始录屏失败。"]) }
    let handle = Unmanaged.passRetained(recording).toOpaque()
    return response(["handle": UInt(bitPattern: handle)])
}

private func recorder(_ handle: UnsafeRawPointer) -> ScreenRecording { Unmanaged<ScreenRecording>.fromOpaque(handle).takeUnretainedValue() }
@_cdecl("tbox_record_pause") public func recordPause(_ handle: UnsafeRawPointer) { recorder(handle).pause() }
@_cdecl("tbox_record_resume") public func recordResume(_ handle: UnsafeRawPointer) { recorder(handle).resume() }
@_cdecl("tbox_record_status") public func recordStatus(_ handle: UnsafeRawPointer) -> UnsafeMutablePointer<CChar>? {
    response(["failure": recorder(handle).errorMessage() as Any? ?? NSNull()])
}

private final class StopReply: @unchecked Sendable { let done = DispatchSemaphore(value: 0); var result: [String: Any] = [:] }
@_cdecl("tbox_record_stop") public func recordStop(_ handle: UnsafeRawPointer) -> UnsafeMutablePointer<CChar>? {
    let recording = recorder(handle); let reply = StopReply()
    Task.detached {
        do { reply.result = try await recording.stop() }
        catch { reply.result = ["error": error.localizedDescription] }
        reply.done.signal()
    }
    guard reply.done.wait(timeout: .now() + 45) == .success else { return response(["error": "完成录屏超时。"] ) }
    return response(reply.result)
}
@_cdecl("tbox_record_dispose") public func recordDispose(_ handle: UnsafeRawPointer) {
    let recording = Unmanaged<ScreenRecording>.fromOpaque(handle).takeRetainedValue()
    // Close the file before returning ownership to the managed workspace cleanup.
    recording.cancelWriter()
    Task { await recording.cancel() }
}
