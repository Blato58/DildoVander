import Foundation

@main
struct NativeRealtimeTests {
    @MainActor static func main() async throws {
        guard let base = ProcessInfo.processInfo.environment["KUFR_DEBUG_HTTP_PEER"], let url = URL(string: base) else {
            fatalError("Missing HTTP peer")
        }
        let expected: [String: Any] = ["deviceId": "debug-test", "bootId": "00000000-0000-4000-8000-000000000001",
                                       "conversationId": "00000000-0000-4000-8000-000000000002",
                                       "clientTurnId": "00000000-0000-4000-8000-000000000003", "generation": 0, "deviceRevision": 0]
        try expectFraming(expected: expected)
        try expectLargestAudioChunk(expected: expected)
        try expectWrongCorrelation(expected: expected)
        try expectNoNewline(expected: expected)
        try await expectCleanupRetry()
        try await expectCancellation(url.appendingPathComponent("realtime-hang"), expected: expected)
    }

    private static func expectFraming(expected: [String: Any]) throws {
        let operationId = "00000000-0000-4000-8000-000000000004"
        let events: [[String: Any]] = [
            ["type": "started", "operationId": operationId, "correlation": expected, "audioFormat": "pcm16-mono-24000"],
            ["type": "audio", "delta": Data([0, 0, 1, 0]).base64EncodedString()],
            ["type": "visitor", "text": "Ahoj"],
            ["type": "completed", "operationId": operationId, "correlation": expected,
             "result": ["reply": "synthetic", "decision": "ask", "reason": "", "proposal": NSNull()]],
        ]
        let wire = try events.map { try JSONSerialization.data(withJSONObject: $0) + Data([10]) }.reduce(Data(), +)
        let framer = DebugRealtimeFramer(expectedCorrelation: expected)
        let first = try framer.append(wire.prefix(17))
        let rest = try framer.append(wire.dropFirst(17))
        try framer.finish()
        assert(first.isEmpty && rest.count == 4)
    }

    private static func expectNoNewline(expected: [String: Any]) throws {
        let framer = DebugRealtimeFramer(expectedCorrelation: expected)
        do {
            _ = try framer.append(Data(repeating: 65, count: 65_537))
            fatalError("Expected no-newline limit")
        } catch is DebugBackendError {
            return
        }
    }

    private static func expectLargestAudioChunk(expected: [String: Any]) throws {
        let operationId = "00000000-0000-4000-8000-000000000004"
        let started: [String: Any] = ["type": "started", "operationId": operationId,
                                      "correlation": expected, "audioFormat": "pcm16-mono-24000"]
        let audio: [String: Any] = ["type": "audio", "delta": Data(repeating: 0, count: 32_768).base64EncodedString()]
        let framer = DebugRealtimeFramer(expectedCorrelation: expected)
        _ = try framer.append(try JSONSerialization.data(withJSONObject: started) + Data([10]))
        let events = try framer.append(try JSONSerialization.data(withJSONObject: audio) + Data([10]))
        guard case .audio(let pcm) = events.first else { fatalError("Expected audio event") }
        assert(pcm.count == 32_768)
    }

    private static func expectWrongCorrelation(expected: [String: Any]) throws {
        var wrong = expected
        wrong["generation"] = 1
        let event: [String: Any] = ["type": "started", "operationId": "00000000-0000-4000-8000-000000000004",
                                     "correlation": wrong, "audioFormat": "pcm16-mono-24000"]
        let framer = DebugRealtimeFramer(expectedCorrelation: expected)
        do {
            _ = try framer.append(try JSONSerialization.data(withJSONObject: event) + Data([10]))
            fatalError("Expected correlation rejection")
        } catch is DebugBackendError {
            return
        }
    }

    @MainActor private static func expectCleanupRetry() async throws {
        let conversation = "00000000-0000-4000-8000-000000000005"
        var deletes = 0
        let client = DebugBackendClient(http: { request, _ in
            assert(request.httpMethod == "DELETE" && request.url?.path == "/v1/conversations/\(conversation)")
            deletes += 1
            if deletes < 3 { throw URLError(.networkConnectionLost) }
            return (Data(), HTTPURLResponse(url: request.url!, statusCode: 204, httpVersion: "HTTP/1.1", headerFields: nil)!)
        })
        let profile = try DebugBackendProfile(address: "https://example.test/", deviceId: "debug-test", credential: "credential")
        let session = DebugRealtimeSession(client: client, profile: profile, token: "access", conversation: conversation,
                                           boot: "00000000-0000-4000-8000-000000000001", readyMask: 0, voice: "cedar", speed: 1)
        do {
            try await session.close()
            fatalError("Expected first cleanup failure")
        } catch is URLError {
            // DebugBackendClient performs its own two attempts. This verifies
            // the session remains retryable after both have failed.
        }
        try await session.close()
        assert(deletes == 3)
    }

    private static func expectCancellation(_ url: URL, expected: [String: Any]) async throws {
        let task = Task {
            let stream = DebugRealtimeStream(request: URLRequest(url: url), expectedCorrelation: expected)
            for try await _ in stream.events() { }
        }
        try await Task.sleep(nanoseconds: 25_000_000)
        task.cancel()
        do {
            _ = try await task.value
            fatalError("Expected stream cancellation")
        } catch is CancellationError {
            return
        } catch let error as URLError where error.code == .cancelled {
            return
        }
    }
}
