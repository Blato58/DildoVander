# Backend kufru (E7)

## Podepsaná firmware distribuce (D158)

Veřejné read-only `GET /v1/firmware/manifests/{channel}.json` a
`GET /v1/firmware/artifacts/{sha256}.bin` nevyžadují device token. Autoritu
určuje P-256 podpis provisionovaný na ESP, nikoli samotný checksum.
`Backend:Firmware:Directory` je absolutní admin-owned adresář; neplatná
konfigurace/release vrací 404. Shared concurrency limit 4, queue 0, 429.
Root-only publikace ověřeného CI artefaktu udržuje podpisové klíče mimo
runner/source/image. [Kontrakt a přesný deployment](../../docs/firmware-ota.md).


## Volitelná fotografie kostýmu a rozpočet (9. 10. 2026)

`POST /v1/conversations/{id}/image` přijme jednu vědomě aktivovanou
fotografii pro toto sezení. iPhone Debug ji před odesláním zobrazí;
samostatná ESP kamera pořizuje a odesílá snímek po explicitním souhlasu
bez náhledu na telefonu. Vyžaduje samostatné
`consent.image=true`, device token, UUID `Idempotency-Key`, `Client-Turn-Id`,
`Boot-Id` a hlavičky `Generation`, `Device-Revision`, `Catalog-Version`.
Tělo je přímo `image/jpeg`, nejvýše 262144 B a 640 × 640 pixelů; přijímají se
8bitové baseline/progressive JPEG, bez EXIF/GPS, komentářů a vložených thumbnailů.
Klient po preview JPEG znovu zakóduje. Upload musí skončit do 60 s.
Prováděj ho před `/opening`, `/realtime-live` nebo `/gpt-live`; po zahájení
dialogu se odmítne. Výchozí fake nečte obsah fotografie a nevolá AI.

Responses adaptér posílá skutečný JPEG přes oficiální SDK jako `input_image`
s base64 data URL, `detail=low`, `store=false`, bez retry, s deadlinem 45 s.
Vrací výhradně přísný stručný popis kostýmu, možnou fiktivní postavu a nejistotu;
nesmí určovat identitu člověka, věk, gender, biometrii ani pohony. Odpověď `202`
obsahuje `operationId`, `kind=image`, `status=pending`. Poll přes stávající
`GET /v1/operations/{id}` vrací obvyklý envelope; dokončený `result` má
`description`, `possibleCharacter`, `uncertainty` (`low/medium/high`), `source`
(`photo/fake/manual`) a `expiresAt`. Ztracený nebo expirovaný výsledek se placeně
neobnovuje. Jeden přijatý image upload na rozhovor; ani novým klíčem jej neopakuj.

Volitelná ruční oprava přes `PUT /v1/conversations/{id}/costume-context`
s tělem `{ "description": "červený plášť" }` a stejnými korelačními hlavičkami
nevolá AI. Nepotřebuje obrazový souhlas; nejvýše 240 znaků bez řídicích znaků,
prázdný text odstraní vodítko. Vrací `200` s dokončenou operací `kind=costume`.
Prázdný popis v ručním výsledku je pouze potvrzení odstranění; do žádného
provider promptu se nepředává. Odstranění zruší i výsledek image operace
(`status=cancelled`, `result=null`), takže poll nikdy nevrátí prázdný dokončený
popis fotografie. Rozpracovaná analýza přesto doúčtuje spotřebu, vynuluje JPEG
a uvolní provider slot. První upload po odstranění se odmítne s 409
`costume_context_discarded`; neprázdná nová ruční oprava může vodítko obnovit.
Popis z fotografie/fake musí obsahovat nejméně jeden znak.
Ruční údaj má přednost i před pozdní odpovědí fotografie. Pro změněnou opravu
použij nový idempotency key. Nejvýše osm oprav na rozhovor chrání RAM. Opravu dokonči před hlasem. Kontext se předává
textovému Responses, Realtime, GPT-Live i samostatnému Sol rozhodování jako
nejistá data o kostýmu; není mluveným důkazem identity nebo povahy návštěvníka.

Vlastněné vstupní JPEG, base64 znaky a UTF-8 provider request buffery jsou pouze
v RAM a při dokončení/chybě se vynulují; upload 60 s + analýza 45 s je pod 120 s.
Nemáme záruku vymazání interních kopií SDK/TLS/OS nebo dat u poskytovatele.
Strukturovaný popis ani snímek se přímo neukládají do journalu, paměti, logů
či na SD. Lokální lookup/poll výsledku expiruje po 120 s. Expirace ruční opravy nepovolí
pozdní fotografii obnovit staré vodítko; nový první upload se odmítne 409
`costume_context_expired` před účtováním, pozdní již odeslaný výsledek selže. Již přijatý hlasový
provider prompt může vodítko držet do uzavření tohoto rozhovoru; celé DELETE
rozhovoru jej zruší. DELETE image operace odstraní její vodítko z budoucích
tahů, pokud jej nepřepsala ruční oprava. Běžná dokončená textová/mluvená
výměna zůstává v paměti večera podle existujícího souhlasu a může obsahovat
vyslovenou zmínku o kostýmu. Fotografie nevytváří identitu ani vazbu na hosta.
Všechny odpovědi mají `Cache-Control:no-store`. Selhání fotografie ani její
rušení neukončí dialog; Stop rozhovoru ruší i rozpracovanou analýzu.
DELETE rozhovoru s placenou fotografií vrátí 204 až po vynulování JPEG a uvolnění
sdíleného provider slotu. Čeká nejvýše 5 s; 503 `image_cancel_pending` znamená,
že rozhovor už skončil, ale cleanup ještě běží. Opakuj DELETE se stejným klíčem
a nový rozhovor bez fotografie otevři až po 204. Neopakuje to AI ani upload.

Majitel 9. 10. 2026 zrušil všechny softwarové finanční stropy. Rozpočet řídí
výhradně OpenAI projekt. `Backend:Usage:*CapMicroUsd` a staré deployment cap
hodnoty se ignorují, včetně chybného/nízkého nastavení. Režimy potřebují
explicitní volbu a platný provider klíč. Účetní rezervace, settlement usage,
retence a nejasné výsledky zůstávají evidované, nikdy neblokují další práci.
Pevné technické hranice (jeden placený slot, počet tahů/delegací, velikosti,
tokeny, audio a čas) zůstávají. Root-owned deploy helper musí být před dalším
nasazením aktualizován z `deploy/deploy-helper.py`; nový payload neposílá cap
hodnoty a stará instalovaná verze helperu není důkazem této změny na VPS.

## OpenAI SDK (D119)

Všechny provider operace používají oficiální .NET SDK **2.14.0**:
Responses přes `ResponsesClient`, kompatibilní STT/TTS přes `AudioClient`
a Realtime přes `RealtimeSessionClient`. SDK protocol overload zachovává
přísná tool schémata i `languages[]` GPT-Transcribe (typed options připnuté
verze mají jen starší `language`). SDK transport omezuje příjem před
bufferováním (Responses 64 KiB, STT 16 KiB, PCM dosavadních 30 s),
produkční HTTP klienti odmítají redirect a retry policy je **0**.
Rezervace zůstávají před voláním; nejasný výsledek se placeně neopakuje.
ESP nyní používá existující `realtime-live` API se stejnými device tokeny,
VAD, pořadím vstupních bloků, dohráním a kontrolou návrhů jako telefonní Debug.

