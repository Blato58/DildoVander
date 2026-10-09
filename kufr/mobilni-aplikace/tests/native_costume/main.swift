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
            let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic")
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
        let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic")
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
        let timeoutProfile = DebugBackendProfile(baseURL: address, deviceId: timeoutDevice, credential: "synthetic")
        do {
            _ = try await DebugBackendClient().openSession(timeoutProfile, expectedMode: "fake", readyMask: 63,
                voice: "cedar", speed: 1.1, costume: DebugCostumeInput(jpeg: fixture, description: nil))
            fatalError("Unconfirmed image cleanup admitted a fresh voice session")
        } catch DebugBackendError.httpStatus { }
        let timeoutReceipt = try await receipt(address, device: timeoutDevice)
        assert((timeoutReceipt["createdIds"] as? [String])?.count == 1)
        assert(timeoutReceipt["openingIds"] as? [String] == [])
        print("Native photo consent/order/lost response/fresh fallback/cleanup barrier/Stop HTTP peer tests passed")
    }

    @MainActor static func cancellation(_ address: URL, fixture: Data, mode: String,
                                        waitFor: String, count: Int) async throws {
        let device = "debug-\(mode)"
        let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic")
        let task = Task { @MainActor in
            try await DebugBackendClient().openSession(profile, expectedMode: "fake", readyMask: 63,
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
        let state = try await receipt(address, device: device)
        let created = state["createdIds"] as! [String]
        assert(created.count == (mode.contains("cancel-fresh") ? 2 : 1))
        assert(Set(state["deletedIds"] as! [String]) == Set(created))
        assert(state["openingIds"] as? [String] == [])
        assert(state["manualIds"] as? [String] == [])
        assert((state["events"] as! [String]).filter { $0 == "image" }.count == 1)
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
