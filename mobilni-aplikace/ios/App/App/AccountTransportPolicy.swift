import Foundation

// Shared by the live native bridge and standalone macOS regression tests.
enum AccountTransportPolicy {
    static func credentialCommit(path: String, method: String) -> Bool {
        method == "POST" && ["/app/v1/passkeys/sign-in", "/app/v1/passkeys/reauthenticate",
            "/app/v1/enrollment/complete", "/app/v1/passkeys/complete"].contains(path)
    }

    // Caller priority flags cannot bypass the account queue. Only cancellation
    // operations with their exact selected-device route can use frozen cookies.
    static func priorityCancellation(path: String, method: String, deviceId: String, body: Data) -> Bool {
        guard method == "POST", body.count <= 4096, show(path: path, method: method, query: []),
              let object = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else { return false }
        let parts = path.split(separator: "/").map(String.init)
        guard parts.count >= 5, parts[3] == deviceId else { return false }
        if parts.count == 5 && parts[4] == "commands" {
            return ["stop", "cancel-speech", "cancel-cue"].contains(object["kind"] as? String ?? "")
        }
        return parts.count == 7 && parts[4] == "duels" && parts[6] == "cancel"
    }

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

    static func show(path: String, method: String, query: [URLQueryItem]) -> Bool {
        guard !path.contains("%"), !path.contains("\\"), !path.contains("#"), !path.contains("//"), !path.hasSuffix("/"),
              !path.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }) else { return false }
        let parts = path.split(separator: "/").map(String.init)
        guard parts.count >= 4, Array(parts.prefix(3)) == ["v1", "show", "devices"],
              !parts[3].isEmpty, parts[3].count <= 128,
              parts[3].allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_" || $0 == "-" || $0 == ".") }) else { return false }
        let suffix = Array(parts.dropFirst(4))
        if method == "GET" {
            if suffix.count == 1 && ["identities", "prophecies", "duels"].contains(suffix[0]) {
                guard query.count == 1, query[0].name == "eveningId", let evening = query[0].value else { return false }
                return !evening.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && evening.count <= 240
                    && !evening.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
            }
            return query.isEmpty && (suffix.isEmpty || (suffix.count == 2 && ["duels", "ambient-previews"].contains(suffix[0]) && UUID(uuidString: suffix[1]) != nil))
        }
        guard method == "POST", query.isEmpty else { return false }
        if suffix.count == 1 { return ["commands", "end-evening", "offline-outcomes", "duels", "ambient-previews"].contains(suffix[0]) }
        if suffix.count == 3, suffix[0] == "prophecies", suffix[2] == "reveal" {
            return !suffix[1].isEmpty && suffix[1].count <= 128 && suffix[1].allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
        }
        guard suffix.count >= 3, suffix[0] == "duels", UUID(uuidString: suffix[1]) != nil else { return false }
        return (suffix.count == 3 && ["start", "cancel", "grants"].contains(suffix[2]))
            || (suffix.count == 4 && suffix[2] == "grants" && suffix[3] == "revoke")
    }

    static func guestUsesCookies(path: String) -> Bool {
        let route = URLComponents(string: path)?.path ?? ""
        return route != "/guest/v1/evening" && !route.hasPrefix("/guest/v1/evenings/")
            && route != "/guest/v1/chronicle" && !route.hasPrefix("/guest/v1/duels/")
    }

    static func guestHeader(path: String, method: String, name: String, value: String) -> Bool {
        guard value.utf8.count <= 1024, !value.contains("\r"), !value.contains("\n") else { return false }
        let key = name.lowercased()
        if !guestUsesCookies(path: path) {
            if ["accept", "content-type"].contains(key) { return true }
            return key == "authorization" && method == "POST" && path.hasSuffix("/choices")
                && guest(path: path, method: method) && value.hasPrefix("Bearer ") && value.count == 50
                && value.dropFirst(7).allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
        }
        return ["accept", "content-type", "x-dv10-csrf", "idempotency-key", "x-dv10-consent"].contains(key)
    }

    static func device(path: String, method: String, query: [URLQueryItem]) -> Bool {
        guard !path.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }),
              !path.contains("%"), !path.contains("\\"), path.hasPrefix("/v1/") else { return false }
        if show(path: path, method: method, query: query) { return true }
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
        if route == "/guest/v1/chronicle" { return method == "GET" && components.query == nil }
        let duel = route.split(separator: "/").map(String.init)
        if duel.count >= 4, Array(duel.prefix(3)) == ["guest", "v1", "duels"] {
            return components.query == nil && UUID(uuidString: duel[3]) != nil
                && ((duel.count == 4 && method == "GET") || (duel.count == 5 && duel[4] == "choices" && method == "POST"))
        }
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
            return method == "POST" && ["revoke-access", "portrait-consents", "portrait-uploads", "portraits", "exports", "account-associations", "wizard-identity"].contains(segments[4])
        }
        return method == "POST" && segments.count == 7 && ["portraits", "generation"].contains(segments[4])
            && UUID(uuidString: segments[5]) != nil && segments[6] == "cancel"
    }

    static func account(path: String, method: String) -> Bool {
        if let parts = URLComponents(string: path), parts.scheme == nil, parts.host == nil, parts.fragment == nil,
           !parts.percentEncodedPath.contains("%"), !path.contains("\\"), path.count <= 4096 {
            if show(path: parts.path, method: method, query: parts.queryItems ?? []) { return true }
            if method == "GET", parts.path == "/v1/admin/show/chronicle", let query = parts.queryItems,
               query.count == 2, Set(query.map { $0.name }) == Set(["deviceId", "eveningId"]),
               let device = query.first(where: { $0.name == "deviceId" })?.value, !device.isEmpty, device.count <= 128,
               device.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_" || $0 == "-" || $0 == ".") }),
               let evening = query.first(where: { $0.name == "eveningId" })?.value,
               !evening.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, evening.count <= 240,
               !evening.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) { return true }
        }
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

