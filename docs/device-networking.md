# Device networking and recovery

ESP32 is a standalone HTTPS client. The phone provides the preferred hotspot,
but is not a cloud relay. Wi-Fi recovery is independent of backend token/time
failures and of whether the observer app remains connected.

Priority is hotspot, then configured fallback. Each connection attempt waits
15 seconds for DHCP; failures use existing exponential backoff/jitter. Fallback
is chosen after a failed preferred attempt. While on fallback, a preferred-network
probe is limited to once per minute and admitted only during safe idle. A failed
probe restores fallback. This is a bounded reconnect probe, not aggressive
continuous scanning; it temporarily interrupts an idle fallback connection.

The owner reserves idle before changing the network. New manual leases, text,
voice, physical presentation and concurrent OTA/SD work cannot enter while the
reservation is held. Stop/status remain available. Radio loss itself does not
reset credentials or replay a selection. The existing voice transport observes
lost IP and cancels its bounded work, releasing the gate for reconnect.

NVS `wand_network/profiles_v1` stores the versioned two-profile record and last successful
profile. The supported loader migrates legacy `wand_network` Wi-Fi fields;
the configured backend host remains immutable during BLE network changes.
No general factory reset is exposed. Successful credential deletion/replacement
also removes stale legacy Wi-Fi values so they cannot reappear after reboot.

Replacement is staged in RAM, connected/tested, then committed as one blob.
Wrong password or failed NVS commit leaves the old record intact and tries to
restore the previously active network. A reported accepted request is not a
successful network connection. Native UI polls the final phase/result.

If both networks are unavailable, use the authenticated BLE recovery window,
correct credentials or manually re-enable the hotspot. If the window expired,
power-cycle deliberately. The suitcase retains its configuration and offline
manual safety rules. A changed cellular/WAN state can leave Wi-Fi connected while
backend connectivity is false; inspect both statuses.

Service initialization sets backend host, trusted time and device identity using
the existing service boundaries. TLS validates hostname/certificate through the
CA bundle; wrong time or rejected identity stays offline, never `insecure`.
Provider credentials exist only on the backend. [BLE instructions](ios-ble-provisioning.md)
and [OTA instructions](firmware-ota.md) describe independent trust material.

NVS survives ordinary app-slot OTA. This build does not enable flash encryption
or secure-boot eFuses; BLE encryption does not encrypt flash at rest. Protect
service/USB access and private provisioning files. Enabling those facilities is
a separately planned physical commissioning operation, not an unverified toggle.
