import Foundation
import ServiceManagement

private func startupState() -> [String: Any] {
    let status = SMAppService.mainApp.status
    var value: [String: Any] = ["enabled": status == .enabled, "requiresApproval": status == .requiresApproval]
    if status == .requiresApproval { value["message"] = "请在系统设置 → 通用 → 登录项与扩展中允许 Tbox。" }
    if status == .notFound { value["message"] = "系统尚未找到开机启动登记；可尝试开启后检查状态。" }
    return value
}

@_cdecl("tbox_startup_status")
public func startupStatus() -> UnsafeMutablePointer<CChar>? { response(startupState()) }

@_cdecl("tbox_startup_set")
public func startupSet(_ enabled: Int32) -> UnsafeMutablePointer<CChar>? {
    do {
        let service = SMAppService.mainApp
        if enabled != 0 {
            if service.status != .enabled && service.status != .requiresApproval { try service.register() }
        } else if service.status == .enabled || service.status == .requiresApproval { try service.unregister() }
        return response(startupState())
    } catch { return response(["error": "无法更改开机启动：\(error.localizedDescription)"]) }
}
