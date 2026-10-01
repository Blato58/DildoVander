import Capacitor
import UIKit
import AVFoundation

@objc(KufrDebugPlugin)
public class KufrDebugPlugin: CAPPlugin, CAPBridgedPlugin, AVAudioPlayerDelegate, AVSpeechSynthesizerDelegate {
    public let identifier = "KufrDebugPlugin"
    public let jsName = "KufrDebug"
    public let pluginMethods: [CAPPluginMethod] = ["status", "configure", "inspectBackend", "inspectServer", "changeServerModes", "runOpeningSpeech", "speakSystem", "stop", "shareLog"].map {
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
            call.resolve(["configured": self.profile != nil, "running": self.task != nil || self.player != nil || self.synthesizer.isSpeaking,
                          "phase": self.phase, "mode": self.mode,
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
        UIApplication.shared.applicationState == .active && task == nil && player == nil && !synthesizer.isSpeaking && configurationCall == nil
    }

    private func stopWork() {
        generation += 1
        task?.cancel(); task = nil
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
            self.player = nil; self.phase = flag ? "completed" : "failed"; self.deactivateAudio()
        }
    }
    public func audioPlayerDecodeErrorDidOccur(_ player: AVAudioPlayer, error: Error?) {
        DispatchQueue.main.async {
            guard self.player === player else { return }
            self.player = nil; self.phase = "failed"; self.deactivateAudio()
        }
    }
    public func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        DispatchQueue.main.async {
            if self.phase == "playing", self.player == nil { self.phase = "completed"; self.deactivateAudio() }
        }
    }
}
