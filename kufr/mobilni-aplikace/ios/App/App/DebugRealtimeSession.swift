import Foundation
#if os(iOS)
import AVFoundation
#endif

// A Debug-only cloud stream. The bearer stays native and this type never sends
// a selection to the device; a completed proposal is rendered as a simulation.
@MainActor
final class DebugRealtimeSession {
    private let client: DebugBackendClient
    private let profile: DebugBackendProfile
    private let token: String
    private let conversation: String
    private let boot: String
    private let readyMask: Int
    private let voice: String
    private let speed: Double
    private var operation: String?
    private var closed = false
    private var turns = 0

    init(client: DebugBackendClient, profile: DebugBackendProfile, token: String, conversation: String,
         boot: String, readyMask: Int, voice: String, speed: Double) {
        self.client = client
        self.profile = profile
        self.token = token
        self.conversation = conversation
        self.boot = boot
        self.readyMask = readyMask
        self.voice = voice
        self.speed = speed
    }

    func advance(text: String?, wav: Data?, phase: @escaping (String) -> Void,
                 visitor: @escaping (String) -> Void, audio: @escaping (Data) throws -> Void) async throws -> DebugRealtimeReply {
        guard !closed, operation == nil, (turns == 0) == (text == nil && wav == nil) else {
            throw DebugBackendError.invalidResponse
        }
        if let text = text {
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, text.unicodeScalars.count <= 1000, wav == nil else {
                throw DebugBackendError.invalidResponse
            }
        }
        if let wav = wav, !DebugBackendClient.validWav(wav) || text != nil {
            throw DebugBackendError.invalidResponse
        }
        let turn = UUID().uuidString.lowercased()
        var body: [String: Any] = [
            "clientTurnId": turn, "bootId": boot, "generation": 0, "readyMask": readyMask,
            "deviceRevision": 0, "catalogVersion": "ollivander-private-party-v1", "opening": turns == 0,
            "text": text ?? "", "voice": voice, "speed": speed
        ]
        body["wav"] = wav?.base64EncodedString() ?? NSNull()
        let requestData = try JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])
        var request = URLRequest(url: profile.baseURL.appendingPathComponent("v1/conversations/\(conversation)/realtime-turns"))
        request.httpMethod = "POST"
        request.httpBody = requestData
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue(UUID().uuidString.lowercased(), forHTTPHeaderField: "Idempotency-Key")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/x-ndjson", forHTTPHeaderField: "Accept")
        phase(turns == 0 ? "opening" : "turn")
        let startedAt = ProcessInfo.processInfo.systemUptime
        let expected: [String: Any] = ["deviceId": profile.deviceId, "bootId": boot, "conversationId": conversation,
                                        "clientTurnId": turn, "generation": 0, "deviceRevision": 0]
        let stream = DebugRealtimeStream(request: request, expectedCorrelation: expected)
        defer { stream.cancel() }
        var firstAudioMs: Double?
        var result: [String: Any]?
        for try await event in stream.events() {
            switch event {
            case .started(let id): operation = id
            case .audio(let pcm):
                if firstAudioMs == nil { firstAudioMs = (ProcessInfo.processInfo.systemUptime - startedAt) * 1000 }
                phase("playing")
                try audio(pcm)
            case .visitor(let spoken): visitor(spoken)
            case .completed(let value): result = value
            }
        }
        operation = nil
        turns += 1
        guard let result else { throw DebugBackendError.invalidResponse }
        let reply = try validate(result, turn: turn)
        var timings = ["totalMs": (ProcessInfo.processInfo.systemUptime - startedAt) * 1000]
        if let firstAudioMs { timings["firstAudioMs"] = firstAudioMs }
        return DebugRealtimeReply(text: reply.text, selected: reply.selected, terminal: reply.terminal, timings: timings)
    }

    func cancel() async {
        if let operation = operation { try? await client.cancelOperation(profile, token: token, operation: operation) }
    }

    func close() async throws {
        guard !closed else { return }
        try await client.cleanup(profile, token: token, conversation: conversation)
        closed = true
    }

    private func validate(_ value: [String: Any], turn: String) throws -> (text: String, selected: Int?, terminal: Bool) {
        guard let reply = value["reply"] as? String, !reply.isEmpty, reply.unicodeScalars.count <= 500,
              let decision = value["decision"] as? String, ["ask", "propose", "no_selection"].contains(decision),
              let reason = value["reason"] as? String, reason.unicodeScalars.count <= 200 else {
            throw DebugBackendError.invalidResponse
        }
        if decision != "propose" {
            guard value["proposal"] == nil || value["proposal"] is NSNull else { throw DebugBackendError.invalidResponse }
            return (reply, nil, decision != "ask")
        }
        guard let proposal = value["proposal"] as? [String: Any], proposal["conversationId"] as? String == conversation,
              proposal["turnId"] as? String == turn, proposal["bootId"] as? String == boot,
              proposal["catalogVersion"] as? String == "ollivander-private-party-v1",
              let proposalId = proposal["proposalId"] as? String, UUID(uuidString: proposalId) != nil,
              (proposal["generation"] as? NSNumber)?.intValue == 0, (proposal["deviceRevision"] as? NSNumber)?.intValue == 0,
              let number = proposal["compartment"] as? NSNumber, (1...6).contains(number.intValue),
              readyMask & (1 << (number.intValue - 1)) != 0, let expiry = proposal["expiresAt"] as? String else {
            throw DebugBackendError.invalidResponse
        }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let expiresAt = formatter.date(from: expiry) ?? ISO8601DateFormatter().date(from: expiry),
              expiresAt > Date(), expiresAt < Date().addingTimeInterval(65) else {
            throw DebugBackendError.invalidResponse
        }
        return (reply, number.intValue, true)
    }
}

