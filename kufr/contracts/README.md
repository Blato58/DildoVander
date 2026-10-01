# Sdílené kontrakty kufru

## Hlas ESP v lokálním control v1 (D117)

`voice.configure` nese běžnou device/boot/messageId obálku a
`mode=off|realtime`, `voice=cedar|marin|coral`. `voice.start` přidává
`expectedRevision`; `voice.stop` jen společnou obálku. `voice.ack` vrací
requestMessageId, bootId, accepted a aktuální voice status.
Spárovaný pozorovatel smí tyto explicitní audio akce; start navíc vyžaduje
bezpečný standalone snapshot. Žádný z příkazů přímo neovládá pohon.
Volitelný `state.snapshot.voice` obsahuje available/mode/voice/phase/error;
nepřítomnost znamená nepodporovanou audio cestu, nikoli implicitní Realtime.

`control-validator-core.mjs` je společný strict JSON/schema validátor pro
Node i nativní mobilní adaptér. Node wrapper `control-validator.mjs` načítá
stejné schéma ze souboru; mobil jej importuje do Vite balíčku. Duplicity,
necelé numerické tokeny a délkové limity se kontrolují v obou prostředích.

`control-message-v1.schema.json` je rozpracovaný lokální WSS protokol verze 1
podle kapitoly 5 [implementačního plánu](../docs/implementacni-plan.md).
Zatím popisuje `hello`, `welcome`, `state.get`, `control.acquire/release`,
`heartbeat/heartbeat.ack`, `selection.start`, `inventory.confirmReload`,
`conversation.text/cancel`, `command.ack`, `selection.result`,
`state.snapshot`, `safety.stop` a `safety.reset`. Adresáře
`fixtures/valid/` a `fixtures/invalid/` obsahují zprávy pro další adaptéry.
Schéma je zdroj typů a rozsahů; další mediální typy přibudou v E2.

`state.snapshot.phase: presenting` označuje skutečně aktivní autonomní
cyklus pevné hůlky (vysunutí, prezentace a návrat). Jde o rozšíření enumu
v1 po přijetí autonomie; strict klienti musí aktualizovat své schéma.
Mobilní simulátor čte phase jako string a jakoukoli ne-ready fázi mapuje
na probíhající sekvenci. Kalibrace 0 nesmí být vydávána za ready, mikrofon
bez I2S je unavailable a terminální výsledky mají physicalVerification
unavailable. Předání snapshotu není fyzická akceptace.

`backend-openapi-v1.json` je HTTP kontrakt E2.6. Pokrývá výměnu servisně
provisionovaného device credential za 15minutový access token, založení
sezení, textový tah, WAV přepis, syntézu, polling/rušení operace, stažení
krátkodobého WAV a ukončení sezení. Pozdější kostýmní snímek do v1 první
verze nepatří.
Autentizované `GET /v1/diagnostics` (D108) vrací pouze skutečně nakonfigurovaný
režim dialogu a hlasu, verzi katalogu a WAV formát. Nevolá AI, nevrací obsah
paměti ani přístupová tajemství a zachovává `Cache-Control: no-store`.
Device credential a krátkodobý access token jsou oddělená bearer schémata;
token endpoint nepřijímá access token jako náhradu credential. Sezení,
operace a výsledky musí backend ověřovat proti identitě device tokenu,
nikoli pouze podle ID v URL nebo JSON. `Idempotency-Key` je UUID a případná
kolize se stejným klíčem, ale jiným tělem vrací 409.

Každý přijímač musí před validací schématu odmítnout neplatné UTF-8,
duplicitní JSON klíče, desetinný/exponenciální zápis celočíselných polí a
control zprávy nad 2048 bajtů, s výjimkou `conversation.text` a `conversation.result` do 8192 bajtů
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

`media-frame-v1.mjs` kóduje a čte binární `KF` rámec z kapitoly 7 plánu.
`MediaStreamVerifier` hlídá jeden domluvený stream, 0-based pořadí chunků,
limity 4096 B na payload a 1,92 MB PCM capture / 2 MiB PCM playback /
512 KiB JPEG na celé médium, 1 s bez pokroku a konečný SHA-256. Při chybě
stream uzavře bez možnosti pokračovat; neukládá obsah média. Nyní jde o
čistý hostitelský parser/test, bez WSS media socketu, audio driveru,
alokací PSRAM či HTTP uploadu. Konec streamu musí přijít až po úplné délce;
samotný hash není oprávnění k přehrání.

`media-control-v1.schema.json` a `media-validator.mjs` tvoří samostatný
přísný JSON kontrakt pro `/v1/media`. `media.hello` váže telefon na
aktuální boot/manual lease/conversation generation a náhodný `mediaTicket`,
který se po acquire posílá pouze vlastnickému control socketu zprávou
`media.authorize`, nikdy v observer snapshotu. `media.open` přidělí
device stream ID a PCM16 mono 32 kHz limity, `media.credit` obnovuje
16KiB okno a `media.end/cancel` končí stream. `verify-media-control.mjs`
hlídá povinný kontext, formát, rozsahy, SHA-256 tvar a zákaz polí pohonu.
Současný simulátor přijímá jen telefonní PCM capture; typy rámců PCM
playback/JPEG zůstávají zatím pouze binárními hodnotami kontraktu.

## Party opening a paměť večera

HTTP OpenAPI nově popisuje `/v1/conversations/{id}/opening` bez textu,
`GET /v1/evening-memory` pro device-scoped ID/počty a
`DELETE /v1/evening-memory/{id}` pro smazání konkrétního večera. Opening
používá původní turn operaci a korelaci; nemění WSS control zprávy ani
oprávnění fyzického pohybu. Starší klienti mohou dál používat `/turns`.
Skutečný firmware/telefon musí nové operace teprve zapojit.

D97 přidává skutečný `conversation.result` s `simulated:false`; simulátor nadále
vrací true. Limit reply je 500 Unicode znaků (firmware nejvýše 2000 UTF-8 bajtů),
stejně jako backend. `conversation.text` i `conversation.result` mají obálku do
8192 bajtů kvůli escapování; jiné control zprávy zůstávají do 2048. Strict klient
přebírá nové schéma a limit. Původ backendu neprokazuje placenou AI, TTS ani pohyb.

D98 konkretizuje místní HTTPS `POST /v1/pair` pro již servisně provisionovanou
TLS identitu a jednorázový párovací záznam. Klient připne SPKI z QR a pošle
`Content-Type: application/json` a objekt `{"protocolVersion":1,"pairingSecret":"lowercase hex"}`
(secret 16–64 bajtů, tělo do 192 bajtů, žádný query token/Transfer-Encoding).
Úspěch vrací `{"protocolVersion":1,"token":"64 lowercase hex znaků"}` s no-store.
WSS používá token v Authorization Bearer; zařízení uloží jen hash. 400 znamená
formát, 401 nesprávné tajemství nebo nedostupné RNG, 409 již spotřebované párování,
413/415 velikost/typ, 503 poškozené úložiště. Ztráta úspěšné odpovědi vyžaduje
fyzický servisní reset. Server nepotřebuje backend token pro párování, vyžaduje
však aktivní STA pro RNG. Servisní AP, QR/Keychain/native klient a skutečný USB
zápis po fyzické bráně zůstávají navazující části; žádná identita není v image.
