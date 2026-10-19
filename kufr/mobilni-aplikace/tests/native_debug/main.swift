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
        try await verifyAccountTransport()
        try await verifyShowTransport()
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

// Exercise the actual client with a native-only authorizer seam. The account
// fixture has no raw device/admin credential and cannot mint a device token.
@MainActor private func verifyAccountTransport() async throws {
    let profile = try DebugBackendProfile(accountOrigin: URL(string: "https://accounts.example.test/")!,
        deviceId: "physical-kufr-1", binding: "native-session-one")
    assert(profile.usesAccountSession && profile.credential.isEmpty)
    let peer = AccountDebugPeer()
    let client = DebugBackendClient(http: peer.request, accountAuthorizer: peer.authorize)
    let state = try await client.engineInventory(profile)
    assert(state.primaryEngine == "gpt-live" && state.engines.count == 2)
    assert(peer.paths == ["/v1/diagnostics"] && peer.authorizationCount == 1)
    let conversation = UUID().uuidString.lowercased()
    let stream = UUID().uuidString.lowercased()
    for endpoint in ["gpt-live", "realtime-live"] {
        let upgrade = try await client.audioStreamRequest(profile, conversation: conversation,
            endpoint: endpoint, streamId: stream, token: "")
        assert(upgrade.url?.scheme == "wss" && upgrade.httpMethod == "GET")
        assert(upgrade.url?.path == "/v1/conversations/\(conversation)/\(endpoint)/\(stream)/audio-stream")
        assert(upgrade.value(forHTTPHeaderField: "Cookie") == "synthetic-native-only-cookie")
        assert(upgrade.value(forHTTPHeaderField: "X-DV-CSRF") == "synthetic-csrf")
        assert(upgrade.value(forHTTPHeaderField: "Authorization") == nil)
    }
    try await client.revokeConversationImage(profile, token: "", conversation: conversation,
        boot: UUID().uuidString.lowercased(), revision: 1)
    assert(peer.paths.count == 2 && peer.authorizationCount == 4)
    do { _ = try await client.deleteConversationMemory(profile, conversation: conversation, expectedEpoch: 7)
        fatalError("Unconfirmed account deletion admitted") }
    catch DebugBackendError.serverCode(let code) { assert(code == "account_admin_confirmation_required") }
    let guest = DebugGuestMemoryBinding(guestId: "host-042", identityVerified: true,
        longTermAllowed: true, cloudExportAllowed: false)
    do { _ = try await client.openHandsFreeSession(profile, readyMask: 63, voice: "cedar", speed: 1,
        gptLive: true, guestMemory: guest, phase: { _ in }, visitor: { _ in }, reply: { _ in }, audio: { _ in },
        listening: { _ in }, inputGate: { _ in }, failure: { _ in }); fatalError("Unconfirmed account guest admitted") }
    catch DebugBackendError.serverCode(let code) { assert(code == "account_admin_confirmation_required") }
    assert(peer.paths.count == 2 && peer.authorizationCount == 4)
    let deleted = try await client.deleteConversationMemory(profile, conversation: conversation, expectedEpoch: 7, confirmed: true)
    assert(deleted["deleted"] as? Bool == true && peer.paths.count == 3 && peer.authorizationCount == 5)
    peer.binding = "native-session-two"
    do { _ = try await client.audioStreamRequest(profile, conversation: conversation,
        endpoint: "gpt-live", streamId: stream, token: ""); fatalError("Previous account upgraded socket") }
    catch DebugBackendError.invalidProfile { }
    do { _ = try await client.engineInventory(profile); fatalError("Previous account binding reused") }
    catch DebugBackendError.invalidProfile { }
    assert(peer.paths.count == 3)
    let unconfigured = DebugBackendClient(http: peer.request)
#if !os(iOS)
    do { _ = try await unconfigured.engineInventory(profile); fatalError("Account request fell back to bearer transport") }
    catch DebugBackendError.invalidProfile { }
#endif
    do { try DebugBackendKeychain(service: "kufr-native-account-transport-test").write(profile)
        fatalError("Account binding persisted as developer credential") }
    catch DebugBackendError.invalidProfile { }
}