D114/D116 přidávají přímý Realtime hlas přes oficiální OpenAI .NET SDK 2.14.0,
model `gpt-realtime-2.1` od D135, s `reasoning.effort=medium` od D140 na přání majitele. Textový Sol i dosavadní fake/STT/Sol/TTS zůstávají.
`POST /v1/conversations/{id}/realtime-turns` vyžaduje device token, UUID
idempotency key, audio souhlas a režimy `responses` / `openai`.
OpenAI klíč zůstává na backendu. SDK obsluhuje typované příkazy a události;
omezený transport odmítá redirect a WebSocket zprávu nad 256 KiB.
Realtime SDK API je experimental, proto je verze připnutá a serializace
PTT ověřená testem. Backend připojení používá GA API bez beta hlavičky.
Výchozí iOS Debug používá D116, jedno provider spojení pro celý rozhovor:
`POST /v1/conversations/{id}/realtime-live` přijímá opening JSON do 4096 B
a vrací dlouhý NDJSON stream. `session` obsahuje `streamId`, `listening`
nové `inputId`. Telefon průběžně posílá PCM16 mono 24 kHz přes
`POST /v1/conversations/{id}/realtime-live/{streamId}/audio`, nejvýše
9600 B v jednom požadavku, přesné hlavičky `Input-Id` a `Input-Sequence`
od nuly. Jeden upload v letu, žádný placený replay. Pozdní blok právě
ukončeného vstupu se zahodí s 204, nesmí ukončit další poslech.
Server VAD používá 650 ms ticha/300 ms prefix/threshold 0.5. Automatické
provider odpovědi jsou vypnuté: každý tah nejprve rezervuje backend.
`speech_stopped` zavře mikrofon, následují `started`, `audio`, `visitor`,
`completed` s výsledkem a konzervativním `costMicroUsd`. Telefon potvrdí
skutečné dohrání na `/played` s přesným `operationId` do 35 s; potom
se rezervuje a otevře další poslech. Událost `listening` smí předběhnout
HTTP odpověď potvrzení. Jedno sezení nejvýše 8 min/10 tahů, heartbeat 10 s,
tah včetně poslechu 45 s, vstup/výstup nejvýše 30 s. DELETE rozhovoru,
disconnect nebo ukončení služby ruší socket i v mezeře čekání na dohrání.
Tento automatický poloduplex neumožňuje přerušit mluvící kufr.
NDJSON a malé binární HTTPS požadavky fungují přes stávající proxy;
veřejný WebSocket upgrade není potřebný. Starý `/realtime-turns` je PTT záloha.
Při změně NuGet závislostí na Windows regeneruj CI lockfiles přes
`dotnet restore kufr/backend-tests/BackendTests.csproj -r linux-x64 -p:RuntimeIdentifier=linux-x64 -p:SelfContained=true --force-evaluate`
z kořene. Explicitní singular `RuntimeIdentifier` zabrání přidání hostitelského
win-x64; povinné linuxové CI pak používá locked restore bez změny lockfile.

Vstup obsahuje korelaci textového tahu, `opening`, `text`, `wav`, `voice`
(`cedar`, `marin`, `coral`) a `speed` 0.8–1.2. Opening má prázdný text a
`wav:null`; další tah má právě text nebo base64 PCM16 mono 32kHz WAV
(100 ms až 30 s). JSON tělo nejvýše 2 600 000 B. Backend provede filtrovaný
převod na 24 kHz, vypne VAD a po PTT explicitně commitne audio.
Model slyší nahrávku přímo; souběžný český přepis slouží paměti večera,
není předřazeným krokem pro vygenerování odpovědi.

Odpověď je HTTPS `application/x-ndjson`: `started` s operation ID/korelací,
`audio` s base64 PCM16 mono 24 kHz po blocích do 32 KiB, volitelný `visitor`,
a `completed` s běžným `TurnResult` nebo technický `failed`. Každý blok se
hned flushne; `X-Accel-Buffering:no` vypíná proxy buffering. Nejvýše 30 s
výstupního zvuku, deadline 45 s včetně přepisu. Stop, odpojení a restart
ruší provider socket. Dokončený výsledek lze číst přes stávající operaci;
stream ani placené audio se neopakují (retry vrací 409).

Otázky zazní průběžně; jejich zvuk je před dokončením provizorní. Výběr
vyžaduje funkci `propose_wand`, backendovou kontrolu otázkové brány/masky,
potom druhou krátkou hlasovou odpověď. Teprve `completed` obsahuje návrh.
Debug stream nikdy nespouští pohon. Po úspěšném dokončení se uloží skutečný
přepis a replika do aktivního večera; Stop nepřipojí pozdní obsah.
Paměť a nákladový journal mají oddělené SQLite soubory. Pokud po zápisu
paměti selže dokončení journalu, tah se ohlásí jako neúspěšný, ale výměna
může zůstat v paměti. Toto není atomická transakce přes obě databáze.

Realtime sdílí placený slot. Účetní rezervace pokrývá nejvýše dvě odpovědi
(PTT 1024, průběžný hlas 768 tokenů), serializovaný prompt/paměť a audio;
navíc 0,01 USD pro souběžný přepis. Usage se vypořádá při konzervativních
sazbách 4 USD/1M textového vstupu, 32 USD/1M audio vstupu a 64 USD/1M
výstupu. Chybějící usage ponechá účetní odhad; další práci neblokuje.
Kontext ani hlasový tokenový limit se nesnižují kvůli peněžní rezervaci.
Staré provider audio/context se mezi tahy odstraní a backend vloží aktuální
omezenou paměť/history. Rozpočet řídí OpenAI projekt.
Lokální testy nedokládají latenci ani kvalitu placeného hlasu.
Autorizovanou syntetickou zkoušku D116 spouští `kufr-backend-realtime-live.yml`:
jeden rozhovor s českou offline syntetickou řečí, nejvýše čtyři odpovědi,
peněžní strop zajišťuje OpenAI projekt. Report měří první audio,
celý tah a konzervativní náklad, ukončí sezení a obnoví předchozí večer.
Není to poslech na skutečném iPhonu ani účtenka OpenAI.

Kontrakt je v [OpenAPI](../contracts/backend-openapi-v1.json).
Nasazení backendu a nová podepsaná IPA jsou samostatné předání; stará
aplikace ani starý server se pouhou úpravou zdrojového kódu nezmění.

D110 rozšiřuje `POST /v1/conversations/{id}/speech` o volitelné `voice`
(`cedar`, `marin`, `onyx`, `coral`) a konečné `speed` 0.85–1.25. Chybějící
hodnoty zachovají marin/1.0. Jiná pole/hlasy a neplatná rychlost se odmítají;
idempotence zahrnuje zvolené hodnoty. Pevný model, souhlas, nákladové rezervace,
32kHz PCM16 a jeden aktivní audio výstup zůstávají. Log měří provider a PCM
konverzi bez textu návštěvníka.

Výhradně fake dialog virtuální identity `debug-server-admin` položí čtyři
otázky pro původní personalitu, dvě pro `ollivander-trunk`, pak navrhne jednu
přihrádku z masky; maska 0 vede k `no_selection`.
Je to neplacená diagnostická simulace pro iOS D110, nikoli skutečný snapshot
nebo fyzické ovládání. Běžné fake identity ani Responses Sol se nemění.
Fake přepis nahrávky zůstává syntetický. Odeslání vlastní hlasové odpovědi
k opravdovému STT vyžaduje explicitní `voice_mode=openai` a může být účtované poskytovatelem.

