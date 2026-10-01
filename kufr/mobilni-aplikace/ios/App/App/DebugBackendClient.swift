import Foundation

enum DebugBackendError: Error { case invalidProfile, invalidResponse, httpStatus, tooLarge, wrongMode, timeout }

struct DebugBackendProfile {
    let baseURL: URL
    let deviceId: String
    let credential: String

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

    var bridgeValue: [String: Any] {
        ["revision": revision, "dialogMode": dialogMode, "voiceMode": voiceMode,
         "dialogAvailable": dialogAvailable, "voiceAvailable": voiceAvailable,
         "activeConversations": activeConversations, "lifetime": lifetime]
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

@MainActor
final class DebugBackendClient {
    typealias HTTP = (URLRequest, Int) async throws -> (Data, HTTPURLResponse)
    private let http: HTTP
    private let transport: DebugHTTPTransport?

    init(http: HTTP? = nil) {
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

    func openSession(_ profile: DebugBackendProfile, expectedMode: String, readyMask: Int,
                     voice: String, speed: Double) async throws -> DebugDialogSession {
        guard ["fake", "openai"].contains(expectedMode), (0...63).contains(readyMask),
              ["cedar", "marin", "onyx", "coral"].contains(voice), speed.isFinite,
              (0.85...1.25).contains(speed) else { throw DebugBackendError.invalidResponse }
        let token = try await accessToken(profile)
        guard try await metadata(profile, token: token, expectedMode: expectedMode, requireDialog: true) == expectedMode else {
            throw DebugBackendError.wrongMode
        }
        let boot = UUID().uuidString.lowercased()
        let created = try await json(profile, path: "v1/conversations", token: token, method: "POST", body: [
            "deviceId": profile.deviceId, "bootId": boot, "generation": 0,
            "catalogVersion": "ollivander-private-party-v1", "locale": "cs-CZ",
            "consent": ["audio": true, "image": false]
        ])
        let conversation = try uuid(created, "conversationId")
        do {
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
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue(boundary, forHTTPHeaderField: "Idempotency-Key")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body
        try Task.checkCancellation()
        let (data, response) = try await http(request, 16_384)
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

    func serverModes(_ profile: DebugBackendProfile, expectedRevision: String? = nil,
                     dialogMode: String? = nil, voiceMode: String? = nil) async throws -> DebugServerModes {
        var body: [String: Any]?
        if let revision = expectedRevision {
            guard UUID(uuidString: revision) != nil,
                  let dialog = dialogMode, ["fake", "responses"].contains(dialog),
                  let voice = voiceMode, ["fake", "openai"].contains(voice) else {
                throw DebugBackendError.wrongMode
            }
            body = ["expectedRevision": revision, "dialogMode": dialog, "voiceMode": voice]
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
        return modes
    }

    func run(_ profile: DebugBackendProfile, expectedMode: String,
             phase: (String) -> Void) async throws -> Data {
        guard ["fake", "openai"].contains(expectedMode) else { throw DebugBackendError.wrongMode }
        phase("checking")
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

    fileprivate func cleanup(_ profile: DebugBackendProfile, token: String, conversation: String) async throws {
        // An unstructured task performs cleanup even when the caller was cancelled.
        // Only the conversation ID acquired by this run may be deleted.
        try await Task { @MainActor in
            let key = UUID().uuidString.lowercased()
            for attempt in 0..<2 {
                do {
                    let (_, response) = try await request(profile, path: "v1/conversations/\(conversation)", token: token,
                        method: "DELETE", body: nil, limit: 4096, key: key)
                    guard response.statusCode == 204 else { throw DebugBackendError.invalidResponse }
                    return
                } catch {
                    if attempt == 1 { throw error }
                }
            }
        }.value
    }

    fileprivate func json(_ profile: DebugBackendProfile, path: String, token: String,
                      method: String = "GET", body: [String: Any]? = nil,
                      idempotencyKey: String? = nil, recoverCancelledCreate: Bool = true) async throws -> [String: Any] {
        let key = idempotencyKey ?? UUID().uuidString.lowercased()
        // Recover an ambiguously accepted create with the same key and body.
        // Never overwrite/delete a conflicting session belonging to another run.
        for attempt in 0..<2 {
            do {
                let (data, response) = try await request(profile, path: path, token: token,
                    method: method, body: body, limit: 16_384, key: key)
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
                         method: String, body: [String: Any]?, limit: Int, key: String? = nil) async throws -> (Data, HTTPURLResponse) {
        try Task.checkCancellation()
        var request = URLRequest(url: profile.baseURL.appendingPathComponent(path))
        request.httpMethod = method
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        if method != "GET" { request.setValue(key ?? UUID().uuidString.lowercased(), forHTTPHeaderField: "Idempotency-Key") }
        if let body = body {
            request.httpBody = try JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, response) = try await http(request, limit)
        try Task.checkCancellation()
        guard data.count <= limit else { throw DebugBackendError.tooLarge }
        guard (200...299).contains(response.statusCode) else { throw DebugBackendError.httpStatus }
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

    private static func date(_ value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: value) ?? ISO8601DateFormatter().date(from: value)
    }
}
