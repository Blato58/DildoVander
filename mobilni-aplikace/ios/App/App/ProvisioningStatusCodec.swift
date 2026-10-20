import Foundation

// Whitelist public fields before crossing the Capacitor bridge. Firmware bugs
// or future endpoint additions must never expose credentials to JavaScript.
enum ProvisioningStatusCodec {
    static func decode(_ data: Data) -> [String: Any]? {
        guard data.count <= 512,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              object["v"] as? Int == 1, let ok = object["ok"] as? Bool,
              let deviceId = text(object["id"], max: 64),
              let firmware = text(object["fw"], max: 31),
              let network = object["n"] as? [String: Any],
              let phase = text(network["p"]), ["offline", "connecting", "connected"].contains(phase),
              let active = text(network["a"]), ["none", "hotspot", "fallback"].contains(active),
              let last = text(network["l"]), ["none", "hotspot", "fallback"].contains(last),
              let result = text(network["r"]),
              let hotspot = profile(network["h"]), let fallback = profile(network["f"]),
              let backend = object["b"] as? Bool,
              let ota = object["o"] as? [String: Any], let state = text(ota["s"]) else { return nil }
        var safeOta: [String: Any] = ["state": state, "version": firmware]
        if let version = text(ota["a"], max: 31) { safeOta["availableVersion"] = version }
        if let error = text(ota["e"]) { safeOta["error"] = error }
        if let automatic = ota["i"] as? Bool { safeOta["automaticInstall"] = automatic }
        var safeNetwork: [String: Any] = ["phase": phase, "activeProfile": active,
            "lastSuccessfulProfile": last, "result": result, "hotspot": hotspot, "fallback": fallback]
        if phase == "connected", let ip = network["ip"] as? String, validIpv4(ip) {
            safeNetwork["ipv4"] = ip
        }
        var safe: [String: Any] = ["v": 1, "ok": ok, "deviceId": deviceId, "firmwareVersion": firmware,
            "network": safeNetwork, "backendConnected": backend, "ota": safeOta]
        if let error = text(object["error"]) { safe["error"] = error }
        return safe
    }

    static func validIpv4(_ address: String) -> Bool {
        let parts = address.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return false }
        var octets: [Int] = []
        for part in parts {
            guard !part.isEmpty, part.count <= 3,
                  part.utf8.allSatisfy({ (48...57).contains($0) }),
                  part.count == 1 || part.first != "0",
                  let octet = Int(part), octet <= 255 else { return false }
            octets.append(octet)
        }
        // Reject unspecified, loopback, multicast and reserved destinations.
        return octets[0] > 0 && octets[0] != 127 && octets[0] < 224
    }

    private static func text(_ value: Any?, max: Int = 64) -> String? {
        guard let value = value as? String, value.utf8.count <= max, !value.contains("\0") else { return nil }
        return value
    }

    private static func profile(_ value: Any?) -> [String: Any]? {
        guard let ssid = text(value, max: 32) else { return nil }
        return ["configured": !ssid.isEmpty, "ssid": ssid]
    }
}
