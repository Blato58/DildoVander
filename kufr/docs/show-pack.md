# DildoVander Show Pack

Zadání `dildovander-show-pack` 2026.10.19.1 rozšiřuje DV01, DV02 a DV10.
Aktuální akceptaci vede [SHOW-PACK-INTEGRATION](../../docs/project/work-items/SHOW-PACK-INTEGRATION.json);
[mapa požadavků](show-pack-requirements.json) váže jednotlivá ID na zdroj a testy.
Software, skutečný poskytovatel, nativní iOS, nasazení a fyzická akceptace jsou
oddělené důkazy. Žádná nová funkce nepovyšuje Limited/Blocked na Named.

## Vlastníci a data

- `Features/Show/ShowCoordinator` je režijní projekce existující konverzace;
  `AutonomousLiveDialogue` zůstává vlastníkem Live a `WandDecisionService` výběru.
  Ovládání obsluhy používá revizi, session generation, omezenou frontu a TTL.
  Stop a rušení mají přednost. Zásah není potvrzení pohybu.
- `ShowSceneLibrary` obsahuje devět archetypů, každý v běžné, express a nemotorické
  variantě. `ShowSceneExecution` zpracovává omezené místní hranice, skutečný audio
  submission proxy, potvrzený drain a monotónní pauzu. Submission nepotvrzuje přesná
  slova. Fyzická prezentace nadále používá původní proposal a ESP journal.
- Šest `ShowRepertoire` profilů rozšiřuje stávající katalog o dikci, rivalitu a
  devět replik na hůlku; dvanáct ambientních replik má čtyři rodiny. Katalogová ID,
  názvy a fakta se nemění. Nálada je oddělená od tónu, tempa a bezpečnosti.
  Po platném výběru se střídá úvod, reakce odpovídající scéně a závěr. Omezená
  Historie plánů a pozorovaných replik je oddělená. Použití ID vyžaduje shodu
  fráze v aktuálním assistant transcriptu a korelované potvrzení drainu playeru.
  Parafráze či chybějící transcript zůstávají nedoložené; nejde o akustické měření.
- Režie, duel a import používají stávající `Backend:DatabasePath`; události,
  izolovaný konverzační kontext a kroniky stávající `.evening.sqlite` store.
  Jde o rozšíření současných úložišť, bez další služby či databázového produktu.
- DV10 ukládá identitu a proroctví do původního dokumentu/edice. Veřejná karta
  obsahuje pouze povolenou fikci a indicii. Plné proroctví dostává pouze owner
  po serverové časové hranici 15 minut, oprávněném odemčení nebo konci večera.
  Operátor může odemknout, ale nečte soukromé znění jiného hosta.
- Hůlka se generuje z textu; portrét upravuje oprávněnou fotografii stávající
  cestou. Při selhání obrazu zůstává skutečná textová identita a PNG karta bez
  předstíraného obrázku. GET nic negeneruje ani neclaimuje.
- Duel ukládá oba identity snapshoty, seed a tři kola. Síla/lest/obrana mají
  součet 9, cyklus úder/lest/štít přidává ±2, uložený jitter je −1 až +1.
  První tah se zamyká; timeout 15 s použije předem připravený automatický tah.
  Zrušení nemá vítěze a refresh není odveta.
- `manual_only` zůstává platné. Uzavření, expiry, quiet, odvolání přístupu ani
  deployment nic nemažou. Ruční smazání zneplatní související veřejné derivace,
  proroctví, duely, kroniku a pozdní joby; nepotvrzuje smazání u poskytovatele/záloh.

## Příprava a spuštění

Rozhovor používá sjednocený GPT Live/Realtime kontrakt podle [aktuální architektury](autonomous-live-memory.md). Samostatný ShowSpeechProvider čte pouze krátké autorské cue přes oficiální SDK; neobnovuje vyřazený STT/dialog/TTS rozhovor. Audituje jej původní cue journal s jediným pokusem.

Použij existující backend konfiguraci podle [backend README](../backend/README.md)
a [DV10](dv10/README.md). Nezakládej nový provider projekt ani klíč.
`Keepsake:Enabled=true` zpřístupní archivní funkce; generování má nadále vlastní
`GenerationEnabled` a explicitní `ProviderMode=fake|openai`. Vývojové testy používají
izolovaný fake režim a žádné placené volání.

Pro webový operátorský klient z jiného originu nastav přesný
`Show:AllowedOrigins` seznam (například vývojové `http://localhost:5174`). Žádný
wildcard ani cookie credential se nepovoluje. Backend nadále ověřuje bearer
server-admin na každé operátorské cestě. Seznam `Devices` určuje povolený kufr.
Na iOS zůstává credential v Keychain a `KufrDebug.showRequest` povoluje jen
konkrétní show cesty a metody. Web drží credential pouze v paměti do odhlášení.

