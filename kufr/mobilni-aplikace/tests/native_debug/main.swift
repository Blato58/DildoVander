import Foundation

@main
struct NativeDebugTests {
    @MainActor static func main() async throws {
        let profile = try DebugBackendProfile(address: "https://debug.example.test", deviceId: "debug-test", credential: "test-secret")
        for address in ["http://debug.example.test", "https://secret@debug.example.test", "https://debug.example.test/path", "https://debug.example.test?token=secret"] {
            do { _ = try DebugBackendProfile(address: address, deviceId: "debug-test", credential: "secret"); fatalError("Invalid profile accepted") }
            catch DebugBackendError.invalidProfile { }
        }
        do { _ = try DebugBackendProfile(address: "https://example.test", deviceId: "real-esp", credential: "secret"); fatalError("Physical identity accepted") }
        catch DebugBackendError.invalidProfile { }
        let wav = makeWav()
        assert(DebugBackendClient.validWav(wav))
        assert(!DebugBackendClient.validWav(Data()))
        var malformed = wav; malformed[25] = 0
        assert(!DebugBackendClient.validWav(malformed))
        assert(!DebugBackendClient.validWav(wav + Data([0])))
        let peer = Peer(wav: wav)
        let client = DebugBackendClient(http: peer.request)
        let inspectedMode = try await client.inspect(profile)
        assert(inspectedMode == "fake")
        let server = try await client.serverModes(profile)
        assert(server.dialogMode == "fake" && server.voiceAvailable)
        let changed = try await client.serverModes(profile, expectedRevision: server.revision,
                                                    dialogMode: "responses", voiceMode: "openai")
        assert(changed.dialogMode == "responses" && changed.voiceMode == "openai")
        for failure in ["admin-denied", "admin-conflict", "admin-invalid"] {
            let bad = Peer(wav: wav, failure: failure)
            do { _ = try await DebugBackendClient(http: bad.request).serverModes(profile); fatalError("Invalid admin response accepted") }
            catch { }
        }
        let audio = try await client.run(profile, expectedMode: "fake", phase: { _ in })
        assert(audio == wav && peer.cleaned && peer.mask == 0)
        assert(peer.paths.allSatisfy { !$0.contains("secret") })
        let mismatch = Peer(wav: wav)
        do { _ = try await DebugBackendClient(http: mismatch.request).run(profile, expectedMode: "openai", phase: { _ in }); fatalError("Mode mismatch accepted") }
        catch DebugBackendError.wrongMode { }
        assert(!mismatch.paths.contains("/v1/conversations"))
        for failure in ["mode-race", "paid-dialog-fake-voice"] {
            let bad = Peer(wav: wav, failure: failure)
            do { _ = try await DebugBackendClient(http: bad.request).run(profile, expectedMode: "fake", phase: { _ in }); fatalError("Paid fake test accepted") }
            catch DebugBackendError.wrongMode { }
            assert(!bad.paths.contains(where: { $0.hasSuffix("/opening") }))
            assert(failure != "mode-race" || bad.cleaned)
        }
        for failure in ["mime", "correlation", "oversize", "cancel"] {
            let bad = Peer(wav: wav, failure: failure)
            do { _ = try await DebugBackendClient(http: bad.request).run(profile, expectedMode: "fake", phase: { _ in }); fatalError("Failure accepted") }
            catch { }
            assert(bad.cleaned, "Failure must clean owned conversation")
        }
        let noOwnership = Peer(wav: wav, failure: "conflict")
        do { _ = try await DebugBackendClient(http: noOwnership.request).run(profile, expectedMode: "fake", phase: { _ in }); fatalError("Conflict accepted") }
        catch { }
        assert(!noOwnership.cleaned)
        let lostCreate = Peer(wav: wav, failure: "lost-create")
        _ = try await DebugBackendClient(http: lostCreate.request).run(profile, expectedMode: "fake", phase: { _ in })
        assert(lostCreate.cleaned && lostCreate.createAttempts == 2)
        let lostDelete = Peer(wav: wav, failure: "lost-delete")
        _ = try await DebugBackendClient(http: lostDelete.request).run(profile, expectedMode: "fake", phase: { _ in })
        assert(lostDelete.cleaned && lostDelete.deleteAttempts == 2)
        let failedCleanup = Peer(wav: wav, failure: "failed-cleanup")
        do { _ = try await DebugBackendClient(http: failedCleanup.request).run(profile, expectedMode: "fake", phase: { _ in }); fatalError("Failed cleanup accepted") }
        catch { }
        assert(!failedCleanup.cleaned && failedCleanup.deleteAttempts == 2)
        let blocked = Peer(wav: wav, failure: "blocked")
        let cancelledRun = Task { @MainActor in
            try await DebugBackendClient(http: blocked.request).run(profile, expectedMode: "fake", phase: { _ in })
        }
        for _ in 0..<1000 {
            if blocked.paths.contains(where: { $0.hasPrefix("/v1/operations/") }) { break }
            await Task.yield()
        }
        assert(blocked.paths.contains(where: { $0.hasPrefix("/v1/operations/") }))
        cancelledRun.cancel()
        do { _ = try await cancelledRun.value; fatalError("Cancelled run returned audio") }
        catch { }
        assert(blocked.cleaned && !blocked.paths.contains(where: { $0.hasSuffix("/speech") }))
        let createBlocked = Peer(wav: wav, failure: "blocked-create")
        let cancelledCreate = Task { @MainActor in
            try await DebugBackendClient(http: createBlocked.request).run(profile, expectedMode: "fake", phase: { _ in })
        }
        for _ in 0..<1000 {
            if createBlocked.createAttempts == 1 { break }
            await Task.yield()
        }
        assert(createBlocked.createAttempts == 1)
        cancelledCreate.cancel()
        do { _ = try await cancelledCreate.value; fatalError("Cancelled create returned audio") }
        catch { }
        assert(createBlocked.cleaned && createBlocked.createAttempts == 2)
        assert(!createBlocked.paths.contains(where: { $0.hasSuffix("/opening") }))
        try await testInteractiveSessions(profile: profile, wav: wav)
        try await testHTTPTransport()
        print("Native debug profile, mode, correlation, WAV, cancellation and cleanup tests passed")
    }

