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
        var malformed = wav; malformed[24] = 0
        assert(!DebugBackendClient.validWav(malformed))
        assert(!DebugBackendClient.validWav(wav + Data([0])))
        let peer = Peer(wav: wav)
        let client = DebugBackendClient(http: peer.request)
        let inspectedMode = try await client.inspect(profile)
        assert(inspectedMode == "fake")
        let audio = try await client.run(profile, expectedMode: "fake", phase: { _ in })
        assert(audio == wav && peer.cleaned && peer.mask == 0)
        assert(peer.paths.allSatisfy { !$0.contains("secret") })
        let mismatch = Peer(wav: wav)
        do { _ = try await DebugBackendClient(http: mismatch.request).run(profile, expectedMode: "openai", phase: { _ in }); fatalError("Mode mismatch accepted") }
        catch DebugBackendError.wrongMode { }
        assert(!mismatch.paths.contains("/v1/conversations"))
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
        print("Native debug profile, mode, correlation, WAV, cancellation and cleanup tests passed")
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
        assert(request.value(forHTTPHeaderField: "Authorization") == (path.hasSuffix("/token") ? "Bearer test-secret" : "Bearer access"))
        let body = try request.httpBody.map { try JSONSerialization.jsonObject(with: $0) as! [String: Any] } ?? [:]
        var result: [String: Any] = [:]
        var mime = "application/json"
        var status = 200
        var data: Data?
        if path == "/v1/device-access/token" { result = ["deviceId": "debug-test", "accessToken": "access"] }
        else if path == "/v1/diagnostics" { result = ["voiceMode": "fake", "catalogVersion": "ollivander-private-party-v1", "audioFormat": "pcm16-mono-32000-wav"] }
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
