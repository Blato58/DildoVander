import Foundation
import AVFoundation

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
        try await testGptLive(client: client, profile: profile)
        try testMicrophoneConversion()
        testAudioRouteChanges()
        for (path, expectedCode) in [
            ("problem", "realtime_unavailable"),
            ("mime", "realtime_live_protocol_failed"),
            ("eof", "realtime_live_protocol_failed")
        ] {
            try await testStartupFailure(baseURL: baseURL.appendingPathComponent("failure/\(path)"), expectedCode: expectedCode)
        }
        assert(DebugHandsFreeSession.failureCode(for: URLError(.timedOut)) == "realtime_live_connection_timeout")
        assert(DebugHandsFreeSession.failureCode(for: URLError(.networkConnectionLost)) == "realtime_live_connection_failed")
        assert(DebugHandsFreeSession.failureCode(for: URLError(.serverCertificateUntrusted)) == "realtime_live_tls_failed")
        print("Hands-free native multi-turn, VAD gate, early acknowledgement and cleanup passed.")
        print("Hands-free audio startup, route changes, 48kHz conversion and early HTTP failures passed.")
    }

    @MainActor static func testGptLive(client: DebugBackendClient, profile: DebugBackendProfile) async throws {
        var gate = false
        var audioBlocks = 0
        var session: DebugHandsFreeSession!
        session = DebugHandsFreeSession(client: client, profile: profile, token: "access",
            conversation: "00000000-0000-4000-8000-000000000002", boot: "00000000-0000-4000-8000-000000000001",
            readyMask: 63, voice: "cedar", speed: 1, gptLive: true,
            phase: { _ in }, visitor: { _ in }, reply: { reply in
                assert(reply.terminal && reply.text == "Live závěr")
                Task { try? await session.played(operationId: reply.operationId) }
            }, audio: { _ in
                assert(gate) // Live playback must preserve continuous microphone capture.
                audioBlocks += 1
            }, listening: { input in
                Task { try? await session.upload(inputId: input, sequence: 0, pcm: Data([0, 0, 1, 0])) }
            }, inputGate: { gate = $0 }, failure: { fatalError("Unexpected Live failure: \($0)") })
        let timeout = Task { try await Task.sleep(nanoseconds: 10_000_000_000); fatalError("Live timeout") }
        try await session.run()
        timeout.cancel()
        try await session.close()
        assert(audioBlocks == 2 && !gate)
    }

    static func testAudioRouteChanges() {
        // Our own category/speaker notifications must not stop a started call.
        for change in [DebugHandsFreeAudioPolicy.RouteChange.category, .speakerOverride, .configuration] {
            assert(!DebugHandsFreeAudioPolicy.shouldStop(change: change, configurationMatches: true))
            assert(DebugHandsFreeAudioPolicy.shouldStop(change: change, configurationMatches: false))
        }
        assert(DebugHandsFreeAudioPolicy.shouldStop(change: .device, configurationMatches: true))
        assert(DebugHandsFreeAudioPolicy.shouldStop(change: .device, configurationMatches: false))
    }

    static func testMicrophoneConversion() throws {
        for (sampleRate, channels) in [(48_000.0, AVAudioChannelCount(1)), (48_000.0, 2), (44_100.0, 1)] {
            let sourceFormat = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: channels)!
            let converter = try DebugHandsFreePCMConverter(sourceFormat: sourceFormat)
            let buffer = AVAudioPCMBuffer(pcmFormat: sourceFormat, frameCapacity: 2_400)!
            let empty = try converter.convert(buffer)
            assert(empty == nil)
            var output = Data()
            // Tiny first buffers exercise converter priming without treating empty
            // output as a fatal microphone error; then continue the same converter.
            for frames in [1, 1, 1, 2_400, 2_400] {
                buffer.frameLength = AVAudioFrameCount(frames)
                for channelIndex in 0..<Int(channels) {
                    let channel = buffer.floatChannelData![channelIndex]
                    for index in 0..<frames { channel[index] = 0.25 }
                }
                if let pcm = try converter.convert(buffer) { output.append(pcm) }
            }
            assert(!output.isEmpty && output.count % 2 == 0 && output.count <= 6_000)
            assert(output.contains { $0 != 0 })
        }
    }

    @MainActor static func testStartupFailure(baseURL: URL, expectedCode: String) async throws {
        let profile = DebugBackendProfile(baseURL: baseURL, deviceId: "debug-test", credential: "test")
        var failure: String?
        let session = DebugHandsFreeSession(client: DebugBackendClient(), profile: profile, token: "access",
            conversation: "00000000-0000-4000-8000-000000000002", boot: "00000000-0000-4000-8000-000000000001",
            readyMask: 63, voice: "cedar", speed: 1, phase: { _ in }, visitor: { _ in }, reply: { _ in },
            audio: { _ in fatalError("Unexpected audio before startup failure") }, listening: { _ in },
            inputGate: { _ in }, failure: { failure = $0 })
        do {
            try await session.run()
            fatalError("Startup failure was not surfaced")
        } catch {
            assert(failure == expectedCode)
        }
        try await session.close()
    }
}
