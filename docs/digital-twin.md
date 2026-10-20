# Digital Twin: provoz a důkazní hranice

Jeden generovaný Workshop viewer zobrazuje dva zdroje. V simulaci jej napájí
dlouhodobý nativní C++ proces; v živém režimu čte odchozí ESP telemetrii.
Web neurčuje sekvenci, journal, bezpečnost ani cílové polohy. Původní E2 bridge
zůstává kompatibilním testovacím harnessem, nikoli aktuálním DV02 runtime.
Práce a skutečné receipts: [privátní evidence](development-coordination.md).

## Skutečně zjištěný výchozí stav

- `simulator/bridge.cpp` používal starší `DeviceControl`/`SelectionSequence`
  a fake text. Neprováděl dnešní physical presentation ani Dance.
- Produkční DV02 již měl `PhysicalPresentation`, compiled `PresentationScore`,
  `PhysicalEffects`, journal v2, bounded trace a try-lock observer mailbox.
- Workshop používal skutečnou CAD sestavu, ale jeho plánový přehrávač byl
  ilustrací. Importované trace dosud nepohybovaly skutečnou 3D scénou.
- `/v1/show-trace` byl pouze lokální read-only HTTPS endpoint. VPS nemělo
  odchozí ingest, snapshot aktivních výstupů ani chráněné Twin sessions.
- Fyzická kalibrace a akceptace aktuální sestavy zůstávají otevřenou branou G.

## Architektura a moduly

| Oblast | Sdílený produkční kód | Adaptér / simulovaná periferie | Nepokrytá vlastnost |
|---|---|---|---|
| Řízení | SafetyController, SelectionSequence, DeviceControl, DeviceControlRuntime | jediný host owner, stdin line port | CPU/RTOS a elektrické vypnutí |
| Rozhodnutí | ConversationRuntimeState, TurnPollGuard, PendingProposal, WandPresentation | syntetická služba nebo produkční HTTP kontrakty | placená AI bez explicitního oprávnění |
| Scény | PhysicalPresentation, PhysicalEffects, generovaný PresentationScore | VirtualHardware přijímá skutečné driver writes | proud, tření, moment, mechanická bezpečnost |
| Journal | PersistentCommandJournal v2 | FileJournal: exclusive lock, atomic replace, POSIX fsync | NVS/power-loss akceptace ESP |
| Hlas/foto | VoiceWorkflow, RealtimeSessionEngine, RealtimeEventCodec, CostumePhoto | ESP drivery nebo browser PCM/JPEG HAL přes bounded binární IPC | AEC, I2S/DMA, kvalita kamery a fyzická slyšitelnost |
| Dance | DanceCoordinator, PresentationAudioPort | syntetický media cursor a tone enqueue | skutečný SD obsah a audio periferie |
| JSON | ConversationTextCodec, TurnOperationCodec, production correlation/expiry guards | isolated HTTP handoff | síťový jitter skutečného ESP |
| Telemetrie | skutečné accepted PhysicalIo writes a ExportPresentationTrace | bounded latest mailbox, samostatný HTTPS worker | optické/akustické/polohové měření |
| CAD | parameters, mechanism.cam_law, skutečné tessellované díly | verzovaný manifest a observer projekce | fyzická vůle a ověření spojů |

Přehled cest: [native host](../firmware/simulator/native/README.md),
[wire contract](../contracts/digital-twin.md),
[viewer](../mechanika/final/VIEWER.md), [DV02](dv02.md).
Backend vlastní pouze autentizaci, clock/input orchestration, ingest a záznam.
Neimplementuje scénový engine ani náhradní firmware.

## Místní build a běh

Z kořene checkoutu:

```powershell
python firmware/simulator/native/build_host.py --output "$env:TEMP/kufr-twin/native-twin.exe"
python firmware/simulator/native/test_host.py
python firmware/simulator/native/test_media.py --executable "$env:TEMP/kufr-twin/native-twin.exe"
npm ci --prefix contracts
python firmware/tests/run_show_tests.py
pio run -d firmware
dotnet restore backend-tests/BackendTests.csproj --locked-mode -p:RuntimeIdentifiers=linux-x64
dotnet build backend-tests/BackendTests.csproj --no-restore -p:RuntimeIdentifiers=linux-x64 -p:SelfContained=false
dotnet backend-tests/bin/Debug/net10.0/BackendTests.dll --twin
$env:KUFR_TWIN_TEST_NATIVE = "$env:TEMP/kufr-twin/native-twin.exe"
dotnet backend-tests/bin/Debug/net10.0/BackendTests.dll --twin-media
node --test mechanika/final/test_twin_viewer.cjs mechanika/final/test_twin_media.cjs mechanika/final/test_viewer.cjs mechanika/final/test_show_timeline.cjs
```

