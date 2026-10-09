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
var legacy = good
legacy["id"] = "kufr_1"
require(ProvisioningStatusCodec.decode(try JSONSerialization.data(withJSONObject: legacy)) != nil,
        "Legacy provisioning identity remains decodable")
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
var withAddress = good
var addressNetwork = good["n"] as! [String: Any]
addressNetwork["ip"] = "192.168.0.59"
withAddress["n"] = addressNetwork
let addressed = ProvisioningStatusCodec.decode(try JSONSerialization.data(withJSONObject: withAddress))!
require((addressed["network"] as? [String: Any])?["ipv4"] as? String == "192.168.0.59", "secured address projection")
let invalidAddresses: [Any] = ["0.0.0.0", "127.0.0.1", "224.0.0.1", "255.255.255.255", "192.168.0.256",
                           "192.168.00.59", "192.168.0.59:443", "kufr.local", "192.168.0.59\n", true, 123]
for invalidAddress in invalidAddresses {
    addressNetwork["ip"] = invalidAddress
    withAddress["n"] = addressNetwork
    let decoded = ProvisioningStatusCodec.decode(try JSONSerialization.data(withJSONObject: withAddress))!
    require((decoded["network"] as? [String: Any])?["ipv4"] == nil, "invalid address must clear hint")
}
addressNetwork["ip"] = "192.168.0.59"
addressNetwork["p"] = "offline"
withAddress["n"] = addressNetwork
let offline = ProvisioningStatusCodec.decode(try JSONSerialization.data(withJSONObject: withAddress))!
require((offline["network"] as? [String: Any])?["ipv4"] == nil, "offline address must clear hint")
var maximumNetwork = maximum["n"] as! [String: Any]
maximumNetwork["p"] = "connected"
maximumNetwork["ip"] = "192.168.255.254"
maximum["n"] = maximumNetwork
let maximumAddressed = try JSONSerialization.data(withJSONObject: maximum)
require(maximumAddressed.count <= 512 && ProvisioningStatusCodec.decode(maximumAddressed) != nil,
        "maximum current firmware status plus address exceeds ATT capacity")
print("Native provisioning status codec: passed")
