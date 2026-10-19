import Capacitor
import UIKit
import AVFoundation

// The system camera supplies preview, retake, Use Photo and Cancel. It never
// writes to Photos. Only the normalized bounded JPEG is retained in memory.
@MainActor
private final class DebugCostumeCamera: NSObject, UIImagePickerControllerDelegate, UINavigationControllerDelegate {
    let picker = UIImagePickerController()
    private var completed: ((Data?) -> Void)?
    private var dismissing = false
    private var dismissalWaiters: [() -> Void] = []

    init(completed: @escaping (Data?) -> Void) {
        self.completed = completed
        super.init()
        picker.sourceType = .camera
        picker.cameraCaptureMode = .photo
        picker.modalPresentationStyle = .fullScreen
        picker.delegate = self
    }

    func cancel(afterDismiss: @escaping () -> Void = {}) { finish(nil, animated: false, afterDismiss: afterDismiss) }

    private func finish(_ data: Data?, animated: Bool = true, afterDismiss: @escaping () -> Void = {}) {
        guard let completed else {
            if dismissing { dismissalWaiters.append(afterDismiss) } else { afterDismiss() }
            return
        }
        self.completed = nil
        picker.delegate = nil
        // Resolve after dismissal so the next bridge operation can present safely.
        guard picker.presentingViewController != nil else { completed(data); afterDismiss(); return }
        dismissing = true
        picker.dismiss(animated: animated) {
            self.dismissing = false
            completed(data)
            afterDismiss()
            let waiters = self.dismissalWaiters
            self.dismissalWaiters.removeAll()
            waiters.forEach { $0() }
        }
    }

    func imagePickerControllerDidCancel(_ picker: UIImagePickerController) {
        finish(nil)
    }

    func imagePickerController(_ picker: UIImagePickerController,
                               didFinishPickingMediaWithInfo info: [UIImagePickerController.InfoKey: Any]) {
        guard let image = info[.originalImage] as? UIImage, image.size.width > 0, image.size.height > 0 else {
            finish(nil); return
        }
        let ratio = min(1, 640 / max(image.size.width, image.size.height))
        let size = CGSize(width: max(1, floor(image.size.width * ratio)), height: max(1, floor(image.size.height * ratio)))
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1; format.opaque = true
        let redrawn = UIGraphicsImageRenderer(size: size, format: format).image { context in
            UIColor.white.setFill(); context.fill(CGRect(origin: .zero, size: size))
            image.draw(in: CGRect(origin: .zero, size: size))
        }
        let qualities: [CGFloat] = [0.8, 0.65, 0.5, 0.35]
        for quality in qualities {
            if let jpeg = redrawn.jpegData(compressionQuality: quality),
               let clean = try? DebugCostumeJPEG.withoutMetadata(jpeg) {
                finish(clean); return
            }
        }
        finish(nil)
    }
}

@objc(KufrDebugPlugin)
public class KufrDebugPlugin: CAPPlugin, CAPBridgedPlugin, AVSpeechSynthesizerDelegate {
    public let identifier = "KufrDebugPlugin"
    public let jsName = "KufrDebug"
    public let pluginMethods: [CAPPluginMethod] = ["status", "configure", "forgetProfile", "inspectEvenings", "createEvening", "activateEvening", "inspectBackend", "inspectServer", "changeServerModes", "startConversation", "speakSystem", "stop", "shareLog", "readServerLogs", "captureCostumePhoto", "discardCostumePhoto", "attachCostumePhoto", "inspectMemory", "deleteConversationMemory", "showRequest"].map {
        CAPPluginMethod(name: $0, returnType: CAPPluginReturnPromise)
    }
    private var profile: DebugBackendProfile?
    private var logTask: Task<Void, Never>?
    private let profileKeychain = DebugBackendKeychain()