Viewer generuje v existujícím CAD prostředí skutečný `preview.py --viewer-only`;
nevkládejte náhradní model. Použijte připnuté `final/requirements.txt`. Celé
výrobní balení nadále vyžaduje vlastní generaci, kontrolu a slicer acceptance;
regenerace vieweru sama nepotvrzuje výrobní ZIP.

Nativní Linux build nepotřebuje ESP-IDF ani nový broker:

```sh
python3 firmware/simulator/native/build_host.py --output /tmp/kufr-native-twin
python3 firmware/simulator/native/test_host.py --executable /tmp/kufr-native-twin
```

Alternativní CMake příkazy jsou v native README. Host vyžaduje vlastní instance,
boot, build a absolutní journal path. Backend jej spouští nezávisle na prohlížeči.

Privátní JSON `KUFR_TWIN_CONFIG` je absolutní cesta mimo checkout. Obsahuje
`Backend.DatabasePath` pod izolovaným `DigitalTwin.DataRoot`, vypnutou Memory,
`DigitalTwin.Origin`, `ViewerPath`, `Instances` a SHA-256 scoped credentials.
Každá simulace má `Mode=simulation`, `DeviceId` shodné s ID, `Executable`,
`BuildId`, `Profile=deterministic|integration`. Integrace navíc vyžaduje `sim-`
identitu v `Devices` a loopback `IntegrationBackendUrl` k tomuto izolovanému
backendu. Rozhovory se zachovají přes jednotlivé vstupy v rámci platné generace.

```powershell
$env:KUFR_TWIN_CONFIG = 'C:/private/kufr-twin/config.json'
$env:ASPNETCORE_URLS = 'http://localhost:5082'
dotnet backend/bin/Debug/net10.0/backend.dll
```

Otevřete `/twin` na stejném originu. Loopback HTTP je pouze místní test; VPS
používá HTTPS. Přihlaste scoped observe/control credential, vyberte instanci.
Pro první simulovaný běh je nutná vědomá **Servisní inspekce**. Testovací text
vybere slot a main/short; v integračním profilu text projde backendovým rozhovorem
a návrh vybírá backend. Clock pause/step/speed jsou pouze deterministické.
Stop nevrací lůžko a vyžaduje zotavení. Uvolnění řízení neukončuje zařízení.

Restart služby zachová všechny journals; nová boot epoch začíná s neznámou
polohou a případná rozpracovaná operace zůstane v recovery. Čistou instanci
vytvoří provozovatel jako nový `sim-*` klíč v `DigitalTwin.Instances` s vlastním
control credentialem. Journal path se odvozuje z tohoto ID, nikoli z browseru;
starou instanci ani její úložiště nemažte. V konfiguraci jsou nejvýše dva native
hosty. Integrační instanci doplňte také do izolovaných `Devices`. Po změně
konfigurace restartujte službu; reload vieweru tyto úkony nikdy neprovádí.

Fake integrační nastavení zůstává fake i při změně globálního provider režimu.
Skutečné služby vyžadují izolovanou databázi, vypnutou paměť, existující serverový
klíč a `DigitalTwin.ExternalServicesAuthorized=true`. Provozovatel explicitně
vybere existující soubor přes `--reuse-provider-env`; loader přečte pouze
`OPENAI_API_KEY`, nikoli produkční identity, cestu DB či paměť. Hodnotu nekopíruje
do browseru, repozitáře ani nového credential souboru. Majitel tuto volbu schválil
14.10.2026. Testy služeb zůstávají fake; žádný placený provider nebyl volán.
Pro reálnou síť se injektovaný clock ovládat nedá.

## Rozhovor s browser mikrofonem, reproduktorem a kamerou