```text
dotnet run --project kufr/backend/backend.csproj
npm ci --prefix kufr/contracts
npm ci --prefix kufr/mobilni-aplikace
npm run dev --prefix kufr/mobilni-aplikace -- --port 5174
```

Director otevři přes diagnostickou navigaci aplikace nebo `#director`; skrytý
vstup neuděluje oprávnění. Zadej backend a identitu zařízení a ověř přístup.
Stav serveru není živý stav zařízení. Spárovaný lokální transport zůstává
samostatnou autoritou pro Stop a offline ovládání.

## Provoz u kufru

1. Připrav večer stávajícím správcem večerů. Ověř místní kontrolu, kalibraci,
   dostupnost, audio a nevyřešený journal. Preflight sám servem nepohybuje.
2. Běžnou návštěvu zahaj stávající cestou. Obvyklé dvě obsahové otázky zůstávají;
   express nepřidává souhlas s výběrem ani nezkracuje bezpečný pohyb.
3. Director nabízí tón/tempo, náladu session nebo večera a návrat do automatu,
   scénové zvraty, zrušení řeči, odchod a rychlé dokončení. Volby mají TTL a působnost.
   Override hůlky je dovolen pouze před uzamčením výsledku, se skutečnou dostupností
   a aktuální revizí; ESP znovu ověřuje návrh. Žádný force ani obcházení obnovy.
4. Quiet a ambient mají skutečné místní ovládání i bez cloudového přístupu.
   Při kombinovaném zásahu UI přizná částečný výsledek, když místní cesta chybí.
   Ambient je výslovný opt-in; místní uložený režim nepotřebuje trvale připojený
   telefon. Přijatá návštěva, Stop nebo vyšší priorita ruší ambient.
5. Pro duel vyber dvě dokončené identity stejného večera a potvrď zájem hostů.
   Autoplay nevyžaduje dva telefony. Volitelné odkazy tahů mají vlastní scoped
   grant; veřejný přehled právo táhnout nedává. Výsledek a webová stránka zůstávají
   čitelné i bez obrázků. UI beat není potvrzení fyzického efektu.
6. Proroctví odemkni pro konkrétní outcome. Host si vlastní plné znění přečte
   v soukromé památce; na veřejné stránce zůstane jen indicie.
7. Uzavření večera je explicitní a odmítne aktivní návštěvu/obsazení nebo starou
   revizi. Vznikne faktická uložená kronika, nikoli smazání archivu. Pozdní
   oprávněný výsledek patří k původnímu večeru a vytvoří další edici.

Veřejný statický QR nadále otevírá `/keepsake/evening/`. Z přehledu vede odkaz na
`/keepsake/chronicle.html`; duel používá `/keepsake/duel.html`. Soukromá památka
zachovává stávající claim/recovery/share a exportní volby. Zamčený text ani portrét
nepatří do veřejného HTML, PNG, ZIP, preloadu či metadat.

## Offline banka a návrat online

`firmware/offline-bank/manifest.json` obsahuje české texty pro dva úvody, šest
katalogových výroků, dvě zakončení, dvě provozní hlášky a dvanáct ambientních vstupů.
Každý slot má hlavní a krátkou scénu. Aktuálně nejsou přiložené skutečné schválené
nahrávky: výstup správně hlásí `missing_assets`. Tóny nebo testovací ticho nejsou
mluvená banka. Offline Show proto nelze prohlásit za provozně připravený.

```text
python kufr/firmware/scripts/prepare_offline_bank.py --check
python kufr/firmware/scripts/prepare_offline_bank.py --audio-dir C:/Recordings/DildoVander --inspect-recordings
python kufr/firmware/scripts/prepare_offline_bank.py --audio-dir C:/Recordings/DildoVander
```

Nahrávky musí odpovídat schválenému hlasu, licenci a českému textu; vstup je WAV
PCM16 mono 24 kHz. Přípravný nástroj nic neposílá poskytovateli. Banka je v read-only
firmware image, s hashi, omezenou velikostí a proudovým dekódováním; nevyžaduje SD.
Rozložení flash a obě OTA pozice zůstávají. Použij stávající autorizovanou OTA
distribuci; tento postup sám neautorizuje flash nebo placené vytvoření nahrávek.

Místní preflight rozlišuje `ready`, `audio_only`, `missing_assets`,
`incompatible_catalog`, `calibration_required` a `recovery_required`. Arm vyžaduje
platný místní lease/epoch a kontrolu. Krátký stisk v připraveném armed idle zahájí
scénu; při běhu zachovává Stop, přidržení zachovává místní kontrolu. Stop je sticky.
Změna WAN ani reconnect nepřevezme řízení uprostřed scény.

Outbox má osm položek a rezervuje trvalý pending záznam před fyzickou akcí.
Plný outbox odmítá další archivovatelnou prezentaci. Synchronizace posílá pouze
záznamy přes `/v1/show/devices/{deviceId}/offline-outcomes`; až po úspěchu se lokálně
potvrdí `operationId`. Backend mapuje dvojici device/localEvening na samostatný
stabilní archivní večer, nikdy na právě aktivní večer. Čas je přiznaný boot/monotónní,
fyzická evidence `completed_by_timing`, nikoli měření nebo předání do ruky hosta.

