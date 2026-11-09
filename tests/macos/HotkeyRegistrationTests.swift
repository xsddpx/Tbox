import Carbon
import Foundation

@main
struct HotkeyRegistrationTests {
    private static let modifiers = UInt32(cmdKey | shiftKey | optionKey | controlKey)
    private static let occupiedKey = UInt32(kVK_F19)
    private static let availableKey = UInt32(kVK_F20)

    static func main() throws {
        if CommandLine.arguments.count > 1 {
            try holdShortcut()
            return
        }

        try withOccupiedShortcut {
            let status = registerHotkey({ _ in }, 1, Int32(occupiedKey), Int32(modifiers))
            precondition(status == eventHotKeyExistsErr, "Registration should report the conflict, got \(status)")
            precondition(registerHotkey({ _ in }, 2, Int32(availableKey), Int32(modifiers)) == noErr)
            unregisterHotkey(2)
        }
        precondition(registerHotkey({ _ in }, 1, Int32(occupiedKey), Int32(modifiers)) == noErr)
        unregisterHotkeys()
        print("PASS: native hotkeys reject an exclusive conflict and register again after release")
    }

    private static func holdShortcut() throws {
        var reference: EventHotKeyRef?
        let status = RegisterEventHotKey(occupiedKey, modifiers, EventHotKeyID(signature: 0x54455354, id: 1),
                                         GetApplicationEventTarget(), OptionBits(kEventHotKeyExclusive), &reference)
        guard status == noErr, let reference else {
            preconditionFailure("Could not register the fixture shortcut: \(status)")
        }
        defer { UnregisterEventHotKey(reference) }
        try FileHandle.standardOutput.write(contentsOf: Data([1]))
        _ = FileHandle.standardInput.readData(ofLength: 1)
    }

    private static func withOccupiedShortcut(verify: () -> Void) throws {
        let child = Process()
        let input = Pipe()
        let output = Pipe()
        child.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
        child.arguments = ["--hold"]
        child.standardInput = input
        child.standardOutput = output
        try child.run()
        defer {
            try? input.fileHandleForWriting.close()
            child.waitUntilExit()
        }
        precondition(output.fileHandleForReading.readData(ofLength: 1) == Data([1]))
        verify()
    }
}