Přihlaste **integration** scoped credential a zvolte `sim-integration`. Po
vědomé servisní inspekci otevřete **Nastavení nativního rozhovoru**. Na stránce
vyberte Fake/Realtime/GPT-Live, hlas, rychlost, main/short a foto consent; změna
je verzovaná a perzistentní. Aktivní operace změnu odmítá. Dostupnost poskytovatele
znamená konfiguraci/oprávnění, nikoli úspěšně otestované placené spojení. Odvolané
serverové oprávnění zablokuje i dříve uložené real nastavení.

Výslovně aktivujte reproduktor a mikrofon, potom **Spustit rozhovor**. Kamera se
aktivuje až na nativní photo request. Náhled zůstává lokální, dokud nepotvrdíte
jediný JPEG; limit je640×640/262144B bez metadata. Odmítnutí dovoluje pokračovat
bez fotky. Výběr browser zařízení zůstává lokální a jeho identifikátory se
nezaznamenávají do technické telemetrie.

Browser posílá PCM16 mono24kHz pouze při skutečném native inputEnabled gate;
fronta včetně in-flight je400ms. Audio drain potvrzuje dokončený `onended`
aktuálního výstupu při běžícím AudioContext, správné epochě a přesné délce.
Elapsed virtual time nikdy nenahrazuje ACK. Suspended/disconnected playback,
overflow, změna zdroje nebo Stop ruší lokální média a staré výsledky. Zrušení
již dokončené voice relace neukončí přijatou fyzickou prezentaci; Stop kufru je
samostatný runtime příkaz. Replay žádné media/control endpointy nevolá.
Při zavření nebo navigaci stránky se odesílá pouze omezený `keepalive` cancel
aktuální hlasové admission; PCM ani fotografie se při unloadu neposílají.

Fake profil používá skutečné `ConversationStore` transcription/turn/speech
ownery: testovací přepis, existující syntetický dialog/proposal a100ms tichou
speech fixture. Nepředstírá rozpoznání skutečného hlasu ani mluvenou AI odpověď.
Realtime/GPT-Live používají existující produkční HTTP/NDJSON poskytovatele;
interpretace událostí, guards, foto fallback a návrh před prezentací zůstávají
ve stejných C++ modulech na ESP a Linuxu. Hlasové timeouty používají wall-clock.
Soukromý kontrakt: [twin-browser-media](../contracts/twin-browser-media.md).

## VPS a oprávnění

Nasazený chráněný viewer: **https://2.28.202.220/twin**.
Veřejný `/workshop/` zůstává veřejným CAD přehledem bez control credentialů.

| Role | Povolení |
|---|---|
| observe | čtení snapshots/recordings; žádné povely |
| control | jedna konkrétní `sim-*` instance; žádný živý zdroj |
| ingest | jedna identita a režim; žádná browser session ani device control |
| ESP provisioning identity | výměna za krátkodobý **Twin ingest grant** jen pro explicitní live allowlist |

Twin grant neplatí pro běžný backend a běžný device access token neplatí pro
ingest. Prohlížeč dostane HttpOnly/Secure/SameSite Strict session cookie; zápisy
vyžadují přesný origin. Nenastavuje se CORS ani manual lease živého kufru.
Scope není odvozován z browser booleanu. Jeden řídící session owner platí do
expirace nebo explicitního release. Stop má samostatné urgent admission.

Provozní umístění:

- `/opt/kufr-twin/current` a immutable `/opt/kufr-twin/releases/`;
- `kufr-twin.service`, loopback `127.0.0.1:5082`;
- `/etc/kufr-twin/config.json` a root-only `operator-credentials.json`;
- `/var/lib/kufr-twin/`: izolovaná DB, simulation journals, latest a technické JSONL;
- původní backend na `5080` a jeho data se nepřepisují.

Secrets se nepřenášejí do zdrojů ani release balíčku. Operátor si scoped
credential vyzvedne bezpečně z root-only souboru; nevkládá jej do chatu ani URL.
Živý kufr se provisionuje explicitním `--live-device-id`; staré simulátory se
automaticky nepovažují za živá zařízení.

Linux publish a balení ze skutečně ověřených bytes:

