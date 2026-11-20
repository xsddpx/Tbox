import Foundation

let lidSleepServiceName = "com.xsddpx.tbox.lid-sleep"
let lidSleepServicePlist = lidSleepServiceName + ".plist"

@objc protocol LidSleepService {
    // Returns 0 for released, 1 for held, or -1 for a failed change.
    func apply(_ hold: Bool, legacyChange: Bool, reply: @escaping (Int32) -> Void)
}
