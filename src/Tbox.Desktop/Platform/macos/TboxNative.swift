import AppKit
import Carbon
import ScreenCaptureKit
import Vision
import ApplicationServices

// Only capabilities that Avalonia cannot provide live here. All UI and product rules are C#.
func response(_ value: [String: Any]) -> UnsafeMutablePointer<CChar>? {
    guard let data = try? JSONSerialization.data(withJSONObject: value),
          let text = String(data: data, encoding: .utf8) else { return nil }
    return strdup(text)
}

@_cdecl("tbox_free")
public func freeResponse(_ pointer: UnsafeMutableRawPointer?) { free(pointer) }

@_cdecl("tbox_permission")
public func permission() -> Int32 { CGPreflightScreenCaptureAccess() ? 1 : 0 }

@_cdecl("tbox_request_permission")
public func requestPermission() -> Int32 { CGRequestScreenCaptureAccess() ? 1 : 0 }

@_cdecl("tbox_open_permission_settings")
public func openPermissionSettings() {
    guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") else { return }
    NSWorkspace.shared.open(url)
}

private final class CaptureResult: @unchecked Sendable {
    var frames: [(CGDirectDisplayID, CGRect, CGImage)] = []
    var error: String?
    let complete = DispatchSemaphore(value: 0)
}

public typealias CaptureFrameCallback = @convention(c) (UInt32, Double, Double, Double, Double, Int32, Int32, UnsafeRawPointer) -> Int32

// The callback only runs on the original worker after the asynchronous capture completes.
// A timed-out ScreenCaptureKit request never retains or invokes a managed callback.
@_cdecl("tbox_capture_pixels")
public func capturePixels(_ regionOnly: Int32, _ left: Double, _ top: Double,
                          _ width: Double, _ height: Double, _ receive: CaptureFrameCallback) -> UnsafeMutablePointer<CChar>? {
    guard CGPreflightScreenCaptureAccess() else {
        return strdup("请在系统设置中允许 Tbox 录制屏幕，然后重新打开应用。")
    }
    if regionOnly != 0 && (width <= 0 || height <= 0) { return strdup("捕获区域无效。") }
    let region = regionOnly == 0 ? nil : CGRect(x: left, y: top, width: width, height: height)
    let result = CaptureResult()
    Task.detached {
        defer { result.complete.signal() }
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            for display in content.displays {
                let bounds = CGDisplayBounds(display.displayID)
                if let region = region, !bounds.contains(region) { continue }
                let mode = CGDisplayCopyDisplayMode(display.displayID)
                let target = region ?? bounds
                let config = SCStreamConfiguration()
                config.width = max(1, Int((target.width / bounds.width * Double(mode?.pixelWidth ?? display.width)).rounded()))
                config.height = max(1, Int((target.height / bounds.height * Double(mode?.pixelHeight ?? display.height)).rounded()))
                if region != nil { config.sourceRect = CGRect(x: target.minX - bounds.minX, y: target.minY - bounds.minY, width: target.width, height: target.height) }
                config.showsCursor = false
                config.pixelFormat = kCVPixelFormatType_32BGRA
                config.colorSpaceName = CGColorSpace.sRGB
                let excluded = region == nil ? [] : content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
                let filter = SCContentFilter(display: display, excludingApplications: excluded, exceptingWindows: [])
                let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
                result.frames.append((display.displayID, target, image))
                if region != nil { break }
            }
        } catch {
            result.error = "截图失败：\(error.localizedDescription)"
        }
    }
    guard result.complete.wait(timeout: .now() + 30) == .success else { return strdup("截图超时，请重试。") }
    if let error = result.error { return strdup(error) }
    guard !result.frames.isEmpty else { return strdup("没有找到可截图的显示器。") }
    for (id, frame, image) in result.frames {
        let format = CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: image.width, height: image.height,
                                      bitsPerComponent: 8, bytesPerRow: image.width * 4,
                                      space: colorSpace, bitmapInfo: format),
              let pixels = context.data else { return strdup("无法分配截图缓冲区。") }
        let rect = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        context.setFillColor(gray: 0, alpha: 1)
        context.fill(rect)
        context.draw(image, in: rect)
        guard receive(id, frame.minX, frame.minY, frame.width, frame.height,
                      Int32(image.width), Int32(image.height), pixels) != 0 else { return strdup("截图像素读取中断。") }
    }
    return nil
}