    // Director uses the existing private profile. Neither its credential nor a
    // destination URL can be supplied by web content to this bounded bridge.
    @objc func showRequest(_ call: CAPPluginCall) {
        guard let deviceId = call.getString("deviceId"),
              deviceId.range(of: "^[A-Za-z0-9_-]{1,128}$", options: .regularExpression) != nil,
              let suffix = call.getString("suffix"), suffix.utf8.count <= 1024,
              let method = call.getString("method"), ["GET", "POST"].contains(method),
              let profile = try? profileKeychain.read() else {
            call.reject("Neplatný nebo chybějící operátorský profil.", "invalid_profile"); return
        }
        let allowedGet = "^(|/(identities|prophecies|duels)(\\?eveningId=[A-Za-z0-9%_-]+)?|/duels/[A-Za-z0-9_-]+)$"
        let allowedPost = "^/(commands|end-evening|offline-outcomes|prophecies/[A-Za-z0-9_-]+/reveal|duels|duels/[A-Za-z0-9_-]+/(start|cancel|grants|grants/revoke))$"
        guard suffix.range(of: method == "GET" ? allowedGet : allowedPost, options: .regularExpression) != nil else {
            call.reject("Nepovolená cesta režie.", "invalid_path"); return
        }
        let body = call.getString("body")
        guard (method == "GET" && body == nil) ||
              (method == "POST" && body != nil && body!.utf8.count <= 65536 &&
               (try? JSONSerialization.jsonObject(with: Data(body!.utf8))) is [String: Any]) else {
            call.reject("Neplatné tělo požadavku.", "invalid_body"); return
        }
        guard let url = URL(string: "/v1/show/devices/\(deviceId)\(suffix)", relativeTo: profile.baseURL)?.absoluteURL,
              url.host == profile.baseURL.host, url.scheme == profile.baseURL.scheme,
              url.port == profile.baseURL.port else {
            call.reject("Neplatný cíl režie.", "invalid_path"); return
        }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("Bearer \(profile.credential)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let body = body {
            request.httpBody = Data(body.utf8)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        Task {
            do {
                let transport = DebugHTTPTransport()
                defer { transport.invalidate() }
                let (data, response) = try await transport.request(request, limit: 1048576)
                let object: Any = data.isEmpty ? NSNull() : try JSONSerialization.jsonObject(with: data)
                call.resolve(["status": response.statusCode, "body": object])
            } catch {
                // Never include URLRequest, headers or response bodies in errors.
                call.reject("Server neodpověděl; výsledek povelu není ověřený.", "show_request_failed")
            }
        }
    }
    private var evenings: DebugEveningList?
    private var task: Task<Void, Never>?
    private let synthesizer = AVSpeechSynthesizer()
    private var systemUtterance: AVSpeechUtterance?
    private var phase = "idle"
    private var mode = "unknown"
    private var serverModes: DebugServerModes?
    private var generation = 0
    private var observers: [NSObjectProtocol] = []
    private var configurationCall: CAPPluginCall?
    private weak var configurationAlert: UIAlertController?
    private var transcript: [[String: String]] = []
    private var selected: Int?
    private var timings: [String: Double] = [:]
    private var handsFreeSession: DebugHandsFreeSession?
    private var handsFreeAudio: DebugHandsFreeAudio?
    private var lastError: String?
    private var cleanupTask: Task<Void, Never>?
    private var conversationClient: DebugBackendClient?
    private var camera: DebugCostumeCamera?
    private var cameraCall: CAPPluginCall?
    private var cameraRequest = UUID()
    private var cameraDismissPending = false
    private var imageTask: Task<Void, Never>?
    private var imageRequest = UUID()
    private var imageRevision = 0
    private var effectiveEngine: String?
    private var lastConversationId: String?
    private var photoDraft: (id: String, jpeg: Data, capturedAt: Date, expiry: Date)?
    private var photoTimer: Timer?
    private var costumeStatus: String?

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
                DispatchQueue.main.async {
                    guard let self else { return }
                    self.stopWork()
                    self.lastError = "realtime_live_audio_interrupted"
                    self.phase = "failed"
                }
            }
        })
    }

    deinit {
        logTask?.cancel()
        task?.cancel()
        imageTask?.cancel()
        observers.forEach { NotificationCenter.default.removeObserver($0) }
    }

    @objc func status(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            self.restoreProfile()
            call.resolve(["configured": self.profile != nil, "running": self.task != nil || self.synthesizer.isSpeaking || self.cleanupTask != nil || self.conversationClient?.hasPendingCleanup == true,
                          "phase": self.phase, "mode": self.mode,
                          "cameraActive": self.cameraCall != nil || self.cameraDismissPending, "imagePending": self.imageTask != nil,
                          "effectiveEngine": (self.effectiveEngine as Any?) ?? NSNull(),
                          "effectiveModel": (self.effectiveEngine.map { $0 == "gpt-live" ? "gpt-live-1" : "gpt-realtime-2.1" } as Any?) ?? NSNull(),
                          "sessionGeneration": self.generation,
                          "lastConversationId": (self.lastConversationId as Any?) ?? NSNull(),
                          "conversationId": (self.handsFreeSession?.conversationId as Any?) ?? NSNull(),
                          "inputActive": self.handsFreeAudio?.isInputActive == true,
                          "outputActive": self.handsFreeAudio?.isOutputActive == true,
                          "nativeVersion": Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "unknown",
                          "session": self.handsFreeSession != nil,
                          "recording": self.handsFreeAudio?.isInputActive == true, "handsFree": self.handsFreeSession != nil,
                          "errorCode": (self.lastError as Any?) ?? NSNull(),
                          "costumeStatus": self.conversationClient?.hasPendingCleanup == true
                            ? "Server nepotvrdil úklid rozhovoru. Použijte znovu Stop; nový rozhovor je zablokovaný."
                            : (self.costumeStatus as Any?) ?? NSNull(),
                          "audioOutput": (self.handsFreeAudio?.outputStatus as Any?) ?? NSNull(),
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
                    self.generation += 1
                    self.discardPhotoDraft(); self.lastConversationId = nil; self.effectiveEngine = nil
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
                self.generation += 1
                self.discardPhotoDraft(); self.lastConversationId = nil; self.effectiveEngine = nil
                self.transcript = []; self.selected = nil; self.timings = [:]; self.phase = "idle"
                call.resolve()
            } catch { call.reject("Uložený přístup nebyl smazán.", "profile_remove_failed") }
        }
    }

    @objc func inspectBackend(_ call: CAPPluginCall) { start(call) }
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

    @objc func captureCostumePhoto(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard UIApplication.shared.applicationState == .active, self.cameraCall == nil, !self.cameraDismissPending,
                  self.cleanupTask == nil, self.configurationCall == nil,
                  let presenter = self.bridge?.viewController, presenter.viewIfLoaded?.window != nil,
                  presenter.presentedViewController == nil, UIImagePickerController.isSourceTypeAvailable(.camera) else {
                call.reject("Fotoaparát není dostupný. Můžete pokračovat bez fotky.", "camera_unavailable"); return
            }
            self.discardPhotoDraft()
            self.cameraCall = call
            let request = UUID()
            self.cameraRequest = request
            let generation = self.generation
            Task { @MainActor in
                let permitted = await AVCaptureDevice.requestAccess(for: .video)
                // Permission resolution can precede UIKit becoming active.
                for _ in 0..<20 where permitted && UIApplication.shared.applicationState != .active {
                    try? await Task.sleep(nanoseconds: 100_000_000)
                    guard self.cameraRequest == request else { return }
                }
                guard self.generation == generation, self.cameraRequest == request, self.cameraCall === call,
                    UIApplication.shared.applicationState == .active else {
                    if self.cameraCall === call { self.cameraCall = nil; call.resolve(["draftId": NSNull()]) }
                    return
                }
                guard permitted, let controller = self.bridge?.viewController, controller.viewIfLoaded?.window != nil, controller.presentedViewController == nil else {
                    self.cameraCall = nil
                    call.reject("Fotoaparát nebyl povolen. Rozhovor může pokračovat bez fotky.", "camera_unavailable"); return
                }
                let camera = DebugCostumeCamera { [weak self] data in
                    guard let self = self, self.generation == generation, self.cameraRequest == request, self.cameraCall === call else { return }
                    self.camera = nil; self.cameraCall = nil
                    guard let data = data else { call.resolve(["draftId": NSNull()]); return }
                    let id = UUID().uuidString.lowercased()
                    self.photoDraft = (id, data, Date(), Date().addingTimeInterval(600))
                    self.photoTimer = Timer.scheduledTimer(withTimeInterval: 600, repeats: false) { [weak self] _ in
                        DispatchQueue.main.async { self?.discardPhotoDraft() }
                    }
                    call.resolve(["draftId": id])
                }
                self.camera = camera
                controller.present(camera.picker, animated: true)
            }
        }
    }

    @objc func discardCostumePhoto(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            self.cancelCamera(); self.discardPhotoDraft()
            self.imageRequest = UUID()
            self.imageTask?.cancel(); self.imageTask = nil
            guard let session = self.handsFreeSession else { call.resolve(); return }
            let revision = self.imageRevision
            Task { @MainActor in
                do {
                    try await session.revokeImage(revision: revision)
                    if self.handsFreeSession === session { self.costumeStatus = "Fotka byla zrušena. Rozhovor pokračuje bez fotky." }
                    call.resolve()
                }
                catch { call.reject("Zrušení fotky server nepotvrdil. Zkuste znovu; rozhovor pokračuje.", "image_cancel_failed") }
            }
        }
    }

    @MainActor private func cancelCamera() {
        cameraRequest = UUID()
        let pending = cameraCall
        cameraCall = nil
        guard let camera else { pending?.resolve(["draftId": NSNull()]); return }
        self.camera = nil
        cameraDismissPending = true
        camera.cancel { [weak self] in
            self?.cameraDismissPending = false
            pending?.resolve(["draftId": NSNull()])
        }
    }

    @objc func attachCostumePhoto(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let session = self.handsFreeSession, self.imageTask == nil,
                  let draft = self.photoDraft, draft.id == call.getString("draftId"), draft.expiry > Date() else {
                call.reject("Fotka už není dostupná. Pořiďte novou fotku.", "image_unavailable"); return
            }
            let generation = self.generation
            self.imageRevision += 1
            let revision = self.imageRevision
            let imageRequest = UUID()
            self.imageRequest = imageRequest
            self.costumeStatus = "Nahrávám a vyhodnocuji fotku…"
            self.imageTask = Task { @MainActor in
                do {
                    let imageId = try await session.attachPhoto(draft.jpeg, capturedAt: draft.capturedAt, revision: revision)
                    try self.requireCurrent(generation)
                    guard self.handsFreeSession === session, self.imageRequest == imageRequest else { throw CancellationError() }
                    self.costumeStatus = "Fotka byla vyhodnocena pro tento rozhovor."
                    self.discardPhotoDraft()
                    self.imageTask = nil
                    call.resolve(["status": "analysed", "imageId": imageId, "revision": revision])
                } catch {
                    if self.generation == generation, self.imageRequest == imageRequest {
                        self.imageTask = nil
                        self.costumeStatus = "Fotku se nepodařilo zpracovat. Rozhovor pokračuje; fotku můžete zkusit znovu."
                    }
                    call.reject("Fotku se nepodařilo zpracovat. Rozhovor může pokračovat.", "image_failed")
                }
            }
        }
    }

    @MainActor private func discardPhotoDraft() {
        photoDraft = nil
        photoTimer?.invalidate(); photoTimer = nil
    }

    @objc func startConversation(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canStart(), let profile = self.profile else {
                self.discardPhotoDraft()
                call.reject("Nastavte backend a ukončete předchozí test.", "unavailable"); return
            }
            self.generation += 1
            let generation = self.generation
            self.transcript = []; self.selected = nil; self.timings = [:]; self.phase = "checking"
            self.lastError = nil
            self.costumeStatus = nil
            var jpeg: Data?
            var capturedAt = Date()
            if let id = call.getString("photoDraftId") {
                if let draft = self.photoDraft, draft.id == id, draft.expiry > Date() {
                    jpeg = draft.jpeg; capturedAt = draft.capturedAt
                }
                else { self.costumeStatus = "Fotka vypršela nebo byla zrušena. Pokračuji bez fotky." }
            }
            if call.getString("photoDraftId") != nil { self.discardPhotoDraft() }
            let description = call.getString("costumeDescription")?.trimmingCharacters(in: .whitespacesAndNewlines)
            let costume = DebugCostumeInput(jpeg: jpeg, description: description?.isEmpty == false ? description : nil,
                capturedAt: capturedAt)
            let client = DebugBackendClient()
            self.conversationClient = client
            self.task = Task { @MainActor in
                do {
                    self.mode = "openai"
                    guard let engine = call.getString("engine"), engine == "gpt-live" || engine == "realtime" else {
                        throw DebugBackendError.serverCode("native_upgrade_required")
                    }
                    try await self.runHandsFree(call, profile: profile, generation: generation, costume: costume, client: client)
                } catch {
                    if self.generation == generation {
                        self.lastError = DebugHandsFreeSession.failureCode(for: error)
                        self.failDialog()
                    }
                    call.reject("Rozhovor nebyl dokončen. Ověřte přístup a dostupnost vybraného modelu.", "dialog_failed")
                }
            }
        }
    }

    @MainActor private func runHandsFree(_ call: CAPPluginCall, profile: DebugBackendProfile, generation: Int,
                                       costume: DebugCostumeInput, client: DebugBackendClient) async throws {
        let permitted: Bool
        if #available(iOS 17.0, *) { permitted = await AVAudioApplication.requestRecordPermission() }
        else {
            permitted = await withCheckedContinuation { continuation in
                AVAudioSession.sharedInstance().requestRecordPermission { continuation.resume(returning: $0) }
            }
        }
        // Permission callbacks may precede UIApplication becoming active.
        for _ in 0..<20 where permitted && UIApplication.shared.applicationState == .inactive {
            try await Task.sleep(nanoseconds: 100_000_000)
            guard self.generation == generation else { throw CancellationError() }
        }
        try requireCurrent(generation)
        guard permitted else { throw DebugBackendError.serverCode("microphone_unavailable") }
        let gptLive = call.getString("engine") == "gpt-live"
        effectiveEngine = gptLive ? "gpt-live" : "realtime"
        let audio = DebugHandsFreeAudio(fullDuplex: gptLive, upload: { [weak self] input, sequence, pcm in
            guard let self = self, self.generation == generation, let session = self.handsFreeSession else {
                throw CancellationError()
            }
            try await session.upload(inputId: input, sequence: sequence, pcm: pcm)
        }, playbackDrained: { [weak self] operation, playbackGeneration in
            Task { @MainActor in
                guard let self = self, self.generation == generation, let session = self.handsFreeSession,
                      self.handsFreeAudio?.isCurrentPlayback(playbackGeneration) == true else { return }
                do { try await session.played(operationId: operation) }
                catch { self.lastError = DebugHandsFreeSession.failureCode(for: error); self.stopWork(); self.phase = "failed" }
            }
        }, failure: { [weak self] code in
            guard let self = self, self.generation == generation else { return }
            self.lastError = code; self.stopWork(); self.phase = "failed"
        })
        var guestMemory: DebugGuestMemoryBinding?
        if let guest = call.getObject("guestMemory") {
            guard let id = guest["guestId"] as? String, let verified = guest["identityVerified"] as? Bool,
                  let longTerm = guest["longTermAllowed"] as? Bool, let cloud = guest["cloudExportAllowed"] as? Bool else {
                throw DebugBackendError.serverCode("guest_identity_not_verified")
            }
            guestMemory = DebugGuestMemoryBinding(guestId: id, identityVerified: verified,
                longTermAllowed: longTerm, cloudExportAllowed: cloud)
        }
        let session = try await client.openHandsFreeSession(profile,
            readyMask: call.getInt("readyMask") ?? -1, voice: call.getString("voice") ?? "cedar",
            speed: gptLive ? 1 : (call.getDouble("speed") ?? 1.1), gptLive: gptLive,
            costume: costume, guestMemory: guestMemory, costumeStatus: { if self.generation == generation { self.costumeStatus = $0 } },
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
            failure: { [weak self] code in if self?.generation == generation { self?.lastError = code } },
            checkpoint: { [weak self] id in
                if self?.generation == generation { audio.finishPlayback(operationId: id) }
            }, cancelAudio: { [weak self] in
                if self?.generation == generation { audio.cancelPlayback() }
            })
        var resolved = false
        do {
            try requireCurrent(generation)
            handsFreeSession = session
            lastConversationId = session.conversationId
            handsFreeAudio = audio
            do { try await audio.start() }
            catch let error as DebugBackendError { throw error }
            catch is CancellationError { throw CancellationError() }
            catch { throw DebugBackendError.serverCode("realtime_live_audio_start_failed") }
            try requireCurrent(generation)
            if costume.jpeg != nil || costume.description != nil {
                imageRevision = 1
                let imageRequest = UUID()
                self.imageRequest = imageRequest
                imageTask = Task { @MainActor in
                    do {
                        try await session.attachVisual(costume) { [weak self] value in
                            guard let self, self.generation == generation, self.handsFreeSession === session, self.imageRequest == imageRequest else { return }
                            self.costumeStatus = value
                        }
                    } catch {
                        if self.generation == generation, self.handsFreeSession === session, self.imageRequest == imageRequest {
                            self.costumeStatus = "Fotku se nepodařilo zpracovat. Rozhovor pokračuje bez fotky."
                        }
                    }
                    if self.generation == generation, self.handsFreeSession === session, self.imageRequest == imageRequest { self.imageTask = nil }
                }
            }
            // Resolve after the microphone is ready. The ongoing native task and
            // status polling own listening, generation and playback phases.
            call.resolve()
            resolved = true
            try await session.run()
            try await session.close()
            if self.generation == generation {
                imageTask?.cancel(); imageTask = nil
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

    @objc func inspectMemory(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let profile = self.profile, let conversation = call.getString("conversationId"),
                  UUID(uuidString: conversation) != nil else { call.reject("Vyberte rozhovor.", "memory_unavailable"); return }
            let generation = self.generation
            Task { @MainActor in
                do {
                    let client = DebugBackendClient()
                    let modes: DebugServerModes
                    if let current = self.serverModes { modes = current }
                    else { modes = try await client.serverModes(profile) }
                    guard let personality = modes.personalityId else { throw DebugBackendError.invalidResponse }
                    let value = try await client.inspectMemory(profile, conversation: conversation, personality: personality)
                    guard self.generation == generation else { throw CancellationError() }
                    call.resolve(value)
                } catch { call.reject("Paměť není dostupná. Ověřte oprávnění správce a obnovte stav.", "memory_inspection_failed") }
            }
        }
    }

    @objc func deleteConversationMemory(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let profile = self.profile, let conversation = call.getString("conversationId"),
                  UUID(uuidString: conversation) != nil, let epoch = call.getInt("expectedEpoch"), epoch >= 0 else {
                call.reject("Nejprve obnovte paměť rozhovoru.", "memory_unavailable"); return
            }
            if self.handsFreeSession?.conversationId == conversation { self.stopWork() }
            Task { @MainActor in
                do {
                    let value = try await DebugBackendClient().deleteConversationMemory(profile, conversation: conversation, expectedEpoch: epoch)
                    call.resolve(value)
                } catch { call.reject("Smazání nebylo potvrzeno. Obnovte paměť a oprávnění správce.", "memory_deletion_failed") }
            }
        }
    }

    @MainActor private func requireCurrent(_ generation: Int) throws {
        try Task.checkCancellation()
        guard self.generation == generation, UIApplication.shared.applicationState == .active else { throw CancellationError() }
    }

    @MainActor private func failDialog() {
        task = nil
        phase = "failed"
        deactivateAudio()
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
                        personalityId: call.getString("personalityId"), characterEnabled: call.getBool("characterEnabled"),
                        characterTone: call.getString("characterTone"), characterFinaleProfile: call.getString("characterFinaleProfile"),
                        resetCharacterVariety: call.getBool("resetCharacterVariety") ?? false)
                    try Task.checkCancellation()
                    guard self.generation == generation, UIApplication.shared.applicationState == .active else { throw CancellationError() }
                    self.serverModes = modes; self.mode = "openai"
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
    private func start(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.canStart(), let profile = self.profile else { call.reject("Nejdříve nastavte backend.", "unavailable"); return }
            self.generation += 1
            let generation = self.generation
            self.phase = "checking"
            let client = DebugBackendClient()
            self.task = Task { @MainActor in
                do {
                    let mode = try await client.inspect(profile)
                    try self.requireCurrent(generation)
                    self.mode = mode; self.phase = "idle"; self.task = nil
                    call.resolve()
                } catch {
                    if self.generation == generation { self.task = nil; self.phase = "failed"; self.mode = "unknown" }
                    call.reject("Backend nebyl ověřen. Zkontrolujte přístup a verzi aplikace.", "debug_test_failed")
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
                self.systemUtterance = utterance
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

    @MainActor private func canStart() -> Bool {
        UIApplication.shared.applicationState == .active && task == nil && !synthesizer.isSpeaking &&
            configurationCall == nil && cameraCall == nil && !cameraDismissPending && cleanupTask == nil &&
            handsFreeSession == nil && conversationClient?.hasPendingCleanup != true
    }

    @MainActor private func stopWork() {
        logTask?.cancel()
        generation += 1
        discardPhotoDraft()
        costumeStatus = nil
        cancelCamera()
        imageTask?.cancel(); imageTask = nil
        let stoppedTask = task
        task?.cancel(); task = nil
        transcript = []; selected = nil; timings = [:]
        let handsFree = handsFreeSession
        let client = conversationClient
        handsFreeAudio?.stop(); handsFreeAudio = nil; handsFreeSession = nil
        if cleanupTask == nil && (handsFree != nil || stoppedTask != nil || client?.hasPendingCleanup == true) {
            cleanupTask = Task { @MainActor in
                // A cancelled create may still own an ID recovered by its client.
                if let stoppedTask { await stoppedTask.value }
                if let client {
                    do { try await client.retryPendingCleanup() }
                    catch { self.phase = "failed"; self.lastError = "realtime_cleanup_failed" }
                }
                if let handsFree {
                    await handsFree.stop()
                    do { try await handsFree.close() }
                    catch { self.handsFreeSession = handsFree; self.phase = "failed"; self.lastError = "realtime_cleanup_failed" }
                }
                if let client, !client.hasPendingCleanup, self.conversationClient === client {
                    self.conversationClient = nil
                    if self.lastError == "realtime_cleanup_failed", self.handsFreeSession == nil {
                        self.lastError = nil
                        if self.phase == "failed" { self.phase = "cancelled" }
                    }
                }
                self.cleanupTask = nil
            }
        }
        systemUtterance = nil
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

    public func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        DispatchQueue.main.async {
            guard self.systemUtterance === utterance else { return }
            self.systemUtterance = nil
            if self.phase == "playing" { self.phase = "completed"; self.deactivateAudio() }
        }
    }
}