@MainActor private func verifyShowTransport() async throws {
    var slots = DebugShowRequestSlots()
    let reads = (0..<4).map { _ in slots.begin(urgent: false)! }
    assert(slots.begin(urgent: false) == nil)
    assert(DebugShowRequestSlots.isUrgent(suffix: "/commands", method: "POST", body: "{\"kind\":\"stop\"}"))
    assert(DebugShowRequestSlots.isUrgent(suffix: "/duels/duel-1/cancel", method: "POST", body: "{}"))
    assert(!DebugShowRequestSlots.isUrgent(suffix: "/commands", method: "POST", body: "{\"kind\":\"override\"}"))
    let stop = slots.begin(urgent: true)!
    assert(slots.begin(urgent: true) == nil)
    slots.finish(stop)
    assert(slots.begin(urgent: true) != nil)
    reads.forEach { slots.finish($0) }
    slots.clear()
    assert(slots.begin(urgent: false) != nil)
    let profile = try DebugBackendProfile(accountOrigin: URL(string: "https://debug.example.test")!,
        deviceId: "physical-kufr-1", binding: "account-one")
    let peer = ShowDebugPeer()
    let client = DebugBackendClient(http: peer.request, accountAuthorizer: peer.authorize)
    let invalidRequests: [(String, String, String?)] = [("/../admin/modes", "GET", nil), ("/commands?target=other", "POST", "{}"),
        ("/commands\n", "POST", "{}"), ("/device-cues", "GET", nil), ("/commands", "DELETE", nil),
        ("/commands", "POST", "[]"), ("", "GET", "{}"), ("/commands", "POST", String(repeating: "x", count: 65_537))]
    for (suffix, method, body) in invalidRequests {
        do { _ = try await client.showRequest(profile, deviceId: profile.deviceId, suffix: suffix, method: method, body: body)
            fatalError("Unsafe Show proxy accepted") } catch { }
    }
    do { _ = try await client.showRequest(profile, deviceId: "other-device", suffix: "", method: "GET")
        fatalError("Account Show scope changed") } catch DebugBackendError.invalidProfile { }
    for suffix in ["/end-evening", "/offline-outcomes", "/prophecies/outcome-1/reveal", "/duels/duel-1/grants", "/duels/duel-1/grants/revoke"] {
        do { _ = try await client.showRequest(profile, deviceId: profile.deviceId, suffix: suffix, method: "POST", body: "{}")
            fatalError("Unconfirmed sensitive Show mutation admitted") }
        catch DebugBackendError.serverCode(let code) { assert(code == "confirmation_required") }
    }
    assert(peer.calls == 0)
    let state = try await client.showRequest(profile, deviceId: profile.deviceId, suffix: "", method: "GET")
    assert(state.status == 200 && (state.body as? [String: Any])?["revision"] as? Int == 4)
    _ = try await client.showRequest(profile, deviceId: profile.deviceId, suffix: "/commands", method: "POST", body: "{}", confirmed: true)
    assert(peer.confirmation == nil) // routine controls never require recent passkey or inferred consent
    _ = try await client.showRequest(profile, deviceId: profile.deviceId, suffix: "/end-evening", method: "POST", body: "{}", confirmed: true)
    assert(peer.confirmation == "yes")
    peer.status = 409
    let conflict = try await client.showRequest(profile, deviceId: profile.deviceId, suffix: "/duels", method: "POST", body: "{}")
    assert(conflict.status == 409 && peer.calls == 4) // mutation conflicts are never retried
    peer.status = 403; peer.mime = "application/problem+json"; peer.payload = "{\"code\":\"recent_passkey_required\"}"
    let denied = try await client.showRequest(profile, deviceId: profile.deviceId, suffix: "/end-evening", method: "POST", body: "{}", confirmed: true)
    assert(denied.status == 403 && (denied.body as? [String: Any])?["code"] as? String == "recent_passkey_required")
    assert(peer.calls == 5)
    peer.changeAccountDuringResponse = true
    do { _ = try await client.showRequest(profile, deviceId: profile.deviceId, suffix: "", method: "GET")
        fatalError("Previous account private Show response exposed") } catch DebugBackendError.invalidProfile { }
    assert(peer.calls == 6)
    let legacy = try DebugBackendProfile(address: "https://debug.example.test", deviceId: "debug-service", credential: "synthetic-service")
    let service = DebugBackendClient(http: { request, _ in
        assert(request.value(forHTTPHeaderField: "Authorization") == "Bearer synthetic-service")
        assert(request.value(forHTTPHeaderField: "Cookie") == nil)
        return (Data("{}".utf8), HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil,
            headerFields: ["Content-Type": "application/json"])!)
    })
    _ = try await service.showRequest(legacy, deviceId: profile.deviceId, suffix: "/end-evening", method: "POST", body: "{}")
}

