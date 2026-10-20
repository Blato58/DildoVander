import Foundation

@main
struct NativeCostumeTests {
    static let fixture = Data([255, 216, 255, 218, 0, 2, 255, 217])

    @MainActor static func main() async throws {
        let address = URL(string: ProcessInfo.processInfo.environment["KUFR_DEBUG_HTTP_PEER"]!)!
        let metadata = Data([255, 216, 255, 225, 0, 4, 1, 2, 255, 254, 0, 3, 3, 255, 218, 0, 2, 255, 217])
        let clean = try DebugCostumeJPEG.withoutMetadata(metadata)
        assert(clean == fixture)
        for invalid in [Data(), Data([1, 2]), Data([255, 216, 255, 225, 0, 1])] {
            do { _ = try DebugCostumeJPEG.withoutMetadata(invalid); fatalError("Invalid JPEG metadata accepted") } catch { }
        }
        for live in [true, false] {
            let device = live ? "debug-live" : "debug-realtime"
            let (profile, session) = try await open(address, device: device, live: live)
            var receipt = try await readReceipt(address, device: device)
            assert(receipt["events"] as? [String] == ["create"], "Optional image blocked session setup")
            let input = DebugCostumeInput(jpeg: fixture, description: "Host v modrém plášti")
            try await session.attachVisual(input, status: { _ in })
            assert(input.jpeg == nil && input.description == nil)
            receipt = try await readReceipt(address, device: device)
            assert(receipt["events"] as? [String] == ["create", "image-1", "poll-1", "manual"])
            assert(receipt["visualRevision"] as? Int == 1)
            try await session.close()
            assert(profile.deviceId == device)
        }
        for variant in ["failed", "lost"] {
            let device = "debug-\(variant)"
            let (_, session) = try await open(address, device: device)
            do { try await session.attachImage(fixture, imageId: UUID().uuidString.lowercased(), capturedAt: Date(), revision: 1)
                fatalError("Failed photo reported success") } catch { }
            let receipt = try await readReceipt(address, device: device)
            assert(receipt["events"] as? [String] == ["create", "image-1", "revoke-1"])
            assert(receipt["barrier"] as? Int == 1) // Ambiguous POST was fenced without ending voice.
            try await session.close()
        }
        let (_, retry) = try await open(address, device: "debug-retry")
        let capturedAt = Date()
        do { _ = try await retry.attachPhoto(fixture, capturedAt: capturedAt, revision: 1)
            fatalError("Failed analysis reported success") } catch { }
        let retriedImage = try await retry.attachPhoto(fixture, capturedAt: capturedAt, revision: 2)
        let retryReceipt = try await readReceipt(address, device: "debug-retry")
        let imageIds = retryReceipt["imageIds"] as! [String]
        let capturedTimes = retryReceipt["capturedTimes"] as! [String]
        assert(imageIds.count == 2 && Set(imageIds).count == 2 && imageIds[1] == retriedImage)
        assert(capturedTimes.count == 2 && capturedTimes[0] == capturedTimes[1])
        assert(retryReceipt["visualRevision"] as? Int == 2)
        assert(retryReceipt["events"] as? [String] == ["create", "image-1", "poll-1", "revoke-1", "image-2", "poll-2"])
        try await retry.close()
        let (_, blocked) = try await open(address, device: "debug-blocked")
        let pending = Task { @MainActor in
            try await blocked.attachImage(fixture, imageId: UUID().uuidString.lowercased(), capturedAt: Date(), revision: 1)
        }
        for _ in 0..<100 {
            if (try await readReceipt(address, device: "debug-blocked"))["events"] as? [String] == ["create", "image-1", "poll-1"] { break }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        let blockedReceipt = try await readReceipt(address, device: "debug-blocked")
        assert((blockedReceipt["events"] as? [String])?.contains("poll-1") == true)
        pending.cancel()
        do { try await pending.value; fatalError("Cancelled image returned") } catch { }
        var receipt = try await readReceipt(address, device: "debug-blocked")
        assert(receipt["barrier"] as? Int == 1)
        try await blocked.attachImage(fixture, imageId: UUID().uuidString.lowercased(), capturedAt: Date(), revision: 2)
        receipt = try await readReceipt(address, device: "debug-blocked")
        assert(receipt["visualRevision"] as? Int == 2)
        try await blocked.revokeImage(revision: 2)
        receipt = try await readReceipt(address, device: "debug-blocked")
        assert(receipt["visualRevision"] is NSNull && receipt["barrier"] as? Int == 2)
        try await blocked.close()
        let (_, race) = try await open(address, device: "debug-race")
        let delayed = Task { @MainActor in
            try await race.attachImage(fixture, imageId: UUID().uuidString.lowercased(), capturedAt: Date(), revision: 1)
        }
        for _ in 0..<100 {
            if (try await readReceipt(address, device: "debug-race"))["events"] as? [String] == ["create", "image-1"] { break }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        try await race.revokeImage(revision: 1)
        do { try await delayed.value; fatalError("Revocation was overtaken by old upload") } catch { }
        receipt = try await readReceipt(address, device: "debug-race")
        assert(receipt["visualRevision"] is NSNull && receipt["barrier"] as? Int == 1)
        try await race.close()
        print("Native optional image, provenance, expiry, cancellation, revocation and retake tests passed")
    }

    @MainActor static func open(_ address: URL, device: String, live: Bool = true) async throws -> (DebugBackendProfile, DebugHandsFreeSession) {
        let profile = DebugBackendProfile(baseURL: address, deviceId: device, credential: "synthetic-\(device)")
        let session = try await DebugBackendClient().openHandsFreeSession(profile, readyMask: 63, voice: "cedar", speed: 1,
            gptLive: live, phase: { _ in }, visitor: { _ in }, reply: { _ in }, audio: { _ in }, listening: { _ in },
            inputGate: { _ in }, failure: { _ in })
        return (profile, session)
    }

    static func readReceipt(_ address: URL, device: String) async throws -> [String: Any] {
        var request = URLRequest(url: address.appendingPathComponent("receipt"))
        request.setValue("Bearer access-\(device)", forHTTPHeaderField: "Authorization")
        let (data, _) = try await URLSession.shared.data(for: request)
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }
}
