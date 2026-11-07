import AppKit
import Carbon
import ScreenCaptureKit
import Vision
import ApplicationServices

// Only capabilities that Avalonia cannot provide live here. All UI and product rules are C#.
private func response(_ value: [String: Any]) -> UnsafeMutablePointer<CChar>? {
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

private final class CaptureResult: @unchecked Sendable {
    var value: [String: Any] = [:]
    let complete = DispatchSemaphore(value: 0)
}

// Called from a .NET worker, never the Cocoa main thread. Full displays remain in memory only.
@_cdecl("tbox_capture")
public func capture() -> UnsafeMutablePointer<CChar>? {
    guard CGPreflightScreenCaptureAccess() else {
        return response(["error": "请在系统设置 → 隐私与安全性 → 屏幕与系统音频录制中允许 Tbox，然后重新打开应用。"])
    }
    let result = CaptureResult()
    Task.detached {
        defer { result.complete.signal() }
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            var displays: [[String: Any]] = []
            for display in content.displays {
                let bounds = CGDisplayBounds(display.displayID)
                let mode = CGDisplayCopyDisplayMode(display.displayID)
                let config = SCStreamConfiguration()
                config.width = mode?.pixelWidth ?? display.width
                config.height = mode?.pixelHeight ?? display.height
                config.showsCursor = false
                config.pixelFormat = kCVPixelFormatType_32BGRA
                config.colorSpaceName = CGColorSpace.sRGB
                let filter = SCContentFilter(display: display, excludingWindows: [])
                let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
                guard let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
                    throw NSError(domain: "Tbox", code: 1, userInfo: [NSLocalizedDescriptionKey: "无法编码屏幕图像。"])
                }
                displays.append(["id": display.displayID, "left": bounds.minX, "top": bounds.minY,
                                 "width": bounds.width, "height": bounds.height, "png": png.base64EncodedString()])
            }
            result.value = displays.isEmpty ? ["error": "没有找到可截图的显示器。"] : ["displays": displays]
        } catch {
            result.value = ["error": "截图失败：\(error.localizedDescription)"]
        }
    }
    guard result.complete.wait(timeout: .now() + 30) == .success else {
        return response(["error": "截图超时，请检查屏幕录制权限后重试。"])
    }
    return response(result.value)
}

@_cdecl("tbox_recognize")
public func recognize(_ bytes: UnsafeRawPointer?, _ length: Int32) -> UnsafeMutablePointer<CChar>? {
    guard let bytes = bytes, length > 0 else { return response(["error": "识别图像为空。"]) }
    return autoreleasepool {
        do {
            let data = Data(bytes: bytes, count: Int(length))
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = true
            let supported = try request.supportedRecognitionLanguages()
            // Chinese + English is supported as one offline recognition request.
            request.recognitionLanguages = ["zh-Hans", "zh-Hant", "en-US"].filter { supported.contains($0) }
            let handler = VNImageRequestHandler(data: data, options: [:])
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
}

private var hotkeyCallback: (@convention(c) (Int32) -> Void)?
private var hotkeys: [EventHotKeyRef] = []
private var eventHandler: EventHandlerRef?

@_cdecl("tbox_unregister_hotkeys")
public func unregisterHotkeys() {
    for key in hotkeys { UnregisterEventHotKey(key) }
    hotkeys.removeAll()
    if let handler = eventHandler { RemoveEventHandler(handler) }
    eventHandler = nil
    hotkeyCallback = nil
}

@_cdecl("tbox_register_hotkeys")
public func registerHotkeys(_ callback: @escaping @convention(c) (Int32) -> Void) -> Int32 {
    unregisterHotkeys()
    hotkeyCallback = callback
    var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
    let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
        var key = EventHotKeyID()
        let result = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                       nil, MemoryLayout<EventHotKeyID>.size, nil, &key)
        if result == noErr { hotkeyCallback?(Int32(key.id)) }
        return noErr
    }, 1, &spec, nil, &eventHandler)
    guard status == noErr else { unregisterHotkeys(); return status }
    for (index, keyCode) in [kVK_ANSI_1, kVK_ANSI_2, kVK_ANSI_3].enumerated() {
        var reference: EventHotKeyRef?
        let id = EventHotKeyID(signature: 0x54424F58, id: UInt32(index + 1))
        let result = RegisterEventHotKey(UInt32(keyCode), UInt32(controlKey | shiftKey), id,
                                         GetApplicationEventTarget(), 0, &reference)
        guard result == noErr, let reference = reference else { unregisterHotkeys(); return result == noErr ? -1 : result }
        hotkeys.append(reference)
    }
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

@_cdecl("tbox_move_to_pointer")
public func moveToPointer(_ pointer: UnsafeRawPointer?) {
    guard let target = window(pointer) else { return }
    let point = NSEvent.mouseLocation
    guard let screen = NSScreen.screens.first(where: { $0.frame.contains(point) }) else { return }
    let area = screen.visibleFrame
    let size = NSSize(width: min(target.frame.width, area.width), height: min(target.frame.height, area.height))
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
