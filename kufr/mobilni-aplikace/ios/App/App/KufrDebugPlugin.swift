import Capacitor
import UIKit
import AVFoundation

@objc(KufrDebugPlugin)
public class KufrDebugPlugin: CAPPlugin, CAPBridgedPlugin, AVAudioPlayerDelegate, AVSpeechSynthesizerDelegate {
    public let identifier = "KufrDebugPlugin"
    public let jsName = "KufrDebug"
    public let pluginMethods: [CAPPluginMethod] = ["status", "configure", "forgetProfile", "inspectEvenings", "createEvening", "activateEvening", "inspectBackend", "inspectServer", "changeServerModes", "runOpeningSpeech", "startConversation", "answerConversation", "startRecording", "finishRecording", "speakSystem", "stop", "shareLog", "readServerLogs"].map {
        CAPPluginMethod(name: $0, returnType: CAPPluginReturnPromise)
    }
    private var profile: DebugBackendProfile?
    private var logTask: Task<Void, Never>?
    private let profileKeychain = DebugBackendKeychain()
    private var evenings: DebugEveningList?
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
    private var realtimeSession: DebugRealtimeSession?
    private var realtimePlayer: DebugPCMPlayer?
    private var transcript: [[String: String]] = []
    private var selected: Int?
    private var timings: [String: Double] = [:]
    private var microphone: DebugMicrophone?
    private var handsFreeSession: DebugHandsFreeSession?
    private var handsFreeAudio: DebugHandsFreeAudio?
    private var lastError: String?
    private var recordingTimer: Timer?
    private var cleanupTask: Task<Void, Never>?

