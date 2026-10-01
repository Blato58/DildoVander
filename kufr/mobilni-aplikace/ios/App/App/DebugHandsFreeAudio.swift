import AVFoundation
import Foundation

enum DebugHandsFreeAudioPolicy {
    enum RouteChange {
        case category, speakerOverride, configuration, device
    }

    static func shouldStop(change: RouteChange, configurationMatches: Bool) -> Bool {
        switch change {
        case .category, .speakerOverride, .configuration:
            return !configurationMatches
        case .device:
            return true
        }
    }
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

#if os(iOS)
// One duplex engine remains active for the entire hands-free conversation.
// The tap is deliberately gated while model audio is queued, so it cannot
// upload the phone speaker as a visitor response.
@MainActor
final class DebugHandsFreeAudio {
    typealias Upload = (String, Int, Data) async throws -> Void
    typealias PlaybackDrained = (String) -> Void
    typealias Failure = (String) -> Void

    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let upload: Upload
    private let playbackDrained: PlaybackDrained
    private let failure: Failure
    nonisolated(unsafe) private let lock = NSLock()
    private var converter: DebugHandsFreePCMConverter?
    private var activeRoute: [String] = []
    private var tapInstalled = false
    private var playerAttached = false
    // These fields are shared only with the real-time input-tap callback and
    // are always read or written while holding lock.
    nonisolated(unsafe) private var queuedFrames: [(inputId: String, sequence: Int, data: Data)] = []
    nonisolated(unsafe) private var queuedBytes = 0
    nonisolated(unsafe) private var currentInputId: String?
    nonisolated(unsafe) private var nextSequence = 0
    nonisolated(unsafe) private var inputEnabled = false
    private var drainTask: Task<Void, Never>?
    private var queuedPlayback = 0
    private var awaitingDrainOperation: String?
    private var generation = UUID()
    private var observers: [NSObjectProtocol] = []

    init(upload: @escaping Upload, playbackDrained: @escaping PlaybackDrained, failure: @escaping Failure) {
        self.upload = upload
        self.playbackDrained = playbackDrained
        self.failure = failure
    }

    func start() throws {
        guard !engine.isRunning else { return }
        let audioSession = AVAudioSession.sharedInstance()
        try audioSession.setCategory(.playAndRecord, mode: .voiceChat, options: [.defaultToSpeaker])
        try audioSession.setActive(true)

        guard let outputFormat = AVAudioFormat(standardFormatWithSampleRate: 24_000, channels: 1) else {
            throw DebugBackendError.serverCode("realtime_live_audio_start_failed")
        }
        if !playerAttached {
            engine.attach(player)
            engine.connect(player, to: engine.mainMixerNode, format: outputFormat)
            playerAttached = true
        }

        let input = engine.inputNode
        let source = input.outputFormat(forBus: 0)
        let converter = try DebugHandsFreePCMConverter(sourceFormat: source)
        self.converter = converter
        input.installTap(onBus: 0, bufferSize: 2_400, format: source) { [weak self] buffer, _ in
            self?.capture(buffer, converter: converter)
        }
        tapInstalled = true
        do {
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            tapInstalled = false
            throw error
        }
        player.play()
        activeRoute = routeIdentifiers(audioSession.currentRoute)
        observeAudioSession()
    }

    func beginListening(inputId: String) {
        lock.lock()
        queuedFrames.removeAll(keepingCapacity: true)
        queuedBytes = 0
        currentInputId = inputId
        nextSequence = 0
        inputEnabled = queuedPlayback == 0 && awaitingDrainOperation == nil
        let shouldDrain = inputEnabled
        lock.unlock()
        if shouldDrain { scheduleDrain() }
    }

    func stopUploading() {
        lock.lock()
        inputEnabled = false
        queuedFrames.removeAll(keepingCapacity: true)
        queuedBytes = 0
        lock.unlock()
    }

