import AuthenticationServices
import CryptoKit
import Capacitor
import Foundation
import Security
import UIKit

// AuthenticationServices performs native WebAuthn. URLSession owns a separate
// cookie jar: browser/WKWebView cookies and device credentials never enter it.
@objc(KufrIdentityPlugin)
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
            // A restored native device profile may need its first CSRF token.
            // This cookie-writing bootstrap shares admission with ordinary UI.
            let lease = try await requestScheduler.acquire()
            defer { requestScheduler.release(lease) }
            try requestScheduler.requireCurrent(lease)
            guard (try? accountDeviceBinding()) == binding else { throw AccountRequestScheduler.Failure.contextChanged }
            if csrf.isEmpty {
                let current = try json(await send(path: "/app/v1/session", method: "GET"))
                try requestScheduler.requireCurrent(lease)
                guard current["account"] as? [String: Any] != nil else { throw AccountError.verificationFailed }
                guard (try? accountDeviceBinding()) == binding else { throw AccountRequestScheduler.Failure.contextChanged }
                try publishSessionResponse(current, origin: origin)
            }
        }
        guard try accountDeviceBinding() == binding else { throw AccountError.verificationFailed }
        let permitted = ["accept", "content-type", "idempotency-key", "client-turn-id", "boot-id", "generation", "device-revision", "catalog-version", "input-id", "input-sequence", "captured-at", "image-id", "image-source", "image-revision", "image-consent", "x-dv-confirm"]
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
        if request.httpMethod != "GET" || url.path.hasSuffix("/audio-stream") { request.setValue(csrf, forHTTPHeaderField: "X-DV-CSRF") }
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
        CAPPluginMethod(name: "accountTransport", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "publicOrigin", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "publicResource", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "cancelPublicResource", returnType: CAPPluginReturnPromise)
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
    @MainActor private lazy var requestScheduler = AccountRequestScheduler()
    private var passkeyQueued = false
    private var ceremonyLease: AccountRequestScheduler.Lease?
    private var credentialCompletionActive = false
    private let prioritySession: URLSession = {
        let config = AccountPriorityHTTP.configuration()
        return URLSession(configuration: config, delegate: AccountNoRedirect(), delegateQueue: nil)
    }()
    private var publishedCookieDigest: String?
    private var publishedPrioritySession: AccountPrioritySessionSnapshot?
    private let guestSession: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieAcceptPolicy = .always
        configuration.timeoutIntervalForRequest = 60
        return URLSession(configuration: configuration, delegate: AccountNoRedirect(), delegateQueue: nil)
    }()
    private lazy var publicGuestSession: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.httpShouldSetCookies = false
        config.urlCache = nil; config.urlCredentialStorage = nil
        config.timeoutIntervalForRequest = 60
        return URLSession(configuration: config, delegate: AccountNoRedirect(), delegateQueue: nil)
    }()
    private var guestCookiesRestored = false
    private var publicTransfers: [UUID: PublicResourceTransfer] = [:]
    private var publicCancellationWaiters: [UUID: [CAPPluginCall]] = [:]

    @objc public func publicOrigin(_ call: CAPPluginCall) {
        do { call.resolve(["origin": try configuredPublicOrigin().absoluteString]) }
        catch { call.reject(error.localizedDescription) }
    }

    @objc public func publicResource(_ call: CAPPluginCall) {
        Task { @MainActor in
            do {
                let origin = try configuredPublicOrigin()
                guard let path = call.getString("path"), let policyLimit = AccountTransportPolicy.publicResourceLimit(path: path),
                      let requestedLimit = call.getInt("maxBytes"), requestedLimit > 0, requestedLimit <= 96 * 1024 * 1024,
                      let rawId = call.getString("requestId"), let id = UUID(uuidString: rawId),
                      let url = URL(string: path, relativeTo: origin)?.absoluteURL,
                      url.scheme == origin.scheme, url.host == origin.host, url.port == origin.port,
                      url.user == nil, url.password == nil, url.query == nil, url.fragment == nil else { throw PublicResourceError.invalidRequest }
                // A single bounded buffer also bounds concurrent base64 bridge work.
                guard publicTransfers.isEmpty else { throw PublicResourceError.busy }
                let transfer = PublicResourceTransfer(url: url, limit: min(policyLimit, requestedLimit)) { [weak self] result in
                    Task { @MainActor in
                        guard let self = self else { call.reject(PublicResourceError.cancelled.localizedDescription); return }
                        self.publicTransfers.removeValue(forKey: id)
                        switch result {
                        case .success(let (data, response)):
                            call.resolve(["status": response.statusCode,
                                "headers": ["content-type": response.value(forHTTPHeaderField: "Content-Type") ?? "application/json", "content-length": String(data.count)],
                                "bodyBase64": data.base64EncodedString()])
                        case .failure(let error): call.reject(error.localizedDescription)
                        }
                        for waiter in self.publicCancellationWaiters.removeValue(forKey: id) ?? [] { waiter.resolve() }
                    }
                }
                publicTransfers[id] = transfer
                transfer.start()
            } catch { call.reject(error.localizedDescription) }
        }
    }

    @objc public func cancelPublicResource(_ call: CAPPluginCall) {
        Task { @MainActor in
            guard let rawId = call.getString("requestId"), let id = UUID(uuidString: rawId) else { call.reject(PublicResourceError.invalidRequest.localizedDescription); return }
            guard let transfer = publicTransfers[id] else { call.resolve(); return }
            guard (publicCancellationWaiters[id]?.count ?? 0) < 8 else { call.reject(PublicResourceError.busy.localizedDescription); return }
            publicCancellationWaiters[id, default: []].append(call)
            transfer.cancel()
        }
    }

    @objc public func accountTransport(_ call: CAPPluginCall) {
        Task { @MainActor in
            do {
                let (origin, _) = try configuration()
                let path = call.getString("path") ?? "", method = call.getString("method") ?? "GET"
                guard Self.allowed(path: path, method: method), let url = URL(string: path, relativeTo: origin)?.absoluteURL,
                      url.scheme == "https", url.host == origin.host, url.port == origin.port else { throw AccountError.invalidPath }
                var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData)
                request.httpMethod = method
                request.setValue(origin.absoluteString, forHTTPHeaderField: "Origin")
                let permitted = ["accept", "content-type", "x-dv-csrf", "x-dv-confirm", "x-dv-device-id", "idempotency-key", "session-id", "boot-id", "media-epoch", "input-epoch", "input-sequence"]
                for (name, value) in call.getObject("headers") ?? [:] {
                    guard permitted.contains(name.lowercased()), let text = value as? String, text.count <= 1024,
                          !text.contains("\r"), !text.contains("\n") else { throw AccountError.invalidPath }
                    if name.lowercased() != "x-dv-csrf" { request.setValue(text, forHTTPHeaderField: name) }
                }
                if let encoded = call.getString("bodyBase64") {
                    guard encoded.utf8.count <= 349528, let data = Data(base64Encoded: encoded), data.count <= 262144 else { throw AccountError.invalidResponse }
                    request.httpBody = data
                }
                let device = request.value(forHTTPHeaderField: "X-DV-Device-Id") ?? ""
                let result: (Data, URLResponse)
                var ordinaryLease: AccountRequestScheduler.Lease?
                defer { if let lease = ordinaryLease { requestScheduler.release(lease) } }
                let priority = AccountTransportPolicy.priorityCancellation(path: path, method: method, deviceId: device, body: request.httpBody ?? Data())
                if priority {
                    result = try await sendPriorityCancellation(request)
                } else {
                    let commit = AccountTransportPolicy.credentialCommit(path: path, method: method)
                    let lease = try await requestScheduler.acquire(ceremony: commit, logout: path == "/app/v1/session" && method == "DELETE")
                    ordinaryLease = lease
                    try requestScheduler.requireCurrent(lease)
                    if commit { try await requestScheduler.beginCredentialCommit(lease) }
                    restoreCookies(origin: origin)
                    if method != "GET" { request.setValue(csrf, forHTTPHeaderField: "X-DV-CSRF") }
                    result = try await session.data(for: request)
                }
                let archiveAsset = path.hasPrefix("/app/v1/archive/associations/") && path.contains("/assets/")
                guard let http = result.1 as? HTTPURLResponse, result.0.count <= (archiveAsset ? 33554432 : 1048576),
                      http.url?.host == origin.host, http.url?.scheme == origin.scheme, http.url?.port == origin.port else { throw AccountError.invalidResponse }
                if !priority { try persistAccountCookie(origin: origin) }
                var headers: [String: String] = [:]
                for name in ["Content-Type", "X-Twin-Media-Accepted-Bytes", "Retry-After"] {
                    if let value = http.value(forHTTPHeaderField: name) { headers[name.lowercased()] = value }
                }
                call.resolve(["status": http.statusCode, "headers": headers, "bodyBase64": result.0.base64EncodedString()])
            } catch { rejectAccountCall(call, error: error) }
        }
    }

    @MainActor private func sendPriorityCancellation(_ original: URLRequest) async throws -> (Data, URLResponse) {
        // URLSession can apply a completion response's Set-Cookie before the
        // MainActor validates it. Only the immutable published principal is safe
        // here; never read the mutable jar or fetch CSRF during a ceremony.
        guard let snapshot = publishedPrioritySession else { throw AccountError.verificationFailed }
        let lease = try await requestScheduler.acquirePriority()
        defer { requestScheduler.releasePriority(lease) }
        try requestScheduler.requireCurrent(lease)
        guard publishedPrioritySession?.binding == snapshot.binding else { throw AccountRequestScheduler.Failure.contextChanged }
        let result = try await prioritySession.data(for: snapshot.authorize(original))
        try requestScheduler.requireCurrent(lease)
        guard publishedPrioritySession?.binding == snapshot.binding else { throw AccountRequestScheduler.Failure.contextChanged }
        return result
    }

    @MainActor private func publishSessionResponse(_ object: [String: Any], origin: URL) throws {
        guard object["account"] as? [String: Any] != nil, let token = object["csrfToken"] as? String, !token.isEmpty else {
            csrf = ""; publishedPrioritySession = nil; return
        }
        csrf = token
        let binding = try accountDeviceBinding()
        let cookies = session.configuration.httpCookieStorage?.cookies(for: origin) ?? []
        publishedPrioritySession = AccountPrioritySessionSnapshot(binding: binding, csrf: token,
            cookieHeaders: HTTPCookie.requestHeaderFields(with: cookies))
    }

    private func rejectAccountCall(_ call: CAPPluginCall, error: Error) {
        if let scheduling = error as? AccountRequestScheduler.Failure {
            call.reject(scheduling.localizedDescription, scheduling.rawValue)
        } else { call.reject(error.localizedDescription) }
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
                let publicRead = !AccountTransportPolicy.guestUsesCookies(path: path)
                // Public browsing must not reveal a guest identity to the server.
                request.httpShouldHandleCookies = !publicRead
                if !publicRead { restoreGuestCookies(origin: origin) }
                let headers = call.getObject("headers") ?? [:]
                for (name, value) in headers {
                    guard let text = value as? String,
                          AccountTransportPolicy.guestHeader(path: path, method: method, name: name, value: text) else { throw AccountError.invalidPath }
                    request.setValue(text, forHTTPHeaderField: name)
                }
                if let encoded = call.getString("bodyBase64") {
                    guard method != "GET", encoded.utf8.count <= 11184812, let data = Data(base64Encoded: encoded),
                          data.count <= (publicRead ? 16384 : 8388608) else { throw AccountError.invalidResponse }
                    request.httpBody = data
                }
                let (data, response) = try await (publicRead ? publicGuestSession : guestSession).data(for: request)
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
            do {
                let path = call.getString("path") ?? ""
                let method = call.getString("method") ?? "GET"
                guard Self.allowed(path: path, method: method) else { throw AccountError.invalidPath }
                let commit = AccountTransportPolicy.credentialCommit(path: path, method: method)
                let lease = try await requestScheduler.acquire(ceremony: commit, logout: path == "/app/v1/session" && method == "DELETE")
                defer { requestScheduler.release(lease) }
                try requestScheduler.requireCurrent(lease)
                if commit { try await requestScheduler.beginCredentialCommit(lease) }
                let result = try await send(path: path, method: method, body: call.getString("body"), confirmed: call.getBool("confirmed") == true)
                if path == "/app/v1/session", let data = result.1.data(using: .utf8),
                   let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] {
                    try publishSessionResponse(object, origin: configuration().0)
                }
                call.resolve(["status": result.0, "body": result.1])
            } catch { rejectAccountCall(call, error: error) }
        }
    }

    @objc public func passkey(_ call: CAPPluginCall) {
        Task { @MainActor in
            guard #available(iOS 16.0, *) else { call.reject("Přístupové klíče vyžadují iOS 16 nebo novější. Veřejný obsah zůstává dostupný."); return }
            guard !passkeyQueued && pending == nil else { call.reject("Probíhá jiné ověření účtu."); return }
            passkeyQueued = true
            let lease: AccountRequestScheduler.Lease
            do { lease = try await requestScheduler.acquire(ceremony: true) }
            catch { passkeyQueued = false; rejectAccountCall(call, error: error); return }
            passkeyQueued = false
            ceremonyLease = lease; pending = call
            do {
                try requestScheduler.requireCurrent(lease)
                let config = try configuration()
                let sessionResult = try await send(path: "/app/v1/session", method: "GET")
                try requestScheduler.requireCurrent(lease)
                let sessionObject = try json(sessionResult)
                try publishSessionResponse(sessionObject, origin: config.0)
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
                try requestScheduler.requireCurrent(lease)
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
            } catch { finish(error: error.localizedDescription, expected: lease) }
        }
    }

    public func presentationAnchor(for controller: ASAuthorizationController) -> ASPresentationAnchor {
        return bridge?.viewController?.view.window ?? ASPresentationAnchor()
    }

    public func authorizationController(controller: ASAuthorizationController, didCompleteWithAuthorization authorization: ASAuthorization) {
        Task { @MainActor in
            guard controller === self.controller, pending != nil, !credentialCompletionActive,
                  let lease = ceremonyLease else { return }
            credentialCompletionActive = true
            guard #available(iOS 16.0, *) else { finish(error: "Přístupové klíče vyžadují iOS 16 nebo novější.", expected: lease); return }
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
                try await requestScheduler.beginCredentialCommit(lease)
                let result = try await send(path: completionPath, method: "POST", body: encode(["credential": credential]))
                guard result.0 == 204 else { throw AccountError.verificationFailed }
                finish(error: nil, expected: lease)
            } catch { finish(error: error.localizedDescription, expected: lease) }
        }
    }

    public func authorizationController(controller: ASAuthorizationController, didCompleteWithError error: Error) {
        Task { @MainActor in
            guard controller === self.controller, !credentialCompletionActive, let lease = ceremonyLease else { return }
            finish(error: "Ověření přístupovým klíčem bylo zrušeno nebo není na tomto zařízení dostupné.", expected: lease)
        }
    }

    @MainActor private func finish(error: String?, expected: AccountRequestScheduler.Lease) {
        guard ceremonyLease?.id == expected.id else { return }
        let call = pending
        pending = nil; controller = nil; completionPath = ""; ceremonyLease = nil; credentialCompletionActive = false
        if let error = error { call?.reject(error) } else { call?.resolve() }
        requestScheduler.release(expected)
    }

    private func configuration() throws -> (URL, String) {
        guard let raw = Bundle.main.object(forInfoDictionaryKey: "KufrAccountOrigin") as? String,
              let url = URL(string: raw), url.scheme == "https", let host = url.host, url.user == nil, url.password == nil,
              url.path.isEmpty, url.query == nil, url.fragment == nil,
              let rp = Bundle.main.object(forInfoDictionaryKey: "KufrPasskeyRelyingParty") as? String,
              !rp.isEmpty, (host == rp || host.hasSuffix("." + rp)), !rp.contains("$") else { throw AccountError.notConfigured }
        return (url, rp)
    }

    private func configuredPublicOrigin() throws -> URL {
        guard let raw = Bundle.main.object(forInfoDictionaryKey: "KufrAccountOrigin") as? String,
              let origin = AccountTransportPolicy.publicOrigin(raw) else { throw PublicResourceError.notConfigured }
        return origin
    }

    @MainActor private func send(path: String, method: String, body: String? = nil, confirmed: Bool = false) async throws -> (Int, String) {
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
    @MainActor private func persistAccountCookie(origin: URL) throws {
        let query = keychainQuery(origin: origin)
        let cookies = session.configuration.httpCookieStorage?.cookies(for: origin) ?? []
        guard let cookie = cookies.first(where: { $0.name == "__Host-dv-account" && $0.domain == origin.host && $0.isSecure }),
              let properties = cookie.properties else { SecItemDelete(query as CFDictionary); publishCookieChange(nil, origin: origin); return }
        let converted = Dictionary(uniqueKeysWithValues: properties.map { ($0.key.rawValue, $0.value) })
        let data = try PropertyListSerialization.data(fromPropertyList: converted, format: .binary, options: 0)
        var attributes = query; attributes[kSecValueData as String] = data
        attributes[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        let result = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if result == errSecItemNotFound {
            guard SecItemAdd(attributes as CFDictionary, nil) == errSecSuccess else { throw AccountError.storageFailed }
        } else if result != errSecSuccess { throw AccountError.storageFailed }
        publishCookieChange(cookie.value, origin: origin)
    }

    @MainActor private func publishCookieChange(_ cookie: String?, origin: URL) {
        let digest = cookie.map { SHA256.hash(data: Data($0.utf8)).map { String(format: "%02x", $0) }.joined() }
        guard digest != publishedCookieDigest else { return }
        if digest == nil || requestScheduler.credentialCommitActive {
            publishedPrioritySession = nil
            if digest == nil { csrf = "" }
            requestScheduler.invalidateQueuedScope()
        } else if let snapshot = publishedPrioritySession, let binding = digest {
            // Ordinary renewal keeps the same authenticated session. It does
            // not obsolete FIFO jobs or turn a healthy refresh into logout.
            let cookies = session.configuration.httpCookieStorage?.cookies(for: origin) ?? []
            publishedPrioritySession = AccountPrioritySessionSnapshot(binding: binding, csrf: snapshot.csrf,
                cookieHeaders: HTTPCookie.requestHeaderFields(with: cookies))
        }
        publishedCookieDigest = digest
        NotificationCenter.default.post(name: Notification.Name("KufrIdentityAccountChanged"), object: nil)
    }
}

// Own session per transfer: no account/guest jar, shared URL credentials, cache,
// redirect handling or unbounded data(for:) buffer can enter public downloads.
private final class PublicResourceTransfer: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let url: URL
    private let limit: Int
    private var completion: ((Result<(Data, HTTPURLResponse), Error>) -> Void)?
    private var ownedSession: URLSession?
    private var task: URLSessionDataTask?
    private var response: HTTPURLResponse?
    private var body = Data()
    private var failure: Error?

    init(url: URL, limit: Int, completion: @escaping (Result<(Data, HTTPURLResponse), Error>) -> Void) {
        self.url = url; self.limit = limit; self.completion = completion
    }

    func start() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.httpCookieAcceptPolicy = .never
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 30
        configuration.timeoutIntervalForResource = 60
        let queue = OperationQueue(); queue.maxConcurrentOperationCount = 1
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: queue)
        ownedSession = session
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData)
        request.httpMethod = "GET"
        request.httpShouldHandleCookies = false
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("no-store", forHTTPHeaderField: "Cache-Control")
        task = session.dataTask(with: request)
        task?.resume()
    }

    func cancel() { task?.cancel() }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let http = response as? HTTPURLResponse, http.url == url, (200...599).contains(http.statusCode),
              !(300...399).contains(http.statusCode),
              AccountTransportPolicy.publicResourceMIME(http.value(forHTTPHeaderField: "Content-Type"), path: url.path),
              response.expectedContentLength <= Int64(limit) else {
            failure = PublicResourceError.invalidResponse; completionHandler(.cancel); return
        }
        self.response = http
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard failure == nil else { return }
        guard body.count <= limit, data.count <= limit - body.count else {
            failure = PublicResourceError.tooLarge; dataTask.cancel(); return
        }
        body.append(data)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let completion = completion else { return }
        self.completion = nil
        let result: Result<(Data, HTTPURLResponse), Error>
        if let failure = failure { result = .failure(failure) }
        else if let error = error {
            result = .failure((error as NSError).code == NSURLErrorCancelled ? PublicResourceError.cancelled : error)
        } else if let response = response { result = .success((body, response)) }
        else { result = .failure(PublicResourceError.invalidResponse) }
        body = Data()
        ownedSession?.finishTasksAndInvalidate(); ownedSession = nil
        completion(result)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        failure = PublicResourceError.invalidResponse; completionHandler(nil)
    }

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        if challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust {
            completionHandler(.performDefaultHandling, nil)
        } else { completionHandler(.cancelAuthenticationChallenge, nil) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        if challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust {
            completionHandler(.performDefaultHandling, nil)
        } else { completionHandler(.cancelAuthenticationChallenge, nil) }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, willCacheResponse proposedResponse: CachedURLResponse,
                    completionHandler: @escaping (CachedURLResponse?) -> Void) { completionHandler(nil) }
}

private enum PublicResourceError: LocalizedError {
    case notConfigured, invalidRequest, invalidResponse, tooLarge, busy, cancelled
    var errorDescription: String? {
        switch self {
        case .notConfigured: return "Veřejná HTTPS adresa této instalace není nakonfigurovaná."
        case .invalidRequest: return "Nepovolený veřejný prostředek."
        case .invalidResponse: return "Server nevrátil povolený veřejný JSON prostředek."
        case .tooLarge: return "Veřejný prostředek překročil povolenou velikost."
        case .busy: return "Ještě probíhá načítání předchozího veřejného prostředku."
        case .cancelled: return "Načítání veřejného prostředku bylo zrušeno."
        }
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
