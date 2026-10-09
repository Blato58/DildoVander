# Hardware/software integration boundary

The authoritative wiring remains [confirmed hardware](../kufr/docs/zapojeni-potvrzeneho-hardwaru.md),
[pin map](../kufr/docs/pin-mapa-esp32-s3.md), [camera](../kufr/docs/kamera.md)
and [power/effects](../kufr/docs/efekty-a-napajeni.md). New software introduces
no alternate pin profile or cancelled actuator hardware.

| Resource | Allocation / runtime contract |
| --- | --- |
| ESP32-S3 N16R8 | 16MB Quad flash;8MB Octal PSRAM80MHz; current SDK6.1/PlatformIO platform7.1.3 |
| PCA9685/MCP23017 | I2C0 SDA1/SCL14,0x40/0x20; coordinated driver |
| PCA output enable | GPIO2 direct OE, HIGH disabled; green rocker cuts actuator5V |
| Servos/U059 | PCA0..5/6..11; exactly one active actuator; calibrated timed moves |
| WS2812B | GPIO48,36 LEDs,6 per slot, startup limit64/255 |
| Audio B | BCLK21/WS41/mic42/output47; both DFR0954 remain mono; software mute |
| OV5640 | Existing connector mapping, SCCB4/5 on I2C1; one PSRAM JPEG buffer |
| MicroSD | CMD38/CLK39/DATA40,SDMMC1bit; conflicts with external JTAG39/40 |
| USB | GPIO19/20 reserved; USB-UART43/44 retained; no new strapping-pin assignment |
| BLE/Wi-Fi | Same radio; SDK coexistence, separate network/provisioning workers |

The physical port has compile-time B pin uniqueness/camera protection; camera
initialization still requires the measured service flag. SD similarly requires
`wand_hw/profile_b_ok` and `sd_verified`; the hardware service generator defaults
both peripheral verification flags false. No automatic card formatting or writes,
no camera images/visitor recordings saved to SD. The storage driver offers
bounded flat-name asset stat/read and releases failed mounts. There is no new
offline audio-asset playback feature claimed merely because storage exists.

OTA/network/SD work reserves the owner idle state before I/O. BLE stack lifecycle
is off the10ms control loop. Safety Stop is still processed while maintenance
holds admission; queued starts cannot enter. Static worker stacks and image/read
limits are enforced, but real heap/DMA/radio contention must be measured.

The current owner deliberately removed external watchdog, inhibit/high-side
modules and endstops. Timed `raised/resting` positions are assumptions after
calibrated elapsed time, not measured position. Boot/reset audio silence and
mechanical clearance are not certified by software tests.

Commission with the existing service/diagnostic projects, actuator power
disconnected first; measure5V output before connecting ESP, then one motor/servo,
LED brightness25%, microphone/speakers, camera and SD separately. Finally run a
combined BLE/Wi-Fi/audio/camera/SD/OTA soak and power/current/thermal tests.
Calibration, physical button/inspection, USB native port, PSRAM headroom and
safe reset states remain hardware acceptance steps. This task's builds do not
authorize a flash or powered actuator experiment.
