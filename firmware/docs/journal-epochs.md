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

## Implementační checkpointy

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

Navazující checkpoint přenáší původní epochu přes firmware snapshoty, manuální
řízení, online požadavky a návrhy, offline show a jejich trvalé důkazy a ACK.
Chybějící pole znamená legacy nulu, nikdy aktuální epochu zařízení. Rozhraní
LAN přidává capability `operationEpoch`; klient poté žádá
`state.get {"includeOperationEpoch":true}`. Starý klient dostává původní
snapshot a pouze legacy offline záznamy. Scoped záznamy zůstávají v zařízení
a legacy ACK je nemůže odstranit. Retry si uchovává původní desetinný řetězec,
včetně hodnot nad přesností JavaScript `Number`.

Offline blob v2 obsahuje uloženou epochu, revizi a fingerprint. Čtení skutečného
POD formátu v1 používá původní velikost i stride; nepřepisuje jen číslo verze.
Starý pozitivní důkaz lze vyhledat v historické epoše nula i po adopci, ale
neopravňuje k nové operaci v aktuální epoše. Scoped důkaz vyžaduje přesnou
čtveřici epochy, UUID, přihrádky a fingerprintu. Fingerprint nové online
prezentace zachytí vlastník řízení podle zvolené main/short varianty před
trvalou přípravou a pohybem; při obnově se nevymýšlí.

Backendová trvalá korelace a outcome ACK používají popsanou
[migraci backendu](backend-operation-epochs.md). Marker založení identity a
reconciliation pinů popisuje navazující
[implementace a její produkční brány](journal-establishment.md).
Produkce nyní skládá všechny trvalé konzumenty přes společného vlastníka úložiště
a po explicitním servisním založení používá scoped epochu a bounded kompakci.
Čítač device revision není epocha. Třicet dva položek omezuje jedno uchované okno,
nikoli životnost zařízení; plně pinované okno bezpečně odmítne další operaci.
Legacy či nejasně založené zařízení bez servisní migrace pohyb nepovolí.

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

Registrované testy procvičují přes 1 000 skutečných lifecycle každého producenta
přes produkční startup, opakovanou kompakci, restarty a uchované důkazy. Samostatné
řezy ověřují staré epochy, duplicate/conflict, legacy formáty a ztracené ACK commitů.
Simulovaný příjem ACK a aktuátory nenahrazují ověření konkrétní desky.
Skutečné výpadky napájení NVS zůstávají součástí bench akceptace.
