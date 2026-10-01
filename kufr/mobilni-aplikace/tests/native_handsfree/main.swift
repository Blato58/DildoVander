import Foundation

@main
struct NativeHandsFreeTests {
    @MainActor static func main() async throws {
        guard let address = ProcessInfo.processInfo.environment["KUFR_DEBUG_HTTP_PEER"], let baseURL = URL(string: address) else {
            fatalError("Missing HTTP peer")
        }
        let profile = DebugBackendProfile(baseURL: baseURL, deviceId: "debug-test", credential: "test")
        let client = DebugBackendClient()
        var gates: [Bool] = []
        var visitors = 0
        var replies = 0
        var session: DebugHandsFreeSession!
        session = DebugHandsFreeSession(client: client, profile: profile, token: "access", conversation: "00000000-0000-4000-8000-000000000002",
                                        boot: "00000000-0000-4000-8000-000000000001", readyMask: 63, voice: "cedar", speed: 1,
                                        phase: { phase in if phase == "recording" { assert(gates.last == true) } }, visitor: { _ in visitors += 1 }, reply: { reply in
                                            replies += 1
                                            Task { try? await session.played(operationId: reply.operationId) }
                                        }, audio: { _ in }, listening: { input in
                                            Task { try? await session.upload(inputId: input, sequence: 0, pcm: Data([0, 0, 1, 0])) }
                                        }, inputGate: { gates.append($0) }, failure: { code in fatalError("Unexpected failure: \(code)") })
        let timeout = Task { try await Task.sleep(nanoseconds: 10_000_000_000); fatalError("Hands-free timeout") }
        try await session.run()
        timeout.cancel()
        try await session.close()
        assert(replies == 3 && visitors == 2)
        assert(gates.filter { $0 }.count == 2)
        // speech_started must not turn the accepted listening gate back off.
        assert(gates.last == false)
        let (data, _) = try await URLSession.shared.data(from: baseURL.appendingPathComponent("test/metrics"))
        let metrics = try JSONSerialization.jsonObject(with: data) as! [String: Int]
        assert(metrics["played"] == 3 && metrics["uploads"] == 2 && metrics["cleanup"] == 1)
        print("Hands-free native multi-turn, VAD gate, early acknowledgement and cleanup passed.")
    }
}
