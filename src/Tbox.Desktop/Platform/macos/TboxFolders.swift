import AppKit
import Carbon

private func onFolderMainThread<T>(_ work: () -> T) -> T {
    if Thread.isMainThread { return work() }
    return DispatchQueue.main.sync(execute: work)
}

private func withFolderAXOwner<T>(_ element: AXUIElement, _ work: () -> T) -> T {
    // Self-owned AX elements invoke AppKit in this process, on the caller's thread.
    if elementProcess(element) == ProcessInfo.processInfo.processIdentifier { return onFolderMainThread(work) }
    return work()
}

private func folderAttribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    withFolderAXOwner(element) {
        var value: CFTypeRef?
        AXUIElementSetMessagingTimeout(element, 0.12)
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else { return nil }
        return value
    }
}

private func folderElement(_ value: CFTypeRef?) -> AXUIElement? {
    guard let value = value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
    return (value as! AXUIElement)
}

private func filePath(_ value: CFTypeRef?) -> String? {
    let url = (value as? URL) ?? (value as? String).flatMap(URL.init(string:))
    guard let url, url.isFileURL else { return nil }
    return (url as NSURL).filePathURL?.standardizedFileURL.path
}

private enum FinderDirectory: Equatable {
    case path(String)
    case virtualLocation
    case unavailable
}

func finderFolderFromItems(_ itemPaths: [String], title: String) -> String? {
    let parents = Set(itemPaths.map { URL(fileURLWithPath: $0).deletingLastPathComponent().standardizedFileURL })
    guard parents.count == 1, let parent = parents.first else { return nil }
    let localizedName = (try? parent.resourceValues(forKeys: [.localizedNameKey]))?.localizedName
    guard title == parent.lastPathComponent || title == localizedName else { return nil }
    return parent.path
}

private func readFinderDirectory(_ window: AXUIElement, title: String) -> FinderDirectory {
    var document: CFTypeRef?
    let result = AXUIElementCopyAttributeValue(window, kAXDocumentAttribute as CFString, &document)
    guard result == .success || result == .attributeUnsupported || result == .noValue else { return .unavailable }
    if let path = filePath(document) { return .path(path) }

    // Finder may omit AXDocument for a real directory. A folder proxy in
    // the title and file URLs in the active content pane provide independent
    // evidence. Never derive a path from the title or a sidebar selection.
    guard folderElement(folderAttribute(window, "AXProxy")) != nil else { return .virtualLocation }
    let layoutRoles = [kAXWindowRole, kAXSplitGroupRole, kAXScrollAreaRole, kAXGroupRole, kAXTabGroupRole]
    let layout = boundedAXTree(window, maximum: 32, descend: {
        guard let role = folderAttribute($0, kAXRoleAttribute) as? String else { return false }
        return layoutRoles.contains(role)
    })
    guard let content = layout.first(where: {
        let id = folderAttribute($0, kAXIdentifierAttribute) as? String
        return id == "IconView" || id == "ListView"
    }) else { return .unavailable }
    let itemPaths = boundedAXTree(content, maximum: 48, width: 8).compactMap { filePath(folderAttribute($0, "AXURL")) }
    return finderFolderFromItems(itemPaths, title: title).map(FinderDirectory.path) ?? .unavailable
}

private let finderLock = NSLock()
private var lastFinderWindow: AXUIElement?

@_cdecl("tbox_folder_permission") public func folderPermission() -> Int32 { AXIsProcessTrusted() ? 1 : 0 }

