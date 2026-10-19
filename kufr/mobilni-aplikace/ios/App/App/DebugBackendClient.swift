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

struct DebugServerModes: Codable {
    let revision: String
    let dialogMode: String
    let voiceMode: String
    let dialogAvailable: Bool
    let voiceAvailable: Bool
    let activeConversations: Int
    let lifetime: String
    let personalityId: String?
    let personalities: [DebugPersonality]?
    let personalityLifetime: String?
    var characterEnabled: Bool? = nil
    var characterTone: String? = nil
    var characterFinaleProfile: String? = nil

    var bridgeValue: [String: Any] {
        ["revision": revision, "dialogMode": dialogMode, "voiceMode": voiceMode,
         "dialogAvailable": dialogAvailable, "voiceAvailable": voiceAvailable,
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

final class DebugCostumeInput {
    var jpeg: Data?
    var description: String?
    init(jpeg: Data?, description: String?) { self.jpeg = jpeg; self.description = description }
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

    func openSession(_ profile: DebugBackendProfile, expectedMode: String, readyMask: Int,
                     voice: String, speed: Double, costume: DebugCostumeInput? = nil,
                     costumeStatus: (String) -> Void = { _ in }) async throws -> DebugDialogSession {
        guard ["fake", "openai"].contains(expectedMode), (0...63).contains(readyMask),
              ["cedar", "marin", "onyx", "coral"].contains(voice), speed.isFinite,
              (0.85...1.25).contains(speed) else { throw DebugBackendError.invalidResponse }
        guard !hasPendingCleanup else { throw DebugBackendError.serverCode("realtime_cleanup_failed") }
        let token = try await accessToken(profile)
        guard try await metadata(profile, token: token, expectedMode: expectedMode, requireDialog: true) == expectedMode else {
            throw DebugBackendError.wrongMode
        }
        let boot = UUID().uuidString.lowercased()
        let created = try await json(profile, path: "v1/conversations", token: token, method: "POST", body: [
            "deviceId": profile.deviceId, "bootId": boot, "generation": 0,
            "catalogVersion": "ollivander-private-party-v1", "locale": "cs-CZ",
            "consent": ["audio": true, "image": costume?.jpeg != nil]
        ])
        var conversation = try uuid(created, "conversationId")
        do {
            conversation = try await prepareCostume(profile, token: token, conversation: conversation, boot: boot, input: costume, status: costumeStatus)
            guard try await metadata(profile, token: token, expectedMode: expectedMode, requireDialog: true) == expectedMode else {
                throw DebugBackendError.wrongMode
            }
            try Task.checkCancellation()
            return DebugDialogSession(client: self, profile: profile, token: token, conversation: conversation,
                                      boot: boot, readyMask: readyMask, voice: voice, speed: speed)
        } catch {
            try? await cleanup(profile, token: token, conversation: conversation)
            throw error
        }
    }

    func openRealtimeSession(_ profile: DebugBackendProfile, readyMask: Int,
                             voice: String, speed: Double, costume: DebugCostumeInput? = nil,
                             costumeStatus: (String) -> Void = { _ in }) async throws -> DebugRealtimeSession {
        guard (0...63).contains(readyMask), ["cedar", "marin", "coral"].contains(voice),
              speed.isFinite, (0.8...1.2).contains(speed) else {
            throw DebugBackendError.invalidResponse
        }
        guard !hasPendingCleanup else { throw DebugBackendError.serverCode("realtime_cleanup_failed") }
        let token = try await accessToken(profile)
        guard try await metadata(profile, token: token, expectedMode: "openai", requireDialog: true) == "openai" else {
            throw DebugBackendError.wrongMode
        }
        let boot = UUID().uuidString.lowercased()
        let created = try await json(profile, path: "v1/conversations", token: token, method: "POST", body: [
            "deviceId": profile.deviceId, "bootId": boot, "generation": 0,
            "catalogVersion": "ollivander-private-party-v1", "locale": "cs-CZ",
            "consent": ["audio": true, "image": costume?.jpeg != nil]
        ])
        var conversation = try uuid(created, "conversationId")
        do {
            conversation = try await prepareCostume(profile, token: token, conversation: conversation, boot: boot, input: costume, status: costumeStatus)
            guard try await metadata(profile, token: token, expectedMode: "openai", requireDialog: true) == "openai" else {
                throw DebugBackendError.wrongMode
            }
            try Task.checkCancellation()
            return DebugRealtimeSession(client: self, profile: profile, token: token, conversation: conversation,
                                        boot: boot, readyMask: readyMask, voice: voice, speed: speed)
        } catch {
            try? await cleanup(profile, token: token, conversation: conversation)
            throw error
        }
    }

    nonisolated static func realtimeError(data: Data, status: Int) -> DebugBackendError {
        let allowed = ["cost_limit", "turn_limit", "dialogue_busy", "realtime_unavailable",
            "access_token_required", "audio_consent_required", "device_scope_mismatch",
            "conversation_not_found", "conversation_finished", "conversation_version_mismatch",
            "realtime_stream_not_replayable", "invalid_request", "request_too_large",
            "ai_unavailable", "operation_cancelled", "invalid_audio_frame", "realtime_input_mismatch",
            "realtime_session_not_found", "realtime_playback_mismatch"]
        if data.count <= 4096,
           let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let code = value["code"] as? String, allowed.contains(code) {
            return .serverCode(code)
        }
        return status == 429 ? .serverCode("cost_limit") : .httpStatus
    }

    @MainActor func openHandsFreeSession(_ profile: DebugBackendProfile, readyMask: Int,
        voice: String, speed: Double, gptLive: Bool = false, costume: DebugCostumeInput? = nil,
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
        let token = try await accessToken(profile)
        guard try await metadata(profile, token: token, expectedMode: "openai", requireDialog: true) == "openai" else {
            throw DebugBackendError.wrongMode
        }
        let boot = UUID().uuidString.lowercased()
        let created = try await json(profile, path: "v1/conversations", token: token, method: "POST", body: [
            "deviceId": profile.deviceId, "bootId": boot, "generation": 0,
            "catalogVersion": "ollivander-private-party-v1", "locale": "cs-CZ",
            "consent": ["audio": true, "image": costume?.jpeg != nil]
        ])
        var conversation = try uuid(created, "conversationId")
        do {
            conversation = try await prepareCostume(profile, token: token, conversation: conversation, boot: boot, input: costume, status: costumeStatus)
            guard try await metadata(profile, token: token, expectedMode: "openai", requireDialog: true) == "openai" else {
                throw DebugBackendError.wrongMode
            }
            try Task.checkCancellation()
            return DebugHandsFreeSession(client: self, profile: profile, token: token, conversation: conversation,
                boot: boot, readyMask: readyMask, voice: voice, speed: speed, gptLive: gptLive, phase: phase, visitor: visitor,
                reply: reply, audio: audio, listening: listening, inputGate: inputGate, failure: failure, checkpoint: checkpoint, cancelAudio: cancelAudio)
        } catch {
            try? await cleanup(profile, token: token, conversation: conversation)
            throw error
        }
    }

    private func prepareCostume(_ profile: DebugBackendProfile, token: String, conversation: String,
                                boot: String, input: DebugCostumeInput?, status: (String) -> Void) async throws -> String {
        guard let input = input else { return conversation }
        var preparedConversation = conversation
        var photoFailed = false
        do {
            if input.jpeg != nil {
                status("Zpracovávám schválenou fotku kostýmu…")
                do {
                    let turn = UUID().uuidString.lowercased()
                    let accepted = try await submitCostumeImage(profile, token: token, conversation: conversation,
                        boot: boot, turn: turn, input: input)
                    guard accepted["kind"] as? String == "image", accepted["status"] as? String == "pending" else {
                        throw DebugBackendError.invalidResponse
                    }
                    let correlation: [String: Any] = ["deviceId": profile.deviceId, "bootId": boot,
                        "conversationId": conversation, "clientTurnId": turn, "generation": 0, "deviceRevision": 0]
                    let id = try uuid(accepted, "operationId")
                    let result = try await poll(profile, token: token, id: id,
                        kind: "image", correlation: correlation)
                    try validateCostumeResult(result, manual: false)
                    status("Fotka zpracována. Vzhled je jen nejistá nápověda; hosta se zeptáme.")
                } catch {
                    try Task.checkCancellation()
                    // A lost POST/202 may have admitted work whose ID we never saw.
                    // DELETE is a backend completion barrier: no fresh voice may start
                    // until JPEG/provider ownership has been released and 204 confirmed.
                    status("Fotku se nepodařilo zpracovat. Uklízím původní rozhovor…")
                    do { try await cleanup(profile, token: token, conversation: conversation) }
                    catch {
                        status("Úklid fotky nebyl potvrzen. Nový rozhovor se nespustil; použijte Stop.")
                        throw error
                    }
                    try Task.checkCancellation()
                    let created = try await json(profile, path: "v1/conversations", token: token, method: "POST", body: [
                        "deviceId": profile.deviceId, "bootId": boot, "generation": 0,
                        "catalogVersion": "ollivander-private-party-v1", "locale": "cs-CZ",
                        "consent": ["audio": true, "image": false]
                    ])
                    preparedConversation = try uuid(created, "conversationId")
                    try Task.checkCancellation()
                    photoFailed = true
                    status("Fotku se nepodařilo zpracovat. Rozhovor pokračuje bez fotky.")
                }
            }
            if let description = input.description {
                input.description = nil
                do {
                    try await correctCostume(profile, token: token, conversation: preparedConversation,
                        boot: boot, description: description)
                    status((photoFailed ? "Fotku se nepodařilo zpracovat. " : "") + "Používám ruční popis kostýmu místo odhadu z fotky.")
                } catch {
                    try Task.checkCancellation()
                    status("Ruční popis nebyl potvrzen. Rozhovor se nespustil; použijte Stop.")
                    throw error
                }
            }
            try Task.checkCancellation()
            return preparedConversation
        } catch {
            // The caller still owns the original ID until this helper returns.
            // cleanup registers a fresh ID/key before I/O. A failed DELETE
            // remains in this retained client for Stop, even without a session.
            if preparedConversation != conversation {
                try? await cleanup(profile, token: token, conversation: preparedConversation)
            }
            throw error
        }
    }

    private func correctCostume(_ profile: DebugBackendProfile, token: String, conversation: String,
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

    private func submitCostumeImage(_ profile: DebugBackendProfile, token: String, conversation: String,
                                    boot: String, turn: String, input: DebugCostumeInput) async throws -> [String: Any] {
        defer { input.jpeg = nil }
        guard let jpeg = input.jpeg, !jpeg.isEmpty, jpeg.count <= 262_144 else { throw DebugBackendError.tooLarge }
        return try await costumeRequest(profile, token: token, conversation: conversation,
            boot: boot, turn: turn, suffix: "image", method: "POST", data: jpeg, contentType: "image/jpeg")
    }

    private func validateCostumeResult(_ result: [String: Any], manual: Bool) throws {
        guard let description = result["description"] as? String, !description.isEmpty, description.unicodeScalars.count <= 240,
            let uncertainty = result["uncertainty"] as? String, ["low", "medium", "high"].contains(uncertainty),
            let source = result["source"] as? String, (manual ? ["manual"] : ["photo", "fake"]).contains(source),
            let expiry = result["expiresAt"] as? String, let date = DebugDialogSession.date(expiry), date > Date(),
            date <= Date().addingTimeInterval(125) else { throw DebugBackendError.invalidResponse }
        if let character = result["possibleCharacter"] as? String, character.unicodeScalars.count > 80 {
            throw DebugBackendError.invalidResponse
        }
    }

    private func costumeRequest(_ profile: DebugBackendProfile, token: String, conversation: String,
                                boot: String, turn: String, suffix: String, method: String,
                                data: Data, contentType: String, idempotencyKey: String? = nil) async throws -> [String: Any] {
        try Task.checkCancellation()
        var request = URLRequest(url: profile.baseURL.appendingPathComponent("v1/conversations/\(conversation)/\(suffix)"))
        request.httpMethod = method
        request.httpBody = data
        let headers = ["Authorization": "Bearer \(token)", "Content-Type": contentType,
            "Idempotency-Key": idempotencyKey ?? UUID().uuidString.lowercased(), "Client-Turn-Id": turn, "Boot-Id": boot,
            "Generation": "0", "Device-Revision": "0", "Catalog-Version": "ollivander-private-party-v1"]
        for (key, value) in headers where !profile.usesAccountSession || key != "Authorization" { request.setValue(value, forHTTPHeaderField: key) }
        // Single attempt: never resend a photo after an ambiguous network error.
        let (data, response) = try await send(profile, request, limit: 16_384)
        try Task.checkCancellation()
        guard response.statusCode == (method == "POST" ? 202 : 200), data.count <= 16_384,
            response.mimeType == "application/json", let result = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { throw DebugBackendError.invalidResponse }
        return result
    }

    fileprivate func upload(_ profile: DebugBackendProfile, token: String, path: String,
                            fields: [String: String], wav: Data) async throws -> [String: Any] {
        guard Self.validWav(wav) else { throw DebugBackendError.invalidResponse }
        let boundary = UUID().uuidString.lowercased()
        var body = Data()
        for name in fields.keys.sorted() {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(fields[name]!)\r\n".utf8))
        }
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"wav\"; filename=\"answer.wav\"\r\nContent-Type: audio/wav\r\n\r\n".utf8))
        body.append(wav)
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))
        var request = URLRequest(url: profile.baseURL.appendingPathComponent(path))
        request.httpMethod = "POST"
        if !profile.usesAccountSession { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        request.setValue(boundary, forHTTPHeaderField: "Idempotency-Key")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body
        try Task.checkCancellation()
        let (data, response) = try await send(profile, request, limit: 16_384)
        try Task.checkCancellation()
        guard data.count <= 16_384, response.statusCode == 202, response.mimeType == "application/json",
              let value = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw DebugBackendError.invalidResponse
        }
        return value
    }

