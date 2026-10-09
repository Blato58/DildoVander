import Foundation
import Network

func check(_ condition: @autoclosure () -> Bool, _ description: String) {
    if !condition() { fatalError(description) }
}

func waitUntil(_ condition: () -> Bool) {
    let deadline = Date().addingTimeInterval(20)
    while !condition() && Date() < deadline {
        RunLoop.main.run(until: Date().addingTimeInterval(0.01))
    }
    check(condition(), "Native test deadline")
}

let directory = CommandLine.arguments[1]
let port = Int(CommandLine.arguments[2])!
let discoveryTxt = ["deviceId": "test-kufr", "protocol": "1",
                    "capabilities": "manualControl,inventoryReload,conversationText"]
check(LocalDeviceIdentity.validDeviceId("kufr-001"), "Shared v1 device ID")
for invalidId in ["Kufr-001", "kufr_001", "1kufr", "kufr.local", String(repeating: "a", count: 65)] {
    check(!LocalDeviceIdentity.validDeviceId(invalidId), "Reject identity outside control v1 before pairing")
}
let discoveryCandidate = LocalDiscoveredDevice.parse(txt: discoveryTxt, host: "kufr-test.local.", port: 443)
check(discoveryCandidate?.host == "kufr-test.local", "Normalize Bonjour hostname")
check(discoveryCandidate?.capabilities.count == 3, "Bounded public capabilities")
let networkTxt = NWTXTRecord(NetService.data(fromTXTRecord: discoveryTxt.mapValues { Data($0.utf8) })).dictionary
check(LocalDiscoveredDevice.parse(txt: networkTxt, host: "kufr-test.local.", port: 443)?.deviceId == "test-kufr",
      "Accept actual Network.framework decoding of firmware TXT record")
let lowercasedTxt = Dictionary(uniqueKeysWithValues: discoveryTxt.map { ($0.key.lowercased(), $0.value) })
check(LocalDiscoveredDevice.parse(txt: lowercasedTxt, host: "kufr-test.local.", port: 443)?.deviceId == "test-kufr",
      "Accept case-insensitive DNS-SD TXT keys returned by Network.framework")
let uppercaseTxt = Dictionary(uniqueKeysWithValues: discoveryTxt.map { ($0.key.uppercased(), $0.value) })
check(LocalDiscoveredDevice.parse(txt: uppercaseTxt, host: "kufr-test.local.", port: 443)?.deviceId == "test-kufr",
      "TXT key case does not alter advertised device ID")
var duplicateCaseTxt = discoveryTxt
duplicateCaseTxt["deviceid"] = "other-kufr"
check(LocalDiscoveredDevice.parse(txt: duplicateCaseTxt, host: "kufr-test.local.", port: 443) == nil,
      "Reject ambiguous duplicate case-insensitive keys")