    @MainActor static func testInteractiveSessions(profile: DebugBackendProfile, wav: Data) async throws {
        let invalidOptionsPeer = InteractivePeer(wav: wav)
        let invalidOptionsClient = DebugBackendClient(http: invalidOptionsPeer.request)
        let invalidOptions: [(String, Double, Int)] = [
            ("alloy", 1.1, 63), ("cedar", .infinity, 63),
            ("cedar", 1.3, 63), ("cedar", 1.1, 64)
        ]
        for (voice, speed, mask) in invalidOptions {
            do {
                _ = try await invalidOptionsClient.openSession(profile, expectedMode: "fake",
                    readyMask: mask, voice: voice, speed: speed)
                fatalError("Invalid interactive options accepted")
            } catch DebugBackendError.invalidResponse { }
        }
        assert(invalidOptionsPeer.createAttempts == 0)

        let peer = InteractivePeer(wav: wav, failure: "lost-release")
        let client = DebugBackendClient(http: peer.request)
        let session = try await client.openSession(profile, expectedMode: "fake", readyMask: 0b101010,
                                                   voice: "cedar", speed: 1.1)
        var replies: [String] = []
        let opening = try await session.advance(nil, phase: { _ in }, reply: { replies.append($0) })
        assert(!opening.terminal && opening.selected == nil && opening.wav == wav)
        assert(opening.timings["totalMs"] != nil)
        try await session.releaseSpeech()
        assert(peer.releaseAttempts == 2 && peer.releaseKeys.count == 2 &&
               peer.releaseKeys[0] == peer.releaseKeys[1], "Lost release retries the same key")
        let transcription = try await session.transcribe(wav)
        assert(transcription == "Moje vlastní mluvená odpověď")
        for answer in [transcription, "Kreslím", "Zkouším nové věci"] {
            let next = try await session.advance(answer, phase: { _ in }, reply: { replies.append($0) })
            assert(!next.terminal && next.selected == nil)
            try await session.releaseSpeech()
        }
        let proposed = try await session.advance("Přátelé mi věří", phase: { _ in }, reply: { replies.append($0) })
        assert(proposed.terminal && proposed.selected != nil &&
               [2, 4, 6].contains(proposed.selected!) && replies.count == 5)
        try await session.releaseSpeech()
        do { _ = try await session.advance("Ještě jednou", phase: { _ in }, reply: { _ in }); fatalError("Terminal turn accepted") }
        catch DebugBackendError.invalidResponse { }
        async let firstClose: Void = session.close()
        async let secondClose: Void = session.close()
        try await firstClose; try await secondClose
        assert(peer.closeAttempts == 1, "Concurrent close cleans once")

        let emptyPeer = InteractivePeer(wav: wav)
        let empty = try await DebugBackendClient(http: emptyPeer.request).openSession(
            profile, expectedMode: "fake", readyMask: 0, voice: "marin", speed: 1.0)
        let noSelection = try await empty.advance(nil, phase: { _ in }, reply: { _ in })
        assert(noSelection.terminal && noSelection.selected == nil)
        try await empty.releaseSpeech(); try await empty.close()

        for failure in ["correlation", "proposal-correlation", "unavailable", "expired", "late-expiry"] {
            let badPeer = InteractivePeer(wav: wav, failure: failure)
            let bad = try await DebugBackendClient(http: badPeer.request).openSession(
                profile, expectedMode: "fake", readyMask: 0b000010, voice: "cedar", speed: 1.1)
            if failure == "correlation" {
                do { _ = try await bad.advance(nil, phase: { _ in }, reply: { _ in }); fatalError("Bad correlation accepted") }
                catch DebugBackendError.invalidResponse { }
            } else {
                for answer in [nil, "a", "b", "c"] as [String?] {
                    _ = try await bad.advance(answer, phase: { _ in }, reply: { _ in })
                    try await bad.releaseSpeech()
                }
                do { _ = try await bad.advance("d", phase: { _ in }, reply: { _ in }); fatalError("Bad proposal accepted") }
                catch DebugBackendError.invalidResponse { }
                if failure == "late-expiry" {
                    assert(badPeer.audioRequests == 5, "Expiry after download rejects a completed audio response")
                }
            }
            try await bad.close()
            assert(badPeer.closeAttempts == 1)
        }

        let paidFake = InteractivePeer(wav: wav, failure: "paid-fake")
        do {
            _ = try await DebugBackendClient(http: paidFake.request).openSession(
                profile, expectedMode: "fake", readyMask: 63, voice: "cedar", speed: 1.1)
            fatalError("Paid dialog admitted in fake mode")
        } catch DebugBackendError.wrongMode { }
        assert(paidFake.createAttempts == 0)

        let blockedPeer = InteractivePeer(wav: wav, failure: "blocked-turn")
        let blocked = try await DebugBackendClient(http: blockedPeer.request).openSession(
            profile, expectedMode: "fake", readyMask: 63, voice: "cedar", speed: 1.1)
        let pending = Task { @MainActor in
            try await blocked.advance(nil, phase: { _ in }, reply: { _ in })
        }
        for _ in 0..<1000 {
            if blockedPeer.pollStarted { break }
            await Task.yield()
        }
        assert(blockedPeer.pollStarted)
        pending.cancel()
        do { _ = try await pending.value; fatalError("Cancelled turn completed") }
        catch { }
        try await blocked.close()
        assert(blockedPeer.closeAttempts == 1 && blockedPeer.speechAttempts == 0)
    }

