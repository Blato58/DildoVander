# SG90 servo reference

`SG90-Servo.stp` is the unchanged STEP file supplied by the owner on
2026-10-21. Its SHA-256 is
`e2bb320f91ece91deaff236bc0b12565e93ab54a3e619d6c5f2fba884c62aeea`.
The file header identifies CREO PARAMETRIC and a 2023-01-06 export; it does
not establish measurements of the delivered servo or the stock horn.

The STEP uses millimetres, with its output axis along +Y through X=5.625,
Z=0. `servo_reference.py` rotates it -90 degrees around Z and places this
axis on the cam axis. In module coordinates the resulting bounds are
(0.5, 101.5, 30.75) to (30.25, 133.75, 43.25) mm. The assembly applies the
module origin afterwards. The complete reference includes mounting ears,
gear housing, output stub and three short cable stubs; no horn is included.

The owner requested an additional 1 mm in height. The shared servo/cam/horn
axis is therefore Z=37 instead of Z=36 mm. The driven tray, lower stops and
guide retention also rise 1 mm; nominal 1 mm clearance under the upper
washers is preserved. The pedestal leaves 0.25 mm below the 12.5 mm thick
reference body. Open cradle notches clear the ears and cable outlet, and
the first tie moves clear of the outlet. Wiring approach anchors fan out
just in front of the cable stubs and escape beside the cradle before joining
the rear corridor. Their spacing is provisional, not measured plug pitch.
Cable colours/pin order and the real flexible exit require inspection.

The stock horn remains a provisional design envelope with a blind socket
for the reference output stub. The STEP establishes reference geometry,
not physical fit, loaded torque, spline engagement or manufacturing acceptance.
