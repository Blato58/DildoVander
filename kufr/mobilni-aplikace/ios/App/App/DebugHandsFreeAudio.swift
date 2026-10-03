import AVFoundation
import Foundation

// Called under the audio tap's lock. Batch by PCM duration rather than by
// hardware buffer size: 9,600 bytes are exactly 200 ms of mono PCM16/24 kHz.
struct DebugHandsFreeUploadBuffer {
    struct Frame {
        let inputId: String
        let sequence: Int
        let data: Data
    }

    static let chunkBytes = 9_600
    static let capacityBytes = 48_000
    private(set) var inputId: String?
    private(set) var epoch = UUID()
    private var pcm = Data()
    private var sequence = 0
    var isReady: Bool { inputId != nil && pcm.count >= Self.chunkBytes }
    // Main-queue faults must belong to the capture epoch that raised them.
    // A terminal input gate or a new listening turn invalidates queued faults.
    func isCurrent(epoch: UUID) -> Bool { self.epoch == epoch }

    mutating func begin(inputId: String) {
        stop()
        self.inputId = inputId
        sequence = 0
    }

    mutating func stop() {
        epoch = UUID()
        inputId = nil
        pcm.removeAll(keepingCapacity: true)
    }

    // A conversion that crossed a VAD gate belongs to the previous input.
    // Discard it without reporting overflow or relabelling it as new speech.
    mutating func append(_ data: Data, epoch: UUID) -> Bool {
        guard epoch == self.epoch, inputId != nil else { return true }
        guard pcm.count + data.count <= Self.capacityBytes else {
            stop()
            return false
        }
        pcm.append(data)
        return true
    }

    mutating func next() -> Frame? {
        guard isReady, let inputId else { return nil }
        let data = Data(pcm.prefix(Self.chunkBytes))
        pcm.removeFirst(Self.chunkBytes)
        let frame = Frame(inputId: inputId, sequence: sequence, data: data)
        sequence += 1
        return frame
    }
}

enum DebugHandsFreeAudioPolicy {
    // AVAudioSession.RouteChangeReason raw values. Keep the notification
    // mapping testable on macOS as well as the device. Availability is not
    // evidence that our active input/output changed: voice-processing setup
    // can publish a new-device notification for the already selected route.
    static func shouldStop(reason: UInt?, configurationMatches: Bool) -> Bool {
        switch reason {
        case 0, 1, 3, 4, 6, 8: // unknown, new device, category, override, wake, configuration
            return !configurationMatches
        default: // old device unavailable (2), no suitable route (7), missing/future reason
            return true
        }
    }
}

// AVAudioEngine can stop asynchronously while voice processing establishes
// its hardware graph. Do not open the provider stream until real microphone
// callbacks, completed output probe and a running engine are healthy for 300 ms.
struct DebugHandsFreeAudioStartup {
    enum Action { case wait, restart, ready, fail }
    private let deadline: TimeInterval
    private var attemptStartedAt: TimeInterval
    private var healthySince: TimeInterval?
    private var restarts = 0

    init(now: TimeInterval) {
        deadline = now + 3
        attemptStartedAt = now
    }

    mutating func configurationChanged(now: TimeInterval) {
        attemptStartedAt = now
        healthySince = nil
    }

    mutating func poll(now: TimeInterval, engineRunning: Bool, lastCaptureAt: TimeInterval?, playbackReady: Bool) -> Action {
        guard now < deadline else { return .fail }
        if !engineRunning || (!playbackReady && now - attemptStartedAt >= 0.75) {
            configurationChanged(now: now)
            guard restarts < 3 else { return .fail }
            restarts += 1
            return .restart
        }
        guard playbackReady, let capture = lastCaptureAt, capture > attemptStartedAt, now - capture < 0.2 else {
            healthySince = nil
            return .wait
        }
        if let healthySince, now - healthySince >= 0.3 { return .ready }
        if healthySince == nil { healthySince = now }
        return .wait
    }
}