ASP.NET Core 10 Minimal API pro přímé HTTPS spojení ESP32 s backendem.
E7.1–2 přijímají servisně provisionovaný device credential,
vydávají 15minutový access token, zakládají nejvýše jedno fake sezení pro zařízení
a zpracují až deset textových tahů s deterministickou odpovědí `ask`.
E7.3a–c přidávají SQLite journal technických metadat textového tahu a
založení sezení, atomickou rezervaci nejvýše deseti tahů v jednom sezení a
idempotenci po restartu. E7.4 přidává retenci a explicitní rušení. E7.5a
přidává omezený WAV upload s deterministickým fake přepisem. E7.5b
přidává fake syntézu krátkého tichého WAV pro existující textový tah.
D106 přidává volitelné skutečné STT/TTS adaptéry za explicitní přepínač.
Textový tah má výchozí fake režim;
výslovný režim `responses` používá GPT-6.1 Sol se sdíleným party promptem,
šesticí hůlek z `../ai-eval/wand-catalog.json`, přísnými schématy nativních funkcí a kontrolou dostupné přihrádky. Model dostává jen dostupné položky a hraje mluvící kouzelný kufr Ollivanderova obchodu. Před odesláním do
OpenAI transakčně rezervuje nejméně 0,10 USD pro Sol (více podle paměti), jeden backend spustí nejvýše jedno
placené volání současně a výsledek dokončí asynchronně. Nejasný výpadek
ponechá rezervaci a restart volání neopakuje. Zrušený tah nezveřejní pozdní
výsledek, ale skutečně vrácené tokeny vyúčtuje. Návrh přihrádky vzniká na
serveru s korelací a expirací 60 sekund.
Žádná trasa neovládá aktuátory.
V režimu `responses` vytváří otázky i pointu model. U výchozí personality první čtyři dokončené
dialogové tahy vyžadují otázku; nabídka funkcí v nich neobsahuje výběr přihrádky.
Dvě poslední výměny zůstávají v RAM pro aktuální tah. Celé dokončené textové
výměny se navíc ukládají do `Backend:DatabasePath` + `.evening.sqlite`.
Tato paměť je oddělená podle zařízení, přežije konec sezení i restart a nemá
časovou expiraci. D112 odděluje pojmenované večery s aktivním ukazatelem;
migrace zachová původní ID a obsah. Po 2000 výměnách nebo 2 MB UTF-8 textu v jednom večeru vytěsňuje nejstarší
výměny; status uvádí počet vytěsnění. Žádné audio ani fotografie neukládá.
Soubor má být ve stejném soukromém adresáři služby jako journal, ale vynechaný
z technických záloh. SQLite používá secure_delete a rollback journal; smazání
není zárukou odstranění kopií v externích zálohách nebo u poskytovatele.

Model dostane nejvýše 16000 znaků vybraných původních replik: aktuální
rozhovor, starší rozhovory podle shody slov ve jméně/odpovědi a nedávný kontext.
Jde o lexikální vybavení, ne neomylný profil člověka ani biometrickou identitu.
Dva Petrové vyžadují upřesnění. Jména/opravy musí vycházet z řečeného.
Obsah paměti se nikdy nepovažuje za instrukce či aktuální dostupnost hůlek.
Limit základního JSON těla je 14 kB plus JSON-encoded velikost paměti
a přírůstek velikosti zvolené personality;
rezervace před voláním je 0,10 USD plus cache-write cena Fast
přidaných bajtů. Účetní odhad neomezuje další práci; rozpočet řídí OpenAI projekt.

`POST /v1/conversations/{id}/opening` přijímá pole textového tahu **bez text**.
Vytvoří běžnou pollovatelnou turn operaci: kufr promluví první, krátce osloví
hosta a sám vytvoří jednu otevřenou otázku bez nabídky odpovědí. Jméno není povinný úvod. Po prvním přijatém tahu se nový opening odmítne;
retry se stejným klíčem vrací tutéž operaci. Starý `/turns` zůstává kompatibilní.
Firmware má explicitní vstup opening a ukončení sezení (D75); reálný vstup návštěvníka a samostatný hlas vyžadují dokončení E4/E9.

`GET /v1/evenings` vrací aktivní ID a metadata uložených večerů, žádné repliky.
`POST /v1/evenings` s `{name,expectedEveningId}` založí a aktivuje prázdný
večer. `PUT /v1/evenings/{id}/active` s `{expectedEveningId}` přepne paměť.
Obě mutace vyžadují UUID `Idempotency-Key` a token daného zařízení. Nejvýše
100 večerů, název 1–80 Unicode znaků bez řídicích znaků. Živý rozhovor nebo
doznívající provider stejného zařízení vrací 409, stejně jako stará CAS revize.
Idempotence v SQLite vydrží restart, platí 24 h a má limit 1024 neexpirovaných
záznamů na zařízení; překročení vrací 429. Replay vrátí původní snapshot,
neobnoví starý aktivní ukazatel; klient poté načte aktuální GET stav.
Nový večer zachová archiv a kontext se nikdy nemíchá mezi večery/zařízeními.

`GET /v1/evening-memory` vrací pro device token `eveningId`, počty a limity,
nikoli obsah. Obsluha smaže konkrétní večer pomocí
`DELETE /v1/evening-memory/{eveningId}`, prázdného těla a UUID `Idempotency-Key`.
Smazání také ukončí aktivní sezení; zrušená pozdní odpověď nesmí nic obnovit.
Maže pouze aktivní večer a vytvoří nový prázdný; ostatní archivy zůstávají.
Stará/cizí ID vrací neškodné 204; nemohou smazat nový večer nebo jiné zařízení.
Samotné `DELETE /v1/conversations/{id}` paměť večera nemaže. UI tlačítko a
lokální autorizovaná cesta ze spárovaného telefonu jsou ještě integrace E10.
Technický journal neobsahuje text. Zápis paměti předchází publikaci odpovědi;
při pádu mezi dvěma SQLite commity může obsahovat vygenerovanou repliku,
kterou zařízení nepřevzalo. Ani dokončený tah není důkazem přehrání zvuku.

`responses` je zatím pouze vývojový režim pro syntetické vstupy přes loopback.
Vyžaduje `Backend__Dialog__Mode=responses` a `OPENAI_API_KEY`. Rozpočet řídí OpenAI projekt. Samotná přítomnost klíče režim nepřepíná. Neznámá konfigurace zastaví
start; výchozí `fake` nevolá OpenAI. Veřejné HTTPS je dostupné; skutečná identita zařízení, fyzická akceptace a obsahové hodnocení návštěvníky zůstávají otevřené.

## Vývojové spuštění

