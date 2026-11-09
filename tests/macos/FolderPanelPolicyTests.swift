import ApplicationServices
import Foundation

// Model the AX identity checks without requesting permissions or sending keys.
@main struct FolderPanelPolicyTests {
    static func main() {
        let host: pid_t = 41
        let service: pid_t = 42
        func recognized(_ role: String?, _ identifier: String?, _ owner: pid_t, serviceChild: pid_t? = nil) -> Bool {
            standardFolderPanelService(role: role, identifier: identifier, owner: owner, host: host,
                isService: { $0 == service }, serviceChild: { serviceChild }) == service
        }

        precondition(recognized(kAXWindowRole, "open-panel", host, serviceChild: service),
            "Recognize a host-owned open panel backed by AppKit")
        precondition(recognized(kAXSheetRole, "save-panel", host, serviceChild: service),
            "Recognize a host-owned save sheet backed by AppKit")
        precondition(recognized(kAXWindowRole, "OpenPanel", service),
            "Recognize a panel window owned directly by AppKit")
        precondition(!recognized(kAXWindowRole, "open-panel", host),
            "A host cannot authorize its own ordinary window by matching the identifier")
        precondition(!recognized(kAXWindowRole, "open-panel", host, serviceChild: 43),
            "An unrelated process cannot authorize a host-owned proxy")
        precondition(!recognized(kAXWindowRole, "open-panel", 43, serviceChild: service),
            "Reject a window from a different host")
        precondition(!recognized(kAXWindowRole, nil, host, serviceChild: service),
            "An ordinary host window containing a system sheet is not itself a panel")
        precondition(!recognized(kAXSheetRole, "GoToWindow", service),
            "The nested navigation sheet is not an open or save panel")
        precondition(!recognized(kAXTextFieldRole, "open-panel", host, serviceChild: service),
            "A matching identifier on an unrelated control does not authorize navigation")

        let path = "/one/目标 同名"
        precondition(folderSuggestionMatches(path + "/", path: path),
            "The full path with a trailing slash matches")
        precondition(!folderSuggestionMatches("/two/目标 同名", path: path),
            "Two directories with the same name must be distinguished")
        precondition(!folderSuggestionMatches("目标 同名", path: path),
            "A display name is not an exact path")
        precondition(finderFolderFromItems([path + "/一.txt", path + "/二.txt"], title: "目标 同名") == path,
            "Items in the active pane can corroborate the directory title")
        precondition(finderFolderFromItems([path + "/一.txt", "/two/目标 同名/二.txt"], title: "目标 同名") == nil,
            "Mixed parents from different folders are ambiguous")
        precondition(finderFolderFromItems([path + "/一.txt"], title: "搜索结果") == nil,
            "An unrelated title does not authorize navigation")
        precondition(finderFolderFromItems([], title: "目标 同名") == nil,
            "A title alone cannot identify a directory")
        print("PASS: 16 native folder panel and path regressions")
    }
}
