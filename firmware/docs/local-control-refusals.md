# Očekávaná odmítnutí lokálního řízení

Autentizované, správně formátované spojení zůstává použitelné při běžném
odmítnutí kvůli vytížení, údržbě, tanci, staré revizi nebo potřebnému zotavení.
Odmítnutí nepovoluje fyzickou akci a neobnovuje vypršelý lease. Neplatné
ověření, framing, boot identity nebo lease a selhání výstupního transportu
nadále mohou spojení uzavřít.

Výběr a doplnění inventáře používají existující `command.ack`, hlas a další
funkce své stávající ACK. Retired text engine nadále vrací explicitní upgrade
rejection. Nový `control.ack` koreluje acquire/release/reset pomocí
`requestMessageId`, operace, boot identity, event sequence a revize. Úspěch má
`accepted: true` a `errorCode: null`; očekávané odmítnutí obsahuje typovanou chybu.

## Kompatibilita

Nový firmware inzeruje schopnost `controlAck`. Nový klient pouze po jejím
přečtení přidá `acknowledge: true` do acquire/release/reset. Firmware odešle nový
typ zprávy pouze na tento výslovný opt-in. Starší klient dostává původní
snapshoty; novější klient se starším firmwarem dál posílá původní zprávy.
Nesouvisející nebo opožděný ACK nesmí dokončit novější čekající požadavek.

Schopnost je povolený řetězec ve stávajícím v1 snapshotu, takže starší strict
validátor přijme její inzerci. Nový request field a nový typ ACK by původní
strict validátor odmítl: proto se nesmějí posílat bez vyjednání. Starší klient
nemá nový typovaný důvod odmítnutí; jeho dostupným důkazem zůstává snapshot.
Jeho původní acquire timeout může později spojení obnovit; tuto klientskou
reakci nelze opravit zprávou, kterou jeho strict decoder nezná.

## Ověření

```sh
python firmware/tests/run_show_tests.py --only local_control_owner_test --only local_control_codec_test --only local_control_output_test
npm --prefix contracts test
node --test mobilni-aplikace/tests/simulator_device_connection.test.mjs mobilni-aplikace/tests/native_control_socket.test.mjs
npm --prefix mobilni-aplikace run build
```

Testy používají skutečný C++ serializer i strict validátor kontraktu a ověřují
následující status/Stop, retry, lease expiry a korelaci čekajícího klientského
požadavku. Změna neobsahuje nové obrazovky ani změnu fyzického bezpečnostního
pravidla. Signed iPhone a fyzický kufr vyžadují samostatnou akceptaci.
