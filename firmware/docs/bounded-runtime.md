# Production control ownership and cadence

`production/main.cpp` constructs boot-lifetime dependencies, initializes safe
outputs and starts owners. `DeviceRuntime` in
[`src/features/device_runtime`](../src/features/device_runtime/DeviceRuntime.h)
is the single production consumer that applies physical effects, manual lease
changes, local Stop, inspection recovery, presentation, dance, offline scenes
and remote show arbitration. The existing storage runtime remains independently
owned by `EspIdfDeviceStorageWorker`; its durable acknowledgement still precedes
motion admission. Network, TLS, flash commits and file downloads do not run in
`DeviceRuntime`.

Each control pass obtains a logical `ControlMaintenanceGate` ticket, samples
physical inputs and deadlines, applies physical faults/button gestures and the
local owner's Stop/lease/control work, then considers maintenance enqueue and
ordinary show advancement. Idle is recomputed after local admission, so an OTA
request cannot act on an idle view from before a manual command. Autonomous
advancement retains the inbox urgent guard. A maintenance-held pass continues
Stop, fault and lease processing while refusing new admission.

`ControlSchedule` uses absolute monotonic 10,000 us releases. Execution time is
not added to the next period. A late pass handles current state once and counts
missed releases; it does not replay old actuator work or run an unbounded catch-up
loop. The task sleeps only until the next absolute release, subject to the
configured FreeRTOS tick resolution and scheduler. Published counters record
execution, release lateness and start-to-start gaps, including their maxima.
These software observations do not establish a hard real-time or physical
response guarantee.

## Services, reservations and locks

`EspIdfControlServices` performs HTTPS server startup/retries, service
advertisement startup, HTTP work scheduling and one-second diagnostic sampling.
Server/advertisement startup obtains the same logical safe-idle reservation used
for pairing; allocation/TLS startup can hold that reservation without holding a
control-loop mutex. Control transport Send/Close only enqueue bounded RAM work;
only the service Pump calls `httpd_queue_work`. Pump releases the RAM output
mutex before calling the SDK. A separate worker-only lifecycle mutex fences
Start/Stop/Pump and keeps the HTTP server alive through queue admission; it is
never acquired by control Send/Close or HTTP Work. SDK Stop drains callbacks
outside the RAM mutex. The actuator task never joins this worker. Existing OTA and
SD workers retain source I/O ownership; their control-facing Tick methods only
advance policy or queue work under short RAM state locks.

The authenticated inbox urgent callback latches direct OE shutdown before it
waits for the domain inbox guard. Eligibility is scoped to the currently
admitted connection, device and boot. Normal domain Stop completion remains on
the control owner. The urgent lock order is the inbox's short urgent gate,
followed by the adapter's GPIO-only critical section. The callback never takes
the I2C semaphore or calls a storage, network, audio, or domain action.

`RequestUrgentStop()` atomically advances a Stop epoch and drives PCA OE HIGH.
`EnableOutputs(true)` shares only the short GPIO critical section, preventing a
stale check/write from re-enabling OE after a completed urgent request. A new
Stop epoch defeats concurrent inspection recovery. The telemetry I/O wrapper forwards the epoch and recovery hooks unchanged and
records a latched enable request as disabled. Only successful
`PhysicalEffects::RecoverAfterInspection` can clear the inspected epoch after
output cleanup. Adapter failures also latch this gate. The gate is reported as
Stop pending, not as completed domain cancellation or measured acoustic silence.

## Physical I/O admission and remaining timing gap

The physical adapter admits I2C operations without waiting for its bus mutex.
Contention fails closed and deasserts OE. Runtime I2C operations share one 8,000
us admission deadline; each SDK internal wait is configured for at most 2 ms,
with the remaining deadline rounded down. A refused admission cannot obtain a
fresh budget by making another call. Startup uses a separate 10 ms per-transfer
setting while OE is disabled.

Boot resets all PCA channels and the ALL_LED override. Thereafter the sole PCA
adapter tracks channels explicitly written by Servo/Vibration and clears those
channels, including FULL_ON, on FullOff. With the domain's single-actuator power
guard this normally replaces a 64-byte reset with one four-byte register update.
No new position sensor or measured servo position is claimed.

The pinned ESP-IDF 6.1 I2C implementation reapplies its timeout to internal waits.
Its stuck-bus/NACK recovery can additionally execute a bus-clear path with a
50 ms timeout plus tick/scheduling overhead. Therefore the 8 ms value bounds
admission, **not** total faulty-bus execution. Multiple SDK recovery stages and
scheduler interference prevent claiming a 10 ms pass or 20 ms Stop bound from
these settings. Actual transfer duration, summed pass bus duration, budget
refusals and passes whose summed bus time exceeded 8 ms are published. The
GPIO-only urgent callback bypasses this bus path once scheduled; its physical
latency still requires board measurement.

RGB submission never waits for RMT completion. The transmitted symbol buffer
remains retained until a zero-wait completion poll succeeds. Requests arriving
while that frame is in flight coalesce in a separate latest-frame mailbox and
flush on a later pass. Acceptance is a queued software request, not LED readback.
Motor OE shutdown is independent of RGB completion.

Host checks cover absolute cadence, missed-release discard, service reservation
independence, coherent diagnostic copies, zero-wait I/O refusal, retained RMT
buffers, dirty-channel cleanup, stale-enable/Stop epochs and simulated SDK
latency overruns. They do not measure real Stop-to-OE latency, actuator response,
RGB timing, I2C fault recovery, audio DMA drain or acoustic cancellation tail.