```sh
dotnet restore backend/backend.csproj -r linux-x64 -p:SelfContained=true --locked-mode
dotnet publish backend/backend.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false --no-restore -o /tmp/twin-publish
python3 backend/deploy/twin/source-identity.py --output /tmp/twin-source.json
python3 backend/deploy/twin/package-twin.py --publish /tmp/twin-publish --native /tmp/kufr-native-twin --viewer mechanika/final/exports/viewer.html --output /tmp/twin-release --build-id SOURCE_DIGEST
```

Přeneste nový ověřený balíček do nového přímého release adresáře. Na VPS:

```sh
python3 /opt/kufr-twin/releases/RELEASE_ID/install-twin.py --release /opt/kufr-twin/releases/RELEASE_ID --origin https://2.28.202.220 --live-device-id kufr-68ee8f4b6048
# Jen po oprávnění k použití existujícího serverového klíče:
python3 /opt/kufr-twin/releases/RELEASE_ID/install-twin.py --release /opt/kufr-twin/releases/RELEASE_ID --origin https://2.28.202.220 --reuse-provider-env /etc/kufr-backend/backend.env
systemctl status kufr-twin.service
curl --fail http://127.0.0.1:5082/healthz/twin
systemctl stop kufr-twin.service
systemctl start kufr-twin.service
```

`SOURCE_DIGEST` a `RELEASE_ID` jsou skutečné identity dodávaného balíčku.
Installer ověřuje přesný manifest, odmítá symlinky/traversal, privátně zálohuje
konfiguraci a ověřuje readiness, TLS viewer i původní backend health. Při chybě
obnoví předchozí release, konfiguraci a původní active/enabled stav. Pro vědomý
rollback spusťte tentýž installer s předchozím ověřeným release; journal a data
se zachovají. Nevytváří nový broker ani privilegovaný veřejný CI workflow.
Stávající CI doplňuje native a Twin HTTP kontroly; deployment zůstává soukromý
nebo explicitně autorizovaný SSH postup.

## Živý zdroj a pravdivost

ESP posílá plný snapshot přibližně 2× za sekundu přes odchozí HTTPS, také přes
iPhone hotspot. Telefon není prostředníkem. Try-lock publication obsahuje
bounded trace, accepted driver outputs a aktuální runtime fázi. Serializer a
HTTP nejsou v control loop. Network task bere pouze nejnovější snapshot,
timeout je 2 s a backoff 0,5–30 s. Přeskočené, sloučené a neodeslané kopie jsou
viditelné v `droppedSnapshots`; ring zvlášť uvádí `droppedEvents`.

Servo pole jsou kalibrované odhady. PWM off neříká nic o fyzické poloze. LED
GRB je přijatý post-scaling frame, ne optické měření. Audio enqueue/cancel nebo
voice-busy není důkaz zvuku. `physicalVerification=unavailable` se nepovyšuje.
Simulované zaseknutí nemění firmware inputs; firmware může časově ohlásit raised
zatímco model zůstane dole. Model se po bootu bez inspekce nezná.

Každý odběr začíná atomickým snapshotem. Přesné decimal strings, boot epochy,
retired boots, duplicate/conflict rejection a monotónní clock brání smíchání
zdrojů. Při přepnutí se stará projekce zahodí. Stale po 2 s, offline po 10 s;
žádný automatický přechod do simulace nebo dokončení pohybu. Chybějící CAD
revision zařízení zůstává viditelným varováním; nekonstruuje se kompatibilita.

## Záznam, replay a testy

Viewer exportuje bounded plné snapshots (2048 / 8 MiB). Replay/seek mění pouze
projekci. Stávající DV02 import/baseline-candidate porovnání zůstává dostupné;
porovnává pouze kompatibilní identity a mezery zůstávají mezerami. Runtime
inspektor spojuje komponentu se slotem/channel a konkrétními trace událostmi.
Server zapisuje změny technického stavu do JSONL a uchovává samostatný latest;
identické idle snímky nezapisuje donekonečna. Žádné tiché mazání dat.

```sh
python3 backend/deploy/twin/export-recording.py /var/lib/kufr-twin/sim-development.jsonl /private/replay.json
python3 firmware/simulator/native/measure-twin.py --origin https://2.28.202.220 --credentials /etc/kufr-twin/operator-credentials.json --output /private/performance.json
```