    func appendPlayback(_ pcm: Data) throws {
        guard pcm.count > 0, pcm.count <= 49_152, pcm.count % 2 == 0 else { throw DebugBackendError.invalidResponse }
        stopUploading()
        let frames = pcm.count / MemoryLayout<Int16>.size
        guard let format = AVAudioFormat(standardFormatWithSampleRate: 24_000, channels: 1),
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)),
              let channel = buffer.floatChannelData?[0] else { throw DebugBackendError.invalidResponse }
        pcm.withUnsafeBytes { source in
            let samples = source.bindMemory(to: Int16.self)
            for index in 0..<frames { channel[index] = Float(Int16(littleEndian: samples[index])) / 32768.0 }
        }
        buffer.frameLength = AVAudioFrameCount(frames)
        lock.lock()
        queuedPlayback += 1
        let callbackGeneration = generation
        lock.unlock()
        player.scheduleBuffer(buffer, completionCallbackType: .dataPlayedBack) { [weak self] _ in
            Task { @MainActor in self?.playedBack(callbackGeneration) }
        }
        if !player.isPlaying { player.play() }
    }

    func finishPlayback(operationId: String) {
        lock.lock()
        awaitingDrainOperation = operationId
        let shouldNotify = queuedPlayback == 0
        if shouldNotify { awaitingDrainOperation = nil }
        lock.unlock()
        if shouldNotify { playbackDrained(operationId) }
    }

    func stop() {
        lock.lock()
        generation = UUID()
        inputEnabled = false
        queuedFrames.removeAll(keepingCapacity: false)
        queuedBytes = 0
        awaitingDrainOperation = nil
        queuedPlayback = 0
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
        let acceptingInput = inputEnabled
        lock.unlock()
        guard acceptingInput else { return }
        do {
            guard let data = try converter.convert(source) else { return }
            enqueueCaptured(data)
        } catch {
            DispatchQueue.main.async { [weak self] in self?.audioConversionFailed() }
        }
    }

    private nonisolated func enqueueCaptured(_ data: Data) {
        lock.lock()
        defer { lock.unlock() }
        guard inputEnabled, let inputId = currentInputId else { return }
        guard queuedBytes + data.count <= 48_000 else {
            inputEnabled = false
            queuedFrames.removeAll(keepingCapacity: true)
            queuedBytes = 0
            DispatchQueue.main.async { [weak self] in self?.audioQueueOverflowed() }
            return
        }
        var offset = 0
        while offset < data.count {
            let length = min(9_600, data.count - offset)
            guard queuedBytes + length <= 48_000 else {
                inputEnabled = false
                queuedFrames.removeAll(keepingCapacity: true)
                queuedBytes = 0
                DispatchQueue.main.async { [weak self] in self?.audioQueueOverflowed() }
                return
            }
            queuedFrames.append((inputId, 0, data.subdata(in: offset..<(offset + length))))
            queuedBytes += length
            offset += length
        }
        DispatchQueue.main.async { [weak self] in self?.scheduleDrain() }
    }

    private func scheduleDrain() {
        guard drainTask == nil else { return }
        drainTask = Task { [weak self] in await self?.drainFrames() }
    }

    private func drainFrames() async {
        defer { drainTask = nil }
        try? await Task.sleep(nanoseconds: 150_000_000)
        while true {
            guard !Task.isCancelled else { return }
            lock.lock()
            guard inputEnabled, !queuedFrames.isEmpty else { lock.unlock(); return }
            let first = queuedFrames.removeFirst()
            var data = first.data
            while let next = queuedFrames.first, next.inputId == first.inputId,
                  data.count + next.data.count <= 9_600 {
                queuedFrames.removeFirst()
                data.append(next.data)
            }
            queuedBytes -= data.count
            let sequence = nextSequence
            nextSequence += 1
            lock.unlock()
            do {
                try await upload(first.inputId, sequence, data)
            } catch {
                stopUploading()
                let code = DebugHandsFreeSession.failureCode(for: error)
                failure(code == "realtime_live_failed" ? "realtime_live_audio_failed" : code)
                return
            }
        }
    }

    private func playedBack(_ callbackGeneration: UUID) {
        lock.lock()
        guard callbackGeneration == generation, queuedPlayback > 0 else { lock.unlock(); return }
        queuedPlayback -= 1
        let operation = queuedPlayback == 0 ? awaitingDrainOperation : nil
        if operation != nil { awaitingDrainOperation = nil }
        lock.unlock()
        if let operation { playbackDrained(operation) }
    }

    private func observeAudioSession() {
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] notification in
            guard let type = notification.userInfo?[AVAudioSessionInterruptionTypeKey] as? NSNumber,
                  type.uintValue == AVAudioSession.InterruptionType.began.rawValue else { return }
            DispatchQueue.main.async { self?.audioFailure() }
        })
        observers.append(center.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: .main) { [weak self] notification in
            let reason = (notification.userInfo?[AVAudioSessionRouteChangeReasonKey] as? NSNumber)?.uintValue
            DispatchQueue.main.async { self?.routeChanged(reason: reason) }
        })
        observers.append(center.addObserver(forName: AVAudioSession.mediaServicesWereResetNotification, object: nil, queue: .main) { [weak self] _ in
            DispatchQueue.main.async { self?.audioFailure() }
        })
    }

    private func routeIdentifiers(_ route: AVAudioSessionRouteDescription) -> [String] {
        route.inputs.map { "input:\($0.uid)" } + route.outputs.map { "output:\($0.uid)" }
    }

    private func routeChanged(reason: UInt?) {
        guard engine.isRunning else { return }
        let change: DebugHandsFreeAudioPolicy.RouteChange
        switch reason.flatMap(AVAudioSession.RouteChangeReason.init(rawValue:)) {
        case .categoryChange: change = .category
        case .override: change = .speakerOverride
        case .routeConfigurationChange: change = .configuration
        default: change = .device
        }
        let session = AVAudioSession.sharedInstance()
        let configurationMatches = session.category == .playAndRecord
            && session.mode == .voiceChat
            && routeIdentifiers(session.currentRoute) == activeRoute
            && converter?.sourceFormat == engine.inputNode.outputFormat(forBus: 0)
        // Starting our own playAndRecord session posts route notifications too.
        // Only tolerate those when the active route and capture format agree.
        if DebugHandsFreeAudioPolicy.shouldStop(change: change, configurationMatches: configurationMatches) {
            audioFailure()
        }
    }

    private func audioFailure() {
        guard engine.isRunning else { return }
        stop()
        failure("realtime_live_audio_interrupted")
    }

    private func audioQueueOverflowed() {
        guard engine.isRunning else { return }
        failure("realtime_live_audio_overflow")
    }

    private func audioConversionFailed() {
        guard engine.isRunning else { return }
        lock.lock()
        let acceptingInput = inputEnabled
        lock.unlock()
        guard acceptingInput else { return }
        stopUploading()
        failure("realtime_live_audio_conversion_failed")
    }
}
#endif
