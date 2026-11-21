import AppKit
import AVFoundation
import ScreenCaptureKit

private func topmostAttribute(_ element: AXUIElement, _ key: String) -> CFTypeRef? {
    var value: CFTypeRef?
    guard AXUIElementCopyAttributeValue(element, key as CFString, &value) == .success else { return nil }
    return value
}

private struct TopmostSource {
    let application: NSRunningApplication
    let element: AXUIElement
    let frame: CGRect
    let title: String

    static func focused() -> TopmostSource? {
        guard let application = NSWorkspace.shared.frontmostApplication,
              application.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return nil }
        let ax = AXUIElementCreateApplication(application.processIdentifier)
        AXUIElementSetMessagingTimeout(ax, 0.15)
        guard let focused = topmostAttribute(ax, kAXFocusedWindowAttribute),
              CFGetTypeID(focused) == AXUIElementGetTypeID() else { return nil }
        let element = focused as! AXUIElement
        AXUIElementSetMessagingTimeout(element, 0.15)
        guard let position = topmostAttribute(element, kAXPositionAttribute), CFGetTypeID(position) == AXValueGetTypeID(),
              let size = topmostAttribute(element, kAXSizeAttribute), CFGetTypeID(size) == AXValueGetTypeID() else { return nil }
        var origin = CGPoint.zero
        var dimensions = CGSize.zero
        guard AXValueGetValue(position as! AXValue, .cgPoint, &origin), AXValueGetValue(size as! AXValue, .cgSize, &dimensions),
              dimensions.width >= 1, dimensions.height >= 1 else { return nil }
        return TopmostSource(application: application, element: element, frame: CGRect(origin: origin, size: dimensions),
            title: topmostAttribute(element, kAXTitleAttribute) as? String ?? "")
    }

    func captureWindow(in windows: [SCWindow]) -> SCWindow? {
        // AX and ScreenCaptureKit share the desktop origin. Require a unique bounds match,
        // using the title only to disambiguate overlapping windows owned by the same app.
        let matches = windows.filter { window in
            window.owningApplication?.processID == application.processIdentifier && window.windowLayer == 0 &&
            abs(window.frame.minX - frame.minX) <= 2 && abs(window.frame.minY - frame.minY) <= 2 &&
            abs(window.frame.width - frame.width) <= 2 && abs(window.frame.height - frame.height) <= 2
        }
        if matches.count == 1 { return matches.first }
        let named = matches.filter { !title.isEmpty && $0.title == title }
        return named.count == 1 ? named.first : nil
    }

    func focus() -> Bool {
        guard !application.isTerminated, AXIsProcessTrusted(),
              AXUIElementPerformAction(element, kAXRaiseAction as CFString) == .success else { return false }
        return application.isActive || application.activate(options: [])
    }
}

@MainActor
private final class WindowMirror: NSObject, SCStreamOutput, SCStreamDelegate, NSWindowDelegate {
    let source: TopmostSource
    let id: CGWindowID
    let title: String
    let deadline = ProcessInfo.processInfo.systemUptime + 6
    private let panel: NSPanel
    private let video = AVSampleBufferDisplayLayer()
    private let changed: (WindowMirror, String?) -> Void
    private let remove: (WindowMirror, String?) -> Void
    private var stream: SCStream?
    private(set) var shown = false