@_cdecl("tbox_finder_directory")
public func finderDirectory(_ refresh: Int32) -> UnsafeMutablePointer<CChar>? {
    guard AXIsProcessTrusted() else { return response(["observed": false]) }
    return finderLock.withLock {
        guard let finder = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.finder").first else {
            lastFinderWindow = nil; return response(["observed": false])
        }
        let front = NSWorkspace.shared.frontmostApplication?.bundleIdentifier == "com.apple.finder"
        if front || refresh != 0 {
            let application = AXUIElementCreateApplication(finder.processIdentifier)
            // Finder may expose only its main window after another app takes
            // focus. That is its last active window, not an arbitrary AXWindows
            // entry (which could point at a different Finder tab or window).
            if let window = folderElement(folderAttribute(application, kAXFocusedWindowAttribute)) ??
                folderElement(folderAttribute(application, kAXMainWindowAttribute)) { lastFinderWindow = window }
        }
        guard front || refresh != 0, let window = lastFinderWindow else { return response(["observed": false]) }
        for _ in 0..<3 {
            if let title = folderAttribute(window, kAXTitleAttribute) as? String {
                let directory = readFinderDirectory(window, title: title)
                // A tab switch can change the path while keeping the title.
                if directory == readFinderDirectory(window, title: title),
                   title == folderAttribute(window, kAXTitleAttribute) as? String {
                    switch directory {
                    case .path(let path): return response(["observed": true, "path": path])
                    case .virtualLocation: return response(["observed": true, "path": NSNull()])
                    case .unavailable: break
                    }
                }
            }
            Thread.sleep(forTimeInterval: 0.025)
        }
        return response(["observed": false])
    }
}

@_cdecl("tbox_finder_open_folders")
public func finderOpenFolders() -> UnsafeMutablePointer<CChar>? {
    guard AXIsProcessTrusted(),
          let finder = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.finder").first else {
        return response(["paths": []])
    }
    let application = AXUIElementCreateApplication(finder.processIdentifier)
    let windows = folderAttribute(application, kAXWindowsAttribute) as? [AXUIElement] ?? []
    var paths: [String] = []
    let deadline = Date().addingTimeInterval(1)
    for window in windows.prefix(32) {
        guard Date() < deadline else { break }
        guard let title = folderAttribute(window, kAXTitleAttribute) as? String,
              case .path(let path) = readFinderDirectory(window, title: title),
              title == folderAttribute(window, kAXTitleAttribute) as? String,
              readFinderDirectory(window, title: title) == .path(path) else { continue }
        paths.append(path)
    }
    return response(["paths": paths])
}

private struct StandardPanel {
    let element: AXUIElement
    let host: pid_t
    let service: pid_t
}

private func elementProcess(_ element: AXUIElement) -> pid_t? {
    var pid: pid_t = 0
    return AXUIElementGetPid(element, &pid) == .success ? pid : nil
}

private func isPanelService(_ pid: pid_t) -> Bool {
    guard let app = NSRunningApplication(processIdentifier: pid),
          app.bundleIdentifier == "com.apple.appkit.xpc.openAndSavePanelService",
          let executable = app.executableURL else { return false }
    return executable.path.hasPrefix("/System/Library/Frameworks/AppKit.framework/")
}

private func children(_ element: AXUIElement) -> [AXUIElement] {
    (folderAttribute(element, kAXChildrenAttribute) as? [AXUIElement]) ?? []
}

private func boundedAXTree(_ root: AXUIElement, maximum: Int, width: Int = 32,
    descend: (AXUIElement) -> Bool = { _ in true }) -> [AXUIElement] {
    let deadline = Date().addingTimeInterval(0.35)
    var queue = [root]
    var result: [AXUIElement] = []
    while !queue.isEmpty && result.count < maximum && Date() < deadline {
        let next = queue.removeFirst()
        if result.contains(where: { CFEqual($0, next) }) { continue }
        result.append(next)
        if descend(next) { queue.append(contentsOf: children(next).prefix(width)) }
    }
    return result
}

private func hasIdentity(_ element: AXUIElement, role: String, identifier: String) -> Bool {
    folderAttribute(element, kAXRoleAttribute) as? String == role &&
        folderAttribute(element, kAXIdentifierAttribute) as? String == identifier
}

// Recent AppKit versions expose the panel as a host-owned proxy. Only a
// descendant belonging to the real Open/Save service can authenticate it.
func standardFolderPanelService(role: String?, identifier: String?, owner: pid_t, host: pid_t,
    isService: (pid_t) -> Bool, serviceChild: () -> pid_t?) -> pid_t? {
    let namedPanel = (role == kAXWindowRole || role == kAXSheetRole) &&
        (identifier == "open-panel" || identifier == "save-panel")
    if isService(owner) {
        let systemWindow = role == kAXWindowRole && (identifier == "OpenPanel" || identifier == "SavePanel")
        return namedPanel || systemWindow ? owner : nil
    }
    guard owner == host, namedPanel, let service = serviceChild(), isService(service) else { return nil }
    return service
}