#if os(iOS)
// Buffers are accepted before completed arrives. The callback is attached to
// played-back data, so a temporary network gap cannot make the turn look done.
@MainActor
final class DebugPCMPlayer {
    private let engine = AVAudioEngine()
    private let node = AVAudioPlayerNode()
    private let format = AVAudioFormat(standardFormatWithSampleRate: 24_000, channels: 1)!
    private var started = false
    private var queued = 0
    private var finished = false
    private var token = UUID()
    private let drained: () -> Void

    init(drained: @escaping () -> Void) {
        self.drained = drained
        engine.attach(node)
        engine.connect(node, to: engine.mainMixerNode, format: format)
    }

    func append(_ pcm: Data) throws {
        guard !pcm.isEmpty, pcm.count <= 49_152, pcm.count % 2 == 0,
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(pcm.count / 2)) else {
            throw DebugBackendError.invalidResponse
        }
        buffer.frameLength = buffer.frameCapacity
        pcm.withUnsafeBytes { source in
            guard let destination = buffer.floatChannelData?.pointee, let source = source.baseAddress else { return }
            let samples = source.assumingMemoryBound(to: Int16.self)
            for index in 0..<Int(buffer.frameLength) {
                destination[index] = Float(Int16(littleEndian: samples[index])) / 32_768
            }
        }
        queued += 1
        let callbackToken = token
        node.scheduleBuffer(buffer, completionCallbackType: .dataPlayedBack) { [weak self] _ in
            DispatchQueue.main.async {
                guard let self = self, self.token == callbackToken else { return }
                self.queued -= 1
                self.notifyDrainedIfNeeded()
            }
        }
        if !started {
            try AVAudioSession.sharedInstance().setCategory(.playback, mode: .spokenAudio, options: [])
            try AVAudioSession.sharedInstance().setActive(true)
            try engine.start()
            node.play()
            started = true
        }
    }

    func finish() {
        finished = true
        notifyDrainedIfNeeded()
    }

    func stop() {
        node.stop()
        engine.stop()
        queued = 0
        started = false
        finished = false
        token = UUID()
    }

    private func notifyDrainedIfNeeded() {
        if finished && queued == 0 { drained() }
    }
}
#endif

struct DebugRealtimeReply {
    let text: String
    let selected: Int?
    let terminal: Bool
    let timings: [String: Double]
}

enum DebugRealtimeEvent {
    case started(String)
    case audio(Data)
    case visitor(String)
    case completed([String: Any])
}

final class DebugRealtimeFramer {
    private let expectedCorrelation: [String: Any]
    private var line = Data()
    private var totalWireBytes = 0
    private var totalAudioBytes = 0
    private var eventCount = 0
    private var started = false
    private var completed = false

    init(expectedCorrelation: [String: Any]) { self.expectedCorrelation = expectedCorrelation }

    func append(_ bytes: Data) throws -> [DebugRealtimeEvent] {
        totalWireBytes += bytes.count
        guard totalWireBytes <= 2_000_000 else { throw DebugBackendError.tooLarge }
        var events: [DebugRealtimeEvent] = []
        for byte in bytes {
            if byte == 10 {
                guard !line.isEmpty else { throw DebugBackendError.invalidResponse }
                events.append(try decode(line))
                line.removeAll(keepingCapacity: true)
                continue
            }
            guard line.count < 65_536 else { throw DebugBackendError.tooLarge }
            line.append(byte)
        }
        return events
    }

    func finish() throws {
        guard line.isEmpty, completed else { throw DebugBackendError.invalidResponse }
    }

