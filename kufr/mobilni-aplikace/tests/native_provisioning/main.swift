import Foundation

func require(_ condition: Bool, _ description: String) {
    if !condition { fatalError(description) }
}
let good: [String: Any] = ["v": 1, "ok": true, "id": "kufr-1", "fw": "1.0.0", "password": "must-not-cross-bridge",
    "n": ["p": "connected", "a": "hotspot", "l": "hotspot", "r": "connected", "h": "iPhone", "f": ""],
    "b": false, "o": ["s": "idle", "a": "", "e": "", "i": false, "privateKey": "secret"]]
let encoded = try JSONSerialization.data(withJSONObject: good)
let safe = ProvisioningStatusCodec.decode(encoded)!
let projected = String(data: try JSONSerialization.data(withJSONObject: safe), encoding: .utf8)!
require(!projected.contains("password") && !projected.contains("privateKey") && !projected.contains("secret"), "diagnostics leaked a secret field")
require(safe["backendConnected"] as? Bool == false, "backend connectivity changed")
var invalid = good
invalid["v"] = 2
require(ProvisioningStatusCodec.decode(try JSONSerialization.data(withJSONObject: invalid)) == nil, "wrong protocol accepted")
invalid = good
invalid["n"] = ["p": "made-up"]
require(ProvisioningStatusCodec.decode(try JSONSerialization.data(withJSONObject: invalid)) == nil, "invalid network accepted")
require(ProvisioningStatusCodec.decode(Data(repeating: 32, count: 513)) == nil, "ATT oversized response accepted")
var maximum = good
maximum.removeValue(forKey: "password")
maximum["id"] = String(repeating: "a", count: 64)
maximum["fw"] = "65535.65535.65535"
maximum["n"] = ["p": "connecting", "a": "fallback", "l": "hotspot", "r": "connection_failed",
                "h": String(repeating: "\"", count: 32), "f": String(repeating: "\"", count: 32)]
maximum["o"] = ["s": "waiting-idle", "a": "65535.65535.65535", "e": "ota-trust-or-origin-not-provisioned", "i": false]
let maximumData = try JSONSerialization.data(withJSONObject: maximum)
require(maximumData.count <= 512 && ProvisioningStatusCodec.decode(maximumData) != nil, "valid maximum status exceeds ATT capacity")
print("Native provisioning status codec: passed")
