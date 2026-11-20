import Foundation
import OSLog
import SystemConfiguration

private let log = Logger(subsystem: lidSleepServiceName, category: "power")

private struct LidSleepSetting {
    // Written before changing pmset, so a restarted helper can restore it after a crash.
    let marker = URL(fileURLWithPath: "/private/var/db/com.xsddpx.tbox.lid-sleep")
    var needsRestore: Bool { FileManager.default.fileExists(atPath: marker.path) }

    func apply(_ hold: Bool, legacyChange: Bool = false) throws {
        let settings = try pmset(["-g"])
        let disabled = settings.split(separator: "\n").contains { line in
            line.split(whereSeparator: \.isWhitespace) == ["SleepDisabled", "1"]
        }
        if (hold && !disabled || legacyChange && disabled) && !needsRestore {
            try Data().write(to: marker, options: .withoutOverwriting)
        }
        if hold && !disabled { _ = try pmset(["-a", "disablesleep", "1"]) }
        if !hold && needsRestore {
            if disabled { _ = try pmset(["-a", "disablesleep", "0"]) }
            try FileManager.default.removeItem(at: marker)
        }
    }

    private func pmset(_ arguments: [String]) throws -> String {
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/pmset")
        process.arguments = arguments
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw CocoaError(.fileWriteUnknown) }
        return String(decoding: data, as: UTF8.self)
    }
}

private final class Client: NSObject, LidSleepService {
    let daemon: Daemon
    let id: UUID
    let uid: uid_t

    init(daemon: Daemon, id: UUID, uid: uid_t) {
        self.daemon = daemon
        self.id = id
        self.uid = uid
    }

    func apply(_ hold: Bool, legacyChange: Bool, reply: @escaping (Int32) -> Void) {
        daemon.apply(hold, legacyChange: legacyChange, client: self, reply: reply)
    }
}

private final class Daemon: NSObject, NSXPCListenerDelegate {
    private let queue = DispatchQueue(label: lidSleepServiceName)
    private let setting = LidSleepSetting()
    private var clients: Set<UUID> = []
    private var owner: UUID?

    func listener(_ listener: NSXPCListener, shouldAcceptNewConnection connection: NSXPCConnection) -> Bool {
        let uid = connection.effectiveUserIdentifier
        guard Self.isConsoleUser(uid) else { return false }
        connection.setCodeSigningRequirement(lidSleepAppRequirement)
        let id = UUID()
        queue.sync { _ = clients.insert(id) }
        connection.exportedInterface = NSXPCInterface(with: LidSleepService.self)
        connection.exportedObject = Client(daemon: self, id: id, uid: uid)
        connection.invalidationHandler = { [weak self] in self?.disconnect(id) }
        connection.resume()
        return true
    }

    func apply(_ hold: Bool, legacyChange: Bool, client: Client, reply: @escaping (Int32) -> Void) {
        queue.async {
            guard self.clients.contains(client.id), self.owner == nil || self.owner == client.id,
                  !hold || Self.isConsoleUser(client.uid) else { reply(-1); return }
            if hold { self.owner = client.id }
            do {
                try self.setting.apply(hold, legacyChange: legacyChange)
                self.owner = hold ? client.id : nil
                reply(hold ? 1 : 0)
            } catch {
                log.error("Power setting change failed: \(error.localizedDescription)")
                reply(-1)
            }
        }
    }

    private func disconnect(_ id: UUID) {
        queue.async {
            self.clients.remove(id)
            guard self.owner == id else { return }
            self.owner = nil
            self.restore()
        }
    }

    func restore() {
        guard setting.needsRestore else { return }
        do {
            try setting.apply(false)
        } catch {
            log.error("Power setting restoration failed: \(error.localizedDescription)")
            // A failed daemon starts again through launchd and retries the saved restoration.
            exit(EXIT_FAILURE)
        }
    }

    func terminate() {
        queue.sync { restore() }
        exit(EXIT_SUCCESS)
    }

    private static func isConsoleUser(_ uid: uid_t) -> Bool {
        var consoleUID: uid_t = 0
        return uid != 0 && SCDynamicStoreCopyConsoleUser(nil, &consoleUID, nil) != nil && uid == consoleUID
    }
}

// Only launchd runs this as a system service. Startup recovers any interrupted power change.
guard geteuid() == 0 else { exit(EXIT_FAILURE) }
private let daemon = Daemon()
daemon.restore()
signal(SIGTERM, SIG_IGN)
let termination = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
termination.setEventHandler { daemon.terminate() }
termination.resume()
let listener = NSXPCListener(machServiceName: lidSleepServiceName)
listener.delegate = daemon
listener.resume()
dispatchMain()
