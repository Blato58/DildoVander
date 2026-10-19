import Foundation
import Security

enum DebugBackendError: Error {
    case invalidProfile, invalidResponse, httpStatus, tooLarge, wrongMode, timeout
    case serverCode(String)
}

struct DebugBackendProfile {
    let baseURL: URL
    let deviceId: String
    let credential: String
    // Opaque native-only scope, never a bearer credential or JavaScript field.
    let accountBinding: String?
    var usesAccountSession: Bool { accountBinding != nil }

#if !os(iOS)
    // Foundation-only macOS harness uses a synthetic loopback HTTP peer.
    init(baseURL: URL, deviceId: String, credential: String) {
        precondition(baseURL.scheme == "http" && baseURL.host == "127.0.0.1")
        self.baseURL = baseURL
        self.deviceId = deviceId
        self.credential = credential
        self.accountBinding = nil
    }
#endif

    init(address: String, deviceId: String, credential: String) throws {
        guard let url = URL(string: address), let host = url.host, !host.isEmpty,
              url.scheme == "https", url.user == nil, url.password == nil,
              url.query == nil, url.fragment == nil, ["", "/"].contains(url.path),
              deviceId.hasPrefix("debug-"), deviceId.count <= 128,
              deviceId.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }),
              !credential.isEmpty, credential.utf8.count <= 256,
              credential.unicodeScalars.allSatisfy({ $0.value > 32 && $0.value < 127 }) else {
            throw DebugBackendError.invalidProfile
        }
        self.baseURL = url
        self.deviceId = deviceId
        self.credential = credential
        self.accountBinding = nil
    }

    init(accountOrigin: URL, deviceId: String, binding: String) throws {
        guard accountOrigin.scheme == "https", let host = accountOrigin.host, !host.isEmpty,
              accountOrigin.user == nil, accountOrigin.password == nil, accountOrigin.query == nil,
              accountOrigin.fragment == nil, ["", "/"].contains(accountOrigin.path),
              !deviceId.isEmpty, deviceId.count <= 128,
              deviceId.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }),
              !binding.isEmpty, binding.count <= 256 else { throw DebugBackendError.invalidProfile }
        self.baseURL = accountOrigin
        self.deviceId = deviceId
        self.credential = ""
        self.accountBinding = binding
    }
}

// Separate service from the local ESP pairing credential. Stable app signing
// identity keeps this item available across updates; no iCloud synchronization.
final class DebugBackendKeychain {
    private struct StoredProfile: Codable {
        let address: String
        let deviceId: String
        let credential: String
    }
    private let service: String
    init(service: String = "app.turquoise6409.green2444.kufr.debug") { self.service = service }

    private var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service, kSecAttrAccount as String: "backend-profile",
         kSecAttrSynchronizable as String: false]
    }

    func read() throws -> DebugBackendProfile? {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data, data.count <= 2048,
              let stored = try? JSONDecoder().decode(StoredProfile.self, from: data) else {
            throw DebugBackendError.invalidProfile
        }
        return try DebugBackendProfile(address: stored.address, deviceId: stored.deviceId, credential: stored.credential)
    }

    func write(_ profile: DebugBackendProfile) throws {
        guard !profile.usesAccountSession else { throw DebugBackendError.invalidProfile }
        let data = try JSONEncoder().encode(StoredProfile(address: profile.baseURL.absoluteString,
            deviceId: profile.deviceId, credential: profile.credential))
        let attributes: [String: Any] = [kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly]
        let status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            var insertion = query
            attributes.forEach { insertion[$0.key] = $0.value }
            guard SecItemAdd(insertion as CFDictionary, nil) == errSecSuccess else { throw DebugBackendError.invalidProfile }
        } else if status != errSecSuccess { throw DebugBackendError.invalidProfile }
    }

    func remove() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw DebugBackendError.invalidProfile }
    }
}

enum DebugProfileDraft {
    static let defaultAddress = "https://2.28.202.220/"
    static let defaultDeviceId = "debug-server-admin"

    static func resolve(address: String, deviceId: String, credential: String,
                        saved: DebugBackendProfile?) throws -> DebugBackendProfile {
        if !credential.isEmpty { return try DebugBackendProfile(address: address, deviceId: deviceId, credential: credential) }
        // A blank field may retain a credential only for the identical server
        // and device. Editing the destination must never forward it silently.
        guard let saved = saved, deviceId == saved.deviceId,
              let destination = URL(string: address), destination.scheme == "https",
              destination.user == nil, destination.password == nil, destination.query == nil, destination.fragment == nil,
              ["", "/"].contains(destination.path), destination.host?.lowercased() == saved.baseURL.host?.lowercased(),
              (destination.port ?? 443) == (saved.baseURL.port ?? 443) else { throw DebugBackendError.invalidProfile }
        return try DebugBackendProfile(address: address, deviceId: deviceId, credential: saved.credential)
    }
}

struct DebugPersonality: Codable {
    let id: String
    let name: String
    let description: String
    let promptSha256: String
    var bridgeValue: [String: Any] {
        ["id": id, "name": name, "description": description, "promptSha256": promptSha256]
    }
}

struct DebugEngineInventory: Codable {
    struct Engine: Codable {
        let engine: String
        let model: String
        let ready: Bool
        let unavailableReason: String?
        let voices: [String]
        struct Speed: Codable { let supported: Bool; let min: Double; let max: Double; let `default`: Double }
        let speed: Speed
        let inputModes: [String]
        let cameraSources: [String]
    }
    let schemaVersion: Int
    let primaryEngine: String
    let engines: [Engine]

    func validate() throws {
        guard schemaVersion == 1, primaryEngine == "gpt-live", engines.count == 2,
              Set(engines.map { $0.engine }) == Set(["gpt-live", "realtime"]),
              engines.allSatisfy({ value in
                  value.model == (value.engine == "gpt-live" ? "gpt-live-1" : "gpt-realtime-2.1")
                  && (!value.ready || value.unavailableReason == nil)
                  && !value.voices.isEmpty && value.inputModes.contains("audio")
              }) else { throw DebugBackendError.invalidResponse }
    }

