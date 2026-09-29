# Sdílené kontrakty kufru

`control-message-v1.schema.json` je první část lokálního WSS protokolu verze 1
podle kapitoly 5 [implementačního plánu](../docs/implementacni-plan.md).
Zatím popisuje `selection.start`, `command.ack`, `selection.result`,
`state.snapshot`, `safety.stop` a `safety.reset`. Adresáře
`fixtures/valid/` a `fixtures/invalid/` obsahují zprávy pro další adaptéry.
Schéma je zdroj typů a rozsahů; další typy a OpenAPI backendu přibudou v E2.

Každý přijímač musí před validací schématu odmítnout neplatné UTF-8,
duplicitní JSON klíče, desetinný/exponenciální zápis celočíselných polí a
control zprávy nad 2048 bajtů. Samotné JSON Schema
tyto vlastnosti syrové zprávy neověří. Validace schématu nenahrazuje ověření
TLS identity, tokenu, `bootId`, ručního lease, monotónního času, revision,
idempotence a journalu. Hodnota `selectionSource` je pouze auditní údaj,
nikoli oprávnění spustit výběr. `physicalVerification: "unavailable"` výslovně
neprohlašuje fyzické otevření za změřené.

Příkaz `safety.stop` má vlastní `messageId`, bez lease nebo revision; právo
odeslat jej stále vyžaduje autentizovaného klienta. `safety.reset` vyžaduje
ruční lease a potvrzení kontroly obsluhou; samostatný fyzický reset je jiný
lokální vstup firmware. Snapshot nese platný lease pouze v ručním režimu.
Jeho `eventSequence` je nejvyšší pořadové číslo události zahrnuté v atomicky
pořízeném snímku; klient podle něj po reconnectu nebo mezeře pozná další
očekávanou událost. Pole `faults` používá samostatné kódy poruch, nikoli
chybové kódy odmítnutých příkazů.

## Ověření

Použij Node 24.21.0 podle `.nvmrc` a ve složce `kufr/contracts/` spusť:

```text
npm ci
npm test
```

`control-validator.mjs` exportuje `validateControlMessage(bytes)` pro příjemce
syrových control zpráv v Node. Funkce provede přísné parsování a stejné
schéma, které používá následující test. Síťová facade jej teprve zapojí.

Test v `verify-fixtures.mjs` ověřuje schéma v přísném režimu a pozitivní i
negativní fixtures včetně duplicitního klíče, chybných typů, hranic, stavu
lease a velikosti zprávy. V produkčních adaptérech se stejná pravidla musí
ověřit znovu v jejich skutečném parseru; tento test zatím nedokazuje běh
firmwaru, mobilní aplikace ani síťového simulátoru.