## Kontroly a distribuce

```text
dotnet run --project kufr/backend-tests/BackendTests.csproj -- --show
dotnet run --project kufr/backend-tests/BackendTests.csproj -- --character
npm test --prefix kufr/contracts
npm test --prefix kufr/mobilni-aplikace
npm run build --prefix kufr/mobilni-aplikace
python kufr/firmware/tests/run_show_tests.py
python -m unittest discover -s kufr/web/keepsake -p test_*.py
python -m unittest discover -s kufr/web/deploy -p test_keepsake_proxy.py
python kufr/web/build-site.py --keepsake-only --destination C:/Temp/dildovander-guest-new
python scripts/project_status.py validate
python scripts/project_status.py render --check
```

Browser skript `mobilni-aplikace/scripts/check-director-browser.mjs` je opakovatelný
test skutečné aplikace proti izolovanému lokálnímu fake backendu, nikoli produkční
mock. Vyžaduje stávající testovací Playwright a dostupný Edge/Chromium. Přesné
provedené příkazy a jejich limity jsou v immutable receipts, nikoli odhadnuté v této
příručce. Přesný nativní otisk a konkrétní macOS CI pro Swift testy a nepodepsaný
iOS Simulator build vede kritérium NATIVE v
[externí akceptaci](../../docs/project/work-items/SHOW-PACK-OPERATIONS.json).
Starší CI neprokazuje změněné nativní zdroje. Skutečný iPhone, poslech Live,
nová hlasová banka, nasazení a měření mechaniky zůstávají samostatné brány.

`Show:DuelCommentaryEnabled=false` ponechává deterministického českého komentátora.
Zapnutí používá stávající Responses model/credential pro jedinou bounded kompozici
nad dokončeným výsledkem; pravidla ani body nemění. Nejasný placený pokus se slepě
neopakuje. Tato dokumentace nespouští poskytovatele ani nemění produkční flags.

## Povel pro skutečný kufr

Existující síťový worker ESP používá vlastní device token a HTTPS pro
`GET /v1/show/device-cues`. Posílá boot, generaci, revizi zařízení a monotónní čas.
Omezená schránka předává platný povel řídicí smyčce; síťové vlákno neovládá GPIO.
Světlo prochází původní `SceneIntentBridge` a fyzickou validací, bez serva.
Povely i jejich potvrzení mají stejnou korelaci, deadline a ochranu před opakováním.
Stop, nová generace, ztráta oprávnění nebo zrušení povel zneplatní.

Při zahájení se duel váže na nedávno připojené kompatibilní zařízení, nebo se
výslovně označí jako webový souboj bez mluveného potvrzení. Připojený kufr potvrzuje
úvod, jednotlivá kola a závěr až po dokončení přehrání; další fáze čeká. Výpadek
nepovýší neodehraný duel na dokončený. Potvrzení zařízení neměří slyšitelnost.
Online vyprávění používá stávající SDK TTS a hlas; nepotřebuje chybějící offline
nahrávky. GET audia pouze čte výsledek jednou zahájené generace. Nejasný pokus se
znovu neúčtuje automatickým opakováním. Testy používají výslovný fake režim.

Stejná cesta doručuje serverový ambient a změny ticha. Revize požadovaných
nastavení a skutečně aplikovaný stav jsou oddělené; starý snapshot po připojení
nepřepíše místní Stop. Lokální přehrávač sdílí existující audio port a rušení,
zatímco síť zůstává dostupná pro nové nastavení a zrušení povelu.
`stopRevision` je samostatný povel pro původní fyzický Stop, nikoli synonymum
ticha. Operátor má okamžitou místní cestu a výslovně označenou cestu přes backend;
ta vyžaduje WAN a čeká na potvrzenou revizi zařízení. Žádná z nich neměří pohyb.

Výchozí `Show:DeviceCueSpeechMode=configured` respektuje stávající
`Backend:Voice:Mode=openai` a jeho credential/hlas. `off` ponechá světelnou cestu,
`fake` slouží pouze syntetickému ověření a neoznačí duel jako skutečně namluvený.
Samostatný 8KiB síťový worker přebírá jen omezenou kopii již získaného device
tokenu s opravdovou expirací; nezakládá další identitu ani modelovou session.
Nekritické LED/vibrační cue mohou projít během Live, pokud původní fyzická brána
dovolí dekoraci. Lokální klip při obsazeném hlavním playeru dostane explicitní
busy fallback. Live po primárním drainu čeká konečně na přijetí, výsledek nebo
deadline klipu a dál zpracovává Stop. `PresentSelected` se váže až k uloženému
proposal/operation a jeho skutečnému outcome; nezavádí druhý motorový dispatcher.