    private func decode(_ line: Data) throws -> DebugRealtimeEvent {
        eventCount += 1
        guard eventCount <= 4096, !completed,
              let object = try JSONSerialization.jsonObject(with: line) as? [String: Any], let type = object["type"] as? String else {
            throw DebugBackendError.invalidResponse
        }
        switch type {
        case "started":
            guard !started, let id = object["operationId"] as? String, UUID(uuidString: id) != nil,
                  object["audioFormat"] as? String == "pcm16-mono-24000",
                  let correlation = object["correlation"] as? NSDictionary, correlation.isEqual(to: expectedCorrelation) else {
                throw DebugBackendError.invalidResponse
            }
            started = true
            return .started(id)
        case "audio":
            guard started, let encoded = object["delta"] as? String, encoded.utf8.count <= 65_536,
                  let pcm = Data(base64Encoded: encoded), !pcm.isEmpty, pcm.count <= 49_152, pcm.count % 2 == 0 else {
                throw DebugBackendError.invalidResponse
            }
            totalAudioBytes += pcm.count
            guard totalAudioBytes <= 1_440_000 else { throw DebugBackendError.tooLarge }
            return .audio(pcm)
        case "visitor":
            guard started, let text = object["text"] as? String, text.unicodeScalars.count <= 1000 else {
                throw DebugBackendError.invalidResponse
            }
            return .visitor(text)
        case "completed":
            guard started, let id = object["operationId"] as? String, UUID(uuidString: id) != nil,
                  let correlation = object["correlation"] as? NSDictionary, correlation.isEqual(to: expectedCorrelation),
                  let result = object["result"] as? [String: Any] else { throw DebugBackendError.invalidResponse }
            completed = true
            return .completed(result)
        case "failed":
            guard started, let code = object["code"] as? String, ["ai_unavailable", "operation_cancelled"].contains(code) else {
                throw DebugBackendError.invalidResponse
            }
            throw DebugBackendError.invalidResponse
        default: throw DebugBackendError.invalidResponse
        }
    }
}

// A delegate receives bounded bytes and frames them before any JSON allocation.
// Foundation delegates require Sendable. Mutable lifecycle/framing state is
// protected by the two locks; continuation delivery itself is thread-safe.
final class DebugRealtimeStream: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let request: URLRequest
    private let framer: DebugRealtimeFramer
    private let stateLock = NSLock()
    private let framerLock = NSLock()
    private var continuation: AsyncThrowingStream<DebugRealtimeEvent, Error>.Continuation?
    private var session: URLSession?
    private var finished = false

    init(request: URLRequest, expectedCorrelation: [String: Any]) {
        self.request = request
        framer = DebugRealtimeFramer(expectedCorrelation: expectedCorrelation)
    }

    func events() -> AsyncThrowingStream<DebugRealtimeEvent, Error> {
        AsyncThrowingStream { continuation in
            let configuration = URLSessionConfiguration.ephemeral
            configuration.httpCookieStorage = nil
            configuration.urlCache = nil
            configuration.timeoutIntervalForRequest = 55
            configuration.timeoutIntervalForResource = 55
            let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
            continuation.onTermination = { [weak self] _ in self?.cancel() }
            self.stateLock.lock()
            if self.finished {
                self.stateLock.unlock()
                session.invalidateAndCancel()
                continuation.finish(throwing: CancellationError())
                return
            }
            self.continuation = continuation
            self.session = session
            self.stateLock.unlock()
            session.dataTask(with: self.request).resume()
        }
    }

    func cancel() {
        finish(throwing: CancellationError())
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        stateLock.lock()
        let active = !finished
        stateLock.unlock()
        guard active else {
            completionHandler(.cancel)
            return
        }
        guard let http = response as? HTTPURLResponse, http.statusCode == 200,
              http.mimeType == "application/x-ndjson" else {
            completionHandler(.cancel)
            finish(throwing: DebugBackendError.invalidResponse)
            return
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        stateLock.lock()
        let active = !finished
        stateLock.unlock()
        guard active else { return }
        do {
            framerLock.lock()
            let events: [DebugRealtimeEvent]
            do {
                events = try framer.append(data)
                framerLock.unlock()
            } catch {
                framerLock.unlock()
                throw error
            }
            stateLock.lock()
            let continuation = finished ? nil : self.continuation
            stateLock.unlock()
            for event in events { continuation?.yield(event) }
        } catch { finish(throwing: error) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error { finish(throwing: error); return }
        do {
            framerLock.lock()
            do {
                try framer.finish()
                framerLock.unlock()
            } catch {
                framerLock.unlock()
                throw error
            }
            finish(throwing: nil)
        } catch { finish(throwing: error) }
    }

    private func finish(throwing error: Error?) {
        stateLock.lock()
        guard !finished else {
            stateLock.unlock()
            return
        }
        finished = true
        let continuation = continuation
        self.continuation = nil
        let activeSession = self.session
        self.session = nil
        stateLock.unlock()
        activeSession?.invalidateAndCancel()
        if let error { continuation?.finish(throwing: error) }
        else { continuation?.finish() }
    }
}
