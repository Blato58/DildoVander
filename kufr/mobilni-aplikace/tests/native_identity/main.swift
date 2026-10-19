import Foundation

func require(_ value: Bool, _ description: String) { if !value { fatalError(description) } }
let id = "7fc470e2-e25e-46a2-b51b-97d50275ae77"
let publicHash = String(repeating: "a", count: 64)
require(AccountTransportPolicy.publicOrigin("https://public.example:8443")?.host == "public.example", "configured public HTTPS origin unavailable without RP")
for invalidOrigin in ["http://public.example", "https://user:secret@public.example", "https://public.example/path", "https://public.example?query", "https://public.example#fragment", "https://%70ublic.example", "https://public.example ", "https://$(KUFR_ACCOUNT_ORIGIN)"] {
    require(AccountTransportPolicy.publicOrigin(invalidOrigin) == nil, "unsafe public origin accepted")
}
require(AccountTransportPolicy.publicResourceLimit(path: "/models/active.json") == 131072, "public active reference limit mismatch")
require(AccountTransportPolicy.publicResourceLimit(path: "/downloads/index.json") == 262144, "public download index limit mismatch")
require(AccountTransportPolicy.publicResourceLimit(path: "/models/bundles/\(publicHash)/model-bundle.json") == 131072, "public manifest limit mismatch")
require(AccountTransportPolicy.publicResourceLimit(path: "/models/bundles/\(publicHash)/nested/model.data") == 100663296, "safe JSON-kind asset path or 96MiB limit mismatch")
require(AccountTransportPolicy.publicResourceLimit(path: "/models/bundles/\(publicHash)/" + String(repeating: "a", count: 240)) != nil, "maximum safe asset path unavailable")
require(AccountTransportPolicy.publicResourceLimit(path: "/models/bundles/\(publicHash)/model.json\n") == nil, "trailing control character accepted")
require(AccountTransportPolicy.publicOrigin("https://public.example\u{0}") == nil, "public origin control character accepted")
for invalidResource in ["/app/v1/session", "/guest/v1/evening", "/models/active.json?x=1", "/downloads/file.zip", "https://public.example/models/active.json", "//public.example/models/active.json", "/models/bundles/\(publicHash)/../model.json", "/models/bundles/\(publicHash)/%2e%2e/model.json", "/models/bundles/\(publicHash)/model.json#fragment", "/models/bundles/\(publicHash)/nested\\model.json", "/models/bundles/\(publicHash)/model..json", "/models/bundles/\(publicHash.uppercased())/model.json", "/models/bundles/\(publicHash)/" + String(repeating: "a", count: 241)] {
    require(AccountTransportPolicy.publicResourceLimit(path: invalidResource) == nil, "unscoped or unsafe public resource accepted")
}
require(AccountTransportPolicy.publicResourceMIME("application/json; charset=utf-8", path: "/models/active.json"), "JSON response MIME rejected")
require(AccountTransportPolicy.publicResourceMIME("application/problem+json", path: "/downloads/index.json"), "bounded JSON problem response rejected")
let publicDataPath = "/models/bundles/\(publicHash)/nested/model.data"
require(AccountTransportPolicy.publicResourceMIME("application/octet-stream", path: publicDataPath), "safe non-.json bundle data MIME rejected")
for metadataPath in ["/models/active.json", "/downloads/index.json", "/models/bundles/\(publicHash)/model-bundle.json"] {
    require(!AccountTransportPolicy.publicResourceMIME("application/octet-stream", path: metadataPath), "metadata accepted binary MIME")
}
require(!AccountTransportPolicy.publicResourceMIME("application/octet-stream", path: "/models/bundles/\(publicHash)/../model.data"), "unsafe binary asset path accepted")
require(!AccountTransportPolicy.publicResourceMIME("application/json", path: "/app/v1/session"), "unscoped MIME path accepted")
for invalidMIME in [nil, "text/html", "application/javascript", "application/octet-stream", "image/svg+xml"] as [String?] {
    require(!AccountTransportPolicy.publicResourceMIME(invalidMIME, path: "/models/active.json"), "non-JSON public metadata accepted")
    if invalidMIME != "application/octet-stream" {
        require(!AccountTransportPolicy.publicResourceMIME(invalidMIME, path: publicDataPath), "HTML/script or unsupported bundle MIME accepted")
    }
}
require(AccountTransportPolicy.device(path: "/v1/conversations/\(id)/realtime-live", method: "POST", query: []), "native account voice admission unavailable")
require(AccountTransportPolicy.device(path: "/v1/conversations/\(id)/gpt-live/\(id)/audio-stream", method: "GET", query: []), "persistent phone socket admission unavailable")
require(!AccountTransportPolicy.device(path: "/v1/conversations/\(id)/gpt-live/\(id)/audio-stream", method: "POST", query: []), "socket method broadened")
require(AccountTransportPolicy.device(path: "/v1/conversations/\(id)/image", method: "DELETE", query: []), "explicit image revocation unavailable")
require(!AccountTransportPolicy.device(path: "/v1/conversations/\(id)/turns", method: "POST", query: []), "retired dialog route exposed")
let memoryContext = [URLQueryItem(name: "deviceId", value: "device-a"), URLQueryItem(name: "conversationId", value: id), URLQueryItem(name: "personalityId", value: "ollivander-trunk")]
require(AccountTransportPolicy.device(path: "/v1/admin/memory/context", method: "GET", query: memoryContext), "scoped memory context unavailable")
require(!AccountTransportPolicy.device(path: "/v1/admin/memory/context", method: "GET", query: memoryContext + [URLQueryItem(name: "token", value: "forbidden")]), "memory query credential accepted")
require(AccountTransportPolicy.device(path: "/v1/admin/memory/guest", method: "PUT", query: []), "explicit admin guest permission update unavailable")
require(AccountTransportPolicy.device(path: "/v1/admin/memory/scopes/conversation/\(id)", method: "DELETE", query: []), "confirmed scoped memory deletion unavailable")
require(!AccountTransportPolicy.device(path: "/v1/admin/memory/scopes/arbitrary/\(id)", method: "DELETE", query: []), "memory scope broadened")
require(AccountTransportPolicy.account(path: "/app/v1/workshop/case.s3/engines", method: "GET"), "scoped canonical engine metadata unavailable")
require(!AccountTransportPolicy.account(path: "/app/v1/workshop/case.s3/engines", method: "PUT"), "read-only engine metadata became mutable")
require(!AccountTransportPolicy.device(path: "/v1/device-access/token", method: "POST", query: []), "native account flow accepted bearer exchange")
require(!AccountTransportPolicy.device(path: "/v1/admin/modes", method: "PUT", query: []), "advanced profile bypassed application confirmation")
require(AccountTransportPolicy.account(path: "/app/v1/session", method: "GET"), "session unavailable")
require(AccountTransportPolicy.account(path: "/app/v1/passkeys/credential_name", method: "PUT"), "own passkey naming unavailable")
require(!AccountTransportPolicy.account(path: "/app/v1/passkeys/credential_name", method: "PATCH"), "unsupported credential method accepted")
require(AccountTransportPolicy.account(path: "/app/v1/enrollment/complete", method: "POST"), "enrollment unavailable")
require(AccountTransportPolicy.account(path: "/app/v1/passkeys/reauthenticate", method: "POST"), "step-up unavailable")
require(AccountTransportPolicy.account(path: "/app/v1/admin/operations/logs?level=warning&after=12", method: "GET"), "bounded logs unavailable")
require(!AccountTransportPolicy.account(path: "/app/v1/admin/operations/logs?url=https://evil.invalid", method: "GET"), "log proxy broadened")
require(AccountTransportPolicy.account(path: "/app/v1/archive/associations/\(id)/assets/\(id)", method: "GET"), "account-linked portrait result unavailable")
require(AccountTransportPolicy.account(path: "/app/v1/devices/kufr-1/association", method: "POST"), "association unavailable")
require(!AccountTransportPolicy.account(path: "/v1/device-access/token", method: "POST"), "account bridge accepted device credentials")
require(!AccountTransportPolicy.account(path: "/guest/v1/bootstrap", method: "POST"), "account bridge accepted guest credentials")
require(!AccountTransportPolicy.account(path: "https://evil.invalid/app/v1/session", method: "GET"), "absolute URL accepted")
require(!AccountTransportPolicy.account(path: "/app/v1/admin/users/../invitations", method: "POST"), "traversal accepted")
require(AccountTransportPolicy.account(path: "/v1/twin/instances/simulation/media/photo", method: "POST"), "simulation photo unavailable")
require(AccountTransportPolicy.account(path: "/v1/twin/instances/case.s3", method: "GET"), "canonical dotted Twin source unavailable")
require(AccountTransportPolicy.account(path: "/v1/twin/instances/case..s3/media/start", method: "POST"), "embedded source dots rejected")
require(AccountTransportPolicy.account(path: "/v1/twin/instances/" + String(repeating: "a", count: 96), method: "GET"), "maximum Twin source unavailable")
require(!AccountTransportPolicy.account(path: "/v1/twin/instances/" + String(repeating: "a", count: 97), method: "GET"), "overlong Twin source accepted")
require(!AccountTransportPolicy.account(path: "/v1/twin/instances/case:s3", method: "GET"), "noncanonical colon source accepted")
require(!AccountTransportPolicy.account(path: "/v1/twin/instances/..", method: "GET"), "dot segment source accepted")
require(!AccountTransportPolicy.account(path: "/v1/twin/ingest/simulation", method: "POST"), "account bridge accepted source ingest")
require(!AccountTransportPolicy.account(path: "/v1/twin/instances/simulation/media/output", method: "POST"), "media method broadened")
require(AccountTransportPolicy.account(path: "/v1/evenings", method: "GET"), "account evening list unavailable")
require(AccountTransportPolicy.account(path: "/v1/evenings", method: "POST"), "account evening create unavailable")
require(AccountTransportPolicy.account(path: "/v1/evenings/\(id)/active", method: "PUT"), "account evening activation unavailable")
require(!AccountTransportPolicy.account(path: "/v1/evenings/\(id)/active", method: "DELETE"), "evening method broadened")
require(!AccountTransportPolicy.account(path: "/v1/evening-memory/\(id)", method: "DELETE"), "shared panel gained destructive memory clear")
require(AccountTransportPolicy.guest(path: "/guest/v1/bootstrap", method: "POST"), "guest bootstrap unavailable")
require(AccountTransportPolicy.guest(path: "/guest/v1/evening?cursor=opaque&limit=10", method: "GET"), "public pagination unavailable")
require(AccountTransportPolicy.guest(path: "/guest/v1/evenings/\(id)/keepsakes/\(id)/editions/\(id)/assets/\(id)", method: "GET"), "public asset unavailable")
require(AccountTransportPolicy.guest(path: "/guest/v1/keepsakes/\(id)?view=share&editionId=\(id)", method: "GET"), "shared edition unavailable")
require(AccountTransportPolicy.guest(path: "/guest/v1/keepsakes/\(id)/portrait-uploads", method: "POST"), "portrait upload unavailable")
require(AccountTransportPolicy.guest(path: "/guest/v1/keepsakes/\(id)/account-associations", method: "POST"), "explicit account link proof unavailable")
require(AccountTransportPolicy.guest(path: "/guest/v1/keepsakes/\(id)/portrait", method: "DELETE"), "portrait deletion unavailable")
require(!AccountTransportPolicy.guest(path: "/guest/v1/keepsakes/\(id)/portrait-uploads", method: "DELETE"), "portrait upload method broadened")
require(!AccountTransportPolicy.guest(path: "/app/v1/session", method: "GET"), "guest bridge accepted account path")
require(!AccountTransportPolicy.guest(path: "/guest/v1/evenings/anything", method: "GET"), "unscoped public path accepted")
print("PASS native account and guest route/method policy, public pagination, share edition, portrait upload/deletion, simulation media boundaries")

