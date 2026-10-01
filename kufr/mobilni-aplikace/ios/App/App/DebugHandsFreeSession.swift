import Foundation

// Native Debug transport for a single backend-proxied Realtime conversation.
// It owns no credential UI and never sends a proposal to the physical device.
@MainActor
final class DebugHandsFreeSession {
    typealias Phase = (String) -> Void
    typealias Visitor = (String) -> Void
    typealias Reply = (DebugHandsFreeReply) -> Void
    typealias Audio = (Data) throws -> Void
    typealias Listening = (String) -> Void
    typealias InputGate = (Bool) -> Void
    typealias Failure = (String) -> Void

    private let client: DebugBackendClient
    private let profile: DebugBackendProfile
    private let token: String
    private let conversation: String
    private let boot: String
    private let readyMask: Int
    private let voice: String
    private let speed: Double
    private let phase: Phase
    private let visitor: Visitor
    private let reply: Reply
    private let audio: Audio
    private let listening: Listening
    private let inputGate: InputGate
    private let failure: Failure
    private var stream: DebugHandsFreeLiveStream?
    private var streamId: String?
    private var currentInputId: String?
    private var previousInputId: String?
    private var currentOperationId: String?
    private var awaitingPlayedOperation: String?
    private var turnStartedAt: Date?
    private var firstAudioAt: Date?
    private var terminalReceived = false
    private var closed = false

    init(client: DebugBackendClient, profile: DebugBackendProfile, token: String, conversation: String,
         boot: String, readyMask: Int, voice: String, speed: Double, phase: @escaping Phase,
         visitor: @escaping Visitor, reply: @escaping Reply, audio: @escaping Audio, listening: @escaping Listening,
         inputGate: @escaping InputGate, failure: @escaping Failure) {
        self.client = client
        self.profile = profile
        self.token = token
        self.conversation = conversation
        self.boot = boot
        self.readyMask = readyMask
        self.voice = voice
        self.speed = speed
        self.phase = phase
        self.visitor = visitor
        self.reply = reply
        self.audio = audio
        self.listening = listening
        self.inputGate = inputGate
        self.failure = failure
    }

