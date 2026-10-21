import Capacitor
import CoreBluetooth
import Foundation

@objc(KufrCustomerBlePlugin)
public final class KufrCustomerBlePlugin: CAPPlugin, CAPBridgedPlugin, CBCentralManagerDelegate, CBPeripheralDelegate {
    public let identifier = "KufrCustomerBlePlugin"
    public let jsName = "KufrCustomerBle"
    public let pluginMethods: [CAPPluginMethod] = [
        CAPPluginMethod(name: "discover", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "connect", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "start", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "status", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "end", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "disconnect", returnType: CAPPluginReturnPromise)]
    private let service = CBUUID(string: "B97A7F10-70E8-4B98-9DCC-40D66395C822")
    private let control = CBUUID(string: "B97AFF55-70E8-4B98-9DCC-40D66395C822")
    private var central: CBCentralManager?
    private var peripherals: [UUID: CBPeripheral] = [:]
    private var peripheral: CBPeripheral?
    private var characteristic: CBCharacteristic?
    private var pending: CAPPluginCall?
    private var timeout: DispatchWorkItem?
    private var epoch: UInt64 = 0
    private var scanning = false
    private var operationID: String?

    private func begin(_ call: CAPPluginCall, seconds: Double = 15) -> Bool {
        guard pending == nil else { call.reject("busy", "busy"); return false }
        epoch &+= 1
        let current = epoch
        pending = call
        let timer = DispatchWorkItem { [weak self] in
            guard let self, self.epoch == current, self.pending === call else { return }
            self.invalidate("timeout")
        }
        timeout = timer
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: timer)
        return true
    }
    private func finish(_ result: [String: Any] = [:], error: String? = nil) {
        timeout?.cancel(); timeout = nil
        let call = pending; pending = nil
        if let error { call?.reject(error, error) } else { call?.resolve(result) }
    }
    private func invalidate(_ error: String) {
        central?.stopScan(); scanning = false
        if let selected = peripheral { central?.cancelPeripheralConnection(selected) }
        peripheral = nil; characteristic = nil
        finish(error: error)
    }
    @objc func discover(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard self.begin(call, seconds: 12) else { return }
            self.scanning = true
            self.peripherals.removeAll()
            if self.central == nil { self.central = CBCentralManager(delegate: self, queue: .main) }
            else { self.startScanIfReady() }
        }
    }
    private func startScanIfReady() {
        guard scanning, pending != nil, let central else { return }
        switch central.state {
        case .poweredOn:
            central.scanForPeripherals(withServices: [service], options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
            let current = epoch
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
                guard let self, self.epoch == current, self.scanning else { return }
                self.central?.stopScan(); self.scanning = false
                let devices = self.peripherals.values.map { ["id": $0.identifier.uuidString, "name": $0.name ?? "DildoVander"] }
                    .sorted { ($0["id"] ?? "") < ($1["id"] ?? "") }
                self.finish(["devices": devices])
            }
        case .unauthorized: scanning = false; finish(error: "permission_denied")
        case .unsupported: scanning = false; finish(error: "unsupported")
        case .poweredOff: scanning = false; finish(error: "bluetooth_off")
        default: break
        }
    }
    public func centralManagerDidUpdateState(_ central: CBCentralManager) { startScanIfReady() }
    public func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral,
                               advertisementData: [String: Any], rssi RSSI: NSNumber) {
        guard scanning, peripherals.count < 32 else { return }
        peripherals[peripheral.identifier] = peripheral
    }
    @objc func connect(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let raw = call.getString("id"), let id = UUID(uuidString: raw),
                  let chosen = self.peripherals[id], chosen.state == .disconnected else { call.reject("device_not_discovered", "device_not_discovered"); return }
            guard self.begin(call) else { return }
            self.central?.stopScan(); self.scanning = false
            if let previous = self.peripheral, previous !== chosen {
                self.central?.cancelPeripheralConnection(previous)
            }
            self.peripheral = chosen; self.characteristic = nil; self.operationID = nil
            chosen.delegate = self
            self.central?.connect(chosen)
        }
    }
    public func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        guard self.peripheral === peripheral, pending != nil else { return }
        peripheral.discoverServices([service])
    }
    public func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        guard self.peripheral === peripheral else { return }; finish(error: "connection_failed")
    }
    public func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        guard self.peripheral === peripheral else { return }
        characteristic = nil; self.peripheral = nil; finish(error: "disconnected")
    }
    public func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard self.peripheral === peripheral, pending != nil else { return }
        guard error == nil, let selected = peripheral.services?.first(where: { $0.uuid == service }) else {
            finish(error: "requires_update"); return
        }
        peripheral.discoverCharacteristics([control], for: selected)
    }
    public func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard self.peripheral === peripheral, pending != nil else { return }
        guard error == nil, let selected = service.characteristics?.first(where: { $0.uuid == control }),
              selected.properties.contains(.write), selected.properties.contains(.read) else {
            finish(error: "requires_update"); return
        }
        characteristic = selected
        send(["v": 1, "op": "challenge"])
    }
    private func send(_ object: [String: Any]) {
        guard let peripheral, peripheral.state == .connected, let characteristic,
              let bytes = try? JSONSerialization.data(withJSONObject: object), bytes.count <= 512,
              bytes.count <= peripheral.maximumWriteValueLength(for: .withResponse) else {
            finish(error: "unavailable"); return
        }
        peripheral.writeValue(bytes, for: characteristic, type: .withResponse)
    }
    public func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        guard self.peripheral === peripheral, self.characteristic === characteristic, pending != nil else { return }
        guard error == nil else { invalidate("write_failed"); return }
        peripheral.readValue(for: characteristic)
    }
    public func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard self.peripheral === peripheral, self.characteristic === characteristic, pending != nil else { return }
        guard error == nil, let bytes = characteristic.value,
              let object = CustomerBleProtocol.decode(bytes) else {
            invalidate("invalid_response"); return
        }
        finish(object)
    }
    @objc func start(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let ticket = call.getString("ticket"), !ticket.isEmpty, ticket.utf8.count <= 256,
                  ticket.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }),
                  let operation = call.getString("operationId"), UUID(uuidString: operation) != nil else {
                call.reject("invalid_request", "invalid_request"); return
            }
            guard self.begin(call) else { return }
            self.operationID = operation
            self.send(["v": 1, "op": "start", "ticket": ticket, "operationId": operation])
        }
    }
    @objc func status(_ call: CAPPluginCall) { query(call, op: "status") }
    @objc func end(_ call: CAPPluginCall) { query(call, op: "end") }
    private func query(_ call: CAPPluginCall, op: String) {
        DispatchQueue.main.async {
            guard self.begin(call) else { return }
            var request: [String: Any] = ["v": 1, "op": op]
            if let operation = call.getString("operationId") ?? self.operationID { request["operationId"] = operation }
            self.send(request)
        }
    }
    @objc func disconnect(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            self.epoch &+= 1; self.central?.stopScan(); self.scanning = false
            self.finish(error: "cancelled")
            if let peripheral = self.peripheral { self.central?.cancelPeripheralConnection(peripheral) }
            self.peripheral = nil; self.characteristic = nil; self.operationID = nil
            call.resolve()
        }
    }
}
