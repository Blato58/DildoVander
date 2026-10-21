import Foundation
import CoreFoundation

// Foundation-only codec shares the exact wire bound with the firmware. No
// CoreBluetooth instance or permission prompt is needed to validate a reply.
enum CustomerBleProtocol {
    static let maximumBytes = 512
    static func decode(_ bytes: Data) -> [String: Any]? {
        guard !bytes.isEmpty, bytes.count <= maximumBytes,
              let object = try? JSONSerialization.jsonObject(with: bytes) as? [String: Any],
              let version = object["v"] as? NSNumber, CFGetTypeID(version) != CFBooleanGetTypeID(), version.intValue == 1, version.doubleValue == 1,
              let device = object["deviceId"] as? String, !device.isEmpty, device.utf8.count <= 128,
              let boot = object["bootId"] as? String, UUID(uuidString: boot) != nil,
              let challenge = object["challenge"] as? String,
              challenge.isEmpty || UUID(uuidString: challenge) != nil,
              let state = object["state"] as? String,
              ["idle", "starting", "running", "recovering", "completed", "failed"].contains(state),
              let capabilities = object["capabilities"] as? [String: Any],
              let supported = capabilities["customerStart"] as? NSNumber,
              CFGetTypeID(supported) != CFBooleanGetTypeID(),
              supported.doubleValue == 0 || supported.doubleValue == 1 else { return nil }
        let fields: Set<String> = ["v", "deviceId", "bootId", "challenge", "state", "capabilities", "operationId", "sessionId", "error"]
        guard Set(object.keys).isSubset(of: fields), Set(capabilities.keys) == Set(["customerStart"]) else { return nil }
        for key in ["operationId", "sessionId", "error"] {
            if object[key] != nil && !(object[key] is String) { return nil }
        }
        if let operation = object["operationId"] as? String, UUID(uuidString: operation) == nil { return nil }
        if let session = object["sessionId"] as? String, UUID(uuidString: session) == nil { return nil }
        if let error = object["error"] as? String, error.utf8.count > 96 { return nil }
        return object
    }
}