    func run() async throws {
        guard !closed, stream == nil, (0...63).contains(readyMask),
              ["cedar", "marin", "coral"].contains(voice), speed.isFinite, (0.8...1.2).contains(speed) else {
            throw DebugBackendError.invalidResponse
        }
        let openingId = UUID().uuidString.lowercased()
        currentInputId = openingId
        turnStartedAt = Date()
        let body: [String: Any] = [
            "clientTurnId": openingId, "bootId": boot, "generation": 0, "readyMask": readyMask,
            "deviceRevision": 0, "catalogVersion": "ollivander-private-party-v1", "opening": true,
            "text": "", "wav": NSNull(), "voice": voice, "speed": speed
        ]
        let data = try JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])
        var request = URLRequest(url: profile.baseURL.appendingPathComponent("v1/conversations/\(conversation)/realtime-live"))
        request.httpMethod = "POST"
        request.httpBody = data
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue(UUID().uuidString.lowercased(), forHTTPHeaderField: "Idempotency-Key")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/x-ndjson", forHTTPHeaderField: "Accept")
        let live = DebugHandsFreeLiveStream(request: request)
        stream = live
        phase("opening")
        defer {
            live.cancel()
            if stream === live { stream = nil }
        }
        do {
            for try await event in live.events() {
                try handle(event)
            }
            guard terminalReceived else { throw DebugBackendError.invalidResponse }
        } catch {
            inputGate(false)
            if !closed {
                if case DebugBackendError.serverCode(let code) = error {
                    failure(code)
                } else {
                    failure("realtime_live_failed")
                }
            }
            throw error
        }
    }

    // Audio owns its strictly increasing per-input sequence; this method only
    // accepts a currently listening input or a just-ended in-flight frame.
    func upload(inputId: String, sequence: Int, pcm: Data) async throws {
        guard !closed, let streamId, sequence >= 0, pcm.count > 0, pcm.count <= 9_600,
              pcm.count % 2 == 0, inputId == currentInputId || inputId == previousInputId else {
            throw DebugBackendError.invalidResponse
        }
        var request = URLRequest(url: profile.baseURL.appendingPathComponent(
            "v1/conversations/\(conversation)/realtime-live/\(streamId)/audio"))
        request.httpMethod = "POST"
        request.httpBody = pcm
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.setValue(inputId, forHTTPHeaderField: "Input-Id")
        request.setValue(String(sequence), forHTTPHeaderField: "Input-Sequence")
        let (data, response) = try await DebugHandsFreeRequest.perform(request, limit: 4096, timeout: 10)
        guard data.isEmpty, response.statusCode == 204 else { throw DebugBackendError.invalidResponse }
    }

    // Called exactly once after all dataPlayedBack callbacks for an operation.
    func played(operationId: String) async throws {
        guard !closed, let streamId, operationId == awaitingPlayedOperation else { throw DebugBackendError.invalidResponse }
        var request = URLRequest(url: profile.baseURL.appendingPathComponent(
            "v1/conversations/\(conversation)/realtime-live/\(streamId)/played"))
        request.httpMethod = "POST"
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["operationId": operationId], options: [.sortedKeys])
        awaitingPlayedOperation = nil
        phase("waiting_listening")
        let (data, response) = try await DebugHandsFreeRequest.perform(request, limit: 4096, timeout: 10)
        guard data.isEmpty, response.statusCode == 204 else { throw DebugBackendError.invalidResponse }
    }

    func stop() async {
        inputGate(false)
        stream?.cancel()
        if let operation = currentOperationId {
            try? await client.cancelOperation(profile, token: token, operation: operation)
        }
    }

    func close() async throws {
        guard !closed else { return }
        inputGate(false)
        stream?.cancel()
        try await client.cleanup(profile, token: token, conversation: conversation)
        closed = true
    }

    private func handle(_ event: DebugHandsFreeEvent) throws {
        switch event {
        case .session(let id):
            guard streamId == nil else { throw DebugBackendError.invalidResponse }
            streamId = id
        case .started(let operation, let correlation):
            guard correlation == expectedCorrelation(clientTurnId: currentInputId ?? "") else { throw DebugBackendError.invalidResponse }
            currentOperationId = operation
            inputGate(false)
            phase("thinking")
        case .audio(let pcm):
            guard currentOperationId != nil else { throw DebugBackendError.invalidResponse }
            if firstAudioAt == nil { firstAudioAt = Date() }
            inputGate(false)
            phase("playing")
            try audio(pcm)
        case .visitor(let text): visitor(text)
        case .completed(let operation, let correlation, let result, let cost):
            guard operation == currentOperationId, correlation == expectedCorrelation(clientTurnId: currentInputId ?? ""), cost > 0 else {
                throw DebugBackendError.invalidResponse
            }
            let parsed = try validate(result)
            awaitingPlayedOperation = operation
            currentOperationId = nil
            let now = Date()
            let started = turnStartedAt ?? now
            let firstAudio = firstAudioAt ?? now
            reply(DebugHandsFreeReply(text: parsed.text, selected: parsed.selected, terminal: parsed.terminal,
                                      operationId: operation, timings: [
                                          "costMicroUsd": Double(cost),
                                          "firstAudioMs": firstAudio.timeIntervalSince(started) * 1_000,
                                          "totalMs": now.timeIntervalSince(started) * 1_000
                                      ]))
        case .listening(let input):
            guard awaitingPlayedOperation == nil, UUID(uuidString: input) != nil else { throw DebugBackendError.invalidResponse }
            previousInputId = currentInputId
            currentInputId = input
            turnStartedAt = nil
            firstAudioAt = nil
            listening(input)
            inputGate(true)
            phase("listening")
        case .speechStarted(let input):
            guard input == currentInputId else { throw DebugBackendError.invalidResponse }
            phase("recording")
        case .speechStopped(let input):
            guard input == currentInputId else { throw DebugBackendError.invalidResponse }
            inputGate(false)
            phase("thinking")
            turnStartedAt = Date()
            firstAudioAt = nil
        case .heartbeat: break
        case .closed:
            terminalReceived = true
            inputGate(false)
            phase("completed")
        case .failed(let code):
            inputGate(false)
            throw DebugBackendError.serverCode(code)
        }
    }

    private func expectedCorrelation(clientTurnId: String) -> [String: AnyHashable] {
        ["deviceId": profile.deviceId, "bootId": boot, "conversationId": conversation,
         "clientTurnId": clientTurnId, "generation": 0, "deviceRevision": 0]
    }

    private func validate(_ value: [String: Any]) throws -> DebugHandsFreeReply {
        guard let text = value["reply"] as? String, !text.isEmpty, text.unicodeScalars.count <= 500,
              let decision = value["decision"] as? String, ["ask", "propose", "no_selection"].contains(decision),
              let reason = value["reason"] as? String, reason.unicodeScalars.count <= 200 else {
            throw DebugBackendError.invalidResponse
        }
        if decision != "propose" {
            guard value["proposal"] == nil || value["proposal"] is NSNull else { throw DebugBackendError.invalidResponse }
            return DebugHandsFreeReply(text: text, selected: nil, terminal: decision != "ask", operationId: "", timings: [:])
        }
        guard let proposal = value["proposal"] as? [String: Any], proposal["conversationId"] as? String == conversation,
              proposal["turnId"] as? String == currentInputId, proposal["bootId"] as? String == boot,
              proposal["catalogVersion"] as? String == "ollivander-private-party-v1",
              let proposalId = proposal["proposalId"] as? String, UUID(uuidString: proposalId) != nil,
              (proposal["generation"] as? NSNumber)?.intValue == 0,
              (proposal["deviceRevision"] as? NSNumber)?.intValue == 0,
              let number = proposal["compartment"] as? NSNumber, (1...6).contains(number.intValue),
              readyMask & (1 << (number.intValue - 1)) != 0,
              let expiry = proposal["expiresAt"] as? String else { throw DebugBackendError.invalidResponse }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let expiresAt = formatter.date(from: expiry) ?? ISO8601DateFormatter().date(from: expiry),
              expiresAt > Date(), expiresAt < Date().addingTimeInterval(65) else { throw DebugBackendError.invalidResponse }
        return DebugHandsFreeReply(text: text, selected: number.intValue, terminal: true, operationId: "", timings: [:])
    }
}

