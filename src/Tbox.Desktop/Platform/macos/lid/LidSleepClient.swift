import Foundation
import ServiceManagement

private var connection: NSXPCConnection?

@_cdecl("tbox_lid_sleep_approved")
public func lidSleepApproved() -> Int32 {
    SMAppService.daemon(plistName: lidSleepServicePlist).status == .enabled ? 1 : 0
}

@_cdecl("tbox_lid_sleep_request_approval")
public func lidSleepRequestApproval() -> Int32 {
    let service = SMAppService.daemon(plistName: lidSleepServicePlist)
    if service.status == .notRegistered || service.status == .notFound {
        do {
            try service.register()
        } catch {
            // macOS can report an error while successfully recording a pending approval.
            guard service.status == .requiresApproval || service.status == .enabled else { return -1 }
        }
    }
    if service.status == .requiresApproval { SMAppService.openSystemSettingsLoginItems() }
    return service.status == .enabled || service.status == .requiresApproval ? 0 : -1
}

// Calls are serialized by MacKeepAwake. Keep a connection while the app holds the wake lease.
@_cdecl("tbox_lid_sleep_apply")
public func lidSleepApply(_ hold: Int32, _ legacyChange: Int32) -> Int32 {
    if connection == nil {
        let newConnection = NSXPCConnection(machServiceName: lidSleepServiceName, options: .privileged)
        newConnection.setCodeSigningRequirement(lidSleepDaemonRequirement)
        newConnection.remoteObjectInterface = NSXPCInterface(with: LidSleepService.self)
        newConnection.resume()
        connection = newConnection
    }
    let result = LidSleepReply()
    guard let service = connection?.remoteObjectProxyWithErrorHandler({ _ in result.finish(-1) }) as? LidSleepService else { return -1 }
    service.apply(hold != 0, legacyChange: legacyChange != 0, reply: result.finish)
    let status = result.wait()
    if status != 1 {
        connection?.invalidate()
        connection = nil
    }
    return status
}

private final class LidSleepReply: @unchecked Sendable {
    private let completed = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var status: Int32 = -1

    func finish(_ result: Int32) {
        lock.withLock { status = result }
        completed.signal()
    }

    func wait() -> Int32 {
        guard completed.wait(timeout: .now() + 5) == .success else { return -1 }
        return lock.withLock { status }
    }
}
