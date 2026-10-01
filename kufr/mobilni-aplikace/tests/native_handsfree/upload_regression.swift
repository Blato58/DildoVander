import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

@main
struct NativeHandsFreeUploadTests {
    static func main() async throws {
        testBatchingAndEpoch()
        testBoundedBacklog()
        guard let address = ProcessInfo.processInfo.environment["KUFR_DEBUG_HTTP_PEER"], let baseURL = URL(string: address) else {
            fatalError("Missing HTTP peer")
        }
        try await testContinuousUpload(baseURL: baseURL)
        try await testRedirectAndCancellation(baseURL: baseURL)
        print("Hands-free 200ms batching, bounded backlog, input epoch, connection reuse and cancellation passed.")
    }

    static func testBatchingAndEpoch() {
        var buffer = DebugHandsFreeUploadBuffer()
        buffer.begin(inputId: "first")
        let epoch = buffer.epoch
        let original = Data((0..<26_120).map { UInt8($0 % 251) })
        // 44.1kHz capture/resampling does not align with the HTTP chunk size.
        for offset in stride(from: 0, to: original.count, by: 2_612) {
            assert(buffer.append(original.subdata(in: offset..<(offset + 2_612)), epoch: epoch))
        }
        let first = buffer.next()!
        let second = buffer.next()!
        assert(first.inputId == "first" && first.sequence == 0 && second.sequence == 1)
        assert(first.data == original.prefix(9_600))
        assert(second.data == original.subdata(in: 9_600..<19_200))
        assert(buffer.next() == nil) // Retain the partial chunk, don't POST it.
        buffer.begin(inputId: "second")
        assert(buffer.append(Data(repeating: 255, count: 9_600), epoch: epoch))
        assert(buffer.next() == nil) // A late conversion must not cross inputs.
        assert(buffer.append(Data(repeating: 7, count: 9_600), epoch: buffer.epoch))
        let fresh = buffer.next()!
        assert(fresh.inputId == "second" && fresh.sequence == 0 && fresh.data.allSatisfy { $0 == 7 })
        let stoppedEpoch = buffer.epoch
        buffer.stop()
        assert(buffer.append(Data(repeating: 1, count: 9_600), epoch: stoppedEpoch))
        assert(buffer.next() == nil)
    }

    static func testBoundedBacklog() {
        var buffer = DebugHandsFreeUploadBuffer()
        buffer.begin(inputId: "slow")
        assert(buffer.append(Data(repeating: 0, count: 48_000), epoch: buffer.epoch))
        assert(!buffer.append(Data([0, 0]), epoch: buffer.epoch))
        assert(buffer.next() == nil && buffer.inputId == nil)
        buffer.begin(inputId: "recovered")
        assert(buffer.append(Data(repeating: 0, count: 9_600), epoch: buffer.epoch))
        assert(buffer.next()?.sequence == 0)
    }

    // A 50ms hardware tap runs independently while sequential HTTP replies
    // take 150ms, with an extra startup delay on the first frame. Verify the
    // actual byte stream, sequence and socket reuse at the loopback peer.
    @MainActor static func testContinuousUpload(baseURL: URL) async throws {
        let requests = DebugHandsFreeRequest()
        defer { requests.finish() }
        var buffer = DebugHandsFreeUploadBuffer()
        buffer.begin(inputId: "upload-regression")
        let capture = Task { @MainActor in
            for index in 0..<40 {
                try await Task.sleep(nanoseconds: 50_000_000)
                assert(buffer.append(Data(repeating: UInt8(index), count: 2_400), epoch: buffer.epoch))
            }
        }
        defer { capture.cancel() }
        var sent = 0
        let deadline = Date().addingTimeInterval(10)
        while sent < 10 {
            assert(Date() < deadline, "Continuous upload stalled")
            guard let frame = buffer.next() else {
                try await Task.sleep(nanoseconds: 5_000_000)
                continue
            }
            var request = URLRequest(url: baseURL.appendingPathComponent("test/upload"))
            request.httpMethod = "POST"
            request.httpBody = frame.data
            request.setValue(frame.inputId, forHTTPHeaderField: "Input-Id")
            request.setValue(String(frame.sequence), forHTTPHeaderField: "Input-Sequence")
            let (body, response) = try await requests.perform(request, limit: 4096)
            assert(body.isEmpty && response.statusCode == 204)
            sent += 1
        }
        try await capture.value
        assert(buffer.next() == nil)
        let (data, _) = try await URLSession.shared.data(from: baseURL.appendingPathComponent("test/metrics"))
        let metrics = try JSONSerialization.jsonObject(with: data) as! [String: Int]
        assert(metrics["regressionUploads"] == 10 && metrics["regressionSockets"] == 1)
    }

    static func testRedirectAndCancellation(baseURL: URL) async throws {
        let draining = DebugHandsFreeRequest()
        let finishing = Task {
            try await draining.perform(URLRequest(url: baseURL.appendingPathComponent("test/finish")), limit: 4096)
        }
        try await Task.sleep(nanoseconds: 50_000_000)
        draining.finish()
        let (_, completed) = try await finishing.value
        assert(completed.statusCode == 204)

        let requests = DebugHandsFreeRequest()
        let (_, response) = try await requests.perform(URLRequest(url: baseURL.appendingPathComponent("test/redirect")), limit: 4096)
        assert(response.statusCode == 302)
        let pending = Task {
            try await requests.perform(URLRequest(url: baseURL.appendingPathComponent("test/stall")), limit: 4096)
        }
        try await Task.sleep(nanoseconds: 50_000_000)
        requests.cancel()
        do {
            _ = try await pending.value
            fatalError("Cancelled upload completed")
        } catch let error as URLError {
            assert(error.code == .cancelled)
        }
        let (data, _) = try await URLSession.shared.data(from: baseURL.appendingPathComponent("test/metrics"))
        let metrics = try JSONSerialization.jsonObject(with: data) as! [String: Int]
        assert(metrics["redirected"] == 0)
    }
}