struct DebugHandsFreeReply {
    let text: String
    let selected: Int?
    let terminal: Bool
    let operationId: String
    let timings: [String: Double]
}

private enum DebugHandsFreeEvent {
    case session(String)
    case started(String, [String: AnyHashable])
    case audio(Data)
    case visitor(String)
    case completed(String, [String: AnyHashable], [String: Any], Int)
    case listening(String)
    case speechStarted(String)
    case speechStopped(String)
    case heartbeat
    case closed
    case failed(String)
}

private final class DebugHandsFreeLiveStream: NSObject, URLSessionDataDelegate {
    private let request: URLRequest
    private let lock = NSLock()
    private var continuation: AsyncThrowingStream<DebugHandsFreeEvent, Error>.Continuation?
    private var session: URLSession?
    private var line = Data()
    private var wire = 0
    private var eventCount = 0
    private var turnPcm = 0
    private var responseStatus: Int?
    private var responseBody = Data()
    private var finished = false

    init(request: URLRequest) {
        self.request = request
    }

    func events() -> AsyncThrowingStream<DebugHandsFreeEvent, Error> {
        AsyncThrowingStream { continuation in
            let configuration = URLSessionConfiguration.ephemeral
            configuration.httpCookieStorage = nil
            configuration.urlCache = nil
            configuration.timeoutIntervalForRequest = 65
            configuration.timeoutIntervalForResource = 480
            let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
            continuation.onTermination = { [weak self] _ in self?.cancel() }
            lock.lock()
            if finished {
                lock.unlock()
                session.invalidateAndCancel()
                continuation.finish(throwing: CancellationError())
                return
            }
            self.continuation = continuation
            self.session = session
            lock.unlock()
            session.dataTask(with: request).resume()
        }
    }