private func panelRoot(_ element: AXUIElement, host: pid_t) -> StandardPanel? {
    var current: AXUIElement? = element
    for _ in 0..<12 {
        guard let node = current, let owner = elementProcess(node), owner == host || isPanelService(owner) else { break }
        let service = standardFolderPanelService(role: folderAttribute(node, kAXRoleAttribute) as? String,
            identifier: folderAttribute(node, kAXIdentifierAttribute) as? String, owner: owner, host: host,
            isService: isPanelService, serviceChild: {
                boundedAXTree(node, maximum: 32).dropFirst().lazy.compactMap(elementProcess).first(where: isPanelService)
            })
        if let service { return StandardPanel(element: node, host: host, service: service) }
        current = folderElement(folderAttribute(node, kAXParentAttribute))
    }
    return nil
}

private func appElement(_ host: pid_t, _ attribute: String) -> AXUIElement? {
    folderElement(folderAttribute(AXUIElementCreateApplication(host), attribute))
}

func shouldInspectFolderPanel(host: pid_t, ownProcess: pid_t, hasLocalFilePanel: () -> Bool) -> Bool {
    host != ownProcess || hasLocalFilePanel()
}

private func hasLocalFilePanel() -> Bool {
    onFolderMainThread {
        guard let window = NSApp?.keyWindow else { return false }
        return window is NSSavePanel || window.attachedSheet is NSSavePanel
    }
}

private func focusedStandardPanel() -> StandardPanel? {
    guard let front = NSWorkspace.shared.frontmostApplication,
          !["com.apple.SecurityAgent", "com.apple.LocalAuthentication.UIAgent", "com.apple.LocalAuthenticationRemoteService"].contains(front.bundleIdentifier ?? "") else { return nil }
    let host = front.processIdentifier
    // Polling the app's regular Avalonia windows is unnecessary and can invoke a stale automation peer.
    guard shouldInspectFolderPanel(host: host, ownProcess: ProcessInfo.processInfo.processIdentifier,
        hasLocalFilePanel: hasLocalFilePanel) else { return nil }
    for attribute in [kAXFocusedWindowAttribute, kAXFocusedUIElementAttribute] {
        if let focused = appElement(host, attribute), let panel = panelRoot(focused, host: host) { return panel }
    }
    let system = AXUIElementCreateSystemWide()
    if let focused = folderElement(folderAttribute(system, kAXFocusedUIElementAttribute)) {
        return panelRoot(focused, host: host)
    }
    return nil
}

private func stillFocused(_ target: StandardPanel) -> Bool {
    guard let now = focusedStandardPanel() else { return false }
    return now.host == target.host && now.service == target.service && CFEqual(now.element, target.element) &&
        NSWorkspace.shared.frontmostApplication?.processIdentifier == target.host
}

private let retainedPanelLock = NSLock()
private var retainedPanel: StandardPanel?
private var retainedPanelID: Int64 = 0

private func folderBarIsKey(_ handle: UnsafeRawPointer?) -> Bool {
    guard let handle else { return false }
    return onFolderMainThread {
        guard NSRunningApplication.current.isActive, let keyWindow = NSApp.keyWindow else { return false }
        if UnsafeRawPointer(Unmanaged.passUnretained(keyWindow).toOpaque()) == handle { return true }
        guard let content = keyWindow.contentView else { return false }
        return UnsafeRawPointer(Unmanaged.passUnretained(content).toOpaque()) == handle
    }
}