for host in ["example.com", "https://kufr.local", "kufr.local:443", "a..local", "kufr.local/secret"] {
    check(LocalDiscoveredDevice.parse(txt: discoveryTxt, host: host, port: 443) == nil, "Reject discovery host")
}
check(LocalDiscoveredDevice.parse(txt: discoveryTxt, host: "kufr.local", port: 80) == nil, "Require TLS port")
for (key, value) in [("protocol", "2"), ("deviceId", "../secret"), ("capabilities", "a,a"),
                     ("capabilities", "a,,b"), ("capabilities", String(repeating: "a", count: 33)),
                     ("capabilities", (0..<17).map { "feature" + String($0) }.joined(separator: ",")),
                     ("pin", String(repeating: "a", count: 64)), ("token", "secret")] {
    var record = discoveryTxt
    record[key] = value
    check(LocalDiscoveredDevice.parse(txt: record, host: "kufr.local", port: 443) == nil, "Reject hostile TXT")
}
var missing = discoveryTxt
missing.removeValue(forKey: "deviceId")
check(LocalDiscoveredDevice.parse(txt: missing, host: "kufr.local", port: 443) == nil, "Require identity hint")
let cancelledDiscovery = LocalDeviceDiscovery()
var cancelCount = 0
cancelledDiscovery.start { devices, failed in
    check(devices.isEmpty && failed, "Cancel never returns old candidates")
    cancelCount += 1
}
cancelledDiscovery.cancel()
cancelledDiscovery.cancel()
check(cancelCount == 1, "Discovery completes once")
let boundedDiscovery = LocalDeviceDiscovery()
var discoveryFinished = false
final class TestAdvertisementDelegate: NSObject, NetServiceDelegate {
    var published = false
    func netServiceDidPublish(_ sender: NetService) { published = true }
    func netService(_ sender: NetService, didNotPublish errorDict: [String: NSNumber]) {
        fatalError("Test Bonjour advertisement failed")
    }
}
let advertisementDelegate = TestAdvertisementDelegate()
let advertisement = NetService(domain: "local.", type: "_kufr._tcp.",
                               name: "kufr-native-test-" + UUID().uuidString, port: 443)
advertisement.delegate = advertisementDelegate
check(advertisement.setTXTRecord(NetService.data(fromTXTRecord: discoveryTxt.mapValues { Data($0.utf8) })),
      "Set synthetic public TXT")
advertisement.publish()
waitUntil { advertisementDelegate.published }
let discoveryStarted = Date()
boundedDiscovery.start { devices, _ in
    check(devices.count <= 16, "Bound discovery candidates")
    check(devices.contains { $0.deviceId == "test-kufr" && $0.capabilities.count == 3 },
          "Resolve real local Bonjour advertisement")
    discoveryFinished = true
}
waitUntil { discoveryFinished }
check(Date().timeIntervalSince(discoveryStarted) < 6, "Discovery has a five-second deadline")
advertisement.stop()

for name in ["ec256", "ec384", "rsa"] {
    let certificate = try Data(contentsOf: URL(fileURLWithPath: directory + "/" + name + ".der"))
    let pin = try String(contentsOfFile: directory + "/" + name + ".sha256", encoding: .utf8)
        .trimmingCharacters(in: .whitespacesAndNewlines)
    check(LocalCertificatePin.matches(certificate, expected: pin), "SPKI must equal OpenSSL for " + name)
    check(!LocalCertificatePin.matches(certificate, expected: String(repeating: "0", count: 64)), "Wrong pin")
    for length in [0, 1, 2, 32, certificate.count - 1] {
        check(!LocalCertificatePin.matches(certificate.prefix(length), expected: pin), "Truncated DER")
    }
    check(!LocalCertificatePin.matches(certificate + Data([0]), expected: pin), "Trailing DER")
}
check(!LocalDeviceIdentity.validHost("https://127.0.0.1"), "Reject URL")
check(!LocalDeviceIdentity.validHost("127.0.0.1:443"), "Reject port")
check(!LocalDeviceIdentity.validHost("user@localhost"), "Reject userinfo")
check(!LocalDeviceIdentity.validHost("localhost/secret"), "Reject path")
check(LocalDeviceIdentity.validHost("kufr.local"), "Allow local hostname")
let pin = try String(contentsOfFile: directory + "/ec256.sha256", encoding: .utf8)
    .trimmingCharacters(in: .whitespacesAndNewlines)
let keychain = LocalDeviceKeychain(service: "kufr-native-test." + UUID().uuidString)
let bleResponse: [String: Any] = ["v": 1, "ok": true, "id": "test-kufr", "host": "kufr-test.local",
                                "spkiSha256": pin, "token": String(repeating: "b", count: 64)]
