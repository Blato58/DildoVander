import Foundation

@main
struct NativeCostumeTests {
    @MainActor static func main() async throws {
        let address = URL(string: ProcessInfo.processInfo.environment["KUFR_DEBUG_HTTP_PEER"]!)!
        let fixture = Data([255, 216, 255, 218, 0, 2, 255, 217])
        let metadata = Data([255, 216, 255, 225, 0, 4, 1, 2, 255, 254, 0, 3, 3, 255, 218, 0, 2, 255, 217])
        let clean = try DebugCostumeJPEG.withoutMetadata(metadata)
        assert(clean == fixture)
        for mode in ["chain", "ptt", "realtime", "live", "chain-failed", "chain-manual", "chain-none",
                     "chain-lost", "chain-malformed", "ptt-lost", "realtime-lost", "live-lost"] {
            let device = "debug-\(mode)"
            let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic-\(device)")
            let client = DebugBackendClient()
            let input = DebugCostumeInput(jpeg: mode == "chain-manual" || mode == "chain-none" ? nil : fixture,
                description: mode == "chain-none" ? nil : "Host v modrém plášti")
            let fallback = mode.contains("failed") || mode.contains("lost") || mode.contains("malformed")
            var messages: [String] = []
            if mode.hasPrefix("chain") {
                let session = try await client.openSession(profile, expectedMode: "fake", readyMask: 63,
                    voice: "cedar", speed: 1.1, costume: input, costumeStatus: { messages.append($0) })
                assert(input.jpeg == nil && input.description == nil)
                if fallback {
                    let opening = try await session.advance(nil, phase: { _ in }, reply: { _ in })
                    assert(opening.terminal)
                }
                try await session.close()
            } else if mode.hasPrefix("ptt") {
                let session = try await client.openRealtimeSession(profile, readyMask: 63, voice: "cedar", speed: 1.1,
                    costume: input, costumeStatus: { messages.append($0) })
                try await session.close()
            } else {
                let session = try await client.openHandsFreeSession(profile, readyMask: 63, voice: "cedar", speed: 1,
                    gptLive: mode.hasPrefix("live"), costume: input, costumeStatus: { messages.append($0) },
                    phase: { _ in }, visitor: { _ in }, reply: { _ in }, audio: { _ in }, listening: { _ in },
                    inputGate: { _ in }, failure: { _ in })
                try await session.close()
            }
            let snapshotReceipt = try await receipt(address, device: device)
            let expected = mode == "chain-none" ? ["create", "delete"]
                : mode == "chain-manual" ? ["create", "manual", "delete"]
                : fallback ? ["create", "image", "delete", "create", "manual", "delete"]
                : ["create", "image", "poll", "manual", "delete"]
            let events = snapshotReceipt["events"] as! [String]
            assert(events.filter { ["create", "image", "poll", "manual", "delete"].contains($0) } == expected)
            assert(snapshotReceipt["photoConsent"] as? Bool == !(mode == "chain-none" || mode == "chain-manual"))
            if fallback {
                assert(messages.last?.contains("Fotku se nepodařilo") == true)
                assert(snapshotReceipt["photoConsents"] as? [Bool] == [true, false])
                let ids = snapshotReceipt["createdIds"] as! [String]
                assert(Set(snapshotReceipt["createKeys"] as! [String]).count == 2)
                assert(snapshotReceipt["manualIds"] as? [String] == [ids[1]])
                assert(snapshotReceipt["deletedIds"] as? [String] == ids)
                assert(events.filter { $0 == "image" }.count == 1)
                if mode.hasPrefix("chain") { assert(snapshotReceipt["openingIds"] as? [String] == [ids[1]]) }
            }
        }
        let device = "debug-chain-cancel"
        let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic-\(device)")
        let task = Task { @MainActor in
            try await DebugBackendClient().openSession(profile, expectedMode: "fake", readyMask: 63,
                voice: "cedar", speed: 1.1, costume: DebugCostumeInput(jpeg: fixture, description: nil))
        }
        for _ in 0..<100 {
            if let state = try? await receipt(address, device: device),
                (state["events"] as? [String])?.contains("poll") == true { break }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        task.cancel()
        do { _ = try await task.value; fatalError("Cancelled photo preparation started a session") }
        catch is CancellationError { }
        catch let error as URLError { assert(error.code == .cancelled) }
        let cancelled = try await receipt(address, device: device)
        assert(cancelled["events"] as? [String] == ["create", "image", "poll", "delete"])
        try await cancellation(address, fixture: fixture, mode: "chain-lost-cancel-cleanup", waitFor: "delete", count: 1)
        try await cancellation(address, fixture: fixture, mode: "chain-lost-cancel-fresh", waitFor: "create", count: 2)
        let timeoutDevice = "debug-chain-lost-cleanup-timeout"
        let timeoutProfile = DebugBackendProfile(baseURL: address, deviceId: timeoutDevice, credential: "synthetic-\(timeoutDevice)")
        do {
            _ = try await DebugBackendClient().openSession(timeoutProfile, expectedMode: "fake", readyMask: 63,
                voice: "cedar", speed: 1.1, costume: DebugCostumeInput(jpeg: fixture, description: nil))
            fatalError("Unconfirmed image cleanup admitted a fresh voice session")
        } catch DebugBackendError.httpStatus { }
        let timeoutReceipt = try await receipt(address, device: timeoutDevice)
        assert((timeoutReceipt["createdIds"] as? [String])?.count == 1)
        assert(timeoutReceipt["openingIds"] as? [String] == [])
        try await cleanupReplay(address)
        try await cleanupRenewal(address)
        try await manualReplay(address)
        for engine in ["chain", "ptt", "realtime", "live"] { try await freshOrphan(address, fixture: fixture, engine: engine) }
        try await cancellation(address, fixture: fixture, mode: "chain-lost-cancel-fresh-orphan", waitFor: "create", count: 2)
        print("Native costume ownership/DELETE replay/manual replay/fallback/Stop HTTP peer tests passed")
    }

    @MainActor static func cancellation(_ address: URL, fixture: Data, mode: String,
                                        waitFor: String, count: Int) async throws {
        let device = "debug-\(mode)"
        let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic-\(device)")
        let client = DebugBackendClient()
        let task = Task { @MainActor in
            try await client.openSession(profile, expectedMode: "fake", readyMask: 63,
                voice: "cedar", speed: 1.1, costume: DebugCostumeInput(jpeg: fixture, description: "Host v modrém plášti"))
        }
        var reached = false
        for _ in 0..<100 {
            if let state = try? await receipt(address, device: device),
                let events = state["events"] as? [String], events.filter({ $0 == waitFor }).count >= count {
                reached = true; break
            }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        assert(reached)
        task.cancel()
        do { _ = try await task.value; fatalError("Cancelled fallback started a voice session") }
        catch is CancellationError { }
        catch let error as URLError { assert(error.code == .cancelled) }
        if mode.contains("fresh-orphan") {
            assert(client.hasPendingCleanup)
            do {
                _ = try await client.openSession(profile, expectedMode: "fake", readyMask: 63, voice: "cedar", speed: 1.1)
                fatalError("Pending cancelled-create cleanup admitted a next Start")
            } catch DebugBackendError.serverCode(let code) { assert(code == "realtime_cleanup_failed") }
            try await client.retryPendingCleanup()
            assert(!client.hasPendingCleanup)
        }
        let state = try await receipt(address, device: device)
        let created = state["createdIds"] as! [String]
        assert(created.count == (mode.contains("cancel-fresh") ? 2 : 1))
        assert(Set(state["deletedIds"] as! [String]) == Set(created))
        assert(state["openingIds"] as? [String] == [])
        assert(state["manualIds"] as? [String] == [])
        assert((state["events"] as! [String]).filter { $0 == "image" }.count == 1)
        if mode.contains("fresh-orphan") {
            let keys = (state["deleteKeys"] as! [[String: String]]).filter { $0["conversation"] == created[1] }
            assert(keys.count == 3 && Set(keys.map { $0["key"]! }).count == 1)
            let next = try await client.openSession(profile, expectedMode: "fake", readyMask: 63, voice: "cedar", speed: 1.1)
            try await next.close()
        }
    }

    @MainActor static func cleanupReplay(_ address: URL) async throws {
        let device = "debug-chain-delete-retry"
        let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic-\(device)")
        let client = DebugBackendClient()
        let session = try await client.openSession(profile, expectedMode: "fake", readyMask: 63, voice: "cedar", speed: 1.1)
        do { try await session.close(); fatalError("Two failed barrier acknowledgements lost cleanup ownership") }
        catch DebugBackendError.httpStatus { }
        assert(client.hasPendingCleanup)
        // The plugin's next Stop calls this even if no session was returned.
        try await client.retryPendingCleanup()
        assert(!client.hasPendingCleanup)
        try await session.close()
        let state = try await receipt(address, device: device)
        let keys = state["deleteKeys"] as! [[String: String]]
        assert(keys.count == 3 && Set(keys.map { $0["key"]! }).count == 1)
        let next = try await client.openSession(profile, expectedMode: "fake", readyMask: 63, voice: "cedar", speed: 1.1)
        try await next.close()
        let finished = try await receipt(address, device: device)
        assert((finished["createdIds"] as! [String]).count == 2)
        assert(Set(finished["createdIds"] as! [String]) == Set(finished["deletedIds"] as! [String]))
    }

    @MainActor static func cleanupRenewal(_ address: URL) async throws {
        for mode in ["expired", "expired-refresh-failed", "expired-renewed-pending"] {
            let device = "debug-chain-delete-retry-\(mode)"
            let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic-\(device)")
            let client = DebugBackendClient()
            let session = try await client.openSession(profile, expectedMode: "fake", readyMask: 63, voice: "cedar", speed: 1.1)
            do { try await session.close(); fatalError("Unconfirmed barrier lost ownership") }
            catch DebugBackendError.httpStatus { }
            assert(client.hasPendingCleanup)
            if mode == "expired" {
                try await client.retryPendingCleanup()
            } else {
                do { try await client.retryPendingCleanup(); fatalError("Failed renewal/barrier discarded ownership") }
                catch DebugBackendError.httpStatus { }
                assert(client.hasPendingCleanup)
                do {
                    _ = try await client.openSession(profile, expectedMode: "fake", readyMask: 63, voice: "cedar", speed: 1.1)
                    fatalError("Pending cleanup admitted a new Start")
                } catch DebugBackendError.serverCode(let code) { assert(code == "realtime_cleanup_failed") }
                let pending = try await receipt(address, device: device)
                assert(pending["tokenRequests"] as? Int == 2)
                assert((pending["createdIds"] as! [String]).count == 1)
                // A later Stop can renew again after refresh failure. A renewed
                // token is retained when its subsequent barrier still fails.
                try await client.retryPendingCleanup()
            }
            assert(!client.hasPendingCleanup)
            try await session.close()
            let state = try await receipt(address, device: device)
            let keys = state["deleteKeys"] as! [[String: String]]
            assert(Set(keys.map { $0["key"]! }).count == 1)
            assert(Set(keys.map { $0["conversation"]! }).count == 1)
            assert(state["tokenRequests"] as? Int == (mode.contains("refresh-failed") ? 3 : 2))
            // The device name itself includes "-renewed-" in one scenario.
            // Match the peer's exact renewed token, not a substring of the name.
            let renewedToken = "access-\(device)-renewed-\(state["tokenRequests"] as! Int)"
            let renewed = keys.firstIndex { $0["token"] == renewedToken }!
            assert(keys[renewed...].allSatisfy { $0["token"] == keys[renewed]["token"] })
            let events = state["events"] as! [String]
            assert(events.contains("expired-token") && events.contains("renew"))
            assert(!events.contains("image") && state["openingIds"] as? [String] == [])
            let next = try await client.openSession(profile, expectedMode: "fake", readyMask: 63, voice: "cedar", speed: 1.1)
            try await next.close()
            let finished = try await receipt(address, device: device)
            assert((finished["createdIds"] as! [String]).count == 2)
            assert(Set(finished["createdIds"] as! [String]) == Set(finished["deletedIds"] as! [String]))
        }
    }

    @MainActor static func manualReplay(_ address: URL) async throws {
        for mode in ["chain-manual-lost", "chain-manual-malformed", "chain-manual-repeat-failure"] {
            let device = "debug-\(mode)"
            let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic-\(device)")
            let client = DebugBackendClient()
            var messages: [String] = []
            do {
                let session = try await client.openSession(profile, expectedMode: "fake", readyMask: 63,
                    voice: "cedar", speed: 1.1, costume: DebugCostumeInput(jpeg: nil, description: "Host v modrém plášti"),
                    costumeStatus: { messages.append($0) })
                assert(!mode.contains("repeat-failure"))
                let opening = try await session.advance(nil, phase: { _ in }, reply: { _ in })
                assert(opening.terminal)
                try await session.close()
            } catch DebugBackendError.invalidResponse {
                assert(mode.contains("repeat-failure"))
                assert(messages.last?.contains("nebyl potvrzen") == true)
            }
            let state = try await receipt(address, device: device)
            let keys = state["correctionKeys"] as! [String]
            assert(keys.count == 2 && Set(keys).count == 1)
            assert((state["manualIds"] as! [String]).count == 1)
            assert((state["events"] as! [String]).filter { $0 == "image" }.isEmpty)
            assert((state["createdIds"] as! [String]).count == 1)
            assert(Set(state["createdIds"] as! [String]) == Set(state["deletedIds"] as! [String]))
            if mode.contains("repeat-failure") { assert((state["openingIds"] as! [String]).isEmpty) }
            else { assert(state["openingIds"] as? [String] == state["createdIds"] as? [String]) }
        }
    }

    @MainActor static func freshOrphan(_ address: URL, fixture: Data, engine: String) async throws {
        let device = "debug-\(engine)-lost-fresh-orphan"
        let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic-\(device)")
        let client = DebugBackendClient()
        do {
            try await openAndClose(client, profile: profile, engine: engine,
                costume: DebugCostumeInput(jpeg: fixture, description: "Host v modrém plášti"))
            fatalError("An unconfirmed manual cue started voice")
        } catch DebugBackendError.invalidResponse { }
        assert(client.hasPendingCleanup)
        do {
            try await openAndClose(client, profile: profile, engine: engine, costume: nil)
            fatalError("An orphan admitted a next Start")
        } catch DebugBackendError.serverCode(let code) { assert(code == "realtime_cleanup_failed") }
        let before = try await receipt(address, device: device)
        assert((before["createdIds"] as! [String]).count == 2)
        assert((before["openingIds"] as! [String]).isEmpty)
        try await client.retryPendingCleanup()
        assert(!client.hasPendingCleanup)
        let cleaned = try await receipt(address, device: device)
        let fresh = (cleaned["createdIds"] as! [String])[1]
        let keys = (cleaned["deleteKeys"] as! [[String: String]]).filter { $0["conversation"] == fresh }
        assert(keys.count == 3 && Set(keys.map { $0["key"]! }).count == 1)
        try await openAndClose(client, profile: profile, engine: engine, costume: nil)
        let state = try await receipt(address, device: device)
        assert((state["createdIds"] as! [String]).count == 3)
        assert((state["events"] as! [String]).filter { $0 == "image" }.count == 1)
        assert(Set(state["createdIds"] as! [String]) == Set(state["deletedIds"] as! [String]))
    }

    @MainActor static func openAndClose(_ client: DebugBackendClient, profile: DebugBackendProfile,
                                       engine: String, costume: DebugCostumeInput?) async throws {
        if engine == "chain" {
            let session = try await client.openSession(profile, expectedMode: "fake", readyMask: 63, voice: "cedar", speed: 1.1, costume: costume)
            try await session.close()
        } else if engine == "ptt" {
            let session = try await client.openRealtimeSession(profile, readyMask: 63, voice: "cedar", speed: 1.1, costume: costume)
            try await session.close()
        } else {
            let session = try await client.openHandsFreeSession(profile, readyMask: 63, voice: "cedar", speed: 1,
                gptLive: engine == "live", costume: costume, phase: { _ in }, visitor: { _ in }, reply: { _ in },
                audio: { _ in }, listening: { _ in }, inputGate: { _ in }, failure: { _ in })
            try await session.close()
        }
    }

    @MainActor static func receipt(_ address: URL, device: String) async throws -> [String: Any] {
        var request = URLRequest(url: address.appendingPathComponent("receipt"))
        request.setValue("Bearer access-\(device)", forHTTPHeaderField: "Authorization")
        let transport = DebugHTTPTransport()
        defer { transport.invalidate() }
        let (data, response) = try await transport.request(request, limit: 16_384)
        guard response.statusCode == 200 else { throw DebugBackendError.httpStatus }
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }
}
