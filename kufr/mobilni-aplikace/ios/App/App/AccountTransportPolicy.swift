import Foundation

// Shared by the live native bridge and standalone macOS regression tests.
enum AccountTransportPolicy {
    static func publicOrigin(_ raw: String) -> URL? {
        guard raw.count <= 2048, raw.rangeOfCharacter(from: .whitespacesAndNewlines) == nil,
              !raw.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }),
              !raw.contains("%"), !raw.contains("$"), !raw.contains("\\"),
              let parts = URLComponents(string: raw), parts.scheme == "https", let host = parts.host, !host.isEmpty,
              parts.user == nil, parts.password == nil, parts.path.isEmpty, parts.query == nil, parts.fragment == nil,
              parts.port == nil || (1...65535).contains(parts.port!), let url = parts.url else { return nil }
        return url
    }

    // Mirrors ModelBundle safeAssetPath, including safe non-.json filenames.
    // The maintained loader verifies JSON content and hashes even when a safe
    // non-.json asset is served with a generic binary MIME by the static server.
    static func publicResourceLimit(path: String) -> Int? {
        if path == "/models/active.json" { return 128 * 1024 }
        if path == "/downloads/index.json" { return 256 * 1024 }
        let prefix = "/models/bundles/"
        guard path.hasPrefix(prefix) else { return nil }
        let parts = path.dropFirst(prefix.count).split(separator: "/", maxSplits: 1, omittingEmptySubsequences: false)
        guard parts.count == 2, parts[0].count == 64, parts[0].allSatisfy({ "0123456789abcdef".contains($0) }),
              !parts[1].isEmpty, parts[1].count <= 240,
              parts[1].allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_" || $0 == "-" || $0 == "." || $0 == "/") }),
              String(parts[1]).range(of: "^[A-Za-z0-9_-]+(?:[.][A-Za-z0-9_-]+)*(?:/[A-Za-z0-9_-]+(?:[.][A-Za-z0-9_-]+)*)*$", options: .regularExpression) != nil else { return nil }
        return parts[1] == "model-bundle.json" ? 128 * 1024 : 96 * 1024 * 1024
    }

    static func publicResourceMIME(_ contentType: String?, path: String) -> Bool {
        guard let limit = publicResourceLimit(path: path),
              let type = contentType?.split(separator: ";", maxSplits: 1).first?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() else { return false }
        if type == "application/json" || type == "application/problem+json" { return true }
        // Only the validated immutable bundle data role has this byte policy;
        // active references, manifests and the download index stay JSON-only.
        return type == "application/octet-stream" && path.hasPrefix("/models/bundles/") && limit == 96 * 1024 * 1024
    }

    static func device(path: String, method: String, query: [URLQueryItem]) -> Bool {
        guard !path.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }),
              !path.contains("%"), !path.contains("\\"), path.hasPrefix("/v1/") else { return false }
        if path == "/v1/admin/logs" {
            return method == "GET" && query.allSatisfy { ["after", "limit", "level", "traceId", "operationId"].contains($0.name) && ($0.value?.count ?? 0) <= 128 }
        }
        if path == "/v1/admin/memory/context" {
            guard method == "GET", query.count == 3, Set(query.map { $0.name }) == Set(["deviceId", "conversationId", "personalityId"]) else { return false }
            return query.allSatisfy { item in
                guard let value = item.value, !value.isEmpty else { return false }
                if item.name == "conversationId" { return UUID(uuidString: value) != nil }
                return value.count <= (item.name == "deviceId" ? 128 : 80)
                    && value.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") }
            }
        }
        guard query.isEmpty else { return false }
        if path == "/v1/admin/memory/guest" { return method == "PUT" }
        let memory = path.split(separator: "/").map(String.init)
        if memory.count == 6, Array(memory.prefix(4)) == ["v1", "admin", "memory", "scopes"], method == "DELETE" {
            if ["conversation", "evening"].contains(memory[4]) { return UUID(uuidString: memory[5]) != nil }
            return memory[4] == "guest" && !memory[5].isEmpty && memory[5].count <= 80
                && memory[5].allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") }
        }
        if path == "/v1/admin/modes" || path == "/v1/diagnostics" || path == "/v1/evening-memory" { return method == "GET" }
        if path == "/v1/evenings" { return ["GET", "POST"].contains(method) }
        if path == "/v1/conversations" { return method == "POST" }
        let parts = path.split(separator: "/").map(String.init)
        guard parts.count >= 3, UUID(uuidString: parts[2]) != nil else { return false }
        if parts[1] == "audio" { return parts.count == 3 && method == "GET" }
        if parts[1] == "operations" { return parts.count == 3 && ["GET", "DELETE"].contains(method) }
        if parts[1] == "evenings" { return parts.count == 4 && parts[3] == "active" && method == "PUT" }
        guard parts[1] == "conversations" else { return false }
        if parts.count == 3 { return method == "DELETE" }
        if parts.count == 4 {
            if parts[3] == "image" && method == "DELETE" { return true }
            if parts[3] == "costume-context" { return method == "PUT" }
            return method == "POST" && ["realtime-live", "gpt-live", "image"].contains(parts[3])
        }
        if parts.count == 6 && method == "GET" && ["realtime-live", "gpt-live"].contains(parts[3])
            && UUID(uuidString: parts[4]) != nil && parts[5] == "audio-stream" { return true }
        return parts.count == 6 && method == "POST" && ["realtime-live", "gpt-live"].contains(parts[3])
            && UUID(uuidString: parts[4]) != nil && ["audio", "played", "playback", "availability"].contains(parts[5])
    }
    static func guest(path: String, method: String) -> Bool {
        guard path.count <= 4096, ["GET", "POST", "DELETE"].contains(method), !path.split(separator: "?", maxSplits: 1)[0].contains("%"),
              !path.contains(".."), !path.contains("\\"), !path.contains("#"),
              let components = URLComponents(string: path), components.scheme == nil, components.host == nil else { return false }
        let route = components.path
        if method == "POST", ["/guest/v1/bootstrap", "/guest/v1/claim", "/guest/v1/recover", "/guest/v1/share-claim"].contains(route) { return components.query == nil }
        if route == "/guest/v1/evening" {
            return method == "GET" && components.queryItems?.contains(where: { !["cursor", "limit"].contains($0.name) || ($0.value?.count ?? 0) > 256 }) != true
        }
        if route.hasPrefix("/guest/v1/evenings/") {
            let pieces = route.split(separator: "/").map(String.init)
            return method == "GET" && components.query == nil && pieces.count == 10
                && pieces[2] == "evenings" && pieces[4] == "keepsakes" && pieces[6] == "editions" && pieces[8] == "assets"
                && [3, 5, 7, 9].allSatisfy { UUID(uuidString: pieces[$0]) != nil }
        }
        let allowedQuery = Set(["editionId", "view"])
        if components.queryItems?.contains(where: { !allowedQuery.contains($0.name) || ($0.value?.count ?? 0) > 128 }) == true { return false }
        let segments = route.split(separator: "/").map(String.init)
        guard segments.count >= 4, segments[0] == "guest", segments[1] == "v1",
              UUID(uuidString: segments[3]) != nil else { return false }
        if segments[2] == "deletions" { return method == "GET" && segments.count == 4 }
        guard segments[2] == "keepsakes" else { return false }
        if method == "GET" { return segments.count == 4 || (segments.count == 5 && segments[4] == "history") || (segments.count == 6 && segments[4] == "assets" && UUID(uuidString: segments[5]) != nil) }
        if segments.count == 4 { return method == "DELETE" }
        if segments.count == 5 {
            if ["recovery-links", "share-links"].contains(segments[4]) { return method == "POST" || method == "DELETE" }
            if segments[4] == "portrait" { return method == "DELETE" }
            return method == "POST" && ["revoke-access", "portrait-consents", "portrait-uploads", "portraits", "exports", "account-associations"].contains(segments[4])
        }
        return method == "POST" && segments.count == 7 && ["portraits", "generation"].contains(segments[4])
            && UUID(uuidString: segments[5]) != nil && segments[6] == "cancel"
    }

    static func account(path: String, method: String) -> Bool {
        if method == "GET", let query = URLComponents(string: path), query.scheme == nil, query.host == nil,
           query.path == "/app/v1/admin/operations/logs", path.count <= 1024 {
            return query.queryItems?.contains(where: { !["after", "level"].contains($0.name) || ($0.value?.count ?? 0) > 32 }) != true
        }
        guard !path.contains("%"), !path.contains("?"), !path.contains("#"), !path.contains("\\"),
              !path.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }) else { return false }
        if path == "/v1/evenings" { return ["GET", "POST"].contains(method) }
        let evening = path.split(separator: "/").map(String.init)
        if evening.count == 4, evening[0] == "v1", evening[1] == "evenings", UUID(uuidString: evening[2]) != nil, evening[3] == "active" { return method == "PUT" }
        let exact: [String: Set<String>] = [
            "/app/v1/session": ["GET", "DELETE"], "/app/v1/account": ["GET"],
            "/app/v1/passkeys/request-options": ["POST"], "/app/v1/passkeys/sign-in": ["POST"],
            "/app/v1/passkeys/reauthentication-options": ["POST"], "/app/v1/passkeys/reauthenticate": ["POST"],
            "/app/v1/enrollment/options": ["POST"], "/app/v1/enrollment/complete": ["POST"],
            "/app/v1/passkeys/options": ["POST"], "/app/v1/passkeys/complete": ["POST"],
            "/app/v1/admin/operations/status": ["GET"], "/app/v1/admin/operations/modes": ["PUT"],
            "/app/v1/admin/model-releases": ["GET"], "/app/v1/admin/model-releases/active": ["POST"],
            "/app/v1/admin/users": ["GET"], "/app/v1/admin/invitations": ["POST"], "/app/v1/admin/grants": ["POST"]]
        if exact[path]?.contains(method) == true { return true }
        let operations = path.split(separator: "/").map(String.init)
        if method == "POST", Array(operations.prefix(5)) == ["app", "v1", "admin", "operations", "keepsakes"],
           operations.count >= 7, UUID(uuidString: operations[5]) != nil {
            return (operations.count == 7 && operations[6] == "revoke-shares")
                || (operations.count == 9 && operations[6] == "jobs" && UUID(uuidString: operations[7]) != nil && operations[8] == "cancel")
        }
        let archive = path.split(separator: "/").map(String.init)
        if archive.count == 5, Array(archive.prefix(3)) == ["app", "v1", "workshop"], archive[4] == "engines" {
            return method == "GET" && !archive[3].isEmpty && archive[3].count <= 96
                && archive[3].allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") }
        }
        if Array(archive.prefix(4)) == ["app", "v1", "archive", "associations"] {
            if archive.count == 4 { return ["GET", "POST"].contains(method) }
            guard archive.count >= 5, UUID(uuidString: archive[4]) != nil else { return false }
            if archive.count == 5 { return ["GET", "DELETE"].contains(method) }
            return archive.count == 7 && method == "GET" && archive[5] == "assets" && UUID(uuidString: archive[6]) != nil
        }
        let device = path.split(separator: "/").map(String.init)
        if device.count == 5, Array(device.prefix(3)) == ["app", "v1", "devices"], device[4] == "association" {
            return ["POST", "DELETE"].contains(method) && device[3].count <= 128
                && device[3].allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
        }
        let twin = path.split(separator: "/").map(String.init)
        if twin.count >= 3, Array(twin.prefix(3)) == ["v1", "twin", "instances"] {
            if twin.count == 3 { return method == "GET" }
            guard !twin[3].isEmpty, twin[3].count <= 96, twin[3].allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" || $0 == ".") }) else { return false }
            if twin.count == 4 { return method == "GET" }
            if twin.count == 5 {
                return (twin[4] == "recording" && method == "GET") || (twin[4] == "commands" && method == "POST")
                    || (twin[4] == "settings" && ["GET", "PUT"].contains(method))
            }
            return twin.count == 6 && twin[4] == "media"
                && ((method == "GET" && ["state", "output"].contains(twin[5]))
                    || (method == "POST" && ["start", "cancel", "input", "photo", "played"].contains(twin[5])))
        }
        if path.hasPrefix("/app/v1/passkeys/"), ["DELETE", "PUT"].contains(method) {
            let id = String(path.dropFirst("/app/v1/passkeys/".count))
            return !id.isEmpty && id.count <= 2048 && id.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
        }
        let prefixes: [(String, String)] = [("/app/v1/sessions/", "DELETE"), ("/app/v1/installations/", "PUT"),
            ("/app/v1/admin/users/", "PUT"), ("/app/v1/admin/grants/", "DELETE")]
        return prefixes.contains { prefix, expected in
            guard method == expected, path.hasPrefix(prefix) else { return false }
            let id = String(path.dropFirst(prefix.count))
            return !id.isEmpty && id.count <= 128 && id.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
        }
    }
}
