import Foundation

// Pure ownership fixtures: no AX calls, windows, keys, or permission requests.
@main struct FolderPanelPolicyTests {
    final class Node {
        let owner: pid_t?
        let identifier: String?
        weak var parent: Node?
        weak var window: Node?
        init(_ owner: pid_t?, _ identifier: String? = nil) { self.owner = owner; self.identifier = identifier }
    }
    static func resolve(_ node: Node) -> (Node, pid_t)? {
        standardFolderPanelRoot(node, owner: { $0.owner }, identifier: { $0.identifier },
            parent: { $0.parent }, window: { $0.window })
    }
    static func main() {
        let host = Node(nil), open = Node(42, "open-panel"), field = Node(42)
        field.parent = open; field.window = host; open.parent = host
        precondition(resolve(field)?.0 === open, "Use the remote open sheet rather than its host AXWindow")
        let save = Node(42, "save-panel")
        precondition(resolve(save)?.0 === save, "A directly focused save panel is already the root")
        let foreign = Node(nil, "open-panel")
        precondition(resolve(foreign) == nil, "An identifier cannot authorize a non-system provider")
        let orphan = Node(42); orphan.window = host
        precondition(resolve(orphan) == nil, "Reject a host window from another owner")
        let providerWindow = Node(42); orphan.window = providerWindow
        precondition(resolve(orphan)?.0 === providerWindow, "Allow the provider's own window")
        let otherProvider = Node(43, "open-panel"), wrongParent = Node(42)
        wrongParent.parent = otherProvider
        precondition(resolve(wrongParent) == nil, "Do not cross process ownership while climbing parents")
        let cycle = Node(42); cycle.parent = cycle
        precondition(resolve(cycle) == nil, "Bound malformed parent cycles")
        print("PASS: 7 native folder panel ownership regressions")
    }
}