    @MainActor static func testHTTPTransport() async throws {
        guard let address = ProcessInfo.processInfo.environment["KUFR_DEBUG_HTTP_PEER"],
              let baseURL = URL(string: address) else { fatalError("HTTP peer address missing") }
        let transport = DebugHTTPTransport()
        defer { transport.invalidate() }
        func fetch(_ path: String, limit: Int) async throws -> (Data, HTTPURLResponse) {
            try await transport.request(URLRequest(url: baseURL.appendingPathComponent(path)), limit: limit)
        }
        let (chunks, success) = try await fetch("chunks", limit: 11)
        assert(success.statusCode == 200 && String(data: chunks, encoding: .utf8) == "hello world")
        for path in ["known-oversize", "chunked-oversize"] {
            do { _ = try await fetch(path, limit: 8); fatalError("Oversized HTTP response accepted") }
            catch DebugBackendError.tooLarge { }
        }
        let (_, redirect) = try await fetch("redirect", limit: 16)
        assert(redirect.statusCode == 302, "Transport must not follow redirects")
        let hanging = Task { @MainActor in try await fetch("hang", limit: 16) }
        try await Task.sleep(nanoseconds: 100_000_000)
        hanging.cancel()
        do { _ = try await hanging.value; fatalError("Cancelled HTTP request completed") }
        catch is CancellationError { }
    }