    func cancel() { finish(CancellationError()) }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let response = response as? HTTPURLResponse else {
            completionHandler(.cancel)
            finish(DebugBackendError.invalidResponse)
            return
        }
        guard response.statusCode == 200, response.mimeType == "application/x-ndjson" else {
            lock.lock()
            responseStatus = response.statusCode
            lock.unlock()
            completionHandler(.allow)
            return
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        var yielded: [DebugHandsFreeEvent] = []
        var continuation: AsyncThrowingStream<DebugHandsFreeEvent, Error>.Continuation?
        do {
            lock.lock()
            defer { lock.unlock() }
            guard !finished else { return }
            if responseStatus != nil {
                guard responseBody.count + data.count <= 4_096 else { throw DebugBackendError.tooLarge }
                responseBody.append(data)
                return
            }
            for byte in data {
                wire += 1
                guard wire <= 20_000_000 else { throw DebugBackendError.tooLarge }
                if byte == 10 {
                    guard !line.isEmpty else { throw DebugBackendError.invalidResponse }
                    let event = try decode(line)
                    line.removeAll(keepingCapacity: true)
                    yielded.append(event)
                } else {
                    guard line.count < 65_536 else { throw DebugBackendError.tooLarge }
                    line.append(byte)
                }
            }
            continuation = self.continuation
        } catch { finish(error) }
        for event in yielded { continuation?.yield(event) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error { finish(error); return }
        lock.lock()
        let hasPartialLine = !line.isEmpty
        let status = responseStatus
        let body = responseBody
        lock.unlock()
        if let status {
            finish(DebugBackendClient.realtimeError(data: body, status: status))
            return
        }
        guard !hasPartialLine else { finish(DebugBackendError.invalidResponse); return }
        finish(nil)
    }

    private func decode(_ line: Data) throws -> DebugHandsFreeEvent {
        eventCount += 1
        guard eventCount <= 20_000, let object = try JSONSerialization.jsonObject(with: line) as? [String: Any],
              let type = object["type"] as? String else { throw DebugBackendError.invalidResponse }
        switch type {
        case "session":
            guard let id = uuid(object, "streamId"), object["audioFormat"] as? String == "pcm16-mono-24000" else { throw DebugBackendError.invalidResponse }
            return .session(id)
        case "started":
            guard let operation = uuid(object, "operationId"), let correlation = correlation(object) else { throw DebugBackendError.invalidResponse }
            turnPcm = 0
            return .started(operation, correlation)
        case "audio":
            guard let encoded = object["delta"] as? String, encoded.utf8.count <= 65_536,
                  let pcm = Data(base64Encoded: encoded), !pcm.isEmpty, pcm.count <= 49_152, pcm.count % 2 == 0 else { throw DebugBackendError.invalidResponse }
            turnPcm += pcm.count
            guard turnPcm <= 1_440_000 else { throw DebugBackendError.tooLarge }
            return .audio(pcm)
        case "visitor":
            guard let text = object["text"] as? String, text.unicodeScalars.count <= 1000 else { throw DebugBackendError.invalidResponse }
            return .visitor(text)
        case "completed":
            guard let operation = uuid(object, "operationId"), let correlation = correlation(object),
                  let result = object["result"] as? [String: Any], let cost = object["costMicroUsd"] as? NSNumber,
                  cost.intValue > 0, cost.doubleValue == Double(cost.intValue) else { throw DebugBackendError.invalidResponse }
            return .completed(operation, correlation, result, cost.intValue)
        case "listening": guard let id = uuid(object, "inputId") else { throw DebugBackendError.invalidResponse }; return .listening(id)
        case "speech_started": guard let id = uuid(object, "inputId") else { throw DebugBackendError.invalidResponse }; return .speechStarted(id)
        case "speech_stopped": guard let id = uuid(object, "inputId") else { throw DebugBackendError.invalidResponse }; return .speechStopped(id)
        case "heartbeat": return .heartbeat
        case "closed": guard object["reason"] as? String == "conversation_terminal" else { throw DebugBackendError.invalidResponse }; return .closed
        case "failed":
            guard let code = object["code"] as? String, [
                "cost_limit", "turn_limit", "dialogue_busy", "realtime_unavailable",
                "conversation_not_found", "conversation_finished", "conversation_version_mismatch",
                "audio_consent_required", "operation_cancelled", "ai_unavailable", "invalid_input", "timeout",
                "invalid_request", "idempotency_conflict", "realtime_stream_not_replayable",
                "device_scope_mismatch", "opening_required_or_already_started", "duplicate_turn_id",
                "access_token_required", "invalid_idempotency_key", "realtime_session_not_found",
                "invalid_audio_frame", "realtime_input_mismatch", "realtime_playback_mismatch"
            ].contains(code) else { throw DebugBackendError.invalidResponse }
            return .failed(code)
        default: throw DebugBackendError.invalidResponse
        }
    }

    private func correlation(_ object: [String: Any]) -> [String: AnyHashable]? {
        guard let raw = object["correlation"] as? [String: Any], raw.count == 6 else { return nil }
        var value: [String: AnyHashable] = [:]
        for key in ["deviceId", "bootId", "conversationId", "clientTurnId"] {
            guard let string = raw[key] as? String else { return nil }
            value[key] = string
        }
        guard let generation = raw["generation"] as? NSNumber, let revision = raw["deviceRevision"] as? NSNumber,
              generation.doubleValue == Double(generation.intValue), revision.doubleValue == Double(revision.intValue) else { return nil }
        value["generation"] = generation.intValue
        value["deviceRevision"] = revision.intValue
        return value
    }

    private func uuid(_ object: [String: Any], _ key: String) -> String? {
        guard let value = object[key] as? String, let parsed = UUID(uuidString: value), parsed.uuidString.lowercased() == value.lowercased() else { return nil }
        return value.lowercased()
    }

    private func finish(_ error: Error?) {
        lock.lock()
        guard !finished else { lock.unlock(); return }
        finished = true
        let continuation = self.continuation; self.continuation = nil
        let session = self.session; self.session = nil
        lock.unlock()
        session?.invalidateAndCancel()
        if let error { continuation?.finish(throwing: error) } else { continuation?.finish() }
    }
}

private enum DebugHandsFreeRequest {
    static func perform(_ request: URLRequest, limit: Int, timeout: TimeInterval) async throws -> (Data, HTTPURLResponse) {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout
        let session = URLSession(configuration: configuration, delegate: DebugHandsFreeNoRedirects(), delegateQueue: nil)
        let (data, response) = try await session.data(for: request)
        session.invalidateAndCancel()
        guard data.count <= limit, let http = response as? HTTPURLResponse else { throw DebugBackendError.tooLarge }
        return (data, http)
    }
}

private final class DebugHandsFreeNoRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
