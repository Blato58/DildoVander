# Firmware OTA, signing and recovery

ESP-IDF 6.1 uses two existing 6,553,600-byte app slots in the unchanged 16MB
partition table. NVS remains at `0x9000/0x5000`, OTA data at `0xe000/0x2000`,
app0 at `0x10000`, app1 at `0x650000`, SPIFFS retains `0x360000` bytes and the
core-dump partition remains intact. SD is a separate card, not reduced flash.

## Initial installation

Build from the repository root:

```powershell
pio run -d kufr/firmware -e esp32-s3-idf
$firmwareVersion = (Get-Content kufr/firmware/version.txt -Raw).Trim()
python kufr/firmware/tools/ota_release.py check --binary kufr/firmware/.pio/build/esp32-s3-idf/firmware.bin --version $firmwareVersion
```

`version.txt` supplies ESP-IDF `PROJECT_VER` and the updater version string.
The CMake build also embeds the Git build identity. The automatic check validates
the partition layout, image chip/flash target and embedded version in CI.

Before initial USB installation confirm the actual N16R8 board, detach actuator
power, identify the correct port and privately back up the whole flash/NVS with
the installed esptool. Save bootloader/partitions/NVS backup hashes. Do not run
erase-flash. For the identical documented partition layout, `pio run -d
kufr/firmware -e esp32-s3-idf -t upload --upload-port COM8` installs bootloader,
table and app using the generated flash arguments. Examine those arguments
before upload: no NVS erase/write is allowed. A different installed layout needs
a separate controlled backup/migration; a partition-table change is not normal OTA.

Run the separate [USB network service](../kufr/firmware/diagnostics/network-service/README.md)
to initialize PoP, registered device identity and OTA trust, then restore the
application image. Firmware initially lacking this updater/rollback bootloader
cannot acquire these capabilities by a claimed automatic OTA migration.

## Trust and manifest contract

Service NVS namespace `wand_ota`:

| Key | Meaning |
| --- | --- |
| `origin` | Approved HTTPS origin, hostname/IP and optional port443, no path/query/userinfo |
| `channel` | `development` or `stable` |
| `trust_pem` | Trusted ECDSA P-256 public key PEM, at most1024 bytes |
| `auto_install` | u8 automatic installation policy; manual checks/install still available |
| `check_interval` | u32 seconds60..86400, default3600 |

Checks start after connectivity and safe idle, initially after30s. Periodic checks
use the configured interval; failures back off30s..32min. No update request may
disable normal functionality merely by checking. A requested install waits idle,
then deliberately enters maintenance until completion/reboot; Stop and status
remain available while new interactions are blocked.

GET `/v1/firmware/manifests/{channel}.json` returns
`{schemaVersion:1,payload:<base64 exact JSON bytes>,signature:<base64 DER ECDSA signature>}`.
The signed payload has exactly twelve fields: `schemaVersion`, `version`,
`buildId`, `hardware`, `profile`, `channel`, `updaterVersion`,
`minUpdaterVersion`, `url`, `sizeBytes`, `sha256`, `signatureAlgorithm`.
Hardware is `esp32s3-n16r8`, profile `B-effects`, algorithm
`ecdsa-p256-sha256`, updater1, build identity lowercase Git40.
Version is three canonical integer components0..65535. Only a strictly newer
version is installed. Artifact URL is the same-origin relative path
`/v1/firmware/artifacts/<sha256>.bin`.

The device verifies the pinned public key signature before trusting metadata,
certificate/hostname for HTTPS, exact byte count and SHA-256 of the staged image,
ESP application structure and embedded version/project. Redirects, incompatible
targets, corrupt/truncated images and previously failed versions are rejected.
Hashing alone is not authentication. The production trust key must be supplied
separately; development material is not production authority.

The implementation evaluates `esp_https_ota` but uses official `esp_http_client`
and `esp_ota_begin/write/end/set_boot_partition`: installed6.1 wrapper code can
follow redirects internally despite the client disable flag. Explicit HTTP
open/header/read APIs let this implementation reject them before following.
It still uses ESP-IDF OTA/partition APIs, not custom raw flash management.
Download and exact partition hashing reuse one 4 KiB heap buffer, allocated
before flash erase/write. Allocation failure releases maintenance and leaves
the running slot intact. Local Xtensa frame measurements cover updater source;
physical task stack high-water and complete SDK call-stack use remain unverified.

## Build and publish a future release

Increment `kufr/firmware/version.txt`, run relevant host/full builds and merge a
reviewed PR with green CI. The existing provenance verifier accepts only a
successful firmware artifact matching the merged/current main Git tree.

