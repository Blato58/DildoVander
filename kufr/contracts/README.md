# Sdílené kontrakty kufru

`control-message-v1.schema.json` je rozpracovaný lokální WSS protokol verze 1
podle kapitoly 5 [implementačního plánu](../docs/implementacni-plan.md).
Zatím popisuje `hello`, `welcome`, `state.get`, `control.acquire/release`,
`heartbeat/heartbeat.ack`, `selection.start`, `inventory.confirmReload`,
`conversation.text/cancel`, `command.ack`, `selection.result`,
`state.snapshot`, `safety.stop` a `safety.reset`. Adresáře
`fixtures/valid/` a `fixtures/invalid/` obsahují zprávy pro další adaptéry.
Schéma je zdroj typů a rozsahů; další mediální typy přibudou v E2.

`backend-openapi-v1.json` je HTTP kontrakt E2.6. Pokrývá výměnu servisně
provisionovaného device credential za 15minutový access token, založení
sezení, textový tah, WAV přepis, syntézu, polling/rušení operace, stažení
krátkodobého WAV a ukončení sezení. Pozdější kostýmní snímek do v1 první
verze nepatří.
Device credential a krátkodobý access token jsou oddělená bearer schémata;
token endpoint nepřijímá access token jako náhradu credential. Sezení,
operace a výsledky musí backend ověřovat proti identitě device tokenu,
nikoli pouze podle ID v URL nebo JSON. `Idempotency-Key` je UUID a případná
kolize se stejným klíčem, ale jiným tělem vrací 409.

Každý přijímač musí před validací schématu odmítnout neplatné UTF-8,
duplicitní JSON klíče, desetinný/exponenciální zápis celočíselných polí a
control zprávy nad 2048 bajtů, s výjimkou `conversation.text` do 8192 bajtů
a 1000 znaků textu. Samotné JSON Schema
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

`inventory.confirmReload` potvrzuje fyzickou kontrolu jedné přihrádky
`1..6` přes `payload: {compartment, inspectionConfirmed:true}`. Sdílí
`commandId`, pořadí, lease, deadline a expectedRevision s výběrem, vrací
`command.ack` a nový snapshot. Nevydá žádný požadavek na motor/západku;
masku dostupnosti musí zařízení trvale zapsat před ACK. Opakování stejného
příkazu masku znovu nepřepíše, stejné ID s jiným obsahem je konflikt.

`conversation.text/cancel` mají manual lease, boot a conversation generation;
text obsahuje jednorázové `clientTurnId` a expectedRevision. Identitu
`conversationId`, generaci a aktivní tah vrací snapshot od zařízení.
`conversation.ack` odlišuje přijetí, odmítnutí a duplicitní aktivní text.
`conversation.result` v současném simulátoru obsahuje pouze pevnou fake
odpověď se `simulated:true`, bez návrhu přihrádky. Přijetí textu v parseru
ani fake výsledek nejsou důkaz cloudového tahu či fyzického potvrzení.

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

`verify-backend-openapi.mjs` kontroluje reference, trasy, security schémata
a kladné/chybné HTTP struktury přes AJV. WAV byte stream zde není dekódován;
hlavičky, délku a PCM formát musí ověřit backendový upload parser. Jde o kontrolu dokumentu,
nikoli o integrační test skutečného backendu nebo důkaz vynucení scope,
idempotence, kvót a retence v běžící službě.