Skutečný browser E2E používá Playwright pro opakovatelnou kontrolu GPU matice
`tray_3` (16 mm, ostatní 0), screenshotů, reloadu, Stop a viewportu 390×844.
Použijte již dostupný Playwright/browser a soukromý testovací credential soubor.
VPS proof vyžaduje explicitní `KUFR_TEST_VPS_PROOF=true`; skript smí adresovat
jen pevně zvolené simulační instance.

Plný media E2E na skutečně běžícím Twin serveru používá stejný CAD viewer a
Chromium fake zařízení (neužívá osobní mikrofon/kameru a nevolá poskytovatele):

```sh
KUFR_TEST_VPS_PROOF=true node firmware/simulator/native/twin_browser_media_test.cjs https://2.28.202.220 /private/operator-credentials.json /private/media-proof
```

Nastavení je před testem vynuceno na fake. Test kontroluje skutečný JPEG,
mikrofonní PCM, pět drain ACK, návrh z produkčního fake backendu, pohyb výhradně
vybraného skutečného CAD lůžka, reload bez opakování vstupu, Stop a mobil.

## Omezení a akceptace

IMPLEMENTOVÁNO: text a sdílený produkční VoiceWorkflow/RealtimeSessionEngine,
browser mikrofon/JPEG/playback jako virtuální HAL, nastavení Fake/Realtime/GPT-Live
na stránce, šest slotů/main/short, syntetický Dance/audio enqueue, persistent
journal, observer snapshot/transport, CAD projekce, autorizační izolace, bounded
záznam a replay, VPS balení. Reuse existujícího klíče je pouze serverová konfigurace.

SOFTWAROVĚ OVĚŘENO: native Windows/Linux regrese, původní backend a firmware
testy, ESP-IDF build, negativní HTTP testy, skutečný CAD browser E2E a syntetický
producent skutečného firmware serializeru přes VPS HTTPS. Přesné výsledky a
identity jsou v receipts; build/screenshot nejsou fyzickou akceptací.

NASAZENO NA VPS: oddělená služba a skutečně běžící host procesy, chráněný viewer
a ingest. Testy používají fake provider; Realtime/GPT-Live jsou připravené
v nastavení se schváleným existujícím serverovým klíčem. Fyzický kufr nebyl
ovládán ani flashován. Aktuální nasazený release je `twin-20261015-v17`;
source/build ID `a7ee2cf3740efc92912cfe450e7c7637d98eb0596d35b2111c41db987fb86148`.
[Historický cílený důkaz v17 v privátním archivu](development-coordination.md) zachycuje
opravu mikrofonní fronty na skutečném VPS: při zpoždění 603 ms rozhovor pokračoval,
fronta zůstala do 400 ms, prošly JPEG, PCM/drain a reload bez opakování. Použit byl
fake provider a fake zařízení Chrome; původní nastavení bylo obnoveno a ověřeno.
[Plný softwarový baseline v16 v privátním archivu](development-coordination.md)
zůstává historickým důkazem; fyzickou akceptaci dál blokuje PHYSICAL.

Standardní installer odmítl aktuální Nginx layout před aktivací. V17 byl aktivován
atomickým přepnutím se soukromou zálohou a rollbackem přes existující ověřenou
route/unit; jejich konfigurace i credentials zůstaly stejné. Obě služby jsou zdravé.
Podrobnosti a omezení jsou v [privátní evidenci](development-coordination.md).

Test s 20 HTTPS polls po 50 ms a pomalým čtenářem 10 B/s naměřil z VPS na jeho
vlastní TLS origin median 7,91 ms, p95 10,19 ms, simulační Stop 32,45 ms a
4,50 snapshot updates/s. Z externího Windows klienta byl median 110,03 ms,
p95 247,35 ms a Stop 166,81 ms. Oba běhy zachovaly boot a průběh bez vieweru;
pomalý čtenář neblokoval řízení. Skutečný firmware mailbox se syntetickým I/O
zpracoval na Linuxu 100000 publikací za 16977 µs, sloučil 99999 nahraditelných
stavů. Jde o host/VPS cestu; ESP režie ani odezva tím změřena není. Podklady jsou
v [historickém archivu měření](development-coordination.md).

