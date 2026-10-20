# Digital Twin wire contract v1

One observer projection for the native host and ESP32. It never grants control.
All uint64 clocks, revisions and sequences are canonical unsigned decimal strings.
Unknown values are JSON null, never substituted with zero. No visitor text,
media payload, credential or command is included in a telemetry snapshot.

`POST /v1/twin/ingest/{instanceId}` accepts a full snapshot below using a
dedicated source credential. The server binds instance and mode to that credential.
`GET /v1/twin/instances` and `GET /v1/twin/instances/{id}` require a separate
observer session. A snapshot is atomic; polling/reconnection always starts from
the latest full snapshot. Bounded trace events retain their native sequence and
visible drop counter. Server receipt time is distinct from device monotonic time.

```json
{
  "schemaVersion": 1,
  "source": {
    "mode": "simulation",
    "instanceId": "sim-development",
    "deviceId": "sim-development",
    "bootId": "unique-per-process-boot",
    "sessionId": "unique-per-process-boot",
    "firmwareVersion": "source-build",
    "buildId": "source-digest",
    "hardwareProfile": "B-effects",
    "sceneBundleHash": "compiled-bundle-hash",
    "cadManifestRevision": null,
    "calibrationRevision": "1"
  },
  "sequence": "1",
  "timeMs": "0",
  "revision": "1",
  "reported": {
    "state": "idle",
    "slot": 0,
    "readyMask": 63,
    "generation": "0",
    "needsRecovery": false,
    "physicalVerification": "unavailable",
    "servo": [{"slot": 1, "pulseUs": null, "estimate": "unknown"}],
    "outputsEnabled": false,
    "ledGrb": null,
    "vibrationMask": 0,
    "audio": "unavailable"
  },
  "model": null,
  "trace": {},
  "transport": {"droppedSnapshots": "0"},
  "capabilities": ["observe", "presentation"]
}
```

`reported` comes from runtime and accepted driver operations. Servo `estimate`
is `unknown`, `moving`, `raised` or `rest`, and is always a calibrated estimate.
`ledGrb` is the accepted 108-byte post-scaling frame, or null when no accepted
frame is known; it is never optical measurement.
`audio` is enqueue/drain evidence, never proof of audible sound. A live producer
sets `model` to null. A virtual HAL may add `model: {slots:[{slot, position,
jammed}], evidence:"simulated"}`. Its kinematic positions do not feed back into
firmware. On disabled output or Stop the virtual model freezes at the last
position; firmware estimates may become unknown. Trace uses the existing
ExportPresentationTrace contract without replacing or fabricating its evidence.

The server rejects identity changes, regressing sequence/time within a boot,
duplicate snapshots with conflicting contents, oversized records, unknown
fields that could carry private data and a retired boot attempting to overwrite
the current boot. A new boot is a new snapshot epoch, not continuation. Browser
source switching discards previous state and waits for the selected snapshot.
Age is computed from server receipt age, not subtraction of unrelated clocks.
No data after 2 seconds is stale; after 10 seconds offline. Never fall back to
simulation or continue an animation across stale/offline data.

Simulation commands use a distinct control credential/session and only route to
an allowlisted native host. Read-only sessions cannot command either source.
Native host line protocol: UTF-8 ASCII words, one request/JSON response per line;
`SNAPSHOT`, `TICK <absolute-ms>`, `TEXT <operation-uuid> <slot> <main|short>`,
`STOP`, `INSPECT`, `FAULT <none|servo|led|journal|jam> <slot>`,
`DANCE <operation-uuid>`, `DANCE_STOP`. No web route forwards arbitrary lines.
TEXT represents deterministic synthetic service input; the native runtime must
validate and admit the proposal through PendingProposal and the production
presentation owner. Unsupported media/integration capabilities fail explicitly.
The process preserves the journal on restart. Creating a clean instance requires
a new identity and new storage path; it never deletes an existing journal.

Deterministic time is owned by the server, independent of observers; pause, step
and speed affect only this injected clock. Integration runs use wall clock.
Control ownership is exclusive per simulation session and survives viewer polling;
disconnection never automatically replays or resets a device.

CAD manifest v1 is generated from parameters/mechanism, identifies millimetres,
CAD coordinate axes, stable component names, slots/channels, servo/cam pivots,
axes and kinematic limits. Its hash is independent of operational calibration.
A missing or incompatible revision must remain visible in the observer.