For an offline/local package with a signing key kept outside source:

```powershell
$firmwareVersion = (Get-Content kufr/firmware/version.txt -Raw).Trim()
python kufr/firmware/tools/ota_release.py package --binary kufr/firmware/.pio/build/esp32-s3-idf/firmware.bin --version $firmwareVersion --build-id GIT_40_HEX --channel development --key C:/private/nonproduction-key.pem --out C:/private/new-ota-package
python kufr/firmware/tools/ota_release.py verify --out C:/private/new-ota-package
```

OpenSSL is required (`--openssl` may select the installed executable). The output
contains only public trust, manifest, release metadata and content-addressed app.
No private signing key or device credential is included. Verification checks the
real signature and artifact bytes. Development test keys live only in private
temporary storage; no committed test key is accepted as production trust.

On VPS, install the reviewed root-owned publisher as
`/usr/local/sbin/kufr-firmware-publish`, `firmware_artifact.py`, packaging tool
and partition table under
`/usr/local/lib/kufr-firmware/`, and private/public authority files under
`/etc/kufr-backend/ota-{development|stable}-{private|public}.pem`.
Private keys must be root0600; parent directories root-owned and not writable
by runner/backend. The narrow sudo rule allows only this validated publisher.
The helper accepts only `BINARY CI_RUN_ID CHANNEL`; an ephemeral Actions read
token arrives on standard input and is never saved. Root independently fetches
the successful `ci.yml` run, merged same-repository PR/current main provenance,
GitHub artifact ZIP and its server-recorded digest. It validates the bounded
ZIP/TAR package, source tree and payload hash, derives version/build identity
from immutable GitHub source, and compares the exact firmware bytes to its
bounded snapshot of the runner file. Caller-supplied metadata is not authority.
The signed-URL download receives no Authorization header. Missing metadata,
modified firmware or a changed main branch rejects publication before promotion.
It signs with the selected root-held key, verifies it, then atomically publishes
the artifact before the channel manifest under `/opt/kufr-firmware`.
GitHub CI and its build runner remain trusted producers. The server-recorded
digest authenticates the uploaded artifact; it does not prove reproducible
compilation or protect against a compromised builder uploading malicious bytes.
The root boundary prevents substituting a different local staging image after
CI. Production authority and builder access therefore require separate custody.
Backend config is `Backend__Firmware__Directory=/opt/kufr-firmware`; it verifies
against `trust-{channel}.pem` and caps public download concurrency at4.

```sh
gh workflow run kufr-firmware-release.yml --ref main -f build_run_id=CI_RUN_ID -f channel=development
```

Use `stable` only after explicitly provisioning its production authority and
the same public trust on the device. The workflow does not create a production key.
The actual publication/channel evidence is in the implementation status.

## Activation, diagnosis and recovery

States include disabled/idle/checking/current/available/waiting-idle/downloading/
verifying-image/ready-to-reboot/rebooting/verifying-boot/rollback/failed.
The owner reserves safe idle across installation and reboot; it blocks new
interactions. Only the inactive slot is written. Failed transfers continue the
existing app; persistent NVS credentials remain outside both app slots.

New boot must pass passive local health and network readiness continuously for
5s within120s. A selected effects profile also requires loaded calibration,
successful effects configuration and current passive effects health; an effects
fault restarts the continuous health window. Pending verification blocks
interactions but allows Wi-Fi recovery.
Failure marks the image invalid and requests bootloader rollback. If no valid
fallback exists, the updater cannot invent one; use the USB backup/recovery path.
Reinstalling a previously invalid image of the same version is rejected.

Use BLE diagnostics/check/install controls. Verify version and backend status
after reboot; a successful server download/build is not device OTA acceptance.
Physical tests must cover interrupted/power-loss writes, failed health,
fallback boot, credential retention and idle activation. Keep the last accepted
USB recovery package and initial flash backup privately available.

D159 verified one real development-channel update on9 October2026:
`kufr-68ee8f4b6048` automatically upgraded0.1.0→0.1.1 over trusted HTTPS,
booted ota_1 at`0x650000` and confirmed its boot VALID. A read-only NVS check
proved identity, PoP, network and OTA settings unchanged; another reset retained
the new slot and successful backend authentication. The CI USB recovery package
and pre-service flash backup remain private. This accepts the successful update
path; interrupted writes, invalid signatures/images and actual rollback still
need physical tests. Exact CI/build/hash/UTC evidence is in the
[implementation status](../kufr/docs/stav-implementace.md).