// Continued network arrivals must not hide a stalled speaker queue.
struct DebugHandsFreePlaybackProgress {
    private(set) var pending = 0
    private var lastProgress: TimeInterval = 0
    mutating func enqueue(now: TimeInterval) {
        if pending == 0 { lastProgress = now }
        pending += 1
    }
    mutating func complete(now: TimeInterval) {
        guard pending > 0 else { return }
        pending -= 1
        lastProgress = now
    }
    func stalled(now: TimeInterval) -> Bool { pending > 0 && now - lastProgress >= 2.5 }
}

// AVAudioConverter can legitimately return no output while collecting enough
// input for resampling. Keep its state between microphone buffers.
final class DebugHandsFreePCMConverter {
    let sourceFormat: AVAudioFormat
    private let targetFormat: AVAudioFormat
    private let converter: AVAudioConverter

    init(sourceFormat: AVAudioFormat) throws {
        guard sourceFormat.sampleRate > 0, sourceFormat.channelCount > 0,
              let target = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 24_000,
                                         channels: 1, interleaved: true),
              let converter = AVAudioConverter(from: sourceFormat, to: target) else {
            throw DebugBackendError.serverCode("realtime_live_audio_start_failed")
        }
        self.sourceFormat = sourceFormat
        self.targetFormat = target
        self.converter = converter
    }

    func convert(_ source: AVAudioPCMBuffer) throws -> Data? {
        guard source.frameLength > 0 else { return nil }
        let capacity = AVAudioFrameCount(Double(source.frameLength) * targetFormat.sampleRate / sourceFormat.sampleRate + 2)
        guard let converted = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else {
            throw DebugBackendError.serverCode("realtime_live_audio_conversion_failed")
        }
        var conversionError: NSError?
        var suppliedSource = false
        let result = converter.convert(to: converted, error: &conversionError) { _, status in
            guard !suppliedSource else {
                status.pointee = .noDataNow
                return nil
            }
            suppliedSource = true
            status.pointee = .haveData
            return source
        }
        guard conversionError == nil, result != .error else {
            throw DebugBackendError.serverCode("realtime_live_audio_conversion_failed")
        }
        guard converted.frameLength > 0 else { return nil }
        guard let samples = converted.int16ChannelData?[0] else {
            throw DebugBackendError.serverCode("realtime_live_audio_conversion_failed")
        }
        return Data(bytes: samples, count: Int(converted.frameLength) * MemoryLayout<Int16>.size)
    }
}

// Shared with native tests: initialize the valid frame range before filling it.
enum DebugHandsFreePlayback {
    // Rebuild only while stopped and before any provider audio is queued.
    // Reset the player's clock after engine configuration changes and explicitly
    // connect the mixer to the current hardware (or offline renderer) format.
    static func prepare(engine: AVAudioEngine, player: AVAudioPlayerNode) throws {
        guard !engine.isRunning else { throw DebugBackendError.invalidResponse }
        if player.engine == nil { engine.attach(player) }
        player.stop()
        let output = engine.outputNode
        let outputFormat = output.outputFormat(forBus: 0)
        guard outputFormat.sampleRate > 0, outputFormat.channelCount > 0,
              let pcmFormat = AVAudioFormat(standardFormatWithSampleRate: 24_000, channels: 1)
        else { throw DebugBackendError.serverCode("realtime_live_audio_start_failed") }
        let mixer = engine.mainMixerNode
        engine.disconnectNodeOutput(player)
        engine.disconnectNodeOutput(mixer)
        engine.connect(player, to: mixer, fromBus: 0, toBus: 0, format: pcmFormat)
        engine.connect(mixer, to: output, format: outputFormat)
        engine.prepare()
    }

