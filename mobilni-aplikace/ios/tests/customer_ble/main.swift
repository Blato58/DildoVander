import Foundation

let valid: [String: Any] = ["v": 1, "deviceId": "kufr-1",
    "bootId": "12345678-1234-4234-8234-123456789abc",
    "challenge": "22345678-1234-4234-8234-123456789abc",
    "state": "idle", "capabilities": ["customerStart": 1]]
func data(_ object: [String: Any]) -> Data { try! JSONSerialization.data(withJSONObject: object) }
precondition(CustomerBleProtocol.decode(data(valid)) != nil)
for (key, value) in [("v", 2 as Any), ("v", true as Any), ("v", 1.5 as Any), ("sessionId", 42 as Any), ("bootId", "bad" as Any),
                     ("state", "provisioning" as Any), ("challenge", "bad" as Any),
                     ("deviceId", "" as Any), ("capabilities", ["customerStart": 2] as Any),
                     ("operationId", "bad" as Any), ("sessionId", "bad" as Any)] {
    var invalid = valid; invalid[key] = value
    precondition(CustomerBleProtocol.decode(data(invalid)) == nil)
}
precondition(CustomerBleProtocol.decode(Data(repeating: 0x20, count: 513)) == nil)
var disconnected = valid; disconnected["challenge"] = ""; disconnected["state"] = "recovering"
precondition(CustomerBleProtocol.decode(data(disconnected)) != nil)
precondition(AccountTransportPolicy.account(path: "/app/v1/customer/character", method: "PUT"))
precondition(AccountTransportPolicy.account(path: "/app/v1/customer/enrollment/complete", method: "POST"))
precondition(AccountTransportPolicy.account(path: "/app/v1/customer/admin/characters", method: "GET"))
precondition(AccountTransportPolicy.account(path: "/app/v1/customer/admin/sessions/12345678-1234-4234-8234-123456789abc/end", method: "POST"))
precondition(AccountTransportPolicy.account(path: "/app/v1/customer/character?expectedRevision=1&expectedProfileId=12345678123442348234123456789abc", method: "DELETE"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/customer/character?expectedRevision=1&expectedProfileId=12345678123442348234123456789abc&extra=1", method: "DELETE"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/customer/character?expectedRevision=-1&expectedProfileId=12345678123442348234123456789abc", method: "DELETE"))
precondition(AccountTransportPolicy.credentialCommit(path: "/app/v1/customer/enrollment/complete", method: "POST"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/customer/sessions/not-a-uuid/end", method: "POST"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/customer/../admin/users", method: "GET"))
precondition(!AccountTransportPolicy.account(path: "/app/v1/customer/start-tickets", method: "GET"))
print("Customer BLE codec and account allowlist passed")