    init(source: TopmostSource, window: SCWindow, offset: Int, border: Bool,
         changed: @escaping (WindowMirror, String?) -> Void, remove: @escaping (WindowMirror, String?) -> Void) {
        self.source = source
        self.id = window.windowID
        self.title = source.title.isEmpty ? (source.application.localizedName ?? "未命名窗口") : source.title
        self.changed = changed
        self.remove = remove

        let scale = min(1, 440 / window.frame.width, 300 / window.frame.height)
        let size = CGSize(width: max(160, window.frame.width * scale), height: max(80, window.frame.height * scale))
        let referenceTop = NSScreen.screens.first?.frame.maxY ?? 0
        let center = CGPoint(x: window.frame.midX, y: referenceTop - window.frame.midY)
        let screen = NSScreen.screens.first { $0.frame.contains(center) } ?? NSScreen.main
        let area = screen?.visibleFrame ?? CGRect(x: 0, y: 0, width: 800, height: 600)
        let inset = CGFloat(18 + (offset % 6) * 24)
        let rectangle = CGRect(x: max(area.minX, area.maxX - size.width - inset),
            y: max(area.minY, area.maxY - size.height - 30 - inset), width: size.width, height: size.height)
        panel = NSPanel(contentRect: rectangle, styleMask: [.titled, .closable, .resizable, .utilityWindow, .nonactivatingPanel],
            backing: .buffered, defer: false)
        super.init()
        panel.title = "实时浮窗 · \(title)"
        panel.isReleasedWhenClosed = false
        panel.level = .floating
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        panel.contentMinSize = CGSize(width: 160, height: 80)
        panel.delegate = self

        let body = NSView(frame: CGRect(origin: .zero, size: size))
        body.wantsLayer = true
        video.frame = body.bounds
        video.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        video.videoGravity = .resizeAspect
        body.layer?.backgroundColor = NSColor.black.cgColor
        body.layer?.addSublayer(video)
        body.layer?.borderColor = NSColor(srgbRed: 109 / 255, green: 74 / 255, blue: 232 / 255, alpha: 1).cgColor
        let button = NSButton(frame: body.bounds)
        button.autoresizingMask = [.width, .height]
        button.title = ""
        button.isBordered = false
        button.target = self
        button.action = #selector(focusSource)
        button.setAccessibilityLabel("回到原窗口 · \(title)")
        button.toolTip = "回到原窗口"
        body.addSubview(button)
        panel.contentView = body
        setBorder(border)
    }

    func start(_ window: SCWindow) async throws {
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let config = SCStreamConfiguration()
        let scale = min(CGFloat(filter.pointPixelScale), 1280 / window.frame.width, 900 / window.frame.height)
        config.width = max(1, Int(window.frame.width * scale))
        config.height = max(1, Int(window.frame.height * scale))
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.minimumFrameInterval = CMTime(value: 1, timescale: 20)
        config.queueDepth = 3
        config.showsCursor = false
        config.ignoreShadowsSingleWindow = true
        config.ignoreGlobalClipSingleWindow = true
        let capture = SCStream(filter: filter, configuration: config, delegate: self)
        try capture.addStreamOutput(self, type: .screen, sampleHandlerQueue: .main)
        stream = capture
        try await capture.startCapture()
        if stream !== capture { try? await capture.stopCapture() }
    }

    func setBorder(_ visible: Bool) { panel.contentView?.layer?.borderWidth = visible ? 3 : 0 }

    @objc private func focusSource() {
        if !source.focus() { changed(self, "无法回到原窗口，请检查窗口是否仍打开及辅助功能权限。") }
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool { remove(self, nil); return false }

    nonisolated func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor [weak self] in
            guard let self, self.stream === stream else { return }
            self.remove(self, self.source.application.isTerminated ? nil : "窗口画面已停止共享。")
        }
    }

    nonisolated func stream(_ stream: SCStream, didOutputSampleBuffer sample: CMSampleBuffer, of type: SCStreamOutputType) {
        MainActor.assumeIsolated {
            guard self.stream === stream, type == .screen, sample.isValid,
                  let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
                  let status = attachments.first?[.status] as? Int else { return }
            let renderer = video.sampleBufferRenderer
            if status == SCFrameStatus.blank.rawValue || status == SCFrameStatus.suspended.rawValue {
                renderer.flush(removingDisplayedImage: true, completionHandler: nil)
                return
            }
            guard status == SCFrameStatus.complete.rawValue, sample.imageBuffer != nil else { return }
            if renderer.requiresFlushToResumeDecoding { renderer.flush() }
            if renderer.isReadyForMoreMediaData { renderer.enqueue(sample) }
            guard !shown else { return }
            shown = true
            panel.orderFrontRegardless()
            changed(self, nil)
        }
    }

    func stop() {
        let capture = stream
        stream = nil
        panel.delegate = nil
        panel.close()
        video.sampleBufferRenderer.flush(removingDisplayedImage: true, completionHandler: nil)
        if let capture { Task { try? await capture.stopCapture() } }
    }
}

public typealias TopmostChange = @convention(c) (UnsafePointer<CChar>?) -> Void

@MainActor
private final class WindowTopmost {
    static let shared = WindowTopmost()
    var callback: TopmostChange?
    private var border = true
    private var pending: [UUID: TopmostSource] = [:]
    private var mirrors: [CGWindowID: WindowMirror] = [:]
    private var timer: Timer?

    var windows: [[String: Any]] {
        mirrors.values.filter(\.shown).sorted { $0.id < $1.id }.map { ["id": $0.id, "title": $0.title] }
    }