D109 přidává `GET/PUT /v1/admin/modes` za samostatným raw bearer credential
správce. Server zná jen `Backend:Admin:CredentialSha256`; přístup se nikdy
neodvozuje z device access tokenu. Hash ani rezervované ID `debug-server-admin`
nesmí kolidovat s `Devices` (start se odmítne). Credential správce se může
vyměnit za běžný token této oddělené testovací identity, nikdy identity ESP.
GET vrací revizi, dialog/hlas režimy, dostupnost providerů, počet živých sezení
a `lifetime=until_restart`. PUT přijímá `expectedRevision`, `dialogMode`
(`fake/responses`) a `voiceMode` (`fake/openai`). Vrací potvrzený stav; chybná
revize, živý rozhovor/doznívající provider či nedostupný provider vrací 409.
Změna a create sdílejí zámek a nikdy nerozdělí probíhající rozhovor mezi režimy.
Stejný stav je povolený no-op. Režimy nejsou trvalé; po restartu platí konfigurace
nasazení a nová revize. Přepnutí nemění model Sol, credential, technické hranice ani paměť
večera a samo nevolá OpenAI. Ve fake režimu mohou být klienti připraveni, ale
žádný provider se nepoužije.

Ruční deploy očekává silný samostatný secret `ADMIN_CREDENTIAL` v environmentu
`backend-production`. Workflow odvodí hash do konfigurace release; provider
klíč stále zapisuje stávající root helper. Parametr `voice_mode` doplňuje
dosavadní `dialog_mode`. Veřejný `server_mode_smoke.py` ověřuje admin auth,
izolovaný debug token, no-op PUT, stale revizi a diagnostics přes běžné PKI;
nemění produkční režimy ani nevytváří rozhovor/placené provider volání.

D108 přidává autentizované `GET /v1/diagnostics` s provider režimy, aktuálním
katalogem a audio formátem. Endpoint používá device access token, `no-store`,
nevrací žádné tajemství/obsah paměti a sám nevolá AI. iOS Debug může pro test
opening → speech → telefon použít samostatné provisionované zařízení `debug-*`.
Registrace a hash credential používají stávající `Devices` konfiguraci;
provider klíč zůstává pouze na serveru. Testy mají stejné kvóty a paměť večera
jako ostatní sezení a žádné zvláštní právo převzít aktivní sezení. Příprava kódu
neznamená jeho nasazení. Aktuální nasazení D109, provider režimy a ověření
eviduje [stav implementace](../docs/stav-implementace.md).

SDK je připnuté v `../global.json`; .NET 10 ASP.NET Core runtime je nutný.
Pro běh nastav `Backend__DatabasePath` na absolutní cestu k místnímu
SQLite souboru mimo git (adresář musí existovat). Na Windows PowerShell:

```powershell
$databaseDirectory = Join-Path $env:LOCALAPPDATA 'KufrBackend'
New-Item -ItemType Directory -Path $databaseDirectory -Force | Out-Null
$env:Backend__DatabasePath = Join-Path $databaseDirectory 'journal.sqlite'
```

Backend bez cesty odmítne start; žádný soubor s daty ani credential není
součástí repozitáře.
Z adresáře `kufr/backend/`:

```text
dotnet build backend.csproj
dotnet run --launch-profile https
node tests/smoke.mjs
dotnet run --project ../backend-tests/BackendTests.csproj
```

HTTPS profil používá `https://localhost:7043` a důvěryhodný místní
vývojový certifikát. Smoke test sám spouští krátkodobý backend pouze na
`127.0.0.1`, vytváří náhodné credentials a ověřuje skutečné HTTP odpovědi.
Test je samostatný proces, vytvoří dočasný SQLite soubor a nepotřebuje
poskytovatele AI.
Samostatný C# test s řízeným časem ověřuje expiraci tokenu/sezení a retry.
Používá dočasný SQLite soubor a ověřuje také atomický souběh rezervací a
znovuotevření po restartu. `Microsoft.Data.Sqlite` je připnutý na `10.0.12`.

Zařízení se registrují mimo HTTP API konfigurací `Devices__0__DeviceId`
a `Devices__0__CredentialSha256` (64 hex znaků SHA-256 přesného UTF-8 textu
credential, bez koncového newline; hex credential se před hashováním nedekóduje).
V repozitáři není žádný credential ani ukázkový platný hash. Unikátní
náhodný credential se zadá do zařízení servisním postupem; hash se přidá
do konfigurace backendu mimo git. Další zařízení používá index `1` atd.
Bez konfigurace zařízení se backend spustí, ale token nevydá.
Duplicitní ID nebo hash credential zabrání startu. Na jedno zařízení lze mít
nejvýše 16 současně platných access tokenů; další výměna vrátí 429.

Na VPS aktivační helper při každém deploy přepisuje
`/etc/kufr-backend/backend.env` a spravuje v něm index `0`. Další servisní
identity patří do root-owned `/etc/kufr-backend/device-identities.env`
(mode 0600), se dvěma položkami `Devices__N__DeviceId` a
`Devices__N__CredentialSha256` pro každý dosud nepoužitý index `N`.
Raw credential ani PoP do tohoto souboru nepatří. Trvalý systemd drop-in
`/etc/systemd/system/kufr-backend.service.d/50-device-identities.conf`
(root-owned, mode 0644) obsahuje:

```ini
[Service]
EnvironmentFile=/etc/kufr-backend/device-identities.env
```

Prázdným `EnvironmentFile=` nerušit původní soubor. Před změnou soukromě
zálohovat unit/config, zachovat všechny existující identity a ověřit unikátnost
ID i hashů přes oba soubory. Po změně provést `systemctl daemon-reload`,
restart backendu, health check a token exchange nové identity bez výpisu
credentialu/tokenů. Registrace se načítá při startu; restart zneplatní RAM
access tokeny, jejich zachované credentialy dovolí získat nové.
D159 přidalo fyzickou desku `kufr-68ee8f4b6048` na index `1`; deploy index `0`
zůstal zachovaný. Změna GitHub `DEVICE_ID`/hash na stejnou identitu by vytvořila
duplicitu a zabránila startu; případná migrace vyžaduje koordinovanou úpravu
obou zdrojů konfigurace.

`POST /v1/device-access/token` přijímá provisionovaný credential jako bearer.
`POST /v1/conversations` a `POST /v1/conversations/{id}/turns` vyžadují
vydaný access token, UUID hlavičku `Idempotency-Key` a tělo dle
`../contracts/backend-openapi-v1.json`. `GET /v1/operations/{id}` vrací
fake operaci ve stavu `completed` s identitami požadavku, pevnou odpovědí
`ask` a `proposal:null`. Text návštěvníka se neukládá do technické operace ani logu; dokončený textový tah patří do oddělené paměti večera.
`DELETE /v1/operations/{id}` a `DELETE /v1/conversations/{id}` vyžadují stejný
access token, UUID `Idempotency-Key` a prázdné tělo. Rušení potlačí publikaci
výsledku v RAM i pozdě dokončeného tahu; již odeslaná placená práce by ale
nebyla refundována. Úspěšný DELETE key lze po restartu opakovat po dobu 24 h.
`POST /v1/conversations/{id}/transcriptions` přijímá pouze se souhlasem
`consent.audio=true` multipart s `clientTurnId`, `bootId`, `generation`,
`deviceRevision` a `wav` (`audio/wav`). WAV musí být PCM16 mono 32 kHz,
nejvýše 30 sekund / 1 920 000 bajtů vzorků; celý požadavek má strop 2 MB.
Ve výchozím `fake` režimu backend vrací pevný testovací přepis se zdrojem
`deviceMicrophone`; `openai` použije skutečný STT adaptér popsaný níže.
Nahrávka ani přepis se nezapisují do SQLite; operace je device-scoped a po
restartu se její ztracený RAM výsledek neobnoví.
`POST /v1/conversations/{id}/speech` bere pouze UUID předchozího
`clientTurnId` a `outputTarget` (`device` nebo `phone`), nikdy klientský text.
Vyžaduje audio souhlas, nejvýše deset syntéz v jednom sezení a právě jeden
aktivní výstup. Výsledek operace nese `audioId`; autentizované
`GET /v1/audio/{audioId}` vrací PCM16 mono 32 kHz WAV (ve fake režimu ticho) s
metadaty a `no-store`. Obsah zůstává jen v RAM nejvýše 120 sekund; po expiraci
vrací vlastník `410`, cizí zařízení `403`. Po přehrání klient použije
`DELETE /v1/operations/{id}` k uvolnění aktivního výstupu; jinak jej uvolní
120sekundová expirace. Ukončení sezení také audio odstraní.
Odpovědi `/v1` mají `Cache-Control: no-store`; chyby používají
`application/problem+json` se stabilním `code` a `traceId`.

