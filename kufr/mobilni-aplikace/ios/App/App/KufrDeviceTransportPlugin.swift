import Capacitor
import Foundation
import UIKit

@objc(KufrDeviceTransportPlugin)
public class KufrDeviceTransportPlugin: CAPPlugin, CAPBridgedPlugin {
    public let identifier = "KufrDeviceTransportPlugin"
    public let jsName = "KufrDeviceTransport"
    public let pluginMethods: [CAPPluginMethod] = [
        CAPPluginMethod(name: "savedDevice", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "pair", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "connect", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "send", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "close", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "forget", returnType: CAPPluginReturnPromise)
    ]
    private let keychain = LocalDeviceKeychain()
    private var transport: LocalDeviceTransport?
    private var connectionId: String?
    private var pairingCall: CAPPluginCall?
    private var lifecycleObservers: [NSObjectProtocol] = []

    public override func load() {
        for name in [UIApplication.didEnterBackgroundNotification,
                     UIApplication.protectedDataWillBecomeUnavailableNotification] {
            lifecycleObservers.append(NotificationCenter.default.addObserver(
                forName: name, object: nil, queue: .main
            ) { [weak self] _ in
                self?.transport?.close()
            })
        }
    }

    deinit {
        lifecycleObservers.forEach { NotificationCenter.default.removeObserver($0) }
    }