let bleData = try JSONSerialization.data(withJSONObject: bleResponse)
check(LocalDeviceEnrollment.begin(), "Reserve native enrollment")
check(!LocalDeviceEnrollment.begin(), "Serialize QR and BLE before token consumption")
LocalDeviceEnrollment.end()
check(LocalDeviceEnrollment.begin(), "Release native enrollment")
LocalDeviceEnrollment.end()
for code in ["already_paired", "busy", "pairing_failed", "invalid_request"] {
    let response = try JSONSerialization.data(withJSONObject: ["v": 1, "ok": false, "error": code])
    check(LocalBlePairingCodec.error(response) == code, "Allowlisted BLE recovery error")
}
let unknownError = try JSONSerialization.data(withJSONObject: ["v": 1, "ok": false, "error": "secret value"])
check(LocalBlePairingCodec.error(unknownError) == "pairing_failed", "Never expose firmware error text")
let malformedRecoveryResponses: [[String: Any]] = [
    ["v": true, "ok": false, "error": "already_paired"],
    ["v": 1, "ok": 0, "error": "already_paired"],
    ["v": 1, "ok": true, "error": "already_paired"],
    ["v": 1, "ok": false, "error": "already_paired", "token": "secret"]
]
for malformed in malformedRecoveryResponses {
    let response = try JSONSerialization.data(withJSONObject: malformed)
    check(LocalBlePairingCodec.error(response) == nil, "Malformed response cannot trigger recovery")
}
check(LocalBlePairingCodec.error(Data(repeating: 32, count: 513)) == nil, "Bound recovery errors")
let bleIdentity = LocalBlePairingCodec.decode(bleData, expectedDeviceId: "test-kufr")!
check(bleIdentity.host == "kufr-test.local", "Decode native BLE pairing identity")
check(LocalBlePairingCodec.decode(bleData, expectedDeviceId: "other-kufr") == nil, "BLE device binding")
for (field, value) in [("host", "https://evil.local"), ("token", "secret"), ("spkiSha256", "ABC"), ("extra", "secret")] {
    var invalid = bleResponse
    invalid[field] = value
    let invalidData = try JSONSerialization.data(withJSONObject: invalid)
    check(LocalBlePairingCodec.decode(invalidData, expectedDeviceId: "test-kufr") == nil,
          "Reject malformed BLE pairing " + field)
}
for (field, value) in [("v", NSNumber(value: true)), ("ok", NSNumber(value: 1))] {
    var invalid = bleResponse
    invalid[field] = value
    let invalidData = try JSONSerialization.data(withJSONObject: invalid)
    check(LocalBlePairingCodec.decode(invalidData, expectedDeviceId: "test-kufr") == nil,
          "Reject BLE scalar type mismatch " + field)
}
check(LocalBlePairingCodec.decode(Data(repeating: 32, count: 513), expectedDeviceId: "test-kufr") == nil,
      "Bound BLE pairing response")
try keychain.insert(bleIdentity)
do {
    try keychain.insert(LocalDeviceIdentity(deviceId: "other-kufr", host: "other.local", spkiSha256: pin,
                                         token: String(repeating: "c", count: 64)))
    fatalError("Enrollment replaced existing Keychain pairing")
} catch LocalDeviceError.credentialWriteFailed {}
let preserved = try keychain.read()
check(preserved?.token == bleIdentity.token && preserved?.deviceId == bleIdentity.deviceId, "Preserve first enrollment")
try keychain.remove()
let initialIdentity = try keychain.read()
check(initialIdentity == nil, "Initially unpaired")
let identity = LocalDeviceIdentity(deviceId: "test-kufr", host: "127.0.0.1", spkiSha256: pin,
                                   token: String(repeating: "b", count: 64))
try keychain.write(identity)
let restoredIdentity = try keychain.read()
check(restoredIdentity?.token == identity.token, "Keychain roundtrip")
try keychain.remove()
let removedIdentity = try keychain.read()
check(removedIdentity == nil, "Keychain cleanup")