## Hlasové adaptéry E9.1

`Backend__Voice__Mode=openai` zapne nezávisle na textovém dialogu
STT `gpt-transcribe` (český vstup) a TTS `gpt-4o-mini-tts-2025-12-15`,
hlas `marin` s českými instrukcemi. Vyžaduje `OPENAI_API_KEY`. Výchozí `fake` nevolá poskytovatele;
neznámý režim nebo chybějící předpoklad zastaví start. TTS čte pouze uloženou
validovanou repliku dokončeného dialogu, nikoli libovolný klientský text.

Trasy a schéma v1 se nemění. Hlasové operace vrací `202` a pollují se stejně
jako dialog. Jeden backend dovolí nejvýše jedno placené volání současně
napříč dialogem, STT a TTS. Každé audio volání má 15sekundový deadline;
nejasný výpadek se neopakuje. Zrušení, konec sezení, shutdown nebo pozdní
odpověď nesmějí publikovat výsledek. Worker vlastní samostatnou omezenou
kopii uploadu, kterou po dokončení vynuluje. Nahrávky ani generované audio
se neukládají do SQLite; přepis v RAM lze následně předat textovému dialogu.

Poskytovatel vrací [PCM 24 kHz, mono, signed 16-bit little-endian](https://developers.openai.com/api/docs/guides/text-to-speech).
Backend provede skutečný filtrovaný převod na 32 kHz a vytvoří WAV;
nepřeznačuje pouze hlavičku. Přijme nejvýše 30 sekund zvuku. Delší výsledek
odmítne celý bez částečného přehrávání; i platná 500znaková replika může tento
limit překročit a již odeslané volání může být účtované. Délku a český projev
je nutné změřit při autorizované živé hlasové evaluaci.

Před dispatch rezervuje STT 0,01 USD a TTS 0,05 USD. Jde o konzervativní
vývojové odhady, nikoli ověřené skutečné účty nebo zaručený cenový strop
poskytovatele. Bez spolehlivého billing údaje zůstávají rezervace započtené
i po úspěchu či chybě; neodeslaná práce se vypořádá nulou. Projektový hard
spend limit zůstává nutný. Testy používají falešný HTTP provider a syntetické
PCM, ověřují spektrum/délku převodu, kontrakty, rušení, TTL a nákladové
rezervace. Neprokazují reálný český hlas ani reproduktor ESP32.

Tento krok nemění konfiguraci běžícího serveru ani nasazovací helper.
Fyzické PTT/I2S a firmware audio tok jsou navazující části E9.

Sezení, tokeny a fake obsah výsledku jsou jen v paměti jedné instance.
SQLite drží hash těla, idempotency key, identifikátory, status a 24hodinovou
expiraci textové operace, nikdy text návštěvníka; ten má oddělený obsahový soubor popsaný výše. Po restartu lze opakovat
stejný turn key bez druhého požadavku. Rozběhnutá rezervace přejde na
`unknown`; již dokončený, ale z RAM ztracený výsledek se čte jako
`failed/result_expired`. Nové tahy starého sezení nejsou po restartu možné.
Soubor `journal.sqlite.instance.lock` dovoluje právě jeden běžící backend
nad databází; druhá instance skončí chybou před recovery.
Schéma v2 přidalo `conversation_turn_usage` a atomicky zapisuje kvótu spolu
s pending operací a idempotency key. Schéma v3 přidává
`conversation_creates`: create key, hash těla, původní ID odpovědi a expirace
přežijí restart. Po restartu je starý conversationId pouze identitou retry;
obsah sezení z RAM zmizel a nový rozhovor vyžaduje nový key. Schéma v4 přidává
`mutation_idempotency` pro DELETE retry. Migrace v1/v2/v3→v4 je dopředná a
schéma v5 přidává typ operace do technického journalu; osazené v4 řádky jsou
typ `turn`. Schéma v6 přidává technický nákladový journal a agregáty bez
obsahu. Fake operace rezervují nulu. Budoucí placený adaptér musí v transakci
vzniku operace zaznamenat účetní odhad. Žádný softwarový finanční strop se
neuplatňuje; historické `Backend__Usage__*CapMicroUsd` se ignorují.
Po odpovědi `SettleCost` zaúčtuje skutečnost, levnější i dražší odpověď
pouze upraví účetnictví. Neznámý výsledek po restartu si odhad ponechá;
nejde o blokaci nového tahu ani o povolení placeného retry.
Technický audit se maže po 7 dnech, denní/měsíční agregáty po 90 dnech.
D61 přidal ověřenou konzervativní sazbu
`gpt-6.1-sol`; explicitní režim `responses` ji používá. Výchozí režim
`fake` žádné placené volání nespouští.
`RetentionWorker` každou minutu smaže expirované paměťové sezení/výsledky,
granty a 24hodinové technické SQLite záznamy. Sezení trvá nejvýše
15 minut nečinnosti a 30 minut od vytvoření. Ztracený výsledek se znovu
nevytváří; polling vrátí `result_expired`. E8 stále vyžaduje širší evaluaci
a fyzické potvrzení, backend nesmí být použit pro veřejný provoz. Stav a
další krok jsou v `../docs/stav-implementace.md`.

## Ubuntu runner a nasazení

Před první aktivací lze z GitHub Actions na větvi main ručně spustit
workflow Kufr backend host probe. Na self-hosted runneru jen přečte účet
procesu, dostupnost neinteraktivního sudo, release adresáře, instalačního
helperu, stav služby a odpověď lokálního /healthz. Nevypisuje tajemství,
nevytváří adresáře a nespouští bootstrap. Chybějící releases directory
znamená, že níže uvedený jednorázový bootstrap ještě neproběhl.

Pokud správce nemá na serveru checkout soukromého repozitáře, ruční workflow
Kufr backend bootstrap source stáhne aktuální `main` na serverovém runneru,
uloží `bootstrap-ubuntu.sh`, `deploy-helper.py` a `kufr-backend.service` do
jednoho soukromého adresáře runneru a vypíše absolutní cestu i jejich SHA-256.
Správce porovná hashe v root shellu příkazem `sha256sum CESTA/*`
a spustí `sudo bash CESTA/bootstrap-ubuntu.sh github-runner`. Soubory není nutné kopírovat do
`/root`; workflow skript samo nespouští ani nepoužívá sudo.


Workflow `.github/workflows/kufr-backend.yml` volané z `ci.yml` sestaví a otestuje
samostatný `linux-x64` release při každém PR na serverovém runneru
`[self-hosted, Linux, X64, kufr-backend]`. Node a .NET SDK instalují
setup actions do zapisovatelných cest účtu runneru. Merge již nebuildí. Ruční
PR kontroluje i balení, ale neukládá artefakty do GitHub storage. Před nasazením
spusť ručně `ci.yml` na `main` s `project=backend`; artefakt platí jeden den.
`kufr-backend-deploy.yml` z `main` se vstupem `build_run_id` ověří původ a shodu
stromu tohoto artefaktu s `main`, pak jej aktivuje bez překladu na self-hosted runneru
se štítky `self-hosted`, `Linux`, `X64` a
`kufr-backend` v environmentu `backend-production`.
Vstup `dialog_mode` při ručním nasazení volí `fake` (výchozí) nebo `responses`;
volba se uloží do konfigurace daného release a rollback ji vrátí s releasem.
Od D144 lze zadat také `live_actor_mode=LegacyBackendScripted|AutonomousActor`,
`hybrid_memory=false|true` a `cloud_indexing=false|true`. Výchozí hodnoty
zůstávají legacy/false/false; osobní dlouhodobá paměť zůstává vypnutá.
Od D148 lze nezávisle zvolit `live_decision_model=gpt-6-luna` pro úzký
autonomní výběr (`none`, 512 tokenů), zatímco text/legacy používají Sol.
`live_terminal_quiet_ms=1500` zkrátí pauzu po úplném verdiktu o 2,5 s
proti výchozím 4000; místní playback checkpoint zůstává povinný.
Explicitní workflow `kufr-memory-provision.yml` nabízí `dry-run`, `verify`
a `create`, používá stejný ověřený backendový CLI a trvale uloží ID mimo
checkout. Nevkládá paměť hostů a nepotřebuje širší root práva. Deployment
přebírá IDs z tohoto provisioning journalu do konfigurace release.
Příkazy, existující store IDs, zálohování konfigurace a rollback jsou
v [provozním návodu](../docs/autonomous-live-memory.md#github-actions-na-produkčním-runneru).
Režim `responses` používej pouze pro jednorázový vývojový test s připravenými
limity a následně znovu nasaď `fake`.
Na serveru je nutný
jednorázový bootstrap z důvěryhodné kopie repozitáře:

```bash
sudo bash kufr/backend/deploy/bootstrap-ubuntu.sh RUNNER_USER
```

`RUNNER_USER` je skutečný účet, pod kterým běží GitHub runner, nikoli název
runneru; zjistíš jej na serveru přes `ps -eo user,args | grep '[R]unner.Listener'`.
Bootstrap vytvoří odděleného systémového uživatele `kufr-backend`,
datový adresář `/var/lib/kufr-backend`, systemd unit a root-owned aktivační
helper. Sudo pro runner dovolí jen tento helper s příkazem `activate`.
Release leží v `/opt/kufr-backend/releases`; `current` se po úspěchu přepne
atomicky. Při neúspěšném startu či `GET /healthz` se obnoví předchozí release
i konfigurace. SQLite leží mimo releasy a rollback schématu databáze se
neprovádí; před změnou schématu je nutné ověřit kompatibilitu.

Environment `backend-production` má tyto GitHub Actions secrets:

| Secret | Hodnota |
| --- | --- |
| `DEVICE_ID` | Identita deploy indexu `0`, dosud `dev-simulator-1`. Další fyzická zařízení registrovat na nepoužitých indexech v trvalém servisním environmentu výše; nevytvořit duplicitní ID. |
| `DEVICE_CREDENTIAL_SHA256` | 64 hex znaků SHA-256 přesného UTF-8 credential textu pro deploy index `0`; zachovat shodu s jeho identitou a případným `DEV_DEVICE_CREDENTIAL`. |
| `DEV_DEVICE_CREDENTIAL` | Jen pro ruční loopback smoke vývojové identity indexu `0`; neprovisionovat jej do fyzického kufru. Fyzické zařízení má vlastní credential a registraci, nezávislou na této testovací identitě. |
| `OPENAI_API_KEY` | Projektový OpenAI API klíč. Čte jej pouze explicitní režim `responses`; ve výchozím `fake` se OpenAI nevolá. |
| `SESSION_CAP_MICRO_USD` | Zastaralé, ignoruje se; rozpočet řídí OpenAI. |
| `DAILY_CAP_MICRO_USD` | Zastaralé, ignoruje se. Denní součty zůstávají auditní. |
| `MONTHLY_CAP_MICRO_USD` | Zastaralé, ignoruje se. Celkový rozpočet spravuje OpenAI. |

Po nasazení lze spustit **Kufr backend deployed smoke**: fake režim ověřuje
technický tah, responses režim pouze opening. **Kufr backend public dialogue**
ověřuje jeden placený opening, autentizaci, idempotentní retry, poll a ukončení
přes veřejné HTTPS. Tyto smoke testy nehodnotí humor.

**Kufr backend interactive dialogue** slouží pro skutečné vedení rozhovoru.
Každý ruční běh vykoná jedinou z akcí `start`, `reply`, `finish`, `status`, `clear`.
`start` založí sezení a vyvolá opening; artefakt `interactive-dialogue` obsahuje
veřejný stav a skutečnou repliku. Člověk/agent si ji přečte a až poté zadá další
`reply` s vráceným `state`, vlastním textem a novým UUID `request_id`. Retry
původního UUID a stejného vstupu používá stejné idempotentní klíče. Žádný tajný
credential/token není vstupem ani součástí artefaktu; zůstává v runneru.
`finish` uvolní sezení, paměť zůstává. `status` ukazuje limity/počty večera;
`clear` vyžaduje přesné aktuální `expected_evening_id`. Před mazáním ověř, že jde
výhradně o vlastní testovací data. Rozpočet testování řídí OpenAI projekt; ceníkový odhad není skutečná fakturace.

Nasazení volí `dialog_model=gpt-6.1-sol`; backend čte
`Backend__Dialog__Model` a jiný model v responses režimu odmítne. Funkce
`continue_conversation`, `propose_wand`, `end_conversation` se mapují na stávající
`ask/propose/no_selection`. Terminální odpověď zůstává pollovatelná, její replay
nevolá model; nový tah dostane 409 `conversation_finished`, dokud ESP32 neprovede
DELETE. AI sama vybírá hůlku. Místní autonomní brána ESP32 provede prezentaci
pevné rekvizity a návrat; nevyžaduje potvrzení obsluhy. Stop a poruchy mají přednost.

Všechny hodnoty mohou pro první fake nasazení chybět. Bez dvojice zařízení
se nikdo nepřihlásí. Částečná dvojice device identity nasazení zastaví.
Historické cap položky nový helper ignoruje a nezapisuje do prostředí.
Environment je omezený na větev `main`; podle potřeby nastav požadované
schválení. Při změně secrets spusť workflow znovu; běžící služba se sama
nepřekonfiguruje. Klíč nevkládej do chatu, repozitáře ani mobilní aplikace.

Služba poslouchá pouze `http://127.0.0.1:5080`; port 5080 nemá být veřejně
otevřený. Pro kontrolu na serveru použij
`curl -fsS http://127.0.0.1:5080/healthz` a `systemctl status kufr-backend`.

### Veřejné HTTPS na IP adrese

Workflow **Kufr backend IP HTTPS** na `main` má ruční režim `stage`, který
na serverovém runneru připraví přesně commitované soubory z
`deploy/ip-https/` v soukromém adresáři a vypíše cestu a SHA-256. Skript se
automaticky nespustí s root právy. Správce ověří hash a spustí jej v root
shellu jako `sudo bash CESTA/bootstrap-ip-https.sh 2.28.202.220`.
Předpoklady: běžící loopback backend, volné porty 80/443 a příchozí TCP
80/443 povolené i ve firewallu poskytovatele. Skript odmítne cizí Nginx site,
nainstaluje Nginx a Certbot 5.4+ ze snapu, vystaví veřejně důvěryhodný
krátkodobý certifikát s IP SAN přes HTTP-01 webroot a zapne HTTPS proxy.
Port 80 slouží obnově certifikátu a jinak přesměrovává na HTTPS. Nginx
nikdy neposílá provoz přímo na veřejný port backendu.

`kufr-cert-renew.timer` kontroluje obnovu dvakrát denně; deploy hook po
obnově otestuje a znovu načte Nginx. Bootstrap ověří lokální důvěru TLS,
IP SAN a zkušební obnovu. Režim workflow `verify` z GitHub runneru ověří
veřejné `https://IP/healthz`, důvěru IP certifikátu, alespoň 48 hodin
platnosti a HTTP přesměrování. Stejná kontrola běží denně s repository
variable `KUFR_BACKEND_PUBLIC_IPV4`. Bez úspěšného veřejného běhu se B4
nepovažuje za ověřené. Certifikát je krátkodobý, proto je průběžná obnova
součástí provozu. Při chybě lze zastavit jen Nginx (`systemctl stop nginx`);
backend zůstane dostupný na loopbacku pro runner/deploy diagnostiku.

### Safari instalace iOS aplikace

Po ověření veřejného HTTPS připrav režimem `stage` téhož workflow aktuální
soubory a jako root spusť `sudo bash CESTA/install-ios-host.sh 2.28.202.220
github-runner`. Skript přidá do vlastního Nginx site pouze statickou cestu
`/ios/`, vytvoří stránku „podepsaná verze se připravuje“ a při chybě
konfigurace vrátí předchozí site. Adresář `/var/www/kufr-ios` vlastní
serverový runner, který smí publikovat pouze statické soubory. Přístup k
backendovým secrets ani k portu 5080 stránka nemá.

Po úspěšném Ad Hoc podpisu **Build signed Kufr IPA** samostatné workflow
**Deploy Kufr IPA** se vstupem ID podepsaného buildu nahraje do tohoto adresáře
verzovanou IPA, `manifest.plist`, SHA-256 a instalační stránku. Změna
hlavní stránky proběhne atomicky až po uložení celé verze. Samostatný
job na témže serverovém runneru ověří přes veřejné HTTPS stránku, manifest, MIME typy,
staženou IPA a checksum. Instalaci `itms-services` a spuštění může potvrdit
jen registrovaný iPhone v Safari; serverový test to nenahrazuje a neprokazuje
dostupnost z jiné sítě. Soubory
pod `/ios/` jsou veřejně čitelné, proto IPA nesmí obsahovat tajemství.

Responses požadavky explicitně používají `service_tier: default`. Jiný nebo chybějící vrácený tarif se odmítne a drží rezervaci (včetně rezervy pro 2× sazbu Fast), bez automatického opakování. Potvrzený Standard se vypořádá konzervativně podle tokenů; tato interní částka není účetní údaj z OpenAI. Ochrana tarifu byla přidána po živém eval, který vracel Fast při neuvedeném tarifu.

Úplný postup a omezení opakovaného použití artefaktů jsou v [CI/CD návodu](../../docs/ci-cd.md).

## Serverové logy a společný debug D118

Backend ukládá technické události do oddělené SQLite, výchozí historie 24 h
a 5000 záznamů. `GET /v1/admin/logs` a `/v1/admin/logs/status` používají
credential správce nebo samostatný reader token; device access nestačí.
Každý požadavek vrací `X-Request-Id`, Realtime zapisuje i fáze/chyby uvnitř
streamu. Úspěšné GET a audio uploady se vynechávají. Bez textů/audio/tajemství.

[Codex integrace a API](codex/README.md) popisují kurzory, filtry, úložiště,
MCP, společný postup s iOS a GitHub workflow **Kufr server logs**. Pro přímé
MCP lze v `backend-production` přidat volitelný secret `LOG_READER_CREDENTIAL`;
deploy uloží jeho hash. Bez něj funguje iOS/admin a GitHub čtení přes existující
admin secret. Čtení neprovádí provider volání ani změny serveru.

## D127: nový rozhovor a kvalita humoru

Textový i Realtime opening neodesílají archiv večera ani historii starých
výměn. Paměť se nemaže a vrací se do kontextu dalších tahů.
Od D141 `ConversationOpening` přidává jen interní identifikátor nového úvodu;
téma a formulaci otevřené otázky vytváří AI sama. Pevný cyklus šesti námětů
a ukázkové dialogy jsou odstraněné. Realtime uloží identifikátor jednou
do kontextu tahu, takže rezervace a její případné zkrácení nemění zadání.
Sdílený prompt používá principy CARLIN, jednu otázku a jeden doložený detail.
[Rešerše](../docs/hlas-a-humor-research.md) rozlišuje nynější Realtime, GPT‑Live
a další hlasové cesty a obsahuje kritéria českého poslechového testu.
Offline testy neověřují vtipnost. Tento slice neprovádí migraci, nasazení
ani placené volání a nemění fyzické řízení, API nebo limity.

## D143: autonomní GPT-Live a hybridní paměť

Nová opt-in větev `Backend:GptLive:ActorMode=AutonomousActor` nechává Live
vytvářet běžné repliky a závěr. `WandDecisionService` se samostatně zvoleným
`Backend:GptLive:DecisionModel` (Sol nebo od D148 Luna)
vrací pouze validované katalogové údaje a důvody svázané s odpověďmi.
Výchozí `LegacyBackendScripted` i Sol/Realtime fallbacky zůstávají.
Katalog a mobilní přepínání se nemění; Ollivander má samostatnou Live
adaptaci, zdrojové persony/hashy zůstávají původní.
Od D150 (`autonomous-live-v3`) ověřený verdikt obsahuje pouze oznamovací
věty a krátké rýpnutí či callback, potom Live mlčí. Nežádá souhlas ani
hodnocení a nepřidává řečnickou otázku. Publikování návrhu je automatické;
dobrovolná podstatná oprava hosta dál podléhá stávající validaci.

EveningMemoryStore má nedestruktivní hybridní schema: aktuální rozhovor,
anonymní motivy a zvlášť povolenou osobní paměť. Skutečný OpenAI REST
provider používá dva stores, Files upload, attach/poll/search s `filters`
a mazání asociace i vlastního souboru. Každý hit ověřuje zpět v SQLite;
outbox a tombstones přežijí restart. Vzor má všechny paměťové přepínače
vypnuté, prázdná store ID a žádný klíč. Provisioning `--memory-provision`
je výchozí dry-run, provedení vyžaduje `--execute`; `--create` pouze
výslovně vytvoří chybějící stores v servisním okně.

[Úplný návod](../docs/autonomous-live-memory.md) obsahuje přesné klíče,
admin API, provisioning příkazy, identitu/souhlas, retenci, rollback a
poslechové scénáře. Autonomní nativní klient potvrzuje `playback_checkpoint`
přes `/playback` až po místním drain. Cloudový append ACK, ticho a
`session.closed` samy nepublikují mechanický návrh. ESP gate zůstává;
serverový klíč není v klientovi. Lokální testy nezastupují placený poslech,
fyzickou akceptaci ani produkční aktivaci.

Od D148 diagnostika vrací skutečný rozhodovací model, terminal quiet a
tři paměťové přepínače. Technický `live_decision_completed` měří dobu
faktické delegace bez textů; samotná celková délka Live session latenci
výběru nedokazuje. Produkční stav aktivace je veden v D148 ve stavu
implementace.

Od D149 čeká backendová řeč i publikování na mikrofonní RMS klid
`Backend:GptLive:InputQuietMs` (výchozí 1200 ms). Běžné přijetí hůlky
neruší validní výběr; rozhodnutí počká i na pokračování hosta během
výpočtu. Závěr rozpoznává jedinečný katalogový titul v českém skloňování
bez povinné tečky; nejasné oznámení může jednou krátce opravit, vždy s
novým přednesem a skutečným playback ACK. `live_terminal_state` ukazuje
technický stav dokončení bez obsahu rozhovoru. Poslechová akceptace
zůstává samostatná.

Od D151 přijímá autonomní Live konečné číselné časy přepisů/delegací
v mezích 0–200000 ms i v desetinném zápisu. Přesné časování se zachová
v projekci a SQLite; záporné, chybějící či obrácené časy dál odmítne.
Rozhodovací schema omezuje citaci na 100 znaků, fakt na 160, chybějící
informaci na 200 a důvody na dva, stejně jako stávající validace.
`live_failure_detail` zaznamená pevnou kategorii příčiny `ai_unavailable`
bez syrové výjimky či provider zprávy. `decision_running` a stav `error`
v `live_decision_completed` zpřístupní také neúspěšnou delegaci.
[Diagnostika](codex/README.md) uvádí interpretaci; původní dva pády
bez tohoto záznamu nemají zpětně doloženou příčinu.

## D128: GPT-Live + Sol (legacy větev)

`POST /v1/conversations/{id}/gpt-live` otevírá testovací průběžný hlasový
režim. Sdílí autentizaci, audio consent, Stop, snapshot a validaci návrhu
s Realtime, ale používá samostatný provider Live protokol. PCM16/24 kHz
se posílá na `/gpt-live/{streamId}/audio`; po finálním dohrání klient
potvrdí `/played`. Rychlost je vždy 1×, hlasy Marin/Cedar/Coral.

Frontend `gpt-live-1` deleguje odpovědi do Solu přes Responses SDK;
frontend sám nesmí vydat fyzický návrh. Opening nemá starý archiv.
Od D135 je kufr ostrý prodavač hůlek; otázky navazují na použití hůlky
a konkrétní chování zákazníka. Delegace doručená před přepisem se podrží.
Doplnění během Solu automaticky spustí zpracování nejnovějšího vstupu
po dokončení starého; starý výsledek se započítá, ale nevysloví.
Stejná revize nespustí duplicitní práci ani pokyn k čekání. Limit deseti
backendových volání zahrnuje i přepočty opraveného textu. Nejde o retry
nejasného či selhaného placeného volání. GPT-Live zůstává `gpt-live-1`.
Kontext dalších delegací dostává paměť a poslední výměny. GPT-Live zapisuje
konzervativní účetní rezervaci 3 USD, která není limitem. Po řádném
`session.closed` přepočítá kumulativní délku (0,05 USD/min, po sekundách) a
všechny Sol odpovědi. Při nejistém ukončení eviduje odhad; další práci
neblokuje. Sezení trvá nejvýše 180 s, má nejvýše deset backendových volání.
Žádný retry placeného requestu. Rozpočet řídí výhradně OpenAI projekt.

Pinned .NET SDK 2.14.0 Live API neposkytuje, proto `Features/GptLive`
izoluje omezený WebSocket adaptér podle oficiálního schématu. Událost
`pause` vzniká po 900 ms bez řeči ve výstupním PCM a slouží segmentaci
přehrávání; není provider `audio.done`. Ukončení závěrečné repliky je
heuristika určená k živému ověření. Výběr se zveřejní teprve po potvrzeném
uzavření a finální usage. Oprava návštěvníka před uzavřením ruší závěr. D137 podrží hotový Sol výsledek
do 650 ms od posledního input fragmentu, aby nepředběhl doplňovanou odpověď;
nejde o skutečné provider VAD. Při závěru pošle `speech_stopped` ještě před
`session.close` a čekáním na usage. Pozdní mikrofonní uploady se zahodí s 204.
Live během řeči i delegace mlčí. Jakmile jsou podklady a brána dovolí výběr,
rovnou přidělí hůlku bez potvrzovací otázky.

[Rešerše, omezení a postup porovnání](../docs/hlas-a-humor-research.md#d128-testovaci-gpt-live-rezim).

### Omezená diagnostika GPT-Live

Ruční workflow [kufr-gpt-live-smoke.yml](https://github.com/Blato58/DildoVander-deploy/blob/main/.github/workflows/kufr-gpt-live-smoke.yml)
na `main` spustí `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release -- --gpt-live-smoke`
z kořene repozitáře. Vyžaduje serverový `OPENAI_API_KEY`. Jde o placený
provider test: jeden hovor, syntetické ticho 20 s, absolutní deadline 35 s,
bez Solu (přibližně do 0,03 USD). Běžné testy jej nespouštějí. Výstup jsou
jen počty událostí, audia/přepisu a finální usage, nikdy obsah řeči. Úspěch
vyžaduje potvrzení pokynu, neprázdný hlas i přepis a finální usage. Test
ověřuje poskytovatele a transport, nikoli reproduktor iPhonu.

## D140: společná persona podle GPT Live

D140 sjednocuje personu a pravidla hlasového rozhovoru podle GPT Live v
`../ai-eval/voice-persona.txt`: stejný roast, otázky, ticho při práci backendu,
přerušení a oznámení výběru bez souhlasu. Sol i Realtime přidávají
`prompt.txt` s funkcemi; Live pouze vlastní pokyny k delegaci na Sol.


## D142: trvalý inventář personalit

`GET /v1/admin/modes` vrací navíc `personalityId`, `personalityLifetime=persistent`
a metadata `personalities` (ID, název, popis, SHA-256 promptu). `PUT` přijímá
volitelné `personalityId`: vynechání/null zachová aktivní volbu, neznámé ID
vrací `400 invalid_personality`. Změna sdílí revizi a bránu neaktivních
rozhovorů/dobíhajících provider volání s režimy. Zápis personality je trvalý
v journalu (`server_settings`, migrace schema 6 → 7); režimy dál platí do
restartu. Výchozí zůstává `party-original`.

Každý nový rozhovor má neměnnou personalitu. Sol, Realtime i GPT-Live
používají jediný vybraný zdroj; text `ollivander-trunk-persona.txt` je embedded
beze změn. Jeho obvyklé dvě odpovědi a povolený dřívější výběr nepřebírají
původní povinné čtyři otázky ani požadavek na otevřenou otázku. Pro delší
prompt se limit requestu a rezervace zvýší o serializovaný UTF-8 přírůstek;
Účetní odhady, katalog a validace návrhu zůstávají; peněžní limit má OpenAI projekt.
[Inventář a iOS debug test](../ai-eval/personalities.md).
