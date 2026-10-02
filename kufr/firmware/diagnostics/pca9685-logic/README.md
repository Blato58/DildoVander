# PCA9685 logic diagnostic

Standalone ESP-IDF diagnostic for the delivered ESP32-S3 N16R8. It does
not run the suitcase application or enable any actuator. USB-UART powers
the ESP32; only the following four wires may be connected to the PCA9685:

| ESP32 label | PCA9685 label |
| --- | --- |
| 3V3 | VCC |
| GND | GND |
| 1 (GPIO1) | SDA |
| 14 (GPIO14) | SCL |

Disconnect USB before wiring. Leave V+, OE, servo outputs, screw terminals,
motors, servos and the external power converter unconnected. No camera or
MicroSD card is installed. VCC and the I2C pull-ups must use 3.3 V.

The test uses only GPIO1/14 and the individual I2C address 0x40. It probes
for ACK and reads MODE1/MODE2 five times at 100 kHz with bounded transfers.
Each combined read transmits only the register pointer byte; no register
value, output, frequency, General Call/reset or All Call write is sent.
It then deletes the bus and prints a UART heartbeat with the result.
There is no NVS, network, audio, OE or PWM initialization.

`RESULT PASS` means an ACK and five successful stable mode-register reads.
The PCA9685 has no unique identification register; this does not prove chip
authenticity, precise supply voltage, pull-up resistance, waveform margins,
servo/motor operation or the physical watchdog/inhibit safety circuit.
Measured acceptance remains in the [implementation status](../../../docs/stav-implementace.md).
After a full VCC power cycle the expected MODE1/MODE2 defaults are 0x11/0x04;
different stable values require interpretation and are not rejected by this test.

Build from this directory with `pio run -j 2`. Upload, when explicitly
authorized, with `pio run -j 2 --target upload --upload-port COM7`, then
capture UART at 115200 baud. Confirm the target board before upload and
preserve the previous application images. After the diagnostic, restore
the existing application from this directory:

```text
pio run -d ../.. -e esp32-s3-idf -j 2 --target upload --upload-port COM7
```

Its PCA actuator driver is still absent.

Protocol/API sources: [NXP PCA9685 datasheet](https://www.nxp.com/docs/en/data-sheet/PCA9685.pdf)
and the installed ESP-IDF 6.1 `driver/i2c_master.h`.
