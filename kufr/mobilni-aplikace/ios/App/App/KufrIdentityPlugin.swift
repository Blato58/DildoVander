import AuthenticationServices
import CryptoKit
import Capacitor
import Foundation
import Security
import UIKit

// AuthenticationServices performs native WebAuthn. URLSession owns a separate
// cookie jar: browser/WKWebView cookies and device credentials never enter it.
@objc(KufrIdentityPlugin)
@available(iOS 16.0, *)
public final class KufrIdentityPlugin: CAPPlugin, CAPBridgedPlugin,
    ASAuthorizationControllerDelegate, ASAuthorizationControllerPresentationContextProviding {
    internal static weak var active: KufrIdentityPlugin?
    public override func load() { Self.active = self }

    internal func configuredAccountOrigin() throws -> URL { try configuration().0 }

    @MainActor internal func accountDeviceBinding() throws -> String {
        let origin = try configuration().0
        restoreCookies(origin: origin)
        guard let cookie = session.configuration.httpCookieStorage?.cookies(for: origin)?.first(where: { $0.name == "__Host-dv-account" && $0.isSecure }) else { throw AccountError.verificationFailed }
        return SHA256.hash(data: Data(cookie.value.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    // Native-only request preparation lets the existing bounded streaming
    // transports retain cancellation/backpressure. Credentials never enter JS.
    @MainActor internal func authorizeDeviceRequest(_ original: URLRequest, deviceId: String, binding: String) async throws -> URLRequest {
        guard try accountDeviceBinding() == binding else { throw AccountError.verificationFailed }
        let (origin, _) = try configuration()
        guard let url = original.url, url.scheme == origin.scheme, url.host == origin.host, url.port == origin.port,
              url.user == nil, url.password == nil, url.fragment == nil,
              deviceId.count > 0, deviceId.count <= 128,
              deviceId.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }),
              AccountTransportPolicy.device(path: url.path, method: original.httpMethod ?? "GET", query: URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []),
              (original.httpBody?.count ?? 0) <= 8388608 else { throw AccountError.invalidPath }
        restoreCookies(origin: origin)
        if csrf.isEmpty {
            let current = try json(await send(path: "/app/v1/session", method: "GET"))
            guard current["account"] as? [String: Any] != nil else { throw AccountError.verificationFailed }
            csrf = current["csrfToken"] as? String ?? ""
        }
        guard try accountDeviceBinding() == binding else { throw AccountError.verificationFailed }
        let permitted = ["accept", "content-type", "idempotency-key", "client-turn-id", "boot-id", "generation", "device-revision", "catalog-version", "input-id", "input-sequence"]
        var request = original
        request.allHTTPHeaderFields = [:]
        for (name, value) in original.allHTTPHeaderFields ?? [:] {
            if name.lowercased() == "authorization" { continue }
            guard permitted.contains(name.lowercased()), value.count <= 1024, !value.contains("\r"), !value.contains("\n") else { throw AccountError.invalidPath }
            request.setValue(value, forHTTPHeaderField: name)
        }
        request.httpShouldHandleCookies = false
        let cookies = session.configuration.httpCookieStorage?.cookies(for: url) ?? []
        for (name, value) in HTTPCookie.requestHeaderFields(with: cookies) { request.setValue(value, forHTTPHeaderField: name) }
        request.setValue(origin.absoluteString, forHTTPHeaderField: "Origin")
        request.setValue(deviceId, forHTTPHeaderField: "X-DV-Device-Id")
        if request.httpMethod != "GET" { request.setValue(csrf, forHTTPHeaderField: "X-DV-CSRF") }
        return request
    }

    @MainActor internal func deviceRequest(_ original: URLRequest, deviceId: String, binding: String, limit: Int) async throws -> (Data, HTTPURLResponse) {
        guard limit > 0, limit <= 8388608 else { throw AccountError.invalidResponse }
        let request = try await authorizeDeviceRequest(original, deviceId: deviceId, binding: binding)
        let (data, response) = try await session.data(for: request)
        guard try accountDeviceBinding() == binding else { throw AccountError.verificationFailed }
        guard data.count <= limit, let http = response as? HTTPURLResponse else { throw AccountError.invalidResponse }
        return (data, http)
    }
    public let identifier = "KufrIdentityPlugin"
    public let jsName = "KufrIdentity"
    public let pluginMethods: [CAPPluginMethod] = [
        CAPPluginMethod(name: "request", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "passkey", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "guestCapabilities", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "guestRequest", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "shareGuestFile", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "shareGuestLink", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "accountTransport", returnType: CAPPluginReturnPromise)
    ]
    private let session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieAcceptPolicy = .always
        configuration.httpShouldSetCookies = true
        configuration.timeoutIntervalForRequest = 30
        return URLSession(configuration: configuration, delegate: AccountNoRedirect(), delegateQueue: nil)
    }()
    private var csrf = ""
    private var pending: CAPPluginCall?
    private var completionPath = ""
    private var controller: ASAuthorizationController?
    private var restoredCookies = false
    private var requestActive = false
    private var publishedCookieDigest: String?
    private let guestSession: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieAcceptPolicy = .always
        configuration.timeoutIntervalForRequest = 60
        return URLSession(configuration: configuration, delegate: AccountNoRedirect(), delegateQueue: nil)
    }()
    private var guestCookiesRestored = false

    @objc public func accountTransport(_ call: CAPPluginCall) {
        Task { @MainActor in
            do {
                guard pending == nil else { throw AccountError.verificationFailed }
                let (origin, _) = try configuration()
                let path = call.getString("path") ?? "", method = call.getString("method") ?? "GET"
                guard Self.allowed(path: path, method: method), let url = URL(string: path, relativeTo: origin)?.absoluteURL,
                      url.scheme == "https", url.host == origin.host, url.port == origin.port else { throw AccountError.invalidPath }
                restoreCookies(origin: origin)
                var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData)
                request.httpMethod = method
                request.setValue(origin.absoluteString, forHTTPHeaderField: "Origin")
                let permitted = ["accept", "content-type", "x-dv-csrf", "x-dv-device-id", "idempotency-key", "session-id", "boot-id", "media-epoch", "input-epoch", "input-sequence"]
                for (name, value) in call.getObject("headers") ?? [:] {
                    guard permitted.contains(name.lowercased()), let text = value as? String, text.count <= 1024,
                          !text.contains("\r"), !text.contains("\n") else { throw AccountError.invalidPath }
                    if name.lowercased() != "x-dv-csrf" { request.setValue(text, forHTTPHeaderField: name) }
                }
                if method != "GET" { request.setValue(csrf, forHTTPHeaderField: "X-DV-CSRF") }
                if let encoded = call.getString("bodyBase64") {
                    guard encoded.utf8.count <= 349528, let data = Data(base64Encoded: encoded), data.count <= 262144 else { throw AccountError.invalidResponse }
                    request.httpBody = data
                }
                let (data, response) = try await session.data(for: request)
                let archiveAsset = path.hasPrefix("/app/v1/archive/associations/") && path.contains("/assets/")
                guard let http = response as? HTTPURLResponse, data.count <= (archiveAsset ? 33554432 : 1048576), http.url?.host == origin.host else { throw AccountError.invalidResponse }
                try persistAccountCookie(origin: origin)
                var headers: [String: String] = [:]
                for name in ["Content-Type", "X-Twin-Media-Accepted-Bytes", "Retry-After"] {
                    if let value = http.value(forHTTPHeaderField: name) { headers[name.lowercased()] = value }
                }
                call.resolve(["status": http.statusCode, "headers": headers, "bodyBase64": data.base64EncodedString()])
            } catch { call.reject(error.localizedDescription) }
        }
    }

    @objc public func guestCapabilities(_ call: CAPPluginCall) {
        if let config = try? configuration() { call.resolve(["available": true, "origin": config.0.absoluteString]) }
        else { call.resolve(["available": false]) }
    }

    @objc public func shareGuestLink(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            do {
                let (origin, _) = try self.configuration()
                guard let raw = call.getString("url"), let url = URL(string: raw), url.scheme == "https",
                      url.host == origin.host, url.port == origin.port, url.path == "/keepsake/", url.query == nil,
                      url.user == nil, url.password == nil, let fragment = url.fragment, fragment.hasPrefix("s="),
                      fragment.count >= 34, fragment.count <= 258,
                      fragment.dropFirst(2).allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }),
                      let view = self.bridge?.viewController else { throw AccountError.invalidPath }
                let sheet = UIActivityViewController(activityItems: [url], applicationActivities: nil)
                sheet.popoverPresentationController?.sourceView = view.view
                sheet.popoverPresentationController?.sourceRect = CGRect(x: view.view.bounds.midX, y: view.view.bounds.midY, width: 0, height: 0)
                sheet.completionWithItemsHandler = { _, completed, _, _ in call.resolve(["completed": completed]) }
                view.present(sheet, animated: true)
            } catch { call.reject(error.localizedDescription) }
        }
    }

    @objc public func guestRequest(_ call: CAPPluginCall) {
        Task { @MainActor in
            do {
                let (origin, _) = try configuration()
                let path = call.getString("path") ?? ""
                let method = call.getString("method") ?? "GET"
                guard Self.allowedGuest(path: path, method: method),
                      let url = URL(string: path, relativeTo: origin)?.absoluteURL, url.host == origin.host,
                      url.scheme == origin.scheme, url.port == origin.port else { throw AccountError.invalidPath }
                var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData)
                request.httpMethod = method
                request.setValue(origin.absoluteString, forHTTPHeaderField: "Origin")
                let normalizedPath = URLComponents(string: path)?.path ?? ""
                let publicRead = normalizedPath == "/guest/v1/evening" || normalizedPath.hasPrefix("/guest/v1/evenings/")
                // Public browsing must not reveal a guest identity to the server.
                request.httpShouldHandleCookies = !publicRead
                if !publicRead { restoreGuestCookies(origin: origin) }
                let headers = call.getObject("headers") ?? [:]
                let permitted = ["accept", "content-type", "x-dv10-csrf", "idempotency-key", "x-dv10-consent"]
                for (name, value) in headers {
                    guard permitted.contains(name.lowercased()), let text = value as? String,
                          text.utf8.count <= 1024, !text.contains("\r"), !text.contains("\n") else { throw AccountError.invalidPath }
                    request.setValue(text, forHTTPHeaderField: name)
                }
                if let encoded = call.getString("bodyBase64") {
                    guard !publicRead, encoded.utf8.count <= 11184812, let data = Data(base64Encoded: encoded), data.count <= 8388608 else { throw AccountError.invalidResponse }
                    request.httpBody = data
                }
                let (data, response) = try await guestSession.data(for: request)
                guard let http = response as? HTTPURLResponse, http.url?.host == origin.host,
                      data.count <= 33554432 else { throw AccountError.invalidResponse }
                if !publicRead { try persistGuestCookie(origin: origin) }
                var responseHeaders: [String: String] = [:]
                for name in ["Content-Type", "Content-Disposition", "Cache-Control", "ETag", "Retry-After"] {
                    if let value = http.value(forHTTPHeaderField: name) { responseHeaders[name.lowercased()] = value }
                }
                call.resolve(["status": http.statusCode, "headers": responseHeaders, "bodyBase64": data.base64EncodedString()])
            } catch { call.reject(error.localizedDescription) }
        }
    }

    @objc public func shareGuestFile(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            do {
                let name = call.getString("filename") ?? ""
                let type = call.getString("mediaType") ?? ""
                let permitted = ["image/png": "png", "image/jpeg": "jpg", "image/webp": "webp", "image/svg+xml": "svg", "text/plain": "txt", "application/zip": "zip"]
                guard let suffix = permitted[type], !name.isEmpty, name.count <= 160,
                      !name.contains("/"), !name.contains("\\"), !name.contains(".."), !name.contains(":"),
                      !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
                      name.lowercased().hasSuffix("." + suffix), let encoded = call.getString("bodyBase64"),
                      encoded.utf8.count <= 44739244, let data = Data(base64Encoded: encoded), data.count <= 33554432,
                      let view = self.bridge?.viewController else { throw AccountError.invalidResponse }
                let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
                try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                let url = directory.appendingPathComponent(name)
                try data.write(to: url, options: [.atomic, .completeFileProtection])
                let sheet = UIActivityViewController(activityItems: [url], applicationActivities: nil)
                sheet.popoverPresentationController?.sourceView = view.view
                sheet.popoverPresentationController?.sourceRect = CGRect(x: view.view.bounds.midX, y: view.view.bounds.midY, width: 0, height: 0)
                sheet.completionWithItemsHandler = { _, completed, _, _ in
                    try? FileManager.default.removeItem(at: directory)
                    call.resolve(["completed": completed])
                }
                view.present(sheet, animated: true)
            } catch { call.reject(error.localizedDescription) }
        }
    }

    internal static func allowedGuest(path: String, method: String) -> Bool {
        AccountTransportPolicy.guest(path: path, method: method)
    }

    private func guestKeychain(origin: URL) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "DildoVander.GuestCookie.v1", kSecAttrAccount as String: origin.absoluteString]
    }
    private func restoreGuestCookies(origin: URL) {
        guard !guestCookiesRestored else { return }; guestCookiesRestored = true
        var query = guestKeychain(origin: origin); query[kSecReturnData as String] = true
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data,
              let properties = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else { return }
        let converted = Dictionary(uniqueKeysWithValues: properties.map { (HTTPCookiePropertyKey($0.key), $0.value) })
        if let cookie = HTTPCookie(properties: converted), cookie.name == "__Host-dv10", cookie.isSecure,
           cookie.domain == origin.host, cookie.path == "/" { guestSession.configuration.httpCookieStorage?.setCookie(cookie) }
    }
    private func persistGuestCookie(origin: URL) throws {
        let query = guestKeychain(origin: origin)
        guard let cookie = guestSession.configuration.httpCookieStorage?.cookies(for: origin)?.first(where: { $0.name == "__Host-dv10" && $0.domain == origin.host && $0.isSecure }),
              let properties = cookie.properties else { SecItemDelete(query as CFDictionary); return }
        let converted = Dictionary(uniqueKeysWithValues: properties.map { ($0.key.rawValue, $0.value) })
        let data = try PropertyListSerialization.data(fromPropertyList: converted, format: .binary, options: 0)
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var attributes = query; attributes[kSecValueData as String] = data
            attributes[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
            guard SecItemAdd(attributes as CFDictionary, nil) == errSecSuccess else { throw AccountError.storageFailed }
        } else if status != errSecSuccess { throw AccountError.storageFailed }
    }

    @objc public func request(_ call: CAPPluginCall) {
        Task { @MainActor in
            guard !requestActive && pending == nil else { call.reject("Probíhá jiné ověření účtu."); return }
            requestActive = true
            defer { requestActive = false }
            do {
                let path = call.getString("path") ?? ""
                let method = call.getString("method") ?? "GET"
                guard Self.allowed(path: path, method: method) else { throw AccountError.invalidPath }
                let result = try await send(path: path, method: method, body: call.getString("body"), confirmed: call.getBool("confirmed") == true)
                if path == "/app/v1/session", let data = result.1.data(using: .utf8),
                   let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] {
                    csrf = object["csrfToken"] as? String ?? ""
                }
                call.resolve(["status": result.0, "body": result.1])
            } catch { call.reject(error.localizedDescription) }
        }
    }

    @objc public func passkey(_ call: CAPPluginCall) {
        Task { @MainActor in
            guard !requestActive && pending == nil else { call.reject("Probíhá jiné ověření účtu."); return }
            pending = call
            do {
                let config = try configuration()
                let sessionResult = try await send(path: "/app/v1/session", method: "GET")
                let sessionObject = try json(sessionResult)
                csrf = sessionObject["csrfToken"] as? String ?? ""
                let mode = call.getString("mode") ?? ""
                let optionsPath: String
                var body: String?
                switch mode {
                case "reauth": optionsPath = "/app/v1/passkeys/reauthentication-options"; completionPath = "/app/v1/passkeys/reauthenticate"
                case "signin": optionsPath = "/app/v1/passkeys/request-options"; completionPath = "/app/v1/passkeys/sign-in"
                case "enroll":
                    optionsPath = "/app/v1/enrollment/options"; completionPath = "/app/v1/enrollment/complete"
                    body = try encode(["token": call.getString("token") ?? "", "displayName": call.getString("displayName") ?? ""])
                case "add": optionsPath = "/app/v1/passkeys/options"; completionPath = "/app/v1/passkeys/complete"
                default: throw AccountError.invalidPath
                }
                let options = try json(await send(path: optionsPath, method: "POST", body: body))
                guard let challengeValue = options["challenge"] as? String, let challenge = Self.decode(challengeValue) else { throw AccountError.invalidResponse }
                let provider = ASAuthorizationPlatformPublicKeyCredentialProvider(relyingPartyIdentifier: config.1)
                let request: ASAuthorizationRequest
                if mode == "signin" || mode == "reauth" {
                    guard let rp = options["rpId"] as? String, rp == config.1 else { throw AccountError.invalidResponse }
                    let assertion = provider.createCredentialAssertionRequest(challenge: challenge)
                    assertion.userVerificationPreference = .required
                    request = assertion
                } else {
                    guard let rp = options["rp"] as? [String: Any], rp["id"] as? String == config.1,
                          let user = options["user"] as? [String: Any], let id = user["id"] as? String,
                          let userId = Self.decode(id), let name = user["name"] as? String else { throw AccountError.invalidResponse }
                    let registration = provider.createCredentialRegistrationRequest(challenge: challenge, name: name, userID: userId)
                    registration.displayName = user["displayName"] as? String
                    registration.userVerificationPreference = .required
                    request = registration
                }
                let authorization = ASAuthorizationController(authorizationRequests: [request])
                authorization.delegate = self
                authorization.presentationContextProvider = self
                controller = authorization
                authorization.performRequests()
            } catch { finish(error: error.localizedDescription) }
        }
    }

    public func presentationAnchor(for controller: ASAuthorizationController) -> ASPresentationAnchor {
        return bridge?.viewController?.view.window ?? ASPresentationAnchor()
    }

    public func authorizationController(controller: ASAuthorizationController, didCompleteWithAuthorization authorization: ASAuthorization) {
        Task { @MainActor in
            do {
                let credential: [String: Any]
                if let assertion = authorization.credential as? ASAuthorizationPlatformPublicKeyCredentialAssertion {
                    credential = ["id": Self.encode(assertion.credentialID), "rawId": Self.encode(assertion.credentialID), "type": "public-key",
                        "authenticatorAttachment": "platform", "clientExtensionResults": [:], "response": [
                            "clientDataJSON": Self.encode(assertion.rawClientDataJSON), "authenticatorData": Self.encode(assertion.rawAuthenticatorData),
                            "signature": Self.encode(assertion.signature), "userHandle": Self.encode(assertion.userID)]]
                } else if let registration = authorization.credential as? ASAuthorizationPlatformPublicKeyCredentialRegistration,
                          let attestation = registration.rawAttestationObject {
                    credential = ["id": Self.encode(registration.credentialID), "rawId": Self.encode(registration.credentialID), "type": "public-key",
                        "authenticatorAttachment": "platform", "clientExtensionResults": [:], "response": [
                            "clientDataJSON": Self.encode(registration.rawClientDataJSON), "attestationObject": Self.encode(attestation), "transports": ["internal"]]]
                } else { throw AccountError.invalidResponse }
                let result = try await send(path: completionPath, method: "POST", body: encode(["credential": credential]))
                guard result.0 == 204 else { throw AccountError.verificationFailed }
                finish(error: nil)
            } catch { finish(error: error.localizedDescription) }
        }
    }

    public func authorizationController(controller: ASAuthorizationController, didCompleteWithError error: Error) {
        finish(error: "Ověření přístupovým klíčem bylo zrušeno nebo není na tomto zařízení dostupné.")
    }

    private func finish(error: String?) {
        let call = pending
        pending = nil; controller = nil; completionPath = ""
        if let error = error { call?.reject(error) } else { call?.resolve() }
    }

    private func configuration() throws -> (URL, String) {
        guard let raw = Bundle.main.object(forInfoDictionaryKey: "KufrAccountOrigin") as? String,
              let url = URL(string: raw), url.scheme == "https", let host = url.host, url.user == nil, url.password == nil,
              url.path.isEmpty, url.query == nil, url.fragment == nil,
              let rp = Bundle.main.object(forInfoDictionaryKey: "KufrPasskeyRelyingParty") as? String,
              !rp.isEmpty, (host == rp || host.hasSuffix("." + rp)), !rp.contains("$") else { throw AccountError.notConfigured }
        return (url, rp)
    }

    private func send(path: String, method: String, body: String? = nil, confirmed: Bool = false) async throws -> (Int, String) {
        guard Self.allowed(path: path, method: method) else { throw AccountError.invalidPath }
        let (origin, _) = try configuration()
        restoreCookies(origin: origin)
        guard let url = URL(string: path, relativeTo: origin)?.absoluteURL,
              url.host == origin.host, url.scheme == "https", url.port == origin.port else { throw AccountError.invalidPath }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(origin.absoluteString, forHTTPHeaderField: "Origin")
        if method != "GET" { request.setValue(csrf, forHTTPHeaderField: "X-DV-CSRF") }
        if confirmed { request.setValue("yes", forHTTPHeaderField: "X-DV-Confirm") }
        if let body = body {
            guard body.utf8.count <= 65536 else { throw AccountError.invalidResponse }
            request.httpBody = Data(body.utf8)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, data.count <= 1048576,
              http.url?.host == origin.host else { throw AccountError.invalidResponse }
        try persistAccountCookie(origin: origin)
        return (http.statusCode, String(data: data, encoding: .utf8) ?? "")
    }

    // Exact route grammar prevents a compromised view from turning this cookie
    // bridge into an arbitrary URL, guest, firmware, or server-admin proxy.
    internal static func allowed(path: String, method: String) -> Bool {
        AccountTransportPolicy.account(path: path, method: method)
    }

    private func json(_ response: (Int, String)) throws -> [String: Any] {
        guard response.0 == 200, let data = response.1.data(using: .utf8),
              let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw AccountError.verificationFailed }
        return object
    }
    private func encode(_ value: [String: Any]) throws -> String { String(decoding: try JSONSerialization.data(withJSONObject: value), as: UTF8.self) }
    private static func encode(_ data: Data) -> String { data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "") }
    private static func decode(_ value: String) -> Data? {
        let base = value.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        return Data(base64Encoded: base + String(repeating: "=", count: (4 - base.count % 4) % 4))
    }

    private func keychainQuery(origin: URL) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "DildoVander.AccountCookie.v1", kSecAttrAccount as String: origin.absoluteString]
    }
    private func restoreCookies(origin: URL) {
        guard !restoredCookies else { return }; restoredCookies = true
        var query = keychainQuery(origin: origin); query[kSecReturnData as String] = true
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data,
              let properties = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else { return }
        let converted = Dictionary(uniqueKeysWithValues: properties.map { (HTTPCookiePropertyKey($0.key), $0.value) })
        if let cookie = HTTPCookie(properties: converted), cookie.isSecure, cookie.name == "__Host-dv-account",
           cookie.domain == origin.host, cookie.path == "/" { session.configuration.httpCookieStorage?.setCookie(cookie) }
    }
    private func persistAccountCookie(origin: URL) throws {
        let query = keychainQuery(origin: origin)
        let cookies = session.configuration.httpCookieStorage?.cookies(for: origin) ?? []
        guard let cookie = cookies.first(where: { $0.name == "__Host-dv-account" && $0.domain == origin.host && $0.isSecure }),
              let properties = cookie.properties else { SecItemDelete(query as CFDictionary); publishCookieChange(nil); return }
        let converted = Dictionary(uniqueKeysWithValues: properties.map { ($0.key.rawValue, $0.value) })
        let data = try PropertyListSerialization.data(fromPropertyList: converted, format: .binary, options: 0)
        var attributes = query; attributes[kSecValueData as String] = data
        attributes[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        let result = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if result == errSecItemNotFound {
            guard SecItemAdd(attributes as CFDictionary, nil) == errSecSuccess else { throw AccountError.storageFailed }
        } else if result != errSecSuccess { throw AccountError.storageFailed }
        publishCookieChange(cookie.value)
    }

    private func publishCookieChange(_ cookie: String?) {
        let digest = cookie.map { SHA256.hash(data: Data($0.utf8)).map { String(format: "%02x", $0) }.joined() }
        guard digest != publishedCookieDigest else { return }
        publishedCookieDigest = digest
        NotificationCenter.default.post(name: Notification.Name("KufrIdentityAccountChanged"), object: nil)
    }
}

private final class AccountNoRedirect: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}
private enum AccountError: LocalizedError {
    case notConfigured, invalidPath, invalidResponse, verificationFailed, storageFailed
    var errorDescription: String? {
        switch self {
        case .notConfigured: return "Tato instalace nemá nastavenou doménu účtu a přístupových klíčů. Správce musí připravit podepsanou aplikaci s Associated Domains."
        case .invalidPath: return "Nepovolená operace účtu."
        case .invalidResponse: return "Neplatná odpověď serveru účtu."
        case .verificationFailed: return "Přístup se nepodařilo ověřit. Zkontrolujte platnost pozvánky a konfiguraci domény."
        case .storageFailed: return "Přístup účtu se nepodařilo bezpečně uložit."
        }
    }
}
