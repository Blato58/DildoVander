import Capacitor
import UIKit
import AVFoundation

@objc(KufrDebugPlugin)
public class KufrDebugPlugin: CAPPlugin, CAPBridgedPlugin, AVAudioPlayerDelegate, AVSpeechSynthesizerDelegate {
    public let identifier = "KufrDebugPlugin"
    public let jsName = "KufrDebug"
    public let pluginMethods: [CAPPluginMethod] = ["status", "configure", "inspectBackend", "inspectServer", "changeServerModes", "runOpeningSpeech", "startConversation", "answerConversation", "startRecording", "finishRecording", "speakSystem", "stop", "shareLog"].map {
        CAPPluginMethod(name: $0, returnType: CAPPluginReturnPromise)
    }
    private var profile: DebugBackendProfile?
    private var task: Task<Void, Never>?
    private var player: AVAudioPlayer?
    private let synthesizer = AVSpeechSynthesizer()
    private var phase = "idle"
    private var mode = "unknown"
    private var serverModes: DebugServerModes?
    private var generation = 0
    private var observers: [NSObjectProtocol] = []
    private var configurationCall: CAPPluginCall?
    private weak var configurationAlert: UIAlertController?
    private var dialogSession: DebugDialogSession?
    private var transcript: [[String: String]] = []
    private var selected: Int?
    private var timings: [String: Double] = [:]
    private var microphone: DebugMicrophone?
    private var recordingTimer: Timer?
    private var cleanupTask: Task<Void, Never>?

