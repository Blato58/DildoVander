# Physical USB network/identity service

This separately flashable ESP-IDF 6.1 image is the `diag-network-service`
environment in the unified PlatformIO project. It initializes only NVS and the ESP32-S3 native USB
Serial/JTAG peripheral. It never starts Wi-Fi/BLE, drives actuator GPIO, enables
actuator power, erases NVS, prints credentials, or embeds supplied secrets in its
application binary. The main application has no permanent serial provisioning
endpoint. This image is an explicit physical maintenance session.

Before using it, disconnect actuator power with the green rocker, use a trusted
USB data connection to the board native USB port, and verify the exact board and
partition layout. The software cannot measure the rocker or disconnected power;
the typed physical confirmation is an operator assertion. UART bridge ports are
not supported by this service image.

Build from `firmware` without uploading:

```powershell
pio run -e diag-network-service
python -m unittest discover -s diagnostics/network-service -p 'test_*.py'
```

Installation is a separately authorized physical operation. Flash this service
image with `pio run -e diag-network-service -t upload --upload-port COM8`
only after confirming its partition
layout matches the installed firmware. Do not use erase-flash. If the current
partition layout differs, follow the controlled initial installation/migration
procedure in the parent firmware OTA documentation, including backups.

Run the existing PlatformIO Python environment interactively after installation:

```powershell
& "$env:USERPROFILE\.platformio\penv\Scripts\python.exe" diagnostics/network-service/provision.py --port COM8 --receipt "$env:USERPROFILE\DildoVander-private\kufr-setup.json"
```

Use the native USB COM port. The service publishes only readiness and a fixed
result code. The client refuses noninteractive secret entry. It prompts for:

- A 32 lowercase hex BLE proof of possession, or generates a random 128-bit PoP.
- The backend hostname, without scheme or path, if it needs changing.
- An optional existing backend device ID and its already registered 64 lowercase
  hex device credential. This does not register the identity with the backend.
- Explicit replacement permission for an existing PoP or backend identity.
- Optional OTA HTTPS origin, stable/development channel, P-256 PUBLIC signing key,
  automatic installation policy, and a 60-86400 second check interval.

The private receipt contains the BLE PoP and service UUID, never the backend
credential. It must be outside the repository, is created exclusively without
overwriting an existing file, and receives owner-only ACL permissions on Windows
before any secret is written. Keep it private; enter its PoP in the iPhone native
authorization dialog. No Wi-Fi password is passed over this USB service; configure
hotspot/fallback through the secured BLE endpoint after restoring the application.

The service limits one frame to 4096 bytes, parser depth to two object levels,
five failed attempts and five minutes per boot. It ends after one successful
request. Restore the main application image before operating the suitcase; the
service does not automatically boot or restart into that image.

Only these targeted namespaces/keys can change:

| Namespace | Keys |
| --- | --- |
| `wand_ble` | `pop` |
| `wand_network` | `backend_host` |
| `wand_identity` | `device`, using the existing KID1 serializer |
| `wand_ota` | `origin`, `channel`, `trust_pem`, `auto_install`, `check_interval` |

Hardware calibration, command journal, Wi-Fi profile blob and local pairing/TLS
identities are preserved. A multi-namespace service request is not a transaction;
`nvs-failed` can mean earlier requested fields were already saved. Inspect and
explicitly authorize replacement before retrying. Automatic OTA is disabled
before changing its trust/origin tuple. Provisioning does not activate secure
boot, flash encryption or eFuses: the current prototype NVS remains vulnerable to
physical flash extraction. NVS key deletion also does not promise forensic secure
erasure. Production encryption requires a separate controlled hardware procedure.

The Python tests exercise validation, bounded framing, serial response filtering,
receipt permissions and overwrite prevention. Building this image does not prove
native USB operation, NVS writes, radio provisioning or hardware safety on a board.