// Native account HTTP and the complete passkey ceremony share one FIFO lease.
// This is also compiled by the Foundation-only native identity harness.
@MainActor final class AccountRequestScheduler {
    struct Lease: Equatable {
        let id = UUID()
        let epoch: UInt64
        let ceremony: Bool
    }
    enum Failure: String, LocalizedError {
        case contextChanged = "ACCOUNT_CONTEXT_CHANGED"
        case queueFull = "ACCOUNT_QUEUE_FULL"
        case committing = "ACCOUNT_CREDENTIAL_COMMITTING"
        var errorDescription: String? { rawValue }
    }
    private struct Waiter {
        let lease: Lease
        let requestId: UUID?
        let continuation: CheckedContinuation<Lease, Error>
    }
    private(set) var epoch: UInt64 = 0
    private var active: Lease?
    private var waiting: [Waiter] = []
    private var priorityActive: Lease?
    private var priorityWaiting: [Waiter] = []
    private var committing = false
    var credentialCommitActive: Bool { committing }

    func acquire(ceremony: Bool = false, logout: Bool = false, requestId: UUID? = nil) async throws -> Lease {
        try Task.checkCancellation()
        // No old-view job may acquire the next epoch while a credential response
        // is still in flight. JS treats this exact code as obsolete work.
        guard !committing else { throw Failure.contextChanged }
        if logout { invalidateQueuedScope() }
        let lease = Lease(epoch: epoch, ceremony: ceremony)
        if active == nil { active = lease; return lease }
        guard waiting.count < 32 else { throw Failure.queueFull }
        return try await withCheckedThrowingContinuation { continuation in
            let waiter = Waiter(lease: lease, requestId: requestId, continuation: continuation)
            if logout { waiting.insert(waiter, at: 0) } else { waiting.append(waiter) }
        }
    }

    func release(_ lease: Lease) {
        guard active?.id == lease.id else { return }
        active = nil; committing = false
        if !waiting.isEmpty {
            let next = waiting.removeFirst(); active = next.lease
            next.continuation.resume(returning: next.lease)
        }
    }

    func requireCurrent(_ lease: Lease) throws {
        try Task.checkCancellation()
        guard lease.epoch == epoch else { throw Failure.contextChanged }
    }

    func acquirePriority(requestId: UUID? = nil) async throws -> Lease {
        try Task.checkCancellation()
        let lease = Lease(epoch: epoch, ceremony: false)
        if priorityActive == nil { priorityActive = lease; return lease }
        guard priorityWaiting.count < 4 else { throw Failure.queueFull }
        return try await withCheckedThrowingContinuation { continuation in
            priorityWaiting.append(Waiter(lease: lease, requestId: requestId, continuation: continuation))
        }
    }

    func cancelWaiting(requestId: UUID) {
        if let index = waiting.firstIndex(where: { $0.requestId == requestId }) {
            waiting.remove(at: index).continuation.resume(throwing: AccountRequestRegistry.Failure.cancelled)
        }
        if let index = priorityWaiting.firstIndex(where: { $0.requestId == requestId }) {
            priorityWaiting.remove(at: index).continuation.resume(throwing: AccountRequestRegistry.Failure.cancelled)
        }
    }