@_cdecl("tbox_recognize")
public func recognize(_ bytes: UnsafeRawPointer?, _ length: Int32) -> UnsafeMutablePointer<CChar>? {
    guard let bytes = bytes, length > 0 else { return response(["error": "识别图像为空。"]) }
    return autoreleasepool {
        recognizeText(VNImageRequestHandler(data: Data(bytes: bytes, count: Int(length)), options: [:]))
    }
}

@_cdecl("tbox_recognize_pixels")
public func recognizePixels(_ bytes: UnsafeRawPointer?, _ length: Int32, _ width: Int32, _ height: Int32) -> UnsafeMutablePointer<CChar>? {
    guard let bytes, width > 0, height > 0, length > 0, length % 4 == 0,
          Int64(width) * Int64(height) == Int64(length) / 4 else { return response(["error": "识别图像尺寸无效。"]) }
    return autoreleasepool {
        let data = Data(bytes: bytes, count: Int(length)) as CFData
        // The managed boundary supplies top-down, opaque BGRA in sRGB. B is the low byte in little-endian xRGB.
        let layout = CGBitmapInfo.byteOrder32Little.union(CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipFirst.rawValue))
        guard let provider = CGDataProvider(data: data), let space = CGColorSpace(name: CGColorSpace.sRGB),
              let image = CGImage(width: Int(width), height: Int(height), bitsPerComponent: 8, bitsPerPixel: 32,
                  bytesPerRow: Int(width) * 4, space: space, bitmapInfo: layout, provider: provider,
                  decode: nil, shouldInterpolate: false, intent: .defaultIntent) else {
            return response(["error": "无法读取识别图像。"])
        }
        return recognizeText(VNImageRequestHandler(cgImage: image, options: [:]))
    }
}

private func recognizeText(_ handler: VNImageRequestHandler) -> UnsafeMutablePointer<CChar>? {
    do {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        let supported = try request.supportedRecognitionLanguages()
        request.recognitionLanguages = ["zh-Hans", "zh-Hant", "en-US"].filter { supported.contains($0) }
        try handler.perform([request])
        let lines: [[String: Any]] = (request.results ?? []).compactMap { observation in
            guard let candidate = observation.topCandidates(1).first else { return nil }
            let box = observation.boundingBox
            return ["text": candidate.string, "left": box.minX, "top": 1 - box.maxY,
                    "width": box.width, "height": box.height]
        }
        return response(["text": lines.compactMap { $0["text"] as? String }.joined(separator: "\n"), "lines": lines])
    } catch { return response(["error": "文字识别失败：\(error.localizedDescription)"]) }
}

private var hotkeyCallback: (@convention(c) (Int32) -> Void)?
private var hotkeys: [Int32: EventHotKeyRef] = [:]
private var eventHandler: EventHandlerRef?

@_cdecl("tbox_unregister_hotkeys")
public func unregisterHotkeys() {
    for key in hotkeys.values { UnregisterEventHotKey(key) }
    hotkeys.removeAll()
    if let handler = eventHandler { RemoveEventHandler(handler) }
    eventHandler = nil
    hotkeyCallback = nil
}

@_cdecl("tbox_unregister_hotkey")
public func unregisterHotkey(_ id: Int32) {
    if let key = hotkeys.removeValue(forKey: id) { UnregisterEventHotKey(key) }
}

// One shortcut per action id; registering an id again replaces its previous shortcut.
// keyCode is a kVK_* code and modifiers are Carbon flags (cmdKey, shiftKey, optionKey, controlKey).
@_cdecl("tbox_register_hotkey")
public func registerHotkey(_ callback: @escaping @convention(c) (Int32) -> Void, _ id: Int32, _ keyCode: Int32, _ modifiers: Int32) -> Int32 {
    hotkeyCallback = callback
    if eventHandler == nil {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var key = EventHotKeyID()
            let result = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                           nil, MemoryLayout<EventHotKeyID>.size, nil, &key)
            guard result == noErr, key.signature == 0x54424F58 else { return OSStatus(eventNotHandledErr) }
            hotkeyCallback?(Int32(key.id))
            return noErr
        }, 1, &spec, nil, &eventHandler)
        guard status == noErr else { eventHandler = nil; return status }
    }
    unregisterHotkey(id)
    var reference: EventHotKeyRef?
    // A shared Carbon registration can succeed without receiving events from an exclusively held shortcut.
    let result = RegisterEventHotKey(UInt32(keyCode), UInt32(modifiers), EventHotKeyID(signature: 0x54424F58, id: UInt32(id)),
                                     GetApplicationEventTarget(), OptionBits(kEventHotKeyExclusive), &reference)
    guard result == noErr, let reference = reference else { return result == noErr ? -1 : result }
    hotkeys[id] = reference
    return noErr
}