let show = "/v1/show/devices/case.s3"
require(AccountTransportPolicy.account(path: show, method: "GET"), "account Show state unavailable")
require(AccountTransportPolicy.device(path: show + "/commands", method: "POST", query: []), "bound native Show command unavailable")
for suffix in ["commands", "end-evening", "offline-outcomes", "duels", "prophecies/" + id + "/reveal", "duels/" + id + "/start", "duels/" + id + "/cancel", "duels/" + id + "/grants", "duels/" + id + "/grants/revoke"] {
    require(AccountTransportPolicy.account(path: show + "/" + suffix, method: "POST"), "Show POST missing " + suffix)
}
for suffix in ["identities", "prophecies", "duels"] {
    require(AccountTransportPolicy.account(path: show + "/" + suffix + "?eveningId=" + id, method: "GET"), "Show scoped list missing")
    require(!AccountTransportPolicy.account(path: show + "/" + suffix + "?eveningId=" + id + "&deviceId=other", method: "GET"), "Show extra query accepted")
}
for invalid in [show + "/commands?deviceId=other", "/v1/show/device-cues", "/v1/show/offline-outcomes", "/v1/show/devices/../commands", show + "/duels/" + id + "/arbitrary"] {
    require(!AccountTransportPolicy.account(path: invalid, method: "POST"), "Show proxy broadened")
}
require(!AccountTransportPolicy.account(path: show + "/commands", method: "DELETE"), "Show method broadened")
require(AccountTransportPolicy.guest(path: "/guest/v1/chronicle", method: "GET"), "public chronicle unavailable")
require(AccountTransportPolicy.guest(path: "/guest/v1/duels/" + id, method: "GET"), "public duel unavailable")
let choice = "/guest/v1/duels/" + id + "/choices"
require(AccountTransportPolicy.guest(path: choice, method: "POST"), "guest choice unavailable")
require(!AccountTransportPolicy.guestUsesCookies(path: choice), "duel choice used owner cookie jar")
require(!AccountTransportPolicy.guestUsesCookies(path: "/guest/v1/chronicle"), "chronicle used owner cookies")
require(AccountTransportPolicy.guestUsesCookies(path: "/guest/v1/keepsakes/" + id), "owner cookie isolation lost")
require(AccountTransportPolicy.guestHeader(path: choice, method: "POST", name: "Authorization", value: "Bearer " + String(repeating: "a", count: 43)), "scoped duel grant unavailable")
require(!AccountTransportPolicy.guestHeader(path: choice, method: "POST", name: "Cookie", value: "secret"), "guest cookie forwarding allowed")
require(!AccountTransportPolicy.guestHeader(path: choice, method: "POST", name: "X-DV10-CSRF", value: "owner"), "guest choice accepted owner CSRF")
require(!AccountTransportPolicy.guestHeader(path: "/guest/v1/keepsakes/" + id, method: "GET", name: "Authorization", value: "Bearer " + String(repeating: "a", count: 43)), "duel grant crossed owner boundary")
require(!AccountTransportPolicy.guestHeader(path: choice, method: "POST", name: "Authorization", value: "Bearer short"), "unbounded-purpose bearer accepted")
require(AccountTransportPolicy.guest(path: "/guest/v1/keepsakes/" + id + "/wizard-identity", method: "POST"), "owner legacy identity unavailable")
print("PASS Show native policy and isolated guest duel grants (source/macOS execution required)")