    func releasePriority(_ lease: Lease) {
        guard priorityActive?.id == lease.id else { return }
        priorityActive = nil
        if !priorityWaiting.isEmpty {
            let next = priorityWaiting.removeFirst(); priorityActive = next.lease
            next.continuation.resume(returning: next.lease)
        }
    }

    func beginCredentialCommit(_ lease: Lease) async throws {
        guard active?.id == lease.id, lease.ceremony, !committing else { throw Failure.committing }
        try requireCurrent(lease)
        committing = true
        // Priority HTTP cannot persist cookies, so it need not hold credential
        // completion hostage. Existing priority results are fenced by this epoch;
        // new Stop requests keep using the published precommit snapshot.
        invalidateQueuedScope()
    }

    func invalidateQueuedScope() {
        epoch &+= 1
        let invalid = waiting + priorityWaiting
        waiting.removeAll(); priorityWaiting.removeAll()
        for waiter in invalid { waiter.continuation.resume(throwing: Failure.contextChanged) }
    }
}

// Shared by the bridge and actual URLSession regression tests. Priority replies
// cannot update any account jar, credential storage, Keychain or cached response.
enum AccountPriorityHTTP {
    static func configuration() -> URLSessionConfiguration {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.httpShouldSetCookies = false
        config.httpCookieAcceptPolicy = .never; config.urlCredentialStorage = nil; config.urlCache = nil
        config.timeoutIntervalForRequest = 5; config.timeoutIntervalForResource = 8
        return config
    }
}

struct AccountPrioritySessionSnapshot {
    let binding: String
    let csrf: String
    let cookieHeaders: [String: String]

    func authorize(_ original: URLRequest) -> URLRequest {
        var request = original
        request.httpShouldHandleCookies = false
        for (name, value) in cookieHeaders { request.setValue(value, forHTTPHeaderField: name) }
        request.setValue(csrf, forHTTPHeaderField: "X-DV-CSRF")
        return request
    }
}

// Register before FIFO admission, so cancellation cannot expire while a request
// waits for a long-running ceremony. No credentials enter this bounded ledger.
@MainActor final class AccountRequestRegistry {
    struct Ticket: Equatable { let requestId: UUID; let nonce = UUID() }
    enum Failure: String, LocalizedError {
        case cancelled = "ACCOUNT_REQUEST_CANCELLED"
        case duplicate = "ACCOUNT_REQUEST_DUPLICATE"
        case full = "ACCOUNT_REQUEST_CAPACITY"
        case invalidId = "ACCOUNT_REQUEST_INVALID_ID"
        var errorDescription: String? { rawValue }
    }
    private struct Entry {
        let ticket: Ticket
        let cancelWaiting: () -> Void
        var cancelWork: (() -> Void)?
        var cancelled = false
    }
    private struct Recent { let cancelled: Bool; let expires: TimeInterval }
    private let now: () -> TimeInterval
    private var active: [UUID: Entry] = [:]
    private var recent: [UUID: Recent] = [:]
    private var rejectNewUntil: TimeInterval = 0
    private let ttl: TimeInterval = 60
    private let limit = 128
    var activeCount: Int { active.count }
    var recentCount: Int { prune(); return recent.count }

    init(now: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) { self.now = now }

    func reserve(_ id: UUID, cancelWaiting: @escaping () -> Void) throws -> Ticket {
        prune()
        if let prior = recent[id] { throw prior.cancelled ? Failure.cancelled : Failure.duplicate }
        if active[id] != nil { throw Failure.duplicate }
        // If cancellation markers overflow, reject ALL new admissions until
        // their bounded race window expires rather than forgetting a cancel.
        guard now() >= rejectNewUntil else { throw Failure.cancelled }
        guard active.count < limit else { throw Failure.full }
        let ticket = Ticket(requestId: id)
        active[id] = Entry(ticket: ticket, cancelWaiting: cancelWaiting)
        return ticket
    }

    func attach(_ ticket: Ticket, cancel: @escaping () -> Void) {
        guard var entry = active[ticket.requestId], entry.ticket == ticket else { cancel(); return }
        entry.cancelWork = cancel; active[ticket.requestId] = entry
        if entry.cancelled { cancel() }
    }

    @discardableResult func cancel(_ id: UUID) -> Bool {
        prune()
        if var entry = active[id] {
            entry.cancelled = true; active[id] = entry
            entry.cancelWork?(); entry.cancelWaiting()
            return true
        }
        if let prior = recent[id] {
            if prior.cancelled { recent[id] = Recent(cancelled: true, expires: now() + ttl) }
            return prior.cancelled
        }
        if makeRoom() { recent[id] = Recent(cancelled: true, expires: now() + ttl) }
        else { rejectNewUntil = max(rejectNewUntil, now() + ttl) }
        return true
    }