    static func schedule(_ buffer: AVAudioPCMBuffer, player: AVAudioPlayerNode,
                         completion: @escaping @Sendable (AVAudioPlayerNodeCompletionCallbackType) -> Void) {
        // Start with a scheduled buffer, never an empty player during engine warmup.
        player.scheduleBuffer(buffer, completionCallbackType: .dataPlayedBack, completionHandler: completion)
        if !player.isPlaying { player.play() }
    }

    static func buffer(from pcm: Data) throws -> AVAudioPCMBuffer {
        guard !pcm.isEmpty, pcm.count <= 49_152, pcm.count % 2 == 0,
              let format = AVAudioFormat(standardFormatWithSampleRate: 24_000, channels: 1),
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(pcm.count / 2))
        else { throw DebugBackendError.invalidResponse }
        buffer.frameLength = buffer.frameCapacity
        guard let channel = buffer.floatChannelData?[0] else { throw DebugBackendError.invalidResponse }
        // Decode bytes explicitly; Data does not promise Int16 alignment.
        pcm.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) in
            for index in 0..<Int(buffer.frameLength) {
                let bits = UInt16(bytes[index * 2]) | UInt16(bytes[index * 2 + 1]) << 8
                channel[index] = Float(Int16(bitPattern: bits)) / 32768.0
            }
        }
        return buffer
    }
}

#if os(iOS)
// One duplex engine remains active for the entire hands-free conversation.
// Realtime gates the tap during playback. GPT-Live keeps it running with
// Apple voice processing for acoustic echo cancellation.
@MainActor
final class DebugHandsFreeAudio {
    typealias Upload = (String, Int, Data) async throws -> Void
    typealias PlaybackDrained = (String) -> Void
    typealias Failure = (String) -> Void

    private let fullDuplex: Bool
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let upload: Upload
    private let playbackDrained: PlaybackDrained
    private let failure: Failure
    nonisolated(unsafe) private let lock = NSLock()
    private var converter: DebugHandsFreePCMConverter?
    private var activeRoute: [String] = []
    private var audioActive = false
    private var startup: DebugHandsFreeAudioStartup?
    private var tapInstalled = false
    private var probeGeneration = UUID()
    private var playbackReady = false
    private var playbackWatchdog: Task<Void, Never>?
    // These fields are shared only with the real-time input-tap callback and
    // are always read or written while holding lock.
    nonisolated(unsafe) private var uploadBuffer = DebugHandsFreeUploadBuffer()
    nonisolated(unsafe) private var inputEnabled = false
    nonisolated(unsafe) private var lastCaptureAt: TimeInterval?
    private var drainTask: Task<Void, Never>?
    private var receivedFrames = 0
    private var renderedFrames = 0
    private var playbackProgress = DebugHandsFreePlaybackProgress()
    private var awaitingDrainOperation: String?
    private var generation = UUID()
    private var observers: [NSObjectProtocol] = []

    init(fullDuplex: Bool = false, upload: @escaping Upload, playbackDrained: @escaping PlaybackDrained, failure: @escaping Failure) {
        self.fullDuplex = fullDuplex
        self.upload = upload
        self.playbackDrained = playbackDrained
        self.failure = failure
    }

    func start() async throws {
        guard !engine.isRunning else { return }
        let audioSession = AVAudioSession.sharedInstance()
        // Realtime uses half duplex with normal speaker audio. GPT-Live
        // keeps capture running and enables Apple voice processing for echo
        // cancellation; its speaker level still requires device acceptance.
        try audioSession.setCategory(.playAndRecord, mode: fullDuplex ? .voiceChat : .default, options: [.defaultToSpeaker])
        try audioSession.setActive(true)

        let input = engine.inputNode
        if fullDuplex { try input.setVoiceProcessingEnabled(true) }
        // Voice-processing setup can select the built-in receiver. This is a
        // hands-free conversation; retain external outputs but use the loudspeaker
        // for the phone's own route before recording the stable configuration.
        if audioSession.currentRoute.outputs.contains(where: { $0.portType == .builtInReceiver }) {
            try audioSession.overrideOutputAudioPort(.speaker)
        }

        let source = input.outputFormat(forBus: 0)
        let converter = try DebugHandsFreePCMConverter(sourceFormat: source)
        self.converter = converter
        input.installTap(onBus: 0, bufferSize: 2_400, format: source) { [weak self] buffer, _ in
            self?.capture(buffer, converter: converter)
        }
        tapInstalled = true
        audioActive = true
        activeRoute = routeIdentifiers(audioSession.currentRoute)
        startup = DebugHandsFreeAudioStartup(now: ProcessInfo.processInfo.systemUptime)
        observeAudioSession()
        do {
            try startPlaybackAttempt()
            try await finishStartup()
            startPlaybackWatchdog()
        } catch {
            stop()
            throw error
        }
    }

