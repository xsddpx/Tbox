import AppKit

// Menu bar entry for the tray panel. Avalonia's macOS tray icon only opens a menu and never reports
// clicks, so Tbox owns this status item: left click opens the panel, right or Control-click the menu.
private final class StatusItemTarget: NSObject {
    let callback: @convention(c) (Int32) -> Void
    init(_ callback: @escaping @convention(c) (Int32) -> Void) { self.callback = callback }

    @objc func clicked(_ sender: Any?) {
        let event = NSApp.currentEvent
        let secondary = event?.type == .rightMouseUp || event?.modifierFlags.contains(.control) == true
        if secondary, let item = statusItem, let menu = statusMenu {
            item.menu = menu
            item.button?.performClick(nil)
            item.menu = nil
        } else {
            callback(0)
        }
    }

    @objc func choose(_ sender: NSMenuItem) { callback(Int32(sender.tag)) }
}

private var statusItem: NSStatusItem?
private var statusMenu: NSMenu?
private var statusTarget: StatusItemTarget?

// `titles` is newline-separated; an empty line is a separator. Menu choices report 1...n.
@_cdecl("tbox_status_item_create")
public func statusItemCreate(_ png: UnsafeRawPointer?, _ length: Int32, _ tooltip: UnsafePointer<CChar>?,
                             _ titles: UnsafePointer<CChar>?, _ callback: @escaping @convention(c) (Int32) -> Void) -> Int32 {
    statusItemRemove()
    let target = StatusItemTarget(callback)
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    guard let button = item.button else { NSStatusBar.system.removeStatusItem(item); return 0 }
    if let png = png, length > 0, let image = NSImage(data: Data(bytes: png, count: Int(length))) {
        let height = floor(NSFont.menuFont(ofSize: 0).pointSize * 1.333333)
        image.size = NSSize(width: floor(image.size.width * height / image.size.height), height: height)
        image.isTemplate = true
        button.image = image
    }
    if let tooltip = tooltip { button.toolTip = String(cString: tooltip) }
    button.target = target
    button.action = #selector(StatusItemTarget.clicked(_:))
    button.sendAction(on: [.leftMouseUp, .rightMouseUp])

    let menu = NSMenu()
    var tag = 0
    for title in (titles.map { String(cString: $0) } ?? "").components(separatedBy: "\n") {
        if title.isEmpty { menu.addItem(.separator()); continue }
        tag += 1
        let entry = NSMenuItem(title: title, action: #selector(StatusItemTarget.choose(_:)), keyEquivalent: "")
        entry.target = target
        entry.tag = tag
        menu.addItem(entry)
    }
    statusItem = item; statusMenu = menu; statusTarget = target
    return 1
}

// Retitles menu choice `tag` (1...n), e.g. after a shortcut changes.
@_cdecl("tbox_status_item_retitle")
public func statusItemRetitle(_ tag: Int32, _ title: UnsafePointer<CChar>?) {
    guard let title = title, let entry = statusMenu?.item(withTag: Int(tag)) else { return }
    entry.title = String(cString: title)
}

@_cdecl("tbox_status_item_remove")
public func statusItemRemove() {
    if let item = statusItem { NSStatusBar.system.removeStatusItem(item) }
    statusItem = nil; statusMenu = nil; statusTarget = nil
}

// Top-left desktop coordinates of the status item, so the panel opens below the icon rather than the pointer.
@_cdecl("tbox_status_item_anchor")
public func statusItemAnchor() -> UnsafeMutablePointer<CChar>? {
    guard let button = statusItem?.button, let window = button.window else { return response(["error": "菜单栏图标不可用。"]) }
    let frame = window.convertToScreen(button.convert(button.bounds, to: nil))
    let primary = CGDisplayBounds(CGMainDisplayID()).height
    return response(["x": frame.midX, "y": primary - frame.minY])
}
