import Foundation

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

func testPair(_ secret: String, pin expectedPin: String, success: Bool) {
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
}
testPair(String(repeating: "a", count: 32), pin: pin, success: true)
testPair(String(repeating: "a", count: 32), pin: String(repeating: "0", count: 64), success: false)
for secret in ["c", "d", "e", "f"] {
    testPair(String(repeating: secret, count: 32), pin: pin, success: false)
}

func testSocket(_ message: String, expectedPin: String, token: String, receivesFrame: Bool) {
    let transport = LocalDeviceTransport(host: "127.0.0.1", pin: expectedPin, port: port)
    var done = false
    var received = false
    transport.onOpen = { transport.send(message) { _ in } }
    transport.onFrame = { frame in
        check(receivesFrame && frame == message, "Unexpected WSS frame")
        received = true
        transport.close()
    }
    transport.onClose = { done = true }
    transport.connect(token: token)
    waitUntil { done }
    check(received == receivesFrame, "Frame delivery")
}
testSocket("hello", expectedPin: pin, token: identity.token, receivesFrame: true)
testSocket("hello", expectedPin: String(repeating: "0", count: 64), token: identity.token, receivesFrame: false)
testSocket("hello", expectedPin: pin, token: String(repeating: "a", count: 64), receivesFrame: false)
testSocket("binary", expectedPin: pin, token: identity.token, receivesFrame: false)
testSocket("large", expectedPin: pin, token: identity.token, receivesFrame: false)
print("Native SPKI / Keychain / pinned HTTPS / authenticated WSS tests passed")