    private func startPlaybackAttempt() throws {
        // stop() may invoke old completion handlers: invalidate them first.
        probeGeneration = UUID()
        playbackReady = false
        engine.stop()
        try DebugHandsFreePlayback.prepare(engine: engine, player: player)
        try engine.start()
        let probe = try DebugHandsFreePlayback.buffer(from: Data(repeating: 0, count: 9_600))
        let attempt = probeGeneration
        DebugHandsFreePlayback.schedule(probe, player: player) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.audioActive, self.probeGeneration == attempt,
                      self.engine.isRunning, self.player.isPlaying,
                      let rendered = self.player.lastRenderTime,
                      let position = self.player.playerTime(forNodeTime: rendered),
                      position.isSampleTimeValid, position.sampleTime > 0
                else { return }
                self.playbackReady = true
            }
        }
    }

    private func startPlaybackWatchdog() {
        playbackWatchdog = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(nanoseconds: 100_000_000) } catch { return }
                guard let self, self.audioActive else { return }
                if self.playbackProgress.stalled(now: ProcessInfo.processInfo.systemUptime) {
                    self.audioFailure(code: "realtime_live_audio_output_stalled")
                    return
                }
            }
        }
    }

    private func latestCaptureTime() -> TimeInterval? {
        lock.lock()
        defer { lock.unlock() }
        return lastCaptureAt
    }

    private func finishStartup() async throws {
        while audioActive {
            try Task.checkCancellation()
            if let code = configurationFailure() { throw DebugBackendError.serverCode(code) }
            let action = startup?.poll(now: ProcessInfo.processInfo.systemUptime,
                                       engineRunning: engine.isRunning, lastCaptureAt: latestCaptureTime(),
                                       playbackReady: playbackReady)
            switch action {
            case .ready:
                startup = nil
                return
            case .restart:
                // No provider audio has been requested or scheduled yet.
                // Same route/format are enforced by notification handlers.
                try startPlaybackAttempt()
            case .wait:
                break
            default:
                throw DebugBackendError.serverCode(playbackReady ? "realtime_live_audio_start_unstable" : "realtime_live_audio_output_stalled")
            }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        throw CancellationError()
    }

    func beginListening(inputId: String) {
        lock.lock()
        uploadBuffer.begin(inputId: inputId)
        inputEnabled = fullDuplex || (playbackProgress.pending == 0 && awaitingDrainOperation == nil)
        let shouldDrain = inputEnabled
        lock.unlock()
        if shouldDrain { scheduleDrain() }
    }

    func stopUploading() {
        lock.lock()
        inputEnabled = false
        uploadBuffer.stop()
        lock.unlock()
    }

    func appendPlayback(_ pcm: Data) throws {
        guard audioActive, engine.isRunning else {
            throw DebugBackendError.serverCode("realtime_live_audio_engine_stopped")
        }
        let buffer = try DebugHandsFreePlayback.buffer(from: pcm)
        let frames = Int(buffer.frameLength)
        if !fullDuplex { stopUploading() }
        receivedFrames += frames
        lock.lock()
        playbackProgress.enqueue(now: ProcessInfo.processInfo.systemUptime)
        let callbackGeneration = generation
        lock.unlock()
        DebugHandsFreePlayback.schedule(buffer, player: player) { [weak self] _ in
            Task { @MainActor in self?.playedBack(callbackGeneration, frames: frames) }
        }
    }

    var outputStatus: [String: Any] {
        let session = AVAudioSession.sharedInstance()
        let ports = session.currentRoute.outputs.map { $0.portType }
        let route = ports.contains(.builtInSpeaker) ? "speaker"
            : ports.contains(.builtInReceiver) ? "receiver"
            : ports.isEmpty ? "none" : "external"
        return ["route": route, "volume": Double(session.outputVolume),
                "receivedSeconds": Double(receivedFrames) / 24_000,
                "renderedSeconds": Double(renderedFrames) / 24_000,
                "engineRunning": engine.isRunning]
    }

    func finishPlayback(operationId: String) {
        lock.lock()
        awaitingDrainOperation = operationId
        let shouldNotify = playbackProgress.pending == 0
        if shouldNotify { awaitingDrainOperation = nil }
        lock.unlock()
        if shouldNotify { playbackDrained(operationId) }
    }

    func stop() {
        audioActive = false
        startup = nil
        probeGeneration = UUID()
        playbackReady = false
        playbackWatchdog?.cancel()
        playbackWatchdog = nil
        lock.lock()
        lastCaptureAt = nil
        generation = UUID()
        inputEnabled = false
        uploadBuffer.stop()
        awaitingDrainOperation = nil
        playbackProgress = DebugHandsFreePlaybackProgress()
        lock.unlock()
        drainTask?.cancel()
        drainTask = nil
        if tapInstalled {
            engine.inputNode.removeTap(onBus: 0)
            tapInstalled = false
        }
        player.stop()
        engine.stop()
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers.removeAll()
        converter = nil
        activeRoute = []
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    private nonisolated func capture(_ source: AVAudioPCMBuffer, converter: DebugHandsFreePCMConverter) {
        lock.lock()
        if source.frameLength > 0 { lastCaptureAt = ProcessInfo.processInfo.systemUptime }
        let acceptingInput = inputEnabled
        let epoch = uploadBuffer.epoch
        lock.unlock()
        guard acceptingInput else { return }
        do {
            guard let data = try converter.convert(source) else { return }
            enqueueCaptured(data, epoch: epoch)
        } catch {
            DispatchQueue.main.async { [weak self] in self?.audioConversionFailed() }
        }
    }

    private nonisolated func enqueueCaptured(_ data: Data, epoch: UUID) {
        lock.lock()
        defer { lock.unlock() }
        guard inputEnabled else { return }
        guard uploadBuffer.append(data, epoch: epoch) else {
            inputEnabled = false
            let failureEpoch = uploadBuffer.epoch
            DispatchQueue.main.async { [weak self] in self?.audioQueueOverflowed(epoch: failureEpoch) }
            return
        }
        if uploadBuffer.isReady {
            DispatchQueue.main.async { [weak self] in self?.scheduleDrain() }
        }
    }

    private func scheduleDrain() {
        guard drainTask == nil else { return }
        drainTask = Task { [weak self] in await self?.drainFrames() }
    }

    private func drainFrames() async {
        defer { drainTask = nil }
        while true {
            guard !Task.isCancelled else { return }
            lock.lock()
            guard inputEnabled, let frame = uploadBuffer.next() else { lock.unlock(); return }
            lock.unlock()
            do {
                try await upload(frame.inputId, frame.sequence, frame.data)
            } catch {
                stopUploading()
                let code = DebugHandsFreeSession.failureCode(for: error)
                failure(code == "realtime_live_failed" ? "realtime_live_audio_failed" : code)
                return
            }
        }
    }

    private func playedBack(_ callbackGeneration: UUID, frames: Int) {
        lock.lock()
        guard callbackGeneration == generation, playbackProgress.pending > 0 else { lock.unlock(); return }
        renderedFrames += frames
        playbackProgress.complete(now: ProcessInfo.processInfo.systemUptime)
        let operation = playbackProgress.pending == 0 ? awaitingDrainOperation : nil
        if operation != nil { awaitingDrainOperation = nil }
        lock.unlock()
        if let operation { playbackDrained(operation) }
    }

    private func observeAudioSession() {
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] notification in
            guard let type = notification.userInfo?[AVAudioSessionInterruptionTypeKey] as? NSNumber,
                  type.uintValue == AVAudioSession.InterruptionType.began.rawValue else { return }
            DispatchQueue.main.async { self?.audioFailure(code: "realtime_live_audio_interrupted") }
        })
        observers.append(center.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: .main) { [weak self] notification in
            let reason = (notification.userInfo?[AVAudioSessionRouteChangeReasonKey] as? NSNumber)?.uintValue
            DispatchQueue.main.async { self?.routeChanged(reason: reason) }
        })
        observers.append(center.addObserver(forName: AVAudioSession.mediaServicesWereResetNotification, object: nil, queue: .main) { [weak self] _ in
            DispatchQueue.main.async { self?.audioFailure(code: "realtime_live_audio_services_reset") }
        })
        observers.append(center.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main) { [weak self] _ in
            // Leave the notification callback before touching the engine.
            DispatchQueue.main.async { self?.routeChanged(reason: 8) }
        })
    }

    private func routeIdentifiers(_ route: AVAudioSessionRouteDescription) -> [String] {
        route.inputs.map { "input:\($0.uid)" } + route.outputs.map { "output:\($0.uid)" }
    }

    private func routeChanged(reason: UInt?) {
        guard audioActive else { return }
        let graphFailure = configurationFailure()
        let configurationMatches = (engine.isRunning || startup != nil) && graphFailure == nil
        if startup != nil {
            startup?.configurationChanged(now: ProcessInfo.processInfo.systemUptime)
        }
        // Inspect the current graph, not the route that existed when a queued
        // startup notification was posted. Never ignore a lost device or an
        // unusable graph, even if its last reported route is still identical.
        // Only startup may restart a stopped engine, before provider audio.
        if DebugHandsFreeAudioPolicy.shouldStop(reason: reason, configurationMatches: configurationMatches) {
            let code: String
            if reason == 2 || reason == 7 { code = "realtime_live_audio_route_changed" }
            else if let graphFailure { code = graphFailure }
            else if !engine.isRunning { code = "realtime_live_audio_engine_stopped" }
            else { code = "realtime_live_audio_route_unknown" }
            audioFailure(code: code)
        }
    }

    private func configurationFailure() -> String? {
        let session = AVAudioSession.sharedInstance()
        if routeIdentifiers(session.currentRoute) != activeRoute { return "realtime_live_audio_route_changed" }
        if session.category != .playAndRecord || session.mode != (fullDuplex ? .voiceChat : .default)
            || (fullDuplex && !engine.inputNode.isVoiceProcessingEnabled) {
            return "realtime_live_audio_session_changed"
        }
        if converter?.sourceFormat != engine.inputNode.outputFormat(forBus: 0) { return "realtime_live_audio_format_changed" }
        return nil
    }

    private func audioFailure(code: String) {
        // iOS may stop the engine before notifying us. Only our own stop()
        // marks the lifecycle inactive, so genuine interruptions still clean up.
        guard audioActive else { return }
        stop()
        failure(code)
    }

    private func audioQueueOverflowed(epoch: UUID) {
        guard audioActive else { return }
        lock.lock()
        let current = uploadBuffer.isCurrent(epoch: epoch)
        lock.unlock()
        guard current else { return }
        failure("realtime_live_audio_overflow")
    }

    private func audioConversionFailed() {
        guard audioActive else { return }
        lock.lock()
        let acceptingInput = inputEnabled
        lock.unlock()
        guard acceptingInput else { return }
        stopUploading()
        failure("realtime_live_audio_conversion_failed")
    }
}
#endif