// Avalonia returns an NSWindow or NSView handle depending on the backend version.
private func window(_ pointer: UnsafeRawPointer?) -> NSWindow? {
    guard let pointer = pointer else { return nil }
    let object = Unmanaged<AnyObject>.fromOpaque(pointer).takeUnretainedValue()
    return (object as? NSWindow) ?? (object as? NSView)?.window
}

private func primaryHeight() -> CGFloat { CGDisplayBounds(CGMainDisplayID()).height }

@_cdecl("tbox_pointer_position")
public func pointerPosition() -> UnsafeMutablePointer<CChar>? {
    let point = NSEvent.mouseLocation
    return response(["x": point.x, "y": primaryHeight() - point.y])
}

@_cdecl("tbox_capture_targets")
public func captureTargets() -> UnsafeMutablePointer<CChar>? {
    let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
    let targets: [[String: Any]] = list.compactMap { item in
        guard let pid = item[kCGWindowOwnerPID as String] as? Int, pid != ProcessInfo.processInfo.processIdentifier,
              let layer = item[kCGWindowLayer as String] as? Int, layer == 0,
              let bounds = item[kCGWindowBounds as String] as? [String: Any],
              let rect = CGRect(dictionaryRepresentation: bounds as CFDictionary), rect.width > 1, rect.height > 1 else { return nil }
        return ["processId": pid, "left": Int(floor(rect.minX)), "top": Int(floor(rect.minY)),
                "width": Int(ceil(rect.width)), "height": Int(ceil(rect.height))]
    }
    return response(["targets": targets])
}

@_cdecl("tbox_enable_elements")
public func enableElements() -> Int32 {
    let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
    return AXIsProcessTrustedWithOptions(options) ? 1 : 0
}

@_cdecl("tbox_element_bounds")
public func elementBounds(_ processId: Int32, _ x: Double, _ y: Double) -> UnsafeMutablePointer<CChar>? {
    guard AXIsProcessTrusted() else { return response(["found": false]) }
    let application = AXUIElementCreateApplication(processId)
    AXUIElementSetMessagingTimeout(application, 0.15)
    var element: AXUIElement?
    guard AXUIElementCopyElementAtPosition(application, Float(x), Float(y), &element) == .success,
          let element = element else { return response(["found": false]) }
    AXUIElementSetMessagingTimeout(element, 0.15)
    var positionValue: CFTypeRef?, sizeValue: CFTypeRef?
    guard AXUIElementCopyAttributeValue(element, kAXPositionAttribute as CFString, &positionValue) == .success,
          AXUIElementCopyAttributeValue(element, kAXSizeAttribute as CFString, &sizeValue) == .success,
          let positionValue = positionValue, let sizeValue = sizeValue,
          CFGetTypeID(positionValue) == AXValueGetTypeID(), CFGetTypeID(sizeValue) == AXValueGetTypeID() else { return response(["found": false]) }
    var position = CGPoint.zero, size = CGSize.zero
    guard AXValueGetValue(positionValue as! AXValue, .cgPoint, &position),
          AXValueGetValue(sizeValue as! AXValue, .cgSize, &size), size.width > 1, size.height > 1,
          CGRect(origin: position, size: size).contains(CGPoint(x: x, y: y)) else { return response(["found": false]) }
    return response(["found": true, "left": Int(floor(position.x)), "top": Int(floor(position.y)),
                     "width": Int(ceil(size.width)), "height": Int(ceil(size.height))])
}

@_cdecl("tbox_place_window")
public func placeWindow(_ pointer: UnsafeRawPointer?, _ left: Double, _ top: Double,
                        _ width: Double, _ height: Double, _ overlay: Int32) {
    guard let target = window(pointer) else { return }
    target.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
    target.level = overlay != 0 ? .screenSaver : .floating
    target.hasShadow = overlay == 0
    target.setFrame(NSRect(x: left, y: primaryHeight() - top - height, width: width, height: height), display: true)
    if overlay != 0 { target.orderFrontRegardless() }
}