Plný VPS Chrome E2E verze v11 přijal 4361 B JPEG bez photo fallbacku, čtyři 9600 B mikrofonní
vstupy a pět 4800 B drain výstupů; návrh z backendu vysunul skutečné CAD lůžko 6
o 16 mm a ostatní zůstala na 0. Reload, Stop a viewport 390×844 prošly. Součástí regresí
je Stop před/po registraci hlasového požadavku, následná inspekce a ztracený
ACK již přijatého startu. Starý požadavek nemůže oživit novou relaci.

V12 přidává omezený `keepalive` při `pagehide` a paměť 64 zrušených admission
na simulovanou instanci podle vlastníka, bootu a operation ID až do vypršení
vlastníka. Zaplnění blokuje nové starty; aktivní odpovídající session lze zrušit
i při zaplnění. Prošlo 98 JS testů, dva skutečné Chrome HAL testy a backendové
offline/twin/media regrese. Poslední plný VPS browser test přerušila syntetická
kamera Chromu, která sama ukončila stream; aplikace pravdivě předala unavailable.
Aktivní reload v tomto testu nebyl dosažen. Na výslovný pokyn majitele se další
browser testy zastavily a aktuální browser akceptace je ponechána jeho kontrole;
starší v11 důkazy nejsou vydávány za nový v12 průchod.

OVĚŘENO NA SKUTEČNÉM KUFRU: **ne**. Browser PCM/JPEG adaptéry a sdílený hlasový
runtime jsou přeneseny do hostu; skutečné provider volání ani fyzické ESP hlasové/SD
periferie nebyly testovány. Viewer o nich nevrací falešný úspěch.
Virtuální kalibrace 1100/1900 µs a
500 ms je testovací předpoklad, nikoli měření aktuální sestavy. Časový/kinematický
model neověřuje servo moment, odběr, tření, akustiku ani mechanickou bezpečnost.


V14: HAL32 položek slučuje sousední PCM stejné epochy do32768B; služby mají
samostatné2 položky, vstupní události8 /512KiB. Pouze media worker čeká nejvýše5s
wall-clock. Stop nečeká. Retry je jen pro nepřijaté `media-event-backpressure`.
Browser má150ms počáteční buffer a dostupné bloky načítá bez další50ms pauzy.
ACK potvrzuje software přehrání a přesné kumulativní bajty. Vadný stream ukončí
actor; úklid se neopakuje při každém pollu. Přihlášení stejného browseru/scope
zachová vlastníka řízení. V5 receipts:19native Windows/Linux,103JS/Chrome,
HTTP/native260x4800B s pomalým čtenářem a CAD desktop/mobile/reload/Stop.
Placená AI vynechána; GPT-Live rozhovor ověří majitel. Výkonnostní čísla výše
patří staršímu běhu a nebyla znovu měřena pro v14.


V15 doplňuje nejvýše3 pokusy o úklid konverzace (4s každý, stejný idempotentní
klíč;204/404 ukončí opakování). Úspěšné přihlášení stejným credentialem a cookie
obnoví8h platnost téhož ID i dosud platného vlastnictví médií/hostu a paměti
zrušených operací. Jiný browser nebo scope původní řízení nepřebírá.

Paměť zrušení je omezená na64 operací a64 vlastníků overflow bariéry na instanci.
Při dalším zaplnění blokuje nové vstupy až do explicitního restartu služby;
nezahazuje neexpirované záznamy. Deadline sleduje obnovenou autentizovanou relaci.


Přechod mikrofonní brány (v16): platný opožděný PCM blok z již uzavřené vstupní
epochy vrací409 `media-input-retired` bez přijetí bajtů a bez ukončení rozhovoru.
Browser blok neopakuje, vyčistí čekající mikrofonní vstup a čeká na vyšší epochu
potvrzenou nativním stavem. Starší FIFO událost nesmí bránu znovu otevřít.
Nesprávné pořadí v aktuální epoše (`media-input-sequence-mismatch`), budoucí epocha,
neplatná identita a přeplnění (`media-input-overflow`) zůstávají chybami.
Nativní upload čeká s oddělenou engine a HAL epochou; staré čekání nespotřebuje
první blok nově otevřené brány. Pause GPT-Live může obnovit HAL bránu, aniž změní
vstupní turn sdíleného engine, proto se čítače nesmějí číselně zaměňovat.
