import AppKit

// Hold the parent pipe open without sending credentials. AppKit must still finish
// launching so it can handle the main-queue callbacks used for mouse and keyboard.
func checkHostLaunch(executable: String) throws {
    let process = Process()
    let input = Pipe()
    process.executableURL = URL(fileURLWithPath: executable)
    process.arguments = ["--tbox-host"]
    process.standardInput = input
    process.standardOutput = FileHandle.nullDevice
    process.standardError = FileHandle.nullDevice
    try process.run()
    defer {
        input.fileHandleForWriting.closeFile()
        if process.isRunning { process.terminate() }
        process.waitUntilExit()
    }

    let deadline = Date().addingTimeInterval(8)
    while Date() < deadline && process.isRunning {
        if NSRunningApplication(processIdentifier: process.processIdentifier)?.isFinishedLaunching == true {
            print("The remote host finished AppKit launch while waiting for the parent.")
            return
        }
        RunLoop.current.run(until: Date().addingTimeInterval(0.1))
    }
    throw NSError(domain: "TboxRemoteHostLaunch", code: 1,
                  userInfo: [NSLocalizedDescriptionKey: "The remote host did not finish AppKit launch while its parent pipe was open."])
}

do { try checkHostLaunch(executable: CommandLine.arguments[1]) }
catch { FileHandle.standardError.write(Data((error.localizedDescription + "\n").utf8)); exit(1) }
