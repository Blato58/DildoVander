# Trvalá replay hranice journalu

## Rozhodnutí

Původní journal uchovává 32 UUID bez bezpečného uvolňování. Host reprodukce
20. 10. 2026 odmítla 33. operaci i při zdravém úložišti a bez pending operace;
restart historii zachová. Větší pole ani rotující seznam UUID problém replay
ochrany neřeší. Revize, generation a manuální sequence byly pouze v RAM.

Nová úplná identita operace je `(deviceId, operationEpoch, operationId)`.
Epochu ukládá zařízení. Původní zpráva si ponechá svoji epochu navždy; retry
nesmí převzít novou epochu z aktuálního snapshotu. Záměrně nově vydaný příkaz
s novou epochou je nová identita, i kdyby opakoval UUID. Na drátu musí být
epocha kanonický desetinný řetězec, aby ji JavaScript nezaokrouhlil.

## Dva implementační checkpointy

První checkpoint přidává bounded úložný formát v4 a testovaný engine. Zachová
legacy admission v epoše nula a **nezapíná produkční kompakci**. Explicitní
opt-in, posun epochy a uvolnění pinů jsou operace vlastníka; běžný `Begin`
nesmí při zaplnění sám evikovat historii. Legacy záznamy nesmějí získat
vymyšlený důkaz dokončení.

Čtení přijímá existující formáty v1–v3; první další úspěšný zápis je převádí
na v4, včetně stále legacy epochy nula. Firmware před v4 tento nový záznam
nepřečte a musí odmítnout pohyb. Při návratu na starý obraz journal nemaž:
obnov podporovaný obraz a servisně ověř stav. Provisioning ani kalibrace se
nemigrují. Automatický rollback během pending bootu nemá důvod přepisovat
journal; potvrzení OTA samo jeho obsah nemění.

Druhý checkpoint musí před zapnutím přenést epochu přes všechny producenty a
kontroly: manuální výběr, online návrh, offline show, simulator, backend,
snapshoty, památky a jejich ACK. Chybějící nebo stará epocha pak selže uzavřeně.
Současný čítač device revision nelze bez další migrace vydávat za tento plot.
Dokud druhý checkpoint není integrován, kapacitní omezení produkční legacy
cesty zůstává otevřené.

## Pořadí trvalých přechodů

Intent a rezervace důkazu musí být trvalé před prvním pohybem. Kompakce probíhá
jen na řízené idle hranici, zachová pending a pinované důkazy a zapíše novou
epochu s novou historií jako jeden atomický náhradní záznam. Nový snapshot se
publikuje až po potvrzeném commitu. Příkaz přijatý před změnou se nepřeznačí.

Pin fyzického důkazu lze uvolnit až po trvalém ACK, disposition nebo odstranění
všech odkazujících online/offline outbox záznamů. Ztracené uvolnění může držet
místo navíc; nesmí smazat potřebný důkaz. Reconciliation po restartu musí
proběhnout před admissions. Pokud důkazy obsadí celý bounded prostor, zařízení
ohlásí backlog a odmítne nový pohyb.

`JournalStorage::Write` vyžaduje atomický stav starý-nebo-nový. Návrat `false`
může znamenat ztracené potvrzení již provedeného zápisu; instance pak musí
zastavit admission a znovu načíst autoritativní stav. Host injekce výpadků
ověřuje model tohoto kontraktu, nikoli skutečnou flash nebo napájecí obvod.

Chybějící journal u již provisionované identity nesmí automaticky založit
epochu jedna. Integrace musí ověřit trvalý marker založení, nebo selhat
uzavřeně a vyžádat servis. Nová identity/provisioning, obnova zálohy, rollback
na starší firmware a tovární reset musí mít výslovný migrační postup.

## Důkazní hranice

Testovací engine má procvičit alespoň 1 000 scoped operací s opakovanou
kompakcí a restarty, staré epochy, duplicate/conflict, pinované památky,
legacy formáty a ztracené potvrzení commitů. Tento test sám nedokládá odstranění
produkčního limitu: to vyžaduje druhý checkpoint a ověření všech producentů.
Skutečné výpadky napájení NVS zůstávají součástí bench akceptace.
