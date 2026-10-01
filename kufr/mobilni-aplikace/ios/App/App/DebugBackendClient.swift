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

// Default PKI trust is retained. Redirects must never forward a device bearer.
final class DebugNoRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

@MainActor
final class DebugBackendClient {
    typealias HTTP = (URLRequest, Int) async throws -> (Data, HTTPURLResponse)
    private let http: HTTP
    private let session: URLSession?

    init(http: HTTP? = nil) {
        if let http = http {
            self.http = http
            session = nil
        } else {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.httpCookieStorage = nil
            configuration.urlCache = nil
            configuration.timeoutIntervalForRequest = 15
            configuration.timeoutIntervalForResource = 20
            let session = URLSession(configuration: configuration)
            self.session = session
            self.http = { request, limit in
                let (bytes, response) = try await session.bytes(for: request, delegate: DebugNoRedirects())
                guard let response = response as? HTTPURLResponse else { throw DebugBackendError.invalidResponse }
                guard response.expectedContentLength <= Int64(limit) else { throw DebugBackendError.tooLarge }
                var data = Data()
                for try await byte in bytes {
                    if data.count >= limit { throw DebugBackendError.tooLarge }
                    data.append(byte)
                }
                try Task.checkCancellation()
                return (data, response)
            }
        }
    }

    deinit { session?.invalidateAndCancel() }

    func inspect(_ profile: DebugBackendProfile) async throws -> String {
        let token = try await accessToken(profile)
        return try await metadata(profile, token: token)
    }

    func run(_ profile: DebugBackendProfile, expectedMode: String,
             phase: (String) -> Void) async throws -> Data {
        guard ["fake", "openai"].contains(expectedMode) else { throw DebugBackendError.wrongMode }
        phase("checking")
        let token = try await accessToken(profile)
        guard try await metadata(profile, token: token) == expectedMode else { throw DebugBackendError.wrongMode }
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

    private func accessToken(_ profile: DebugBackendProfile) async throws -> String {
        let result = try await json(profile, path: "v1/device-access/token", token: profile.credential, method: "POST")
        guard result["deviceId"] as? String == profile.deviceId,
              let token = result["accessToken"] as? String, !token.isEmpty, token.utf8.count <= 256,
              token.unicodeScalars.allSatisfy({ $0.value > 32 && $0.value < 127 }) else {
            throw DebugBackendError.invalidResponse
        }
        return token
    }

    private func metadata(_ profile: DebugBackendProfile, token: String) async throws -> String {
        let result = try await json(profile, path: "v1/diagnostics", token: token)
        guard let mode = result["voiceMode"] as? String, ["fake", "openai"].contains(mode),
              result["catalogVersion"] as? String == "ollivander-private-party-v1",
              result["audioFormat"] as? String == "pcm16-mono-32000-wav" else {
            throw DebugBackendError.invalidResponse
        }
        return mode
    }

    private func poll(_ profile: DebugBackendProfile, token: String, id: String, kind: String,
                      correlation: [String: Any]) async throws -> [String: Any] {
        let deadline = Date().addingTimeInterval(25)
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

    private func cleanup(_ profile: DebugBackendProfile, token: String, conversation: String) async throws {
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

    private func json(_ profile: DebugBackendProfile, path: String, token: String,
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

    private func request(_ profile: DebugBackendProfile, path: String, token: String,
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

    private func uuid(_ object: [String: Any], _ key: String) throws -> String {
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