    var bridgeValue: [String: Any] {
        guard let data = try? JSONEncoder().encode(self),
              let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
        return value
    }
}

struct DebugServerModes: Codable {
    let revision: String
    let engines: DebugEngineInventory
    let activeConversations: Int
    let lifetime: String
    let personalityId: String?
    let personalities: [DebugPersonality]?
    let personalityLifetime: String?
    var characterEnabled: Bool? = nil
    var characterTone: String? = nil
    var characterFinaleProfile: String? = nil

    var bridgeValue: [String: Any] {
        ["revision": revision, "engines": engines.bridgeValue,
         "activeConversations": activeConversations, "lifetime": lifetime,
         "personalityId": personalityId as Any? ?? NSNull(),
         "personalities": personalities?.map { $0.bridgeValue } as Any? ?? NSNull(),
         "personalityLifetime": personalityLifetime as Any? ?? NSNull(),
         "characterEnabled": characterEnabled as Any? ?? NSNull(),
         "characterTone": characterTone as Any? ?? NSNull(),
         "characterFinaleProfile": characterFinaleProfile as Any? ?? NSNull()]
    }
}

struct DebugServerLogEntry: Codable {
    let id: Int64
    let timestamp: String
    let level: String
    let eventCode: String
    let traceId: String?
    let route: String?
    let method: String?
    let status: Int?
    let durationMs: Double?
    let operationId: String?
    let conversationId: String?
    let code: String?
}

struct DebugServerLogPage: Codable {
    let storeId: String
    let oldestCursor: Int64
    let latestCursor: Int64
    let nextCursor: Int64
    let hasMore: Bool
    let truncated: Bool
    let droppedWrites: Int64
    let entries: [DebugServerLogEntry]

    func validate() throws {
        guard UUID(uuidString: storeId) != nil, oldestCursor >= 0, latestCursor >= oldestCursor,
              nextCursor >= 0, nextCursor <= latestCursor, latestCursor <= 9_007_199_254_740_991,
              droppedWrites >= 0, entries.count <= 200,
              Set(entries.map { $0.id }).count == entries.count,
              entries.map({ $0.id }) == entries.map({ $0.id }).sorted(),
              (!hasMore || !entries.isEmpty && nextCursor == entries.last?.id) else { throw DebugBackendError.invalidResponse }
        let date = ISO8601DateFormatter()
        date.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        for entry in entries {
            guard entry.id >= oldestCursor, entry.id <= nextCursor,
                  entry.timestamp.utf8.count <= 64, date.date(from: entry.timestamp) != nil,
                  ["info", "warning", "error"].contains(entry.level),
                  entry.eventCode.range(of: "^[a-z_]{1,64}$", options: .regularExpression) != nil,
                  [entry.traceId, entry.operationId, entry.conversationId].allSatisfy({ $0 == nil || UUID(uuidString: $0!) != nil }),
                  entry.route == nil || entry.route!.utf8.count <= 200,
                  entry.method == nil || ["GET", "POST", "PUT", "DELETE", "OPTIONS", "OTHER"].contains(entry.method!),
                  entry.status == nil || (100...599).contains(entry.status!),
                  entry.durationMs == nil || entry.durationMs!.isFinite && entry.durationMs! >= 0,
                  entry.code == nil || entry.code!.range(of: "^[a-z_]{1,80}$", options: .regularExpression) != nil
            else { throw DebugBackendError.invalidResponse }
        }
    }

    var bridgeValue: [String: Any] {
        // Re-encode only the typed, validated metadata, excluding extra fields
        // from a future/malformed server response.
        (try? JSONSerialization.jsonObject(with: JSONEncoder().encode(self))) as? [String: Any] ?? [:]
    }
}

struct DebugEvening: Codable {
    let eveningId: String
    let name: String
    let createdAt: String
    let exchangeCount: Int
    let droppedCount: Int
    var bridgeValue: [String: Any] {
        ["eveningId": eveningId, "name": name, "createdAt": createdAt,
         "exchangeCount": exchangeCount, "droppedCount": droppedCount]
    }
}

struct DebugEveningList: Codable {
    let activeEveningId: String
    let evenings: [DebugEvening]
    var bridgeValue: [String: Any] {
        ["activeEveningId": activeEveningId, "evenings": evenings.map { $0.bridgeValue }]
    }

    func validate() throws {
        guard UUID(uuidString: activeEveningId) != nil, (1...100).contains(evenings.count),
              Set(evenings.map { $0.eveningId.lowercased() }).count == evenings.count,
              evenings.contains(where: { $0.eveningId == activeEveningId }) else { throw DebugBackendError.invalidResponse }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        for evening in evenings {
            guard UUID(uuidString: evening.eveningId) != nil, !evening.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  evening.name.unicodeScalars.count <= 80, !evening.name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
                  evening.createdAt.utf8.count <= 64,
                  (formatter.date(from: evening.createdAt) ?? ISO8601DateFormatter().date(from: evening.createdAt)) != nil,
                  (0...2000).contains(evening.exchangeCount), evening.droppedCount >= 0 else { throw DebugBackendError.invalidResponse }
        }
    }
}

// Default PKI trust is retained. Redirects must never forward a device bearer.
final class DebugNoRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

