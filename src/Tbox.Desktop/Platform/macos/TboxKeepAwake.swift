import Foundation
import IOKit.pwr_mgt

// Idle sleep and display assertions end with the process. The daemon manages lid sleep.
private var systemAssertion: IOPMAssertionID = 0
private var displayAssertion: IOPMAssertionID = 0

private func hold(_ id: inout IOPMAssertionID, _ type: String, _ wanted: Bool) {
    if !wanted {
        if id != 0 { IOPMAssertionRelease(id); id = 0 }
        return
    }
    if id != 0 { return }
    if IOPMAssertionCreateWithName(type as CFString, IOPMAssertionLevel(kIOPMAssertionLevelOn),
        "Tbox 保持唤醒" as CFString, &id) != kIOReturnSuccess { id = 0 }
}

// Returns bit 1 when the system assertion is held and bit 2 when the display assertion is held.
@_cdecl("tbox_keep_awake")
public func keepAwake(_ system: Int32, _ display: Int32) -> Int32 {
    // PreventUserIdleSystemSleep lets the display sleep; the display assertion keeps both awake.
    hold(&systemAssertion, kIOPMAssertionTypePreventUserIdleSystemSleep, system != 0)
    hold(&displayAssertion, kIOPMAssertionTypePreventUserIdleDisplaySleep, system != 0 && display != 0)
    return (systemAssertion != 0 ? 1 : 0) | (displayAssertion != 0 ? 2 : 0)
}

// Macs with a built-in display report AppleClamshellState; desktops do not.
@_cdecl("tbox_has_lid")
public func hasLid() -> Int32 {
    let root = IORegistryEntryFromPath(kIOMainPortDefault, "IOPower:/IOPowerConnection/IOPMrootDomain")
    guard root != 0 else { return 0 }
    defer { IOObjectRelease(root) }
    return IORegistryEntryCreateCFProperty(root, "AppleClamshellState" as CFString, kCFAllocatorDefault, 0) != nil ? 1 : 0
}