    public override func load() {
        synthesizer.delegate = self
        observeAudioInterruptions()
        for name in [UIApplication.didEnterBackgroundNotification, UIApplication.protectedDataWillBecomeUnavailableNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                guard let self = self else { return }
                self.stopWork()
                self.profile = nil
                self.mode = "unknown"
                self.serverModes = nil
                self.configurationAlert?.textFields?.forEach { $0.text = "" }
                self.configurationAlert?.dismiss(animated: false)
                self.configurationCall?.reject("Nastavení přerušeno.", "cancelled")
                self.configurationCall = nil
            })
        }
    }

    private func observeAudioInterruptions() {
        observers.append(NotificationCenter.default.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] notification in
            if let type = notification.userInfo?[AVAudioSessionInterruptionTypeKey] as? NSNumber,
               type.uintValue == AVAudioSession.InterruptionType.began.rawValue {
                self?.stopWork()
            }
        })
    }

    deinit {
        task?.cancel()
        player?.stop()
        observers.forEach { NotificationCenter.default.removeObserver($0) }
    }

    @objc func status(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            call.resolve(["configured": self.profile != nil, "running": self.task != nil || self.player != nil || self.synthesizer.isSpeaking || self.microphone != nil || self.cleanupTask != nil,
                          "phase": self.phase, "mode": self.mode,
                          "session": self.dialogSession != nil, "recording": self.microphone != nil,
                          "transcript": self.transcript, "selected": (self.selected as Any?) ?? NSNull(), "timings": self.timings,
                          "server": (self.serverModes?.bridgeValue as Any?) ?? NSNull()])
        }
    }

    @objc func configure(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canStart(), self.configurationCall == nil, let controller = self.bridge?.viewController else {
                call.reject("Nastavení není dostupné.", "busy"); return
            }
            self.configurationCall = call
            let alert = UIAlertController(title: "Serverový / testovací přístup", message:
                "Zadejte HTTPS adresu a credential správce s ID debug-server-admin, nebo samostatnou debug- testovací identitu. Údaje zůstanou jen v nativní paměti do přechodu do pozadí.", preferredStyle: .alert)
            alert.addTextField { $0.placeholder = "https://backend.example.cz"; $0.keyboardType = .URL; $0.autocapitalizationType = .none; $0.autocorrectionType = .no }
            alert.addTextField { $0.placeholder = "debug-server-admin"; $0.autocapitalizationType = .none; $0.autocorrectionType = .no }
            alert.addTextField { $0.placeholder = "Backend device credential"; $0.isSecureTextEntry = true; $0.autocapitalizationType = .none; $0.autocorrectionType = .no }
            alert.addAction(UIAlertAction(title: "Zrušit", style: .cancel) { _ in
                self.configurationAlert?.textFields?.forEach { $0.text = "" }
                self.configurationCall = nil; call.reject("Nastavení zrušeno.", "cancelled")
            })
            alert.addAction(UIAlertAction(title: "Nastavit", style: .default) { [weak alert] _ in
                self.configurationCall = nil
                do {
                    self.profile = try DebugBackendProfile(address: alert?.textFields?[0].text ?? "",
                        deviceId: alert?.textFields?[1].text ?? "", credential: alert?.textFields?[2].text ?? "")
                    self.mode = "unknown"; self.serverModes = nil; self.phase = "idle"; call.resolve()
                } catch {
                    self.profile = nil; self.mode = "unknown"; self.serverModes = nil; self.phase = "failed"
                    call.reject("Vyžaduje HTTPS, debug- device ID a platný credential.", "invalid_profile")
                }
                alert?.textFields?.forEach { $0.text = "" }
            })
            self.configurationAlert = alert
            controller.present(alert, animated: true)
        }
    }

    @objc func inspectBackend(_ call: CAPPluginCall) { start(call, expectedMode: nil) }
    @objc func inspectServer(_ call: CAPPluginCall) { serverAction(call, change: false) }
    @objc func changeServerModes(_ call: CAPPluginCall) { serverAction(call, change: true) }

    @objc func startConversation(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canStart(), let profile = self.profile else { call.reject("Nastavte backend a ukončete předchozí test.", "unavailable"); return }
            self.generation += 1
            let generation = self.generation
            self.transcript = []; self.selected = nil; self.timings = [:]; self.phase = "checking"
            self.task = Task { @MainActor in
                var owned: DebugDialogSession?
                do {
                    let session = try await DebugBackendClient().openSession(profile,
                        expectedMode: call.getString("expectedMode") ?? "", readyMask: call.getInt("readyMask") ?? -1,
                        voice: call.getString("voice") ?? "cedar", speed: call.getDouble("speed") ?? 1.1)
                    owned = session
                    try self.requireCurrent(generation)
                    self.dialogSession = session
                    self.mode = call.getString("expectedMode") ?? "unknown"
                    try await self.dialogTurn(session, text: nil, generation: generation)
                    if self.generation == generation { self.task = nil }
                    call.resolve()
                } catch {
                    if let owned = owned { try? await owned.close() }
                    if self.generation == generation { self.failDialog() }
                    call.reject("Rozhovor nebyl dokončen. Ověřte přístup, režim a limity serveru.", "dialog_failed")
                }
            }
        }
    }

    @objc func answerConversation(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canAnswer(), let session = self.dialogSession, let text = call.getString("text"),
                  !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, text.unicodeScalars.count <= 1000 else {
                call.reject("Počkejte na otázku a zadejte odpověď do 1000 znaků.", "unavailable"); return
            }
            self.submitAnswer(call, session: session, text: text, wav: nil)
        }
    }

    private func submitAnswer(_ call: CAPPluginCall, session: DebugDialogSession, text: String?, wav: Data?) {
        let generation = self.generation
        self.task = Task { @MainActor in
            do {
                let answer: String
                if let wav = wav {
                    self.phase = "transcription"
                    answer = try await session.transcribe(wav)
                } else { answer = text ?? "" }
                try self.requireCurrent(generation)
                self.transcript.append(["role": "visitor", "text": answer])
                try await self.dialogTurn(session, text: answer, generation: generation)
                if self.generation == generation { self.task = nil }
                call.resolve()
            } catch {
                try? await session.close()
                if self.generation == generation { self.failDialog() }
                call.reject("Odpověď nebyla dokončena. Rozhovor byl ukončen.", "dialog_failed")
            }
        }
    }

    @MainActor private func requireCurrent(_ generation: Int) throws {
        try Task.checkCancellation()
        guard self.generation == generation, UIApplication.shared.applicationState == .active else { throw CancellationError() }
    }

    @MainActor private func dialogTurn(_ session: DebugDialogSession, text: String?, generation: Int) async throws {
        let result = try await session.advance(text, phase: { value in
            if self.generation == generation { self.phase = value }
        }, reply: { value in
            if self.generation == generation { self.transcript.append(["role": "kufr", "text": value]) }
        })
        try requireCurrent(generation)
        try activateAudio()
        let player = try AVAudioPlayer(data: result.wav)
        player.delegate = self
        self.player = player
        guard player.play() else { throw DebugBackendError.invalidResponse }
        self.phase = "playing"; self.selected = result.selected; self.timings = result.timings
        // Playback starts before the cleanup round trip. The next turn waits
        // for both playback and release of the backend's single audio slot.
        try await session.releaseSpeech()
        if result.terminal {
            try await session.close()
            try requireCurrent(generation)
            dialogSession = nil
        }
        try requireCurrent(generation)
        if self.player == nil { phase = result.terminal ? "completed" : "awaiting" }
    }

    private func failDialog() {
        task = nil; dialogSession = nil; player?.stop(); player = nil
        phase = "failed"; deactivateAudio()
    }

    @objc func startRecording(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canAnswer() else { call.reject("Počkejte na otázku.", "unavailable"); return }
            let generation = self.generation
            self.phase = "checking"
            self.task = Task { @MainActor in
                do {
                    let permitted: Bool
                    if #available(iOS 17.0, *) { permitted = await AVAudioApplication.requestRecordPermission() }
                    else {
                        permitted = await withCheckedContinuation { continuation in
                            AVAudioSession.sharedInstance().requestRecordPermission { continuation.resume(returning: $0) }
                        }
                    }
                    try self.requireCurrent(generation)
                    guard permitted else { throw DebugBackendError.invalidResponse }
                    let microphone = DebugMicrophone()
                    self.microphone = microphone
                    try microphone.start { [weak self, weak microphone] in
                        DispatchQueue.main.async { [weak self, weak microphone] in
                            if let microphone = microphone, self?.generation == generation,
                               self?.microphone === microphone { self?.stopWork() }
                        }
                    }
                    self.recordingTimer = Timer.scheduledTimer(withTimeInterval: 30, repeats: false) { [weak self] _ in
                        // At the cap keep the recording for explicit Send; never
                        // upload a visitor's recording without that action.
                        self?.microphone?.pause()
                    }
                    self.phase = "recording"; self.task = nil; call.resolve()
                } catch {
                    if self.generation == generation {
                        self.microphone?.cancel(); self.microphone = nil; self.task = nil
                        self.phase = "awaiting"; self.deactivateAudio()
                    }
                    call.reject("Mikrofon není dostupný nebo není povolený.", "microphone_unavailable")
                }
            }
        }
    }

    @objc func finishRecording(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard UIApplication.shared.applicationState == .active, self.task == nil,
                  let microphone = self.microphone, let session = self.dialogSession else {
                call.reject("Nahrávání není aktivní.", "unavailable"); return
            }
            self.recordingTimer?.invalidate(); self.recordingTimer = nil
            self.microphone = nil
            do {
                let wav = try microphone.finish()
                self.deactivateAudio()
                self.submitAnswer(call, session: session, text: nil, wav: wav)
            } catch {
                microphone.cancel(); self.phase = "awaiting"; self.deactivateAudio()
                call.reject("Nahrávka je prázdná nebo neplatná.", "recording_invalid")
            }
        }
    }

    private func serverAction(_ call: CAPPluginCall, change: Bool) {
        DispatchQueue.main.async {
            guard self.canStart(), let profile = self.profile else {
                call.reject("Nejdříve nastavte serverový přístup.", "unavailable"); return
            }
            let revision = call.getString("expectedRevision")
            if change && revision == nil { call.reject("Obnovte stav serveru.", "invalid_mode"); return }
            self.generation += 1
            let generation = self.generation
            self.serverModes = nil; self.mode = "unknown"; self.phase = "checking"
            self.task = Task { @MainActor in
                do {
                    let modes = try await DebugBackendClient().serverModes(profile,
                        expectedRevision: change ? revision : nil,
                        dialogMode: call.getString("dialogMode"), voiceMode: call.getString("voiceMode"))
                    try Task.checkCancellation()
                    guard self.generation == generation, UIApplication.shared.applicationState == .active else { throw CancellationError() }
                    self.serverModes = modes; self.mode = modes.voiceMode
                    self.phase = "idle"; self.task = nil; call.resolve()
                } catch {
                    if self.generation == generation {
                        self.serverModes = nil; self.mode = "unknown"; self.task = nil
                        self.phase = error is CancellationError ? "cancelled" : "failed"
                    }
                    call.reject("Serverový stav nebyl ověřen. Obnovte jej a zkontrolujte oprávnění správce.", "server_control_failed")
                }
            }
        }
    }
    @objc func runOpeningSpeech(_ call: CAPPluginCall) {
        guard let expectedMode = call.getString("expectedMode"), ["fake", "openai"].contains(expectedMode) else {
            call.reject("Neplatný režim testu.", "invalid_mode"); return
        }
        start(call, expectedMode: expectedMode)
    }

    private func start(_ call: CAPPluginCall, expectedMode: String?) {
        DispatchQueue.main.async {
            guard self.canStart(), let profile = self.profile else { call.reject("Nejdříve nastavte backend.", "unavailable"); return }
            self.generation += 1
            let generation = self.generation
            self.phase = "checking"
            self.task = Task { @MainActor in
                do {
                    let client = DebugBackendClient()
                    if let expectedMode = expectedMode {
                        let wav = try await client.run(profile, expectedMode: expectedMode) { value in
                            if self.generation == generation { self.phase = value }
                        }
                        try Task.checkCancellation()
                        guard self.generation == generation, UIApplication.shared.applicationState == .active else { throw CancellationError() }
                        try self.activateAudio()
                        let player = try AVAudioPlayer(data: wav)
                        player.delegate = self
                        self.player = player
                        guard player.play() else { throw DebugBackendError.invalidResponse }
                        self.phase = "playing"
                        self.mode = expectedMode
                    } else {
                        let mode = try await client.inspect(profile)
                        try Task.checkCancellation()
                        guard self.generation == generation else { throw CancellationError() }
                        self.mode = mode; self.phase = "idle"
                    }
                    if self.generation == generation { self.task = nil }
                    call.resolve()
                } catch {
                    if self.generation == generation {
                        self.task = nil; self.player?.stop(); self.player = nil
                        self.phase = error is CancellationError ? "cancelled" : "failed"
                        self.mode = "unknown"
                        self.deactivateAudio()
                    }
                    call.reject("Hlasový test nebyl dokončen. Ověřte backend, credential, režim a AI limity.", "debug_test_failed")
                }
            }
        }
    }

    @objc func speakSystem(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canStart() else { call.reject("Jiný test běží.", "busy"); return }
            do {
                try self.activateAudio()
                let utterance = AVSpeechUtterance(string: "Ahoj, tady kufr. Tohle je test reproduktoru telefonu.")
                utterance.voice = AVSpeechSynthesisVoice(language: "cs-CZ")
                self.phase = "playing"; self.synthesizer.speak(utterance); call.resolve()
            } catch { self.phase = "failed"; call.reject("Reproduktor není dostupný.", "audio_unavailable") }
        }
    }

    @objc func stop(_ call: CAPPluginCall) {
        DispatchQueue.main.async { self.stopWork(); call.resolve() }
    }

    @objc func shareLog(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard UIApplication.shared.applicationState == .active,
                  let json = call.getString("json"), json.utf8.count <= 131_072,
                  let data = json.data(using: .utf8),
                  (try? JSONSerialization.jsonObject(with: data)) != nil,
                  let controller = self.bridge?.viewController,
                  controller.presentedViewController == nil else {
                call.reject("Export nyní není dostupný.", "export_unavailable"); return
            }
            let share = UIActivityViewController(activityItems: [json], applicationActivities: nil)
            share.popoverPresentationController?.sourceView = controller.view
            share.popoverPresentationController?.sourceRect = CGRect(x: controller.view.bounds.midX, y: controller.view.bounds.midY, width: 1, height: 1)
            controller.present(share, animated: true)
            call.resolve()
        }
    }

    private func canStart() -> Bool {
        canAnswer() && dialogSession == nil
    }

    private func canAnswer() -> Bool {
        UIApplication.shared.applicationState == .active && task == nil && player == nil &&
            !synthesizer.isSpeaking && configurationCall == nil && microphone == nil && cleanupTask == nil
    }

    private func stopWork() {
        generation += 1
        let stoppedTask = task
        task?.cancel(); task = nil
        recordingTimer?.invalidate(); recordingTimer = nil
        microphone?.cancel(); microphone = nil
        transcript = []; selected = nil; timings = [:]
        let session = dialogSession
        dialogSession = nil
        if cleanupTask == nil && (session != nil || stoppedTask != nil) {
            cleanupTask = Task { @MainActor in
                // A cancelled create can still be recovering its owned ID.
                // Do not admit another test until that recovery has finished.
                if let stoppedTask = stoppedTask { await stoppedTask.value }
                if let session = session {
                    do { try await session.close() }
                    catch { self.phase = "failed" }
                }
                self.cleanupTask = nil
            }
        }
        player?.stop(); player = nil
        synthesizer.stopSpeaking(at: .immediate)
        phase = "cancelled"
        deactivateAudio()
    }

    private func activateAudio() throws {
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playback, mode: .spokenAudio, options: [])
        try session.setActive(true)
    }
    private func deactivateAudio() { try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation) }

    public func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        DispatchQueue.main.async {
            guard self.player === player else { return }
            self.player = nil; self.phase = flag ? (self.dialogSession == nil ? "completed" : "awaiting") : "failed"; self.deactivateAudio()
            if !flag { self.stopWork(); self.phase = "failed" }
        }
    }
    public func audioPlayerDecodeErrorDidOccur(_ player: AVAudioPlayer, error: Error?) {
        DispatchQueue.main.async {
            guard self.player === player else { return }
            self.stopWork(); self.phase = "failed"
        }
    }
    public func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        DispatchQueue.main.async {
            if self.phase == "playing", self.player == nil { self.phase = "completed"; self.deactivateAudio() }
        }
    }
}