private func panelBounds(_ target: StandardPanel) -> CGRect? {
    guard let position = folderAttribute(target.element, kAXPositionAttribute),
          let size = folderAttribute(target.element, kAXSizeAttribute),
          CFGetTypeID(position) == AXValueGetTypeID(), CFGetTypeID(size) == AXValueGetTypeID() else { return nil }
    var origin = CGPoint.zero, dimensions = CGSize.zero
    guard AXValueGetValue(position as! AXValue, .cgPoint, &origin),
          AXValueGetValue(size as! AXValue, .cgSize, &dimensions),
          dimensions.width > 1, dimensions.height > 1 else { return nil }
    return CGRect(origin: origin, size: dimensions)
}

@_cdecl("tbox_folder_dialog")
public func folderDialog(_ previous: Int64, _ bar: UnsafeRawPointer?) -> UnsafeMutablePointer<CChar>? {
    guard AXIsProcessTrusted() else { return response([:]) }
    let focused = focusedStandardPanel()
    let keepPrevious = focused == nil && folderBarIsKey(bar)
    let identified: (StandardPanel, Int64)? = retainedPanelLock.withLock {
        if let focused {
            if let previous = retainedPanel, focused.host == previous.host, focused.service == previous.service,
               CFEqual(focused.element, previous.element) {
                return (focused, retainedPanelID)
            }
            retainedPanel = focused
            retainedPanelID += 1
            return (focused, retainedPanelID)
        }
        if keepPrevious, previous == retainedPanelID, let retainedPanel { return (retainedPanel, previous) }
        return nil
    }
    guard let (target, id) = identified, let bounds = panelBounds(target) else { return response([:]) }
    let work: CGRect? = onFolderMainThread {
        let primaryHeight = CGDisplayBounds(CGMainDisplayID()).height
        let rect = CGRect(x: bounds.minX, y: primaryHeight - bounds.maxY, width: bounds.width, height: bounds.height)
        let screen = NSScreen.screens.first { $0.frame.contains(CGPoint(x: rect.midX, y: rect.midY)) } ??
            NSScreen.screens.first { $0.frame.intersects(rect) }
        guard let frame = screen?.visibleFrame else { return nil }
        return CGRect(x: frame.minX, y: primaryHeight - frame.maxY, width: frame.width, height: frame.height)
    }
    guard let work else { return response([:]) }
    func json(_ rect: CGRect) -> [String: CGFloat] {
        ["left": rect.minX, "top": rect.minY, "width": rect.width, "height": rect.height]
    }
    return response(["target": ["id": id, "bounds": json(bounds), "workArea": json(work), "scale": 1]])
}

@_cdecl("tbox_restore_folder_dialog")
public func restoreFolderDialog(_ id: Int64, _ bar: UnsafeRawPointer?) -> Int32 {
    guard AXIsProcessTrusted(), let target = folderTarget(id) else { return 0 }
    if stillFocused(target) { return 1 }
    guard folderBarIsKey(bar), panelBounds(target) != nil,
          let app = NSRunningApplication(processIdentifier: target.host) else { return 0 }
    guard app.activate(options: []) else { return 0 }
    if target.host == ProcessInfo.processInfo.processIdentifier {
        _ = withFolderAXOwner(target.element) { AXUIElementPerformAction(target.element, kAXRaiseAction as CFString) }
    }
    let deadline = Date().addingTimeInterval(0.4)
    repeat {
        let front = NSWorkspace.shared.frontmostApplication?.processIdentifier
        if front == target.host, stillFocused(target) { return 1 }
        if front != target.host && front != ProcessInfo.processInfo.processIdentifier { break }
        Thread.sleep(forTimeInterval: 0.02)
    } while Date() < deadline
    return 0
}

private func folderTarget(_ id: Int64) -> StandardPanel? {
    retainedPanelLock.withLock { id == retainedPanelID ? retainedPanel : nil }
}

private func goSheet(in target: StandardPanel) -> AXUIElement? {
    children(target.element).first { hasIdentity($0, role: kAXSheetRole, identifier: "GoToWindow") }
}

private func focusedGoSheet(_ target: StandardPanel, _ sheet: AXUIElement) -> Bool {
    guard NSWorkspace.shared.frontmostApplication?.processIdentifier == target.host,
          let current = goSheet(in: target), CFEqual(current, sheet),
          let focused = appElement(target.host, kAXFocusedWindowAttribute) else { return false }
    return CFEqual(focused, sheet)
}