    static func makeWav() -> Data {
        var bytes = [UInt8](repeating: 0, count: 46)
        func tag(_ offset: Int, _ text: String) { bytes.replaceSubrange(offset..<(offset + 4), with: text.utf8) }
        func number(_ offset: Int, _ value: UInt32, _ count: Int) {
            for i in 0..<count { bytes[offset + i] = UInt8(truncatingIfNeeded: value >> (8 * i)) }
        }
        tag(0, "RIFF"); tag(8, "WAVE"); tag(12, "fmt "); tag(36, "data")
        number(4, 38, 4); number(16, 16, 4); number(20, 1, 2); number(22, 1, 2)
        number(24, 32_000, 4); number(28, 64_000, 4); number(32, 2, 2); number(34, 16, 2); number(40, 2, 4)
        return Data(bytes)
    }
}

@MainActor
final class Peer {
    let wav: Data
    let failure: String
    var paths: [String] = []
    var cleaned = false
    var mask: Int?
    var correlation: [String: Any] = [:]
    var createAttempts = 0
    var createKey: String?
    var createBody: Data?
    var deleteAttempts = 0
    let modeRevision = UUID().uuidString.lowercased()
    var deleteKey: String?
    let conversation = UUID().uuidString.lowercased()
    let opening = UUID().uuidString.lowercased()
    let speech = UUID().uuidString.lowercased()
    let audio = UUID().uuidString.lowercased()
    init(wav: Data, failure: String = "") { self.wav = wav; self.failure = failure }

