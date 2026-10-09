import Capacitor
import CoreBluetooth
import ESPProvision
import Foundation
import UIKit

// ESPProvision owns CoreBluetooth and its official Security1 handshake. Every
// custom endpoint payload is passed through the SDK's authenticated encryption.
@objc(KufrProvisioningPlugin)
public class KufrProvisioningPlugin: CAPPlugin, CAPBridgedPlugin, ESPDeviceConnectionDelegate {
    public let identifier = "KufrProvisioningPlugin"
    public let jsName = "KufrProvisioning"
    public let pluginMethods: [CAPPluginMethod] = [
        CAPPluginMethod(name: "discover", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "connect", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "status", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "configure", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "command", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "disconnect", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "openSettings", returnType: CAPPluginReturnPromise)
    ]
    private let service = CBUUID(string: "b97a7f10-70e8-4b98-9dcc-40d66395c821")
    private var devices: [String: ESPDevice] = [:]
    private var device: ESPDevice?
    private var pending: CAPPluginCall?
    private var epoch = UUID()
    private var timeout: DispatchWorkItem?
    private var prompt: UIAlertController?
    private var lifecycleObservers: [NSObjectProtocol] = []
    private var bleObserver: ProvisioningBLEObserver?
    private var connectionStarted = false

    public override func load() {
        // Upstream debug logs include PoP and Wi-Fi secrets; disable before any SDK use.
        ESPProvisionManager.shared.enableLogs(false)
        for name in [UIApplication.didEnterBackgroundNotification, UIApplication.protectedDataWillBecomeUnavailableNotification] {
            lifecycleObservers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                self?.close(code: "disconnected")
            })
        }
    }

    deinit { lifecycleObservers.forEach { NotificationCenter.default.removeObserver($0) } }

    @objc func discover(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.begin(call, seconds: 12) else { return }
            self.releaseDevice()
            self.devices.removeAll()
            let epoch = self.epoch
            ESPProvisionManager.shared.searchESPDevices(devicePrefix: "KUFR_", transport: .ble, security: .secure) { devices, error in
                DispatchQueue.main.async {
                    guard self.epoch == epoch, self.pending === call else { return }
                    if let error {
                        switch error {
                        case .espDeviceNotFound:
                            self.finish(["devices": []])
                        default:
                            self.fail("bluetooth_unavailable")
                        }
                        return
                    }
                    var seen = Set<String>()
                    for device in devices ?? [] {
                        let advertised = device.advertisementData?[CBAdvertisementDataServiceUUIDsKey] as? [CBUUID] ?? []
                        guard advertised.contains(self.service), device.name.range(of: "^KUFR_[A-Fa-f0-9]{6}$", options: .regularExpression) != nil else { continue }
                        // Duplicate names are ambiguous: do not allow selection by an alias.
                        if !seen.insert(device.name).inserted { self.devices.removeValue(forKey: device.name); continue }
                        if self.devices.count < 16 { self.devices[device.name] = device }
                    }
                    self.finish(["devices": self.devices.keys.sorted().map { ["name": $0] }])
                }
            }
        }
    }

    @objc func connect(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let name = call.getString("name"), let selected = self.devices[name] else { call.reject("Kufr znovu vyhledejte.", "provisioning_unavailable"); return }
            guard self.begin(call, seconds: 120), let viewController = self.bridge?.viewController else { return }
            self.releaseDevice()
            self.device = selected
            let epoch = self.epoch
            let prompt = UIAlertController(title: "Ověřit " + name, message: "Zadejte 32znakový PoP z vnitřního servisního štítku. Nejde o heslo Wi-Fi. Tajemství se neukládá.", preferredStyle: .alert)
            prompt.addTextField { field in
                field.placeholder = "Proof of possession (PoP)"
                field.isSecureTextEntry = true
                field.autocapitalizationType = .none
                field.autocorrectionType = .no
                field.textContentType = .oneTimeCode
            }
            prompt.addAction(UIAlertAction(title: "Zrušit", style: .cancel) { _ in
                prompt.textFields?.forEach { $0.text = nil }
                guard self.epoch == epoch else { return }
                self.close(code: "cancelled")
            })
            prompt.addAction(UIAlertAction(title: "Ověřit", style: .default) { _ in
                let pop = prompt.textFields?.first?.text ?? ""
                prompt.textFields?.forEach { $0.text = nil }
                self.prompt = nil
                guard self.epoch == epoch else { return }
                guard pop.range(of: "^[a-f0-9]{32}$", options: .regularExpression) != nil else { self.close(code: "authentication_failed"); return }
                selected.proofOfPossession = pop
                let observer = ProvisioningBLEObserver { [weak self, weak selected] in
                    DispatchQueue.main.async {
                        guard let self, let selected, self.device === selected else { return }
                        self.close(code: "disconnected")
                    }
                }
                self.bleObserver = observer
                selected.bleDelegate = observer
                self.connectionStarted = true
                selected.connect(delegate: self) { status in
                    DispatchQueue.main.async {
                        guard self.epoch == epoch, self.pending === call else { return }
                        switch status {
                        case .connected:
                            guard self.secure(selected) else { self.close(code: "authentication_failed"); return }
                            self.request(["v": 1, "command": "status"], call: call)
                        case .failedToConnect:
                            self.close(code: "authentication_failed")
                        case .disconnected:
                            self.close(code: "disconnected")
                        }
                    }
                }
            })
            self.prompt = prompt
            viewController.present(prompt, animated: true)
        }
    }

    @objc func status(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.begin(call) else { return }
            self.request(["v": 1, "command": "status"], call: call)
        }
    }

    @objc func configure(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let profile = self.profile(call), self.begin(call, seconds: 120), let viewController = self.bridge?.viewController else { return }
            guard let device = self.device, self.secure(device) else { self.fail("disconnected"); return }
            let epoch = self.epoch
            let prompt = UIAlertController(title: profile == "hotspot" ? "iPhone Osobní hotspot" : "Záložní Wi-Fi", message: "Nové údaje nahradí uložené až po úspěšném připojení kufru. Heslo neopustí zabezpečený BLE kanál a v telefonu se neuloží.", preferredStyle: .alert)
            prompt.addTextField { field in
                field.placeholder = "Název sítě (SSID)"
                field.autocapitalizationType = .none
                field.autocorrectionType = .no
            }
            prompt.addTextField { field in
                field.placeholder = "Heslo WPA (8–63 bajtů)"
                field.isSecureTextEntry = true
                field.autocapitalizationType = .none
                field.autocorrectionType = .no
                field.textContentType = .newPassword
            }
            prompt.addAction(UIAlertAction(title: "Zrušit", style: .cancel) { _ in
                prompt.textFields?.forEach { $0.text = nil }
                if self.epoch == epoch { self.fail("cancelled") }
            })
            prompt.addAction(UIAlertAction(title: "Nastavit / nahradit", style: .default) { _ in
                let ssid = prompt.textFields?[0].text ?? ""
                let password = prompt.textFields?[1].text ?? ""
                prompt.textFields?.forEach { $0.text = nil }
                self.prompt = nil
                guard self.epoch == epoch else { return }
                guard (1...32).contains(ssid.utf8.count), (8...63).contains(password.utf8.count), !ssid.contains("\0"), !password.contains("\0") else { self.fail("invalid_credentials"); return }
                self.request(["v": 1, "command": "configure", "profile": profile, "ssid": ssid, "password": password], call: call)
            })
            self.prompt = prompt
            viewController.present(prompt, animated: true)
        }
    }

    @objc func command(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let command = call.getString("command"), ["test", "delete", "ota-check", "ota-install", "ota-policy"].contains(command) else { call.reject("Neplatný příkaz.", "invalid_request"); return }
            var payload: [String: Any] = ["v": 1, "command": command]
            if command == "test" || command == "delete" {
                guard let profile = self.profile(call) else { return }
                payload["profile"] = profile
            }
            if command == "ota-policy" {
                guard let automatic = call.getBool("automaticInstall") else { call.reject("Neplatná volba.", "invalid_request"); return }
                payload["automaticInstall"] = automatic
            }
            guard self.begin(call, seconds: 120) else { return }
            if command == "delete" || command == "ota-install" || command == "ota-policy" {
                guard let viewController = self.bridge?.viewController else { self.fail("provisioning_unavailable"); return }
                let epoch = self.epoch
                let deleting = command == "delete"
                let prompt = UIAlertController(title: deleting ? "Smazat síť?" : "Změnit aktualizace firmware?", message: deleting ? "Smaže se pouze vybraná síť v kufru. Bez jiné dostupné sítě přejde do obnovy BLE. Tato akce nemaže párování ani identitu." : "Aktualizace se aktivuje až v bezpečném klidu kufru. Restart přeruší BLE spojení. Automatická instalace může později aktivovat schválené aktualizace bez telefonu.", preferredStyle: .alert)
                prompt.addAction(UIAlertAction(title: "Zrušit", style: .cancel) { _ in if self.epoch == epoch { self.fail("cancelled") } })
                prompt.addAction(UIAlertAction(title: deleting ? "Smazat vybranou síť" : "Potvrdit", style: deleting ? .destructive : .default) { _ in
                    self.prompt = nil
                    guard self.epoch == epoch else { return }
                    if deleting { payload["confirm"] = true }
                    self.request(payload, call: call)
                })
                self.prompt = prompt
                viewController.present(prompt, animated: true)
            } else { self.request(payload, call: call) }
        }
    }

    @objc func disconnect(_ call: CAPPluginCall) {
        DispatchQueue.main.async { self.close(code: "disconnected"); call.resolve() }
    }

    @objc func openSettings(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let url = URL(string: UIApplication.openSettingsURLString) else { call.reject("Nastavení není dostupné.", "provisioning_unavailable"); return }
            UIApplication.shared.open(url, options: [:]) { opened in
                if opened { call.resolve() } else { call.reject("Otevřete Nastavení ručně.", "provisioning_unavailable") }
            }
        }
    }

    private func begin(_ call: CAPPluginCall, seconds: Double = 15) -> Bool {
        guard pending == nil else { call.reject("Jiná BLE akce probíhá.", "busy"); return false }
        guard UIApplication.shared.applicationState == .active, let viewController = bridge?.viewController,
              viewController.presentedViewController == nil else { call.reject("BLE není dostupné na pozadí.", "provisioning_unavailable"); return false }
        epoch = UUID()
        pending = call
        let epoch = self.epoch
        let timeout = DispatchWorkItem { [weak self] in
            guard let self, self.epoch == epoch else { return }
            self.close(code: "timeout")
        }
        self.timeout = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: timeout)
        return true
    }

    private func profile(_ call: CAPPluginCall) -> String? {
        guard let profile = call.getString("profile"), ["hotspot", "fallback"].contains(profile) else { call.reject("Neplatná síť.", "invalid_request"); return nil }
        return profile
    }

    private func secure(_ device: ESPDevice) -> Bool {
        let version = device.versionInfo?["prov"] as? NSDictionary
        return device.isSessionEstablished() && version?["sec_ver"] as? Int == 1 && !(device.capabilities ?? []).contains("no_pop")
    }

    private func request(_ payload: [String: Any], call: CAPPluginCall) {
        guard let device, secure(device), let data = try? JSONSerialization.data(withJSONObject: payload), data.count <= 512 else { fail("disconnected"); return }
        let epoch = self.epoch
        device.sendData(path: "kufr-control", data: data) { data, error in
            DispatchQueue.main.async {
                guard self.epoch == epoch, self.pending === call else { return }
                guard error == nil, let data, let result = ProvisioningStatusCodec.decode(data) else {
                    self.close(code: error == nil ? "invalid_response" : "disconnected")
                    return
                }
                self.finish(result)
            }
        }
    }

    private func finish(_ result: [String: Any]) {
        let call = pending
        pending = nil
        timeout?.cancel()
        timeout = nil
        prompt = nil
        call?.resolve(result)
    }

    private func fail(_ code: String) {
        let call = pending
        pending = nil
        timeout?.cancel()
        timeout = nil
        prompt?.textFields?.forEach { $0.text = nil }
        prompt?.dismiss(animated: false)
        prompt = nil
        call?.reject("BLE požadavek nebyl dokončen.", code)
    }

    private func close(code: String) {
        epoch = UUID()
        fail(code)
        ESPProvisionManager.shared.stopESPDevicesSearch()
        releaseDevice()
    }

    private func releaseDevice() {
        let old = device
        let disconnect = connectionStarted
        connectionStarted = false
        device = nil
        bleObserver = nil
        old?.bleDelegate = nil
        old?.delegate = nil
        old?.proofOfPossession = nil
        // ESPDevice's BLE transport is initialized only by connect(). Calling
        // disconnect after cancelling the PoP dialog would dereference nil.
        if disconnect { old?.disconnect() }
    }

    public func getProofOfPossesion(forDevice: ESPDevice, completionHandler: @escaping (String) -> Void) { completionHandler("") }
    public func getUsername(forDevice: ESPDevice, completionHandler: @escaping (String?) -> Void) { completionHandler(nil) }
}

private final class ProvisioningBLEObserver: ESPBLEDelegate {
    private let disconnected: () -> Void
    init(disconnected: @escaping () -> Void) { self.disconnected = disconnected }
    func peripheralConnected() {}
    func peripheralDisconnected(peripheral: CBPeripheral, error: Error?) { disconnected() }
    func peripheralFailedToConnect(peripheral: CBPeripheral?, error: Error?) { disconnected() }
}
