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
        try testPlaybackConversion()
        try testPlaybackRendering()
        testPlaybackProgress()
        try testMicrophoneConversion()
        testAudioRouteChanges()
        testEngineStartup()
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

    static func testPlaybackConversion() throws {
        let buffer = try DebugHandsFreePlayback.buffer(from: Data([0, 0, 0xff, 0x7f, 0, 0x80, 0, 0x40, 0, 0xc0]))
        assert(buffer.format.sampleRate == 24_000 && buffer.format.channelCount == 1)
        assert(buffer.frameLength == 5)
        let samples = buffer.floatChannelData![0]
        assert(samples[0] == 0 && samples[1] == Float(32767) / 32768)
        assert(samples[2] == -1 && samples[3] == 0.5 && samples[4] == -0.5)
        for invalid in [Data(), Data([1]), Data(repeating: 0, count: 49_154)] {
            do { _ = try DebugHandsFreePlayback.buffer(from: invalid); fatalError("Invalid playback buffer accepted") }
            catch DebugBackendError.invalidResponse { }
        }
    }

    static func testPlaybackRendering() throws {
        // Render the production graph through AVAudioEngine, including a restart
        // while the player is still active. This detects disconnected output and
        // stale scheduling clocks; it does not simulate iOS voice-processing I/O.
        let engine = AVAudioEngine()
        let player = AVAudioPlayerNode()
        let output = AVAudioFormat(standardFormatWithSampleRate: 48_000, channels: 2)!
        try engine.enableManualRenderingMode(.offline, format: output, maximumFrameCount: 1_024)
        defer { player.stop(); engine.stop() }
        var pcm = Data()
        for index in 0..<4_800 {
            let sample = Int16(sin(Double(index) * 2 * .pi * 440 / 24_000) * 16_000)
            let bits = UInt16(bitPattern: sample)
            pcm.append(UInt8(bits & 255)); pcm.append(UInt8(bits >> 8))
        }
        for _ in 0..<3 {
            engine.stop()
            try DebugHandsFreePlayback.prepare(engine: engine, player: player)
            assert(!player.isPlaying)
            try engine.start()
            let source = try DebugHandsFreePlayback.buffer(from: pcm)
            DebugHandsFreePlayback.schedule(source, player: player) { _ in }
            let rendered = AVAudioPCMBuffer(pcmFormat: output, frameCapacity: 1_024)!
            var energy = [Double](repeating: 0, count: 2)
            var frames = 0
            for _ in 0..<80 where frames < 16_384 {
                switch try engine.renderOffline(1_024, to: rendered) {
                case .success:
                    for channel in 0..<2 {
                        let samples = rendered.floatChannelData![channel]
                        for index in 0..<Int(rendered.frameLength) { energy[channel] += abs(Double(samples[index])) }
                    }
                    frames += Int(rendered.frameLength)
                case .cannotDoInCurrentContext: Thread.sleep(forTimeInterval: 0.001); continue
                default: fatalError("Offline playback did not render")
                }
            }
            assert(frames >= 16_384 && energy.allSatisfy { $0 > 100 }, "PCM must reach both output channels after every restart")
        }
        print("Hands-free production graph rendered PCM after three engine starts.")
    }

    static func testPlaybackProgress() {
        var queue = DebugHandsFreePlaybackProgress()
        assert(!queue.stalled(now: 100))
        queue.enqueue(now: 100)
        queue.enqueue(now: 102)
        assert(queue.stalled(now: 102.5), "Incoming audio cannot hide absent playback completions")
        queue.complete(now: 102.6)
        assert(!queue.stalled(now: 104))
        assert(queue.stalled(now: 105.1))
        queue.complete(now: 105.2)
        assert(queue.pending == 0 && !queue.stalled(now: 200))
        queue.enqueue(now: 200)
        assert(!queue.stalled(now: 201), "New playback gets its own deadline")
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
        // GPT-Live startup can enqueue newDeviceAvailable (1) followed by
        // categoryChange (3) and routeConfigurationChange (8). All three
        // must preserve a healthy graph on the already selected microphone.
        for reason in [UInt(1), 3, 8, 1] {
            assert(!DebugHandsFreeAudioPolicy.shouldStop(reason: reason, configurationMatches: true))
        }
        // Adding another available device is harmless only if the actual
        // route, format, category, mode AND running engine remain unchanged.
        for reason in [UInt(0), 1, 3, 4, 6, 8] {
            assert(!DebugHandsFreeAudioPolicy.shouldStop(reason: reason, configurationMatches: true))
            assert(DebugHandsFreeAudioPolicy.shouldStop(reason: reason, configurationMatches: false))
        }
        // Unplugging an active device still stops the call, including a
        // queued disconnect/reconnect whose current route looks unchanged.
        for reason in [UInt?](arrayLiteral: 2, 7, nil, 999) {
            assert(DebugHandsFreeAudioPolicy.shouldStop(reason: reason, configurationMatches: true))
            assert(DebugHandsFreeAudioPolicy.shouldStop(reason: reason, configurationMatches: false))
        }
    }

    static func testEngineStartup() {
        // The first start succeeds synchronously, then iOS stops the engine
        // during its asynchronous configuration notification. Recover before
        // admitting the provider stream; old microphone buffers do not count.
        var startup = DebugHandsFreeAudioStartup(now: 10)
        assert(startup.poll(now: 10, engineRunning: true, lastCaptureAt: nil, playbackReady: true) == .wait)
        startup.configurationChanged(now: 10.01)
        assert(startup.poll(now: 10.02, engineRunning: false, lastCaptureAt: 10.01, playbackReady: true) == .restart)
        assert(startup.poll(now: 10.03, engineRunning: true, lastCaptureAt: 10.01, playbackReady: true) == .wait)
        assert(startup.poll(now: 10.08, engineRunning: true, lastCaptureAt: 10.07, playbackReady: true) == .wait)
        assert(startup.poll(now: 10.46, engineRunning: true, lastCaptureAt: 10.45, playbackReady: true) == .ready)

        // A new configuration invalidates readiness, even while isRunning
        // stays true. A frozen tap must never admit a voice session.
        var quiet = DebugHandsFreeAudioStartup(now: 0)
        assert(quiet.poll(now: 0.01, engineRunning: true, lastCaptureAt: 0.005, playbackReady: true) == .wait)
        quiet.configurationChanged(now: 0.1)
        assert(quiet.poll(now: 0.2, engineRunning: true, lastCaptureAt: 0.005, playbackReady: true) == .wait)
        assert(quiet.poll(now: 0.3, engineRunning: true, lastCaptureAt: 0.29, playbackReady: true) == .wait)
        assert(quiet.poll(now: 0.6, engineRunning: true, lastCaptureAt: 0.29, playbackReady: true) == .wait)
        assert(quiet.poll(now: 3, engineRunning: true, lastCaptureAt: nil, playbackReady: true) == .fail)

        // A live microphone and running engine are insufficient when the
        // player's queued buffers never reach the output (iPhone regression).
        var silentOutput = DebugHandsFreeAudioStartup(now: 0)
        assert(silentOutput.poll(now: 0.4, engineRunning: true, lastCaptureAt: 0.39, playbackReady: false) == .wait)
        assert(silentOutput.poll(now: 0.76, engineRunning: true, lastCaptureAt: 0.75, playbackReady: false) == .restart)
        assert(silentOutput.poll(now: 0.99, engineRunning: true, lastCaptureAt: 0.98, playbackReady: true) == .wait)
        assert(silentOutput.poll(now: 1.31, engineRunning: true, lastCaptureAt: 1.30, playbackReady: true) == .ready)
        var neverPlays = DebugHandsFreeAudioStartup(now: 0)
        for time in [0.76, 1.52, 2.28] {
            assert(neverPlays.poll(now: time, engineRunning: true, lastCaptureAt: time - 0.01, playbackReady: false) == .restart)
        }
        assert(neverPlays.poll(now: 3, engineRunning: true, lastCaptureAt: 2.99, playbackReady: false) == .fail)

        var broken = DebugHandsFreeAudioStartup(now: 0)
        for now in [0.01, 0.02, 0.03] {
            assert(broken.poll(now: now, engineRunning: false, lastCaptureAt: nil, playbackReady: true) == .restart)
        }
        assert(broken.poll(now: 0.04, engineRunning: false, lastCaptureAt: nil, playbackReady: true) == .fail)
        // Notifications cannot extend the absolute startup deadline.
        var noisy = DebugHandsFreeAudioStartup(now: 0)
        noisy.configurationChanged(now: 2.99)
        assert(noisy.poll(now: 3, engineRunning: false, lastCaptureAt: nil, playbackReady: true) == .fail)
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
