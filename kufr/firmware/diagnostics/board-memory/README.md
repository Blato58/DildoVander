# USB-only board memory diagnostic

Standalone ESP-IDF project for the delivered ESP32-S3 N16R8 board. It does not
include the suitcase application, Wi-Fi, audio, camera, SD, provisioning or
actuator drivers. Keep all external components and external power disconnected;
place the board on a nonconductive surface and use only the USB-UART connector.
Firmware replacement requires the owner's authorization.

## Build and upload

From this directory, use the existing PlatformIO installation:

```powershell
pio run
pio run --target upload --upload-port COM7
pio device monitor --port COM7 --baud 115200
```

`COM7` was the delivered board's UART bridge on 2 October 2026; recheck the port
and chip identity before each upload. The platform is pinned to `espressif32@7.1.3`
(ESP-IDF 6.1). Generated sdkconfig and `.pio` outputs are ignored.

Before the first upload, read and retain the full 16 MiB factory flash outside
Git using esptool `read_flash 0 0x1000000 <backup-path>`, and record its SHA-256.
Do not erase the whole flash, burn eFuses, or provision credentials as part of
this diagnostic. Upload replaces the bootloader, partition table and application.
The application itself does not write flash or initialize NVS.

## Checks and limits

Boot initializes Octal PSRAM at 80 MHz with ESP-IDF's startup memory test enabled.
The application requires ESP32-S3 with two cores, 16 MiB flash and 8 MiB PSRAM,
then allocates **7 MiB** specifically from the external-memory allocator.
It writes and reads every 32-bit word using address-dependent values XORed with
`00000000`, `ffffffff`, `aaaaaaaa` and `55555555`, releasing the buffer afterward.
It checks heap integrity and prints `RESULT PASS` or `RESULT FAIL`, followed by
five-second `ALIVE` records. Memory sweeps yield to FreeRTOS idle/watchdog tasks.

Require all four `PATTERN PASS` records, correct capacity and 80 MHz logs, heap
integrity `PASS`, final `RESULT PASS`, and continued heartbeat without resets.
Missing final output, panic, boot loop or watchdog error means failure. Repeat
with multiple resets. `RESULT PASS` is not a full RAM march test, thermal soak,
flash endurance test, electrical measurement, native USB test or acceptance of
physical suitcase outputs. The fixed 7 MiB test leaves the remaining memory for
the runtime; it does not claim an exhaustive test of every PSRAM cell.

Actual hardware results and remaining B1 requirements belong in
[implementation status](../../../docs/stav-implementace.md).