require(AccountTransportPolicy.account(path: show + "/duels?eveningId=Legacy%20evening", method: "GET"), "opaque legacy Show evening rejected")
require(!AccountTransportPolicy.account(path: show + "/duels?eveningId=%0a", method: "GET"), "control character evening accepted")
require(AccountTransportPolicy.account(path: "/v1/admin/show/chronicle?eveningId=Legacy%20evening&deviceId=case.s3", method: "GET"), "bounded legacy chronicle unavailable")
require(!AccountTransportPolicy.account(path: "/v1/admin/show/chronicle?eveningId=x&deviceId=case.s3&deviceId=other", method: "GET"), "duplicate chronicle keys accepted")

// Exercise the production scheduler with real, deliberately suspended URLSession
// requests. URLProtocol keeps this offline and lets each response settle exactly.
private final class DelayedIdentityProtocol: URLProtocol, @unchecked Sendable {
    @MainActor static var received: [String: DelayedIdentityProtocol] = [:]
    override class func canInit(with request: URLRequest) -> Bool { request.url?.host == "native-account.invalid" }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() { Task { @MainActor in Self.received[self.request.url!.path] = self } }
    override func stopLoading() {}
    @MainActor func complete(cookie: String? = nil, status: Int = 200, cookieStorage: HTTPCookieStorage? = nil) {
        var headers = ["Content-Type": "application/json"]
        if let cookie = cookie { headers["Set-Cookie"] = cookie }
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: headers)!
        // A custom URLProtocol replaces Apple's HTTP loader; forwarding a
        // response does not model its cookie-store side effect. Apply response
        // cookies explicitly to the session's ACTUAL configured jar. Priority
        // uses the production nil jar, so even a hostile response has no store.
        if cookie != nil, let storage = cookieStorage {
            let parsed = HTTPCookie.cookies(withResponseHeaderFields: headers, for: request.url!)
            require(!parsed.isEmpty, "synthetic ordinary HTTP Set-Cookie was not parsed")
            storage.setCookies(parsed, for: request.url!, mainDocumentURL: request.url!)
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        if status != 204 { client?.urlProtocol(self, didLoad: Data("{}".utf8)) }
        client?.urlProtocolDidFinishLoading(self)
    }
}