    func finish(_ ticket: Ticket) {
        guard let entry = active[ticket.requestId], entry.ticket == ticket else { return }
        active.removeValue(forKey: ticket.requestId)
        prune()
        if makeRoom() { recent[ticket.requestId] = Recent(cancelled: entry.cancelled, expires: now() + ttl) }
        else if entry.cancelled { rejectNewUntil = max(rejectNewUntil, now() + ttl) }
    }

    private func prune() { let time = now(); recent = recent.filter { $0.value.expires > time } }
    private func makeRoom() -> Bool {
        if recent.count < limit { return true }
        if let oldest = recent.filter({ !$0.value.cancelled }).min(by: { $0.value.expires < $1.value.expires })?.key {
            recent.removeValue(forKey: oldest); return true
        }
        return false
    }

    nonisolated static func isCancellation(_ error: Error) -> Bool {
        error as? Failure == .cancelled || error is CancellationError || (error as? URLError)?.code == .cancelled
    }
}

// Antiforgery is issued to anonymous clients too: it authorizes the bootstrap
// POST, not an account. A priority cookie snapshot additionally needs identity.
struct AccountSessionResponse {
    let csrfToken: String
    let hasAccount: Bool
    var canPublishPriority: Bool { hasAccount && !csrfToken.isEmpty }

    init(_ object: [String: Any]) {
        if let token = object["csrfToken"] as? String, !token.isEmpty, token.utf8.count <= 4096,
           token.utf8.allSatisfy({ $0 >= 33 && $0 <= 126 }) {
            csrfToken = token
        } else { csrfToken = "" }
        hasAccount = object["account"] as? [String: Any] != nil
    }

    static func authorizeMutation(_ original: URLRequest, csrfToken: String) -> URLRequest {
        var request = original
        if request.httpMethod != "GET" { request.setValue(csrfToken, forHTTPHeaderField: "X-DV-CSRF") }
        return request
    }
}

// This snapshot authorizes only the native local-control UI. Cloud requests
// still need the server cookie and grants; fallback never publishes CSRF.
struct NativeOfflineAccountSession {
    static let maximumAge: TimeInterval = 12 * 60 * 60

    static func envelope(_ object: [String: Any], origin: String, binding: String, now: TimeInterval) -> [String: Any]? {
        guard object["offline"] as? Bool != true, object["enabled"] as? Bool == true,
              object["origin"] as? String == origin,
              let account = object["account"] as? [String: Any],
              let id = account["id"] as? String, !id.isEmpty, id.utf8.count <= 128,
              let name = account["displayName"] as? String, name.utf8.count <= 320,
              let roles = account["roles"] as? [String], !roles.isEmpty, roles.count <= 4,
              roles.allSatisfy({ ["Owner", "Administrator", "Operator", "Viewer"].contains($0) }),
              let expiry = object["expiresAt"] as? NSNumber,
              expiry.doubleValue.isFinite, expiry.doubleValue > now,
              binding.utf8.count == 64 else { return nil }
        return ["origin": origin, "binding": binding, "verifiedAt": now,
                "expiresAt": min(expiry.doubleValue, now + maximumAge),
                "account": ["id": id, "displayName": name, "roles": roles]]
    }

    static func response(_ envelope: [String: Any], origin: String, binding: String, now: TimeInterval) -> [String: Any]? {
        guard envelope["origin"] as? String == origin, envelope["binding"] as? String == binding,
              let verified = envelope["verifiedAt"] as? NSNumber,
              let expiry = envelope["expiresAt"] as? NSNumber,
              verified.doubleValue.isFinite, expiry.doubleValue.isFinite,
              now >= verified.doubleValue, now < expiry.doubleValue,
              expiry.doubleValue <= verified.doubleValue + maximumAge,
              let account = envelope["account"] as? [String: Any] else { return nil }
        let candidate: [String: Any] = ["enabled": true, "origin": origin,
            "account": account, "expiresAt": expiry]
        guard self.envelope(candidate, origin: origin, binding: binding, now: now) != nil else { return nil }
        return candidate.merging(["csrfToken": "", "offline": true]) { _, new in new }
    }

    static func permitsFallback(_ error: Error) -> Bool {
        guard let failure = error as? URLError else { return false }
        let allowed: Set<URLError.Code> = [.notConnectedToInternet, .networkConnectionLost, .timedOut,
            .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed]
        return allowed.contains(failure.code)
    }
}