    func request(_ request: URLRequest, _ limit: Int) async throws -> (Data, HTTPURLResponse) {
        let path = request.url!.path
        paths.append(path)
        assert(request.url!.scheme == "https")
        assert(request.value(forHTTPHeaderField: "Authorization") == (path.hasSuffix("/token") || path == "/v1/admin/modes" ? "Bearer test-secret" : "Bearer access"))
        let body = try request.httpBody.map { try JSONSerialization.jsonObject(with: $0) as! [String: Any] } ?? [:]
        var result: [String: Any] = [:]
        var mime = "application/json"
        var status = 200
        var data: Data?
        if path == "/v1/device-access/token" { result = ["deviceId": "debug-test", "accessToken": "access"] }
        else if path == "/v1/diagnostics" {
            result = ["voiceMode": failure == "mode-race" && createAttempts > 0 ? "openai" : "fake",
                      "dialogMode": failure == "paid-dialog-fake-voice" ? "responses" : "fake",
                      "catalogVersion": "ollivander-private-party-v1", "audioFormat": "pcm16-mono-32000-wav"]
        }
        else if path == "/v1/admin/modes" {
            if request.httpMethod == "PUT" { assert(body["expectedRevision"] as? String == modeRevision) }
            if failure == "admin-denied" { status = 401 }
            if failure == "admin-conflict" { status = 409 }
            result = ["revision": modeRevision, "dialogMode": body["dialogMode"] ?? "fake",
                      "voiceMode": body["voiceMode"] ?? "fake", "dialogAvailable": true, "voiceAvailable": true,
                      "activeConversations": 0, "lifetime": failure == "admin-invalid" ? "unexpected" : "until_restart"]
        }
        else if path == "/v1/conversations" {
            createAttempts += 1
            if failure == "blocked-create" {
                if createAttempts == 1 {
                    createKey = request.value(forHTTPHeaderField: "Idempotency-Key")
                    createBody = request.httpBody
                    try await Task.sleep(nanoseconds: 60_000_000_000)
                } else {
                    assert(createKey == request.value(forHTTPHeaderField: "Idempotency-Key"))
                    assert(createBody == request.httpBody)
                }
            }
            if failure == "lost-create" {
                if createAttempts == 1 {
                    createKey = request.value(forHTTPHeaderField: "Idempotency-Key")
                    createBody = request.httpBody
                    throw URLError(.networkConnectionLost)
                }
                assert(createKey == request.value(forHTTPHeaderField: "Idempotency-Key"))
                assert(createBody == request.httpBody)
            }
            if failure == "conflict" { status = 409 }
            result = ["conversationId": conversation]
        } else if request.httpMethod == "DELETE" {
            deleteAttempts += 1
            if failure == "failed-cleanup" { throw URLError(.networkConnectionLost) }
            if failure == "lost-delete" {
                if deleteAttempts == 1 {
                    deleteKey = request.value(forHTTPHeaderField: "Idempotency-Key")
                    throw URLError(.networkConnectionLost)
                }
                assert(deleteKey == request.value(forHTTPHeaderField: "Idempotency-Key"))
            }
            assert(path == "/v1/conversations/\(conversation)")
            assert(request.value(forHTTPHeaderField: "Idempotency-Key") != nil)
            cleaned = true; status = 204; data = Data()
        } else if path.hasSuffix("/opening") {
            mask = body["readyMask"] as? Int
            correlation = ["deviceId": "debug-test", "bootId": body["bootId"]!, "conversationId": conversation,
                           "clientTurnId": body["clientTurnId"]!, "generation": 0, "deviceRevision": 0]
            result = ["operationId": opening]
        } else if path.hasSuffix("/speech") {
            assert(body["outputTarget"] as? String == "phone")
            assert(body["turnId"] as? String == correlation["clientTurnId"] as? String)
            result = ["operationId": speech]
        } else if path.hasPrefix("/v1/operations/") {
            if failure == "blocked" { try await Task.sleep(nanoseconds: 60_000_000_000) }
            if failure == "cancel" { throw CancellationError() }
            var received = correlation
            if failure == "correlation" { received["generation"] = 1 }
            let isOpening = path.hasSuffix(opening)
            result = ["operationId": isOpening ? opening : speech, "kind": isOpening ? "turn" : "speech",
                      "status": "completed", "correlation": received,
                      "result": isOpening ? ["reply": "synthetic"] : ["audioId": audio, "outputTarget": "phone"]]
        } else if path == "/v1/audio/\(audio)" {
            mime = failure == "mime" ? "text/html" : "audio/wav"
            data = failure == "oversize" ? Data(repeating: 0, count: limit + 1) : wav
        } else { fatalError("Unexpected request") }
        return (try data ?? JSONSerialization.data(withJSONObject: result), HTTPURLResponse(url: request.url!, statusCode: status,
            httpVersion: "HTTP/1.1", headerFields: ["Content-Type": mime])!)
    }
}

