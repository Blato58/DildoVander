# DV10 VPS delivery and recovery

## Production observation — 2026-10-15

The owner authorized production deployment and paid generation. Public pickup is
live at **https://2.28.202.220/keepsake/evening/** with one static QR and no token.
The active backend and full canonical site use clean main
`985071520f4c64c7ff98eaff5f92ab10bbbabd98`, built by
[CI 38748013953](https://github.com/Blato58/DildoVander/actions/runs/38748013953).
The existing private workflows completed successfully:
[backend 38755345896](https://github.com/Blato58/DildoVander-deploy/actions/runs/38755345896)
and [site 38755728034](https://github.com/Blato58/DildoVander-deploy/actions/runs/38755728034).
Exact artifact/runtime hashes and checks are in the
[production record](../../../docs/project/artifacts/DV10-production-v1.json).

- Backend release: `985071520f4c64c7ff98eaff5f92ab10bbbabd98-38755345896-1`;
  site release: `38755728034-1`. Backend and Twin services are active.
- Separate root-owned `/etc/kufr-backend/keepsake.env` mode0600 is loaded by
  `90-keepsake.conf`; `UMask=0077`. Enabled, GenerationEnabled and PortraitEnabled
  are true, ProviderMode is `openai`, StoryModel `gpt-6.1-sol`, ImageModel
  `gpt-image-2`. PublicDeviceId is the existing provisioned primary ESP32 identity.
  The existing backend key and dialog/voice/memory modes are preserved.
- Archive `/var/lib/kufr-backend/keepsakes` and keyring
  `/var/lib/kufr-backend/keepsakes.keys` are backend-owned mode0700, outside releases.
  Retention remains manual-only; paid portraits require the private consent flow.
- Active nginx uses the complete generated profile in
  `/etc/nginx/snippets/dildovander-keepsake.conf` and the http-scope Upgrade map
  `/etc/nginx/conf.d/keepsake-upgrade.conf`. Its69 method/route pairs preserve
  current backend/Twin endpoints; encoded API paths are rejected. Certificate,
  iOS, ACME and external CloudVPSBridge locations are preserved. Legacy bootstrap,
  site-install or Twin-install helpers must not reinsert their old overlapping
  includes/catch-all; reapply this complete generated profile if they are used.
- Full candidate and active `nginx -t` passed. Isolated nginx10 cases, public HTTPS21
  cases plus wrong-host421, SQLite quick_check, visible browser navigation/refresh,
  and publishing workflow checks of all308 site files passed.
- Private pre-change configuration and online SQLite copies are retained at
  `/root/kufr-dv10-backups/20261015T140756Z`. This is a local operational backup;
  independent encrypted off-VPS backup and isolated recovery remain unverified.

At verification there were zero Keepsake items/jobs and no active evening. Paid
inference was enabled but no inference sample was invoked. Account access to both
models was checked without generation. Real output quality, consented photo pilot,
real iPhone and physical device acceptance remain unverified.

For emergency disable, set Enabled, GenerationEnabled and PortraitEnabled to false
in the separate environment file and restart `kufr-backend.service`, then check
loopback/public health. Keep the compatible backend, live SQLite, archive and
DataProtection keys. Never roll back older DB/grants or delete retained content.
The pre-DV10 backend is not a compatible rollback after accepting DV10 data.

## Historical read-only preflight — 2026-10-14

At **2026-10-14 15:18:15 UTC**, Windows `ssh -G vps-dildovander` resolved
`root`, host `2.28.202.220`, port 22, normal host-key verification (`ask`).
Batch-mode OpenSSH inspection succeeded through the existing agent. The source
deployment/config files were read first. No environment contents, private keys,
database contents, archive contents, or visitor photographs were read.

| Surface | Actual observation |
| --- | --- |
| Runtime | Linux 7.0.0-34-generic, Python 3.14.4, nginx 1.28.3 |
| Backend | `kufr-backend.service` active/running, user/group `kufr-backend` |
| Listener | `127.0.0.1:5080`; public 80/443 are nginx; loopback health returned 200 |
| Existing origin | nginx HTTPS server name `2.28.202.220`; configured Let's Encrypt certificate path, public TLS trust/expiry not verified |
| Release | `/opt/kufr-backend/releases/59006409c04a81b5b52cff3b1544c6519888d6f4-38555408343-1` |
| Data | `/var/lib/kufr-backend` mode 0700, owner `kufr-backend`; `journal.sqlite` mode 0644 inside that private directory |
| Web | `/var/www/dildovander`, owner `github-runner`; separate from DB/archive |
| Capacity | Root disk 38G total, 8.6G available, 76% used; 2,144,038 free inodes |
| Compute | One CPU; 1909 MiB RAM, 1272 MiB available; 2047 MiB swap, 402 MiB used |
| Backup tools | `age` and `gpg` binaries present; no `sqlite3` CLI found, Python SQLite is used instead |

These observations do not establish sustained image/export capacity, a verified
backup target, custody of recovery keys, a real restore, current public TLS, or
DV10 public availability. Those remain `not_run/blocked` gates.

## Existing delivery boundary

`.github/workflows/kufr-backend.yml` publishes the self-contained Linux backend,
tests it, and packages provenance via `scripts/ci/package.py`. Actual activation
is orchestrated in private `Blato58/DildoVander-deploy`; this source checkout does
not own that workflow. Use its existing protected production environment and
release helper. No clone of source code on VPS is required.

`deploy-helper.py` rewrites `backend.env`, swaps `/opt/kufr-backend/current`, and
health-checks loopback. Keepsake configuration is therefore a separate root-owned
`/etc/kufr-backend/keepsake.env` (0640, group `kufr-backend`) loaded through the
reviewable [systemd drop-in](../../web/deploy/keepsake-systemd.conf). The
[template](../../web/deploy/keepsake.env.example) keeps `Keepsake__Enabled=false`.
The public pickup page is `/keepsake/evening/`; its GET-only gallery uses the
configured `Keepsake__PublicDeviceId` and existing per-device active evening.
Only generated base wands/stories/exports are public. Photos, portraits and
owner/admin actions require separate private authority. The static QR contains
no credential. See [the accepted pickup change](STATIC_EVENING.md).
Data Protection encrypts session/link replay material in persistent SQLite.
`Keepsake__DataProtectionRoot` must be a persistent private directory, separate
from archive, webroot and releases. Its key ring must survive deploy/rollback and
must be included inside the encrypted backup; never print or package its keys.

Persistent layout:

| Purpose | Path |
| --- | --- |
| Existing shared SQLite domain | `Backend__DatabasePath=/var/lib/kufr-backend/journal.sqlite` |
| Private retained DV10 bytes | `Keepsake__DataRoot=/var/lib/kufr-backend/keepsakes` |
| Persistent Data Protection ring | `Keepsake__DataProtectionRoot=/var/lib/kufr-backend/keepsakes.keys` |
| Code release | `/opt/kufr-backend/releases/RELEASE_ID` |
| Public compiled guest web | `/var/www/dildovander/releases/RELEASE_ID/keepsake` |
| Recovery private keys | Independent authorized custodian/secret store; never webroot/release |

The service already grants writes only to `/var/lib/kufr-backend`. Archive
directories must be 0700 and retained files 0600, owned by the service. The
archive is not an nginx alias. No release cleanup, janitor, TTL or LRU may touch
manual-only content. Disk-full handling stops intake with an explicit storage
error; it must never delete retained content to make room.

## One coordinated code artifact

After the established builds produce a Linux backend and compiled portal:

```sh
npm --prefix kufr/mobilni-aplikace run build
python3 kufr/web/build-site.py --web-dist kufr/mobilni-aplikace/dist --destination "$RUNNER_TEMP/dv10-web"
python3 kufr/backend/deploy/keepsake_release.py \
  --backend "$RUNNER_TEMP/kufr-publish" \
  --web "$RUNNER_TEMP/dv10-web" \
  --output "$RUNNER_TEMP/keepsake-release" --revision "$GITHUB_SHA"
python3 scripts/ci/package.py pack "$RUNNER_TEMP/keepsake-release" "$RUNNER_TEMP/keepsake-package"
```

The release packager combines backend and guest web, records each file SHA-256 and
the source revision, rejects known DB/credential/archive paths and symlinks, and
does not activate anything. The web root contains the shared Vue application, including guest archive routes; production must preserve the complete application and distribution routes using the [canonical delivery profile](DELIVERY.md). Existing CI packaging supplies its normal provenance
and extraction checks. Only compiled code belongs in the artifact; root operator
configuration and all persistent state remain outside. The private deployment
workflow must verify provenance/digests, stage both code trees, activate the
compatible backend first with the feature off, health-check, then activate web.
It must retain a previous compatible pair and verify both manifests. Preparing
this tool is not evidence that private orchestration or production ran it.

Schema rollout is additive in the shared SQLite database. Take a consistent
backup before enabling the new feature, verify schema compatibility and existing
Live/OTA/iOS/admin/device health, then expose the dependent portal. Rollback swaps
compatible code/web only; it never swaps an older SQLite DB, restores revoked
grants, removes archive files, runs old content janitors, or performs destructive
down migrations. An incompatible rollback keeps the keepsake feature off and
repairs forward.

## Complete ingress map and production replacement gate

Generate the [complete compatibility locations](../../web/deploy/keepsake_proxy.py)
with the verified canonical host and the separately scoped Upgrade map. The
checked-in [locations example](../../web/deploy/keepsake-compatibility.conf.example)
and [http-scope map example](../../web/deploy/keepsake-upgrade.conf.example) were
produced by the same generator with an intentionally invalid example origin:

```sh
python3 kufr/web/deploy/keepsake_proxy.py --host VERIFIED_CANONICAL_HOST \
  --compatibility --output /private/staging/keepsake.conf \
  --http-map-output /private/staging/keepsake-upgrade.conf
```

The source generator is the exact guest method/route allowlist, and
[backend-route-contract.json](../../web/deploy/backend-route-contract.json)
records all 69 existing backend route/method pairs with their actual source
declarations and listener split: the prior 49 on `127.0.0.1:5080` plus 20
DigitalTwin mappings on its isolated `127.0.0.1:5082` service. The tests compare
this inventory with every literal current source `/v1/*`, `/healthz`,
`/healthz/twin` and `/twin` mapping, including the operator outcome/claim and admin
owner-restoration endpoints. GUIDs remain canonical lowercase; Twin instance IDs
use the source's 1-96 ASCII letter/digit, underscore, hyphen or dot rule. Firmware
channels and artifact hashes keep their existing bounded patterns.
It serves only compiled `/keepsake/`, disables access
logs and cache for guest paths, forwards to the existing private listener, sets
the canonical upstream Host, and overwrites forwarded host/protocol/client data.
Unknown `/guest/*`, `/api/*`, `/v1/*`, and other paths return 404; wrong methods
return 405. The public root uses the existing site static route, without SPA
fallback. Unrecognized Host returns 421. Query strings
do not alter route scope. Cookie, CSRF, origin, scope, and direct-listener auth
remain enforced by .NET. There is no wildcard CORS, new port, BFF or gateway key.

The compatibility map preserves device-token issuance, diagnostics, logs, server
modes, memory administration, evening management, conversation/text/speech/image,
operations/audio, Live/GPT-Live streams and their subroutes, OTA stable/development
manifests and exact SHA-256 binaries, authenticated keepsake producer/claim, and
admin owner restoration. It also preserves exact DigitalTwin viewer/health,
session, instance/snapshot/recording, simulation-command, ingest and provisioned
device-token routes, keeping Twin roles and origin/cookie checks in .NET. Replace
the old `dildovander-twin.conf` include along with the old site include and
catch-all; its broad `^~ /v1/twin/` prefix would override the exact method guards.
Twin inbound uses ordinary HTTP, with a 256 KiB JPEG photo-upload limit, 32 KiB
for other Twin requests and a 30-second read timeout. Nine grouped media routes
retain the existing control/Origin authorization on port 5082; output is polled JSON.
The exact API locations reject percent-encoded raw paths before method dispatch;
percent encoding in query values remains supported. Its isolated outbound conversation/turn/operation calls
use configured loopback HTTP and introduce no ingress route or WebSocket.
It preserves backend auth rather than translating guest grants into those schemes.
Existing Live ingress is POST `application/x-ndjson`, not a backend WebSocket
GET endpoint: request/response buffering is disabled on those stream roots and
the read timeout allows the existing long session. Upgrade forwarding uses the
http-scope map without adding methods or routes. This follows the
[nginx Upgrade guidance](https://nginx.org/en/docs/http/websocket.html) and
[streaming buffering controls](https://nginx.org/en/docs/http/ngx_http_proxy_module.html).

**Observed production replacement gate:** existing nginx `location /` still
proxies the entire backend. During separately authorized deployment, privately
back up the server file and site snippet, install the generated Upgrade map once
in `http` scope, replace the current site include with the complete compatibility
locations, and remove the old `location /` proxy block. Do not include the old
site routes again: the complete profile already contains those source routes.
Keep the server's existing certificate/listeners, `/ios/`, ACME and
CloudVPSBridge include/location intact. Run `nginx -t` before reload; on failure
restore the backed-up code/config without restoring old DB/grants. The app and
authorized public nginx rehearsal must verify every allowed/forbidden path,
stream delivery, OTA bytes, iOS downloads, admin/device guest-cookie rejection,
Host/forwarded poisoning and no SPA API fallback. No production replacement or
runtime nginx test was performed here.

## Independent encrypted backup and isolated restore

The [recovery tool](../../backend/deploy/keepsake_recovery.py) uses SQLite's online
backup API rather than copying the live DB/WAL. DB and files share a point in time
only when **all writers are quiesced**: jobs, late provider ingest, uploads,
exports, grant mutations and manual deletion. The `--writers-quiesced` switch is
an operator assertion, not an automatic process lock. Pause admission/provider
dispatch, drain or mark uncertain attempts, stop the app, capture authoritative
evidence, then invoke the snapshot. Quiescence keeps archive and DB consistent;
online backup keeps SQLite internally consistent. Never print visitor content.

The evidence file is a read-only export from the latest authoritative SQLite
`dv10_recovery_state`, append-only `dv10_revocations`, `dv10_deletions`, deleted
keepsake tombstones, and uncertain job states, stored independently with each
backup. Capture it while all writers are quiesced:

```sh
python3 kufr/backend/deploy/keepsake_recovery.py export-evidence \
  --database /var/lib/kufr-backend/journal.sqlite \
  --archive /var/lib/kufr-backend/keepsakes \
  --keys /var/lib/kufr-backend/keepsakes.keys \
  --output /private/latest-recovery-evidence.json --writers-quiesced
```

The exporter rejects a missing/nonunique store identity or gaps in monotonic
revocation revisions. Its offline transport contract is:

```json
{"schemaVersion":1,"retentionMode":"manual_only","storeId":"opaque-store-id","revision":0,"capturedAtUtc":"2026-10-14T00:00:00Z","deletedKeepsakeIds":[],"revocationEvidenceHashes":[],"deletionEvidenceHashes":[],"unresolvedAttemptIds":[],"archiveSha256":{},"keyRingSha256":{}}
```

It contains only opaque identifiers/hashes, never grant plaintext, prompt secrets
or visitor content. The revision and evidence are authoritative only when the
operator proves their provenance and that they reflect the latest source. A
handwritten empty file is not production evidence. Row evidence is hashed and
archive/keyring inventory contains opaque relative names plus SHA-256 only.
The snapshot checks its DB authority and copied file inventories against that
export. Offline reconciliation of a newer ledger remains a gate; this tool
deliberately refuses to reopen an older restore instead of claiming reconciliation.

On the quiesced primary, with an already approved **public** age recipient:

```sh
python3 kufr/backend/deploy/keepsake_recovery.py encrypt \
  --database /var/lib/kufr-backend/journal.sqlite \
  --archive /var/lib/kufr-backend/keepsakes \
  --keys /var/lib/kufr-backend/keepsakes.keys \
  --evidence /private/latest-recovery-evidence.json \
  --output /private/staging/dv10-backup.age \
  --recipient PUBLIC_AGE_RECIPIENT --writers-quiesced
```

Copy only the encrypted bundle to the independently authorized off-VPS target.
Record its hash and successful retrieval. No destination was provisioned and no
transfer was attempted here. Retain older backups until explicit authorized
manual deletion; a live deletion receipt must distinguish backup copies still
pending. Backup retention may not silently resurrect deleted access/content.

On an isolated recovery host, block public ingress and provider egress, remove
all provider credentials, keep the app stopped and the feature disabled:

```sh
age --decrypt --identity /private/custodian-key.txt --output /private/restore.tar /private/dv10-backup.age
python3 kufr/backend/deploy/keepsake_recovery.py unpack --bundle /private/restore.tar --output /private/isolated-restore
python3 kufr/backend/deploy/keepsake_recovery.py verify --snapshot /private/isolated-restore --latest-evidence /private/authoritative-latest-evidence.json
```

The unpacker rejects traversal, links, duplicate paths and special files. The
verifier checks SQLite integrity and every retained byte hash. It refuses any
different deletion/revocation/content/key evidence and any ambiguous provider
attempt (including cancelled leased attempts conservatively). Capture timestamps
may differ; authoritative evidence and retained inventories must match. Even
success returns `verified_isolated_only`, with **public/provider start not
authorized**. A newer ledger requires a separately reviewed offline reconciler
or a newer consistent backup; never replace the latest ledger with an older one
to bypass this refusal. Check expiration/revoke all recovered grants
conservatively, adjudicate uncertain provider sends without inference, and
verify manually deleted bytes stay inaccessible before authorized activation.

## Actual validation and open gates

Local synthetic SQLite/archive/restore and release tests pass; proxy allowlist
negative tests pass. They cover retained origin/history hashes, unsafe tar,
newer deletion/revocation refusal, ambiguous attempt refusal, and DB exclusion
from code release. These are offline evidence, not live guest acceptance.

Local `age` and `nginx` are unavailable on Windows. Ubuntu WSL startup failed
because its configured `ext4.vhdx` is absent. Encrypted round-trip, nginx runtime
syntax, independent off-server restore, production config/activation, public TLS,
public guest flow, and recovery-key custody were **not_run/blocked** at that local verification time. The production section above supersedes nginx/TLS/activation status; independent recovery remains unverified.
The server's age binary existence does not count as an executed encrypted backup.

The delivery skill passed the bundled `quick_validate.py` validator and its
nonpaid script dry-run. Actual discovery was also verified using installed
`codex-cli 0.162.1`: a temporary stdio `codex app-server` session performed
`initialize`, `initialized`, then `skills/list` with this checkout's exact `cwds`
and `forceReload: true`. Final reload returned `dildovander-keepsake-delivery`,
`dildovander-keepsake-design`, and `dildovander-keepsake-eval`, each at its tracked
`.agents/skills` path with `enabled: true`, `scope: repo`, and zero discovery errors.
No thread/model turn or global configuration change occurred. The supported
reload mechanism is documented in [official app-server docs](https://learn.chatgpt.com/docs/app-server).