// URLSession feeds bounded chunks on its delegate queue. Per-byte iteration
// on MainActor can stall both playback preparation and the debug status UI.
final class DebugHTTPTransport: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var calls: [Int: DebugHTTPCall] = [:]
    private var session: URLSession!
    override init() {
        super.init()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.timeoutIntervalForRequest = 15
        configuration.timeoutIntervalForResource = 60
        session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
    }

    func request(_ request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse) {
        let call = DebugHTTPCall(limit: limit)
        return try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { continuation in
                let task = session.dataTask(with: request)
                lock.lock(); calls[task.taskIdentifier] = call; lock.unlock()
                call.start(task, continuation: continuation)
            }
        }, onCancel: { call.fail(CancellationError()) })
    }

    func invalidate() { session.invalidateAndCancel() }

    private func call(_ task: URLSessionTask) -> DebugHTTPCall? {
        lock.lock(); defer { lock.unlock() }
        return calls[task.taskIdentifier]
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let call = call(dataTask), let response = response as? HTTPURLResponse,
              call.accept(response) else { completionHandler(.cancel); return }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        call(dataTask)?.append(data)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock(); let call = calls.removeValue(forKey: task.taskIdentifier); lock.unlock()
        if let error = error { call?.fail(error) } else { call?.complete() }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

private final class DebugHTTPCall: @unchecked Sendable {
    private let lock = NSLock()
    private let limit: Int
    private var finished = false
    private var task: URLSessionDataTask?
    private var continuation: CheckedContinuation<(Data, HTTPURLResponse), Error>?
    private var response: HTTPURLResponse?
    private var data = Data()
    init(limit: Int) { self.limit = limit }

    func start(_ task: URLSessionDataTask, continuation: CheckedContinuation<(Data, HTTPURLResponse), Error>) {
        lock.lock()
        if finished {
            lock.unlock(); task.cancel(); continuation.resume(throwing: CancellationError()); return
        }
        self.task = task; self.continuation = continuation
        lock.unlock(); task.resume()
    }

    func accept(_ response: HTTPURLResponse) -> Bool {
        if response.expectedContentLength > Int64(limit) { fail(DebugBackendError.tooLarge); return false }
        lock.lock(); defer { lock.unlock() }
        guard !finished else { return false }
        self.response = response; return true
    }

    func append(_ chunk: Data) {
        lock.lock()
        guard !finished else { lock.unlock(); return }
        if chunk.count > limit - data.count {
            lock.unlock(); fail(DebugBackendError.tooLarge); return
        }
        data.append(chunk); lock.unlock()
    }

    func fail(_ error: Error) { finish(.failure(error)) }
    func complete() {
        lock.lock()
        let result: Result<(Data, HTTPURLResponse), Error> = response.map { .success((data, $0)) }
            ?? .failure(DebugBackendError.invalidResponse)
        lock.unlock(); finish(result)
    }
    private func finish(_ result: Result<(Data, HTTPURLResponse), Error>) {
        lock.lock()
        guard !finished else { lock.unlock(); return }
        finished = true
        let continuation = self.continuation; self.continuation = nil
        let task = self.task; self.task = nil
        data.removeAll(keepingCapacity: false)
        lock.unlock()
        if case .failure = result { task?.cancel() }
        continuation?.resume(with: result)
    }
}

struct DebugGuestMemoryBinding {
    let guestId: String
    let identityVerified: Bool
    let longTermAllowed: Bool
    let cloudExportAllowed: Bool

    func validate() throws {
        guard identityVerified, (1...80).contains(guestId.utf8.count),
              guestId.range(of: "[A-Za-z0-9._-]+", options: .regularExpression) == guestId.startIndex..<guestId.endIndex,
              !cloudExportAllowed || longTermAllowed else { throw DebugBackendError.serverCode("guest_identity_not_verified") }
    }
}

final class DebugCostumeInput {
    var jpeg: Data?
    var description: String?
    let capturedAt: Date
    init(jpeg: Data?, description: String?, capturedAt: Date = Date()) {
        self.jpeg = jpeg; self.description = description
        self.capturedAt = capturedAt
    }
}

enum DebugCostumeJPEG {
    // The redraw removes location/orientation; strip auxiliary JPEG metadata too.
    static func withoutMetadata(_ data: Data) throws -> Data {
        let bytes = [UInt8](data)
        guard bytes.count >= 4, bytes[0] == 255, bytes[1] == 216 else { throw DebugBackendError.invalidResponse }
        var output = Data(bytes.prefix(2))
        var offset = 2
        while offset + 3 < bytes.count {
            guard bytes[offset] == 255 else { throw DebugBackendError.invalidResponse }
            let marker = bytes[offset + 1]
            if marker == 218 {
                output.append(contentsOf: bytes[offset...])
                guard output.count <= 262_144 else { throw DebugBackendError.tooLarge }
                return output
            }
            let length = Int(bytes[offset + 2]) * 256 + Int(bytes[offset + 3])
            guard length >= 2, offset + 2 + length <= bytes.count else { throw DebugBackendError.invalidResponse }
            if !(225...239).contains(marker) && marker != 254 {
                output.append(contentsOf: bytes[offset..<(offset + 2 + length)])
            }
            offset += 2 + length
        }
        throw DebugBackendError.invalidResponse
    }
}

@MainActor
final class DebugBackendClient {
    typealias HTTP = (URLRequest, Int) async throws -> (Data, HTTPURLResponse)
    typealias AccountAuthorizer = (URLRequest, String, String) async throws -> URLRequest
    private let http: HTTP
    private let accountAuthorizer: AccountAuthorizer?
    private let transport: DebugHTTPTransport?
    private struct CleanupRecord {
        let profile: DebugBackendProfile
        var token: String
        let conversation: String
        let key: String
    }
    // Only this client's acquired conversation IDs enter this registry. Keep
    // failed mutations across Stop; remove credentials after confirmed 204.
    private var pendingCleanup: [String: CleanupRecord] = [:]
    private var cleanupTasks: [String: Task<Void, Error>] = [:]
    private var confirmedCleanup = Set<String>()
    var hasPendingCleanup: Bool { !pendingCleanup.isEmpty }

    func retryPendingCleanup() async throws {
        for record in Array(pendingCleanup.values) {
            try await cleanup(record.profile, token: record.token, conversation: record.conversation)
        }
    }

    init(http: HTTP? = nil, accountAuthorizer: AccountAuthorizer? = nil) {
        if let accountAuthorizer = accountAuthorizer {
            self.accountAuthorizer = accountAuthorizer
        } else {
#if os(iOS)
            self.accountAuthorizer = { request, deviceId, binding in
                try await Self.authorizeAccountRequest(request, deviceId: deviceId, binding: binding)
            }
#else
            self.accountAuthorizer = nil
#endif
        }
        if let http = http {
            self.http = http
            transport = nil
        } else {
            let transport = DebugHTTPTransport()
            self.transport = transport
            self.http = { request, limit in try await transport.request(request, limit: limit) }
        }
    }

    deinit { transport?.invalidate() }

#if os(iOS)
    @MainActor private static func authorizeAccountRequest(_ request: URLRequest, deviceId: String, binding: String) async throws -> URLRequest {
        guard let identity = KufrIdentityPlugin.active else { throw DebugBackendError.invalidProfile }
        return try await identity.authorizeDeviceRequest(request, deviceId: deviceId, binding: binding)
    }
#endif

    func authorizedRequest(_ profile: DebugBackendProfile, _ request: URLRequest) async throws -> URLRequest {
        guard profile.usesAccountSession else { return request }
        guard let binding = profile.accountBinding, let authorize = accountAuthorizer else { throw DebugBackendError.invalidProfile }
        // Check the binding anew for each upload, stream, poll and cleanup.
        // Old owned conversations cannot inherit a different signed-in user.
        return try await authorize(request, profile.deviceId, binding)
    }

    private func send(_ profile: DebugBackendProfile, _ request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse) {
        let authorized = try await authorizedRequest(profile, request)
        try Task.checkCancellation()
        return try await http(authorized, limit)
    }

    @MainActor func openHandsFreeSession(_ profile: DebugBackendProfile, readyMask: Int,
        voice: String, speed: Double, gptLive: Bool = false, costume: DebugCostumeInput? = nil,
        guestMemory: DebugGuestMemoryBinding? = nil, confirmGuestMemory: Bool = false,
        costumeStatus: (String) -> Void = { _ in }, phase: @escaping DebugHandsFreeSession.Phase,
        visitor: @escaping DebugHandsFreeSession.Visitor, reply: @escaping DebugHandsFreeSession.Reply,
        audio: @escaping DebugHandsFreeSession.Audio, listening: @escaping DebugHandsFreeSession.Listening,
        inputGate: @escaping DebugHandsFreeSession.InputGate, failure: @escaping DebugHandsFreeSession.Failure,
        checkpoint: @escaping DebugHandsFreeSession.Checkpoint = { _ in },
        cancelAudio: @escaping DebugHandsFreeSession.CancelAudio = {})
        async throws -> DebugHandsFreeSession {
        guard (0...63).contains(readyMask), ["cedar", "marin", "coral"].contains(voice),
              speed.isFinite, (0.8...1.2).contains(speed) else { throw DebugBackendError.invalidResponse }
        guard !hasPendingCleanup else { throw DebugBackendError.serverCode("realtime_cleanup_failed") }
        try guestMemory?.validate()
        guard !profile.usesAccountSession || guestMemory == nil || confirmGuestMemory else {
            throw DebugBackendError.serverCode("account_admin_confirmation_required")
        }
        let token = try await accessToken(profile)
        try await requireEngine(profile, token: token, engine: gptLive ? "gpt-live" : "realtime")
        let boot = UUID().uuidString.lowercased()
        let created = try await json(profile, path: "v1/conversations", token: token, method: "POST", body: [
            "deviceId": profile.deviceId, "bootId": boot, "generation": 0,
            "catalogVersion": "ollivander-private-party-v1", "locale": "cs-CZ",
            "consent": ["audio": true, "image": costume?.jpeg != nil]
        ])
        let conversation = try uuid(created, "conversationId")
        do {
            if let guestMemory { try await bindGuestMemory(profile, conversation: conversation, binding: guestMemory) }
            try await requireEngine(profile, token: token, engine: gptLive ? "gpt-live" : "realtime")
            try Task.checkCancellation()
            return DebugHandsFreeSession(client: self, profile: profile, token: token, conversation: conversation,
                boot: boot, readyMask: readyMask, voice: voice, speed: speed, gptLive: gptLive, phase: phase, visitor: visitor,
                reply: reply, audio: audio, listening: listening, inputGate: inputGate, failure: failure, checkpoint: checkpoint, cancelAudio: cancelAudio)
        } catch {
            try? await cleanup(profile, token: token, conversation: conversation)
            throw error
        }
    }

    private func bindGuestMemory(_ profile: DebugBackendProfile, conversation: String, binding: DebugGuestMemoryBinding) async throws {
        try binding.validate()
        _ = try await json(profile, path: "v1/admin/memory/guest", token: profile.credential, method: "PUT", body: [
            "deviceId": profile.deviceId, "conversationId": conversation, "guestId": binding.guestId,
            "identitySource": "operator", "identityVerified": binding.identityVerified,
            "longTermAllowed": binding.longTermAllowed, "cloudExportAllowed": binding.cloudExportAllowed, "expectedVersion": 0], confirmedAdminMutation: true)
    }

    func inspectMemory(_ profile: DebugBackendProfile, conversation: String, personality: String) async throws -> [String: Any] {
        guard UUID(uuidString: conversation) != nil else { throw DebugBackendError.invalidResponse }
        let (data, response) = try await request(profile, path: "v1/admin/memory/context", token: profile.credential,
            method: "GET", body: nil, limit: 262_144,
            queryItems: [URLQueryItem(name: "deviceId", value: profile.deviceId),
                URLQueryItem(name: "conversationId", value: conversation), URLQueryItem(name: "personalityId", value: personality)])
        guard response.mimeType == "application/json",
              let value = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let epoch = value["epoch"] as? Int, epoch >= 0, value["records"] is [[String: Any]],
              value["memoryStatus"] is [String: Any] else { throw DebugBackendError.invalidResponse }
        if let identity = value["identity"] as? [String: Any] {
            guard identity["deviceId"] as? String == profile.deviceId,
                  identity["conversationId"] as? String == conversation else { throw DebugBackendError.invalidResponse }
        }
        return value
    }

    func deleteConversationMemory(_ profile: DebugBackendProfile, conversation: String, expectedEpoch: Int, confirmed: Bool = false) async throws -> [String: Any] {
        guard UUID(uuidString: conversation) != nil, expectedEpoch >= 0 else { throw DebugBackendError.invalidResponse }
        guard !profile.usesAccountSession || confirmed else { throw DebugBackendError.serverCode("account_admin_confirmation_required") }
        let (data, response) = try await request(profile, path: "v1/admin/memory/scopes/conversation/\(conversation)",
            token: profile.credential, method: "DELETE", body: ["deviceId": profile.deviceId, "expectedEpoch": expectedEpoch], limit: 262_144, confirmedAdminMutation: confirmed)
        if response.statusCode == 204 { return ["deleted": true] }
        guard response.mimeType == "application/json",
              let value = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw DebugBackendError.invalidResponse }
        return value
    }

    private func requireEngine(_ profile: DebugBackendProfile, token: String, engine: String) async throws {
        let diagnostics = try await json(profile, path: "v1/diagnostics", token: token)
        guard diagnostics["catalogVersion"] as? String == "ollivander-private-party-v1",
              let value = diagnostics["engines"] as? [String: Any] else {
            throw DebugBackendError.serverCode("native_upgrade_required")
        }
        let data = try JSONSerialization.data(withJSONObject: value)
        let inventory = try JSONDecoder().decode(DebugEngineInventory.self, from: data)
        try inventory.validate()
        guard let selected = inventory.engines.first(where: { $0.engine == engine }), selected.ready else {
            throw DebugBackendError.serverCode(engine == "gpt-live" ? "gpt_live_unavailable" : "realtime_unavailable")
        }
    }

    func revokeConversationImage(_ profile: DebugBackendProfile, token: String, conversation: String,
                                 boot: String, revision: Int) async throws {
        guard revision >= 1 else { return }
        var request = URLRequest(url: profile.baseURL.appendingPathComponent("v1/conversations/\(conversation)/image"))
        request.httpMethod = "DELETE"
        request.timeoutInterval = 10
        let headers = ["Authorization": "Bearer \(token)", "Idempotency-Key": UUID().uuidString.lowercased(),
            "Client-Turn-Id": UUID().uuidString.lowercased(), "Boot-Id": boot, "Generation": "0", "Device-Revision": "0",
            "Catalog-Version": "ollivander-private-party-v1", "Image-Revision": String(revision)]
        for (key, value) in headers where !profile.usesAccountSession || key != "Authorization" { request.setValue(value, forHTTPHeaderField: key) }
        let (data, response) = try await send(profile, request, limit: 4_096)
        guard response.statusCode == 204, data.isEmpty else { throw DebugBackendError.invalidResponse }
    }

    func attachConversationImage(_ profile: DebugBackendProfile, token: String, conversation: String,
                                 boot: String, jpeg: Data, imageId: String, capturedAt: Date, revision: Int) async throws {
        guard !jpeg.isEmpty, jpeg.count <= 262_144, UUID(uuidString: imageId) != nil, revision > 0 else {
            throw DebugBackendError.invalidResponse
        }
        do {
            let turn = UUID().uuidString.lowercased()
            let accepted = try await costumeRequest(profile, token: token, conversation: conversation,
                boot: boot, turn: turn, suffix: "image", method: "POST", data: jpeg, contentType: "image/jpeg",
                extraHeaders: ["Image-Consent": "true", "Image-Id": imageId, "Image-Source": "phone",
                    "Captured-At": ISO8601DateFormatter().string(from: capturedAt), "Image-Revision": String(revision)])
            guard accepted["kind"] as? String == "image", accepted["status"] as? String == "pending" else {
                throw DebugBackendError.invalidResponse
            }
            let correlation: [String: Any] = ["deviceId": profile.deviceId, "bootId": boot,
                "conversationId": conversation, "clientTurnId": turn, "generation": 0, "deviceRevision": 0]
            let id = try uuid(accepted, "operationId")
            let result = try await poll(profile, token: token, id: id, kind: "image", correlation: correlation)
            try validateCostumeResult(result, manual: false)
        } catch {
            // Revoke even an ambiguously admitted upload. An unstructured barrier
            // survives cancellation and prevents its late provider result winning.
            let revocation = Task {
                try await self.revokeConversationImage(profile, token: token, conversation: conversation, boot: boot, revision: revision)
            }
            do { try await revocation.value }
            catch { throw DebugBackendError.serverCode("image_cancel_failed") }
            throw error
        }
    }

    func correctCostume(_ profile: DebugBackendProfile, token: String, conversation: String,
                                boot: String, description: String) async throws {
        guard !description.isEmpty, description.unicodeScalars.count <= 240,
            !description.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw DebugBackendError.invalidResponse
        }
        let data = try JSONSerialization.data(withJSONObject: ["description": description], options: [.sortedKeys])
        let turn = UUID().uuidString.lowercased()
        let key = UUID().uuidString.lowercased()
        let correlation: [String: Any] = ["deviceId": profile.deviceId, "bootId": boot,
            "conversationId": conversation, "clientTurnId": turn, "generation": 0, "deviceRevision": 0]
        for attempt in 0..<2 {
            do {
                // A lost or malformed success may already have committed a cue.
                // Replay only this exact mutation; never retry photo/provider work.
                let operation = try await costumeRequest(profile, token: token, conversation: conversation,
                    boot: boot, turn: turn, suffix: "costume-context", method: "PUT", data: data,
                    contentType: "application/json", idempotencyKey: key)
                _ = try uuid(operation, "operationId")
                guard let received = operation["correlation"] as? NSDictionary, received.isEqual(to: correlation),
                    operation["kind"] as? String == "costume", operation["status"] as? String == "completed",
                    let result = operation["result"] as? [String: Any] else { throw DebugBackendError.invalidResponse }
                try validateCostumeResult(result, manual: true)
                return
            } catch {
                try Task.checkCancellation()
                if attempt == 1 { throw error }
            }
        }
        throw DebugBackendError.invalidResponse
    }

    private func validateCostumeResult(_ result: [String: Any], manual: Bool) throws {
        guard let description = result["description"] as? String, !description.isEmpty, description.unicodeScalars.count <= 240,
            let uncertainty = result["uncertainty"] as? String, ["low", "medium", "high"].contains(uncertainty),
            let source = result["source"] as? String, (manual ? ["manual"] : ["photo", "fake"]).contains(source),
            let expiry = result["expiresAt"] as? String, let date = Self.date(expiry), date > Date(),
            date <= Date().addingTimeInterval(1_860) else { throw DebugBackendError.invalidResponse }
        if let character = result["possibleCharacter"] as? String, character.unicodeScalars.count > 80 {
            throw DebugBackendError.invalidResponse
        }
    }

    private func costumeRequest(_ profile: DebugBackendProfile, token: String, conversation: String,
                                boot: String, turn: String, suffix: String, method: String,
                                data: Data, contentType: String, idempotencyKey: String? = nil,
                                extraHeaders: [String: String] = [:]) async throws -> [String: Any] {
        try Task.checkCancellation()
        var request = URLRequest(url: profile.baseURL.appendingPathComponent("v1/conversations/\(conversation)/\(suffix)"))
        request.httpMethod = method
        request.httpBody = data
        let headers = ["Authorization": "Bearer \(token)", "Content-Type": contentType,
            "Idempotency-Key": idempotencyKey ?? UUID().uuidString.lowercased(), "Client-Turn-Id": turn, "Boot-Id": boot,
            "Generation": "0", "Device-Revision": "0", "Catalog-Version": "ollivander-private-party-v1"]
        for (key, value) in headers.merging(extraHeaders, uniquingKeysWith: { _, value in value })
            where !profile.usesAccountSession || key != "Authorization" { request.setValue(value, forHTTPHeaderField: key) }
        // Single attempt: never resend a photo after an ambiguous network error.
        let (data, response) = try await send(profile, request, limit: 16_384)
        try Task.checkCancellation()
        guard response.statusCode == (method == "POST" ? 202 : 200), data.count <= 16_384,
            response.mimeType == "application/json", let result = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { throw DebugBackendError.invalidResponse }
        return result
    }

    func inspect(_ profile: DebugBackendProfile) async throws -> String {
        _ = try await engineInventory(profile)
        return "openai"
    }

    func engineInventory(_ profile: DebugBackendProfile) async throws -> DebugEngineInventory {
        let token = try await accessToken(profile)
        let diagnostics = try await json(profile, path: "v1/diagnostics", token: token)
        guard let value = diagnostics["engines"] as? [String: Any] else {
            throw DebugBackendError.serverCode("native_upgrade_required")
        }
        let data = try JSONSerialization.data(withJSONObject: value)
        let inventory = try JSONDecoder().decode(DebugEngineInventory.self, from: data)
        try inventory.validate()
        return inventory
    }

    // Authorize the HTTPS upgrade before converting its transport scheme. The
    // account helper owns the cookie, CSRF and exact-device scope; none enters JS.
    func audioStreamRequest(_ profile: DebugBackendProfile, conversation: String,
                            endpoint: String, streamId: String, token: String) async throws -> URLRequest {
        guard UUID(uuidString: conversation) != nil, UUID(uuidString: streamId) != nil,
              ["gpt-live", "realtime-live"].contains(endpoint) else { throw DebugBackendError.invalidResponse }
        var request = URLRequest(url: profile.baseURL.appendingPathComponent(
            "v1/conversations/\(conversation)/\(endpoint)/\(streamId)/audio-stream"))
        if !profile.usesAccountSession { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        request.httpMethod = "GET"
        request = try await authorizedRequest(profile, request)
        try Task.checkCancellation()
        guard let url = request.url, var components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              let scheme = components.scheme, ["https", "http"].contains(scheme) else { throw DebugBackendError.invalidProfile }
        components.scheme = components.scheme == "https" ? "wss" : "ws"
        guard let socketURL = components.url else { throw DebugBackendError.invalidProfile }
        request.url = socketURL
        return request
    }

    func eveningList(_ profile: DebugBackendProfile, action: String = "list", name: String? = nil,
                     eveningId: String? = nil, expectedEveningId: String? = nil) async throws -> DebugEveningList {
        guard ["list", "create", "activate"].contains(action) else { throw DebugBackendError.invalidResponse }
        var path = "v1/evenings"
        var body: [String: Any]?
        var method = "GET"
        if action != "list" {
            guard let expected = expectedEveningId, UUID(uuidString: expected) != nil else { throw DebugBackendError.invalidResponse }
            body = ["expectedEveningId": expected]
            if action == "create" {
                guard let name = name, !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                      name.unicodeScalars.count <= 80,
                      !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { throw DebugBackendError.invalidResponse }
                body?["name"] = name
                method = "POST"
            } else {
                guard let id = eveningId, UUID(uuidString: id) != nil else { throw DebugBackendError.invalidResponse }
                path += "/\(id.lowercased())/active"
                method = "PUT"
            }
        }
        let token = try await accessToken(profile)
        if let body = body {
            let key = UUID().uuidString.lowercased()
            for attempt in 0..<2 {
                do {
                    let result = try await json(profile, path: path, token: token, method: method, body: body,
                                                idempotencyKey: key, limit: 262_144)
                    let mutation = try JSONDecoder().decode(DebugEveningList.self, from: JSONSerialization.data(withJSONObject: result))
                    try mutation.validate()
                    break
                } catch {
                    try Task.checkCancellation()
                    if case DebugBackendError.httpStatus = error { throw error }
                    if attempt == 1 { throw error }
                }
            }
        }
        // A same-key replay may carry an older snapshot after another phone's
        // switch. Always show the authoritative current pointer after mutation.
        // Up to 100 names of 80 Unicode scalars, including JSON-escaped emoji.
        let current = try await json(profile, path: "v1/evenings", token: token, limit: 262_144)
        let result = try JSONDecoder().decode(DebugEveningList.self, from: JSONSerialization.data(withJSONObject: current))
        try result.validate()
        return result
    }

    func serverLogs(_ profile: DebugBackendProfile, after: Int64? = nil, level: String = "info") async throws -> DebugServerLogPage {
        guard ["info", "warning", "error"].contains(level), after == nil || after! >= 0 && after! <= 9_007_199_254_740_991
        else { throw DebugBackendError.invalidResponse }
        var query = [URLQueryItem(name: "limit", value: "200"), URLQueryItem(name: "level", value: level)]
        if let after = after { query.append(URLQueryItem(name: "after", value: String(after))) }
        let (data, response) = try await request(profile, path: "v1/admin/logs", token: profile.credential,
            method: "GET", body: nil, limit: 262_144, queryItems: query)
        guard response.mimeType == "application/json" else { throw DebugBackendError.invalidResponse }
        let page = try JSONDecoder().decode(DebugServerLogPage.self, from: data)
        try page.validate()
        return page
    }

    func serverModes(_ profile: DebugBackendProfile, expectedRevision: String? = nil,
                     personalityId: String? = nil, characterEnabled: Bool? = nil, characterTone: String? = nil,
                     characterFinaleProfile: String? = nil, resetCharacterVariety: Bool = false) async throws -> DebugServerModes {
        var body: [String: Any]?
        if let revision = expectedRevision {
            guard UUID(uuidString: revision) != nil else { throw DebugBackendError.wrongMode }
            body = ["expectedRevision": revision]
            if let enabled = characterEnabled { body?["characterEnabled"] = enabled }
            if let tone = characterTone {
                guard ["Reserved", "Dry", "Adult", "Chaos"].contains(tone) else { throw DebugBackendError.wrongMode }
                body?["characterTone"] = tone
            }
            if let finale = characterFinaleProfile {
                guard ["Limited", "Blocked"].contains(finale) else { throw DebugBackendError.wrongMode }
                body?["characterFinaleProfile"] = finale
            }
            if resetCharacterVariety { body?["resetCharacterVariety"] = true }
            if let id = personalityId {
                guard id.range(of: "^[a-z0-9-]{1,64}$", options: .regularExpression) != nil else {
                    throw DebugBackendError.wrongMode
                }
                body?["personalityId"] = id
            }
        }
        let value = try await json(profile, path: "v1/admin/modes", token: profile.credential,
                                   method: body == nil ? "GET" : "PUT", body: body)
        let data = try JSONSerialization.data(withJSONObject: value)
        let modes = try JSONDecoder().decode(DebugServerModes.self, from: data)
        try modes.engines.validate()
        guard UUID(uuidString: modes.revision) != nil, modes.activeConversations >= 0,
              modes.lifetime == "until_restart" else { throw DebugBackendError.invalidResponse }
        if modes.personalityId != nil || modes.personalities != nil || modes.personalityLifetime != nil {
            guard let id = modes.personalityId, let inventory = modes.personalities,
                  modes.personalityLifetime == "persistent", (1...32).contains(inventory.count),
                  Set(inventory.map { $0.id }).count == inventory.count,
                  inventory.contains(where: { $0.id == id }),
                  inventory.allSatisfy({ item in
                      item.id.range(of: "^[a-z0-9-]{1,64}$", options: .regularExpression) != nil
                      && (1...100).contains(item.name.unicodeScalars.count)
                      && (1...300).contains(item.description.unicodeScalars.count)
                      && !(item.name + item.description).unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
                      && item.promptSha256.range(of: "^[a-f0-9]{64}$", options: .regularExpression) != nil
                  }) else { throw DebugBackendError.invalidResponse }
        }
        guard modes.characterTone == nil || ["Reserved", "Dry", "Adult", "Chaos"].contains(modes.characterTone!),
              modes.characterFinaleProfile == nil || ["Limited", "Blocked"].contains(modes.characterFinaleProfile!) else {
            throw DebugBackendError.invalidResponse
        }
        return modes
    }

    fileprivate func accessToken(_ profile: DebugBackendProfile) async throws -> String {
        // Account requests use the existing cookie/CSRF session. No device token
        // is minted, copied or exchanged; this legacy-only argument is unused.
        if profile.usesAccountSession { return "" }
        let result = try await json(profile, path: "v1/device-access/token", token: profile.credential, method: "POST")
        guard result["deviceId"] as? String == profile.deviceId,
              let token = result["accessToken"] as? String, !token.isEmpty, token.utf8.count <= 256,
              token.unicodeScalars.allSatisfy({ $0.value > 32 && $0.value < 127 }) else {
            throw DebugBackendError.invalidResponse
        }
        return token
    }

    fileprivate func poll(_ profile: DebugBackendProfile, token: String, id: String, kind: String,
                      correlation: [String: Any]) async throws -> [String: Any] {
        let deadline = Date().addingTimeInterval(55)
        while Date() < deadline {
            try Task.checkCancellation()
            let operation = try await json(profile, path: "v1/operations/\(id)", token: token)
            guard operation["operationId"] as? String == id, operation["kind"] as? String == kind,
                  let received = operation["correlation"] as? NSDictionary,
                  received.isEqual(to: correlation) else { throw DebugBackendError.invalidResponse }
            let status = operation["status"] as? String
            if status == "completed", let result = operation["result"] as? [String: Any] { return result }
            guard status == "pending" || status == "running" else { throw DebugBackendError.invalidResponse }
            try await Task.sleep(nanoseconds: 250_000_000)
        }
        throw DebugBackendError.timeout
    }

    func cleanup(_ profile: DebugBackendProfile, token: String, conversation: String) async throws {
        if confirmedCleanup.contains(conversation) { return }
        if let current = cleanupTasks[conversation] { try await current.value; return }
        let record = pendingCleanup[conversation] ?? CleanupRecord(profile: profile, token: token,
            conversation: conversation, key: UUID().uuidString.lowercased())
        pendingCleanup[conversation] = record
        // Unstructured cleanup survives caller cancellation. The same key stays
        // owned even if both attempts fail: a later Stop must replay it because
        // the backend removes the conversation before its image barrier completes.
        let current = Task { @MainActor in
            var ownedRecord = record
            var renewalAttempted = false
            for attempt in 0..<2 {
                do {
                    var (_, response) = try await self.request(ownedRecord.profile,
                        path: "v1/conversations/\(ownedRecord.conversation)", token: ownedRecord.token,
                        method: "DELETE", body: nil, limit: 4096, key: ownedRecord.key,
                        allowUnauthorizedResponse: true)
                    if response.statusCode == 401 && !renewalAttempted {
                        renewalAttempted = true
                        ownedRecord.token = try await self.accessToken(ownedRecord.profile)
                        // Persist renewal before the replay: a failed barrier must
                        // keep the fresh credential for the next Stop as well.
                        self.pendingCleanup[conversation] = ownedRecord
                        (_, response) = try await self.request(ownedRecord.profile,
                            path: "v1/conversations/\(ownedRecord.conversation)", token: ownedRecord.token,
                            method: "DELETE", body: nil, limit: 4096, key: ownedRecord.key,
                            allowUnauthorizedResponse: true)
                    }
                    if response.statusCode == 401 { throw DebugBackendError.httpStatus }
                    guard response.statusCode == 204 else { throw DebugBackendError.invalidResponse }
                    self.pendingCleanup.removeValue(forKey: conversation)
                    self.confirmedCleanup.insert(conversation)
                    return
                } catch {
                    if attempt == 1 { throw error }
                }
            }
        }
        cleanupTasks[conversation] = current
        do {
            try await current.value
            cleanupTasks.removeValue(forKey: conversation)
        } catch {
            cleanupTasks.removeValue(forKey: conversation)
            throw error
        }
    }

    func cancelOperation(_ profile: DebugBackendProfile, token: String, operation: String) async throws {
        let (_, response) = try await request(profile, path: "v1/operations/\(operation)", token: token,
                                              method: "DELETE", body: nil, limit: 4096)
        guard response.statusCode == 204 else { throw DebugBackendError.invalidResponse }
    }

    fileprivate func json(_ profile: DebugBackendProfile, path: String, token: String,
                      method: String = "GET", body: [String: Any]? = nil,
                      idempotencyKey: String? = nil, recoverCancelledCreate: Bool = true,
                      limit: Int = 16_384, confirmedAdminMutation: Bool = false) async throws -> [String: Any] {
        let key = idempotencyKey ?? UUID().uuidString.lowercased()
        // Recover an ambiguously accepted create with the same key and body.
        // Never overwrite/delete a conflicting session belonging to another run.
        for attempt in 0..<2 {
            do {
                let (data, response) = try await request(profile, path: path, token: token,
                    method: method, body: body, limit: limit, key: key, confirmedAdminMutation: confirmedAdminMutation)
                guard response.mimeType == "application/json",
                      let value = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                    throw DebugBackendError.invalidResponse
                }
                if path == "v1/conversations" { _ = try uuid(value, "conversationId") }
                return value
            } catch {
                if recoverCancelledCreate && method == "POST" && path == "v1/conversations" &&
                    (Task.isCancelled || error is CancellationError) {
                    // The server may have accepted create just before Stop. Recover
                    // only this key in a cleanup task; never continue to a provider stream.
                    await Task { @MainActor in
                        if let created = try? await json(profile, path: path, token: token,
                            method: method, body: body, idempotencyKey: key, recoverCancelledCreate: false),
                           let conversation = try? uuid(created, "conversationId") {
                            try? await cleanup(profile, token: token, conversation: conversation)
                        }
                    }.value
                    throw CancellationError()
                }
                try Task.checkCancellation()
                if attempt != 0 || method != "POST" || path != "v1/conversations" { throw error }
                if case DebugBackendError.httpStatus = error { throw error }
                if error is CancellationError { throw error }
            }
        }
        throw DebugBackendError.invalidResponse
    }

    fileprivate func request(_ profile: DebugBackendProfile, path: String, token: String,
                         method: String, body: [String: Any]?, limit: Int, key: String? = nil,
                         queryItems: [URLQueryItem]? = nil,
                         allowUnauthorizedResponse: Bool = false, confirmedAdminMutation: Bool = false) async throws -> (Data, HTTPURLResponse) {
        try Task.checkCancellation()
        var components = URLComponents(url: profile.baseURL.appendingPathComponent(path), resolvingAgainstBaseURL: false)!
        components.queryItems = queryItems
        var request = URLRequest(url: components.url!)
        request.httpMethod = method
        if !profile.usesAccountSession { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        if method != "GET" { request.setValue(key ?? UUID().uuidString.lowercased(), forHTTPHeaderField: "Idempotency-Key") }
        if profile.usesAccountSession && confirmedAdminMutation { request.setValue("yes", forHTTPHeaderField: "X-DV-Confirm") }
        if let body = body {
            request.httpBody = try JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, response) = try await send(profile, request, limit: limit)
        try Task.checkCancellation()
        guard data.count <= limit else { throw DebugBackendError.tooLarge }
        guard (200...299).contains(response.statusCode) ||
                (allowUnauthorizedResponse && response.statusCode == 401) else {
            if profile.usesAccountSession, data.count <= 4_096,
               let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let code = value["code"] as? String,
               ["account_admin_required", "recent_passkey_required", "confirmation_required",
                "csrf_required", "account_origin_required"].contains(code) {
                throw DebugBackendError.serverCode(code)
            }
            throw DebugBackendError.httpStatus
        }
        return (data, response)
    }

    fileprivate func uuid(_ object: [String: Any], _ key: String) throws -> String {
        guard let value = object[key] as? String, let parsed = UUID(uuidString: value),
              parsed.uuidString.lowercased() == value.lowercased() else { throw DebugBackendError.invalidResponse }
        return value.lowercased()
    }

    // Shared by the retained streaming engines before an event stream opens.
    // Project only known machine codes; never surface arbitrary response text.
    nonisolated static func realtimeError(data: Data, status: Int) -> DebugBackendError {
        let allowed: Set<String> = [
            "turn_limit", "dialogue_busy", "realtime_unavailable", "gpt_live_unavailable",
            "access_token_required", "audio_consent_required", "device_scope_mismatch",
            "conversation_not_found", "conversation_finished", "conversation_version_mismatch",
            "realtime_stream_not_replayable", "invalid_request", "request_too_large",
            "ai_unavailable", "operation_cancelled", "invalid_audio_frame", "realtime_input_mismatch",
            "realtime_session_not_found", "realtime_playback_mismatch", "invalid_idempotency_key",
            "idempotency_conflict", "opening_required_or_already_started", "duplicate_turn_id",
            "invalid_input", "timeout"
        ]
        guard (400...599).contains(status), data.count <= 4_096,
              let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let code = value["code"] as? String, allowed.contains(code) else { return .httpStatus }
        return .serverCode(code)
    }

    static func date(_ value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: value) ?? ISO8601DateFormatter().date(from: value)
    }
}