// Target only the AppKit service that owns the validated panel. Posting to
// the host PID can miss the actual window even when AX presents a host proxy.
private func panelKey(_ key: CGKeyCode, flags: CGEventFlags = [], character: UniChar? = nil, to service: pid_t) {
    let source = CGEventSource(stateID: .privateState)
    guard let down = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: true),
          let up = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false) else { return }
    if let character {
        down.keyboardSetUnicodeString(stringLength: 1, unicodeString: [character])
        up.keyboardSetUnicodeString(stringLength: 1, unicodeString: [character])
    }
    down.flags = flags
    up.flags = []
    down.postToPid(service)
    up.postToPid(service)
}

func folderSuggestionMatches(_ identifier: String?, path: String) -> Bool {
    guard let identifier, identifier.hasPrefix("/") else { return false }
    return URL(fileURLWithPath: identifier).standardizedFileURL.path == path
}

private func selectedSuggestionMatches(_ sheet: AXUIElement, path: String) -> Bool {
    let selectedRows = boundedAXTree(sheet, maximum: 24)
        .filter { folderAttribute($0, kAXRoleAttribute) as? String == kAXTableRole }
        .flatMap { (folderAttribute($0, kAXSelectedRowsAttribute) as? [AXUIElement]) ?? [] }
    guard selectedRows.count == 1 else { return false }
    return boundedAXTree(selectedRows[0], maximum: 16).contains {
        folderAttribute($0, kAXRoleAttribute) as? String == kAXListRole &&
            folderSuggestionMatches(folderAttribute($0, kAXIdentifierAttribute) as? String, path: path)
    }
}

private func confirmsDestination(_ target: StandardPanel, _ destination: URL) -> Bool {
    for attribute in ["AXURL", kAXDocumentAttribute] {
        if let current = filePath(folderAttribute(target.element, attribute)) {
            return current == destination.path
        }
    }
    // AppKit's proxy often omits the current directory URL. The path was
    // verified against the full selected suggestion before submission. Check
    // that the sheet closed and the panel now shows that directory's label.
    let localizedName = (try? destination.resourceValues(forKeys: [.localizedNameKey]))?.localizedName
    return boundedAXTree(target.element, maximum: 32).contains { node in
        guard hasIdentity(node, role: kAXPopUpButtonRole, identifier: "where popup"),
              let value = folderAttribute(node, kAXValueAttribute) as? String else { return false }
        return value == destination.lastPathComponent || value == localizedName
    }
}

private func folderJumpFailure(_ message: String, stage: String) -> UnsafeMutablePointer<CChar>? {
    let front = NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "none"
    return response(["success": false, "message": message, "diagnostic": "stage=\(stage); front=\(front)"])
}

@_cdecl("tbox_folder_jump")
public func folderJump(_ destination: UnsafePointer<CChar>?, _ hotkeyCode: Int32) -> UnsafeMutablePointer<CChar>? {
    navigateFolder(destination, hotkeyCode, expected: nil)
}

@_cdecl("tbox_folder_jump_to")
public func folderJumpTo(_ destination: UnsafePointer<CChar>?, _ hotkeyCode: Int32,
                         _ id: Int64, _ bar: UnsafeRawPointer?) -> UnsafeMutablePointer<CChar>? {
    // Let the key that selected the result go up before returning focus to the system dialog.
    let deadline = Date().addingTimeInterval(0.6)
    while CGEventSource.keyState(.hidSystemState, key: CGKeyCode(hotkeyCode)) && Date() < deadline {
        Thread.sleep(forTimeInterval: 0.005)
    }
    guard !CGEventSource.keyState(.hidSystemState, key: CGKeyCode(hotkeyCode)),
          restoreFolderDialog(id, bar) != 0, let target = folderTarget(id) else {
        return folderJumpFailure("原文件面板已关闭或不在前台。", stage: "restore-panel")
    }
    return navigateFolder(destination, hotkeyCode, expected: target)
}