    func inspect(_ profile: DebugBackendProfile) async throws -> String {
        let token = try await accessToken(profile)
        return try await metadata(profile, token: token)
    }

    func inspectAvailability(_ profile: DebugBackendProfile) async throws -> (voiceMode: String, dialogMode: String) {
        let token = try await accessToken(profile)
        let value = try await json(profile, path: "v1/diagnostics", token: token)
        guard let voice = value["voiceMode"] as? String, ["fake", "openai"].contains(voice),
              let dialog = value["dialogMode"] as? String, ["fake", "responses"].contains(dialog),
              value["catalogVersion"] as? String == "ollivander-private-party-v1",
              value["audioFormat"] as? String == "pcm16-mono-32000-wav" else { throw DebugBackendError.invalidResponse }
        return (voice, dialog)
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
                     dialogMode: String? = nil, voiceMode: String? = nil,
                     personalityId: String? = nil, characterEnabled: Bool? = nil, characterTone: String? = nil,
                     characterFinaleProfile: String? = nil, resetCharacterVariety: Bool = false) async throws -> DebugServerModes {
        var body: [String: Any]?
        if let revision = expectedRevision {
            guard UUID(uuidString: revision) != nil,
                  let dialog = dialogMode, ["fake", "responses"].contains(dialog),
                  let voice = voiceMode, ["fake", "openai"].contains(voice) else {
                throw DebugBackendError.wrongMode
            }
            body = ["expectedRevision": revision, "dialogMode": dialog, "voiceMode": voice]
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
        guard UUID(uuidString: modes.revision) != nil,
              ["fake", "responses"].contains(modes.dialogMode),
              ["fake", "openai"].contains(modes.voiceMode),
              modes.activeConversations >= 0, modes.lifetime == "until_restart",
              (modes.dialogMode != "responses" || modes.dialogAvailable),
              (modes.voiceMode != "openai" || modes.voiceAvailable) else {
            throw DebugBackendError.invalidResponse
        }
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

    func run(_ profile: DebugBackendProfile, expectedMode: String,
             phase: (String) -> Void) async throws -> Data {
        guard ["fake", "openai"].contains(expectedMode) else { throw DebugBackendError.wrongMode }
        phase("checking")
        guard !hasPendingCleanup else { throw DebugBackendError.serverCode("realtime_cleanup_failed") }
        let token = try await accessToken(profile)
        guard try await metadata(profile, token: token, expectedMode: expectedMode) == expectedMode else { throw DebugBackendError.wrongMode }
        let bootId = UUID().uuidString.lowercased()
        let turnId = UUID().uuidString.lowercased()
        var conversationId: String?
        var cleanupAttempted = false
        do {
            let created = try await json(profile, path: "v1/conversations", token: token, method: "POST", body: [
                "deviceId": profile.deviceId, "bootId": bootId, "generation": 0,
                "catalogVersion": "ollivander-private-party-v1", "locale": "cs-CZ",
                "consent": ["audio": true, "image": false]
            ])
            let conversation = try uuid(created, "conversationId")
            conversationId = conversation
            // Create freezes server modes until cleanup. Recheck after it to
            // close the race between the first inspection and admission.
            guard try await metadata(profile, token: token, expectedMode: expectedMode) == expectedMode else { throw DebugBackendError.wrongMode }
            let correlation: [String: Any] = [
                "deviceId": profile.deviceId, "bootId": bootId, "conversationId": conversation,
                "clientTurnId": turnId, "generation": 0, "deviceRevision": 0
            ]
            phase("opening")
            let opening = try await json(profile, path: "v1/conversations/\(conversation)/opening",
                                         token: token, method: "POST", body: [
                "clientTurnId": turnId, "bootId": bootId, "generation": 0, "readyMask": 0,
                "deviceRevision": 0, "catalogVersion": "ollivander-private-party-v1"
            ])
            _ = try await poll(profile, token: token, id: uuid(opening, "operationId"),
                               kind: "turn", correlation: correlation)
            phase("speech")
            let speech = try await json(profile, path: "v1/conversations/\(conversation)/speech",
                                        token: token, method: "POST", body: ["turnId": turnId, "outputTarget": "phone"])
            let result = try await poll(profile, token: token, id: uuid(speech, "operationId"),
                                        kind: "speech", correlation: correlation)
            guard result["outputTarget"] as? String == "phone" else { throw DebugBackendError.invalidResponse }
            let audioId = try uuid(result, "audioId")
            let (wav, response) = try await request(profile, path: "v1/audio/\(audioId)", token: token,
                                                    method: "GET", body: nil, limit: 1_920_044)
            guard response.mimeType == "audio/wav", Self.validWav(wav) else { throw DebugBackendError.invalidResponse }
            try Task.checkCancellation()
            cleanupAttempted = true
            try await cleanup(profile, token: token, conversation: conversation)
            conversationId = nil
            try Task.checkCancellation()
            return wav
        } catch {
            if let conversation = conversationId, !cleanupAttempted {
                try? await cleanup(profile, token: token, conversation: conversation)
            }
            throw error
        }
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

    fileprivate func metadata(_ profile: DebugBackendProfile, token: String, expectedMode: String? = nil,
                              requireDialog: Bool = false) async throws -> String {
        let result = try await json(profile, path: "v1/diagnostics", token: token)
        if expectedMode == "fake" && result["dialogMode"] as? String != "fake" {
            throw DebugBackendError.wrongMode
        }
        if requireDialog && expectedMode == "openai" && result["dialogMode"] as? String != "responses" {
            throw DebugBackendError.wrongMode
        }
        guard let mode = result["voiceMode"] as? String, ["fake", "openai"].contains(mode),
              result["catalogVersion"] as? String == "ollivander-private-party-v1",
              result["audioFormat"] as? String == "pcm16-mono-32000-wav" else {
            throw DebugBackendError.invalidResponse
        }
        return mode
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
                      limit: Int = 16_384) async throws -> [String: Any] {
        let key = idempotencyKey ?? UUID().uuidString.lowercased()
        // Recover an ambiguously accepted create with the same key and body.
        // Never overwrite/delete a conflicting session belonging to another run.
        for attempt in 0..<2 {
            do {
                let (data, response) = try await request(profile, path: path, token: token,
                    method: method, body: body, limit: limit, key: key)
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
                    // only this key in a cleanup task; never continue to opening/TTS.
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
                         allowUnauthorizedResponse: Bool = false) async throws -> (Data, HTTPURLResponse) {
        try Task.checkCancellation()
        var components = URLComponents(url: profile.baseURL.appendingPathComponent(path), resolvingAgainstBaseURL: false)!
        components.queryItems = queryItems
        var request = URLRequest(url: components.url!)
        request.httpMethod = method
        if !profile.usesAccountSession { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        if method != "GET" { request.setValue(key ?? UUID().uuidString.lowercased(), forHTTPHeaderField: "Idempotency-Key") }
        if let body = body {
            request.httpBody = try JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, response) = try await send(profile, request, limit: limit)
        try Task.checkCancellation()
        guard data.count <= limit else { throw DebugBackendError.tooLarge }
        guard (200...299).contains(response.statusCode) ||
                (allowUnauthorizedResponse && response.statusCode == 401) else { throw DebugBackendError.httpStatus }
        return (data, response)
    }

    fileprivate func uuid(_ object: [String: Any], _ key: String) throws -> String {
        guard let value = object[key] as? String, let parsed = UUID(uuidString: value),
              parsed.uuidString.lowercased() == value.lowercased() else { throw DebugBackendError.invalidResponse }
        return value.lowercased()
    }

    static func validWav(_ data: Data) -> Bool {
        guard data.count >= 46, data.count <= 1_920_044 else { return false }
        let bytes = [UInt8](data.prefix(44))
        func number(_ offset: Int, _ count: Int) -> UInt32 {
            (0..<count).reduce(0) { $0 | UInt32(bytes[offset + $1]) << (8 * $1) }
        }
        func tag(_ offset: Int, _ value: String) -> Bool { Array(bytes[offset..<(offset + 4)]) == Array(value.utf8) }
        return tag(0, "RIFF") && tag(8, "WAVE") && tag(12, "fmt ") && tag(36, "data") &&
            number(4, 4) == UInt32(data.count - 8) && number(16, 4) == 16 && number(20, 2) == 1 &&
            number(22, 2) == 1 && number(24, 4) == 32_000 && number(28, 4) == 64_000 &&
            number(32, 2) == 2 && number(34, 2) == 16 && number(40, 4) == UInt32(data.count - 44) &&
            (data.count - 44) % 2 == 0
    }
}

struct DebugDialogReply {
    let text: String
    let wav: Data
    let selected: Int?
    let terminal: Bool
    let timings: [String: Double]
}

// A virtual session owns only its conversation and downloaded speech operation.
// It never submits a physical command or a real ESP snapshot.
@MainActor
final class DebugDialogSession {
    private let client: DebugBackendClient
    private let profile: DebugBackendProfile
    private let token: String
    private let conversation: String
    private let boot: String
    private let readyMask: Int
    private let voice: String
    private let speed: Double
    private var speechOperation: String?
    private var closed = false
    private var closing: Task<Void, Error>?
    private var terminal = false
    private var turns = 0

    init(client: DebugBackendClient, profile: DebugBackendProfile, token: String, conversation: String,
         boot: String, readyMask: Int, voice: String, speed: Double) {
        self.client = client; self.profile = profile; self.token = token; self.conversation = conversation
        self.boot = boot; self.readyMask = readyMask; self.voice = voice; self.speed = speed
    }

    private func correlation(_ turn: String) -> [String: Any] {
        ["deviceId": profile.deviceId, "bootId": boot, "conversationId": conversation,
         "clientTurnId": turn, "generation": 0, "deviceRevision": 0]
    }

    func advance(_ text: String?, phase: (String) -> Void, reply: (String) -> Void) async throws -> DebugDialogReply {
        guard !closed, !terminal, turns < 10, speechOperation == nil,
              (turns == 0) == (text == nil) else { throw DebugBackendError.invalidResponse }
        if let text = text {
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  text.unicodeScalars.count <= 1000 else { throw DebugBackendError.invalidResponse }
        }
        let start = ProcessInfo.processInfo.systemUptime
        let turn = UUID().uuidString.lowercased()
        var body: [String: Any] = ["clientTurnId": turn, "bootId": boot, "generation": 0,
            "readyMask": readyMask, "deviceRevision": 0, "catalogVersion": "ollivander-private-party-v1"]
        if let text = text { body["text"] = text }
        phase(text == nil ? "opening" : "turn")
        let accepted = try await client.json(profile, path: "v1/conversations/\(conversation)/\(text == nil ? "opening" : "turns")",
                                             token: token, method: "POST", body: body)
        let result = try await client.poll(profile, token: token, id: client.uuid(accepted, "operationId"),
                                           kind: "turn", correlation: correlation(turn))
        guard let response = result["reply"] as? String, !response.isEmpty, response.unicodeScalars.count <= 500,
              let decision = result["decision"] as? String, ["ask", "propose", "no_selection"].contains(decision) else {
            throw DebugBackendError.invalidResponse
        }
        var selected: Int?
        var selectionExpiry: Date?
        if decision == "propose" {
            guard let proposal = result["proposal"] as? [String: Any],
                  proposal["conversationId"] as? String == conversation,
                  proposal["turnId"] as? String == turn,
                  proposal["bootId"] as? String == boot,
                  proposal["catalogVersion"] as? String == "ollivander-private-party-v1",
                  let revision = proposal["deviceRevision"] as? NSNumber, revision == 0,
                  let generation = proposal["generation"] as? NSNumber, generation == 0,
                  let number = proposal["compartment"] as? NSNumber,
                  number.doubleValue == Double(number.intValue), (1...6).contains(number.intValue),
                  readyMask & (1 << (number.intValue - 1)) != 0,
                  let expiry = proposal["expiresAt"] as? String,
                  let parsedExpiry = Self.date(expiry), parsedExpiry > Date(),
                  parsedExpiry < Date().addingTimeInterval(65) else {
                throw DebugBackendError.invalidResponse
            }
            _ = try client.uuid(proposal, "proposalId")
            selected = number.intValue
            selectionExpiry = parsedExpiry
        } else if !(result["proposal"] == nil || result["proposal"] is NSNull) {
            throw DebugBackendError.invalidResponse
        }
        let dialogEnd = ProcessInfo.processInfo.systemUptime
        reply(response)
        phase("speech")
        let speech = try await client.json(profile, path: "v1/conversations/\(conversation)/speech", token: token,
            method: "POST", body: ["turnId": turn, "outputTarget": "phone", "voice": voice, "speed": speed])
        let operation = try client.uuid(speech, "operationId")
        speechOperation = operation
        let audio = try await client.poll(profile, token: token, id: operation, kind: "speech", correlation: correlation(turn))
        guard audio["outputTarget"] as? String == "phone" else { throw DebugBackendError.invalidResponse }
        let speechEnd = ProcessInfo.processInfo.systemUptime
        phase("download")
        let (wav, http) = try await client.request(profile, path: "v1/audio/\(client.uuid(audio, "audioId"))", token: token,
                                                  method: "GET", body: nil, limit: 1_920_044)
        guard http.mimeType == "audio/wav", DebugBackendClient.validWav(wav) else { throw DebugBackendError.invalidResponse }
        if let selectionExpiry = selectionExpiry, selectionExpiry <= Date() { throw DebugBackendError.invalidResponse }
        let end = ProcessInfo.processInfo.systemUptime
        terminal = decision != "ask"; turns += 1
        return DebugDialogReply(text: response, wav: wav, selected: selected, terminal: terminal, timings: [
            "dialogMs": (dialogEnd - start) * 1000, "speechMs": (speechEnd - dialogEnd) * 1000,
            "downloadMs": (end - speechEnd) * 1000, "totalMs": (end - start) * 1000])
    }

    func releaseSpeech() async throws {
        guard let operation = speechOperation else { return }
        let key = UUID().uuidString.lowercased()
        for attempt in 0..<2 {
            do {
                let (_, response) = try await client.request(profile, path: "v1/operations/\(operation)", token: token,
                                                              method: "DELETE", body: nil, limit: 4096, key: key)
                guard response.statusCode == 204 else { throw DebugBackendError.invalidResponse }
                speechOperation = nil
                return
            } catch {
                try Task.checkCancellation()
                if attempt == 1 { throw error }
            }
        }
    }

    func transcribe(_ wav: Data) async throws -> String {
        guard !closed, !terminal, turns > 0, speechOperation == nil else { throw DebugBackendError.invalidResponse }
        let turn = UUID().uuidString.lowercased()
        let accepted = try await client.upload(profile, token: token, path: "v1/conversations/\(conversation)/transcriptions",
            fields: ["clientTurnId": turn, "bootId": boot, "generation": "0", "deviceRevision": "0"], wav: wav)
        let result = try await client.poll(profile, token: token, id: client.uuid(accepted, "operationId"),
                                           kind: "transcription", correlation: correlation(turn))
        guard let text = result["transcript"] as? String, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              text.unicodeScalars.count <= 1000 else { throw DebugBackendError.invalidResponse }
        return text
    }

    func close() async throws {
        guard !closed else { return }
        if let closing = closing { try await closing.value; return }
        let closing = Task { @MainActor in
            try await client.cleanup(profile, token: token, conversation: conversation)
            closed = true; speechOperation = nil
        }
        self.closing = closing
        do { try await closing.value }
        catch { self.closing = nil; throw error }
    }

    fileprivate static func date(_ value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: value) ?? ISO8601DateFormatter().date(from: value)
    }
}
