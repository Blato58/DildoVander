import CryptoKit
import Foundation
import Security

enum LocalDeviceError: Error {
    case invalidIdentity
    case invalidCertificate
    case unavailableCredential
    case credentialWriteFailed
    case invalidPairingResponse
}

struct LocalDeviceIdentity: Codable {
    let deviceId: String
    let host: String
    let spkiSha256: String
    let token: String

    static func validHost(_ host: String) -> Bool {
        // Only an explicit DNS name or IPv4 address; no URL, port or userinfo.
        guard !host.isEmpty, host.utf8.count <= 253 else { return false }
        return host.split(separator: ".", omittingEmptySubsequences: false).allSatisfy {
            $0.range(of: "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$",
                     options: .regularExpression) != nil
        }
    }

    static func validDeviceId(_ deviceId: String) -> Bool {
        // Match the shared control v1 schema before consuming the pairing secret.
        deviceId.range(of: "^[a-z][a-z0-9-]{0,63}$",
                       options: .regularExpression) != nil
    }

    static func validHex(_ value: String, bytes: Int) -> Bool {
        value.utf8.count == bytes * 2 &&
            value.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }

    func validate() throws {
        guard Self.validDeviceId(deviceId), Self.validHost(host),
              Self.validHex(spkiSha256, bytes: 32), Self.validHex(token, bytes: 32) else {
            throw LocalDeviceError.invalidIdentity
        }
    }
}

// BLE PoP has a separate namespace from the WSS token. ESPProvision exposes
// the validated advertised name; ambiguous duplicate names are rejected by discovery.
final class ProvisioningProofKeychain {
    private let service: String

    init(service: String = "app.turquoise6409.green2444.kufr.provisioning") {
        self.service = service
    }

    private func query(deviceName: String) throws -> [String: Any] {
        guard deviceName.range(of: "^KUFR_[A-Fa-f0-9]{6}$", options: .regularExpression) != nil else {
            throw LocalDeviceError.invalidIdentity
        }
        return [kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: service,
                kSecAttrAccount as String: deviceName,
                kSecAttrSynchronizable as String: false]
    }

    func read(deviceName: String) throws -> String? {
        var request = try query(deviceName: deviceName)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data, data.count == 32,
              let proof = String(data: data, encoding: .utf8),
              LocalDeviceIdentity.validHex(proof, bytes: 16) else {
            throw LocalDeviceError.unavailableCredential
        }
        return proof
    }

    func write(_ proof: String, deviceName: String) throws {
        guard LocalDeviceIdentity.validHex(proof, bytes: 16) else {
            throw LocalDeviceError.invalidIdentity
        }
        let query = try query(deviceName: deviceName)
        let attributes: [String: Any] = [
            kSecValueData as String: Data(proof.utf8),
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        ]
        let status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            var insertion = query
            attributes.forEach { insertion[$0.key] = $0.value }
            guard SecItemAdd(insertion as CFDictionary, nil) == errSecSuccess else {
                throw LocalDeviceError.credentialWriteFailed
            }
        } else if status != errSecSuccess {
            throw LocalDeviceError.credentialWriteFailed
        }
    }

    func remove(deviceName: String) throws {
        let query = try query(deviceName: deviceName)
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw LocalDeviceError.credentialWriteFailed
        }
    }
}

enum LocalBlePairingCodec {
    static func error(_ data: Data) -> String? {
        guard data.count <= 512,
              let response = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              Set(response.keys) == Set(["v", "ok", "error"]),
              let version = response["v"] as? NSNumber,
              CFGetTypeID(version) != CFBooleanGetTypeID(), version.doubleValue == 1,
              let ok = response["ok"] as? NSNumber,
              CFGetTypeID(ok) == CFBooleanGetTypeID(), !ok.boolValue,
              let code = response["error"] as? String else { return nil }
        return ["already_paired", "pairing_unavailable", "busy", "network_not_ready",
                "pairing_requires_service", "pairing_storage_unavailable", "pairing_failed",
                "invalid_request"].contains(code) ? code : "pairing_failed"
    }

    static func decode(_ data: Data, expectedDeviceId: String) -> LocalDeviceIdentity? {
        guard data.count <= 512,
              let response = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              Set(response.keys) == Set(["v", "ok", "id", "host", "spkiSha256", "token"]),
              let version = response["v"] as? NSNumber,
              CFGetTypeID(version) != CFBooleanGetTypeID(), version.doubleValue == 1,
              let ok = response["ok"] as? NSNumber,
              CFGetTypeID(ok) == CFBooleanGetTypeID(), ok.boolValue,
              response["id"] as? String == expectedDeviceId,
              let host = response["host"] as? String,
              let pin = response["spkiSha256"] as? String,
              let token = response["token"] as? String else { return nil }
        let identity = LocalDeviceIdentity(deviceId: expectedDeviceId, host: host, spkiSha256: pin, token: token)
        guard (try? identity.validate()) != nil else { return nil }
        return identity
    }
}

// A route hint is never an identity: WSS still uses the stored token and SPKI pin.
// Only the secured BLE plugin writes this main-queue, session-bound cache.
enum LocalDeviceAddressHints {
    private static var session: UUID?
    private static var deviceId: String?
    private static var address: String?
    private static var receivedAt = Date.distantPast

    static func begin(session: UUID) {
        precondition(Thread.isMainThread)
        self.session = session
        deviceId = nil
        address = nil
    }

    static func update(session: UUID, deviceId: String, address: String?, now: Date = Date()) -> Bool {
        precondition(Thread.isMainThread)
        guard self.session == session,
              self.deviceId == nil || self.deviceId == deviceId else { return false }
        self.deviceId = deviceId
        self.address = LocalDeviceIdentity.validDeviceId(deviceId)
            ? address.flatMap { ProvisioningStatusCodec.validIpv4($0) ? $0 : nil } : nil
        receivedAt = now
        return true
    }

