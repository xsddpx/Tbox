import ApplicationServices
import Carbon
import Foundation

enum ScreenshotShortcutEvent: Equatable {
    case pass, consume, activate
}

// Swallow the matching press and release, including repeats. A release can arrive after its modifiers.
struct ScreenshotShortcutFilter {
    private var pressed = false

    mutating func handle(_ type: CGEventType, key: Int64, flags: CGEventFlags, repeats: Bool) -> ScreenshotShortcutEvent {
        guard key == Int64(kVK_ANSI_3) else { return .pass }
        if type == .keyUp {
            let wasPressed = pressed
            pressed = false
            return wasPressed ? .consume : .pass
        }
        guard type == .keyDown else { return .pass }
        if repeats { return pressed ? .consume : .pass }
        let modifiers: CGEventFlags = [.maskCommand, .maskShift, .maskControl, .maskAlternate, .maskSecondaryFn]
        pressed = flags.intersection(modifiers) == [.maskCommand, .maskShift]
        return pressed ? .activate : .pass
    }
}

// Created, called and torn down on the main run loop. No system preferences are changed.
private var tap: CFMachPort?
private var source: CFRunLoopSource?
private var callback: (@convention(c) () -> Void)?
private var filter = ScreenshotShortcutFilter()

@_cdecl("tbox_start_screenshot_shortcut")
public func startScreenshotShortcut(_ onPressed: @escaping @convention(c) () -> Void, _ requestPermission: Int32) -> Int32 {
    if screenshotShortcutActive() != 0 { return 1 }
    stopScreenshotShortcut()
    if requestPermission != 0 {
        guard enableElements() != 0 else { return 0 }
    } else {
        guard AXIsProcessTrusted() else { return 0 }
    }

    let mask = (CGEventMask(1) << CGEventType.keyDown.rawValue) | (CGEventMask(1) << CGEventType.keyUp.rawValue)
    guard let port = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
        eventsOfInterest: mask, callback: { _, type, event, _ in
            if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
                filter = ScreenshotShortcutFilter()
                // A slow callback can disable the tap. Don't override an explicit disable by the system.
                if type == .tapDisabledByTimeout, let port = tap { CGEvent.tapEnable(tap: port, enable: true) }
                return Unmanaged.passUnretained(event)
            }
            switch filter.handle(type, key: event.getIntegerValueField(.keyboardEventKeycode), flags: event.flags,
                repeats: event.getIntegerValueField(.keyboardEventAutorepeat) != 0) {
            case .pass: return Unmanaged.passUnretained(event)
            case .consume: return nil
            case .activate:
                callback?()
                return nil
            }
        }, userInfo: nil) else { return 0 }
    guard let runLoopSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, port, 0) else {
        CFMachPortInvalidate(port)
        return 0
    }
    tap = port
    source = runLoopSource
    callback = onPressed
    CFRunLoopAddSource(CFRunLoopGetMain(), runLoopSource, .commonModes)
    CGEvent.tapEnable(tap: port, enable: true)
    if screenshotShortcutActive() != 0 { return 1 }
    stopScreenshotShortcut()
    return 0
}

@_cdecl("tbox_stop_screenshot_shortcut")
public func stopScreenshotShortcut() {
    if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
    if let tap {
        CGEvent.tapEnable(tap: tap, enable: false)
        CFMachPortInvalidate(tap)
    }
    source = nil
    tap = nil
    callback = nil
    filter = ScreenshotShortcutFilter()
}

@_cdecl("tbox_screenshot_shortcut_active")
public func screenshotShortcutActive() -> Int32 {
    guard let tap else { return 0 }
    return CGEvent.tapIsEnabled(tap: tap) ? 1 : 0
}