    func toggle() {
        guard AXIsProcessTrusted(), CGPreflightScreenCaptureAccess() else {
            notify("请在 Tbox 的「窗口置顶」页授权屏幕录制及辅助功能。"); return
        }
        guard let source = TopmostSource.focused() else { notify("请先选中要置顶的应用窗口。"); return }
        if let existing = mirrors.values.first(where: { CFEqual($0.source.element, source.element) }) { remove(existing); return }
        if let ticket = pending.first(where: { CFEqual($0.value.element, source.element) })?.key { pending.removeValue(forKey: ticket); return }

        let ticket = UUID()
        pending[ticket] = source
        Task { [self] in
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
                guard pending.removeValue(forKey: ticket) != nil else { return }
                guard let window = source.captureWindow(in: content.windows) else { notify("没有找到可共享的当前窗口。"); return }
                guard mirrors[window.windowID] == nil else { return }
                let mirror = WindowMirror(source: source, window: window, offset: mirrors.count, border: border,
                    changed: { [weak self] mirror, message in
                        if self?.mirrors[mirror.id] === mirror { self?.notify(message) }
                    }, remove: { [weak self] mirror, message in self?.remove(mirror, error: message) })
                mirrors[window.windowID] = mirror
                startTimer()
                do { try await mirror.start(window) }
                catch { remove(mirror, error: "无法共享当前窗口的画面。") }
            } catch {
                if pending.removeValue(forKey: ticket) != nil { notify("无法读取可共享的窗口。") }
            }
        }
    }

    func setBorder(_ visible: Bool) {
        border = visible
        for mirror in mirrors.values { mirror.setBorder(visible) }
    }

    func remove(_ id: CGWindowID) { if let mirror = mirrors[id] { remove(mirror) } }

    private func remove(_ mirror: WindowMirror, error: String? = nil) {
        guard mirrors[mirror.id] === mirror else { return }
        mirrors.removeValue(forKey: mirror.id)
        mirror.stop()
        if mirrors.isEmpty { timer?.invalidate(); timer = nil }
        if mirror.shown || error != nil { notify(error) }
    }

    func clear() {
        pending.removeAll()
        let previous = mirrors
        mirrors.removeAll()
        timer?.invalidate(); timer = nil
        for mirror in previous.values { mirror.stop() }
        if !previous.isEmpty { notify(nil) }
    }

    private func startTimer() {
        guard timer == nil else { return }
        let scan = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.prune() }
        }
        timer = scan
        RunLoop.main.add(scan, forMode: .common)
    }

    private func prune() {
        let windows = CGWindowListCopyWindowInfo(.optionAll, kCGNullWindowID) as? [[String: Any]] ?? []
        let current = Dictionary(windows.compactMap { window -> (CGWindowID, pid_t)? in
            guard let id = window[kCGWindowNumber as String] as? CGWindowID,
                  let pid = window[kCGWindowOwnerPID as String] as? pid_t else { return nil }
            return (id, pid)
        }, uniquingKeysWith: { first, _ in first })
        for mirror in Array(mirrors.values) {
            if current[mirror.id] != mirror.source.application.processIdentifier { remove(mirror) }
            else if !mirror.shown, ProcessInfo.processInfo.systemUptime > mirror.deadline {
                remove(mirror, error: "没有收到可显示的窗口画面。")
            }
        }
    }

    private func notify(_ error: String?) {
        if let error { error.withCString { callback?($0) } }
        else { callback?(nil) }
    }
}

@MainActor @_cdecl("tbox_topmost_watch") public func topmostWatch(_ changed: TopmostChange?) { WindowTopmost.shared.callback = changed }
@MainActor @_cdecl("tbox_topmost_windows") public func topmostWindows() -> UnsafeMutablePointer<CChar>? { response(["windows": WindowTopmost.shared.windows]) }
@MainActor @_cdecl("tbox_topmost_toggle") public func topmostToggle() { WindowTopmost.shared.toggle() }
@MainActor @_cdecl("tbox_topmost_border") public func topmostBorder(_ visible: Int32) { WindowTopmost.shared.setBorder(visible != 0) }
@MainActor @_cdecl("tbox_topmost_remove") public func topmostRemove(_ id: UInt32) { WindowTopmost.shared.remove(id) }
@MainActor @_cdecl("tbox_topmost_clear") public func topmostClear() { WindowTopmost.shared.clear() }