@_cdecl("tbox_capture_overlay")
public func captureOverlay(_ pointer: UnsafeRawPointer?, _ clickThrough: Int32) {
    guard let target = window(pointer) else { return }
    target.ignoresMouseEvents = clickThrough != 0
    target.hasShadow = false
    target.isOpaque = false
    target.backgroundColor = .clear
    // Region captures exclude this process via SCContentFilter, including controls and preview.
}

@_cdecl("tbox_capture_foreground")
public func captureForeground(_ dashboardHidden: Int32) -> Int32 {
    let own = ProcessInfo.processInfo.processIdentifier
    guard let front = NSWorkspace.shared.frontmostApplication else { return 0 }
    if front.processIdentifier != own { return front.processIdentifier }
    guard dashboardHidden != 0 else { return 0 }
    // Ordering out the dashboard can leave our windowless app active. WindowServer
    // returns the exposed windows front to back, including across separate Spaces.
    let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
    for info in windows {
        guard info[kCGWindowLayer as String] as? Int == 0,
              let pid = info[kCGWindowOwnerPID as String] as? Int32, pid != own,
              (info[kCGWindowAlpha as String] as? Double ?? 0) > 0,
              let app = NSRunningApplication(processIdentifier: pid), app.activationPolicy == .regular else { continue }
        return pid
    }
    return 0
}

@_cdecl("tbox_restore_capture_foreground")
public func restoreCaptureForeground(_ pid: Int32) {
    guard NSWorkspace.shared.frontmostApplication?.processIdentifier == ProcessInfo.processInfo.processIdentifier,
          let application = NSRunningApplication(processIdentifier: pid), !application.isTerminated else { return }
    _ = application.activate(options: [])
}

@_cdecl("tbox_move_to_pointer")
public func moveToPointer(_ pointer: UnsafeRawPointer?) {
    guard let target = window(pointer) else { return }
    let point = NSEvent.mouseLocation
    guard let screen = NSScreen.screens.first(where: { $0.frame.contains(point) }) else { return }
    let area = screen.visibleFrame
    let ratio = min(1, min(area.width / target.frame.width, area.height / target.frame.height))
    let size = NSSize(width: target.frame.width * ratio, height: target.frame.height * ratio)
    target.setFrame(NSRect(x: max(area.minX, min(point.x - size.width / 2, area.maxX - size.width)),
                           y: max(area.minY, min(point.y - size.height / 2, area.maxY - size.height)),
                           width: size.width, height: size.height), display: true)
    target.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
    target.level = .floating
}

@_cdecl("tbox_window_placement")
public func windowPlacement(_ pointer: UnsafeRawPointer?) -> UnsafeMutablePointer<CChar>? {
    guard let target = window(pointer) else { return response(["error": "窗口已关闭。"]) }
    let frame = target.frame
    return response(["left": frame.minX, "top": primaryHeight() - frame.maxY, "width": frame.width, "height": frame.height])
}

@_cdecl("tbox_work_area")
public func workArea(_ pointer: UnsafeRawPointer?) -> UnsafeMutablePointer<CChar>? {
    guard let screen = window(pointer)?.screen ?? NSScreen.main else { return response(["error": "显示器不可用。"]) }
    let frame = screen.visibleFrame
    return response(["left": frame.minX, "top": primaryHeight() - frame.maxY, "width": frame.width, "height": frame.height])
}

// Work area of the screen that holds most of a top-left based desktop rectangle.
@_cdecl("tbox_work_area_for")
public func workAreaFor(_ left: Double, _ top: Double, _ width: Double, _ height: Double) -> UnsafeMutablePointer<CChar>? {
    let region = NSRect(x: left, y: primaryHeight() - top - height, width: width, height: height)
    func overlap(_ screen: NSScreen) -> CGFloat { let i = screen.frame.intersection(region); return i.isNull ? 0 : i.width * i.height }
    guard let screen = NSScreen.screens.max(by: { overlap($0) < overlap($1) }) ?? NSScreen.main else { return response(["error": "显示器不可用。"]) }
    let frame = screen.visibleFrame
    return response(["left": frame.minX, "top": primaryHeight() - frame.maxY, "width": frame.width, "height": frame.height])
}

@_cdecl("tbox_display_top_safe_inset")
public func displayTopSafeInset(_ displayID: UInt32) -> Double {
    let screenNumber = NSDeviceDescriptionKey("NSScreenNumber")
    let screen = NSScreen.screens.first { ($0.deviceDescription[screenNumber] as? NSNumber)?.uint32Value == displayID }
    return Double(screen?.safeAreaInsets.top ?? 0)
}