let proofService = "kufr-provisioning-test." + UUID().uuidString
let proofKeychain = ProvisioningProofKeychain(service: proofService)
let proof = String(repeating: "a", count: 32)
let replacementProof = String(repeating: "b", count: 32)
func checkProof(_ store: ProvisioningProofKeychain, _ name: String, _ expected: String?, _ description: String) throws {
    let actual = try store.read(deviceName: name)
    check(actual == expected, description)
}
try checkProof(proofKeychain, "KUFR_AABBCC", nil, "Initially no saved BLE proof")
try proofKeychain.write(proof, deviceName: "KUFR_AABBCC")
let reopenedProofKeychain = ProvisioningProofKeychain(service: proofService)
try checkProof(reopenedProofKeychain, "KUFR_AABBCC", proof, "BLE proof survives store recreation")
try checkProof(reopenedProofKeychain, "KUFR_112233", nil, "Do not reuse proof for another BLE device")
try proofKeychain.write(replacementProof, deviceName: "KUFR_112233")
try proofKeychain.write(replacementProof, deviceName: "KUFR_AABBCC")
try checkProof(reopenedProofKeychain, "KUFR_AABBCC", replacementProof, "Replace old verified BLE proof")
for invalidProof in ["", String(repeating: "a", count: 31), String(repeating: "A", count: 32)] {
    do {
        try proofKeychain.write(invalidProof, deviceName: "KUFR_AABBCC")
        fatalError("Invalid proof accepted")
    } catch LocalDeviceError.invalidIdentity {}
}
try checkProof(reopenedProofKeychain, "KUFR_AABBCC", replacementProof, "Invalid writes preserve stored proof")
do {
    _ = try proofKeychain.read(deviceName: "other-device")
    fatalError("Invalid BLE name accepted")
} catch LocalDeviceError.invalidIdentity {}
try proofKeychain.remove(deviceName: "KUFR_AABBCC")
try checkProof(reopenedProofKeychain, "KUFR_AABBCC", nil, "Remove selected BLE proof")
try checkProof(reopenedProofKeychain, "KUFR_112233", replacementProof, "Removal preserves another device proof")
try proofKeychain.remove(deviceName: "KUFR_112233")

let failureAllowlist: Set<String> = [
    "invalid_identity", "certificate_mismatch", "authorization_rejected", "server_busy",
    "handshake_rejected", "tls_failed", "host_unreachable", "network_unavailable",
    "connection_timeout", "protocol_error", "connection_lost"
]
check(Set(LocalDeviceTransportFailureCode.allCases.map(\.rawValue)) == failureAllowlist,
      "Native failures must be the exact sanitized allowlist")
for (status, expected) in [(401, LocalDeviceTransportFailureCode.authorizationRejected),
                           (403, .authorizationRejected), (409, .serverBusy), (503, .serverBusy),
                           (400, .handshakeRejected), (200, .handshakeRejected)] {
    check(LocalDeviceTransport.classifyFailure(nil, httpStatus: status) == expected,
          "Classify HTTP upgrade status without response details")
}
for (code, expected) in [(NSURLErrorTimedOut, LocalDeviceTransportFailureCode.connectionTimeout),
                         (NSURLErrorCannotFindHost, .hostUnreachable),
                         (NSURLErrorCannotConnectToHost, .hostUnreachable),
                         (NSURLErrorNotConnectedToInternet, .networkUnavailable),
                         (NSURLErrorSecureConnectionFailed, .tlsFailed),
                         (NSURLErrorBadServerResponse, .handshakeRejected),
                         (NSURLErrorNetworkConnectionLost, .connectionLost)] {
    let unsafe = NSError(domain: NSURLErrorDomain, code: code,
                         userInfo: [NSLocalizedDescriptionKey: "secret-token https://private-host.invalid/ pin"])
    check(LocalDeviceTransport.classifyFailure(unsafe) == expected, "Sanitize URL errors")
}
check(LocalDeviceTransport.classifyFailure(NSError(domain: "private-detail", code: 42)) == .connectionLost,
      "Unknown errors cannot expose their domain or description")

