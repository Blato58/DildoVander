# Digital Twin: provoz a důkazní hranice

Jeden generovaný Workshop viewer zobrazuje dva zdroje. V simulaci jej napájí
dlouhodobý nativní C++ proces; v živém režimu čte odchozí ESP telemetrii.
Web neurčuje sekvenci, journal, bezpečnost ani cílové polohy. Původní E2 bridge
zůstává kompatibilním testovacím harnessem, nikoli aktuálním DV02 runtime.
Práce a skutečné receipts: [KUFR-TWIN](../../docs/project/work-items/KUFR-TWIN.json).

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
| Média | DanceCoordinator, PresentationAudioPort | syntetický media cursor a tone enqueue | host speech capture, kamera, SD/I2S/DMA a slyšitelný zvuk |
| JSON | ConversationTextCodec, TurnOperationCodec, production correlation/expiry guards | isolated HTTP handoff | síťový jitter skutečného ESP |
| Telemetrie | skutečné accepted PhysicalIo writes a ExportPresentationTrace | bounded latest mailbox, samostatný HTTPS worker | optické/akustické/polohové měření |
| CAD | parameters, mechanism.cam_law, skutečné tessellované díly | verzovaný manifest a observer projekce | fyzická vůle a ověření spojů |

Přehled cest: [native host](../simulator/native/README.md),
[wire contract](../contracts/digital-twin.md),
[viewer](../mechanika/final/VIEWER.md), [DV02](dv02.md).
Backend vlastní pouze autentizaci, clock/input orchestration, ingest a záznam.
Neimplementuje scénový engine ani náhradní firmware.

## Místní build a běh

Z kořene checkoutu:

```powershell
python kufr/simulator/native/build_host.py --output "$env:TEMP/kufr-twin/native-twin.exe"
python kufr/simulator/native/test_host.py
npm ci --prefix kufr/contracts
python kufr/firmware/tests/run_show_tests.py
pio run -d kufr/firmware
dotnet restore kufr/backend-tests/BackendTests.csproj --locked-mode -p:RuntimeIdentifiers=linux-x64
dotnet build kufr/backend-tests/BackendTests.csproj --no-restore -p:RuntimeIdentifiers=linux-x64 -p:SelfContained=false
dotnet kufr/backend-tests/bin/Debug/net10.0/BackendTests.dll --twin
node --test kufr/mechanika/final/test_twin_viewer.cjs kufr/mechanika/final/test_viewer.cjs kufr/mechanika/final/test_viewer_runtime.cjs kufr/mechanika/final/test_show_timeline.cjs
```

Viewer generuje v existujícím CAD prostředí skutečný `preview.py --viewer-only`;
nevkládejte náhradní model. Použijte připnuté `final/requirements.txt`. Celé
výrobní balení nadále vyžaduje vlastní generaci, kontrolu a slicer acceptance;
regenerace vieweru sama nepotvrzuje výrobní ZIP.

Nativní Linux build nepotřebuje ESP-IDF ani nový broker:

```sh
python3 kufr/simulator/native/build_host.py --output /tmp/kufr-native-twin
python3 kufr/simulator/native/test_host.py --executable /tmp/kufr-native-twin
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
dotnet kufr/backend/bin/Debug/net10.0/backend.dll
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

Fake integrační profil zůstává fake i při změně globálního provider režimu.
Skutečný Responses adaptér je možný jen s izolovanou databází, vypnutou pamětí,
`Backend.Dialog.Mode=responses`, existujícím správně provisionovaným klíčem a
`DigitalTwin.ExternalServicesAuthorized=true`. Tento příznak smí nastavit
provozovatel až po příslušném oprávnění. V tomto slice nebyl použit ani testován
placený provider. Pro reálnou síť se injektovaný clock ovládat nedá.

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
dotnet restore kufr/backend/backend.csproj -r linux-x64 -p:SelfContained=true --locked-mode
dotnet publish kufr/backend/backend.csproj -c Release -r linux-x64 --self-contained true --no-restore -o /tmp/twin-publish
python3 kufr/backend/deploy/twin/package-twin.py --publish /tmp/twin-publish --native /tmp/kufr-native-twin --viewer kufr/mechanika/final/exports/viewer.html --output /tmp/twin-release --build-id SOURCE_DIGEST
```

Přeneste nový ověřený balíček do nového přímého release adresáře. Na VPS:

```sh
python3 /opt/kufr-twin/releases/RELEASE_ID/install-twin.py --release /opt/kufr-twin/releases/RELEASE_ID --origin https://2.28.202.220 --live-device-id kufr-68ee8f4b6048
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
python3 kufr/backend/deploy/twin/export-recording.py /var/lib/kufr-twin/sim-development.jsonl /private/replay.json
python3 kufr/simulator/native/measure-twin.py --origin https://2.28.202.220 --credentials /etc/kufr-twin/operator-credentials.json --output /private/performance.json
```

Skutečný browser E2E používá Playwright pro opakovatelnou kontrolu GPU matice
`tray_3` (16 mm, ostatní 0), screenshotů, reloadu, Stop a viewportu 390×844.
Použijte již dostupný Playwright/browser a soukromý testovací credential soubor.
VPS proof vyžaduje explicitní `KUFR_TEST_VPS_PROOF=true`; skript smí adresovat
jen pevně zvolené simulační instance.

## Omezení a akceptace

IMPLEMENTOVÁNO: text, aktuální produkční host ownery, šest slotů/main/short,
syntetický Dance/audio enqueue, persistent journal, observer snapshot/transport,
CAD projekce, autorizační izolace, bounded záznam a replay, VPS balení.

SOFTWAROVĚ OVĚŘENO: native Windows/Linux regrese, původní backend a firmware
testy, ESP-IDF build, negativní HTTP testy, skutečný CAD browser E2E a syntetický
producent skutečného firmware serializeru přes VPS HTTPS. Přesné výsledky a
identity jsou v receipts; build/screenshot nejsou fyzickou akceptací.

NASAZENO NA VPS: oddělená služba a skutečně běžící host procesy, chráněný viewer
a ingest. Provider je fake; fyzický kufr nebyl ovládán ani flashován.
Aktuální ověřený release je `twin-20261014-v4`; source/build ID
`611164ada409925ec84de8fab4eaa5b5d0c5a5bcdfe4946529ac44d333252cdd`.
VPS test s 20 HTTPS polls po 50 ms a pomalým čtenářem 10 B/s naměřil median
5,25 ms, p95 9,11 ms, simulační Stop 11,59 ms a 4,67 snapshot updates/s.
Jde o host/VPS cestu; ESP režie ani odezva tím změřena není.

OVĚŘENO NA SKUTEČNÉM KUFRU: **ne**. Host mikrofon/kamera, plné ESP hlasové/SD
periferie a skutečné provider volání nejsou v tomto slice ověřeny/přeneseny do
hostu. Viewer o nich nevrací falešný úspěch. Virtuální kalibrace 1100/1900 µs a
500 ms je testovací předpoklad, nikoli měření aktuální sestavy. Časový/kinematický
model neověřuje servo moment, odběr, tření, akustiku ani mechanickou bezpečnost.
