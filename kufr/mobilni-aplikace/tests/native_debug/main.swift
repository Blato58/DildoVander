import Foundation

@main
struct NativeDebugTests {
    @MainActor static func main() async throws {
        if let service = ProcessInfo.processInfo.environment["KUFR_DEBUG_KEYCHAIN_SERVICE"],
           let command = CommandLine.arguments.dropFirst().first {
            let keychain = DebugBackendKeychain(service: service)
            switch command {
            case "--profile-write":
                try keychain.write(DebugBackendProfile(address: DebugProfileDraft.defaultAddress,
                    deviceId: DebugProfileDraft.defaultDeviceId, credential: "synthetic-persistent-secret"))
            case "--profile-read":
                let restored = try keychain.read()
                assert(restored?.baseURL.absoluteString == DebugProfileDraft.defaultAddress)
                assert(restored?.deviceId == "debug-server-admin")
                assert(restored?.credential == "synthetic-persistent-secret")
                try keychain.write(DebugBackendProfile(address: DebugProfileDraft.defaultAddress,
                    deviceId: DebugProfileDraft.defaultDeviceId, credential: "synthetic-updated-secret"))
                let updated = try DebugBackendKeychain(service: service).read()
                assert(updated?.credential == "synthetic-updated-secret")
            case "--profile-remove":
                try keychain.remove()
                let removed = try keychain.read()
                assert(removed == nil)
            default: fatalError("Unknown profile test command")
            }
            return
        }
        checkpoint("profile, WAV and server modes")
        let profile = try DebugBackendProfile(address: "https://debug.example.test", deviceId: "debug-test", credential: "test-secret")
        let retained = try DebugProfileDraft.resolve(address: "https://DEBUG.example.test:443/",
            deviceId: "debug-test", credential: "", saved: profile)
        assert(retained.credential == "test-secret")
        for (address, identity) in [("https://other.example.test", "debug-test"),
                                    ("https://debug.example.test", "debug-other"),
                                    ("https://debug.example.test:444", "debug-test"),
                                    ("https://debug.example.test/path", "debug-test"),
                                    ("https://user@debug.example.test", "debug-test")] {
            do { _ = try DebugProfileDraft.resolve(address: address, deviceId: identity, credential: "", saved: profile)
                fatalError("Credential forwarded to a changed destination") }
            catch DebugBackendError.invalidProfile { }
        }
        do { _ = try DebugProfileDraft.resolve(address: DebugProfileDraft.defaultAddress,
            deviceId: DebugProfileDraft.defaultDeviceId, credential: "", saved: nil)
            fatalError("Empty initial credential accepted") }
        catch DebugBackendError.invalidProfile { }
        for address in ["http://debug.example.test", "https://secret@debug.example.test", "https://debug.example.test/path", "https://debug.example.test?token=secret"] {
            do { _ = try DebugBackendProfile(address: address, deviceId: "debug-test", credential: "secret"); fatalError("Invalid profile accepted") }
            catch DebugBackendError.invalidProfile { }
        }
        do { _ = try DebugBackendProfile(address: "https://example.test", deviceId: "real-esp", credential: "secret"); fatalError("Physical identity accepted") }
        catch DebugBackendError.invalidProfile { }
        let wav = makeWav()
        try await verifyAccountTransport()
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
        assert(server.dialogMode == "fake" && server.voiceAvailable && server.personalities?.count == 2)
        let changed = try await client.serverModes(profile, expectedRevision: server.revision,
                                                    dialogMode: "responses", voiceMode: "openai", personalityId: "ollivander-trunk")
        assert(changed.dialogMode == "responses" && changed.voiceMode == "openai" && changed.personalityId == "ollivander-trunk")
        for failure in ["admin-denied", "admin-conflict", "admin-invalid", "personality-unknown", "personality-duplicate", "personality-hash", "personality-partial"] {
            let bad = Peer(wav: wav, failure: failure)
            do { _ = try await DebugBackendClient(http: bad.request).serverModes(profile); fatalError("Invalid admin response accepted") }
            catch { }
        }
        checkpoint("opening, failure and idempotent cleanup")
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
        checkpoint("cancellation while polling")
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
        checkpoint("cancellation during create")
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
        checkpoint("interactive sessions")
        try await testInteractiveSessions(profile: profile, wav: wav)
        checkpoint("evenings")
        try await testEvenings(profile: profile)
        checkpoint("server logs")
        try await testServerLogs(profile: profile)
        checkpoint("HTTP transport")
        try await testHTTPTransport()
        print("Native debug profile, mode, correlation, WAV, cancellation and cleanup tests passed")
    }

