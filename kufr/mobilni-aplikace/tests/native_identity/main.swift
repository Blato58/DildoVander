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