@MainActor
final class InteractivePeer {
    let wav: Data
    let failure: String
    let conversation = UUID().uuidString.lowercased()
    var createAttempts = 0
    var closeAttempts = 0
    var releaseAttempts = 0
    var releaseKeys: [String] = []
    var speechAttempts = 0
    var audioRequests = 0
    var pollStarted = false
    private var boot = ""
    private var readyMask = 0
    private var turnCount = 0
    private var lastTurn = ""
    private var operations: [String: [String: Any]] = [:]

    init(wav: Data, failure: String = "") {
        self.wav = wav
        self.failure = failure
    }

    func request(_ request: URLRequest, _ limit: Int) async throws -> (Data, HTTPURLResponse) {
        let path = request.url!.path
        let method = request.httpMethod ?? "GET"
        let multipart = path.hasSuffix("/transcriptions")
        var body: [String: Any] = [:]
        if !multipart, let requestBody = request.httpBody {
            body = try JSONSerialization.jsonObject(with: requestBody) as? [String: Any] ?? [:]
        }
        var result: [String: Any] = [:]
        var status = 200
        var mime = "application/json"
        var data: Data?

        if path == "/v1/device-access/token" {
            result = ["deviceId": "debug-test", "accessToken": "access"]
        } else if path == "/v1/diagnostics" {
            result = ["voiceMode": "fake", "dialogMode": failure == "paid-fake" ? "responses" : "fake",
                      "catalogVersion": "ollivander-private-party-v1", "audioFormat": "pcm16-mono-32000-wav"]
        } else if path == "/v1/conversations" {
            createAttempts += 1
            boot = body["bootId"] as! String
            assert(body["deviceId"] as? String == "debug-test")
            result = ["conversationId": conversation]
        } else if method == "DELETE" && path == "/v1/conversations/\(conversation)" {
            closeAttempts += 1
            status = 204
            data = Data()
        } else if method == "DELETE" && path.hasPrefix("/v1/operations/") {
            releaseAttempts += 1
            releaseKeys.append(request.value(forHTTPHeaderField: "Idempotency-Key") ?? "")
            if failure == "lost-release" && releaseAttempts == 1 {
                throw URLError(.networkConnectionLost)
            }
            status = 204
            data = Data()
        } else if path.hasSuffix("/opening") || path.hasSuffix("/turns") {
            let opening = path.hasSuffix("/opening")
            assert(opening == (turnCount == 0))
            readyMask = body["readyMask"] as! Int
            assert(body["bootId"] as? String == boot && body["deviceRevision"] as? Int == 0)
            if !opening { assert((body["text"] as? String)?.isEmpty == false) }
            turnCount += 1
            let turn = body["clientTurnId"] as! String
            lastTurn = turn
            var decision = "ask"
            if readyMask == 0 { decision = "no_selection" }
            else if turnCount == 5 { decision = "propose" }
            var proposal: [String: Any]? = nil
            if decision == "propose" {
                let selected = failure == "unavailable" ? 1 : 2
                let expiry = Date().addingTimeInterval(failure == "expired" ? -1
                    : failure == "late-expiry" ? 2 : 60)
                let dateFormatter = ISO8601DateFormatter()
                dateFormatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                proposal = ["proposalId": UUID().uuidString.lowercased(), "conversationId": conversation,
                            "turnId": failure == "proposal-correlation" ? UUID().uuidString.lowercased() : turn,
                            "bootId": boot, "catalogVersion": "ollivander-private-party-v1",
                            "deviceRevision": 0, "generation": 0, "compartment": selected,
                            "expiresAt": dateFormatter.string(from: expiry)]
            }
            let turnResult: [String: Any] = ["reply": decision == "ask" ? "Zkušební otázka \(turnCount)?" : "Zkušební výběr.",
                                             "decision": decision, "reason": "",
                                             "proposal": proposal.map { $0 as Any } ?? NSNull()]
            let operation = UUID().uuidString.lowercased()
            operations[operation] = makeOperation(id: operation, kind: "turn", turn: turn, result: turnResult)
            result = ["operationId": operation]
        } else if path.hasSuffix("/speech") {
            speechAttempts += 1
            assert(body["turnId"] as? String == lastTurn && body["outputTarget"] as? String == "phone")
            let voice = body["voice"] as? String
            let speed = (body["speed"] as? NSNumber)?.doubleValue
            assert(["cedar", "marin"].contains(voice ?? "") && [1.0, 1.1].contains(speed ?? -1))
            let operation = UUID().uuidString.lowercased()
            let audio = UUID().uuidString.lowercased()
            operations[operation] = makeOperation(id: operation, kind: "speech", turn: lastTurn,
                                                  result: ["audioId": audio, "outputTarget": "phone"])
            result = ["operationId": operation]
            operations[audio] = ["audio": audio]
        } else if multipart {
            let contentType = request.value(forHTTPHeaderField: "Content-Type") ?? ""
            // Multipart headers are UTF-8, but the WAV part is arbitrary binary.
            let upload = String(decoding: request.httpBody!, as: UTF8.self)
            assert(request.httpBody!.range(of: wav) != nil, "Multipart preserves exact WAV bytes")
            assert(contentType.hasPrefix("multipart/form-data; boundary="))
            assert(upload.contains("name=\"wav\"; filename=\"answer.wav\"") &&
                   upload.contains("Content-Type: audio/wav") && upload.contains("RIFF"))
            for field in ["clientTurnId", "bootId", "generation", "deviceRevision"] {
                assert(upload.contains("name=\"\(field)\""))
            }
            assert(upload.contains("name=\"bootId\"\r\n\r\n\(boot)\r\n") &&
                   upload.contains("name=\"generation\"\r\n\r\n0\r\n") &&
                   upload.contains("name=\"deviceRevision\"\r\n\r\n0\r\n"))
            let marker = "name=\"clientTurnId\"\r\n\r\n"
            guard let markerRange = upload.range(of: marker) else { fatalError("Missing multipart turn ID") }
            let turn = String(upload[markerRange.upperBound...].prefix(36))
            assert(UUID(uuidString: turn) != nil)
            let operation = UUID().uuidString.lowercased()
            operations[operation] = makeOperation(id: operation, kind: "transcription", turn: turn,
                                                  result: ["transcript": "Moje vlastní mluvená odpověď", "source": "deviceMicrophone"])
            result = ["operationId": operation]
            status = 202
        } else if path.hasPrefix("/v1/operations/") {
            pollStarted = true
            if failure == "blocked-turn" { try await Task.sleep(nanoseconds: 60_000_000_000) }
            let operation = String(path.dropFirst("/v1/operations/".count))
            result = operations[operation]!
            if failure == "correlation" && result["kind"] as? String == "turn" {
                var changed = result["correlation"] as! [String: Any]
                changed["generation"] = 1
                result["correlation"] = changed
            }
        } else if path.hasPrefix("/v1/audio/") {
            audioRequests += 1
            if failure == "late-expiry" && turnCount == 5 {
                try await Task.sleep(nanoseconds: 3_000_000_000)
            }
            mime = "audio/wav"
            data = wav
        } else {
            fatalError("Unexpected interactive request: \(path)")
        }

        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: "HTTP/1.1",
                                       headerFields: ["Content-Type": mime])!
        return (try data ?? JSONSerialization.data(withJSONObject: result), response)
    }

    private func makeOperation(id: String, kind: String, turn: String, result: [String: Any]) -> [String: Any] {
        ["operationId": id, "kind": kind, "status": "completed",
         "correlation": ["deviceId": "debug-test", "bootId": boot, "conversationId": conversation,
                         "clientTurnId": turn, "generation": 0, "deviceRevision": 0],
         "result": result]
    }
}