// Audio stays in bounded RAM. Conversion happens on the audio tap queue;
// UIKit and session lifecycle remain on the main queue.
private final class DebugMicrophone: @unchecked Sendable {
    private let engine = AVAudioEngine()
    private let lock = NSLock()
    private var pcm = Data()
    private var accepting = true
    private var tapInstalled = false

    func start(failed: @escaping () -> Void) throws {
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playAndRecord, mode: .measurement, options: [.defaultToSpeaker])
        try session.setActive(true)
        let input = engine.inputNode
        let source = input.outputFormat(forBus: 0)
        guard source.sampleRate > 0, source.channelCount > 0,
              let target = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 32_000, channels: 1, interleaved: true),
              let converter = AVAudioConverter(from: source, to: target) else { throw DebugBackendError.invalidResponse }
        input.installTap(onBus: 0, bufferSize: 1024, format: source) { [weak self] buffer, _ in
            guard let self = self else { return }
            let capacity = AVAudioFrameCount(ceil(Double(buffer.frameLength) * 32_000 / source.sampleRate) + 64)
            guard let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { failed(); return }
            var supplied = false
            var error: NSError?
            let status = converter.convert(to: output, error: &error) { _, status in
                if supplied { status.pointee = .noDataNow; return nil }
                supplied = true; status.pointee = .haveData; return buffer
            }
            guard status != .error, error == nil else { failed(); return }
            guard let samples = output.int16ChannelData?.pointee else { return }
            self.lock.lock()
            if self.accepting {
                let count = min(Int(output.frameLength) * 2, 1_920_000 - self.pcm.count)
                if count > 0 { self.pcm.append(UnsafeRawPointer(samples).assumingMemoryBound(to: UInt8.self), count: count) }
                if self.pcm.count == 1_920_000 { self.accepting = false }
            }
            self.lock.unlock()
        }
        tapInstalled = true
        engine.prepare()
        try engine.start()
    }

    func pause() {
        lock.lock(); accepting = false; lock.unlock()
        engine.stop()
        if tapInstalled { engine.inputNode.removeTap(onBus: 0); tapInstalled = false }
    }

    func cancel() {
        pause()
        lock.lock(); pcm.resetBytes(in: 0..<pcm.count); pcm.removeAll(keepingCapacity: false); lock.unlock()
    }

    func finish() throws -> Data {
        pause()
        lock.lock(); defer { lock.unlock() }
        guard pcm.count >= 19_200 else { throw DebugBackendError.invalidResponse }
        var wav = Data()
        func tag(_ value: String) { wav.append(Data(value.utf8)) }
        func number(_ value: UInt32, bytes: Int) {
            for offset in 0..<bytes { wav.append(UInt8(truncatingIfNeeded: value >> (offset * 8))) }
        }
        tag("RIFF"); number(UInt32(pcm.count + 36), bytes: 4); tag("WAVE"); tag("fmt ")
        number(16, bytes: 4); number(1, bytes: 2); number(1, bytes: 2); number(32_000, bytes: 4)
        number(64_000, bytes: 4); number(2, bytes: 2); number(16, bytes: 2); tag("data")
        number(UInt32(pcm.count), bytes: 4); wav.append(pcm)
        pcm.resetBytes(in: 0..<pcm.count); pcm.removeAll(keepingCapacity: false)
        return wav
    }
}