    public override func load() {
        synthesizer.delegate = self
        observeAudioInterruptions()
        for name in [UIApplication.didEnterBackgroundNotification, UIApplication.protectedDataWillBecomeUnavailableNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                // NotificationCenter's main queue is not a Swift actor annotation.
                DispatchQueue.main.async {
                    guard let self = self else { return }
                    self.stopWork()
                    self.profile = nil
                    self.mode = "unknown"
                    self.serverModes = nil
                    self.evenings = nil
                    self.configurationAlert?.textFields?.forEach { $0.text = "" }
                    self.configurationAlert?.dismiss(animated: false)
                    self.configurationCall?.reject("Nastavení přerušeno.", "cancelled")
                    self.configurationCall = nil
                }
            })
        }
    }

    private func observeAudioInterruptions() {
        observers.append(NotificationCenter.default.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] notification in
            if let type = notification.userInfo?[AVAudioSessionInterruptionTypeKey] as? NSNumber,
               type.uintValue == AVAudioSession.InterruptionType.began.rawValue {
                DispatchQueue.main.async { self?.stopWork() }
            }
        })
    }

    deinit {
        logTask?.cancel()
        task?.cancel()
        player?.stop()
        observers.forEach { NotificationCenter.default.removeObserver($0) }
    }

    @objc func status(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            self.restoreProfile()
            call.resolve(["configured": self.profile != nil, "running": self.task != nil || self.player != nil || self.realtimePlayer != nil || self.synthesizer.isSpeaking || self.microphone != nil || self.cleanupTask != nil,
                          "phase": self.phase, "mode": self.mode,
                          "session": self.dialogSession != nil || self.realtimeSession != nil || self.handsFreeSession != nil,
                          "recording": self.microphone != nil, "handsFree": self.handsFreeSession != nil,
                          "errorCode": (self.lastError as Any?) ?? NSNull(),
                          "transcript": self.transcript, "selected": (self.selected as Any?) ?? NSNull(), "timings": self.timings,
                          "evenings": (self.evenings?.bridgeValue as Any?) ?? NSNull(),
                          "server": (self.serverModes?.bridgeValue as Any?) ?? NSNull()])
        }
    }

    @objc func readServerLogs(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            self.restoreProfile()
            guard self.logTask == nil, self.configurationCall == nil, let profile = self.profile,
                  UIApplication.shared.applicationState == .active else {
                call.reject("Nastavte přístup správce serveru.", "logs_unavailable"); return
            }
            let after = call.getDouble("after")
            guard after == nil || after!.isFinite && after! >= 0 && after! <= 9_007_199_254_740_991 && after!.rounded() == after!
            else { call.reject("Neplatný kurzor logu.", "invalid_cursor"); return }
            let level = call.getString("level") ?? "info"
            self.logTask = Task { @MainActor in
                defer { self.logTask = nil }
                do {
                    let page = try await DebugBackendClient().serverLogs(profile, after: after.map { Int64($0) }, level: level)
                    try Task.checkCancellation()
                    guard UIApplication.shared.applicationState == .active,
                          self.profile?.baseURL == profile.baseURL, self.profile?.deviceId == profile.deviceId,
                          self.profile?.credential == profile.credential else { throw CancellationError() }
                    call.resolve(page.bridgeValue)
                } catch {
                    call.reject("Logy nelze načíst. Ověřte přístup správce a verzi serveru.", "logs_failed")
                }
            }
        }
    }

    @objc func configure(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canStart(), self.configurationCall == nil, let controller = self.bridge?.viewController else {
                call.reject("Nastavení není dostupné.", "busy"); return
            }
            self.logTask?.cancel()
            self.restoreProfile()
            let saved = self.profile
            self.configurationCall = call
            let alert = UIAlertController(title: "Serverový / testovací přístup", message:
                "Přístup se uloží do Keychain tohoto iPhonu a zachová při aktualizaci aplikace. Credential se nikdy nezobrazí ve webu ani logu. Prázdné pole ponechá uložený credential pouze pro stejnou adresu a identitu.", preferredStyle: .alert)
            alert.addTextField {
                $0.text = saved?.baseURL.absoluteString ?? DebugProfileDraft.defaultAddress
                $0.keyboardType = .URL; $0.autocapitalizationType = .none; $0.autocorrectionType = .no
            }
            alert.addTextField {
                $0.text = saved?.deviceId ?? DebugProfileDraft.defaultDeviceId
                $0.autocapitalizationType = .none; $0.autocorrectionType = .no
            }
            alert.addTextField {
                $0.placeholder = saved == nil ? "Backend device credential" : "Uložený credential · prázdné = ponechat"
                $0.isSecureTextEntry = true; $0.autocapitalizationType = .none; $0.autocorrectionType = .no
            }
            alert.addAction(UIAlertAction(title: "Zrušit", style: .cancel) { _ in
                self.configurationAlert?.textFields?.forEach { $0.text = "" }
                self.configurationCall = nil; call.reject("Nastavení zrušeno.", "cancelled")
            })
            alert.addAction(UIAlertAction(title: "Nastavit", style: .default) { [weak alert] _ in
                self.configurationCall = nil
                do {
                    let updated = try DebugProfileDraft.resolve(address: alert?.textFields?[0].text ?? "",
                        deviceId: alert?.textFields?[1].text ?? "", credential: alert?.textFields?[2].text ?? "", saved: saved)
                    try self.profileKeychain.write(updated)
                    self.profile = updated
                    self.mode = "unknown"; self.serverModes = nil; self.evenings = nil; self.phase = "idle"; call.resolve()
                } catch {
                    self.phase = "failed"
                    call.reject("Přístup nebyl uložen. Ověřte HTTPS, debug- identitu a credential; při změně adresy nebo identity zadejte credential znovu.", "invalid_profile")
                }
                alert?.textFields?.forEach { $0.text = "" }
            })
            self.configurationAlert = alert
            controller.present(alert, animated: true)
        }
    }

    private func restoreProfile() {
        guard profile == nil, UIApplication.shared.applicationState == .active,
              UIApplication.shared.isProtectedDataAvailable else { return }
        do { profile = try profileKeychain.read() }
        catch { phase = "failed" }
    }

    @objc func forgetProfile(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canStart() else { call.reject("Nejprve ukončete probíhající test.", "busy"); return }
            do {
                try self.profileKeychain.remove()
                self.logTask?.cancel()
                self.profile = nil; self.mode = "unknown"; self.serverModes = nil; self.evenings = nil
                self.transcript = []; self.selected = nil; self.timings = [:]; self.phase = "idle"
                call.resolve()
            } catch { call.reject("Uložený přístup nebyl smazán.", "profile_remove_failed") }
        }
    }

    @objc func inspectBackend(_ call: CAPPluginCall) { start(call, expectedMode: nil) }
    @objc func inspectServer(_ call: CAPPluginCall) { serverAction(call, change: false) }
    @objc func changeServerModes(_ call: CAPPluginCall) { serverAction(call, change: true) }
    @objc func inspectEvenings(_ call: CAPPluginCall) { eveningAction(call, action: "list") }
    @objc func createEvening(_ call: CAPPluginCall) { eveningAction(call, action: "create") }
    @objc func activateEvening(_ call: CAPPluginCall) { eveningAction(call, action: "activate") }

    private func eveningAction(_ call: CAPPluginCall, action: String) {
        DispatchQueue.main.async {
            self.restoreProfile()
            guard self.canStart(), let profile = self.profile else { call.reject("Nejprve nastavte přístup a ukončete rozhovor.", "unavailable"); return }
            self.generation += 1
            let generation = self.generation
            self.evenings = nil; self.phase = "checking"
            self.task = Task { @MainActor in
                do {
                    let list = try await DebugBackendClient().eveningList(profile, action: action,
                        name: call.getString("name"), eveningId: call.getString("eveningId"), expectedEveningId: call.getString("expectedEveningId"))
                    try self.requireCurrent(generation)
                    self.evenings = list; self.task = nil; self.phase = "idle"
                    if action != "list" { self.transcript = []; self.selected = nil; self.timings = [:] }
                    call.resolve()
                } catch {
                    if self.generation == generation {
                        self.evenings = nil; self.task = nil; self.phase = error is CancellationError ? "cancelled" : "failed"
                    }
                    call.reject("Večer nebyl ověřen nebo změněn. Obnovte seznam; běžící rozhovor nebo souběžná změna přepnutí zablokuje.", "evening_failed")
                }
            }
        }
    }

    @objc func startConversation(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canStart(), let profile = self.profile else { call.reject("Nastavte backend a ukončete předchozí test.", "unavailable"); return }
            self.generation += 1
            let generation = self.generation
            self.transcript = []; self.selected = nil; self.timings = [:]; self.phase = "checking"
            self.lastError = nil
            self.task = Task { @MainActor in
                var owned: DebugDialogSession?
                var realtimeOwned: DebugRealtimeSession?
                do {
                    self.mode = call.getString("expectedMode") ?? "unknown"
                    let engine = call.getString("engine") ?? "chained"
                    if engine == "realtime" || engine == "gpt-live" {
                        guard self.mode == "openai" else { throw DebugBackendError.wrongMode }
                        try await self.runHandsFree(call, profile: profile, generation: generation)
                        return
                    } else if engine == "realtime-ptt" {
                        guard self.mode == "openai" else { throw DebugBackendError.wrongMode }
                        let session = try await DebugBackendClient().openRealtimeSession(profile,
                            readyMask: call.getInt("readyMask") ?? -1, voice: call.getString("voice") ?? "cedar",
                            speed: call.getDouble("speed") ?? 1.1)
                        realtimeOwned = session
                        try self.requireCurrent(generation)
                        self.realtimeSession = session
                        try await self.realtimeTurn(session, text: nil, wav: nil, generation: generation)
                    } else {
                        let session = try await DebugBackendClient().openSession(profile,
                            expectedMode: self.mode, readyMask: call.getInt("readyMask") ?? -1,
                            voice: call.getString("voice") ?? "cedar", speed: call.getDouble("speed") ?? 1.1)
                        owned = session
                        try self.requireCurrent(generation)
                        self.dialogSession = session
                        try await self.dialogTurn(session, text: nil, generation: generation)
                    }
                    if self.generation == generation {
                        self.task = nil
                        if realtimeOwned != nil && self.realtimePlayer == nil {
                            self.phase = self.realtimeSession == nil ? "completed" : "awaiting"
                        }
                    }
                    call.resolve()
                } catch {
                    if let owned = owned { try? await owned.close() }
                    if let realtimeOwned = realtimeOwned {
                        await realtimeOwned.cancel()
                        do {
                            try await realtimeOwned.close()
                        } catch {
                            // Keep the only owned session reference so Stop can
                            // retry its cleanup instead of silently orphaning it.
                            if self.generation == generation {
                                self.task = nil
                                self.realtimePlayer?.stop(); self.realtimePlayer = nil
                                self.phase = "failed"
                            }
                            call.reject("Realtime úklid nebyl potvrzen. Zastavte test a zkuste jej znovu.", "realtime_cleanup_failed")
                            return
                        }
                    }
                    if self.generation == generation {
                        if case DebugBackendError.serverCode(let code) = error { self.lastError = code }
                        self.failDialog()
                    }
                    call.reject("Rozhovor nebyl dokončen. Ověřte přístup, režim a limity serveru.", "dialog_failed")
                }
            }
        }
    }

    @MainActor private func runHandsFree(_ call: CAPPluginCall, profile: DebugBackendProfile, generation: Int) async throws {
        let permitted: Bool
        if #available(iOS 17.0, *) { permitted = await AVAudioApplication.requestRecordPermission() }
        else {
            permitted = await withCheckedContinuation { continuation in
                AVAudioSession.sharedInstance().requestRecordPermission { continuation.resume(returning: $0) }
            }
        }
        try requireCurrent(generation)
        guard permitted else { throw DebugBackendError.serverCode("microphone_unavailable") }
        let gptLive = call.getString("engine") == "gpt-live"
        let audio = DebugHandsFreeAudio(fullDuplex: gptLive, upload: { [weak self] input, sequence, pcm in
            guard let self = self, self.generation == generation, let session = self.handsFreeSession else {
                throw CancellationError()
            }
            try await session.upload(inputId: input, sequence: sequence, pcm: pcm)
        }, playbackDrained: { [weak self] operation in
            Task { @MainActor in
                guard let self = self, self.generation == generation, let session = self.handsFreeSession else { return }
                do { try await session.played(operationId: operation) }
                catch { self.lastError = DebugHandsFreeSession.failureCode(for: error); self.stopWork(); self.phase = "failed" }
            }
        }, failure: { [weak self] code in
            guard let self = self, self.generation == generation else { return }
            self.lastError = code; self.stopWork(); self.phase = "failed"
        })
        let session = try await DebugBackendClient().openHandsFreeSession(profile,
            readyMask: call.getInt("readyMask") ?? -1, voice: call.getString("voice") ?? "cedar",
            speed: gptLive ? 1 : (call.getDouble("speed") ?? 1.1), gptLive: gptLive,
            phase: { [weak self] value in if self?.generation == generation { self?.phase = value } },
            visitor: { [weak self] value in
                if self?.generation == generation { self?.transcript.append(["role": "visitor", "text": value]) }
            },
            reply: { [weak self] value in
                guard let self = self, self.generation == generation else { return }
                self.transcript.append(["role": "kufr", "text": value.text])
                self.selected = value.selected; self.timings = value.timings
                audio.finishPlayback(operationId: value.operationId)
            },
            audio: { [weak self] pcm in
                guard self?.generation == generation else { throw CancellationError() }
                try audio.appendPlayback(pcm)
            },
            listening: { [weak self] input in
                if self?.generation == generation { audio.beginListening(inputId: input) }
            },
            inputGate: { enabled in if !enabled { audio.stopUploading() } },
            failure: { [weak self] code in if self?.generation == generation { self?.lastError = code } })
        var resolved = false
        do {
            try requireCurrent(generation)
            handsFreeSession = session
            handsFreeAudio = audio
            do { try await audio.start() }
            catch let error as DebugBackendError { throw error }
            catch is CancellationError { throw CancellationError() }
            catch { throw DebugBackendError.serverCode("realtime_live_audio_start_failed") }
            try requireCurrent(generation)
            // Resolve after the microphone is ready. The ongoing native task and
            // status polling own listening, generation and playback phases.
            call.resolve()
            resolved = true
            try await session.run()
            try await session.close()
            if self.generation == generation {
                audio.stop(); handsFreeAudio = nil; handsFreeSession = nil
                task = nil; phase = "completed"
            }
        } catch {
            if !resolved { call.reject("Mikrofon nebo Realtime rozhovor není dostupný.", "microphone_unavailable") }
            audio.stop()
            await session.stop()
            do { try await session.close() }
            catch {
                if self.generation == generation { task = nil; phase = "failed"; lastError = "realtime_cleanup_failed" }
                return
            }
            if self.generation == generation {
                if lastError == nil { lastError = DebugHandsFreeSession.failureCode(for: error) }
                handsFreeSession = nil; handsFreeAudio = nil; task = nil; phase = "failed"
            }
        }
    }

    @objc func answerConversation(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canAnswer(), (self.dialogSession != nil || self.realtimeSession != nil), let text = call.getString("text"),
                  !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, text.unicodeScalars.count <= 1000 else {
                call.reject("Počkejte na otázku a zadejte odpověď do 1000 znaků.", "unavailable"); return
            }
            if let session = self.dialogSession { self.submitAnswer(call, session: session, text: text, wav: nil) }
            else if let session = self.realtimeSession { self.submitRealtimeAnswer(call, session: session, text: text, wav: nil) }
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

    private func submitRealtimeAnswer(_ call: CAPPluginCall, session: DebugRealtimeSession, text: String?, wav: Data?) {
        let generation = self.generation
        self.task = Task { @MainActor in
            do {
                try await self.realtimeTurn(session, text: text, wav: wav, generation: generation)
                if self.generation == generation {
                    self.task = nil
                    if self.realtimePlayer == nil {
                        self.phase = self.realtimeSession == nil ? "completed" : "awaiting"
                    }
                }
                call.resolve()
            } catch {
                if case DebugBackendError.serverCode(let code) = error, self.generation == generation { self.lastError = code }
                await session.cancel()
                do {
                    try await session.close()
                } catch {
                    if self.generation == generation {
                        self.task = nil
                        self.realtimePlayer?.stop(); self.realtimePlayer = nil
                        self.phase = "failed"
                    }
                    call.reject("Realtime úklid nebyl potvrzen. Zastavte test a zkuste jej znovu.", "realtime_cleanup_failed")
                    return
                }
                if self.generation == generation { self.failDialog() }
                call.reject("Realtime odpověď nebyla dokončena. Rozhovor byl ukončen.", "realtime_failed")
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

    @MainActor private func realtimeTurn(_ session: DebugRealtimeSession, text: String?, wav: Data?, generation: Int) async throws {
        if let text = text {
            transcript.append(["role": "visitor", "text": text])
        }
        let player = DebugPCMPlayer { [weak self] in
            guard let self = self, self.generation == generation else { return }
            self.realtimePlayer = nil
            if self.realtimeSession == nil { self.phase = "completed" }
            else if self.task == nil { self.phase = "awaiting" }
            self.deactivateAudio()
        }
        realtimePlayer = player
        let result = try await session.advance(text: text, wav: wav, phase: { value in
            if self.generation == generation { self.phase = value }
        }, visitor: { value in
            if self.generation == generation { self.transcript.append(["role": "visitor", "text": value]) }
        }, audio: { pcm in
            guard self.generation == generation else { throw CancellationError() }
            try player.append(pcm)
        })
        player.finish()
        try requireCurrent(generation)
        transcript.append(["role": "kufr", "text": result.text])
        selected = result.selected
        timings = result.timings
        if result.terminal {
            try await session.close()
            try requireCurrent(generation)
            realtimeSession = nil
        }
        if realtimePlayer == nil { phase = result.terminal ? "completed" : "awaiting" }
    }

    @MainActor private func failDialog() {
        task = nil; dialogSession = nil; realtimeSession = nil; player?.stop(); player = nil
        realtimePlayer?.stop(); realtimePlayer = nil
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
                  let microphone = self.microphone, (self.dialogSession != nil || self.realtimeSession != nil) else {
                call.reject("Nahrávání není aktivní.", "unavailable"); return
            }
            self.recordingTimer?.invalidate(); self.recordingTimer = nil
            self.microphone = nil
            self.phase = self.realtimeSession == nil ? "transcription" : "turn"
            do {
                let wav = try microphone.finish()
                self.deactivateAudio()
                if let session = self.dialogSession { self.submitAnswer(call, session: session, text: nil, wav: wav) }
                else if let session = self.realtimeSession { self.submitRealtimeAnswer(call, session: session, text: nil, wav: wav) }
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
                  let json = call.getString("json"), json.utf8.count <= 262_144,
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
        canAnswer() && dialogSession == nil && realtimeSession == nil && handsFreeSession == nil
    }

    private func canAnswer() -> Bool {
        UIApplication.shared.applicationState == .active && task == nil && player == nil && realtimePlayer == nil &&
            !synthesizer.isSpeaking && configurationCall == nil && microphone == nil && cleanupTask == nil && handsFreeSession == nil
    }

    @MainActor private func stopWork() {
        logTask?.cancel()
        generation += 1
        let stoppedTask = task
        task?.cancel(); task = nil
        recordingTimer?.invalidate(); recordingTimer = nil
        microphone?.cancel(); microphone = nil
        transcript = []; selected = nil; timings = [:]
        let session = dialogSession
        let realtime = realtimeSession
        let handsFree = handsFreeSession
        handsFreeAudio?.stop(); handsFreeAudio = nil; handsFreeSession = nil
        dialogSession = nil
        realtimeSession = nil
        realtimePlayer?.stop(); realtimePlayer = nil
        if cleanupTask == nil && (session != nil || realtime != nil || handsFree != nil || stoppedTask != nil) {
            cleanupTask = Task { @MainActor in
                // A cancelled create can still be recovering its owned ID.
                // Do not admit another test until that recovery has finished.
                if let stoppedTask = stoppedTask { await stoppedTask.value }
                if let session = session {
                    do { try await session.close() }
                    catch { self.phase = "failed" }
                }
                if let realtime = realtime {
                    await realtime.cancel()
                    do { try await realtime.close() }
                    catch {
                        // Keep ownership after a failed DELETE so another Stop
                        // can retry cleanup with the same conversation identity.
                        self.realtimeSession = realtime
                        self.phase = "failed"
                    }
                }
                if let handsFree = handsFree {
                    await handsFree.stop()
                    do { try await handsFree.close() }
                    catch { self.handsFreeSession = handsFree; self.phase = "failed"; self.lastError = "realtime_cleanup_failed" }
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