private func navigateFolder(_ destination: UnsafePointer<CChar>?, _ hotkeyCode: Int32,
                            expected: StandardPanel?) -> UnsafeMutablePointer<CChar>? {
    guard AXIsProcessTrusted() else {
        return response(["success": false, "message": "文件夹跳转需要在系统设置中允许 Tbox 辅助功能权限。"])
    }
    guard let destination else { return response(["success": false, "message": "目标文件夹无效。"]) }
    let url = URL(fileURLWithPath: String(cString: destination), isDirectory: true).standardizedFileURL
    let path = url.path
    var isDirectory: ObjCBool = false
    guard FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory), isDirectory.boolValue else {
        return response(["success": false, "message": "目标文件夹不可用。"])
    }
    guard let target = expected ?? focusedStandardPanel(), expected == nil || stillFocused(target) else {
        return folderJumpFailure("无法确认当前系统文件面板，未执行跳转。", stage: "panel")
    }
    guard goSheet(in: target) == nil else {
        return folderJumpFailure("请先关闭当前的“前往文件夹”输入框。", stage: "existing-sheet")
    }

    let modifiers: CGEventFlags = [.maskControl, .maskShift, .maskAlternate, .maskCommand]
    func shortcutHeld() -> Bool {
        !CGEventSource.flagsState(.hidSystemState).intersection(modifiers).isEmpty ||
            CGEventSource.keyState(.hidSystemState, key: CGKeyCode(hotkeyCode))
    }
    let releaseDeadline = Date().addingTimeInterval(0.6)
    while shortcutHeld() && Date() < releaseDeadline { Thread.sleep(forTimeInterval: 0.005) }
    guard !shortcutHeld(), stillFocused(target) else {
        return folderJumpFailure("快捷键未松开或文件面板已切换，跳转已取消。", stage: "focus-before-open")
    }
    panelKey(CGKeyCode(kVK_ANSI_G), flags: [.maskCommand, .maskShift], to: target.service)
    let sheetDeadline = Date().addingTimeInterval(1.5)
    var pathField: (AXUIElement, AXUIElement)?
    repeat {
        if let sheet = goSheet(in: target), focusedGoSheet(target, sheet),
           let field = children(sheet).first(where: {
               hasIdentity($0, role: kAXTextFieldRole, identifier: "PathTextField")
           }) {
            pathField = (sheet, field)
            break
        }
        Thread.sleep(forTimeInterval: 0.025)
    } while Date() < sheetDeadline && NSWorkspace.shared.frontmostApplication?.processIdentifier == target.host
    guard let (sheet, field) = pathField, focusedGoSheet(target, sheet) else {
        return folderJumpFailure("未找到系统“前往文件夹”输入框。", stage: "sheet")
    }
    let input = path.hasSuffix("/") ? path : path + "/"
    let filled = withFolderAXOwner(field) { AXUIElementSetAttributeValue(field, kAXValueAttribute as CFString, input as CFString) }
    guard filled == .success else {
        return folderJumpFailure("无法填写文件夹路径。", stage: "fill")
    }
    let suggestionDeadline = Date().addingTimeInterval(1.5)
    while focusedGoSheet(target, sheet) && Date() < suggestionDeadline {
        if selectedSuggestionMatches(sheet, path: path) {
            guard focusedGoSheet(target, sheet) else {
                return folderJumpFailure("文件面板焦点已切换，跳转已取消。", stage: "focus-before-submit")
            }
            panelKey(CGKeyCode(kVK_Return), character: 0x0D, to: target.service)
            let confirmationDeadline = Date().addingTimeInterval(1)
            repeat {
                if goSheet(in: target) == nil, stillFocused(target), confirmsDestination(target, url) {
                    return response(["success": true])
                }
                Thread.sleep(forTimeInterval: 0.05)
            } while Date() < confirmationDeadline &&
                NSWorkspace.shared.frontmostApplication?.processIdentifier == target.host
            return folderJumpFailure("已提交文件夹路径，尚未能确认面板当前位置。", stage: "verify")
        }
        Thread.sleep(forTimeInterval: 0.035)
    }
    return folderJumpFailure("系统未确认目标文件夹，未执行跳转。", stage: "suggestion")
}