    static func end(session: UUID) {
        precondition(Thread.isMainThread)
        guard self.session == session else { return }
        self.session = nil
        deviceId = nil
        address = nil
    }

    static func host(deviceId: String, now: Date = Date()) -> String? {
        precondition(Thread.isMainThread)
        let age = now.timeIntervalSince(receivedAt)
        guard session != nil, self.deviceId == deviceId, age >= 0, age <= 60 else { return nil }
        return address
    }

    static func selectedHost(manual: String?, identity: LocalDeviceIdentity) -> String {
        manual ?? host(deviceId: identity.deviceId) ?? identity.host
    }
}

// Both native enrollment entry points run on the main queue. Reserve before
// asking either device to consume a token, not only before Keychain insertion.
enum LocalDeviceEnrollment {
    private static var pending = false

    static func begin() -> Bool {
        precondition(Thread.isMainThread)
        guard !pending else { return false }
        pending = true
        return true
    }

    static func end() {
        precondition(Thread.isMainThread)
        pending = false
    }
}

final class LocalDeviceKeychain {
    private let service: String
    private let account = "paired-kufr"

    init(service: String = "app.turquoise6409.green2444.kufr.local") {
        self.service = service
    }

    private var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account,
         kSecAttrSynchronizable as String: false]
    }

    func read() throws -> LocalDeviceIdentity? {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data,
              let identity = try? JSONDecoder().decode(LocalDeviceIdentity.self, from: data) else {
            throw LocalDeviceError.unavailableCredential
        }
        try identity.validate()
        return identity
    }

    func write(_ identity: LocalDeviceIdentity) throws {
        try identity.validate()
        let data = try JSONEncoder().encode(identity)
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        ]
        let status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            var insertion = query
            attributes.forEach { insertion[$0.key] = $0.value }
            guard SecItemAdd(insertion as CFDictionary, nil) == errSecSuccess else {
                throw LocalDeviceError.credentialWriteFailed
            }
        } else if status != errSecSuccess {
            throw LocalDeviceError.credentialWriteFailed
        }
    }

    // Enrollment must not replace an existing device/token if another native
    // entry point wins the race. Address updates still use write().
    func insert(_ identity: LocalDeviceIdentity) throws {
        try identity.validate()
        var insertion = query
        insertion[kSecValueData as String] = try JSONEncoder().encode(identity)
        insertion[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        guard SecItemAdd(insertion as CFDictionary, nil) == errSecSuccess else {
            throw LocalDeviceError.credentialWriteFailed
        }
    }

    func remove() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw LocalDeviceError.credentialWriteFailed
        }
    }
}

enum LocalCertificatePin {
    private struct Element {
        let tag: UInt8
        let start: Int
        let content: Int
        let end: Int
    }

    private static func element(_ bytes: [UInt8], at offset: Int, limit: Int) throws -> Element {
        guard offset >= 0, offset + 2 <= limit, limit <= bytes.count else {
            throw LocalDeviceError.invalidCertificate
        }
        let firstLength = bytes[offset + 1]
        var content = offset + 2
        var length = Int(firstLength)
        if firstLength & 0x80 != 0 {
            let count = Int(firstLength & 0x7f)
            guard count > 0, count <= 4, content + count <= limit, bytes[content] != 0 else {
                throw LocalDeviceError.invalidCertificate
            }
            length = 0
            for index in content..<(content + count) {
                length = length * 256 + Int(bytes[index])
            }
            content += count
            guard length >= 128 else { throw LocalDeviceError.invalidCertificate }
        }
        guard length <= limit - content else { throw LocalDeviceError.invalidCertificate }
        return Element(tag: bytes[offset], start: offset, content: content, end: content + length)
    }

    static func subjectPublicKeyInfo(_ certificate: Data) throws -> Data {
        // SecKey external bytes are an EC point / PKCS#1 on Apple. Firmware pins
        // the complete DER SubjectPublicKeyInfo, so extract that exact field.
        guard !certificate.isEmpty, certificate.count <= 8192 else {
            throw LocalDeviceError.invalidCertificate
        }
        let bytes = [UInt8](certificate)
        let outer = try element(bytes, at: 0, limit: bytes.count)
        guard outer.tag == 0x30, outer.end == bytes.count else {
            throw LocalDeviceError.invalidCertificate
        }
        let tbs = try element(bytes, at: outer.content, limit: outer.end)
        guard tbs.tag == 0x30 else { throw LocalDeviceError.invalidCertificate }
        var field = try element(bytes, at: tbs.content, limit: tbs.end)
        if field.tag == 0xa0 {
            field = try element(bytes, at: field.end, limit: tbs.end)
        }
        guard field.tag == 0x02 else { throw LocalDeviceError.invalidCertificate }
        for _ in 0..<4 {
            field = try element(bytes, at: field.end, limit: tbs.end)
            guard field.tag == 0x30 else { throw LocalDeviceError.invalidCertificate }
        }
        let spki = try element(bytes, at: field.end, limit: tbs.end)
        guard spki.tag == 0x30 else { throw LocalDeviceError.invalidCertificate }
        return Data(bytes[spki.start..<spki.end])
    }

    static func matches(_ certificate: Data, expected: String) -> Bool {
        guard LocalDeviceIdentity.validHex(expected, bytes: 32),
              let spki = try? subjectPublicKeyInfo(certificate) else { return false }
        let digest = SHA256.hash(data: spki).map { String(format: "%02x", $0) }.joined()
        return digest == expected
    }
}