    @objc func savedDevice(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            do {
                if let identity = try self.keychain.read() {
                    call.resolve(["paired": true, "deviceId": identity.deviceId, "host": identity.host])
                } else {
                    call.resolve(["paired": false])
                }
            } catch {
                call.reject("Párování nelze přečíst. Odemkněte telefon nebo použijte servisní obnovu.",
                            "credential_unavailable")
            }
        }
    }

    @objc func pair(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.pairingCall == nil, self.transport == nil,
                  UIApplication.shared.applicationState == .active,
                  let viewController = self.bridge?.viewController,
                  viewController.presentedViewController == nil,
                  let host = call.getString("host"), LocalDeviceIdentity.validHost(host),
                  let deviceId = call.getString("deviceId"), LocalDeviceIdentity.validDeviceId(deviceId),
                  let pin = call.getString("spkiSha256"), LocalDeviceIdentity.validHex(pin, bytes: 32) else {
                call.reject("Zkontrolujte adresu a údaje z vnitřního QR kufru.", "invalid_identity")
                return
            }
            do {
                guard try self.keychain.read() == nil else {
                    call.reject("Telefon už má uložený kufr. Nejprve výslovně zapomeňte párování.",
                                "already_paired")
                    return
                }
            } catch {
                call.reject("Úložiště párování není dostupné.", "credential_unavailable")
                return
            }
            self.pairingCall = call
            let prompt = UIAlertController(
                title: "Spárovat kufr " + deviceId,
                message: "Zadejte jednorázové tajemství z QR uvnitř kufru. " +
                    "Zůstane v nativním připojení telefonu.", preferredStyle: .alert)
            prompt.addTextField {
                $0.isSecureTextEntry = true
                $0.autocapitalizationType = .none
                $0.autocorrectionType = .no
                $0.spellCheckingType = .no
                $0.placeholder = "Tajemství (malé hex znaky)"
            }
            prompt.addAction(UIAlertAction(title: "Zrušit", style: .cancel) { _ in
                prompt.textFields?.first?.text = nil
                self.pairingCall = nil
                call.reject("Párování bylo zrušeno.", "cancelled")
            })
            prompt.addAction(UIAlertAction(title: "Spárovat", style: .default) { _ in
                let secret = prompt.textFields?.first?.text ?? ""
                prompt.textFields?.first?.text = nil
                guard UIApplication.shared.applicationState == .active else {
                    self.pairingCall = nil
                    call.reject("Párování vyžaduje otevřenou aplikaci.", "inactive")
                    return
                }
                let transport = LocalDeviceTransport(host: host, pin: pin)
                self.transport = transport
                transport.pair(secret: secret) { result in
                    self.transport = nil
                    self.pairingCall = nil
                    switch result {
                    case .success(let token):
                        do {
                            try self.keychain.write(LocalDeviceIdentity(
                                deviceId: deviceId, host: host, spkiSha256: pin, token: token))
                            call.resolve(["deviceId": deviceId, "host": host])
                        } catch {
                            call.reject("Token se nepodařilo uložit. Párování může být spotřebované; " +
                                        "je nutná fyzická servisní obnova kufru.", "credential_write_failed")
                        }
                    case .failure:
                        call.reject("Párování selhalo. Ověřte LAN, pin a tajemství. " +
                                    "Při ztrátě odpovědi může být nutná fyzická servisní obnova.", "pairing_failed")
                    }
                }
            })
            viewController.present(prompt, animated: true)
        }
    }

    @objc func connect(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.transport == nil, self.pairingCall == nil,
                  UIApplication.shared.applicationState == .active,
                  let id = call.getString("connectionId"), UUID(uuidString: id) != nil else {
                call.reject("Spojení nyní není dostupné.", "connection_unavailable")
                return
            }
            do {
                guard let identity = try self.keychain.read() else {
                    call.reject("Nejprve spárujte kufr.", "not_paired")
                    return
                }
                let transport = LocalDeviceTransport(host: identity.host, pin: identity.spkiSha256)
                self.transport = transport
                self.connectionId = id
                transport.onOpen = { [weak self] in
                    guard let self, self.connectionId == id else { return }
                    self.notifyListeners("transport", data: ["connectionId": id, "state": "open"])
                }
                transport.onFrame = { [weak self] frame in
                    guard let self, self.connectionId == id else { return }
                    self.notifyListeners("transport", data: ["connectionId": id, "state": "frame", "frame": frame])
                }
                transport.onClose = { [weak self] in
                    guard let self, self.connectionId == id else { return }
                    self.transport = nil
                    self.connectionId = nil
                    self.notifyListeners("transport", data: ["connectionId": id, "state": "closed"])
                }
                transport.connect(token: identity.token)
                call.resolve()
            } catch {
                call.reject("Párování nelze přečíst. Odemkněte telefon.", "credential_unavailable")
            }
        }
    }

    @objc func send(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let id = call.getString("connectionId"), self.connectionId == id,
                  let frame = call.getString("frame"), let transport = self.transport else {
                call.reject("Spojení se změnilo; povel nebyl odeslán.", "wrong_connection")
                return
            }
            transport.send(frame) { sent in
                if sent { call.resolve() }
                else { call.reject("Povel se nepodařilo odeslat; jeho výsledek může být nejistý.", "send_failed") }
            }
        }
    }

    @objc func close(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            if call.getString("connectionId") == self.connectionId {
                self.transport?.close()
            }
            call.resolve()
        }
    }

    @objc func forget(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.pairingCall == nil,
                  UIApplication.shared.applicationState == .active,
                  let viewController = self.bridge?.viewController,
                  viewController.presentedViewController == nil else {
                call.reject("Obnova párování nyní není dostupná.", "inactive")
                return
            }
            let prompt = UIAlertController(title: "Zapomenout párování?",
                message: "Nové párování bude vyžadovat fyzický servisní reset kufru. " +
                    "Uložený token už nepůjde získat zpět.", preferredStyle: .alert)
            prompt.addAction(UIAlertAction(title: "Zrušit", style: .cancel) { _ in
                call.reject("Párování zůstalo uložené.", "cancelled")
            })
            prompt.addAction(UIAlertAction(title: "Zapomenout", style: .destructive) { _ in
                self.transport?.close()
                do { try self.keychain.remove(); call.resolve() }
                catch { call.reject("Párování nelze odstranit.", "credential_write_failed") }
            })
            viewController.present(prompt, animated: true)
        }
    }
}