    private static func checkpoint(_ label: String) {
        // stderr remains available when an assertion traps before stdout flushes.
        FileHandle.standardError.write(Data("native-debug: \(label)\n".utf8))
    }

    @MainActor static func testServerLogs(profile: DebugBackendProfile) async throws {
        let store = UUID().uuidString.lowercased()
        let entry: [String: Any] = ["id": 7, "timestamp": "2026-10-01T12:00:00.1234567+00:00",
            "level": "warning", "eventCode": "realtime_failed", "code": "cost_limit",
            "traceId": UUID().uuidString.lowercased(), "privateExtra": "excluded"]
        let page: [String: Any] = ["storeId": store, "oldestCursor": 1, "latestCursor": 7,
            "nextCursor": 7, "hasMore": false, "truncated": false, "droppedWrites": 0, "entries": [entry]]
        let client = DebugBackendClient(http: { request, limit in
            assert(request.httpMethod == "GET" && request.url?.path == "/v1/admin/logs")
            assert(request.value(forHTTPHeaderField: "Authorization") == "Bearer test-secret")
            assert(request.url?.query == "limit=200&level=warning&after=6")
            assert(limit == 262_144)
            return (try JSONSerialization.data(withJSONObject: page), HTTPURLResponse(url: request.url!, statusCode: 200,
                httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
        })
        let received = try await client.serverLogs(profile, after: 6, level: "warning")
        assert(received.nextCursor == 7 && received.entries.count == 1)
        let projected = try JSONSerialization.data(withJSONObject: received.bridgeValue)
        assert(!String(data: projected, encoding: .utf8)!.contains("excluded"))
        for failure in ["denied", "oversize", "order", "cursor", "level", "identity", "mime"] {
            var invalid = page
            if failure == "order" { invalid["entries"] = [entry, entry] }
            if failure == "cursor" { invalid["nextCursor"] = 0 }
            if failure == "level" { invalid["entries"] = [entry.merging(["level": "private-text"]) { _, new in new }] }
            if failure == "identity" { invalid["storeId"] = "private-secret" }
            let payload = try JSONSerialization.data(withJSONObject: invalid)
            let bad = DebugBackendClient(http: { request, limit in
                (failure == "oversize" ? Data(repeating: 0, count: limit + 1) : payload,
                 HTTPURLResponse(url: request.url!, statusCode: failure == "denied" ? 401 : 200,
                    httpVersion: nil, headerFields: ["Content-Type": failure == "mime" ? "text/html" : "application/json"])!)
            })
            do { _ = try await bad.serverLogs(profile); fatalError("Invalid log response accepted") }
            catch { }
        }
        do { _ = try await client.serverLogs(profile, after: -1); fatalError("Negative cursor accepted") }
        catch DebugBackendError.invalidResponse { }
    }

    @MainActor static func testEvenings(profile: DebugBackendProfile) async throws {
        let peer = EveningPeer()
        let client = DebugBackendClient(http: peer.request)
        let original = try await client.eveningList(profile)
        assert(original.evenings[0].exchangeCount == 42)
        let created = try await client.eveningList(profile, action: "create", name: "Friday",
            expectedEveningId: peer.first)
        assert(created.activeEveningId == peer.second && created.evenings[0].exchangeCount == 42)
        let restored = try await client.eveningList(profile, action: "activate", eveningId: peer.first,
            expectedEveningId: peer.second)
        assert(restored.activeEveningId == peer.first)

        let lost = EveningPeer(failure: "lost")
        let afterReplay = try await DebugBackendClient(http: lost.request).eveningList(profile,
            action: "create", name: "Friday", expectedEveningId: lost.first)
        assert(lost.attempts == 2 && afterReplay.activeEveningId == lost.first,
               "Ambiguous retry uses the same key and then reads the authoritative pointer")
        let maximum = EveningPeer(failure: "maximum")
        let maximumList = try await DebugBackendClient(http: maximum.request).eveningList(profile)
        assert(maximumList.evenings.count == 100 && maximum.payloadBytes > 65_536)
        for failure in ["conflict", "invalid-active", "duplicate"] {
            let bad = EveningPeer(failure: failure)
            do { _ = try await DebugBackendClient(http: bad.request).eveningList(profile,
                action: failure == "conflict" ? "create" : "list", name: "Friday", expectedEveningId: bad.first)
                fatalError("Invalid evening response accepted") }
            catch { }
            assert(failure != "conflict" || bad.attempts == 1, "HTTP conflicts must not retry")
        }
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

// Exercise the actual client with a native-only authorizer seam. The account
// fixture has no raw device/admin credential and cannot mint a device token.
@MainActor private func verifyAccountTransport() async throws {
    let profile = try DebugBackendProfile(accountOrigin: URL(string: "https://accounts.example.test/")!,
        deviceId: "physical-kufr-1", binding: "native-session-one")
    assert(profile.usesAccountSession && profile.credential.isEmpty)
    let peer = AccountDebugPeer()
    let client = DebugBackendClient(http: peer.request, accountAuthorizer: peer.authorize)
    let state = try await client.inspectAvailability(profile)
    assert(state.dialogMode == "responses" && state.voiceMode == "openai")
    assert(peer.paths == ["/v1/diagnostics"] && peer.authorizationCount == 1)
    peer.binding = "native-session-two"
    do { _ = try await client.inspectAvailability(profile); fatalError("Previous account binding reused") }
    catch DebugBackendError.invalidProfile { }
    assert(peer.paths.count == 1)
    let unconfigured = DebugBackendClient(http: peer.request)
#if !os(iOS)
    do { _ = try await unconfigured.inspectAvailability(profile); fatalError("Account request fell back to bearer transport") }
    catch DebugBackendError.invalidProfile { }
#endif
    do { try DebugBackendKeychain(service: "kufr-native-account-transport-test").write(profile)
        fatalError("Account binding persisted as developer credential") }
    catch DebugBackendError.invalidProfile { }
}

@MainActor private final class AccountDebugPeer {
    var binding = "native-session-one"
    var authorizationCount = 0
    var paths: [String] = []
    func authorize(_ original: URLRequest, deviceId: String, binding: String) async throws -> URLRequest {
        guard binding == self.binding else { throw DebugBackendError.invalidProfile }
        assert(original.value(forHTTPHeaderField: "Authorization") == nil)
        assert(deviceId == "physical-kufr-1")
        authorizationCount += 1
        var request = original
        request.setValue("synthetic-native-only-cookie", forHTTPHeaderField: "Cookie")
        request.setValue(deviceId, forHTTPHeaderField: "X-DV-Device-Id")
        return request
    }
    func request(_ request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse) {
        assert(request.value(forHTTPHeaderField: "Authorization") == nil)
        assert(request.value(forHTTPHeaderField: "Cookie") == "synthetic-native-only-cookie")
        assert(request.value(forHTTPHeaderField: "X-DV-Device-Id") == "physical-kufr-1")
        assert(request.url?.path == "/v1/diagnostics")
        paths.append(request.url!.path)
        let data = try JSONSerialization.data(withJSONObject: ["dialogMode": "responses", "voiceMode": "openai",
            "catalogVersion": "ollivander-private-party-v1", "audioFormat": "pcm16-mono-32000-wav"])
        return (data, HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "application/json"])!)
    }
}

@MainActor
final class EveningPeer {
    let first = UUID().uuidString.lowercased()
    let second = UUID().uuidString.lowercased()
    let failure: String
    var active = ""
    var created = false
    var attempts = 0
    var key: String?
    var body: Data?
    var payloadBytes = 0
    init(failure: String = "") { self.failure = failure; active = first }

    func request(_ request: URLRequest, _ limit: Int) async throws -> (Data, HTTPURLResponse) {
        let path = request.url!.path
        let isToken = path.hasSuffix("/token")
        assert(request.value(forHTTPHeaderField: "Authorization") == (isToken ? "Bearer test-secret" : "Bearer access"))
        var status = 200
        var result: [String: Any]
        if isToken {
            result = ["deviceId": "debug-test", "accessToken": "access"]
        } else {
            if request.httpMethod != "GET" {
                attempts += 1
                let fields = try JSONSerialization.jsonObject(with: request.httpBody!) as! [String: Any]
                assert(fields["expectedEveningId"] as? String == (created ? second : first))
                let requestKey = request.value(forHTTPHeaderField: "Idempotency-Key")!
                assert(UUID(uuidString: requestKey) != nil)
                if attempts == 1 { key = requestKey; body = request.httpBody }
                if failure == "lost" {
                    assert(key == requestKey && body == request.httpBody)
                    if attempts == 1 { throw URLError(.networkConnectionLost) }
                    // Another client has activated the previous evening since
                    // this mutation committed; its replay still returns second.
                    created = true
                    active = first
                } else if failure == "conflict" { status = 409 }
                else if request.httpMethod == "POST" { created = true; active = second; status = 201 }
                else { assert(path == "/v1/evenings/\(first)/active"); active = first }
            } else { assert(path == "/v1/evenings") }
            func item(_ id: String, _ name: String, _ count: Int) -> [String: Any] {
                ["eveningId": id, "name": name, "createdAt": "2026-10-01T18:00:00Z",
                 "exchangeCount": count, "droppedCount": 0]
            }
            var evenings = [item(first, "Original", 42)]
            if created { evenings.append(item(second, "Friday", 0)) }
            if failure == "duplicate" { evenings.append(item(first, "Duplicate", 0)) }
            if failure == "maximum" {
                evenings = [item(first, String(repeating: "😀", count: 80), 42)]
                for _ in 1..<100 { evenings.append(item(UUID().uuidString.lowercased(), String(repeating: "😀", count: 80), 0)) }
            }
            result = ["activeEveningId": failure == "invalid-active" ? UUID().uuidString.lowercased()
                : failure == "lost" && request.httpMethod != "GET" ? second : active, "evenings": evenings]
        }
        var data = try JSONSerialization.data(withJSONObject: result)
        if failure == "maximum" && !isToken {
            // Match ASP.NET's default escaped supplementary Unicode encoder.
            data = Data(String(data: data, encoding: .utf8)!.replacingOccurrences(of: "😀", with: "\\uD83D\\uDE00").utf8)
            payloadBytes = data.count
        }
        guard data.count <= limit else { throw DebugBackendError.tooLarge }
        return (data, HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil,
            headerFields: ["Content-Type": "application/json"])!)
    }
}

// Cancellation tests inspect this mutable fake while its async request is
// suspended. Keep both sides on the same actor, as the other test peers do.
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
            if request.httpMethod == "PUT" {
                assert(body["expectedRevision"] as? String == modeRevision)
                assert(body["personalityId"] as? String == "ollivander-trunk")
            }
            if failure == "admin-denied" { status = 401 }
            if failure == "admin-conflict" { status = 409 }
            result = ["revision": modeRevision, "dialogMode": body["dialogMode"] ?? "fake",
                      "voiceMode": body["voiceMode"] ?? "fake", "dialogAvailable": true, "voiceAvailable": true,
                      "activeConversations": 0, "lifetime": failure == "admin-invalid" ? "unexpected" : "until_restart",
                      "personalityId": failure == "personality-unknown" ? "missing" : body["personalityId"] ?? "party-original",
                      "personalityLifetime": "persistent", "personalities": [
                        ["id": "party-original", "name": "Původní party kufr", "description": "4–7 otázek",
                         "promptSha256": String(repeating: "a", count: 64)],
                        ["id": failure == "personality-duplicate" ? "party-original" : "ollivander-trunk",
                         "name": "Ollivanderův kouzelný kufr", "description": "2 odpovědi",
                         "promptSha256": failure == "personality-hash" ? "invalid" : String(repeating: "b", count: 64)]
                      ]]
            if failure == "personality-partial" { result.removeValue(forKey: "personalities") }
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
