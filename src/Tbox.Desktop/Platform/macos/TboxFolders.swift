import AppKit
import Carbon

private func folderAttribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var value: CFTypeRef?
    AXUIElementSetMessagingTimeout(element, 0.12)
    guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else { return nil }
    return value
}

private func folderElement(_ value: CFTypeRef?) -> AXUIElement? {
    guard let value = value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
    return (value as! AXUIElement)
}

private func filePath(_ value: CFTypeRef?) -> String? {
    if let url = value as? URL, url.isFileURL { return url.standardizedFileURL.path }
    if let text = value as? String, let url = URL(string: text), url.isFileURL { return url.standardizedFileURL.path }
    return nil
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
            let title = folderAttribute(window, kAXTitleAttribute) as? String
            var document: CFTypeRef?
            let result = AXUIElementCopyAttributeValue(window, kAXDocumentAttribute as CFString, &document)
            guard result == .success || result == .attributeUnsupported || result == .noValue else {
                return response(["observed": false])
            }
            let after = folderAttribute(window, kAXTitleAttribute) as? String
            if title == after, title != nil {
                return response(["observed": true, "path": filePath(document) as Any? ?? NSNull()])
            }
            Thread.sleep(forTimeInterval: 0.025)
        }
        return response(["observed": false])
    }
}

private struct StandardPanel {
    let element: AXUIElement
    let process: pid_t
}

// NSOpenPanel/NSSavePanel run in this AppKit service on supported macOS versions.
// Restrict navigation to that provider; other dialogs never receive synthetic input.
private func panelOwner(_ element: AXUIElement) -> pid_t? {
    var pid: pid_t = 0
    guard AXUIElementGetPid(element, &pid) == .success,
          NSRunningApplication(processIdentifier: pid)?.bundleIdentifier == "com.apple.appkit.xpc.openAndSavePanelService" else { return nil }
    return pid
}

private func panelChildren(_ root: AXUIElement, deadline: Date, maximum: Int = 160) -> [AXUIElement] {
    var queue = [root]; var result: [AXUIElement] = []
    while !queue.isEmpty && result.count < maximum && Date() < deadline {
        let next = queue.removeFirst()
        if result.contains(where: { CFEqual($0, next) }) { continue }
        result.append(next)
        queue.append(contentsOf: (folderAttribute(next, kAXChildrenAttribute) as? [AXUIElement]) ?? [])
    }
    return result
}

// Keep ownership and ancestor selection independently testable without sending
// accessibility input or requiring a user's desktop permissions.
func standardFolderPanelRoot<Node>(_ element: Node, owner: (Node) -> pid_t?,
    identifier: (Node) -> String?, parent: (Node) -> Node?, window: (Node) -> Node?) -> (Node, pid_t)? {
    guard let pid = owner(element) else { return nil }
    var current: Node? = element
    for _ in 0..<12 {
        guard let node = current, owner(node) == pid else { break }
        if let value = identifier(node), value == "open-panel" || value == "save-panel" {
            return (node, pid)
        }
        current = parent(node)
    }
    // The remote sheet's AXWindow can refer back to the host application's
    // window. Do not treat that host window as a file panel.
    if let candidate = window(element), owner(candidate) == pid {
        return (candidate, pid)
    }
    return nil
}

private func remoteStandardPanel(_ element: AXUIElement) -> StandardPanel? {
    guard let (root, pid) = standardFolderPanelRoot(element, owner: panelOwner,
        identifier: { folderAttribute($0, kAXIdentifierAttribute) as? String },
        parent: { folderElement(folderAttribute($0, kAXParentAttribute)) },
        window: { folderElement(folderAttribute($0, kAXWindowAttribute)) }) else { return nil }
    return StandardPanel(element: root, process: pid)
}

private func focusedStandardPanel() -> StandardPanel? {
    guard let front = NSWorkspace.shared.frontmostApplication,
          !["com.apple.SecurityAgent", "com.apple.LocalAuthentication.UIAgent", "com.apple.LocalAuthenticationRemoteService"].contains(front.bundleIdentifier ?? "") else { return nil }
    let system = AXUIElementCreateSystemWide()
    if let focused = folderElement(folderAttribute(system, kAXFocusedUIElementAttribute)),
       let panel = remoteStandardPanel(focused) {
        return panel
    }
    // AppKit can expose the host window as a proxy around the remote sheet.
    guard let application = NSWorkspace.shared.frontmostApplication,
          application.bundleIdentifier != "com.apple.SecurityAgent",
          let window = folderElement(folderAttribute(AXUIElementCreateApplication(application.processIdentifier), kAXFocusedWindowAttribute)) else { return nil }
    for element in panelChildren(window, deadline: Date().addingTimeInterval(0.4)) {
        if let panel = remoteStandardPanel(element) { return panel }
    }
    return nil
}

