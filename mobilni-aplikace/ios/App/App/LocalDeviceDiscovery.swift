import Foundation
import Network

struct LocalDiscoveredDevice {
    let deviceId: String
    let host: String
    let capabilities: [String]

    static func parse(txt: [String: String], host: String, port: Int) -> LocalDiscoveredDevice? {
        // DNS-SD TXT keys are case-insensitive (RFC 6763 section 6.4).
        // Network.framework may return the firmware's deviceId as deviceid.
        var normalized: [String: String] = [:]
        for (key, value) in txt {
            let key = key.lowercased()
            guard normalized[key] == nil else { return nil }
            normalized[key] = value
        }
        let normalizedHost = host.hasSuffix(".") ? String(host.dropLast()) : host
        guard Set(normalized.keys) == Set(["deviceid", "protocol", "capabilities"]),
              txt.reduce(0, { $0 + $1.key.utf8.count + $1.value.utf8.count }) <= 512,
              normalized["protocol"] == "1", let deviceId = normalized["deviceid"],
              LocalDeviceIdentity.validDeviceId(deviceId), port == 443,
              normalizedHost.lowercased().hasSuffix(".local"),
              LocalDeviceIdentity.validHost(normalizedHost),
              let csv = normalized["capabilities"] else { return nil }
        let capabilities = csv.split(separator: ",", omittingEmptySubsequences: false).map(String.init)
        guard !capabilities.isEmpty, capabilities.count <= 16,
              Set(capabilities).count == capabilities.count,
              capabilities.allSatisfy({
                  $0.range(of: "^[A-Za-z][A-Za-z0-9]{0,31}$", options: .regularExpression) != nil
              }) else { return nil }
        return LocalDiscoveredDevice(deviceId: deviceId, host: normalizedHost, capabilities: capabilities)
    }
}

// Discovery supplies routing hints only. It never supplies a pin, token or connection.
final class LocalDeviceDiscovery: NSObject, NetServiceDelegate {
    private let browser = NWBrowser(
        for: .bonjourWithTXTRecord(type: "_kufr._tcp", domain: "local."), using: .tcp)
    private var services: [String: NetService] = [:]
    private var records: [String: [String: String]] = [:]
    private var candidates: [String: LocalDiscoveredDevice] = [:]
    private var completion: (([LocalDiscoveredDevice], Bool) -> Void)?
    private var timeout: DispatchWorkItem?
    private var started = false
    private var waiting = false

    func start(completion: @escaping ([LocalDiscoveredDevice], Bool) -> Void) {
        precondition(Thread.isMainThread)
        guard !started else { return }
        started = true
        self.completion = completion
        let timeout = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.finish(failed: self.waiting && self.candidates.isEmpty)
        }
        self.timeout = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: timeout)
        browser.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed: self?.finish(failed: true)
            // Waiting can recover after the LAN permission prompt or a path change.
            case .waiting: self?.waiting = true
            case .ready: self?.waiting = false
            default: break
            }
        }
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            self?.update(results)
        }
        browser.start(queue: .main)
    }

    func cancel() { finish(failed: true) }

    private func key(_ service: NetService) -> String { service.name + "|" + service.domain }

    private func update(_ results: Set<NWBrowser.Result>) {
        guard completion != nil else { return }
        var visible = Set<String>()
        for result in results {
            guard case .service(let name, let type, let domain, _) = result.endpoint,
                  (type == "_kufr._tcp." || type == "_kufr._tcp"), domain == "local.",
                  !name.isEmpty, name.utf8.count <= 255,
                  case .bonjour(let txt) = result.metadata else { continue }
            let identifier = name + "|" + domain
            visible.insert(identifier)
            let dictionary = txt.dictionary
            // Validate bounded TXT before starting any resolver.
            guard LocalDiscoveredDevice.parse(txt: dictionary, host: "validation.local", port: 443) != nil else {
                candidates.removeValue(forKey: identifier)
                records.removeValue(forKey: identifier)
                services.removeValue(forKey: identifier)?.stop()
                continue
            }
            if records[identifier] != dictionary {
                candidates.removeValue(forKey: identifier)
                services.removeValue(forKey: identifier)?.stop()
                records.removeValue(forKey: identifier)
            }
            guard services[identifier] == nil, services.count < 16 else { continue }
            let service = NetService(domain: domain, type: "_kufr._tcp.", name: name)
            services[identifier] = service
            records[identifier] = dictionary
            service.delegate = self
            service.resolve(withTimeout: 5)
        }
        for identifier in Array(services.keys) where !visible.contains(identifier) {
            services.removeValue(forKey: identifier)?.stop()
            records.removeValue(forKey: identifier)
            candidates.removeValue(forKey: identifier)
        }
    }

    func netServiceDidResolveAddress(_ sender: NetService) {
        let identifier = key(sender)
        guard completion != nil, services[identifier] === sender,
              let record = records[identifier], let host = sender.hostName,
              let candidate = LocalDiscoveredDevice.parse(txt: record, host: host, port: sender.port) else { return }
        candidates[identifier] = candidate
    }

    private func finish(failed: Bool) {
        guard let completion else { return }
        self.completion = nil
        timeout?.cancel()
        timeout = nil
        browser.stateUpdateHandler = nil
        browser.browseResultsChangedHandler = nil
        browser.cancel()
        for service in services.values { service.delegate = nil; service.stop() }
        services.removeAll()
        records.removeAll()
        let result = candidates.values.sorted {
            if $0.deviceId == $1.deviceId { return $0.host < $1.host }
            return $0.deviceId < $1.deviceId
        }
        candidates.removeAll()
        completion(failed ? [] : result, failed)
    }
}
