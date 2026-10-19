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
        try await testEngineCapabilities(profile: profile)
        try await testSessionOwnership(profile: profile)
        try await testGuestAndMemoryScopes(profile: profile)
        try await testEvenings(profile: profile)
        try await testServerLogs(profile: profile)
        try await testHTTPTransport()
        print("Native profile, two-engine capabilities, session ownership, cleanup, evenings and HTTP tests passed")
    }

    @MainActor static func open(_ client: DebugBackendClient, profile: DebugBackendProfile, live: Bool = true) async throws -> DebugHandsFreeSession {
        try await client.openHandsFreeSession(profile, readyMask: 63, voice: "cedar", speed: 1, gptLive: live,
            phase: { _ in }, visitor: { _ in }, reply: { _ in }, audio: { _ in }, listening: { _ in },
            inputGate: { _ in }, failure: { _ in })
    }

    @MainActor static func testEngineCapabilities(profile: DebugBackendProfile) async throws {
        let peer = NativeSessionPeer()
        let client = DebugBackendClient(http: peer.request)
        let inspected = try await client.inspect(profile)
        assert(inspected == "openai")
        let modes = try await client.serverModes(profile)
        assert(modes.engines.engines.count == 2 && modes.engines.primaryEngine == "gpt-live")
        let changed = try await client.serverModes(profile, expectedRevision: modes.revision, personalityId: "ollivander-trunk")
        assert(changed.personalityId == "ollivander-trunk")
        assert(changed.bridgeValue["dialogMode"] == nil && changed.bridgeValue["voiceMode"] == nil)
        for failure in ["unavailable", "wrong-model", "third-engine", "missing-capabilities"] {
            let bad = NativeSessionPeer(failure: failure)
            do { _ = try await open(DebugBackendClient(http: bad.request), profile: profile); fatalError("Invalid engine admitted") }
            catch { }
            assert(bad.creates == 0)
        }
        for failure in ["admin-denied", "admin-invalid"] {
            let bad = NativeSessionPeer(failure: failure)
            do { _ = try await DebugBackendClient(http: bad.request).serverModes(profile); fatalError("Invalid modes admitted") }
            catch { }
        }
    }

    @MainActor static func testSessionOwnership(profile: DebugBackendProfile) async throws {
        for live in [true, false] {
            let peer = NativeSessionPeer()
            let session = try await open(DebugBackendClient(http: peer.request), profile: profile, live: live)
            try await session.close(); try await session.close()
            assert(peer.creates == 1 && peer.deletes == 1)
        }
        for failure in ["lost-create", "lost-delete", "expired-delete"] {
            let peer = NativeSessionPeer(failure: failure)
            let session = try await open(DebugBackendClient(http: peer.request), profile: profile)
            try await session.close()
            assert(peer.deletes >= 1 && peer.createKeys.count == 1 && peer.deleteKeys.count == 1)
            if failure == "lost-create" { assert(peer.creates == 2) }
            if failure == "lost-delete" { assert(peer.deletes == 2) }
            if failure == "expired-delete" { assert(peer.tokenRequests == 2 && peer.deletes == 2) }
        }
        let failed = NativeSessionPeer(failure: "failed-delete")
        let client = DebugBackendClient(http: failed.request)
        let session = try await open(client, profile: profile)
        do { try await session.close(); fatalError("Unknown cleanup treated as complete") } catch { }
        assert(client.hasPendingCleanup && failed.deletes == 2)
        try await client.retryPendingCleanup()
        assert(!client.hasPendingCleanup && failed.deletes == 3 && failed.deleteKeys.count == 1)
        let cancelled = NativeSessionPeer(failure: "cancel-create")
        let creating = Task { @MainActor in try await open(DebugBackendClient(http: cancelled.request), profile: profile) }
        for _ in 0..<1_000 {
            if cancelled.creates > 0 { break }
            await Task.yield()
        }
        assert(cancelled.creates == 1)
        creating.cancel()
        do { _ = try await creating.value; fatalError("Cancelled create survived") } catch { }
        assert(cancelled.creates == 2 && cancelled.deletes == 1 && cancelled.createKeys.count == 1)
    }

    @MainActor static func testGuestAndMemoryScopes(profile: DebugBackendProfile) async throws {
        let peer = NativeSessionPeer()
        let client = DebugBackendClient(http: peer.request)
        let binding = DebugGuestMemoryBinding(guestId: "host-042", identityVerified: true,
            longTermAllowed: true, cloudExportAllowed: false)
        let session = try await client.openHandsFreeSession(profile, readyMask: 63, voice: "cedar", speed: 1,
            gptLive: true, guestMemory: binding, phase: { _ in }, visitor: { _ in }, reply: { _ in }, audio: { _ in },
            listening: { _ in }, inputGate: { _ in }, failure: { _ in })
        assert(peer.boundGuest == "host-042")
        try await session.close()
        let context = try await client.inspectMemory(profile, conversation: session.conversationId, personality: "ollivander-trunk")
        assert(context["epoch"] as? Int == 7)
        let deleted = try await client.deleteConversationMemory(profile, conversation: session.conversationId, expectedEpoch: 7)
        assert(deleted["deleted"] as? Bool == true)
        do {
            try DebugGuestMemoryBinding(guestId: "unverified", identityVerified: false,
                longTermAllowed: true, cloudExportAllowed: false).validate()
            fatalError("Unverified guest established identity")
        } catch { }
        for invalid in ["", " host-042", "host 042", "host-042 ", "host@042", "host-042\n", "host-042\t", "host:042", "host/042", "žlutý-host", String(repeating: "a", count: 81)] {
            do {
                try DebugGuestMemoryBinding(guestId: invalid, identityVerified: true,
                    longTermAllowed: true, cloudExportAllowed: false).validate()
                fatalError("Invalid opaque guest identifier accepted")
            } catch { }
        }
        try DebugGuestMemoryBinding(guestId: "Host-042._", identityVerified: true,
            longTermAllowed: true, cloudExportAllowed: false).validate()
        let mismatch = DebugBackendClient(http: { request, _ in
            let value: [String: Any] = ["epoch": 7, "records": [], "memoryStatus": [:],
                "identity": ["deviceId": "other-device", "conversationId": session.conversationId]]
            return (try JSONSerialization.data(withJSONObject: value), HTTPURLResponse(url: request.url!, statusCode: 200,
                httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
        })
        do { _ = try await mismatch.inspectMemory(profile, conversation: session.conversationId, personality: "ollivander-trunk")
            fatalError("Cross-device context projected") } catch { }
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

@MainActor
final class NativeSessionPeer {
    let failure: String
    var creates = 0
    var deletes = 0
    var tokenRequests = 0
    var boundGuest: String?
    var createKeys = Set<String>()
    var deleteKeys = Set<String>()
    let conversation = UUID().uuidString.lowercased()
    let revision = UUID().uuidString.lowercased()
    init(failure: String = "") { self.failure = failure }

    func request(_ request: URLRequest, _ limit: Int) async throws -> (Data, HTTPURLResponse) {
        let path = request.url!.path
        let body = request.httpBody.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] } ?? [:]
        var value: [String: Any] = [:]
        var status = 200
        if path == "/v1/device-access/token" {
            tokenRequests += 1
            value = ["deviceId": "debug-test", "accessToken": tokenRequests == 1 ? "access" : "renewed"]
        } else if path == "/v1/admin/memory/guest" {
            assert(request.value(forHTTPHeaderField: "Authorization") == "Bearer test-secret")
            assert(body["deviceId"] as? String == "debug-test" && body["conversationId"] as? String == conversation)
            assert(body["identitySource"] as? String == "operator" && body["expectedVersion"] as? Int == 0)
            assert(body["identityVerified"] as? Bool == true)
            boundGuest = body["guestId"] as? String
            value = ["deviceId": "debug-test", "conversationId": conversation, "guestId": boundGuest!, "version": 1]
        } else if path == "/v1/admin/memory/context" {
            assert(request.value(forHTTPHeaderField: "Authorization") == "Bearer test-secret")
            let query = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)!.queryItems!
            assert(query.contains(URLQueryItem(name: "deviceId", value: "debug-test")))
            assert(query.contains(URLQueryItem(name: "conversationId", value: conversation)))
            value = ["epoch": 7, "identity": ["deviceId": "debug-test", "conversationId": conversation],
                "records": [], "memoryStatus": [:]]
        } else if path == "/v1/admin/memory/scopes/conversation/\(conversation)" {
            assert(request.value(forHTTPHeaderField: "Authorization") == "Bearer test-secret")
            assert(request.httpMethod == "DELETE" && body["deviceId"] as? String == "debug-test" && body["expectedEpoch"] as? Int == 7)
            status = 204
        } else if path == "/v1/diagnostics" {
            var inventory = nativeEngineInventory()
            var engines = inventory["engines"] as! [[String: Any]]
            if failure == "unavailable" { engines[0]["ready"] = false; engines[0]["unavailableReason"] = "not configured" }
            if failure == "wrong-model" { engines[0]["model"] = "alternate-model" }
            if failure == "third-engine" { engines.append(engines[0]) }
            inventory["engines"] = engines
            value = ["catalogVersion": "ollivander-private-party-v1"]
            if failure != "missing-capabilities" { value["engines"] = inventory }
        } else if path == "/v1/admin/modes" {
            if request.httpMethod == "PUT" {
                assert(body["expectedRevision"] as? String == revision)
                assert(body["dialogMode"] == nil && body["voiceMode"] == nil)
            }
            status = failure == "admin-denied" ? 401 : 200
            value = ["revision": revision, "engines": nativeEngineInventory(), "activeConversations": 0,
                "lifetime": failure == "admin-invalid" ? "invalid" : "until_restart",
                "personalityId": body["personalityId"] ?? "ollivander-trunk", "personalityLifetime": "persistent",
                "personalities": [["id": "ollivander-trunk", "name": "DildoVander", "description": "Persona",
                    "promptSha256": String(repeating: "a", count: 64)]]]
        } else if path == "/v1/conversations" {
            creates += 1
            createKeys.insert(request.value(forHTTPHeaderField: "Idempotency-Key")!)
            if failure == "lost-create" && creates == 1 { throw URLError(.networkConnectionLost) }
            if failure == "cancel-create" && creates == 1 { try await Task.sleep(nanoseconds: 30_000_000_000) }
            value = ["conversationId": conversation]
        } else if path == "/v1/conversations/\(conversation)" && request.httpMethod == "DELETE" {
            deletes += 1
            deleteKeys.insert(request.value(forHTTPHeaderField: "Idempotency-Key")!)
            if failure == "lost-delete" && deletes == 1 { throw URLError(.networkConnectionLost) }
            if failure == "failed-delete" && deletes <= 2 { throw URLError(.networkConnectionLost) }
            status = failure == "expired-delete" && deletes == 1 ? 401 : 204
            if failure == "expired-delete" && deletes == 2 { assert(request.value(forHTTPHeaderField: "Authorization") == "Bearer renewed") }
        } else { fatalError("Unexpected production route: \(path)") }
        let data: Data
        if status == 204 { data = Data() } else { data = try JSONSerialization.data(withJSONObject: value) }
        assert(data.count <= limit)
        return (data, HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil,
            headerFields: ["Content-Type": "application/json"])!)
    }
}

private func nativeEngineInventory() -> [String: Any] {
    let engines: [[String: Any]] = ["gpt-live", "realtime"].map { engine in
        ["engine": engine, "model": engine == "gpt-live" ? "gpt-live-1" : "gpt-realtime-2.1",
         "ready": true, "voices": ["cedar", "marin", "coral"],
         "speed": ["supported": engine == "realtime", "min": 0.8, "max": 1.2, "default": 1],
         "inputModes": ["audio"], "cameraSources": ["phone", "suitcase"]]
    }
    return ["schemaVersion": 1, "primaryEngine": "gpt-live", "engines": engines]
}
