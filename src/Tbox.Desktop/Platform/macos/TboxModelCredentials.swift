import Foundation
import Security
import LocalAuthentication

// Generic-password items use the app's stable designated requirement and the user's default keychain.
private func modelSecretQuery(_ id: UnsafePointer<CChar>?) -> [String: Any]? {
    guard let id = id, let uuid = UUID(uuidString: String(cString: id)) else { return nil }
    return [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "com.xsddpx.tbox.models",
            kSecAttrAccount as String: uuid.uuidString.lowercased(),
            kSecAttrSynchronizable as String: false]
}

@_cdecl("tbox_model_secret_contains")
public func modelSecretContains(_ id: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    guard var query = modelSecretQuery(id) else { return response(["error": true]) }
    query[kSecReturnAttributes as String] = true
    let context = LAContext()
    context.interactionNotAllowed = true
    query[kSecUseAuthenticationContext as String] = context
    var result: CFTypeRef?
    let status = SecItemCopyMatching(query as CFDictionary, &result)
    guard status == errSecSuccess || status == errSecItemNotFound else { return response(["error": true]) }
    return response(["found": status == errSecSuccess])
}

@_cdecl("tbox_model_secret_get")
public func modelSecretGet(_ id: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    guard var query = modelSecretQuery(id) else { return response(["error": true]) }
    query[kSecReturnData as String] = true
    var result: CFTypeRef?
    let status = SecItemCopyMatching(query as CFDictionary, &result)
    if status == errSecItemNotFound { return response(["value": NSNull()]) }
    guard status == errSecSuccess, let data = result as? Data, let text = String(data: data, encoding: .utf8) else { return response(["error": true]) }
    return response(["value": text])
}

@_cdecl("tbox_model_secret_set")
public func modelSecretSet(_ id: UnsafePointer<CChar>?, _ secret: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    guard var query = modelSecretQuery(id), let secret = secret else { return response(["error": true]) }
    let data = Data(String(cString: secret).utf8)
    let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
    if status == errSecItemNotFound {
        query[kSecValueData as String] = data
        query[kSecAttrLabel as String] = "Tbox 模型服务"
        return response(SecItemAdd(query as CFDictionary, nil) == errSecSuccess ? ["success": true] : ["error": true])
    }
    return response(status == errSecSuccess ? ["success": true] : ["error": true])
}

@_cdecl("tbox_model_secret_delete")
public func modelSecretDelete(_ id: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    guard let query = modelSecretQuery(id) else { return response(["error": true]) }
    let status = SecItemDelete(query as CFDictionary)
    return response(status == errSecSuccess || status == errSecItemNotFound ? ["success": true] : ["error": true])
}