// Failure-only metadata helps distinguish an actual panel recognition problem
// from focus moving before the hotkey finishes. Never log document text/paths.
private func panelFailureDiagnostic() -> String {
    let front = NSWorkspace.shared.frontmostApplication
    var focusedValue: CFTypeRef?
    let status = AXUIElementCopyAttributeValue(AXUIElementCreateSystemWide(), kAXFocusedUIElementAttribute as CFString, &focusedValue)
    guard let focused = folderElement(focusedValue) else {
        return "front=\(front?.bundleIdentifier ?? "none"); focusedError=\(status.rawValue)"
    }
    var pid: pid_t = 0; AXUIElementGetPid(focused, &pid)
    let role = folderAttribute(focused, kAXRoleAttribute) as? String ?? "none"
    return "front=\(front?.bundleIdentifier ?? "none"); focusedOwner=\(NSRunningApplication(processIdentifier: pid)?.bundleIdentifier ?? "none"); focusedRole=\(role)"
}

private func stillFocused(_ target: StandardPanel) -> Bool {
    guard let now = focusedStandardPanel() else { return false }
    return now.process == target.process && CFEqual(now.element, target.element)
}

private func panelKey(_ key: CGKeyCode, flags: CGEventFlags, process: pid_t) {
    let source = CGEventSource(stateID: .combinedSessionState)
    let down = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: true)
    let up = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false)
    down?.flags = flags; up?.flags = flags
    down?.postToPid(process); up?.postToPid(process)
}

@_cdecl("tbox_folder_jump")
public func folderJump(_ destination: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    guard AXIsProcessTrusted() else { return response(["success": false, "message": "文件夹跳转需要在系统设置中允许 Tbox 辅助功能权限。"] ) }
    guard let destination = destination else { return response(["success": false, "message": "目标文件夹无效。"] ) }
    let path = String(cString: destination)
    var isDirectory: ObjCBool = false
    guard FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory), isDirectory.boolValue else {
        return response(["success": false, "message": "目标文件夹不可用。"] )
    }
    guard let target = focusedStandardPanel() else {
        return response(["success": false, "message": "无法确认当前系统文件面板，未执行跳转。", "diagnostic": panelFailureDiagnostic()] )
    }
    let releaseDeadline = Date().addingTimeInterval(0.3)
    while Date() < releaseDeadline && (CGEventSource.keyState(.hidSystemState, key: CGKeyCode(kVK_Control)) || CGEventSource.keyState(.hidSystemState, key: CGKeyCode(kVK_ANSI_G))) {
        Thread.sleep(forTimeInterval: 0.005)
    }
    guard stillFocused(target) else { return response(["success": false, "message": "文件面板已切换，跳转已取消。"] ) }
    panelKey(CGKeyCode(kVK_ANSI_G), flags: [.maskCommand, .maskShift], process: target.process)
    let deadline = Date().addingTimeInterval(1.5)
    var goSheet: AXUIElement?
    while Date() < deadline {
        let sheets = (folderAttribute(target.element, kAXChildrenAttribute) as? [AXUIElement] ?? []).filter {
            folderAttribute($0, kAXRoleAttribute) as? String == kAXSheetRole
        }
        if sheets.count == 1 { goSheet = sheets[0]; break }
        Thread.sleep(forTimeInterval: 0.025)
    }
    guard let goSheet = goSheet, panelOwner(goSheet) == target.process else {
        return response(["success": false, "message": "未找到系统“前往文件夹”输入框。"] )
    }
    let fields = panelChildren(goSheet, deadline: Date().addingTimeInterval(0.4)).filter {
        folderAttribute($0, kAXRoleAttribute) as? String == kAXTextFieldRole &&
        folderAttribute($0, kAXSubroleAttribute) as? String != kAXSecureTextFieldSubrole
    }
    guard stillFocused(target), fields.count == 1,
          AXUIElementSetAttributeValue(fields[0], kAXValueAttribute as CFString, path as CFString) == .success else {
        return response(["success": false, "message": "无法填写文件夹路径。"] )
    }
    guard stillFocused(target) else { return response(["success": false, "message": "文件面板已切换，跳转已取消。"] ) }
    panelKey(CGKeyCode(kVK_Return), flags: [], process: target.process)
    let expected = URL(fileURLWithPath: path).standardizedFileURL.path
    for _ in 0..<20 {
        for name in ["AXURL", kAXDocumentAttribute] {
            if filePath(folderAttribute(target.element, name)) == expected { return response(["success": true]) }
        }
        Thread.sleep(forTimeInterval: 0.05)
    }
    return response(["success": false, "message": "已提交文件夹路径，尚未能确认面板当前位置。"] )
}