@MainActor private final class ShowDebugPeer {
    var calls = 0
    var status = 200
    var mime = "application/json"
    var payload = "{\"revision\":4}"
    var binding = "account-one"
    var confirmation: String?
    var changeAccountDuringResponse = false
    func authorize(_ original: URLRequest, deviceId: String, binding: String) async throws -> URLRequest {
        guard binding == self.binding else { throw DebugBackendError.invalidProfile }
        assert(deviceId == "physical-kufr-1" && original.value(forHTTPHeaderField: "Authorization") == nil)
        assert(original.url?.host == "debug.example.test" && original.url!.path.hasPrefix("/v1/show/devices/physical-kufr-1"))
        var request = original
        request.setValue("synthetic-account-cookie", forHTTPHeaderField: "Cookie")
        request.setValue("synthetic-csrf", forHTTPHeaderField: "X-DV-CSRF")
        request.setValue(deviceId, forHTTPHeaderField: "X-DV-Device-Id")
        return request
    }
    func request(_ request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse) {
        assert(limit == 1_048_576 && request.value(forHTTPHeaderField: "Cookie") == "synthetic-account-cookie")
        assert(request.value(forHTTPHeaderField: "X-DV-Device-Id") == "physical-kufr-1")
        assert(request.value(forHTTPHeaderField: "X-DV-CSRF") == "synthetic-csrf")
        confirmation = request.value(forHTTPHeaderField: "X-DV-Confirm")
        calls += 1
        if changeAccountDuringResponse { binding = "account-two" }
        return (Data(payload.utf8), HTTPURLResponse(url: request.url!, statusCode: status,
            httpVersion: nil, headerFields: ["Content-Type": mime])!)
    }
}

@MainActor private final class AccountDebugPeer {
    var binding = "native-session-one"
    var authorizationCount = 0
    var paths: [String] = []
    func authorize(_ original: URLRequest, deviceId: String, binding: String) async throws -> URLRequest {
        guard binding == self.binding else { throw DebugBackendError.invalidProfile }
        assert(original.value(forHTTPHeaderField: "Authorization") == nil)
        assert(deviceId == "physical-kufr-1")
        assert(original.url?.scheme == "https")
        authorizationCount += 1
        var request = original
        request.setValue("synthetic-native-only-cookie", forHTTPHeaderField: "Cookie")
        request.setValue(deviceId, forHTTPHeaderField: "X-DV-Device-Id")
        request.setValue("synthetic-csrf", forHTTPHeaderField: "X-DV-CSRF")
        return request
    }
    func request(_ request: URLRequest, limit: Int) async throws -> (Data, HTTPURLResponse) {
        assert(request.value(forHTTPHeaderField: "Authorization") == nil)
        assert(request.value(forHTTPHeaderField: "Cookie") == "synthetic-native-only-cookie")
        assert(request.value(forHTTPHeaderField: "X-DV-Device-Id") == "physical-kufr-1")
        paths.append(request.url!.path)
        if request.url!.path.contains("/admin/memory/scopes/conversation/") {
            assert(request.httpMethod == "DELETE" && request.value(forHTTPHeaderField: "X-DV-Confirm") == "yes")
            let body = try JSONSerialization.jsonObject(with: request.httpBody!) as! [String: Any]
            assert(body["deviceId"] as? String == "physical-kufr-1" && body["expectedEpoch"] as? Int == 7)
            return (Data(), HTTPURLResponse(url: request.url!, statusCode: 204, httpVersion: "HTTP/1.1", headerFields: nil)!)
        }
        if request.url!.path.hasSuffix("/image") {
            assert(request.httpMethod == "DELETE" && request.value(forHTTPHeaderField: "Image-Revision") == "1")
            return (Data(), HTTPURLResponse(url: request.url!, statusCode: 204, httpVersion: "HTTP/1.1", headerFields: nil)!)
        }
        assert(request.url?.path == "/v1/diagnostics")
        let data = try JSONSerialization.data(withJSONObject: ["engines": nativeEngineInventory()])
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