func testPair(_ secret: String, pin expectedPin: String, success: Bool,
              expectedFailure: LocalDeviceTransportFailureCode? = nil) {
    let transport = LocalDeviceTransport(host: "127.0.0.1", pin: expectedPin, port: port)
    var done = false
    transport.pair(secret: secret) { result in
        switch result {
        case .success(let token): check(success && token == identity.token, "Pairing response")
        case .failure: check(!success, "Pairing unexpectedly failed")
        }
        done = true
    }
    waitUntil { done }
    check(transport.failureCode == expectedFailure, "Pairing failure code")
}
testPair(String(repeating: "a", count: 32), pin: pin, success: true)
testPair(String(repeating: "a", count: 32), pin: String(repeating: "0", count: 64), success: false,
         expectedFailure: .certificateMismatch)
for (secret, expected) in [("c", LocalDeviceTransportFailureCode.handshakeRejected),
                           ("d", .protocolError), ("e", .protocolError), ("f", .authorizationRejected)] {
    testPair(String(repeating: secret, count: 32), pin: pin, success: false, expectedFailure: expected)
}

func testSocket(_ message: String, expectedPin: String, token: String, receivesFrame: Bool,
                expectedFailures: Set<LocalDeviceTransportFailureCode> = []) {
    let transport = LocalDeviceTransport(host: "127.0.0.1", pin: expectedPin, port: port)
    var done = false
    var received = false
    var closeCount = 0
    transport.onOpen = { transport.send(message) { _ in } }
    transport.onFrame = { frame in
        check(receivesFrame && frame == message, "Unexpected WSS frame")
        received = true
        transport.close()
    }
    transport.onClose = { closeCount += 1; done = true }
    transport.connect(token: token)
    waitUntil { done }
    check(received == receivesFrame, "Frame delivery")
    if receivesFrame {
        check(transport.failureCode == nil, "Explicit close must not invent a failure")
    } else {
        check(transport.failureCode != nil, "Failed WSS reports a sanitized failure code")
        if !expectedFailures.isEmpty {
            check(expectedFailures.contains(transport.failureCode!), "Expected WSS failure code")
        }
    }
    let failure = transport.failureCode
    transport.close()
    transport.close()
    check(closeCount == 1 && transport.failureCode == failure, "Close is idempotent and preserves first failure")
}
testSocket("hello", expectedPin: pin, token: identity.token, receivesFrame: true)
testSocket("hello", expectedPin: String(repeating: "0", count: 64), token: identity.token, receivesFrame: false,
           expectedFailures: [.certificateMismatch])
// Apple may omit the HTTP response for a rejected upgrade. Without its status
// the transport reports the generic handshake code instead of guessing 401.
testSocket("hello", expectedPin: pin, token: String(repeating: "a", count: 64), receivesFrame: false,
           expectedFailures: [.authorizationRejected, .handshakeRejected])
testSocket("hello", expectedPin: pin, token: "invalid", receivesFrame: false, expectedFailures: [.invalidIdentity])
testSocket("binary", expectedPin: pin, token: identity.token, receivesFrame: false, expectedFailures: [.protocolError])
testSocket("large", expectedPin: pin, token: identity.token, receivesFrame: false)
let unsendable = LocalDeviceTransport(host: "127.0.0.1", pin: pin, port: port)
var sendEvents: [String] = []
unsendable.onClose = { sendEvents.append("closed") }
unsendable.send("hello") { sent in
    check(!sent && unsendable.failureCode == .protocolError, "Rejected send reports sanitized failure")
    sendEvents.append("completion")
}
check(sendEvents == ["closed", "completion"], "Typed closure precedes rejected send completion")
print("Native discovery / SPKI / Keychain / pinned HTTPS / authenticated WSS tests passed")