@MainActor private func eventually(_ condition: () -> Bool) async {
    let deadline = ProcessInfo.processInfo.systemUptime + 4
    while ProcessInfo.processInfo.systemUptime < deadline {
        if condition() { return }
        try? await Task.sleep(nanoseconds: 1_000_000)
    }
    fatalError("Native account delayed request did not reach its expected state")
}

@MainActor private func nativeAccountSchedulingTests() async throws {
    let normalConfig = URLSessionConfiguration.ephemeral
    normalConfig.httpCookieAcceptPolicy = .always; normalConfig.httpShouldSetCookies = true
    normalConfig.protocolClasses = [DelayedIdentityProtocol.self]
    let normal = URLSession(configuration: normalConfig)
    let priorityConfig = AccountPriorityHTTP.configuration()
    require(priorityConfig.httpCookieStorage == nil && priorityConfig.urlCredentialStorage == nil && priorityConfig.urlCache == nil,
        "priority transport shares account cookie/credential/cache storage")
    require(!priorityConfig.httpShouldSetCookies && priorityConfig.httpCookieAcceptPolicy == .never,
        "priority response can accept Set-Cookie")
    priorityConfig.protocolClasses = [DelayedIdentityProtocol.self]
    let urgent = URLSession(configuration: priorityConfig)
    require(urgent.configuration.httpCookieStorage == nil && !urgent.configuration.httpShouldSetCookies
        && urgent.configuration.httpCookieAcceptPolicy == .never, "priority URLSession did not retain its cookie-isolation configuration")
    defer { normal.invalidateAndCancel(); urgent.invalidateAndCancel() }
    let gate = AccountRequestScheduler()
    let accountURL = URL(string: "https://native-account.invalid")!
    @MainActor func accountCookie() -> String? {
        normal.configuration.httpCookieStorage?.cookies(for: accountURL)?.first(where: { $0.name == "__Host-dv-account" })?.value
    }
    var entered: [String] = []
    var attempted = Set<String>()
    var responseStatus: [String: Int] = [:]
    @MainActor func wire(_ name: String, priority: Bool = false) async throws {
        var request = URLRequest(url: URL(string: "https://native-account.invalid/" + name)!)
        request.httpMethod = name.hasPrefix("mutation") ? "POST" : "GET"
        if priority {
            request.httpMethod = "POST"
            let snapshot = AccountPrioritySessionSnapshot(binding: "published-existing-session", csrf: "frozen-csrf",
                cookieHeaders: ["Cookie": "__Host-dv-account=frozen-existing-session"])
            request = snapshot.authorize(request)
        }
        try Task.checkCancellation()
        let (_, response) = try await (priority ? urgent : normal).data(for: request)
        try Task.checkCancellation()
        responseStatus[name] = (response as? HTTPURLResponse)?.statusCode
    }
    @MainActor func ordinary(_ name: String, logout: Bool = false, requestId: UUID? = nil) async throws {
        attempted.insert(name)
        let lease = try await gate.acquire(logout: logout, requestId: requestId)
        defer { gate.release(lease) }
        try gate.requireCurrent(lease)
        entered.append(name)
        try await wire(name)
    }
    // Admin mount's concurrent refresh + operations + models wait FIFO instead
    // of rejecting a healthy session as "requestActive".
    let refresh = Task { @MainActor in try await ordinary("refresh") }
    await eventually { DelayedIdentityProtocol.received["/refresh"] != nil }
    let operations = Task { @MainActor in try await ordinary("operations") }
    await eventually { attempted.contains("operations") }
    let mutation = Task { @MainActor in try await ordinary("mutation-model") }
    await eventually { attempted.contains("mutation-model") }
    require(entered == ["refresh"], "overlapping requests escaped ordinary serialization")
    DelayedIdentityProtocol.received["/refresh"]!.complete(cookie: "__Host-dv-account=renewed; Path=/; Secure", cookieStorage: normal.configuration.httpCookieStorage)
    try await refresh.value
    require(accountCookie() == "renewed", "ordinary HTTP fixture did not commit its initial renewed account cookie")
    await eventually { DelayedIdentityProtocol.received["/operations"] != nil }
    require(entered == ["refresh", "operations"], "cookie renewal dropped or reordered queued account reads")
    DelayedIdentityProtocol.received["/operations"]!.complete(); try await operations.value
    await eventually { DelayedIdentityProtocol.received["/mutation-model"] != nil }
    DelayedIdentityProtocol.received["/mutation-model"]!.complete(); try await mutation.value
    require(entered == ["refresh", "operations", "mutation-model"], "ordinary mutation order changed")
    let cancelled = Task { @MainActor in try await ordinary("cancelled-read") }
    await eventually { DelayedIdentityProtocol.received["/cancelled-read"] != nil }
    let afterCancelled = Task { @MainActor in try await ordinary("after-cancelled") }
    await eventually { attempted.contains("after-cancelled") }
    cancelled.cancel()
    do { try await cancelled.value; fatalError("cancelled HTTP request unexpectedly succeeded") }
    catch { }
    await eventually { DelayedIdentityProtocol.received["/after-cancelled"] != nil }
    DelayedIdentityProtocol.received["/after-cancelled"]!.complete(); try await afterCancelled.value


    let body = try JSONSerialization.data(withJSONObject: ["kind": "stop", "commandId": "stop-test", "expectedStateRevision": 0])
    require(AccountTransportPolicy.priorityCancellation(path: show + "/commands", method: "POST", deviceId: "case.s3", body: body),
        "retained Director Stop cannot enter priority lane")
    for kind in ["quick-finale", "override", "guest-leave", "mood"] {
        let data = try JSONSerialization.data(withJSONObject: ["kind": kind])
        require(!AccountTransportPolicy.priorityCancellation(path: show + "/commands", method: "POST", deviceId: "case.s3", body: data),
            "ordinary operation escaped ceremony gate as priority")
    }
    require(!AccountTransportPolicy.priorityCancellation(path: show + "/commands", method: "POST", deviceId: "other", body: body),
        "priority Stop borrowed a different device scope")
    require(!AccountTransportPolicy.priorityCancellation(path: show + "/commands", method: "POST", deviceId: "case.s3", body: Data(repeating: 0, count: 4097)),
        "priority body limit missing")

    // Reauth/add return 204 without changing the server cookie. Stop stays live
    // even after completion while the UI's fresh /session query is delayed.
    let sameAccount = try await gate.acquire(ceremony: true)
    try await gate.beginCredentialCommit(sameAccount)
    let sameAccountHTTP = Task { @MainActor in try await wire("same-account-reauth") }
    await eventually { DelayedIdentityProtocol.received["/same-account-reauth"] != nil }
    let cookieBeforeReauth = normal.configuration.httpCookieStorage?.cookies(for: URL(string: "https://native-account.invalid")!)?.first(where: { $0.name == "__Host-dv-account" })?.value
    DelayedIdentityProtocol.received["/same-account-reauth"]!.complete(status: 204)
    try await sameAccountHTTP.value
    let cookieAfterReauth = normal.configuration.httpCookieStorage?.cookies(for: URL(string: "https://native-account.invalid")!)?.first(where: { $0.name == "__Host-dv-account" })?.value
    require(cookieBeforeReauth == "renewed" && cookieAfterReauth == "renewed", "same-account 204 lost or changed its authenticated cookie")
    gate.release(sameAccount)
    let freshSession = Task { @MainActor in try await ordinary("fresh-session-after-reauth") }
    await eventually { DelayedIdentityProtocol.received["/fresh-session-after-reauth"] != nil }
    let retainedStop = Task { @MainActor in
        let lease = try await gate.acquirePriority(); defer { gate.releasePriority(lease) }
        try gate.requireCurrent(lease)
        try await wire("stop-during-fresh-session", priority: true)
        try gate.requireCurrent(lease)
    }
    await eventually { DelayedIdentityProtocol.received["/stop-during-fresh-session"] != nil }
    DelayedIdentityProtocol.received["/stop-during-fresh-session"]!.complete(); try await retainedStop.value
    DelayedIdentityProtocol.received["/fresh-session-after-reauth"]!.complete(); try await freshSession.value

    // While passkey options are suspended, the existing session may still Stop.
    let ceremony = try await gate.acquire(ceremony: true)
    let options = Task { @MainActor in try await wire("passkey-options") }
    await eventually { DelayedIdentityProtocol.received["/passkey-options"] != nil }
    let oldRead = Task { @MainActor in try await ordinary("old-queued-read") }
    await eventually { attempted.contains("old-queued-read") }
    let stop = Task { @MainActor in
        let lease = try await gate.acquirePriority(); defer { gate.releasePriority(lease) }
        try gate.requireCurrent(lease)
        try await wire("stop-during-options", priority: true)
        try gate.requireCurrent(lease)
    }
    await eventually { DelayedIdentityProtocol.received["/stop-during-options"] != nil }
    require(DelayedIdentityProtocol.received["/old-queued-read"] == nil, "queued ordinary read bypassed ceremony")
    require(DelayedIdentityProtocol.received["/stop-during-options"]!.request.value(forHTTPHeaderField: "Cookie") == "__Host-dv-account=frozen-existing-session",
        "priority request did not use captured native credentials")
    DelayedIdentityProtocol.received["/passkey-options"]!.complete(); try await options.value
    try await gate.beginCredentialCommit(ceremony)
    do { _ = try await gate.acquire(); fatalError("old-view request admitted during credential commit") }
    catch AccountRequestScheduler.Failure.contextChanged { }
    do { try await oldRead.value; fatalError("old queued account read executed under new credential scope") }
    catch AccountRequestScheduler.Failure.contextChanged { }
    require(DelayedIdentityProtocol.received["/old-queued-read"] == nil, "obsolete read reached HTTP transport")
    let heldCredential = Task { @MainActor in try await wire("held-credential") }
    await eventually { DelayedIdentityProtocol.received["/held-credential"] != nil }
    let commitStop = Task { @MainActor in
        let lease = try await gate.acquirePriority(); defer { gate.releasePriority(lease) }
        try gate.requireCurrent(lease)
        try await wire("stop-during-credential", priority: true)
        try gate.requireCurrent(lease)
    }
    DelayedIdentityProtocol.received["/stop-during-options"]!.complete(cookie: "__Host-dv-account=poison; Path=/; Secure", cookieStorage: urgent.configuration.httpCookieStorage)
    do { try await stop.value; fatalError("precommit priority result survived credential epoch") }
    catch AccountRequestScheduler.Failure.contextChanged { }
    await eventually { DelayedIdentityProtocol.received["/stop-during-credential"] != nil }
    require(DelayedIdentityProtocol.received["/stop-during-credential"]!.request.value(forHTTPHeaderField: "Cookie") == "__Host-dv-account=frozen-existing-session",
        "commit-window Stop borrowed the mutable credential jar")
    require(normal.configuration.httpCookieStorage?.cookies(for: accountURL)?.contains(where: { $0.name == "__Host-dv-account" && $0.value == "poison" }) != true,
        "priority Set-Cookie contaminated ordinary account jar")
    DelayedIdentityProtocol.received["/held-credential"]!.complete(cookie: "__Host-dv-account=committed-session; Path=/; Secure", cookieStorage: normal.configuration.httpCookieStorage)
    try await heldCredential.value
    require(accountCookie() == "committed-session", "ordinary credential fixture did not commit its account cookie BEFORE the late priority reply")
    gate.invalidateQueuedScope() // The bridge retires the old published snapshot on credential publication.
    DelayedIdentityProtocol.received["/stop-during-credential"]!.complete(cookie: "__Host-dv-account=old-cookie; Path=/; Secure", cookieStorage: urgent.configuration.httpCookieStorage)
    do { try await commitStop.value; fatalError("late commit-window Stop result survived new principal publication") }
    catch AccountRequestScheduler.Failure.contextChanged { }
    require(normal.configuration.httpCookieStorage?.cookies(for: accountURL)?.contains(where: { $0.name == "__Host-dv-account" && $0.value == "committed-session" }) == true,
        "late priority Set-Cookie replaced committed account cookie")
    gate.release(ceremony)

    // Logout goes next after the active cookie writer; invalidated queued work
    // cannot run under a later login. An already running Stop is fenced by epoch.
    let first = try await gate.acquire()
    let stale = Task { @MainActor in try await ordinary("old-before-logout") }
    await eventually { attempted.contains("old-before-logout") }
    let oldStop = Task { @MainActor in
        let lease = try await gate.acquirePriority(); defer { gate.releasePriority(lease) }
        try await wire("late-stop", priority: true)
        try gate.requireCurrent(lease)
    }
    await eventually { DelayedIdentityProtocol.received["/late-stop"] != nil }
    let logout = Task { @MainActor in try await ordinary("logout", logout: true) }
    await eventually { attempted.contains("logout") }
    do { try await stale.value; fatalError("old queued read survived logout admission") }
    catch AccountRequestScheduler.Failure.contextChanged { }
    gate.release(first)
    await eventually { DelayedIdentityProtocol.received["/logout"] != nil }
    gate.release(first) // Duplicate completion must not release the new owner.
    let afterLogout = Task { @MainActor in try await ordinary("after-logout") }
    await eventually { attempted.contains("after-logout") }
    require(DelayedIdentityProtocol.received["/after-logout"] == nil, "duplicate release freed logout lease")
    DelayedIdentityProtocol.received["/logout"]!.complete(cookie: "__Host-dv-account=; Path=/; Secure; Max-Age=0", cookieStorage: normal.configuration.httpCookieStorage); try await logout.value
    require(accountCookie() == nil, "ordinary logout fixture did not remove its account cookie")
    await eventually { DelayedIdentityProtocol.received["/after-logout"] != nil }
    DelayedIdentityProtocol.received["/after-logout"]!.complete(); try await afterLogout.value
    let login = try await gate.acquire(ceremony: true)
    var loginCommitting = false
    let loginCommit = Task { @MainActor in try await gate.beginCredentialCommit(login); loginCommitting = true }
    await eventually { loginCommitting }
    try await loginCommit.value
    let loginHTTP = Task { @MainActor in try await wire("credential-complete") }
    await eventually { DelayedIdentityProtocol.received["/credential-complete"] != nil }
    DelayedIdentityProtocol.received["/credential-complete"]!.complete(cookie: "__Host-dv-account=new-session; Path=/; Secure", cookieStorage: normal.configuration.httpCookieStorage)
    try await loginHTTP.value
    require(accountCookie() == "new-session", "ordinary signin fixture did not commit its account cookie BEFORE the late priority reply")
    gate.invalidateQueuedScope()
    DelayedIdentityProtocol.received["/late-stop"]!.complete(cookie: "__Host-dv-account=old-session; Path=/; Secure", cookieStorage: urgent.configuration.httpCookieStorage)
    do { try await oldStop.value; fatalError("late Stop result survived logout epoch") }
    catch AccountRequestScheduler.Failure.contextChanged { }
    require(normal.configuration.httpCookieStorage?.cookies(for: accountURL)?.contains(where: { $0.name == "__Host-dv-account" && $0.value == "new-session" }) == true,
        "late priority reply replaced the newly committed session")
    gate.release(login)

    // The actual production registry is reserved before scheduling. Removing a
    // queued command must prevent HTTP, not merely discard its eventual reply.
    let registry = AccountRequestRegistry()
    @MainActor func registered(_ name: String, id: UUID = UUID(), priority: Bool = false) throws -> Task<Void, Error> {
        let ticket = try registry.reserve(id, cancelWaiting: { gate.cancelWaiting(requestId: id) })
        let work = Task { @MainActor in
            defer { registry.finish(ticket) }
            try Task.checkCancellation()
            if priority {
                attempted.insert(name)
                let lease = try await gate.acquirePriority(requestId: id)
                defer { gate.releasePriority(lease) }
                try gate.requireCurrent(lease)
                try await wire(name, priority: true)
                try gate.requireCurrent(lease)
            } else { try await ordinary(name, requestId: id) }
        }
        registry.attach(ticket, cancel: { work.cancel() })
        return work
    }
    let heldRequest = try registered("registered-hold")
    await eventually { DelayedIdentityProtocol.received["/registered-hold"] != nil }
    let obsoleteId = UUID()
    let obsolete = try registered("mutation-obsolete", id: obsoleteId)
    await eventually { attempted.contains("mutation-obsolete") }
    require(registry.cancel(obsoleteId), "registered queued cancellation was not accepted")
    do { try await obsolete.value; fatalError("obsolete queued mutation succeeded") }
    catch { require(AccountRequestRegistry.isCancellation(error), "queued cancellation lost its exact cancellation meaning") }
    require(registry.activeCount == 1 && DelayedIdentityProtocol.received["/mutation-obsolete"] == nil,
        "canceled queued command consumed capacity or dispatched HTTP")
    do { _ = try registered("duplicate-obsolete", id: obsoleteId); fatalError("canceled ID was immediately reused") }
    catch AccountRequestRegistry.Failure.cancelled { }
    let following = try registered("following-cancelled-command")
    DelayedIdentityProtocol.received["/registered-hold"]!.complete(); try await heldRequest.value
    await eventually { DelayedIdentityProtocol.received["/following-cancelled-command"] != nil }
    require(DelayedIdentityProtocol.received["/mutation-obsolete"] == nil, "obsolete command reached HTTP after predecessor settled")
    DelayedIdentityProtocol.received["/following-cancelled-command"]!.complete(); try await following.value

    // A lease may be granted just before cancellation reaches the actor. The
    // canceled Task must check again before calling URLSession.
    let grantedHold = try await gate.acquire()
    let grantedId = UUID()
    let granted = try registered("mutation-granted-cancel", id: grantedId)
    await eventually { attempted.contains("mutation-granted-cancel") }
    gate.release(grantedHold); registry.cancel(grantedId)
    do { try await granted.value; fatalError("canceled granted lease dispatched") }
    catch { require(AccountRequestRegistry.isCancellation(error), "granted lease cancellation was lost") }
    require(DelayedIdentityProtocol.received["/mutation-granted-cancel"] == nil, "grant/cancel race dispatched obsolete HTTP")

    let priorityHold = try registered("registered-priority-hold", priority: true)
    await eventually { DelayedIdentityProtocol.received["/registered-priority-hold"] != nil }
    let priorityId = UUID()
    let priorityCancelled = try registered("registered-priority-cancelled", id: priorityId, priority: true)
    await eventually { attempted.contains("registered-priority-cancelled") }
    registry.cancel(priorityId)
    do { try await priorityCancelled.value; fatalError("queued priority cancellation succeeded") }
    catch { require(AccountRequestRegistry.isCancellation(error), "priority cancellation was lost") }
    DelayedIdentityProtocol.received["/registered-priority-hold"]!.complete(); try await priorityHold.value
    require(DelayedIdentityProtocol.received["/registered-priority-cancelled"] == nil, "canceled priority request dispatched HTTP")

    let unaffectedCeremony = try await gate.acquire(ceremony: true)
    let duringCeremonyId = UUID()
    let duringCeremony = try registered("cancel-during-ceremony", id: duringCeremonyId)
    await eventually { attempted.contains("cancel-during-ceremony") }
    registry.cancel(duringCeremonyId)
    do { try await duringCeremony.value; fatalError("queued ceremony request ignored cancellation") }
    catch { require(AccountRequestRegistry.isCancellation(error), "ceremony waiter cancellation was lost") }
    let retainedCeremony = try registered("after-cancelled-ceremony-request")
    await eventually { attempted.contains("after-cancelled-ceremony-request") }
    require(DelayedIdentityProtocol.received["/after-cancelled-ceremony-request"] == nil,
        "canceling one request released the unrelated whole-passkey lease")
    gate.release(unaffectedCeremony)
    await eventually { DelayedIdentityProtocol.received["/after-cancelled-ceremony-request"] != nil }
    DelayedIdentityProtocol.received["/after-cancelled-ceremony-request"]!.complete(); try await retainedCeremony.value

    let activeId = UUID()
    let activeHTTP = try registered("registered-active-cancel", id: activeId)
    await eventually { DelayedIdentityProtocol.received["/registered-active-cancel"] != nil }
    do { _ = try registered("duplicate-active", id: activeId); fatalError("duplicate active ID replaced its owner") }
    catch AccountRequestRegistry.Failure.duplicate { }
    let afterActive = try registered("after-registered-active-cancel")
    registry.cancel(activeId)
    do { try await activeHTTP.value; fatalError("active URLSession cancellation succeeded") }
    catch { require(AccountRequestRegistry.isCancellation(error), "active URLSession cancellation was not recognized") }
    await eventually { DelayedIdentityProtocol.received["/after-registered-active-cancel"] != nil }
    DelayedIdentityProtocol.received["/after-registered-active-cancel"]!.complete(); try await afterActive.value
    require(registry.activeCount == 0, "active cancellation leaked a registry owner or FIFO lease")

    let early = UUID(); require(registry.cancel(early), "early cancellation was not remembered")
    for _ in 0..<2 {
        do { _ = try registered("cancel-before-register", id: early); fatalError("early cancellation marker was consumed") }
        catch AccountRequestRegistry.Failure.cancelled { }
    }
    let unattached = UUID()
    let ticket = try registry.reserve(unattached, cancelWaiting: { gate.cancelWaiting(requestId: unattached) })
    registry.cancel(unattached)
    let lateAttach = Task { @MainActor in
        defer { registry.finish(ticket) }
        try Task.checkCancellation(); try await ordinary("cancel-before-attach", requestId: unattached)
    }
    registry.attach(ticket, cancel: { lateAttach.cancel() })
    do { try await lateAttach.value; fatalError("cancel-before-attach was lost") }
    catch { require(AccountRequestRegistry.isCancellation(error), "late attach cancellation was not recognized") }
    require(DelayedIdentityProtocol.received["/cancel-before-attach"] == nil, "canceled pre-attachment task reached HTTP")

    let current401Id = UUID()
    let current401 = try registered("current-http-401", id: current401Id)
    await eventually { DelayedIdentityProtocol.received["/current-http-401"] != nil }
    DelayedIdentityProtocol.received["/current-http-401"]!.complete(status: 401); try await current401.value
    require(responseStatus["current-http-401"] == 401 && !registry.cancel(current401Id), "current HTTP 401 was masked as cancellation")

    var ledgerTime: TimeInterval = 100
    let boundedRegistry = AccountRequestRegistry(now: { ledgerTime })
    for _ in 0..<128 { boundedRegistry.cancel(UUID()) }
    let overflowCancel = UUID(); require(boundedRegistry.cancel(overflowCancel), "bounded early cancel was not accepted")
    require(boundedRegistry.recentCount == 128, "cancellation tombstones are unbounded")
    do { _ = try boundedRegistry.reserve(overflowCancel, cancelWaiting: {}); fatalError("overflow forgot a cancellation marker") }
    catch AccountRequestRegistry.Failure.cancelled { }
    ledgerTime += 61
    require(boundedRegistry.recentCount == 0, "cancel-before-registration markers never expire")
    let expiredTicket = try boundedRegistry.reserve(overflowCancel, cancelWaiting: {})
    boundedRegistry.finish(expiredTicket); boundedRegistry.finish(expiredTicket)
    require(boundedRegistry.activeCount == 0 && !boundedRegistry.cancel(overflowCancel), "completed request settlement was not once-only")
    var capacityTickets: [AccountRequestRegistry.Ticket] = []
    for _ in 0..<128 { capacityTickets.append(try boundedRegistry.reserve(UUID(), cancelWaiting: {})) }
    do { _ = try boundedRegistry.reserve(UUID(), cancelWaiting: {}); fatalError("active request registry is unbounded") }
    catch AccountRequestRegistry.Failure.full { }
    for ticket in capacityTickets { boundedRegistry.finish(ticket) }
    require(boundedRegistry.activeCount == 0 && boundedRegistry.recentCount <= 128, "registry capacity was not released")

    // Bounded admission still reserves logout; errors settle every waiter once.
    let blocked = try await gate.acquire()
    var blockedAttempts = 0
    var queued: [Task<AccountRequestScheduler.Lease, Error>] = []
    for _ in 0..<32 {
        queued.append(Task { @MainActor in blockedAttempts += 1; return try await gate.acquire() })
        await eventually { blockedAttempts == queued.count }
    }
    do { _ = try await gate.acquire(); fatalError("ordinary queue is unbounded") }
    catch AccountRequestScheduler.Failure.queueFull { }
    let reservedLogout = Task { @MainActor in try await gate.acquire(logout: true) }
    for task in queued {
        do { _ = try await task.value; fatalError("logout did not invalidate bounded old queue") }
        catch AccountRequestScheduler.Failure.contextChanged { }
    }
    gate.release(blocked)
    let logoutLease = try await reservedLogout.value; gate.release(logoutLease)
    print("PASS native account request-scoped cancellation/zero-obsolete-HTTP, delayed URLSession FIFO, ceremony/credential-commit Stop, credential scope fence, stale/logout fencing, cookie isolation and bounded admission")
}

DispatchQueue.global().asyncAfter(deadline: .now() + 20) { fatalError("Native account scheduling regression timed out") }
Task { @MainActor in
    do { try await nativeAccountSchedulingTests(); exit(0) }
    catch { fatalError("Native account scheduling regression failed: \(error)") }
}
dispatchMain()
