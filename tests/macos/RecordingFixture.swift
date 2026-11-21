import AppKit

// Runs outside Tbox so its capture exclusion leaves the test window and tones visible.
let directory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
guard let screen = NSScreen.screens.first else { fatalError("No display for recording fixture") }
let frame = CGRect(x: screen.visibleFrame.midX - 160, y: screen.visibleFrame.midY - 120, width: 320, height: 240)
let window = NSWindow(contentRect: frame, styleMask: .borderless, backing: .buffered, defer: false)
window.hasShadow = false
window.level = .floating
window.backgroundColor = .red
window.orderFrontRegardless()
let bounds = ["Left": frame.minX, "Top": screen.frame.maxY - frame.maxY,
              "Width": frame.width, "Height": frame.height]
try JSONSerialization.data(withJSONObject: bounds).write(to: directory.appendingPathComponent("fixture-bounds.json"), options: .atomic)
let colors: [String: NSColor] = ["red": .red, "green": .green, "blue": .blue]
var previous = ""
var tone: NSSound?
let timer = Timer(timeInterval: 0.025, repeats: true) { _ in
    guard let phase = try? String(contentsOf: directory.appendingPathComponent("fixture-phase.txt"), encoding: .utf8),
          phase != previous else { return }
    if phase == "stop" {
        tone?.stop()
        try? phase.write(to: directory.appendingPathComponent("fixture-ready.txt"), atomically: true, encoding: .utf8)
        app.terminate(nil)
        return
    }
    guard let color = colors[phase],
          let sound = NSSound(contentsOf: directory.appendingPathComponent(phase + ".wav"), byReference: true) else { return }
    window.backgroundColor = color
    window.displayIfNeeded()
    tone?.stop()
    sound.loops = true
    guard sound.play() else { return }
    tone = sound
    previous = phase
    try? phase.write(to: directory.appendingPathComponent("fixture-ready.txt"), atomically: true, encoding: .utf8)
}
RunLoop.main.add(timer, forMode: .common)
app.run()
