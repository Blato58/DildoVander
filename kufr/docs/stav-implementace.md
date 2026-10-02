# Stav implementace AI kufru

Poslední aktualizace: **2. 10. 2026**. Zdroj stavu: aktuální kód, hostitelské testy a lokální Arduino i ESP-IDF firmware build, kontrakty a simulátor E2, browser kontrola časové osy E2.5, Vite build, cílené testy adaptéru a E3a. Ad Hoc podpis a serverová publikace IPA/manifestu prošly CI a veřejným HTTPS testem; majitel potvrdil instalaci a spuštění na iPhonu 15 Pro (D70). Hardware, native WSS/TLS a aktualizace IPA na telefonu zatím nemají runtime akceptaci. Hotový návrh není hotová implementace.

[Implementační plán](implementacni-plan.md) určuje architekturu a akceptaci. [AGENTS.md](../../AGENTS.md) určuje pracovní postup AI. Tento soubor je jediný průběžný záznam postupu; README ho pouze odkazují.

## Předání další AI

**D124 – 2. 10. 2026 – iOS build pouze ručně pro podepsané nasazení.**
Majitel požádal odstranit iOS build z každého PR a ponechat jej pouze ručně
pro podepsanou aplikaci určenou k nasazení. `ci.yml` nyní používá webový
`kufr-web.yml`; mobilní/kontraktové testy, TypeScript/Vite build a artefakt
`web-dist` zůstávají povinné v `CI required`. Původní `kufr-ios.yml`
a unsigned simulator build/spuštění byly odstraněny.
`kufr-ios-sign.yml` je nadále pouze ruční a main-only, ale používá výhradně
Ad Hoc distribuční identitu. Přebírá ověřený web z CI, kontroluje Xcode 26.3
a provádí přesunuté nativní transport/debug testy před podepsaným device
buildem. Vývojová podpisová volba byla odstraněna. Ruční
`kufr-ios-deploy.yml` nadále publikuje existující ověřenou Ad Hoc IPA bez
nového buildu. Plán, mobilní README a CI/CD návod odpovídají tomuto postupu.
Lokální ověření prošlo: actionlint 1.7.12 s ověřeným checksumem nad všemi
workflow; 29 Node testů `scripts/ci/verify-build.test.cjs`; 6 Python testů
archivů v `scripts/ci`; průchod grafu všech pěti PR workflow bez macOS,
Xcode/simctl/cap sync ios a kontrola ručního main-only Ad Hoc sign/deploy
včetně správných secret/manifest vazeb. Lokální odkazy všech čtyř změněných
dokumentů a `git diff --check` prošly. Git předání a vzdálené CI následují.
Nativní build/podpis ani skutečná publikace nejsou součástí této změny.
Další krok: při požadavku na vydání aplikace ručně podepsat IPA z úspěšného
CI aktuálního main a následně spustit samostatnou publikaci.

**D123 – 2. 10. 2026 – první aplikační flash a začátek sestavování.**
Majitel výslovně požádal začít sestavovat ESP a nahrát firmware; všechny
komponenty má zabalené, zatím nejsou propojené. Po znovupřipojení pouze
USB-UART COM7 byl znovu ověřen MAC `68:ee:8f:4b:60:48` a čtením flash
potvrzená prázdná oblast NVS `0x9000..0xdfff` (20 480 B, vše FF).
Čtecí audit startupu potvrdil, že prázdná NVS nezapne síť ani audio GPIO;
kalibrace 0 a neověřená inspection stále zakazují zotavení/pohyby.

Bez změny zdrojového kódu byl sestaven a nahrán současný aplikační profil
`esp32-s3-idf` ze source commitu `f23853e3ce310b572dd81b9cdc54bdbafeb874b6`.
První Windows build narazil na interní pád GCC v nepoužívaném SDK LCD
driveru. Opakování `pio run -e esp32-s3-idf -j 2` prošlo: RAM 66 032 B,
flash 1 202 041 B. `pio run -e esp32-s3-idf -j 2 --target upload --upload-port COM7`
prošlo; esptool ověřil hash všech zapisovaných obrazů. Nový aplikační
`firmware.bin` má SHA-256
`065f6aba2edcc3c90e29cbcb62e80ba13e6704d74baca6b9eb095f20301374af`.
Původní plná factory záloha a diagnostické obrazy z D122 zůstávají uložené.
Nové obrazy/ELF, build/retry/upload logy, NVS readback a dva boot logy
jsou v podsložce `application-first-boot` místní složky uvedené v D122;
`manifest.json` obsahuje source commit, hash souborů a výsledek bootů.

Oba řízené EN/RTS restarty s 30 s UART capture prošly: bootloader 16 MB,
8 MB Octal PSRAM/80 MHz a SDK memory test OK; požadavek vypnout pohony
(`no GPIO adapter`), neprovisionovaná identita, nenastavená síť a start
ESP-IDF control loop. Žádný neočekávaný restart, panic, brownout, watchdog
ani journal chyba v obou záznamech. To potvrzuje pouze základní offline
aplikační boot. Neprokazuje elektrický inhibit/watchdog, servo/motor/LED
adaptér, zvuk, Wi-Fi/TLS/WSS, provisioning ani mechanickou akceptaci.
Není nastaveno `profile_a_ok`, síť, identita ani jakýkoli credential.

Majitel potvrdil, že nemá multimetr. Modely jsou podle
[nákupního seznamu](../../docs/nakupni-seznam-kufr.md): PCA9685 Drátek 1686,
PD trigger 232511 a XY3606 180391 s pevným výstupem 5 V. Bez měření
výstupu zůstávají PD trigger/měnič a výkonové větve odpojené; tím není
blokované sestavování logiky napájené pouze USB.
Další krok: při odpojeném USB připravit čtyři logické propoje ESP32/PCA9685
podle profilu A: 3V3/VCC, GND/GND, GPIO1/SDA, GPIO14/SCL. Servo V+, OE
a všechny výstupy zatím nezapojovat. Nákupní seznam obsahuje Dupont M-F;
pro dvě osazené samčí lišty jsou potřeba F-F nebo správně pájené propoje.
Majitel potvrdil, že nemá F-F propojky ani páječku. První logické zapojení
čeká na čtyři F-F propojky; původní M-F nákupní položka tuto dvojici
osazených lišt přímo nepropojí. Firmware dosud nemá PCA GPIO/I2C adaptér a toto je
návrh dalšího kroku, nikoli provedený I2C test. B1 zůstává otevřené pro
bezpečnostní větev a periferie; bezpečnostní obvod není v původním košíku.
Tento krok pouze zaznamenává skutečný flash/boot; nové zdrojové změny
firmware ani placené AI/serverové nasazení nejsou jeho součástí.
Dokumentace prošla kontrolou lokálních odkazů a `git diff --check`.
Commit `426b5ee05eed5a751d47e23b55057392f94ab766` byl úspěšně pushnutý
přes `scripts/Push-CurrentBranch.ps1` do `docs/esp-application-first-boot`.
PR vyžaduje zelené `CI required` před sloučením podle CONTRIBUTING.

**D122 – 2. 10. 2026 – první USB flash a paměťový test doručené desky.**
Majitel dodal fotografie ESP32-S3 N16R8 a výslovně schválil minimální
diagnostický firmware a jeho nahrání. Připojený USB-UART má COM7,
VID 1A86/PID 55D3. Čtecí ROM dotazy potvrdily ESP32-S3 revizi čipu v0.2,
8 MB embedded PSRAM, 16 MB Quad flash a 3,3 V flash; secure boot a flash
encryption jsou vypnuté. Revize čipu není revize PCB. Zadání je pouze
USB diagnostika paměti a bootu, bez externích periferií či pohonů.
Samostatný projekt `firmware/diagnostics/board-memory` byl sestaven,
nahrán a ověřen na této fyzické desce. Obsahuje pouze paměťový test a
UART log; neobsahuje aplikaci kufru ani provisioning či externí periférie.
Změny zahrnují jeho CMake/PlatformIO/config/src/README, firmware README,
pinový podklad, tento stav a samostatný build krok ve firmware CI.

Před prvním zápisem byla čtením ROM/stub uložena celá původní flash
(16 777 216 B), bez erase-all a bez zápisu eFuse. Záloha a logy jsou mimo
Git v `C:\Users\vojtech.petrik\AppData\Local\DildoVander\board-diagnostics\68ee8f4b6048\2026-10-02\`.
`factory-flash-16mb.bin` má SHA-256
`d73b6dd5a15e469a30629798664fb6e9e5c0ee30ef2679d75858d97a72b6fb15`.
Složka obsahuje i `manifest.json`, build/upload log, `boot-1.log` až
`boot-3.log` a kopie diagnostických obrazů/ELF. Diagnostický `firmware.bin`
má SHA-256 `ef803c0b834ba45c31fab953364297a776ead34329ae6009398b3a6fac4e9f47`.

Ověření: PlatformIO Core 6.2.0, espressif32 7.1.3 / ESP-IDF 6.1.0,
`pio run` v diagnostickém projektu – SUCCESS, RAM 15 820 B,
flash 161 325 B; první konfigurace potřebovala zahrnout syntetický
`__pio_env` komponent PlatformIO. `pio run --target upload --upload-port COM7`
– SUCCESS, esptool ověřil hash bootloaderu, partition table a aplikace.
Bezprostředně před zápisem byl znovu ověřen MAC `68:ee:8f:4b:60:48`.
Tři řízené EN/RTS restarty, každý s 35 s UART záznamem, všechny PASS:
16 777 216 B flash, 8 388 608 B PSRAM, Octal PSRAM 80 MHz,
SDK startup memory test OK, všechny čtyři adresně závislé XOR vzory
nad 7 340 032 B, heap integrity PASS a pět heartbeatů bez neočekávaného
resetu, panic, brownoutu nebo watchdog chyby. Interní volný heap
393 852 B a externí 8 386 308 B zůstaly během heartbeatů stabilní.
Kontrola lokálních README odkazů, YAML parsování a `git diff --check`
prošly. Nezávislá čtecí revize nenašla blokující chybu; PASS neznamená
hustý test celých 8 MiB (SDK boot test vzorkuje po 32 B), dlouhodobý/tepelný
test, endurance flash ani přímé ověření nativního USB-OTG.

B1 zůstává otevřené pro bezpečnostní větev, zapojení, kalibraci a fyzické
efekty; dnešní souhlas platí pro tento diagnostický obraz.
Další konkrétní krok: určit a změřit externí napájení, inhibit/watchdog,
servisní vstup a profil A; teprve s těmito podklady povolit periferie,
provisioning a další flash aplikačního firmware. Diagnostika může zůstat
na desce, externí komponenty zatím nepřipojovat.

Git předání D122: implementační commit
`21c07cc33b4de1cc53f593e72b7485f2314c109a` byl přes
`scripts/Push-CurrentBranch.ps1` pushnut na
`origin/feat/board-memory-diagnostic`; remote shoda byla ověřena.
Následující dokumentační commit zaznamenává toto doručení a odstraňuje
koncové prázdné řádky nových konfiguračních souborů nalezené při staged
whitespace kontrole; jeho `6048887240bce83f89dec341b5632b79786a31c3`
byl také ověřeně pushnut. Při integraci aktuálního main `e1da30a` byly
zachovány iPhone opravy/publikace a serverové CI runnery. Tento záznam
byl přejmenován na D122, aby zůstal zachovaný existující D120/D121.
[PR #94](https://github.com/Blato58/DildoVander/pull/94) eviduje finální head,
CI a autorizované sloučení; před merge musí projít povinné kontroly.

**D121 – oprava režimu tichého Realtime hlasu na iPhonu (2. 10. 2026).**
Majitel hlásí příliš tichý hlas v aplikaci. Hands-free přehrávač používal
`playAndRecord/voiceChat`, přestože jde o poloduplex a engine nezapíná
voice processing. Telefonní režim může zeslabovat reproduktor. Oprava
v `DebugHandsFreeAudio.swift` přepíná session i kontrolu route na `.default`
a zachovává `.defaultToSpeaker`, vstupní bránu během řeči a detekci přerušení.
Mobilní README popisuje režim a nastavení hlasitosti během přehrávání.
Lokálně prošel TypeScript/Vite build, všech 24 mobilních testů a whitespace.
CI `36944228707` na head `9cfbfa1` prošlo: macOS nativní route/converter/upload
testy, Xcode iOS Simulator build a spuštění aplikace i všechny povinné
serverové kontroly. [PR #92](https://github.com/Blato58/DildoVander/pull/92)
je sloučen jako `e9de404d7043ea09602e3d4ca9bb557363efde35`; změna je ověřena
na main. Skutečnou hlasitost musí potvrdit iPhone.
Implementační commit `8132191f22ada535fab8bd157a86febd85cfc272` je pushnutý
do `fix/iphone-realtime-speaker-volume`; remote SHA bylo ověřeno. Předepsaný
PowerShell helper byl spuštěn, ale zde není spustitelný a `pwsh` chybí;
push provedl scoped gh credential helper po ověření účtu Blato58.
Majitel následně výslovně schválil podpis a publikaci aktualizace. Ad Hoc
podpis [36946442779](https://github.com/Blato58/DildoVander/actions/runs/36946442779)
prošel nad main `e9de404` a ověřeným CI výše; nový build je **1000.0.9**.
Publikace [36946600253](https://github.com/Blato58/DildoVander/actions/runs/36946600253)
prošla všemi třemi joby. [Safari instalace](https://2.28.202.220/ios/)
nabízí verzi 1.0 / **1000.0.9**, release `36946600253-1`.
Serverový runner ověřil přes veřejné HTTPS stránku, manifest, Bundle ID,
stažení IPA, SHA-256 a MIME typy. Přímé čtení z cloudového workspace opět
narazilo na proxy certifikát pro cizí hostname; TLS ověřování zůstalo zapnuté.
To není nezávislá kontrola z jiné sítě ani měření hlasitosti iPhonu.
Další krok: majitel aktualizuje aplikaci, ověří hlasitost Realtime odpovědi
a alespoň dvě výměny v režimu bez tlačítek; během řeči ji může nastavit
bočními tlačítky. Backend ani firmware nebyly nasazeny a neproběhl placený AI
test. Dokumentační commit `282c6c2845650c14114acfd88da191d1bf298e29`
byl pushnut do `docs/realtime-volume-release` a remote SHA bylo ověřeno.
Lokální Markdown odkazy a whitespace prošly. PowerShell helper zde stále
není spustitelný; scoped gh helper provedl push po ověření účtu Blato58.
Finální CI a sloučení dokumentace bude evidováno v navazujícím PR.

**Migrace GitHub Actions na vlastní serverový runner (1. 10. 2026).**
Majitel požaduje všechny joby na svém serveru, kromě macOS. Všech 22 Linux
jobů používá `[self-hosted, Linux, X64, kufr-backend]`, tedy
`dildovander-server`; iOS Simulator a IPA signing zůstávají na `macos-15`.
Node setup doplněn do repository/backend kontrol; .NET SDK používá
zapisovatelný runner tool cache. PlatformIO i OpenAI SDK live eval běží
ve vlastním venv. Realtime test kontroluje předinstalovaný espeak-ng,
nepředpokládá neomezené sudo. Host probe ověřuje build nástroje a venv.
Plán, CI/CD, SECURITY a backend/AI README popisují společný dlouhodobý
runner a omezení veřejného HTTPS testu z téhož serveru. Main-only podmínky,
permissions, dependency graf, timeouty a provenance zůstávají zachované.

Lokálně prošel audit runnerů, actionlint 1.7.12, 29 provenance/promotion
+ 6 archive testů, dokumentační odkazy a whitespace. Probe 36926403741
potvrdil online Ubuntu 26.04 runner a aktivní backend. První CI
36926992141 odhalilo chybějící g++/venv; správce doplnil
`build-essential python3-venv espeak-ng`. CI 36930988401 pak na serveru
ověřilo repository, web, offline AI, všechny firmware host testy, oba
ESP-IDF/Arduino buildy s artefaktem a backend build/testy/publish/HTTP smoke.
Mac v tomto běhu GitHub odmítl před prvním krokem kvůli billing limitu;
majitel následně potvrdil opravu účtu. Původní macOS CI 36926992141 prošlo,
finální společná verze nyní zachovává také D120 opravu iPhone uploadu z main.

Implementační commit `2ff2ada` a následné integrace byly ověřeně pushnuté.
PowerShell helper byl spuštěn, ale není zde spustitelný; scoped gh git
credential helper provedl push po ověření účtu Blato58. Finální CI head,
výsledek všech Linux/macOS kontrol, sloučení a read-only ověření runneru/
veřejného HTTPS jsou evidované v [PR #88](https://github.com/Blato58/DildoVander/pull/88).
Produkční aktivace, podpis/publikace nové IPA, placené AI ani flash nejsou
součástí migrace; stávající akceptační mezery D120 a HW zůstávají zachované.

**D120 – oprava iPhone audio uploadu publikována v IPA 1000.0.8.**
Majitel výslovně schválil publikaci po úspěšné kontrole a podpisu.
PR #90 je sloučen jako `ceb40fbc06d566d116e62180d576a4e092cf968e`;
finální CI 36936917112 ověřilo head `83a2e15`. Původní podepsaný build
1000.0.7 (36937398196) nebyl publikován: mezitím se do main sloučila
migrace runnerů z PR #88. Nový Ad Hoc podpis 36940005506 použil úspěšné
CI 36937790022 a aktuální main `cc2941ee5a52356553c891a376b6fec06e3c41ff`;
provenance, striktní podpis i upload artefaktu prošly.
Publikace 36940186009 prošla všemi třemi joby. [Safari instalace](https://2.28.202.220/ios/)
nabízí 1.0 / **1000.0.8**, release `36940186009-1`. Serverový runner přes
veřejné HTTPS ověřil stránku, manifest, Bundle ID, download IPA, SHA-256
a MIME typy. Není to nezávislá kontrola z jiné sítě. Přímá kontrola z tohoto
cloudového workspace narazila na proxy certifikát s cizím hostname;
TLS ověřování nebylo vypnuté. Majitel musí potvrdit aktualizaci a hlasový
rozhovor na skutečném iPhonu/5G. Backend nebyl nasazen ani restartován,
neproběhl flash desky ani placený AI test.

Majitel sdílí `realtime_live_audio_overflow` a stabilní připojení. Nalezený
defekt: iOS vytvářelo a rušilo URLSession pro každý PCM POST (nejvýše 200 ms
audia), přitom mělo jen 1 s frontu a při průběžném drainu posílalo i menší
bloky. Chyba sama nedokazuje nestabilní připojení.

`DebugHandsFreeSession.swift` nyní drží jedno ephemeral HTTP sezení pro
PCM a playback ACK, zachovává zákaz redirectu a timeout. Stop ruší aktivní
požadavky; normální close nechá doběhnout už potvrzené /played HTTP 204.
`DebugHandsFreeAudio.swift` skládá přesné 9600B/200ms dávky, zachovává
48000B limit a pořadí bez paralelních PCM POSTů. Input epoch zahazuje
pozdní převod předchozího poslechu. `DebugPanel.ts` vysvětluje skutečné
zaplnění fronty místo automatického připsání chyby spojení. README,
native handsfree peer a `scripts/test-native-debug.sh` doplňuje nový
`tests/native_handsfree/upload_regression.swift`.

Lokálně prošly TypeScript/Vite build a všech 24 mobilních testů (Node
24.19.0/npm 11.9.0; CI používá připnutou verzi), bash/Node syntax a
`git diff --check`. Swift 6.1.2 pro Linux z dočasného toolchainu zkompiloval
skutečné deklarace upload bufferu a HTTP helperu s `-swift-version 5
-warnings-as-errors`; nový loopback test prošel dávky/PCM pořadí při 50ms
capture a 150ms HTTP odezvě (první odpověď 550 ms), kapacitu/overflow,
input epoch, jedno TCP spojení pro 10 POSTů, zákaz redirectu a graceful
finish/cancel. Izolovaná baseline s původním per-frame URLSession otevřela
10 spojení a prokazatelně selhala na reuse assertion. Původní multi-turn
loopback test také prošel s dočasnou Linux adaptací importů a bez Keychain
a AVAudioConverter testu; jeho převodník/iPhone audio tím není ověřený.
Linux FoundationNetworking upozorňovalo na již existující Sendable mutable
state v live-stream delegátovi; nativní macOS kompilace nyní prošla.

Nové čtení logů workflow 36931829723 selhalo před prvním krokem runneru:
GitHub annotation uvádí neúspěšné platby nebo nutnost zvýšit spending limit.
Poslední úspěšný snapshot 36928995676 je z 21:29 UTC a předchází screenshotu,
proto nepotvrzoval konkrétní příčinu incidentu. Majitel následně opravil
billing. Opakovaný CI běh 36932961956 (attempt 2, head `980301a`)
prošel všemi povinnými kontrolami: macOS Swift harness včetně nového upload
testu, Xcode 26.3 simulator build a spuštění aplikace, web/kontrakty,
backend a oba firmware profily/host testy. Placený AI job zůstal vypnutý.
Nové serverové čtení 36936002408 a filtrované 36936100858 proběhlo úspěšně:
responses/openai dostupné, 0 aktivních rozhovorů, žádné droppedWrites ani
truncated. U rozhovoru v 21:47 UTC byl úvod dokončen a poslech otevřen,
poté telefon ukončil stream (499); žádná provider chyba v dané korelaci.
To odpovídá lokálnímu overflow, ale samo neměří upload RTT ani neprokazuje
celou příčinu screenshotu. Implementační commit
`3fdcf6ac9261ab1edc2ddf63780d17eea09427bd` byl pushnut do větve
`fix/ios-realtime-upload-overflow`, shoda vzdáleného SHA ověřena.
Push-CurrentBranch.ps1 byl spuštěn, ale nemá uložený OAuth token účtu;
session API potvrdilo Blato58 a push proběhl přes scoped gh credential
helper bez změny ostatních účtů. [PR #90](https://github.com/Blato58/DildoVander/pull/90)
eviduje finální head, povinné CI a výsledek sloučení. Tento zápis aktualizuje
původní blokaci; povinné CI následně ověří i tento dokumentační head.
Další krok: majitel nainstaluje 1000.0.8 a ověří úvod, alespoň dvě vlastní
hlasové odpovědi, automatické obnovení poslechu a Stop na skutečném
iPhonu/5G. Při další chybě dohledat aktuální trace/operation v logu.

**D119 – Realtime hlas přímo na ESP a nastavení v Ovládání; všechny provider cesty přes SDK.**
Majitel 1. 10. požaduje přidat Realtime do firmware, nastavovat hlasový režim
v Ovládání aplikace a převést všechna volání OpenAI na SDK. Rozsah a výjimka
z ručního lease pro samostatné hlasové příkazy jsou odůvodněné v plánu D119.
ESP-IDF nyní drží autentizovaný backend NDJSON stream a pořadové PCM POSTy;
telefon neposílá audio ani provider klíč. Server VAD obnovuje poslech po
potvrzeném dohrání, lokální Stop okamžitě umlčí zesilovač. Odpověď/návrh
projde existující korelací, snapshotem, maskou a TTL do PendingProposal;
volný text ani audio přímo nespínají pohony. Provoz je omezený délkou,
počtem tahů, velikostí audio/eventů a existujícími serverovými cost caps.

Audio adaptér používá standardní I2S 32 kHz, ICS43434 a MAX98357A s
průběžným polyphase FIR převodem 32 ↔ 24 kHz a počáteční hlasitostí 25 %. Zapne se
jen s externě servisně ověřeným profilem A (`wand_audio/profile_a_ok=1`),
bez kamery/SD. Bez tohoto ověření UI hlásí nedostupné audio. Profil B,
PTT, echo cancellation a hardwarová akceptace se tímto nedoplňují.
Ovládání nabízí Vypnutý/Realtime, Marin/Cedar/Coral, Použít, Spustit a
Zastavit. Stav/nastavení potvrzuje ESP přes `voice.ack` a snapshot, nejsou
optimistické; start vyžaduje samostatný ready stav a aktuální revizi.
Odpojení pozorovatele samostatný rozhovor nezastavuje, ruční převzetí a Stop
ano. Nejasné požadavky se po reconnectu nepřehrávají; volba platí do rebootu.

Responses, kompatibilní TTS/STT a Python eval nově používají připnuté
oficiální SDK; Realtime už SDK používal. BoundedSdkTransport omezuje odpověď
před SDK bufferingem, zachovává pevný endpoint/no redirects, timeout,
validaci a serverové rozpočty. Retry je explicitně nula; test 429 ověřuje
jeden provider pokus pro každou cestu. STT protokolový overload zachovává
`languages[]=cs`. Python live eval instaluje openai 3.22.1; offline testy
zůstávají bez klíče a síťových volání.

Změněné soubory: firmware `features/voice_turn`, backend_access,
local_control, conversation start, JsonCursor, IDF composition/CMake a
hostitelské testy/stubs; mobil `LocalVoiceClient`, `VoicePanel`, connection,
main/style a test loader; JSON schema/fixtures; backend OpenAi transport,
ResponsesTextDialogClient, OpenAiSpeechClient, OpenAiTranscriptionClient,
TextDialogRunner a provider testy; Python eval/requirements/test_sdk,
workflow, README, AGENTS a plán/stav.

Lokálně prošly všechny firmware host testy včetně striktního NDJSON,
resamplingu, úvodu a dvou navazujících mic tahů, pořadí uploadu, drain/ACK,
Stop, cost_limit a chyb. Regresní test zadrží mikrofonní blok předchozího
poslechu až do začátku dalšího: input epoch jej zahodí a nedovolí
přeznačit na nové Input-Id. Samostatný test audio driveru ověřil servisní
bránu profilu A, drain, Stop při DMA zápisu a odmítnutí pozdního audio
bloku až do explicitního Begin nového sezení (oprava nalezeného závodu).
52 skutečně emitovaných firmware zpráv prošlo
sdíleným schématem. Oba PlatformIO profily prošly: ESP-IDF RAM 66 032 B,
flash 1 202 041 B; Arduino serial RAM 18 660 B, flash 286 589 B (nezahrnuje
běhový heap/stack). Backend HTTP smoke a testy providerů, Realtime, journalu, fake voice,
rozpočtů, expirace a migrací prošly. Mobilní build a 24 testů, kontrakty
25 validních/41 nevalidních zpráv a 21 offline Python eval testů prošly.
Chromium na 390 px ověřil konfiguraci/start/stop, zachování rozpracované
volby při heartbeat, uzamčení aktivního rozhovoru, viditelný cost_limit a
zakázání hlasu ve fake režimu, bez JS chyb a horizontálního přesahu.

Git předání: implementační commit `229904b5852a9b8755dac0758341fa6501c2269e`
byl pushnut přes autorizovaný Push-CurrentBranch.ps1; shoda remote větve
`feat/esp-realtime-voice-sdk` byla ověřena. Helper vyžaduje uložený účet;
zdejší session credential byla přihlášena jen do dočasného izolovaného
GH_CONFIG_DIR a účet Blato58 ověřen, ostatní GitHub účty se nemění.
[PR #85](https://github.com/Blato58/DildoVander/pull/85) je bez konfliktů a
čeká na povinné CI včetně Swift/Xcode/iOS Simulatoru; lokálně zde Xcode není.
Finální původní head `938f9cc8f7dd2b0d54bed9389983ebedfaf9dd1c` byl
ověřeně pushnut a CI 36923066577 prošlo všechny povinné kontroly včetně
Swift/Xcode/spuštění iOS Simulatoru a obou firmware profilů. Během CI se
do main sloučila oprava iPhone startu z PR #84 (`0d97dea`); obě změny jsou
zachované. Společná verze `5e73e21` byla ověřeně pushnuta a CI 36924319856 prošlo
všechny povinné kontroly. Závěrečná ochrana input epoch dále brání přenosu
rozpracovaného mic bloku do následujícího poslechu. Nový regresní test
prokazatelně selže na původním transportu a projde s opravou; ESP-IDF build
prošel znovu. Povinné CI ověřuje poslední head tohoto PR.
CI 36925265141 poté ověřilo všechny povinné kontroly headu `54d5f9f`,
včetně regrese input epoch. Mezitím se do main sloučila diagnostika z PR
#86 (`0330ce7`); zachovaná další merge integrací, tento slice nově D119.
Po další integraci lokálně znovu prošly backend testy včetně ServerLogTests,
HTTP smoke, mobilní build/24 testů, kontrakty a browser panel hlasu.
CI ověřuje společnou finální verzi. Aktuální výsledek kontrol a sloučení
je doložený přímo v PR.
Tento krok nenasazuje backend, nevydává IPA, nenahrává firmware a nevolá
placené OpenAI. Skutečný mikrofon/reproduktor, paměťové/časové rezervy při
TLS, Stop během audia a 100 tahů na desce ani ovládání na iPhonu nebyly
ověřené. B1/fyzická bezpečnost, kalibrace a HW část E9 zůstávají otevřené.
Další konkrétní krok: po zeleném CI servisně ověřit profil A a bezpečný B1
stav, samostatně autorizovat flash/nasazení a měřit opakovaný rozhovor,
Stop, Wi-Fi výpadek, návrat k poslechu a reálnou češtinu na desce.




**D118 – nasazeno: serverové logy, iOS zobrazení a čtecí integrace Codex.**
Zadání majitele: logování serveru, API přístup, log v iOS a společný debug
nasazeného prostředí s Codexem. Backend přidává oddělenou SQLite logovou DB,
výchozí historii 24 h/5000 událostí, kurzor/filtry/retention gap a počty
neúspěšných zápisů. Každý HTTP požadavek má serverové UUID X-Request-Id;
log používá route template, status/duration, pevný event/code a ID operace/
rozhovoru. Realtime zaznamenává fáze/chyby i uvnitř HTTP 200 streamu.
Úspěšné GET/poll/PCM jsou vynechané. Žádná těla/query, text, audio, credential,
prompty nebo exception message. ILogger adaptér přijímá jen známé bezpečné
události ConversationStore. Logovací selhání nesmí zrušit živý audio tok.
GET /v1/admin/logs a /status vyžadují admin nebo oddělený reader token;
reader nezískává správu, device token ani AI. Deploy volitelně přijme
LOG_READER_CREDENTIAL z backend-production a ukládá pouze SHA-256.
iOS Debug čte logy nezávisle na rozhovoru, filtruje, obnovuje po 2 s a sdílí
JSON. Drží 200 událostí, ošetřuje reset profilu/DB, staré výsledky a pozadí;
credential zůstává v Keychain. Codex stdio MCP má read_server_logs/status,
HTTPS PKI, žádné redirecty, omezenou odpověď a projekci typovaných metadat.
GitHub Kufr server logs umožňuje totéž bez lokálního tokenu, přes existující
serverový secret a jednodenní artefakt; žádné provider volání.
Rozsah: backend Features/Diagnostics, middleware/API/Realtime/operace/testy;
OpenAPI/verifier; DebugBackendClient/plugin/Panel/ServerLogs a native/browser
harness; deploy/read workflow a Codex Python klient/testy/návod; AGENTS/plán/README.
Lokálně prošly locked restore .NET 10.0.303 linux-x64, Release build bez
warnings/errors, kompletní backend testy včetně persist/TTL/cap/filtrů a
oddělení oprávnění, HTTP smoke včetně restartu/sanitizace/query/trace.
Mobilní TypeScript/Vite build a 21 testů, všechny kontrakty, 15 Python testů
(včetně skutečného HTTPS MCP peeru, redirect/denial/oversize/stdio/snapshot),
a actionlint 1.7.12 všech workflow prošly. Playwright Chromium ověřil
filtr/dedup, obnovování během rozhovoru, zapomenutí profilu, HTTP denial se
zastavením obnovování a 390px rozložení bez přesahu/console chyb.
Browser odhalil kolizi společné CSS třídy logových seznamů; aplikační log
nově vybírá svůj vlastní seznam a serverový log zůstává oddělený.
CI 36923732710 již ověřilo Swift/nativní harness i Xcode/spuštění
simulátoru, backend a web. Před merge se do main sloučila souběžná D117
oprava iPhone Realtime audio startu (PR #84, 0d97dea). Zachovaná merge
integrací; konflikt pouze v tomto záznamu vyřešen ponecháním obou změn.
Logy mají nyní číslo D118. Společnou verzi znovu ověřuje povinné CI.
Žádné placené AI volání.
Implementační commit `3446dd3beb9e5164e10f9d16189f7bfab2d1175e` byl pushnut,
remote shoda ověřena; [PR #86](https://github.com/Blato58/DildoVander/pull/86).
Push-CurrentBranch.ps1 byl spuštěn, ale gh auth token --user zde nepodporuje
runtime GH_TOKEN. API ověřilo účet Blato58; scoped gh git credential helper
provedl push bez změny účtu nebo uložení tajemství. Finální macOS/CI/merge
výsledek je evidovaný v PR. Review doplnilo rušení logového HTTP i při
otevření konfigurace a zapomenutí nativního profilu.
Finální CI 36924528705 prošlo celé včetně Swift/Xcode/iOS Simulatoru a obou
firmware profilů. PR #86 sloučen do main `0330ce7b7514897304b91588056eefba0608a278`;
shoda tree s ověřeným head `18fd319c23dd7dd1dd985488652a34529757cc85` doložena.
Deploy 36925672662, Ad Hoc podpis 36925679081 a publikace 36926024016 prošly.
Nasazení zachovává responses/gpt-6.1-sol a openai; nepouští placený test.
[Služba a Safari instalace](https://2.28.202.220/ios/) nabízejí iOS 1.0/build
1000.0.6 (sign run_number 6); externí runner ověřil HTTPS stránku/manifest/IPA.
Živé čtení workflow 36926028955 úspěšně ověřilo log API a režimy přes veřejné
HTTPS s credentialem z runneru, včetně typované projekce. V tomto cloudovém
prostředí je stažení Actions artefaktů blokované (Blob 403) a přímý curl na IP
odmítl hostname certifikátu proxy; TLS se neoslabuje. Autorizované Actions
log API funguje. Následná úprava proto přidává stejný validovaný technický
JSON do konzole workflow mezi KUFR_LOG_SNAPSHOT_BEGIN/END, aby Codex mohl
číst logy i v této relaci bez stažení artefaktu. Artefakt žije 1 den, konzole
má retenci Actions logů nastavenou v GitHub repozitáři. Token ani obsah
rozhovoru se do obou cest nevkládá. Ověření konzole a finální merge předání
jsou evidované v následném PR této úpravy; instalovaný backend/iOS se kvůli
čtecímu workflow znovu nepřekládá ani nenasazuje.
Další krok: aktualizovat iPhone a vyzkoušet Log serveru během rozhovoru,
filtr/obnovování/sdílení a Stop/pozadí. Fyzický iPhone zde není dostupný;
B1/fyzické pohony zůstávají otevřené. Přímé MCP je volitelné po nastavení
reader tokenu v procesu Codex; GitHub čtení využije existující admin secret.

**D117 – oprava startu Realtime, IPA 1000.0.5 publikovaná.**
Majitel u 1000.0.4 doložil chybu hned po Začít AI rozhovor; oranžová tečka
mikrofonu se objeví, první replika nezazní. Zdrojový průchod našel dvě
možné startovací chyby: každá audio route notifikace rušila rozhovor včetně
vlastního přepnutí playAndRecord a převodník považoval prázdný výstup při
rozběhu resamplingu za chybu. Nativní klient nyní ignoruje vlastní
category/override/configuration oznámení jen při shodě kategorie, režimu,
route a vstupního formátu; skutečné změny zařízení dál bezpečně zastaví.
Tap používá outputFormat vstupního uzlu a zavřený vstup se nepřevádí.
Stavový PCM převodník připouští nulový výstup bez ztráty dalšího vstupu.
Debug rozlišuje audio/HTTP/protokol/TLS/timeout chyby a ukazuje bezpečný
technický kód; neposílá surový text chyb, credential ani URL.
Soubory: DebugHandsFreeAudio/Session, KufrDebugPlugin, DebugPanel,
nativní peer/harness a testovací skript, testovací UI, README a tento stav.
Lokálně prošly TypeScript/Vite build, všech 19 mobilních testů, Node
syntax peeru, 16 lokálních dokumentačních odkazů a git diff --check.
In-app Browser ověřil konkrétní audio hlášku/kód, Stop a obnovený poslech
po novém startu přes syntetický bridge. Technický kód se zalomí i na telefonu.
Nový macOS test používá skutečný AVAudioConverter 44,1/48kHz (mono/stereo)
-> PCM16/24kHz, krátké/počáteční buffery,
route policy a časné HTTP/protokol chyby vedle původních tří tahů.
Finální CI 36922914322 prošlo všechny povinné kontroly včetně nativního
testu, Xcode sestavení a spuštění iOS Simulatoru. Read-only review vedlo
k explicitnímu příznaku audio lifecycle: opožděné callbacky po vlastním
Stop se ignorují, ale systémové zastavení enginu nepohltí skutečné přerušení.
Commity `d8de06fa716ba381b8f24250f22c1c473187ec88`,
`2dd0e7fb659dc2f151446ca06636b017a92f4182` a finální CI head
`1b1e3abd796861bea583c92da68d129fbadc8f52` byly ověřeně pushnuté.
[PR #84](https://github.com/Blato58/DildoVander/pull/84) byl sloučen do
main `0d97dea991dbadb27fa8f286fd2c98638227f81c`; shoda tree s CI head ověřena.
Ad Hoc podpis 36924073561 a publikace 36924312823 prošly; instalační
stránka i skutečná veřejná IPA uvádějí 1.0 / **1000.0.5**. Bundle ID
`app.turquoise6409.green2444`, 964677 B, Cache-Control no-store,
SHA-256 `5708765040138869f0bd6a9f32bf6d510bda06d671e5f62eb32690d16daca4d7`.
Zdroj publikované aplikace je uvedené main 0d97dea; následné předání
mění pouze dokumentaci. Backend kód se neměnil, běží nasazení D116.
Backend veřejné healthz vrací 200; NDJSON startup shoda prošla read-only
kontrolou. Nový placený AI test se nespouští. Přesnou příčinu na telefonu
potvrdí až nová IPA a opakovaný start; B1/fyzické pohony se nemění.
Další krok: majitel aktualizuje na 1000.0.5 a ověří první repliku,
automatický poslech po více odpovědích a Stop/pozadí. Pokud start dál
selže, předá konkrétní technický kód z nové hlášky. Tato fyzická kontrola
ani skutečný iOS notification callback nejsou doložené simulátorem.

**D116 – nasazeno: průběžný poslech bez tlačítek a oprava dalších odpovědí.**
Majitel po instalaci D114 hlásí druhou odpověď bez výsledku: po dokončení
nahrávání se pouze deaktivuje tlačítko. Žádá skutečný průběžný rozhovor.
Backend drží jedno provider SDK spojení, VAD rozpozná konec řeči a každý
nový tah rezervuje před příjmem audia. Telefon používá jeden NDJSON stream,
krátké pořadové PCM POSTy a automaticky obnoví mikrofon po dohrání.
Pozdní vstupní blok se zahodí, nezačne nový tah ani nezastaví rozhovor.
Stop funguje i během čekání na dohrání, chyby/cost_limit jsou viditelné.
Rozpočet zůstává: kratší rezervace/context mohou zapadnout do stávajícího
capu; podrobný prompt a uložená paměť se nemažou. PTT/řetězená záloha zůstává.
Rozsah: Features/Realtime, ConversationStore/RealtimeTurns a backend testy;
DebugHandsFreeSession/Audio, plugin/klient/DebugPanel, Swift harness a Xcode;
OpenAPI/verifier, README/plán a omezený živý Python test/workflow.
Lokálně prošly backend testy úvodu a dvou hlasových odpovědí na jednom
socketu, pořadí vstupních bloků, dohrání a nízký cap/Stop. TypeScript/Vite
build, 19 mobilních testů, všechny kontrakty a 12 Python klientských testů
prošly. Finální HTTP smoke také prošel. Read-only review odhalil a opravil
závod pozdního audio bloku při VAD stop/novém poslechu; koncové bloky se
zahazují i při druhém zavření vstupu. In-app Browser ověřil výchozí režim,
dvě navazující syntetické odpovědi bez nahrávacího tlačítka, viditelný
cost_limit, Stop a opětovné spuštění. macOS Swift/Xcode ověří povinné CI.
Implementační commit `292b677ab9e210f69bd12e279c7a521716480d36` a integrace
main `5352d4d15a1be15372ce292ba1d8628ea6dde27f` byly ověřeně pushnuté.
[PR #82](https://github.com/Blato58/DildoVander/pull/82) zachovává D115 opravu
safe area z PR #81. První CI 36905453842 prošlo backend testy, web, kontrakty
a offline eval; Swift odhalil actor izolaci čistého parseru chyb, opraveno
explicitním nonisolated. Linux HTTP negativní test narazil na EPIPE při
znovupoužití spojení po nadlimitním těle; tento požadavek nyní spojení zavře.
Finální CI 36906081419 prošlo všechny povinné kontroly včetně nativního
hands-free harness (úvod/dvě odpovědi, VAD gate, časný ACK a cleanup),
Xcode build/spuštění iOS Simulatoru a obou firmware profilů. PR #82 je
sloučen do main `7581623ef8f843d00244684110a8ce07f2d3ff9c`; shoda tree
se schváleným CI head `c28e4fcbf5ca1f1661e3922748a634400b9ae6d5` ověřena.
Deploy 36907319409, Ad Hoc podpis 36907325300 a veřejná publikace
36907700654 prošly. [Safari instalace](https://2.28.202.220/ios/) nabízí
1.0 / build **1000.0.4**, včetně D115 safe area. Veřejný manifest a IPA
jsou ověřené: Bundle ID `app.turquoise6409.green2444`, 960750 B, no-store,
SHA-256 `35e2c5b695e8e9036e20b0710222710596ca3fe74d3fbd533f44d212096e7c0e`.
Autorizovaná živá zkouška 36907705482 prošla jedno kontinuální spojení,
úvod a čtyři české syntetické hlasové odpovědi až k validovanému návrhu.
První audio po server VAD stop: 739/697/667 ms pro běžné otázky, 1533 ms
pro konečný výběr; úvod od HTTP startu 2764 ms. VAD před tím čeká 650 ms
ticha. Generování běžných tahů celkem 1594–2012 ms; skutečné dohrání
na iPhonu tento syntetický test nesimuluje. Report uvádí konzervativní
náklad **0,198108 USD**, session cap 0,50 USD; schválený strop 1 USD
nebyl překročen. Jde o odhad backendu, nikoli účetní výpis OpenAI.
Sezení bylo ukončené a předchozí večer obnovený, uživatelská paměť se nemaže.
Další konkrétní krok: majitel aktualizuje iPhone na 1000.0.4 a ověří mikrofon,
automatický návrat k poslechu po několika odpovědích, Stop/pozadí a safe area.
Syntetická zkouška obsahuje nepřesnosti STT; skutečnou češtinu, poslech
humoru a audio route potvrdí telefon. B1/fyzické pohony zůstávají blokované.

**D115 – oprava horních tabů pod výřezem iPhonu.**
Majitel hlásí překrytí tabů kamerou/senzory a obtížné klepnutí.
`index.html` zapíná `viewport-fit=cover`; `src/style.css` přidává
systémový `safe-area-inset-top` do paddingu sticky lišty a respektuje
levý/pravý inset v landscape. Horní inset zůstává součástí lišty i při
scrollování; nulový inset zachovává webové odsazení a stávající výšku tlačítek.
Mobilní README popisuje chování. Kontrakty, backend a firmware se nemění.
Kontrola zdrojů ověřila společnou lištu obou tabů, sticky top 0, viewport
a zachování minimálních okrajů; whitespace a odkazy změny jsou v pořádku.
Zdejší prostředí poskytuje GitHub API bez lokálního shellu/PowerShellu;
změna je předána přes Git Data API na větvi `fix/ios-tabs-safe-area`.
Webové testy/build a Swift/Xcode/simulator ověřuje povinné CI tohoto PR;
aktuální výsledek a merge jsou evidované v PR. Nativní vizuální/tap kontrola
na iPhonu, scroll a obě landscape orientace zde nebyly provedeny.
Podpis a publikace nové IPA nejsou součástí tohoto kroku.
Další krok: po zeleném CI a merge připravit novou IPA v autorizovaném
publikačním kroku a ověřit Ovládání/Debug na iPhonu včetně scrollu/otočení.

**D114 – nasazeno: Realtime hlas přes oficiální SDK a podrobný party prompt.**
Majitel doložil 9,8 s dialog + 2,3 s TTS a výslovně schválil Realtime pro
hlas místo dosavadní Sol-only hlasové cesty. Textový Sol zůstává. Požaduje
podrobný popis humoru/jazyka; prompt se nezkracuje kvůli latenci.
Backend používá OpenAI .NET SDK 2.14.0 a `gpt-realtime-1.5`, vlastní provider
WebSocket; iOS přijímá autentizovaný HTTPS NDJSON a přehrává PCM průběžně.
PTT/poloduplex, explicitní VAD null, model slyší audio přímo; souběžný přepis
slouží trvalé paměti. Funkce vybírá až po minimálně čtyřech otázkách, backend
kontroluje masku/korelaci/expiraci. Debug návrh nikdy nespouští pohon.
SDK stream i vstup/výstup jsou omezené, deadline 45 s. Stop/disconnect ruší
provider; journal brání placenému replay a sdílí existující limity/slot.
Prompt popisuje obecnou češtinu, konkrétní narážky, osobní otázky, callbacky,
tempo a příklady dobré/slabé repliky. Nový hlas je výchozí AI tok Debugu;
fake a kompatibilní STT/Sol/TTS jsou dostupné. UI měří první audio i celý tah.
Soubory: backend Features/Realtime, ConversationRealtimeTurns a zapojení,
NuGet reference/lockfiles, RealtimeTests a HTTP smoke; OpenAPI/verifier;
DebugRealtimeSession, DebugBackendClient, KufrDebugPlugin, DebugPanel,
nativní harness/Xcode projekt; prompt a plán/README/stav.
Lokálně prošly kompletní backend testy v Release včetně SDK serializace,
audia před response.done, tool/mask/otázkové brány, resamplingu, journalu,
nízkého capu, chyby paměťového zápisu a Stop. HTTP smoke a všechny kontrakty
prošly; mobilních 19 testů, TypeScript/Vite build a 20 Python eval testů také.
Locked restore linux-x64/self-contained s explicitním RuntimeIdentifier
prošel. In-app Browser ověřil
Realtime jako výchozí, kompatibilní hlasy, ovládání a 390px rozložení bez
horizontálního přesahu/console chyb. Swift/Xcode na Windows není dostupné;
nový parser/rušení/cleanup harness a iOS sestavení ověřuje povinné macOS CI.
Implementační commit `10ce2af240531bc6a3faaa8d735e973c74499104` byl pushnut
přes Push-CurrentBranch.ps1, přesná remote shoda ověřena.
[PR #80](https://github.com/Blato58/DildoVander/pull/80) obsahuje povinné
kontroly backendu, webu, Swift harness a Xcode/simulátoru; aktuální výsledek
kontrol a sloučení je v PR. Tento záznam se nepoužívá jako HW/iPhone akceptace.
První CI 36886840203 odhalilo hostitelské win-x64 v lockfiles a chybějící
Realtime source v původním Swift test buildu. Lockfiles regenerované přes
NuGet s explicitním linux-x64 RuntimeIdentifier; oba Swift harness buildy
nyní zahrnují DebugRealtimeSession. macOS ověření pokračuje v PR.
Linux backend v CI 36887687842 kompletně prošel. Další macOS compile odhalil
fileprivate u sdíleného cleanup; metoda je nově internal pro oba nativní
klienty, bez změny síťového kontraktu nebo chování mazání.
CI 36888412422 prošlo oba nativní harness včetně Realtime. Xcode ještě
vyžaduje explicitní MainActor u failDialog/stopWork; notification callbacks
nově přecházejí přes hlavní DispatchQueue, ostatní callsites již běží na ní
nebo v MainActor Task. Kompletní iOS build/spuštění se dál ověřují v PR.
Read-only review opravil immutable model, 32KiB audio bloky, playback drain,
cleanup retry, souběh streamu a pořadí paměť/journal. Oddělené SQLite soubory
nejsou společná atomická transakce: chyba journalu po úspěšné paměti může
ponechat výměnu v paměti při ohlášeném neúspěchu. Viz backend README.
PR #80 byl sloučen do main `3be859cacac0345799ff003e1dfc78c12b295295`;
finální CI 36889244970 prošlo včetně Swift/Xcode/simulátoru. Autorizované
nasazení backendu 36892004497, podpis 36892009408 a veřejná publikace
36892712624 prošly. Veřejná `/ios/` nabízí build 1000.0.3; majitel jej
nainstaloval. Placená evaluace zatím neproběhla, schválený strop je 1 USD.
Skutečná latence/humor nejsou doložené offline testy. Hlásená iPhone
chyba a automatický poslech pokračují v D116; B1 trvá.

**Předchozí nasazení 1. 10.: D112, IPA 1.0 / build 1000.0.2 publikovaná.**
Debug nabízí pojmenovaný nový večer i návrat ke staré paměti a ukládá profil
do odděleného Keychain. Výchozí URL/ID jsou `https://2.28.202.220/` /
`debug-server-admin`. V nové verzi credential jednou znovu uložit: starší
RAM-only verze ho neměla na disku. Public [instalace](https://2.28.202.220/ios/)
a skutečná IPA ověřeny; původní serverové eveningId i dvě výměny zachovány.
Společný výsledek zahrnuje D111 CI a D113 závislosti. PR #78/#79 sloučené,
všechny push ověřeny; CI, deploy, podpis i publikace prošly. Další konkrétní
krok: majitel aktualizuje registrovaný iPhone, uloží Debug profil a ověří
jeho zachování při pozadí/relaunch/další aktualizaci, reálný hlas a večery.
Bez telefonu nelze tuto akceptaci doložit; fyzický runtime dál blokuje B1.

**D113 – oprava dependency PR #77, web a macOS ověřeny.**
TypeScript 7 odstranil staré compiler API a zpřísnil side-effect importy.
Testovací loadery nyní používají Node transform, `src/vite-env.d.ts` dodává
Vite deklarace pro CSS. `cap sync ios` vygeneroval Swift manifest 8.5.2.
Novou zranitelnou závislost CLI `xcode -> uuid 7` nahrazuje cílené override
na CommonJS kompatibilní uuid 11.1.1; npm lockfile vznikl přes npm.
Soubory: mobilní package/lock, dva testovací loadery, Vite deklarace,
generovaný Swift manifest/lockfile a mobilní README; původní PR aktualizuje i `ws`.
Lokálně: npm ci, 19 mobilních testů, TS7/Vite8 build, všechny kontrakty,
MSVC C++14 bridge build a kompletní simulator npm test včetně TLS/WSS/media
prošly. xcode parse/write a generování UUID prošly, npm audit má 0 nálezů.
Lokální runtime Node 25.9.0; všech 19 testů prošlo také na přesném 24.21.0.
In-app Browser nad Vite8 produkčním buildem ověřil Ovládání/Debug,
převzetí, potvrzení výběru a Stop bez console chyb.
Commit `efe1cd51a726a97350be498d06868d3b294555a3` byl pushnut přes
Push-CurrentBranch.ps1 a remote shoda ověřena. V CI 36869848988 prošel web,
nativní Swift harness, Xcode build i spuštění simulátoru. Package.resolved
8.5.2 pochází z artefaktu `kufr-swift-package-resolved` tohoto macOS běhu.
CI 36870350469 celého commitu `35a150e254d575c1d839c24ec8b5bc94749994dd`
prošlo včetně firmware, backendu a iOS; Swift lockfile je shodný s resolverem.
Souběžně se sloučil PR #78. Jeho kód a původní D112 jsou zachované; konflikt
byl pouze mezi novými záznamy stavu, dependency oprava proto používá D113.
Další krok: ověřit CI společného výsledku s PR #78 a merge PR #77.
Žádné nasazení, podpis IPA ani HW akceptace.

**D112 – správa večerů a trvalý Debug přístup nasazeny.**
Zadání majitele: vytvořit pojmenovaný večer, přepínat aktivní večer bez ztráty
starých rozhovorů a zachovat credential při aktualizaci aplikace. Výchozí
URL `https://2.28.202.220/`, potvrzená identita `debug-server-admin`.
Implementace používá samostatný nesynchronizovaný iOS Keychain; JS ani log
credential nečtou. Prázdné pole může zachovat uložený credential jen pro
stejný server a identitu. Explicitní zapomenutí maže jen Debug profil.
Backend migruje stávající paměť do katalogu večerů s aktivním ukazatelem,
device izolací, CAS a trvalou idempotencí; změny jsou odmítnuté během sezení
nebo doznívajícího provideru. Native klient po mutaci obnovuje skutečný stav.
Lokálně prošel .NET build/test (0 warnings/errors), HTTP smoke, všechny
kontrakty, mobilních 19 testů a TypeScript/Vite build. In-app Browser ověřil
nový večer, návrat k 42 původním výměnám, souběžný konflikt/obnovu, blokování
během rozhovoru, zapomenutí profilu, bezpečný text HTML názvu a log bez názvu.
Rozložení 390/1280 px bez horizontálního přesahu a console chyb.
Read-only review opravil busy guard podle vlastníka PaidCall (i po expiraci
journalu), omezení idempotence 24 h/1024 a native limit 256 KiB pro 100×80
Unicode názvů. Regresní testy pokrývají migraci obsahu/počtů, restart replay,
CAS, izolaci archivů/zařízení, late append, legacy clear a limity.
Swift testy nově ověřují skutečný Keychain mezi procesy a aktualizaci/smazání
testovacího profilu, bezpečné ponechání credentialu a evening retry/GET.
Na Windows nejsou Swift/Xcode; macOS CI ověřilo nový harness i Xcode/simulator.
Změněné soubory: konverzační úložiště/endpoints/PaidCall a backend testy/smoke,
OpenAPI/verifier, DebugBackendClient/KufrDebugPlugin, DebugPanel/Journal/style,
native Swift a UI harness, jejich skript a plán/README/stav.
První commit `abf94d65af815d3b8bd4d83a563e1b0371b751ad` push ověřen;
[PR #78](https://github.com/Blato58/DildoVander/pull/78). Souběžná main změna
CI/CD (D111, PR #75/#76) zachována merge `83c7206`; konflikt pouze ve stavu
vyřešen zachováním obou záznamů. Backend runtime/HTTP smoke nad společným
výsledkem znovu prošel; lokální restore změnu RID lockfiles nezahrnuje.
Nový podpisový workflow má vlastní counter, proto používá explicitní build
`1000.x.y` vyšší než starý 37; Bundle ID/tým zůstávají stejné. Build skript
validuje čtyři/dva/dva číselné komponenty. Finální build je `1000.0.2`;
první podpisový běh skončil ve verifieru, a proto spotřeboval counter 1.
Aktualizace na fyzickém iPhonu a Keychain po ní vyžadují akceptaci majitele.
Head `3da0cb39562aeb06afa7f1335f4807f9a06ce92e` prošel kompletní
[CI 36869900104](https://github.com/Blato58/DildoVander/actions/runs/36869900104),
včetně Swift Keychain/večerů, Xcode/simulatoru a firmware. PR #78 sloučen
do `1e20b54a2a7ba57d37b93fc09d38ac2d826e7407`; scoped fetch/fast-forward ověřen.
Deploy `36871071929` a signing `36871077770` skončily před produkcí chybou
403 při `GET /pulls/78`: nový promotion verifier neměl `pull-requests: read`.
Backend ani IPA se nezměnily. Oprava přidává pouze read permission do callee
i tří caller workflows a test tohoto skutečného permission kontraktu.
Read-only review opravy bez nálezů; všech 29 promotion testů prošlo.
PR #77 mezitím přidal TS7/Vite8/Capacitor 8.5.2: změny zachované merge
`9324c85123f84f2bf6b49605643c71586c67cdf6`, konflikt pouze v záznamu stavu.
Na přesném Node 24.21.0 znovu npm ci (0 audit nálezů), 19 testů, TS7/Vite8
build a browser create/switch večerů bez console chyb; shell syntax a lokální
Markdown odkazy prošly. Head `9324c85` prošel kompletní
[CI 36872465088](https://github.com/Blato58/DildoVander/actions/runs/36872465088).
[PR #79](https://github.com/Blato58/DildoVander/pull/79) sloučen do
`f859231b16376ea34d6a2674eabcbefa85d7171d`, scoped fetch/fast-forward ověřen.
Z tohoto tree byly promovány ověřené backend/web artefakty:
[deploy 36873170903](https://github.com/Blato58/DildoVander/actions/runs/36873170903),
[Ad Hoc podpis 36873176086](https://github.com/Blato58/DildoVander/actions/runs/36873176086)
a [publikace 36873500918](https://github.com/Blato58/DildoVander/actions/runs/36873500918)
prošly včetně veřejného HTTPS ověření. Public GET evening-memory/evenings
potvrdil původní ID `c169fe72-78c3-4b42-9bc0-74b78c380eb4`, dvě výměny,
0 vytěsnění a jeden pojmenovaný původní večer. Produkční večery neměněny.
Dialog `responses` / hlas `openai` zachovány, žádné placené provider volání.
Public IPA 887639 B, v1.0/build1000.0.2, manifest release `36873500918-1`,
SHA-256 `0904b6a09a1bc1bf6a1ba4d4f1b14abd80037e96db79031cf85cb9f6e5ec84f1`.
Skutečný ZIP/Info.plist/native binary/web bundle potvrzují stejný Bundle ID/tým,
Keychain a všechny nové evening/forget metody; UI harness není v IPA.
Page/IPA no-store, checksum shodný; in-app Browser ukazuje sestavení1000.0.2
a správný neměnný Safari manifest, bez console chyb. Akceptace aktualizace
na fyzickém iPhonu, lock/unlock a skutečné persistence zůstává u majitele.

**D111 – CI/CD build jednou a oddělená promotion artefaktů.**
Rozsah: každé PR do main spouští společné CI pro backend, firmware, iOS/web
a offline AI; stabilní `CI required`, zrušení zastaralých PR běhů, timeouty.
Backend Release/RID build sdílí testy i self-contained publish přes no-build,
NuGet lockfiles/cache; web artefakt přebírá macOS i podpisový workflow.
Ruční backend deploy a samostatná IPA publikace nepřekládají. API ověřuje
úspěšný workflow, původ PR/merge stromu a shodu celého tree s aktuálním main;
checksum a bezpečné rozbalení před self-hosted runnerem. Sign IPA je explicitní
samostatný device build (simulator nelze nahradit), nikoli součást deploy.
Firmware toolchain cache a verzované binární výstupy pro oba profily.
Soubory: `.github/workflows/`, `scripts/ci/`, NuGet props/lockfiles,
smoke výběr publikované DLL, CONTRIBUTING/SECURITY/CODEOWNERS/PR šablona,
Dependabot, EditorConfig/Git attributes a `docs/ci-cd.md` + komponentové README.
Lokální ověření: actionlint 1.7.12 všech workflows, 28 Node promotion testů,
6 Python archive testů, .NET 10.0.303 locked restore/Release build bez varování,
backend runtime testy, no-build publish a HTTP smoke nad publikovanou DLL,
skutečný balíček/rozbalení self-contained outputu; 19 mobilních testů a Vite/TS
build, všechny kontrakty, 3 deploy + 9 client + 4 IPA publish + 20 offline AI
Python testů; shell syntax, lokální dokumentační odkazy a whitespace prošly.
Commit `332fd4d9c8e12e4f53743042f37c4a79d30b1735` byl předán přes Git Data API
a scoped fetch potvrdil remote shodu. HTTPS git push vracel 401; zdejší Linux
nemá spustitelný PowerShell push skript. CI výsledky a merge jsou evidované
v [PR #75](https://github.com/Blato58/DildoVander/pull/75); při převzetí ověř
jeho aktuální stav a Actions.
Skutečné nasazení, Apple podpis, publikace IPA, placená AI a HW flash nejsou
součástí tohoto kroku. Repo nastavení API vrátilo 403 integration permission,
rulesets navíc vyžadují Pro pro privátní repo. Připravené konkrétní JSON +
admin apply skript nejsou důkazem aktivní ochrany větve.
Další krok: majitel/admin zpřístupní repo administraci a podporovaný tarif,
potom aplikuje připravená pravidla; nasazení použije run ID úspěšného CI.

**D110 – interaktivní hlasový Debug nasazen, podepsaná IPA build 37 publikovaná.**
Majitel žádá menší prodlevu, volbu hlasu a celý výběr s vlastními odpověďmi;
výslovně potvrdil hlas i psaný text. Implementována virtuální maska 0–63,
celý přechodný přepis a validovaný návrh bez pohybových povelů, volby hlasu
cedar/marin/onyx/coral a tempa, nativní dvoukrokové nahrávání do RAM s 30s
limitem a STT uploadem. Stop/pozadí/interruption ruší nahrávku i vlastní
sezení. Fake admin dialog projde čtyřmi otázkami a vybere dostupnou přihrádku;
fake STT zůstává syntetický. Native příjem WAV je omezený po blocích mimo
MainActor, přehrávání předchází audio cleanup RTT. Měří se dialog/TTS/stažení;
nejde o streaming před dokončením provideru ani důkaz rychlosti na iPhonu.
Změněné soubory: speech/provider/konverzační backend a testy/HTTP smoke,
OpenAPI/verifier, DebugBackendClient.swift/KufrDebugPlugin.swift/Info.plist,
DebugPanel.ts/style.css/native UI harness a plán/README/stav.
Backend runtime testy, HTTP smoke a všechny kontrakty prošly bez provider
volání. Mobilní testy (19) a TypeScript/Vite build prošly. In-app Browser
prošel celým fake výběrem s textem a syntetickým mikrofonem, volbou hlasu/tempa,
vypnutou přihrádkou, návrhem, mazáním přepisu a samostatným log filtrem;
uživatelský text včetně HTML se vykreslí jako text a není v logu.
Rozložení 390/1280 px bez horizontálního přesahu/console chyb.
Read-only review vedlo k opravě souběžného cleanup, stejného klíče při release
retry, expirace návrhu po TTS, okamžitého mazání JS obsahu při pozadí a
odmítnutí starých status/mikrofonních callbacků. Swift harness pokrývá celý
výběr/STT/korelaci/expiraci a skutečný omezený loopback chunk/redirect/cancel;
Node fixture lokálně ověřena. Dvě chyby testovacích fixture (binární WAV jako
UTF-8 a 200 místo upload 202) opraveny při zachování striktního klienta.
UI skrývá prázdné měření před prvním tahem. Diff bez whitespace chyb,
tajemství nepřidána. Poslední head `e72316f3f8b5b25627d40303a01fb400fb2af7c6`
prošel [backend CI 36863902681](https://github.com/Blato58/DildoVander/actions/runs/36863902681),
[iOS CI 36863902326](https://github.com/Blato58/DildoVander/actions/runs/36863902326)
a [firmware CI 36863902366](https://github.com/Blato58/DildoVander/actions/runs/36863902366).
macOS ověřilo Swift/loopback harness, Xcode build a spuštění simulátoru;
firmware Arduino/ESP-IDF a host testy prošly. Všechny push ověřeny,
[PR #74](https://github.com/Blato58/DildoVander/pull/74) sloučen 1. 10. do
`09edc403368cbf297bf3c50f9f32371ccdb91386`, merge ověřen scoped fetch/fast-forward.
[Deploy 36864816231](https://github.com/Blato58/DildoVander/actions/runs/36864816231)
a [Ad Hoc 36864820809](https://github.com/Blato58/DildoVander/actions/runs/36864820809)
spuštěny z téhož merge. Deploy prošel loopback health a veřejným admin/no-op
smoke bez provider volání. Nezávislé lokální HTTPS čtení potvrdilo
`responses/openai`, oba providery dostupné, 0 aktivních sezení,
`until_restart`, správný katalog a 32k PCM16 WAV. Credential zůstává
stejný, chráněný lokální soubor ani backend secret se neměnily.
Ad Hoc prošel všemi joby: web/Swift/Xcode/simulátor, archive/export/podpis,
publikace a externí ověření IPA/manifestu/checksum. `/ios/` v in-app Browser
i lokálním HTTPS nabízí verzi 1.0/**build 37**, release `36864820809-1`,
stejné Bundle ID `app.turquoise6409.green2444`, HTTP `no-store`.
Nezávislé stažení ověřilo MIME a SHA-256
`1b25e1077ecc55ca5768fd4d8ce1756dc0eec8109ef8a7d03737a5874c2b60c6`.
Inspekce skutečné veřejné IPA potvrdila build/Bundle ID, microphone usage
permission, všechny čtyři nové nativní metody i interaktivní JS/Cedar;
testovací native-ui-harness není v balíčku. Starší build 30 zůstává verzovaný
pro návrat, hlavní stránka odkazuje na build 37. Žádné tajemství nebylo měněno
ani přidáno do aplikace. Záznam předání je samostatný dokumentační commit.
Další krok: majitel v Safari aktualizuje build 37, znovu nastaví nativní
`debug-server-admin` přístup a obnoví režimy. V nové sekci zkušebního rozhovoru
ověří vlastní text/mikrofon, český Cedar/Marin/Onyx/Coral a čas dialogu/TTS/
stažení na skutečném iPhonu. Server zůstává `responses/openai`; spuštění
AI rozhovoru může čerpat limit. Placené provider volání z agentních testů
neproběhlo; kvalita/latence a permission skutečného telefonu se ještě
nepotvrdily. Fyzický ESP/I2S zůstává závislý na B1, firmware se nenahrával.

**D109 – serverové režimy nasazené a aktuální IPA publikovaná.**
Majitel žádá ovládání serveru v Debug a opravu staré aplikace na `/ios/`.
Veřejná stránka nabízela release `36636991415-1`, verzi 1.0/build 10; HTTP
`no-store` je funkční, novější unsigned CI samo nepodepisuje/nepublikuje IPA.
Rozsah: samostatné admin hash oprávnění a oddělená `debug-server-admin` identita,
`GET/PUT /v1/admin/modes`, dialog fake/Sol a hlas fake/STT-TTS, revize a odmítnutí
změny za živého sezení/doznívajícího provideru, runtime platnost do restartu.
Native profil drží credential jen v RAM, změna nemá místní řídicí povely.
Fake test ověřuje oba režimy také po create a při neshodě uklidí vlastní sezení
bez opening/provideru. Kontrakt a README rozšířeny, serverový workflow odvozuje
hash ze samostatného environment secretu a veřejný smoke pouze čte/no-op,
bez přepínání produkčních režimů nebo provider volání. Credential správce je
provisionovaný mimo repo v `backend-production`, lokální kopie má ACL vlastníka.
Změněné soubory: backend `ServerAdminAccess.cs`, `DeviceAccessStore.cs`,
`ConversationServerModes.cs`, `ServerModeEndpoints.cs`, oba provider runnery,
`Program.cs`, backend/unit/HTTP/public smoke testy, `kufr-backend.yml`, backend
OpenAPI a jeho verifier, nativní `DebugBackendClient.swift`/`KufrDebugPlugin.swift`,
`DebugPanel.ts`/`DebugJournal.ts`, native harness/Swift testy a plán/README/stav.
Lokálně: .NET build bez varování, backend runtime testy včetně správy/auth,
HTTP smoke s 401/no-op/revision/capability testy, 19 mobilních testů a Vite/TS
build prošly. Python deploy a iOS publikační testy prošly. Nové Swift testy a
Xcode prošly na macOS CI. Kontrakty prošly; in-app Browser ověřil fake/AI
přepnutí přes syntetický bridge, gating hlasu, konflikt/obnovení, samostatný
filtr logu a rozložení 390/1280 px bez horizontálního přesahu a console chyb.
Nejde o skutečný server ani iPhone. Roslyn test impact kvůli chybějící
solution vrací `ambiguous_solution`; cílené příkazy vycházejí ze skutečných
projektů.
Předání: commit `d6de8cb154ed8609e124258d1f477006c5b2bf36` pushnut a ověřen,
[PR #73](https://github.com/Blato58/DildoVander/pull/73) sloučen po zeleném
[backend CI 36856939562](https://github.com/Blato58/DildoVander/actions/runs/36856939562),
[iOS CI 36856939581](https://github.com/Blato58/DildoVander/actions/runs/36856939581)
a [firmware CI 36856939621](https://github.com/Blato58/DildoVander/actions/runs/36856939621).
Merge `7dfa10bbf7d556e71172838a8463fc68c328c8d1` ověřen scoped fetch/fast-forward.
[Deploy 36857643096](https://github.com/Blato58/DildoVander/actions/runs/36857643096)
aktivoval právě tento commit s `dialog_mode=responses`, `voice_mode=openai`;
loopback health a veřejný admin/no-op/revision/diagnostics smoke prošly.
Lokální nezávislé HTTPS GET potvrdilo `responses/openai`, oba providery
konfigurované, 0 aktivních rozhovorů, `until_restart`, 401 bez credentialu
a `Cache-Control: no-store`. Neproběhlo žádné skutečné provider volání.
[Ad Hoc běh 36857646275](https://github.com/Blato58/DildoVander/actions/runs/36857646275)
ze stejného commitu prošel web/Swift/Xcode/simulator, archive/export/podpis,
publikací a nezávislým veřejným stažením IPA/checksum/MIME. `/ios/` a manifest
ověřeny také lokálně a v in-app Browser: verze 1.0/**build 30**, release
`36857646275-1`, stejné Bundle ID `app.turquoise6409.green2444`.
SHA-256 IPA: `80e213095971c0c9823f0e3d2ded49890e3e82eaa4cc5363951ef73d3aa72e61`.
Starý build 10 zůstává ve verzovaném adresáři pro návrat; kořenová stránka už
odkazuje na build 30. Není potřeba mazat aplikaci ani měnit podpisovou identitu.
Další krok: majitel na registrovaném iPhonu v Safari aktualizuje build 30,
v Debug nastaví HTTPS backend, `debug-server-admin` a svůj chráněný credential,
ověří režimy a hlas na telefonu. Tato skutečná iPhone/AI/audio akceptace ještě
neproběhla; konfigurace a syntetické testy ji nenahrazují. Fyzický ESP/I2S/GPIO
runtime nadále čeká na B1, neprovádělo se nahrání do desky.

**D108 – iOS Debug sloučen do main po zeleném CI.**
Záložka nabízí pozorování přijatých ESP snapshotů, omezený redigovaný log a
nativní hlasový test na telefonu. Diagnostický backend test je oddělen od
místního ovládání a neuděluje lease ani neposílá pohybové povely. Lokální testy
a syntetické UI nejsou iPhone/ESP akceptace. B1 neblokuje tuto softwarovou
přípravu; nový backend ani IPA se tímto zadáním nenasazují. [PR #72](https://github.com/Blato58/DildoVander/pull/72)
má head `1f028be5ba8f975dfbde706a27168f0ff2f846cf`, merge
`ae4ffa7c436abf74b5e356a529e562a89cc71557`; push a merge na `main` byly ověřeny.
Pro živý AI test
zbývá nasadit D106/D108, provisionovat samostatnou `debug-*` identitu a explicitně
zapnout hlasový provider s limity. Další krok: samostatné autorizované nasazení
a nová IPA/iPhone zkouška, případně pokračování nezávislého STT/audio softwaru.
Fyzická ESP/I2S akceptace nadále vyžaduje B1.

**D106/D107 – AI hlas připraven v backendu a samostatné firmware softwarové cestě.** Majitel
upřesnil, že kufr musí mít připravené mluvení. Závěr D105 byl příliš široký:
B1 blokuje fyzický port/akceptaci, nikoli backendové STT/TTS adaptéry a hostitelské
audio ověření. Dokončený rozsah: explicitně zapínané provider adaptéry za existujícími
audio API, asynchronní polling/cancel/idempotence, nákladová rezervace, normalizace
PCM16 mono 32 kHz a testy bez skutečného OpenAI volání. Podrobné oddělení je v §7 plánu.
D106 je po zeleném [CI 36842054906](https://github.com/Blato58/DildoVander/actions/runs/36842054906)
a [PR #70](https://github.com/Blato58/DildoVander/pull/70) v `main`:
commit `9863b20caaaf4d72d286c2cf8c07b03d0af4830f`, merge `1f172a4ff0546dc86a380ca129de73c8adce1cbb`;
push byl ověřen a cílová větev načtena scoped fetch/fast-forward. Server se nenasazoval.
D107 přidává striktní speech operaci, bezpečný caller buffer a HTTPS adaptér
s hostitelsky ověřeným řetězcem validovaná replika → speech → poll → WAV → release.
D107 je po zeleném [CI 36845545981](https://github.com/Blato58/DildoVander/actions/runs/36845545981)
a [PR #71](https://github.com/Blato58/DildoVander/pull/71) v `main`:
head `bc48fb3514c5095214affa95a736d06e73a607a4`, merge `430af8d48b174677fe9aae4ecb340fd4effe5857`.
Host testy i ESP-IDF/Arduino build prošly; merge byl ověřen scoped fetch/fast-forward.
Není zapojen do současného textového runtime ani I2S; skutečný capture/playback,
consent vstup a paměť vyžadují B1. Samostatný firmware STT upload a mobilní audio
zůstávají dalšími softwarovými částmi E9/E10, nevydávat celý projekt za blokovaný B1.

**Historická kontrola D105, opravená D106:** majitel znovu potvrdil, že podklady B1 nemá. Původní závěr o úplné blokaci byl příliš široký; firmware README bylo opraveno podle již složeného WSS runtime, nezávislá hlasová příprava ale může pokračovat.

**Předání 1. 10.: nezávislé E10.1–3 a E4.4t dokončené v softwarovém rozsahu, další krok B1.** Nativní transport/TLS/Keychain ([PR #65](https://github.com/Blato58/DildoVander/pull/65), merge `5f54104b783c8cf2fc7e1885276322d535681f7b`), text/cancel/result ([PR #66](https://github.com/Blato58/DildoVander/pull/66), merge `ed7ea0212a9f345f78da32caf51abf6e1a57dc9c`) a firmware/nativní Bonjour ([PR #67](https://github.com/Blato58/DildoVander/pull/67), merge `6315857414e2c6030e991f3ddda43aa21ceee28a`) jsou po příslušných zelených CI sloučené a ověřené scoped fetch/fast-forward na `main`. E10 fyzická akceptace, servisní provisioning/QR a audio zůstávají otevřené; firmware nadále odmítá zotavení bez B1.

**D75/D76 ověřeny v softwarovém rozsahu:** nativní AI funkce, trvalá paměť, pouze Sol, skutečný adaptivní rozhovor přes nasazený backend a hostitelský replay jeho návrhu. [Přepis a důkazy](../ai-eval/reports/2026-09-30-live-backend-dialogue.md). Server běží v `responses` s `gpt-6.1-sol`; testovací sezení uzavřena a syntetický večer vymazán. Fyzická integrace ESP32 a samostatný hlas zůstávají otevřené.

**Předchozí obsahový požadavek majitele 30. 9.:** D74 přidává hovorový party dialog, opening a trvalou paměť večera na backendu; syntetické placené srovnání je schváleno do 1 USD. Integrace nových API do firmware/telefonu ani nasazení nejsou součástí dosavadního ověření.

**Rozpracovaná firmware cesta:** E4.3 – samostatný ESP32 klient backendu, současně E8 backendový textový dialog pro testování bez HW. E4.1 ESP-IDF profil a bezpečná smyčka jsou softwarově sestavené (D33); E4.2 má trvalý journal a model identity s NVS adaptéry bez HW/USB akceptace (D34–D35). E4.3a přidává model backend tokenu a HTTPS adaptér (D36), E4.3b servisně čtenou síť, SNTP/časovou bránu a samostatnou úlohu (D37). E4.3c má JSON kodeky, HTTPS adaptéry, obnovu tokenu, korelaci, UUID a časování (D38–D48). D49 připojil interní cestu start/tah/poll; D51 přidal atomický snapshot a vazbu čekajícího textu na jeho verzi. D64 zapojil `DeviceControlRuntime` jako vlastníka snapshotu v IDF smyčce; D77 doplnil hostitelský autonomní cyklus s journalem a okamžitým Stop. D95–D99 připojují autentizovaný místní textový vstup; boot stále vyžaduje fyzické zotavení/kalibraci B1. E7 backend bez skutečné AI je dokončen a běží na Ubuntu přes loopback; autentizovaný fake tah prošel na nasazené službě (D59). E8 backendový textový dialog a jeho živé ověření jsou v D53–D76; fyzické nezávislé vypnutí a měření zůstávají kritérii E5, produkční E8 provoz a fyzická akceptace stále čekají.

**Další konkrétní krok E4/E5:** B1 od majitele/stavitele: revize PCB, flash/PSRAM/USB, ověřené schéma a měření bezpečnostní větve/watchdogu, servisní a inspection vstup a kalibrace jedné přihrádky. D77–D99 hostitelsky ověřují journal/snapshot, autonomní model, autorizaci/párování/TLS/WSS, řídicí router, pravdivý snapshot/ACK a korelované repliky. D99 je skládá do ESP-IDF runtime a odděluje LAN od cloudové úlohy. E4 potřebuje skutečnou TLS/NVS/STA zkoušku; E5 fyzický port. Bez B1 se nesmí doplnit fake inspection do runtime ani nahrávat do desky. Opening/PTT, TTS/I2S a propojení finálního oznámení s kalibrovaným autonomním portem vyžadují další hardware/software E9, jehož závislost E5 není splněná. Native E10 má v §12 závislost E4/E8 a vyžaduje vlastní iPhone zkoušku. Další placené AI testy, deployment a firmware flash nejsou tímto pokračováním spuštěné.

**Navazující provisioning/QR:** §4 nadále platí pro servisní AP i autorizované USB nastavení bez telefonu; nejde o nové rozhodování mezi těmito cestami. Současný `/v1/pair` páruje již provisionovanou STA/TLS identitu, nepřenáší Wi-Fi SSID/heslo a nevytváří AP. Pro skutečný servisní runtime chybí doložený fyzický vstup/odpojená bezpečnostní větev B1; nevytvářet vzdálenou náhradu. Machine QR envelope dosud není v kontraktech: musí následovat konkrétní servisní provisioning (device ID, SPKI, jednorázové tajemství a AP údaje), zůstat nativní a zachovat tajemství mimo JS/logy. Camera scanner ani samotná nová permission nenahradí tuto chybějící cestu. E9/E10 audio je po E5/E9, E11 provozní akceptace po E6/E8/E9/E10. Nejbližší ověřitelný další krok je B1/service/inspection, nikoli další fake fyzický stav.

**E4.3d2b2 hostitelsky ověřeno 30. 9.:** autonomní cyklus prezentace s trvalým journalem, invalidací/publikací snapshotu a bezpečným uzavřením po návratu či Stop. Rozsah je čisté C++ jádro a fake port; fyzický port a místní autorizovaný vstup zůstávají samostatnými navazujícími kroky.

**E4.3d2b3 hostitelsky ověřeno 30. 9.:** omezené předání textové repliky ze síťového pollu budoucímu výstupnímu portu. V tomto slice vznikla pouze paměťová schránka s vazbou na autoritativní snapshot a hostitelský test; I2S/TTS ani fyzický výstup se nepřidávají.

1. Formát a NVS čtení identity jsou v softwaru. Pro skutečný servisní zápis doplnit USB nástroj až s ověřenou fyzickou servisní bránou a odpojenou větví pohonů (B1). Credential musí vzniknout mimo image a zůstat mimo argumenty/logy. Bez známé revize desky nic nenahrávat. Journal ani identita nemají ověřený běh na desce.
2. Po softwarovém Wi-Fi a tokenovém bootstrappingu připojit HTTPS fake backend konverzaci a lokální WSS podle E4. Skutečné TLS spojení na desce a GPIO/USB měření vyžadují B1; E8 provider čeká na B4.
3. Fyzická HW akceptace zůstává v E5 po vstupu B1; E3a podpis a první instalace jsou vyřešené D70/B2/B3, native ovládání/TLS patří neověřenému E10. Nezaměnit fake log s NVS ani fake odpověď se skutečnou AI.

Dotčené cesty: `kufr/firmware/`, související README a tento stav. Dosavadní softwarové slices nepotřebovaly fyzickou desku ani nový cloudový/Apple klíč. Následující akceptace E4/E5 vyžaduje B1 a bezpečně provisionované zařízení. Toto je implementační předání, nikoli příkaz něco nahrát či publikovat.

## Jak se eviduje práce

- Stavy etap: **nezahájeno**, **rozpracováno**, **blokováno**, **hotovo**, **odloženo**. Poslední stav používat pro volitelné etapy nebo výslovné odložení, nikdy jako náhradu nesplněného kritéria.
- U rozpracované etapy zapisuj konkrétní dílčí kroky `E1.1`, `E1.2` atd. Přidej dokončený rozsah i zbytek; procenta bez měřitelného podkladu nepoužívej. Jedna malá změna může dokončit jen část etapy.
- `Hotovo` vyžaduje všechna kritéria kapitoly 12 plánu a odkazy na důkazy níže. Software/fake, build, integrace, iPhone a hardware jsou různé úrovně ověření. Chybějící požadované měření ponechá etapu rozpracovanou nebo blokovanou.
- Blokace musí uvést dotčené kritérium, potřebný vstup, kdo ho dodá a nezávislou práci, která může pokračovat. Při částečné blokaci nezastavuj celý projekt. Vyřešení blokace zapiš s důkazem, ne jen změnou barvy/stavu.
- Na začátku práce porovnej záznam s aktuálními soubory. Při rozporu oprav stav s vysvětlením; nevracej cizí změny podle starého záznamu. Při souběhu uvede každý pracovník svůj krok a vlastněné soubory, jeden integrátor zapíše společný výsledek.
- Před koncem pracovního kroku aktualizuj tabulku, aktuální předání a deník. Zaznamenej i neúspěšný test, rozpracovaný kód nebo neprovedenou kontrolu. Další AI musí poznat přesný bod navázání bez historie chatu.
- Ověření zapisuj jako: datum; krok; soubory/revize; pracovní adresář a přesný příkaz; výsledek; důležité omezení. Pro CI přidej URL běhu a commit, pro zařízení revizi PCB/iOS, profil a kalibraci. Commit uváděj jen pokud skutečně existuje; jinak uveď místní necommitované změny. Logy uváděj stručně a bez tajemství.
- Pokud změna zneplatní dřívější test, zapiš „ověření zastaralo“ a zopakuj relevantní část. Historický úspěch nedokazuje stav nového kódu.
- Architektonické změny patří do plánu, nikoli jen sem. Do deníku patří důvod, odkaz na změněnou část plánu, dopad na kontrakty/etapy a potřebné přetestování. Číslování etap zachovej; nové kroky přidávej, nepřečísluj historii.

## Výchozí stav podle zdrojů

| Část | Co skutečně existuje | Co zatím chybí |
| --- | --- | --- |
| Firmware | ESP-IDF složený runtime D99: jeden STA vlastník, provisionované HTTPS pairing/TLS/WSS, řídicí router, journal/safety snapshot, textový input a korelované backendové repliky. Autonomní model D77 a porty jsou hostitelsky testované. Arduino sériový profil zůstává vývojový. | B1, fyzické service/inspection/kalibrace, GPIO/watchdog, živý ESP32 NVS/TLS/STA a I2S. |
| Mobil | Web fake / C++ simulator UI; Capacitor 8.4.3 a E10.1 Swift SPKI/Keychain/pairing/WSS. Text/cancel/result (D102), NWBrowser discovery (D103), Debug telemetrie/log a izolovaný backendový hlas na telefonu (D108). macOS native testy, Xcode a iOS Simulator prošly. | Čtení QR/servisní provisioning, běžný audio tok po E9, aktivace diagnostického backendu/nová IPA a živý iPhone/LAN acceptance. |
| Backend/kontrakty/simulátor | E7 SQLite/idempotence/retence/kvóty; E8 Responses Sol s trvalou oddělenou pamětí večera, nasazený textový rozhovor D75. D106: explicitní STT/TTS s omezenými asynchronními operacemi a WAV převodem. Strict v1 schéma, C++/WSS simulator, OpenAPI a fake media E2. | Firmware audio tok, živá hlasová evaluace, zařízení a lidská/provozní akceptace. |
| iOS CI | Capacitor web/Xcode/simulator CI; D70 podepsaná Ad Hoc IPA a HTTPS manifest, majitelem potvrzený start na iPhonu 15 Pro. D101 unsigned nativní build/test. | Nová native IPA a její LAN/TLS/Keychain/lifecycle iPhone zkouška; aktualizace/rollback. |
| Hardware | Dodaný obrázek pinů, návrh napájení a předběžné profily A/B. | Revize konkrétní desky, schéma bezpečnostní větve, měření a fyzická akceptace. |

Zdroje: [firmware](../firmware/README.md), [jeho konfigurace](../firmware/platformio.ini), [sekvence](../firmware/src/features/wand_selection/SelectionSequence.cpp), [hostitelský test](../firmware/tests/selection_sequence_test.cpp), [mobilní vstup](../mobilni-aplikace/src/main.ts), [package.json](../mobilni-aplikace/package.json). Tento přehled aktualizuj, až se skutečný stav změní.

## Etapy

Přesné závislosti a kritéria dokončení jsou v kapitole 12 plánu. Následující tabulka je stav, nikoli jejich zjednodušená náhrada.

| Etapa | Stav | Dokončený rozsah / důkaz | Zbytek nebo podmínka dalšího kroku |
| --- | --- | --- | --- |
| E0 – provozní předpoklady | rozpracováno | Architektonické výchozí předpoklady v plánu; žádná měření. | Vstupy B1–B5 níže; software E1 může začít. |
| E1 – bezpečné jádro | hotovo | E1.1: Stop v každé fázi, safety port, 250/500/5000ms limity, explicitní zotavení (D1). E1.2: hostitelský timeout průchodu smyčkou, porucha, výpadek ručního lease a přednost Stop; hostitelské testy a firmware build prošly (D2). | Fyzická reakce nezávislého watchdogu patří E5 a nebyla měřena; softwarový model vypíná až při dalším průchodu smyčkou. |
| E2 – smlouvy a simulátor | hotovo | E2.1–4 kontrakty, C++ model, fake journal, parser a WSS; E2.5 časová osa s browser ověřením (D10,D22); E2.6 OpenAPI, E2.7 reload, E2.8 fake text/cancel, E2.9 fake media parser/socket (D3–D18). Ztracený ACK, duplicitní/pozdní příkaz, restart a zotavení kryjí hostitelské testy a WSS scénáře. | Fake není skutečný audio buffer, NVS ani backend; tyto hranice patří E4/E7/E9. |
| E3 – ruční mobil bez HW | hotovo | E3.1 fake UI (D19), E3.2a ověřený místní most (D20), E3.2b UI adaptér a celý výběr/reload/Stop/odpojení proti C++ simulátoru, cílené testy ztracených zpráv a nového lease (D21). | Jde pouze o webovou/simulátorovou akceptaci E3; nativní iPhone, TLS pinning a fyzický kufr patří E3a/E10/E5. |
| E3a – iOS CI základ | hotovo | Capacitor 8.4.3, iOS projekt/SPM/sdílené scheme; web, Xcode a simulator CI (D50). D70: [Ad Hoc běh 36636991415](https://github.com/Blato58/DildoVander/actions/runs/36636991415) podepsal a ověřil IPA, publikoval ji na vlastním HTTPS serveru s manifestem a externě znovu ověřil; instalační web prošel browser kontrolou na mobilním viewportu. Majitel 30. 9. potvrdil instalaci a spuštění na iPhonu 15 Pro. | Native WSS/Swift bridge, LAN oprávnění, Keychain a aktualizace aplikace patří E10/E11; nejsou doložené tímto startem. |
| E4 – runtime a přímá síť | rozpracováno | E4.1: ESP-IDF 6.1 profil, CMake, bezpečná smyčka, 16MB oddíly a lokální build; Arduino sériový profil zachován (D33). E4.2a: trvalý journal a NVS adaptér v IDF sestavení, hostitelský restart a chyby úložiště (D34). E4.2b: formát identity, softwarová servisní brána a NVS čtení (D35). E4.3a: tokenový model, parser a HTTPS adaptér (D36). E4.3b: NVS síťové nastavení, Wi-Fi STA/SNTP/čas v samostatném tasku (D37). E4.3c1–11: JSON kodeky, HTTPS adaptéry startu/tahu/pollu, guard korelace, UUID generátor a časování pollu (D38–D48). E4.3c12: interní čekající text a síťový fake cyklus (D49). E4.3d1: uzamčený snapshot a invalidace starého textu/výsledku (D51). E4.3d2a: vlastník bezpečných změn snapshotu v IDF smyčce (D64). E4.3d2b1: fronta přijímá jen předem ověřený snapshot (D71). E4.3d2b2: autonomní journal, snapshot a okamžitý Stop ověřeny hostitelsky (D77). E4.3d2b3–5: omezená replika, korelace tahu a mapování místního sezení (D78–D80). E4.4a–g: vlastník lease, přijetí textu, společná generation, místní brána, sdílené boot ID, čistý párovací model a ESP-IDF úložné/kryptografické adaptéry (D81–D87). E4.4h–s: bearer/admission, RNG/TLS, parser/fronta, WSS server/router, pravdivý output/reply, HTTPS párování a runtime s jediným LAN vlastníkem (D88–D99), host testy/build bez HW. | Fyzicky střežený USB provisioning, inspection/kalibrace, živé WSS/NVS/síť a pro nahrání B1; software input/output je složený, fyzická/hlasová prezentace není. |
| E5 – jedna bezpečná přihrádka | nezahájeno | Bez ovládání a měření. | E0/E1/E4; B1, fyzická akceptace. |
| E6 – šest přihrádek | nezahájeno | Bez fyzického ověření. | E5; cykly, proudy, dostupnost/reload. |
| E7 – backend bez skutečné AI | hotovo | E7.1–2 identity/fake dialog; E7.3a–c SQLite idempotence, restart a kvóta; E7.4a–b cleanup/retence a rušení; E7.5a–b WAV fake hlas; E7.6 transakční nákladová rezervace. Lokální HTTP/SQLite testy přístupů, souběhu, restartu, retence a logů prošly (D23–D32). | Skutečné provider ceny/limity/klíče a placená akceptace patří E8/B4; device HTTPS/TLS integrace E4. |
| E8 – skutečný textový dialog | rozpracováno | D53–D54: samostatný Responses eval a party prompt. D57: 60 českých případů a dvě jazykové odbočky. D60–D62: backendový Responses klient, cena a asynchronní napojení za výslovný přepínač a rezervaci. D63: jeden nasazený syntetický placený tah. D66: modelový vícekolový dialog s omezenou RAM historií. D68: tři skutečné placené tahy přes veřejné HTTPS, dvě AI otázky a návrh s navázáním na odpovědi. D72: původní šestičlenný katalog a tematický kufr Ollivanderova obchodu, backend/eval filtrování dle readyMask ověřeno offline. D73: nové téma včetně prvního představení kufru prošlo veřejným tříkolovým testem; server vrácen do fake režimu. | Širší lidské hodnocení, produkční region/retence, zařízení a fyzické potvrzení; B1/B5. |
| E9 – samostatný hlas kufru | rozpracováno | E9.1a/D106: explicitní OpenAI STT/TTS, asynchronní operace, nákladové rezervace, omezené WAV a převod 24→32 kHz. E9.1b/D107: striktní speech JSON, korelovaný WAV buffer, task-only HTTPS a hostitelský speech/poll/download/release cyklus. | Připojení do runtime, firmware STT upload, capture/playback porty, živá česká evaluace; fyzický PTT/I2S a akceptace E4/E5/E8/B1. |
| E10 – dokončení mobilní zálohy | rozpracováno | E10.1: Swift párování, Keychain, SPKI pinning a WSS; sdílený ruční řadič, strict schema a pozorování po připojení. E10.2: device-owned text/cancel/result; E10.3: Bonjour a adresní fallback; D108: Debug/log a samostatný hlasový test telefonu. macOS loopback TLS/WSS/Bonjour/debug, Xcode a iOS Simulator prošly (D101–D108). | Čtení QR/servisní provisioning, běžný audio tok po E9, nová IPA/aktivace debug backendu a skutečná iPhone/LAN/TLS/audio akceptace. |
| E11 – provozní balení | rozpracováno | D52–D59: Ubuntu backend, systemd a rollback. D67: veřejné IP HTTPS, Nginx, obnovovací timer a externí TLS/health sonda. D70: `/ios/` statická stránka, Ad Hoc IPA/manifest s externí kontrolou podpisu, MIME, hashe a HTTPS. | Reálná obnova certifikátu po expiraci, instalace na iPhone a zařízení; plné E11 vyžaduje E6/E8/E9, zálohu E10 a provozní zkoušku. |
| E12 – obraz telefonu | odloženo | Volitelné rozšíření po základní verzi. | E8/E11 a potvrzený přínos. |
| E13 – OV5640 + SD profil B | odloženo | Jen předběžný návrh B. | E0 pro B/E9/E12; nové měření pinů a zátěže. |

## Chybějící vstupy a blokace

Jde o budoucí podmínky konkrétních etap, nikoli důvod neprovést E1/E2. Další otázky a výchozí rozhodnutí zůstávají v kapitole 13 plánu.

| ID | Chybějící vstup a kdo ho dodá | Co blokuje | Co může pokračovat |
| --- | --- | --- | --- |
| B1 | **Částečně ověřeno D122:** doručený N16R8, USB-UART COM7, ESP32-S3 chip v0.2, 16 MB Quad flash/3,3 V a 8 MB Octal PSRAM/80 MHz; autorizovaná USB-only diagnostika prošla tři restarty a 7 MiB pattern test. Stále chybí revize PCB/schéma, nativní USB/LED, externí napájení, bezpečnostní větev/watchdog, mechanika, servisní vstup a kalibrace. | Flash aplikačního firmware a provisioning s periferiemi, E5/E6 a HW akceptaci E9/E13. | USB-only diagnostika v doloženém rozsahu D122, hostitelské testy, protokol, simulátor, SW build. |
| B2 | **Vyřešeno pro Ad Hoc export:** environment `ios-signing` obsahuje distribuční `.p12` a profil pro Team `3QRXV8G5Y3`, Bundle `app.turquoise6409.green2444`, jedno zařízení, `get-task-allow=false`, expiraci 24. 12. 2026. Stejné `IOS_P12_PASSWORD` OpenSSL přečetl; [běh 36636991415](https://github.com/Blato58/DildoVander/actions/runs/36636991415) importoval přebalenou identitu, ověřil shodu certifikátu s profilem, archiv, podpis a embedded profil IPA. | Podpis již neblokuje E3a; obnovit certifikát/profil před expirací. | iPhone akceptace B3 a další software. |
| B3 | **Vyřešeno pro první instalaci E3a:** majitel potvrdil iPhone 15 Pro s iOS 26.6.1 a správné UDID v profilu. Stránka [https://2.28.202.220/ios/](https://2.28.202.220/ios/) veřejně vrací podepsanou verzi, `itms-services`, manifest a IPA; D70 ověřil externí HTTPS download a majitel 30. 9. potvrdil instalaci i spuštění. | LAN/Keychain a ovládání E10, aktualizace/rollback E11 stále vyžadují samostatnou zkoušku. | E4.3d2b a další software může pokračovat bez telefonu. |
| B4 | Majitel dodal Ubuntu server s veřejnou IP `2.28.202.220` a runnerem `dildovander-server`; backend běží na loopback. OPENAI_API_KEY je v GitHub environmentu; D63 ověřil jeden placený Responses tah na loopback API. D67 root bootstrap vydal platný IP SAN certifikát, zapnul Nginx a obnovovací timer; externí [sonda 36633620137](https://github.com/Blato58/DildoVander/actions/runs/36633620137) ověřila veřejnou TLS důvěru, health, platnost a HTTP přesměrování. Pro skutečné zařízení zbývá jeho Device ID/credential, region/retence a fyzická TLS akceptace. GitHub environment má limity 0,50 USD/sezení a 10 USD/měsíc; majitel 29. 9. potvrdil projektový hard spend limit 10 USD v OpenAI. | Reálné ESP32/TLS spojení a produkční podmínky AI dat. | Veřejné HTTPS nyní funguje pro syntetické testy bez HW; loopback backend, CI a E8 eval korpus. |
| B5 | Katalog šesti původních hůlek a Harry Potter / Ollivander tematickou roli majitel svěřil nezávislému agentovi; zdroj je `ai-eval/wand-catalog.json` (D72). Stále chybí rozměry, router, prostředí akce a živé obsahové hodnocení s hosty. | Provozní akceptaci E8/E11, fyzické mapování hůlek a finální mechaniku. | Sdílený tematický katalog, vývojové a simulované scénáře; skutečné přihrádky až s HW. |

## Deník změn a ověření
### D108 – 1. 10. 2026 – iOS Debug a izolované hlasové testy

- Rozsah: mobilní Ovládání/Debug tab, read-only snapshot telemetrie, uptime/boot/revision/eventSequence/RTT/fault count, Stop, RAM log 200 událostí s filtrem/pause/clear/export. Swift plugin vlastní zabezpečený dialog, dočasný backend credential, HTTPS bez redirectů, opening/speech/poll/WAV/AVAudioPlayer a systémový hlas. Backend doplňuje autentizovaný metadata endpoint a OpenAPI. Změny patří `mobilni-aplikace/src/features/debug/`, mobilnímu vstupu/stylům/adaptérům/testům, dvěma Swift zdrojům/Xcode registraci, nativnímu CI a dotčeným backend/contract/docs souborům; firmware se nemění.
- Ověření na Windows: mobil pod Node 24.21.0 `npm test` (19/19) a `npm run build` — úspěšně. `dotnet build backend/backend.csproj --no-restore` z `kufr` — bez warningů/chyb. `node backend/tests/smoke.mjs` pod Node 24.21.0 — úspěšně, včetně 401 bez access tokenu/za credential a safe fake diagnostiky. `npm test` v `contracts` pod Node 24.21.0 — strict fixtures, OpenAPI/security a média prošly. První buildy odhalily DOM iterátor a chybně umístěné telemetry pole, contract verifier potřeboval přidat novou cestu; po opravách všechny uvedené kontroly prošly. `git diff --check` bez chyb.
- Browser: in-app Browser proti Vite 127.0.0.1:5174 a nativnímu syntetickému harnessu. Ověřeno přepnutí tabů, mobilní 390px i desktop 1280px layout bez vodorovného overflow, párování/connect recovery fixture a skutečná pole snapshotu, profil/metadata/fake voice větev, zakázané AI tlačítko pro fake, Stop hlasu, pause/filtr/clear a klávesnicové přepnutí. Po opravě pause zůstává log během hlasové akce zmrazený a po resume ukáže nasbíranou akci. Console bez warn/error; screenshot uložen jako lokální náhled mimo repo. Syntetický bridge neprovádí žádný síťový/AI test.
- Read-only review opravilo ztracenou create odpověď, identické JSON bytes při retry, persistentní chybu cleanup a cancellation během create. Nativní harness navíc testuje profil, korelaci, WAV/MIME/size, fake/OpenAI mismatch, cleanup, retry a skutečně zrušený Task; na Windows nelze spustit Swift/macOS.
- Neprovedeno: placená AI, nasazení backendu, podpis/publikace IPA, živé iPhone audio/alert/lifecycle, ESP nebo I2S/GPIO. Tiché fake WAV nesmí být vydáváno za AI TTS. Nový debug profil vyžaduje předem provisionované samostatné zařízení `debug-*`; místní WSS token se nepoužívá. Credential/audio nevstupují do JS/logu, rozhovor může zůstat v paměti testovacího večera. Backend zůstává v dosavadním deploymentu, nová metadata route/hlas tam tímto nejsou aktivovány.
- Doručení: první commit `87f068d87a703e8848ffd4a916494eb346548c0b` a finální head `1f028be5ba8f975dfbde706a27168f0ff2f846cf` pushnuty ověřeným skriptem. První [iOS CI 36852443796](https://github.com/Blato58/DildoVander/actions/runs/36852443796) zkompilovalo Swift klienta, ale harness selhal na vadné WAV fixture (měnila již nulový bajt vzorkovací frekvence). Po opravě zelené [iOS CI 36852710009](https://github.com/Blato58/DildoVander/actions/runs/36852710009): native debug harness včetně skutečného Task cancel/create recovery, transport testy, Xcode build a iOS Simulator launch. [Backend CI 36852710066](https://github.com/Blato58/DildoVander/actions/runs/36852710066) a [firmware/contracts CI 36852709863](https://github.com/Blato58/DildoVander/actions/runs/36852709863) také zelené. [PR #72](https://github.com/Blato58/DildoVander/pull/72) sloučen až po těchto kontrolách; merge `ae4ffa7c436abf74b5e356a529e562a89cc71557` načten scoped fetch a ověřen fast-forward na `main`. iPhone/paid/deploy zůstávají neprovedené. Tento navazující zápis mění jen dokumentaci.

### D107 – 1. 10. 2026 – firmware příprava výstupu AI hlasu

- `SpeechOperationCodec` sestaví existující request ze skutečného turn ID;
  přijme pouze `kind:speech`, přesnou korelaci, device target, UUID a validní
  UTC expiry. Sdílí beze změny významu UTC parser s `TurnPollGuard`.
- `SpeechAudioBuffer` nealokuje velkou paměť. Callerem dodaný buffer má strop
  1 920 044 B; ověřuje metadata, canonical WAV32kHz/mono16, celou délku,
  monotónní TTL a snapshot. Počátek/příjem/finish/PCM čtení ověřují stejnou
  autoritativní verzi; neúplná, stará nebo zrušená data se nulují.
- `EspIdfSpeechTransport` přidává POST speech, GET operation/audio a DELETE
  release. CA bundle/hostname, bez redirect/auth retry, bounded JSON/hlavičky,
  známá audio Content-Length a 15s download gate; credential stack se nuluje.
- `SpeechOutputCoordinator` použije existující silnou validaci dokončeného
  turnu, explicitní audio consent a `TurnPollSchedule` se společným 45s limitem.
  Lost acceptance retry zachová key/body, stale/cancel/deadline nic nevydají;
  po celém downloadu uvolní backendovou audio operaci. Data nejsou povel k servo/I2S.
- Všechny tři nové testy `speech_audio_buffer_test`, `espidf_speech_transport_test`
  a `speech_output_coordinator_test` jsou v CI. Lokálně v1.10. prošel MSVC
  `/std:c++14 /EHsc /W4 /WX` nad zdroji z firmware README a všechny executable.
  Testy kryjí metadata, poškozený WAV, overflow, korelaci, TTL, nulování,
  HTTPS konfiguraci/route/release, retry, source/consent, timeout a Stop.
  Úvodní test chybně zkoušel vrátit generation zpět a byl opraven; compile
  narrowing v testu byl opraven před úspěšným během.
- `src/CMakeLists.txt` sestavuje nové soubory, `platformio.ini` vynechává IDF
  síťový adaptér z Arduino profilu. `pio run -e esp32-s3-arduino-serial` prošel.
  Poslední `pio run` narazil na GCC internal compiler error/segfault v SDK
  `esp_lcd_panel_rgb.c`; beze změny zdrojů opakovaný `pio run -j 2` prošel
  (RAM65 228 B, flash1 147 437 B). Lokální odkazy Markdown a `git diff --check`
  prošly. Nezávislá read-only revize nenašla materiální chybu.
- Není skutečné TLS/HTTP na ESP32, ověřená PSRAM, I2S/PTT nebo poslech hlasu.
  `RunTurn` zůstává textový s audio consent false; nové třídy se do něj
  automaticky nepřipojují. Další runtime binding musí mít skutečný playback
  a consent port; STT upload je samostatná připravitelná část. E9 není hotovo.
- Implementační commit `09145cff006bec084bb0a8a2a145c91d91dda5ad` byl pushnut
  přes `scripts/Push-CurrentBranch.ps1`; script ověřil shodu vzdáleného head.
  Při závěrečné staged kontrole byla odstraněna nadbytečná EOF prázdná řádka
  nového kodeku; `git diff --check` byl po opravě zopakován.
- Předání 1.10.: finální head `bc48fb3514c5095214affa95a736d06e73a607a4` byl
  ověřen push scriptem. [CI 36845545981](https://github.com/Blato58/DildoVander/actions/runs/36845545981)
  prošlo: host-tests 41s, oba firmware profily 6m3s.
  [PR #71](https://github.com/Blato58/DildoVander/pull/71) byl bez konfliktu sloučen
  jako `430af8d48b174677fe9aae4ecb340fd4effe5857`; scoped fetch/fast-forward ověřil `main`.

### D106 – 1. 10. 2026 – backendová příprava skutečného AI hlasu

- Majitel požádal o připravený hlas kufru. §7 plánu nyní výslovně odděluje
  nezávislé softwarové E9 od fyzické akceptace; závěr D105 byl opraven.
- `backend/Features/VoiceTurns/`, `Speech/` a částečný `ConversationVoiceOperations`
  přidávají STT `gpt-transcribe` a TTS `gpt-4o-mini-tts-2025-12-15`/`marin`.
  `Backend:Voice:Mode=openai` vyžaduje klíč a kladné limity, výchozí fake zůstává.
  `Program.cs` ověřuje konfiguraci při startu. Existující trasy/SQLite schéma se nemění.
- STT/TTS mají 15s deadline, pouze jeden placený dialog/audio call, stejné
  idempotence/cancel/consent/device kontroly. Vlastní upload a odmítnuté audio se
  nulují. TTS generuje pouze uloženou repliku; filtrovaný PCM24→32kHz převod
  zachovává délku a potlačuje spektrální obraz. WAV nad30s se celý odmítne.
  Rezervace 0,01/0,05 USD zůstávají bez skutečného billing údaje konzervativně
  započtené; nejsou ověřenou skutečnou cenou. Neodeslaná práce se vypořádá nulou.
- `backend-tests/VoiceProviderTests.cs` ověřuje konfiguraci, provider HTTP formáty,
  frekvenci/délku převodu, neplatné/velké výsledky, replay, kopii uploadu, souběh,
  cost/consent, TTL/nulování, cancel/end/shutdown a pozdní výsledky po deadline.
  Backend CI nově validuje i PR, deploy zůstává pouze ruční z main.
- Ověření 1.10. v `kufr/`: `dotnet build backend/backend.csproj --no-restore`
  prošel bez warning/error; `dotnet run --project backend-tests/BackendTests.csproj --no-restore`
  prošel. Z kořene `node kufr/backend/tests/smoke.mjs` prošel;
  `python -m unittest discover -s kufr/backend/deploy -p 'test_*.py'` 3/3 a
  obdobný příkaz pro `kufr/backend/tests` 9/9 prošly. Počáteční compile/config/test
  chyby a změna existujícího busy kódu byly opraveny před úspěšným během.
- Neproběhlo živé placené OpenAI volání, poslech českého hlasu, cloud deploy
  ani hardware test. Firmware audio přenos a skutečný PTT/I2S ještě nejsou součástí D106.
  Další krok je nezávislá firmware audio příprava, nikoli uzavření E9.

### D105 – 1. 10. 2026 – ověřená blokace pokračování bez B1

- Převzetí: čistý `main` na `5b4ce39`; scoped `git fetch origin main` a `git rev-list --left-right --count HEAD...origin/main` potvrdily `0 0`. Plán, stav a skutečný runtime byly znovu porovnány. Majitel v tomto pokračování výslovně potvrdil, že podklady B1 zatím nemá.
- Evidence: `espidf_main.cpp` skládá provisionovaný TLS/WSS server, owner a backendovou síť, ale `UnverifiedInspection::InspectionAllowsRecovery()` vrací false, kalibrace zůstává 0 a safety port pouze loguje bez GPIO. Nezávislý read-only průchod mobilu/backendu potvrdil existující native transport/text/Bonjour a fake audio API. Servisní USB/AP a konkrétní QR envelope čekají na fyzickou bránu; E5/E6, E9 a telefonní audio mají nesplněné závislosti. E12/E13 zůstávají odložené. Nový proveditelný implementační slice podle plánu nebyl nalezen.
- Rozsah změn: firmware README opravuje zastaralé tvrzení o chybějícím WSS parseru/session/gateway a upřesňuje, že owner bránu vytváří při převzetí autentizovaného spojení, před hello/welcome. Tento zápis zachovává historická ověření D101–D104 a jejich rozsah. Kód, kontrakty a konfigurace se nemění.
- Ověření a předání: PowerShell kontrola obou změněných Markdown souborů ověřila 17 místních odkazů a vyvážené code fences; inventura všech 15 etap se shoduje s plánem. `git diff --check` prošlo a diff obsahuje pouze tento stav a firmware README. Aplikační buildy/testy se při dokumentační změně neopakují. Neprovedeno: HW/iPhone akceptace, placený AI test, podpis/publikace IPA, deployment ani firmware flash. Další krok zůstává doložená B1, potom fyzicky střežené provisionování a E4/E5 na jedné přihrádce.
- Doručení: commit `d5e7aa451eb1cd584801f24a64ba180d17a5113c` ověřeně pushnut projektovým `./scripts/Push-CurrentBranch.ps1` na `codex/kufr-b1-recheck`; [PR #69](https://github.com/Blato58/DildoVander/pull/69) obsahuje pouze tyto dvě dokumentační cesty. Sloučení se provede po kontrole dostupného CI a konfliktů.

### D104 – 1. 10. 2026 – předání dokončených nezávislých slices na B1

- D101–D103 jsou doručené na `main`, nikoli pouze místní návrhy: native transport/párování/Keychain, device-owned text/cancel/result a firmware/nativní Bonjour s ručním adresním fallbackem. D103 finální commity `043fd729087ca0c47a3fb2fe99468572183625ac`, `5e212ca4310f0a70ae60386d6a3892e3c7e99bb0`, `a9a34427eb8f0407f96176096bc1492872cf813c` a `e0be0dad1ca8925b7dfe318112228f286f00821e` byly ověřeně pushnuty projektovým skriptem. [PR #67](https://github.com/Blato58/DildoVander/pull/67) bez konfliktů/diff mimo scope sloučen jako `6315857414e2c6030e991f3ddda43aa21ceee28a`; scoped fetch a fast-forward lokální main tento merge ověřily.
- Finální [firmware CI 36797984092](https://github.com/Blato58/DildoVander/actions/runs/36797984092) a [iOS CI 36797984095](https://github.com/Blato58/DildoVander/actions/runs/36797984095) pro `e0be0dad1ca8925b7dfe318112228f286f00821e` prošly: všechny host testy, ESP-IDF/Arduino build, 16 mobilních testů/web build, skutečný macOS Bonjour resolver + Keychain/pinned HTTPS/WSS harness, Xcode a install/launch/terminate iOS Simulator. Finální device ID negativní testy a mDNS 1.13.1 lock jsou součástí této revize. Browser native harness otestoval discovery/paired adresu/empty/denied a invalid/valid v1 ID; 393×852 bez vodorovného přetečení a console warnings/errors. Real C++ simulator browser text/Stop ověřeno D102. Dočasné testovací servery byly ukončeny; pouze vlastní MSVC artefakty vzniklé v rootu byly odstraněny.
- Navazující závislosti byly znovu zkontrolované podle §4/§12. B1 stále chybí v doložených podkladech: konkrétní PCB/flash/PSRAM/USB, měření nezávislé bezpečnostní větve/watchdogu, skutečný service/inspection vstup a kalibrace jedné přihrádky. Bez nich nelze ověřit servisní USB/AP cestu, NVS/TLS/STA na desce a fyzický port E5; E6 a E9/E10 audio na ně navazují. QR kamera potřebuje dosud chybějící konkrétní servisní envelope/cestu, ne samotnou permission. Existující AP i USB varianta plánu se zachovávají. Fyzický iPhone/LAN/Keychain update a mechanická akceptace nejsou nahrazené macOS/simulator testem. Neprovedeno: placený AI test, nový podpis/publikace IPA, cloudové nasazení ani firmware flash.

### D103 – 1. 10. 2026 – E4.4t/E10.3 Bonjour discovery

- Převzetí: firmware i nativní transport mají živé softwarové adaptéry, ale chybí předepsaná `_kufr._tcp.local` inzerce a klientské hledání. Doplnit public TXT `deviceId`, `protocol=1`, capabilities a port 443, nejvýše 5 s hledání s ruční IP fallbackem. mDNS není identita; SPKI pin zůstává jedinou lokální autoritou. ESP-IDF už mDNS neobsahuje, proto konkrétní plánovaná funkce používá oficiální komponentu `espressif/mdns` přes component manifest/lock, původně ověřenou na 1.11.3 a finálně připnutou na 1.13.1. Nové knihovny pro telefon nejsou potřeba.
- Rozsah: vlastní síťová úloha inzeruje až po úspěšném provisionovaném TLS serveru; IP epoch detekuje i krátký reconnect, žádné mDNS SDK volání v safety Tick ani cloudový lifecycle. NWBrowser + bounded NetService resolvery vracejí public hints, nikdy pin/token; nejvýše 16 a 5 s, cancel při pozadí. UI discovery/ruční IP fallback i pro paired zařízení; adresní hint v RAM, trvalý zápis pouze po připnutém autentizovaném WSS se stejným pin/tokenem. Žádný automatický connect/takeover.
- Místní ověření: MSVC /W4 /WX /utf-8 tři lifecycle testy (skutečné WifiStation, StationNetwork, mDNS adapter se SDK stubs) prošly. `pio run -e esp32-s3-idf -s` a Arduino serial profile prošly. První IDF build odhalil pořadí FreeRTOS hlaviček/mutable TXT SDK argument; opraveno. Actual C++ output verifier 49 zpráv prošel po sjednocení capabilities; mobilní testy 16/16, TypeScript/Vite a cap sync ios prošly. Browser synthetic native harness: nalezená adresa vyplní veřejné údaje bez pinu, paired změna IP nechá odpojeno, empty/denied vysvětlení a ruční fallback; konzole čistá. Nativní Swift/mDNS/Xcode CI je doloženo níže. Risk review: opravena recoverable waiting do deadline a normalizace service type s finální tečkou. B1/živý ESP32 multicast/iPhone LAN oprávnění zůstávají samostatná akceptace.
- [iOS CI 36796322516](https://github.com/Blato58/DildoVander/actions/runs/36796322516) pro `043fd729087ca0c47a3fb2fe99468572183625ac`: Swift warnings-as-errors harness skutečně našel/resolveoval místní syntetický Bonjour inzerát, validoval TXT/host/port a deadline/cancel; Keychain/pinned HTTPS/WSS regrese prošly. Xcode build a launch/terminate v iOS Simulator prošly. Signed/publish joby nebyly spuštěné. Finální firmware/iOS CI a sloučení jsou doložené níže. README byly navíc srovnány s D99 runtime (server/owner/gateway/reply již složené, inspection/GPIO/audio stále chybí).
- Finální kontrola kontraktu našla širší native device ID gramatiku než shared v1 (`^[a-z][a-z0-9-]{0,63}$`). Pairing nyní odmítá uppercase/podtržítko/číselný prefix před spotřebováním tajemství; stejný limit má nové discovery inzerování a HTML pole. Cílené host/native negativní testy doplněny; starý NVS formát se tím nemění, servisní generátor musí používat v1 ID.
- Finální dependency kontrola podle [oficiálního changelogu](https://github.com/espressif/esp-protocols/blob/mdns-v1.13.1/components/mdns/CHANGELOG.md) upřesnila nový pin na 1.13.1 (PSRAM/static task a browse memory/parser opravy). První inkrementální `pio run` po změně manifestu ponechal starý lock/komponentu; nebyl důkazem upgradu. Následující `pio run -e esp32-s3-idf -t clean -s` a skutečný rebuild vygenerovaly lock s 1.13.1 a čistý build úspěšně dokončil. Generated `managed_components/` není zdroj a necommituje se.

### D102 – 1. 10. 2026 – E10.2 místní textový klient

- Převzetí: již složený E4 runtime nabízí snapshot-bound text/cancel a korelovanou repliku. Doplnit klienta do společného protokolového řadiče a UI, bez telefonního cloud relay. Ruční lease/capability a snapshot jsou podmínky vstupu; Stop, cancel, změna revision/generation/bootu a odpojení zneplatňují čekající obsah.
- Rozsah: `LocalConversationClient.ts`, společný řadič/snapshot, stabilní formulář mimo heartbeat render a bezpečný textContent reply, cílené client/native regresní testy. Přijetí podle request ID, výsledek podle boot/session/clientTurnId/generation/eventSequence; cancel invaliduje před odesláním. ACK 3 s / reply 45 s, žádná odložená fronta ani automatické opakování. Mimo ruční lease/capability/ready snapshot nelze odeslat. Obsah jen v RAM stránky, s jasným cloud/paměť upozorněním pro nativní režim.
- Ověření: mobilní testy 16/16 a TypeScript/Vite build prošly. Test fixture pro opětovné acquire původně poslala starší eventSequence 2 po watermarku 4; opravena na 5, nový klient nevrací stav zpět. Přísný 8192B textový limit a 1000 Unicode znaků dle §5 včetně emoji/lone surrogate. Testy navíc kontrolují cancel před ACK, pozdní výsledek, boot/revision/lease změnu, chybějící ACK/reply a duplicate ACK bez prodlužování timeoutu. C++ simulator bridge sestaven z živých zdrojů MSVC /W4 /WX /utf-8. Browser přes skutečný loopback TLS/WSS most a C++ model: observer blokuje text, manual text vrátí označenou pevnou repliku, během tahu blokovaný výběr, prázdné pole po odeslání; Stop odstraní draft i reply a blokuje text. Konzole bez warnings/errors.
- Risk review našel chybějící cloudové upozornění a Cancel dostupné mezi result a clearing snapshotem; obojí opraveno, targeted result-before-snapshot assertion prošla. Fyzický iPhone/ESP32, placený backendový tah, audio, deployment ani IPA publikace se neprováděly. Doručení: commity `97009863bb9250d15ee6143394046148da27eade` a `a6bf8c1f905d019f1709b97a0dd745d0237759f7` pushnuty projektovým skriptem; [PR #66](https://github.com/Blato58/DildoVander/pull/66) po zeleném [web/iOS CI 36794263580](https://github.com/Blato58/DildoVander/actions/runs/36794263580) sloučen jako `ed7ea0212a9f345f78da32caf51abf6e1a57dc9c`, ověřen scoped fetch/fast-forward. Další software krok je Bonjour.

### D101 – 1. 10. 2026 – E10.1 nativní místní transport

- Převzetí: E4 má softwarový TLS/WSS/pairing runtime D99 a E8 textový backend D75; chybějící B1 blokuje jejich fyzickou akceptaci, nikoli přípravu Swift transportu podle §12. Implementovat ruční připojení podle device ID, adresy a SPKI z vnitřního QR, nativní párování a Keychain, WSS a stávající ruční protokol. Bez audio, podpisu/publikace nové IPA a bez nahrání firmware.
- Rozsah: čtyři Swift soubory v `mobilni-aplikace/ios/App/App/`, registrace Capacitor pluginu/storyboard, LAN usage description, nativní socket/řadič a párovací UI. Tajemství zadává pouze nativní secure dialog; token je pouze v nesynchronizovaném Keychain. Oddělený URLSession připíná DER SPKI bez globálních ATS výjimek. Strict browser verifier sdílí schéma z `contracts/`. Odpojení/pozadí zneplatní socket a návrat vyžaduje explicitní připojení; automatický retry za běhu má 0,5–8 s backoff s jitterem a nikdy nepřebírá ruční lease.
- Místní ověření 1. 10.: Node 24.21.0, reprodukovatelné npm ci mobilu/kontraktů (0 vulnerabilities), mobilní testy nejprve 9/9, po backoff testu 10/10; TypeScript/Vite build, cap sync ios, kontrakty 22 valid / 39 invalid + OpenAPI/media prošly. První nový integrační test použil již aktivní lease ze sdílené fixture; po změně výchozího welcome na observer prošel. Bash syntax, YAML/plist parser prošly. Browser: web fake select/Stop a syntetický nativní bridge pairing/observer/recovery, viewport 393×852 bez vodorovného přetečení a bez console warnings/errors. Testovací HTML není součástí release bundlu; neověřuje UIKit pairing dialog ani skutečný LAN permission.
- [CI 36792382045](https://github.com/Blato58/DildoVander/actions/runs/36792382045) pro `20a5abda0001dce405d222d5d1c6917c55c2a82f` prošlo: web, Swift test skutečného loopback HTTPS/WSS/Keychain a DER SPKI EC P-256/P-384/RSA, negativní pin/token/redirect/binary/size scénáře, unsigned Xcode build, install/launch/terminate v iOS Simulator. [Firmware CI 36792382095](https://github.com/Blato58/DildoVander/actions/runs/36792382095) prošlo host testy i ESP-IDF/Arduino. Read-only native risk review bez blokujícího nálezu. UIKit plugin lifecycle a native alert nejsou ověřeny CLI harness testem.
- Doručení: commity `20a5abda0001dce405d222d5d1c6917c55c2a82f` a `09b339c1b6240d5f26ac94bfd2ea521d8ef44154`, následně `76a162601ec3fd6afdddd2dd824532c3892707aa` ověřeně pushnuty přes projektový skript; [PR #65](https://github.com/Blato58/DildoVander/pull/65) po zeleném CI sloučen jako `5f54104b783c8cf2fc7e1885276322d535681f7b`, ověřen scoped fetch na main; [poslední iOS CI 36793112827](https://github.com/Blato58/DildoVander/actions/runs/36793112827) prošlo. E10 jako celek není hotové; B1, živý iPhone/TLS/Keychain update a mechanika zůstávají otevřené. Dosavadní výsledky D70/D99 mají původní datum a rozsah. Další nezávislý krok: klient `conversation.text`/cancel/result a následně Bonjour.

### D100 – 30. 9. 2026 – audit návaznosti a předání na B1

- Rozsah pokračování: D90–D99 dokončují deset ověřitelných firmware slice od RNG/TLS identity přes přísný parser a prioritní inbox po WSS router/output/reply, jednorázové HTTPS párování a runtime s jediným vlastníkem LAN. Každý vlastní slice má commit/push, kontrolovaný PR a ověřené sloučení po host/firmware CI. Současná práce neuzavírá E4 hardware ani E5/E9/E10.
- Blokace nejbližšího kroku: B1 dosud nemá revizi PCB a potvrzené flash/PSRAM/USB, schéma/měření watchdogu a bezpečnostní větve, fyzicky měřitelný servisní/inspection vstup a kalibraci jedné přihrádky. Bez nich nelze servisně provisionovat, otestovat skutečný TLS/NVS/RNG/STA a bezpečně připojit fyzický port. Runtime nesmí nahradit tuto mezeru fake Recover/inspection. Majitel 29. 9. potvrdil, že údaje o revizi/paměti/USB nemá.
- Návaznost podle §12: E5 vyžaduje E0/E1/E4 a měření; E9 vyžaduje E4/E5/E8; native E10 potřebuje E4/E8 a vlastní fyzický iPhone test. V rámci převzaté nejbližší firmware cesty není další krok se splněnými závislostmi; zbývající testování na hostu neuzavře chybějící HW kritéria. Přímý servisní opening/PTT a audio/presentation zůstávají po těchto vstupech.
- Dokumentace: rozšíření D97 result a D98 pairing je přesunuto z konce referencí do příslušných částí protokolu; D99 runtime do architektury. Handoff a README odpovídají kódu, B2/B3 nepředstírají novou blokaci. 36 místních Markdown odkazů a diff kontrola prošly; u dokumentačního předání se nespouštěly ručně další aplikační buildy. Cizí změny nebyly přidány.

- Doručení D99: commit `f7afaf427605058762d92b98811232a78c5a577b` ověřeně pushnut; [CI 36776068730](https://github.com/Blato58/DildoVander/actions/runs/36776068730) prošlo všemi hostitelskými testy včetně sdílené STA a JSON/schema interoperability, ESP-IDF i Arduino profilem. [PR #63](https://github.com/Blato58/DildoVander/pull/63) sloučen jako `d88dc78ff3206de162af0d692ae7a7ad9d31a824`, ověřen fetch na `origin/main`.

### D99 – 30. 9. 2026 – E4.4s provisionovaný runtime a nezávislá LAN

- Převzetí: jediný EspIdfStationNetwork vlastní Wi-Fi/netif a retry task, Backend sdílí immutable settings a IP stav; cloudový token/čas/WAN síť neukončí. app_main skládá dlouho žijící TLS/pairing/server/inbox/output/owner, server start až po IP a zdravém provisionování. UUID boot/sezení před RF, lease přes STA RNG, aktuální katalog. Owner Tick/PumpReply jsou jediným řídicím průchodem. Bez fyzického portu zůstává kalibrace 0 a inspection gate false, žádné automatické Recover, GPIO ani fake autonomní ovládání.
- Ověření: station SDK-stub test s MSVC /W4 /WX prošel chybějící nastavení, task/Wi-Fi failure, jedno init, nepublikované IP před init, retry/backoff/reset po reconnect a ztrátu IP. Read-only runtime review bez nutné opravy. Finální ESP-IDF a Arduino profily prošly. YAML parsing, 35 místních Markdown odkazů a diff kontrola prošly.
- Neprovedeno: skutečné STA/netif/IP změny, NVS/TLS/WSS/pairing/text na ESP32 a iPhone, USB/service/inspection brána, kalibrace, pohony, PTT/audio; B1 a závislé E5/E9/E10 zůstávají otevřené. Nejbližší HW krok není nahrazen fake portem.

- Doručení D98: commit `8bbf4d97ded77d7b1afd582d9f44dde57c838daa` pushnut; [CI 36774326378](https://github.com/Blato58/DildoVander/actions/runs/36774326378) prošlo host testy a obě firmware sestavení. [PR #62](https://github.com/Blato58/DildoVander/pull/62) sloučen jako `79785c53bc5357a4bb5b934876bfd2ff4f8465e6`, ověřen fetch na `origin/main`.

### D98 – 30. 9. 2026 – E4.4r jednorázové HTTPS párování

- Převzetí: provisionovaný server přijme pairing-ready záznam, registruje POST `/v1/pair` vedle WSS. Přes připnuté TLS přijímá jen application/json, přesný URI, bez Transfer-Encoding a nejvýše 192B tělo; přesný objekt protocolVersion 1 / pairingSecret lowercase hex 16–64B, bez duplicit/unknown polí. Model ověří tajemství, RNG s aktivní STA vydá 256bitový token, NVS uchová pouze hash a zneplatní secret. Response no-store, všechny citlivé lokální buffery se vynulují. Nikdy neřídí pohony. Ztracená odpověď po trvalém spárování potřebuje fyzický servisní reset, nikoli opakované vydání tokenu.
- Ověření: MSVC `/W4 /WX` skutečného serverového adaptéru se SDK stuby prošlo fragmentované HTTP čtení, secret mismatch, malformed/duplicate/unknown JSON, velikost, Content-Type, query/Transfer-Encoding, chybu čtení/RNG/NVS a ztracenou odpověď, jeden úspěch a odmítnutí replay. Dosavadní WSS/auth/close/reused-FD testy prošly, oba PlatformIO profily prošly. Read-only review bez kritického nálezu; doplněna rezervace citlivých string bufferů před jejich plněním, kontrola response headers a test chyby po částečném HTTP read. Diff kontrola prošla.
- Neprovedeno: živé TLS/NVS/RNG, fyzické provisionování, service AP, native iPhone pairing; B1 brána zůstává beze změny.

- Doručení D97: commit `c36b095c45482feabf2cbeff7131f3403fd71a93` pushnut; [CI 36773306562](https://github.com/Blato58/DildoVander/actions/runs/36773306562) prošlo host testy a obě firmware sestavení. [PR #61](https://github.com/Blato58/DildoVander/pull/61) sloučen jako `b39685c068d8efa7103b94fd329bf685a61a344e`, ověřen fetch na `origin/main`.

### D97 – 30. 9. 2026 – E4.4q skutečná backendová replika ve WSS

- Převzetí: samostatný reply port odděluje síťový inbox od řídicího konzumenta. PumpReply na řídicím vlákně kontroluje token, snapshot, místní sezení/tah, generation, lease a urgentní frontu; uvolní každý převzatý token a vynuluje repliku. Výstup zůstává mimo urgentní guard a neřídí pohony; text zařazený před cancel nese starou generation. Backend false / simulátor true a limit 500 znaků / 8192 raw JSON bajtů jsou zdůvodněné rozšíření v1 v plánu a README. Společný JsonWriter zachovává dosavadní escaping.
- Ověření: MSVC `/W4 /WX` owner test prošel úspěšné/duplicitní dokončení, starý snapshot/token, odpojení, Stop před/uvnitř send a chybu transportu. 49 skutečných C++ zpráv prošlo sdíleným Node v1 schema verifierem, včetně UTF-8, escaping a 500 znaků. Regrese ConversationTextCodec, oba PlatformIO profily, contracts testy (22/39 + OpenAPI/media), mobilní testy (4) a TypeScript/Vite build prošly. Read-only review bez kritického nálezu; dokumentovaná race odeslaného textu proti Stop nezpůsobí dokončení ani pohyb, následný snapshot/generation je autoritativní. Diff kontrola prošla.
- Neprovedeno: app_main aktivace, audio/I2S/TTS, pohony, živý ESP32 WSS a B1.

- Doručení D96: commit `bce9dacf45d952c86e3eb0b265483a8678bcb3f2` pushnut; [CI 36772263834](https://github.com/Blato58/DildoVander/actions/runs/36772263834) prošlo host testy a obě firmware sestavení. [PR #60](https://github.com/Blato58/DildoVander/pull/60) sloučen jako `8bdc4e68cea0c71c6af1d97d921fe8fd76a624d1`, ověřen fetch na `origin/main`.

### D96 – 30. 9. 2026 – E4.4p pravdivý WSS snapshot a ACK

- Převzetí: serializer má číst skutečný control/journal/safety/selection/conversation stav, nikoli kopii simulátoru. Úzké read-only gettery doplňují kalibraci, pending command, terminal result, phase a active turn. Autonomní aktivita potřebuje samostatné phase presenting; proto před implementací doplněn enum v1 a §5 plánu, bez změny verze či dalších polí. Strict klient přebírá nové schéma; nynější mobilní simulátor má string phase a bezpečně mapuje ne-ready na running.
- Ověření: MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` a společný Node 24.21.0 verifier ověřily 47 skutečných firmware zpráv proti v1 schématu. Regrese DeviceControl a LocalControlOwner prošly. Review vedl k opravě fáze po Accept před Begin a revize prvního idle Stop; opakovaný Stop zůstává idempotentní. Journal failure a přímý safety fault mají pravdivý snapshot, mikrofon unavailable a kalibrace 0 nikdy ready. Oba PlatformIO profily prošly, contracts testy (22 valid / 39 invalid + OpenAPI/media) a mobilní testy (4) i TypeScript/Vite build prošly. CI ověřuje identické C++ JSON výstupy přes sdílené schéma; contracts změny spouštějí firmware workflow. YAML parsing a diff kontrola prošly.
- Neprovedeno: aktivace serveru v app_main, skutečná replika a HW/TLS/B1. Žádný fyzický stav se nevydává za měření.

### D95 – 30. 9. 2026 – E4.4o řídicí konzument místních zpráv

- Převzetí: typovanou frontu a místní lease/text bránu spojuje `LocalControlOwner`. Zpracovává pouze řídicí úloha, žádný callback. Output port zůstává typovaný pro následný serializer; reset vyžaduje platný lease a explicitní inspection gate, ID lease dodává oddělený port. Poll má omezený počet zpráv, hello deadline a kontrolu lease. Guard inboxu atomicky řadí přijímání Stop/close a řízené fyzické přechody; `owner.Tick()` je jediná cesta runtime Tick. Výstup zůstává mimo guard, Begin po ACK používá čerstvý monotónní clock a propaguje i Stop při odmítnutí. Fyzický/step port v guardu musí být krátký a nesmí reenterovat inbox/transport.
- Ověření: `%TEMP%/kufr-control-owner/test.cmd` s MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` prošel hello/observer/lease/text/cancel/failure/reset a Stop mezi ACK/Begin. `%TEMP%/kufr-control-inbox/test.cmd` prošel souběžným pořadím guard/Stop. Nezávislý review zachytil TOCTOU a starý čas po ACK; opraveno a testovaný ACK posunutý za expiraci nevydal SearchStep, vydal Stopped a zrušil lease. Prošly obě `pio run -d kufr/firmware -e esp32-s3-idf -s` / `-e esp32-s3-arduino-serial -s`, YAML parsing a diff kontrola. Bez aktivace v `app_main`/GPIO.
- Doručení D95: commit `c7275e6834b1a3b74545060841bdb4cd38226547` pushnut; [CI 36767834236](https://github.com/Blato58/DildoVander/actions/runs/36767834236) prošlo host testy i obě sestavení. [PR #59](https://github.com/Blato58/DildoVander/pull/59) sloučen jako `5ff164fc6d771523c551090eb6c0b74e3edda8de`, ověřen fetch na `origin/main`.
- Neprovedeno: serializer, výstup replika, provisionovaný runtime, živé WSS a HW/B1.

### D94 – 30. 9. 2026 – E4.4n serverový adaptér autentizovaného WSS

- Převzetí: po D93 následuje ESP-IDF HTTPS/WSS adaptér `/v1/control` bez GPIO a bez spuštění v `app_main`. TLS identita a párovací credential musí být provisionované; token se kontroluje před 101. Callback pouze parsuje/enqueue, odpojení přebírá řídicí vlastník. Výstup a close běží omezeným workerem HTTPD s generací socketu. Wi-Fi/netif lifecycle zůstává u síťového vlastníka.
- Ověření: `%TEMP%/kufr-wss-server/test.cmd` s MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` prošel callbacky Start/pre/post/read/close, fixture hello/Stop, chybné rámce, plné fronty, selhání work/send a reused FD. `pio run -d kufr/firmware -e esp32-s3-idf -s` prošel skutečným SDK; první překlad odhalil neúplnou vendor inicializaci, kolem makra je úzce omezené GCC potlačení této jediné kontroly. `pio run -d kufr/firmware -e esp32-s3-arduino-serial -s` také prošel. Lokální ignorovaný sdkconfig byl zálohován do TEMP a generátor ho obnovil z defaults; efektivně zapnuté HTTPS/WS/pre/post byly ověřeny. YAML parser a diff kontrola čisté. Nezávislý review nezjistil materiální auth/lifecycle vadu; pomalý autentizovaný příjem a Stop latency vyžadují skutečné měření.
- Doručení D94: commit `5e3652672d3afa56103a10a1c118c177bb230ada` pushnut; [CI 36764532650](https://github.com/Blato58/DildoVander/actions/runs/36764532650) úspěšné pro host testy i obě sestavení. [PR #58](https://github.com/Blato58/DildoVander/pull/58) sloučen jako `1774b36eafab13a2dce6363130307b48897fc7b9`, ověřen fetch na `origin/main`.
- Neprovedeno: skutečný TLS/WSS provoz, runtime composition, servisní pairing/provisioning a HW/B1.

### D93 – 30. 9. 2026 – E4.4m fronta s prioritou Stop a odpojení

- Převzetí: síťový callback nesmí měnit řízení ani ztratit odpojení při zaplnění fronty. `LocalControlInbox` drží jednu device/boot/socket generation, čtyři běžné zprávy a oddělené sloty připojení/Stop/odpojení. Stop zneplatní běžnou frontu včetně resetu; nepřijímá další povely do převzetí Stop. Odpojení nelze zahltit a nový socket čeká na převzetí starého close. Opožděné či cizí handly a starý boot se odmítají. Rozsah: model, test/CI, CMake, README a stav.
- Ověření: MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` test z `%TEMP%/kufr-control-inbox/test.cmd` prošel plnou frontou, Stop/close prioritou, reused FD a starým bootem. Postupně prošly `pio run -d kufr/firmware -e esp32-s3-idf -s` a `pio run -d kufr/firmware -e esp32-s3-arduino-serial -s`; YAML parser a `git diff --check` čisté. Při odebrání/zahození se uložený text přepisuje. Jedno volání použilo neexistující název Arduino profilu; opravený příkaz prošel.
- Doručení D93: commit `3456f89f0f0573865e9e13e4e544c21918dc9a77` pushnut; [CI 36761901721](https://github.com/Blato58/DildoVander/actions/runs/36761901721) úspěšné pro host testy a obě sestavení. [PR #57](https://github.com/Blato58/DildoVander/pull/57) sloučen jako `5797fbb5220e27d4bd3a2ea9e09b672ae1844e3c`, ověřeno fetch na `origin/main`.
- Neprovedeno: skutečný WSS server a konzument v IDF smyčce, fyzická deska/B1. Následuje serverový adaptér s autentizací před upgradem a bezpečným předáním odpojení.
### D92 – 30. 9. 2026 – E4.4l přísný místní parser příkazů

- Převzetí: WSS nesmí předávat libovolný JSON vlastníku řízení. `LocalControlCodec` podporuje pouze klientské v1 hello/state/lease/text/cancel/Stop/reset/selection/reload, kontroluje přesná pole, duplicitní i escaped klíče, UUID, integer lexémy/rozsah, UTF-8 a limity 2048/8192 B. `JsonCursor` je beze změny původního chování vyčleněný z `TurnOperationCodec` jako sdílený technický parser (doplněno čtení true). Výstup je jen typovaná zpráva; autentizace a aktuální lease/snapshot patří následné bráně. Rozsah: parser/refaktor, test/CI, CMake, README a stav.
- Ověření: MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` prošel vlastními mezemi, všemi 11 klientskými valid fixtures a 8 invalid fixtures i regresním `turn_operation_codec_test`. Postupně prošly `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s`; YAML parser a `git diff --check` čisté. První očekávání testu omylem porovnávalo velké Č s escaped malým č; opraveno a opakování prošlo.
- Doručení D92: commit `f20efe0740977f34fefe62981b7d8332dc4c0aba` ověřeně pushnut; [CI 36759642257](https://github.com/Blato58/DildoVander/actions/runs/36759642257) prošlo hostitelskými testy i firmware buildy. [PR #56](https://github.com/Blato58/DildoVander/pull/56) sloučen jako 315931d9fdc2ddb772ac181f5196ff1ee90a6e97, ověřen na `origin/main`.
- Neprovedeno: živý WSS transport a runtime, skutečné TLS/NVS na desce, B1. Další krok: omezená fronta s prioritou Stop/odpojení, potom uzavřený server a řídicí konzument.
### D91 – 30. 9. 2026 – E4.4k oddělená místní TLS identita

- Převzetí: místní server potřebuje unikátní certifikát/privátní klíč mimo image. `LocalTlsIdentity` drží jednu omezenou dvojici PEM a vyžaduje fyzickou servisní bránu pro atomický zápis; `EspIdfLocalTlsStorage` používá oddělený `wand_tls/server`. mbedTLS/PSA validátor ověřuje parse a shodu klíče s certifikátem a odvozuje SHA-256 DER SPKI pro QR. Chyba úložiště či kryptografické kontroly brání použití identity. RAM klíče se při uvolnění maže. Limity 2048/1024 bajtů (certifikát/EC klíč) udržují záznam pod jednou NVS datovou stránkou. Nejasný zápis vyžaduje nové načtení a ověření uloženého SPKI před vydáním QR. Rozsah: model/adaptéry, test/CI, CMake/Arduino filtr, README a stav.
- Ověření 30. 9.: MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `local_tls_identity_test` (servisní brána, restart, truncation, nadměrná délka, vnitřní NUL, neshoda, nulový pin, chyba čtení/zápisu a nový load po nejasném zápisu). Po revizi limitů postupně znovu prošly `pio run -e esp32-s3-idf -s` i `pio run -e esp32-s3-arduino-serial -s`. Host test navíc ověřil největší 3080bajtový záznam a dvě rotace. YAML parser a `git diff --check` prošly. Hostitelský validátor je fake; skutečná kryptografie/NVS musí projít na desce po B1.
- Doručení D91: commit `8b51cabaf52e509faf7af9cf3c030b687cf526ed` ověřeně pushnut; [CI 36757780334](https://github.com/Blato58/DildoVander/actions/runs/36757780334) prošlo hostitelskými testy i firmware buildy. [PR #55](https://github.com/Blato58/DildoVander/pull/55) sloučen jako `769903023806a2819c12cba8294b02f865e5df8f`, ověřen na `origin/main`.
- Neprovedeno: servisní USB zápis, fyzické zabezpečení flash, TLS handshake a WSS parser/runtime. Prototyp NVS má hranice již přijaté v plánu §9; šifrování a eFuse se tím nezapínají. Další krok: uzavřený TLS/WSS server a fronta autorizovaných událostí do řídicí úlohy.
### D90 – 30. 9. 2026 – E4.4j hardwarově podmíněný párovací RNG

- Převzetí: doplnit produkční `PairingRandomPort` pro právě 32 bajtů lokálního tokenu. `EspIdfPairingRandom` dovolí `esp_fill_random` pouze při připojené Wi-Fi STA před i po generování; při výpadku výstup vymaže. Nepřepíná ADC zdroj během RF/audio provozu. Párovací AP bude potřebovat vlastní doloženou bránu aktivní entropie. Rozsah: adaptér, hostitelský test se stuby IDF, CMake/Arduino filtr, CI, README a stav.
- Ověření 30. 9.: MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `espidf_pairing_random_test` se stuby veřejných IDF API (chybějící spojení, platná délka, výpadek při generování a vymazání). Postupně prošly `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s`. `git diff --check` čistý. Test stubů dokládá bránu adaptéru, nikoli kvalitu hardwarového RNG. Žádné nahrání do desky ani živé párování.
- Doručení D90: commit `b3795f82694e34ad375a8c092c88250795875a95` ověřeně pushnut. [CI 36755796213](https://github.com/Blato58/DildoVander/actions/runs/36755796213) prošlo hostitelskými testy i oběma firmware buildy; [PR #54](https://github.com/Blato58/DildoVander/pull/54) sloučen jako `e1daface837f80f30c48869bbaa6b83030beb4ed`, ověřen na `origin/main`.

### D89 – 30. 9. 2026 – E4.4i jediný místní control socket

- Převzetí: D88 ověřuje token, ale bez WSS transportu zatím není určeno, jak odmítnout druhý telefon a uvolnit neúplný handshake. Čistý `LocalSocketAdmission` drží nanejvýš jednu rezervaci/aktivní FD, autentizuje před rezervací, potvrzuje handshake do 10 s a při zavření rozlišuje rezervovaný a aktivní socket. Generace rezervace brání opožděnému callbacku uvolnit znovu použité číslo FD; vypršení vrátí starý handle, který musí transport zavřít před dalším přijetím. Žádný callback nesmí přímo měnit řízení; budoucí server předá ztrátu aktivního socketu řídicí úloze. Rozsah: model, hostitelský test/CI, firmware README/CMake a tento stav.
- Ověření 30. 9.: MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `local_socket_admission_test` (jediný socket, expirace, opožděný callback při znovu použitém FD, rozlišení zavření). Z `kufr/firmware` postupně prošly `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s`. První Arduino build po generačním handle odmítl složené přiřazení; explicitní konstruktor problém opravil a oba profily poté prošly. [CI 36730263179](https://github.com/Blato58/DildoVander/actions/runs/36730263179) potvrdilo `host-tests` a `firmware-build`.
- Doručení: implementační commit `ecebb09e5de0340356d7af342053e8f9020e4fcf`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-local-wss-admission`. [PR #52](https://github.com/Blato58/DildoVander/pull/52) sloučený jako `0de5cdcb21b55f678e61488e7f9de47f0fdd8a8b` na `main`.
- Neprovedeno: skutečný `esp_https_server`, pre-handshake bearer callback, provisionovaný TLS certifikát/klíč, servisní AP a fyzický běh B1. IDF 6.1 má WebSocket pre-handshake podporu vypnutou, musí se zapnout až s uzavřeným serverovým adaptérem; samotný handler autorizaci před upgradem nenahradí.

### D88 – 30. 9. 2026 – E4.4h místní bearer hlavička

- Převzetí: telefonní WSS handshake potřebuje převést textový `Authorization` na 256bitový token bez volného formátu. Plán nyní určuje přesně 64 malých hexadecimálních znaků. Čistý `LocalBearerAuthenticator` přijímá pouze úplnou `Bearer ` hlavičku, dekóduje token do krátkodobého bufferu, ověří ho přes D86 a buffer vymaže. Simulátor přijímá stejný tvar tokenu. Rozsah: plán, parser, hostitelský test/CI, firmware README/CMake, simulátor a tento stav. Zatím se nevolá z firmwarového socketu.
- Ověření 30. 9.: MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `local_bearer_authenticator_test` (správný token, změněný token, neúplná/dlouhá hlavička, prefix a nepovolený hex). Z `kufr/firmware` postupně prošly `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s`. Sestavený C++ bridge a `npm test` v `kufr/simulator` prošly včetně WSS TLS/auth/media scénářů. Původní test konverzace při skoku fake času 500→831 ms spouštěl 250ms watchdog; vložený průchod řídicí smyčkou v 700 ms odpovídá provoznímu 10ms ticku. YAML firmware workflow se načetl; [CI 36725668382](https://github.com/Blato58/DildoVander/actions/runs/36725668382) potvrdilo `host-tests` a `firmware-build`.
- Doručení: implementační commit `b5ba251e791b7c539c46c99cf310808a91446be1`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-local-bearer-auth` včetně aktualizovaného `main` na `26b5e6bf65e74a3b6dbd34ac9ada9da6c765f2a7`. [PR #50](https://github.com/Blato58/DildoVander/pull/50) sloučený jako `027d330963a095a3dc19d4cfce9ecebcdf1ef24d` na `main`.
- Neprovedeno: skutečný TLS/WSS handshake, limit jednoho klienta, service AP, fyzická brána a běh na desce.

### D87 – 30. 9. 2026 – E4.4g ESP-IDF místní párovací adaptéry

- Převzetí: D86 měl pouze fake SHA-256 a paměťové úložiště. ESP-IDF profil nyní sestavuje `EspIdfSha256` přes PSA Crypto a `EspIdfLocalPairingStorage` s vlastním NVS namespace/key `wand_pairing/local`, odděleným od backendového `wand_identity/device`. Neúplný/nečitelný záznam a nedostupná NVS odmítají čtení; zápis přijímá jen přesnou délku záznamu a vyžaduje commit. Rozsah: oba adaptéry, CMake, vyloučení IDF souborů z Arduino profilu, firmware README a tento stav.
- Ověření 30. 9.: z `kufr/firmware` prošly postupně `pio run -e esp32-s3-idf -s` i `pio run -e esp32-s3-arduino-serial -s`. První souběžný pokus o oba profily narazil na společný `.pio/build` a nepočítá se jako ověření. D86 prošel [CI 36721137045](https://github.com/Blato58/DildoVander/actions/runs/36721137045) v úlohách `host-tests` a `firmware-build` a [PR #48](https://github.com/Blato58/DildoVander/pull/48) je sloučený jako `2f381b3d36b3b87b09697efc04119b71bf6912c9` na `main`.
- Doručení: implementační commit `c1ba9bc2a413c456848328ad0e6858fe94917c3d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push větve `codex/kufr-local-pairing-adapters` včetně aktualizovaného `main` na `48bcd2bca80bf76c017bc231ab9f81bf7ec863f8`. [CI 36723167583](https://github.com/Blato58/DildoVander/actions/runs/36723167583) prošlo v úlohách `host-tests` a `firmware-build`; [PR #49](https://github.com/Blato58/DildoVander/pull/49) sloučený jako `14bf0d92599fd2e921a2668f6eb93a3a7a09f624` na `main`.
- Neprovedeno: běh PSA/NVS na desce, fyzický servisní generátor tajemství, hardwarově podmíněný RNG, TLS identita a WSS spojení. Adaptéry samy zatím nepřijímají žádné síťové požadavky.

### D86 – 30. 9. 2026 – E4.4f místní párovací záznam

- Převzetí: skutečný místní WSS bearer nemá vlastní trvalý stav ani jednorázové párování. Čistý `LocalPairingCredential` zavádí samostatný verzovaný záznam: po servisní bráně drží pouze otisk nejméně 16bajtového párovacího tajemství, po jeho ověření vydá 256bitový token z portu hardwarové entropie, uloží pouze otisk tokenu a tajemství odstraní. Entropii vstupního tajemství musí zajistit budoucí servisní generátor. Další párování vyžaduje novou servisní bránu; poškozený záznam a selhání zápisu odmítnou přístup. Nezasahuje do backendového `DeviceIdentity`. Rozsah: čistý model, hostitelský test s fake porty, firmware CI/README a tento stav.
- Ověření 30. 9.: z `kufr/firmware` MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `local_pairing_credential_test` (servisní brána, jednorázové párování, restart, selhání RNG/zápisu a poškozený záznam včetně délky, hlavičky, stavu a otisků); `pio run -e esp32-s3-idf -s` prošel. Z kořene repozitáře prošlo načtení YAML workflow přes `yaml.safe_load`; [CI 36721137045](https://github.com/Blato58/DildoVander/actions/runs/36721137045) potvrdilo `host-tests` a `firmware-build`.
- Doručení: implementační commit `257b4ca749dda4aa25d571315ca615820746844e`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-local-token-record`. [PR #48](https://github.com/Blato58/DildoVander/pull/48) sloučený jako `2f381b3d36b3b87b09697efc04119b71bf6912c9` na `main`.
- Neprovedeno: produkční SHA-256, RNG a NVS adaptéry, fyzická servisní brána/B1, TLS klíč/certifikát a autentizovaný WSS handshake. Další softwarový krok je adaptér kryptografie a odděleného NVS záznamu, poté uzavřený TLS/WSS vstup; model sám neopravňuje žádný socket.

### D85 – 30. 9. 2026 – E4.4e sdílené boot ID

- Převzetí: IDF `DeviceControl` používal prázdné boot ID, zatímco síťová úloha ho vytvářela nezávisle až po Wi-Fi. `app_main` nyní před startem rádia dočasně zapne interní zdroj entropie ESP-IDF, vygeneruje jedno UUID v4, zdroj vypne a stejné ID předá řízení i backendové úloze. Backend už boot ID nemění po reconnectu; idempotency key dál vzniká až po připojení Wi-Fi. Rozsah: IDF inicializace, konstruktor sítě, CMake závislost, firmware README a tento stav. Není přidána místní WSS služba ani povolen fyzický pohyb; kalibrace zůstává nulová.
- Ověření 30. 9. z `kufr/firmware`: `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s` prošly. [CI 36717463088](https://github.com/Blato58/DildoVander/actions/runs/36717463088) prošlo v úlohách `host-tests` a `firmware-build`. Hostitelský běh hardwarového RNG ani fyzická deska/B1 nejsou k dispozici.
- Doručení: implementační commit `2db29a2a5d07463efb6020c1d85e9504f723ad48`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-shared-boot-identity`. [PR #47](https://github.com/Blato58/DildoVander/pull/47) byl po kontrole diffu a CI sloučen jako `0d988ce24d5c09f9807072e2093223526b1f6228` na `main`.
- Neprovedeno: místní WSS/TLS identita a token, autentizovaný producent, výstup replik a připojení brány do IDF smyčky. Další krok: bezpečné místní tokenové úložiště a WSS handshake po servisním provisioningu; bez B1 zůstane fyzické provisionování a runtime ověření blokováno.

### D84 – 30. 9. 2026 – E4.4d místní textová brána

- Převzetí: předchozí čisté modely neměly jediné volací místo pro lease, generation, text, frontu a asynchronní chybu. `LocalConversationGateway` na řídicí úloze ověřuje autentizovaný `LocalControlSession`, čerstvý lease a autoritativní snapshot. Duplicitní tah znovu neodesílá; nový tah potvrdí před předáním síti a při okamžitém odmítnutí fronty vrátí pouze tento tah. Síťová úloha předává terminální selhání s místním `conversationId`, `clientTurnId` a verzí snapshotu přes omezenou schránku; podmíněné vyzvednutí neztratí zprávu jiného sezení. Brána při shodě aktivní tah uvolní, ale ID nerecykluje. Změna snapshotu zneplatní i předanou repliku. Síťové zrušení je podmíněné ID rozhovoru a verzí přijatého snapshotu, aby opožděná stará brána nevymazala tah nového vlastníka. `EspIdfBackendNetwork` implementuje port, hlásí odhození již přijatého čekajícího textu při výpadku sítě a během čekajícího cancel nebo prezentace odmítá nový tah/opening. Dokončení prezentace drží přijímací bránu zavřenou až do zařazení zrušení. Rozsah: firmware model, síťové předání, hostitelské testy, README, CI a tento stav.
- Ověření 30. 9. z `kufr/firmware`: MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil rozšířený `local_control_session_test` a `conversation_text_inbox_test`. Testy kryjí odmítnutí fronty a opakování původního ID, duplicitní ACK bez síťového tahu, expirovaný lease, cancel a čekající síťové zrušení, Stop, pozdní výsledek, asynchronní selhání, nevratnost použitého ID, cizí/starou chybovou zprávu a opožděné zrušení po převzetí novým vlastníkem. `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s` prošly. [CI 36715741030](https://github.com/Blato58/DildoVander/actions/runs/36715741030) prošlo v úlohách `host-tests` a `firmware-build`.
- Doručení: implementační commit `7aa9d3fabf0dff3e3019d9e1e64b7c6ba7599b73`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-local-conversation-gateway`. [PR #46](https://github.com/Blato58/DildoVander/pull/46) byl po kontrole diffu a CI sloučen jako `e0647f06a1b9e6c0e07b41b7ef768e5805fb9e65` na `main`.
- Neprovedeno: instanciace brány ve skutečné IDF smyčce, WSS/TLS autentizace a parser, místní opening producent, přehrávání replik, fyzická deska/B1. Další krok: autentizovaný transport a bezpečné předání událostí vlastníkovi řízení; brána není běžící WSS server.

### D83 – 30. 9. 2026 – E4.4c společná generation rozhovoru

- Převzetí: `ConversationSession` a `DeviceControlRuntime` dosud měly oddělené čítače. Přidána neměnící kontrola zrušení, podmíněné zvýšení generation vlastníkem řízení pouze s platným ručním lease a monotónní synchronizace sezení po Stop/ztrátě lease. Rozsah: čisté C++ modely, hostitelský test, firmware CI, README a tento stav. Skutečná WSS událost ani odpovědní port se zatím nezapojuje.
- Ověření 30. 9. z `kufr/firmware`: MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil rozšířený `local_control_session_test` a stávající `conversation_session_test`. Nové scénáře kryjí zrušení, Stop, uvolnění lease, pozdní výsledek a odmítnutí expirovaného lease přesně na hranici 6000 ms před Tick. `pio run -e esp32-s3-idf -s` i `pio run -e esp32-s3-arduino-serial -s` prošly po poslední úpravě. `git diff --check` a [CI 36709036203](https://github.com/Blato58/DildoVander/actions/runs/36709036203) v úlohách `host-tests` a `firmware-build` prošly; deska/B1 neověřena.
- Doručení: implementační commit `9eb9e52928f449aa23d97ad4e4a10924a65d344f`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-conversation-generation`. [PR #45](https://github.com/Blato58/DildoVander/pull/45) byl po kontrole diffu a CI sloučen jako `0b089139d068e0f98a7ed4a82b3b6f80b7267af8` na `main`.
- Neprovedeno: živý autentizovaný vstup, automatické předání lifecycle událostí mezi úlohami, deska/B1 a WSS/TLS. Další krok: jediná řídicí brána pro text/cancel/reply s explicitním vlastníkem lease a síťovým portem.

### D82 – 30. 9. 2026 – E4.4b dvoufázové přijetí textu

- Převzetí: při přímém volání `ConversationSession::SubmitText` se ID spotřebovalo před možným odmítnutím síťové fronty. Čistý model nyní nabízí neměnící `ValidateText`; D82 hostitelsky ověřilo kontrolu před zařazením a potvrzení po něm. D84 toto pořadí uvnitř jediné brány změnilo na kontrolu, potvrzení, zařazení a návrat dosud nezařazeného tahu při okamžitém odmítnutí. Tím se síťová úloha nedostane k tahu dříve než jeho místní evidence. Rozsah D82: model sezení, hostitelský integrační test se skutečnou `ConversationTextInbox`, firmware README a tento stav. Síťová/WSS produkce se v D82 nepřipojovala.
- Ověření 30. 9. z `kufr/firmware`: MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `conversation_session_test` a `conversation_turn_correlation_test`; druhý test doložil odmítnutí obsazenou frontou, opakovaný pokus bez ztráty ID, duplicitní retry po vyzvednutí původního tahu bez dalšího zařazení a původní korelaci odpovědi. `pio run -e esp32-s3-idf -s` i `pio run -e esp32-s3-arduino-serial -s` prošly. [CI 36706745630](https://github.com/Blato58/DildoVander/actions/runs/36706745630) prošlo v úlohách `host-tests` i `firmware-build`. Deska/B1 neověřena.
- Doručení: implementační commit `84048e3881879e0424349a322e005fe994fbf7fd`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-text-admission`. [PR #44](https://github.com/Blato58/DildoVander/pull/44) byl po kontrole diffu a CI sloučen jako `481979ccf358f2345a4ebd9ac658f4971b31f485` na `main`.
- Neprovedeno: jednotná generace mezi `ConversationSession` a `DeviceControlRuntime`, skutečný místní autentizovaný producent, WSS/TLS, fyzické výstupy. Další krok: synchronizace životního cyklu v jediné řídicí úloze; až potom napojení textu a odpovědi.

### D81 – 30. 9. 2026 – E4.4a místní řízení lease

- Převzetí: po D80 lze bez desky ověřit vlastnictví jednoho místního control spojení. Rozsah: čisté C++ pravidlo `hello`/manual lease/heartbeat/odpojení nad existujícím `DeviceControlRuntime`, hostitelský test, firmware CI, README a tento stav. Objekt smí vzniknout až po budoucí autentizaci TLS/WSS; žádný autentizační adaptér ani socket v tomto kroku nepřibývá.
- Ověření 30. 9. z `kufr/firmware`: MSVC `cl /std:c++14 /EHsc /W4 /WX` a spuštění nového `local_control_session_test` i stávajícího `device_control_runtime_test` prošly. První běh nového testu odhalil očekávanou nutnost fyzického zotavení po expiraci; test nyní ověřuje odmítnutí převzetí před zotavením. Další kontrola odhalila, že pozdní odpojení starého socketu mohlo zrušit lease nového vlastníka; podmíněné uvolnění podle přesného lease ID a dvousession test chybu opravily. `pio run` i `pio run -e esp32-s3-arduino-serial` prošly po opravě. PR #43 prošel `host-tests` i `firmware-build` a byl sloučen jako `0a09957ed122e27f6f1b05b4016a41fe51a48984` na `main`. Deska nebyla ověřena.
- Neprovedeno: skutečný WSS handshake a token, napojení na IDF control smyčku, textový producent, deska/B1. Další krok je transportní/autorizační adaptér a bezpečné předání jeho událostí vlastníku řízení.

### D80 – 30. 9. 2026 – E4.3d2b5 místní ID rozhovoru

- Převzetí: po D79 zůstávalo místní `conversationId` mimo síťovou cestu. Výsledek neměl vazbu na místní sezení a nový opening mohl následovat starý text z fronty. Rozsah: textová a odpovědní schránka, síťové opening/tah předání, hostitelské testy, firmware README a tento stav. JSON/NVS kontrakt se nemění; WSS a autorizovaný producent stále chybí.
- Ověření 30. 9. z `kufr/firmware`: MSVC `cl /std:c++14 /EHsc /W4 /WX` a spuštění testů textové/odpovědní schránky, runtime stavu a složené korelace prošly po opravě pořadí opening/text. `pio run` a `pio run -e esp32-s3-arduino-serial` prošly; ESP-IDF build byl opakován po přidání brány příjmu. Místní ID validuje UUID a zachová původní zápis pro odpověď. Nový opening zahodí čekající text jiného sezení; brána nepřijme cizí tah ani při neúspěšném backendovém startu. PR #42 prošel `host-tests` i `firmware-build` a byl sloučen jako `fb8fc77c6258625fa7631e3ce093e6931f9d91f8` na `main`. Deska nebyla ověřena.
- Neprovedeno: runtime autentizace WSS, manual lease a textový producent, fyzická deska/B1 a skutečný audio výstup. Další nezávislý krok: místní autorizační/lease brána a její napojení na text/opening; HW akceptace zůstává oddělená.

### D79 – 30. 9. 2026 – E4.3d2b4 korelace místního textového tahu

- Převzetí: po D78 je další nezávislý softwarový krok zachovat `clientTurnId` ze smlouvy `conversation.text` v čekajícím textu, backendovém požadavku a předávané replice. Dosavadní síťová úloha při každém tahu generovala jiné ID, takže budoucí místní klient by výsledek nemohl bezpečně přiřadit. Rozsah: firmware `conversation`/`backend_access`, hostitelské testy, firmware CI, README a tento stav. Nezavádí WSS server, audio port, GPIO ani skutečného místního producenta.
- Ověření 30. 9. z `kufr/firmware`: Visual Studio Build Tools `cl /std:c++14 /EHsc /W4 /WX` a spuštění `conversation_text_inbox_test`, `conversation_reply_inbox_test`, `conversation_runtime_state_test` a složeného `conversation_turn_correlation_test` prošly. `pio run` (ESP-IDF 6.1) a `pio run -e esp32-s3-arduino-serial` prošly po korekci velkých písmen/nulového UUID. Místní `clientTurnId` přijímá i velká písmena, do backendu jde kanonický malý zápis, pro místní výsledek zůstává původní podoba. Nulové UUID se odmítá; idempotency key zůstává oddělený. JSON/NVS kontrakt se nemění. PR #41 prošel `host-tests` i `firmware-build` a byl sloučen jako `b810936dacfa28a6fa1979ad52b1cb890b858d6f` na `main`.
- Neprovedeno: autorizované WSS přijetí `conversation.text`, vazba místního `conversationId` na backendové sezení, odeslání `conversation.result` klientovi, TTS/I2S, fyzický port, deska/B1 a runtime TLS. Další softwarový krok je vlastník autentizovaného místního sezení/lease a mapování jeho ID na backendové sezení; fyzický výstup čeká na B1.

### D78 – 30. 9. 2026 – E4.3d2b3 předání backendové repliky

- Převzetí: po sloučení D77 byla nejbližší nezávislá softwarová mezera zahozená textová replika v `EspIdfBackendNetwork::RunTurn`. Rozsah: `ConversationReplyInbox`, síťové předání, hostitelský test, firmware CI, README a tento stav. Schránka přijme jedinou repliku do 500 UTF-8 znaků/2000 bajtů proti přesnému snapshotu. `TakeReply` ji vydá s odvolatelným tokenem; další tah a prezentace čekají až na `FinishReply`, včetně vstupu zařazeného během předchozího pollu. Terminální návrh zahodí souběžně zařazený další vstup. Změna snapshotu či `QueueCancel` token zneplatní. Ukončení backendového sezení po `no_selection` ji nemaže, aby výstup mohl dohrát již validovanou repliku.
- Ověření 30. 9. z `kufr/firmware`: Visual Studio Build Tools `cl /std:c++14 /EHsc /W4 /WX` a spuštění `conversation_reply_inbox_test` prošly (jediná replika, běžící token, dokončení, starý snapshot, zrušení, UTF-8 a limity včetně 2000 bajtů). První `pio run` selhal při linkování, protože nový zdroj chyběl v explicitním `src/CMakeLists.txt`; seznam byl doplněn. Opravený `pio run`, jeho opakování po zpřísnění zrušení a `pio run -e esp32-s3-arduino-serial` prošly. YAML workflow byl načten a `git diff --check` prošel; vzdálený CI běh je doložen níže.
- Neprovedeno: skutečný konzument `TakeReply` v IDF smyčce, TTS/playback, autorizovaný místní opening/text vstup, kalibrované pohony, deska/B1, hardware a živý backendový test. Tato schránka neprokazuje slyšitelnou odpověď. Další krok je ověřený místní zdroj události a výstupní port; bez B1 nelze vybrat a ověřit fyzické piny ani zapojení.
- Implementační commit: `a7c2d08` na `codex/kufr-reply-handoff`, evidenční commit `c78b9a8`. `./scripts/Push-CurrentBranch.ps1` potvrdil 30. 9. push obou commitů do `origin/codex/kufr-reply-handoff`. [PR #39](https://github.com/Blato58/DildoVander/pull/39) prošel hostitelským i firmware CI [36690324250](https://github.com/Blato58/DildoVander/actions/runs/36690324250) a byl sloučen do `main` jako `9292243`; head PR je předkem ověřeného `origin/main`.

### D77 – 30. 9. 2026 – E4.3d2b2 autonomní journal a vlastník prezentace

- Převzetí: po D76 byl další proveditelný krok hostitelský životní cyklus bez GPIO. Rozsah: `DeviceControl`/`DeviceControlRuntime`, trvalý journal, `WandPresentation`, hostitelské testy, firmware CI, plán kapitola 5, firmware README a tento stav. Nový `ResolveRestored` zachovává masku pouze při potvrzeném návratu; původní `Resolve` dále maže bit po ručním výběru. Formát NVS záznamu se nemění, migrace není potřeba. Vlastník smyčky váže prezentaci před autonomním spuštěním a při Stop volá její vypnutí ihned; snapshot je nedostupný až do ukončení nebo fyzické kontroly.
- Ověření 30. 9. z `kufr/firmware`: Visual Studio Build Tools `cl /std:c++14 /EHsc /W4 /WX` a spuštění `autonomous_presentation_lifecycle_test`, `persistent_command_journal_test`, `device_control_runtime_test`, `wand_presentation_test` prošly; `turn_replay` se stejnými přepínači sestaven. Testy kryjí opakovaný normální návrat, atomickou dostupnost přes restart, starý/expirovaný návrh, chyby journalu, okamžitý Stop portu a zotavení před dalším Tick. `pio run` a `pio run -e esp32-s3-arduino-serial` prošly. `git diff --check` prošel. Firmware CI dostalo nový hostitelský test, vzdálený běh zatím neproběhl.
- Neprovedeno: fyzický servo port, potvrzení polohy, hardware a měření B1, nahrání do desky, místní autorizovaný opening/text producent, WSS, audio ani živé backendové volání. `espidf_main.cpp` zatím nemá skutečný port, proto autonomní transakci nespouští. Další je autorizovaný vstup a bezpečné předání replik/návrhu vlastníkovi smyčky; B1 blokuje až fyzické pohony.
- Implementační commit: `236411b` na `codex/kufr-presentation-lifecycle`; evidenční commit `a0eb5b9`. `./scripts/Push-CurrentBranch.ps1` potvrdil 30. 9. úspěšný push a shodu `origin/codex/kufr-presentation-lifecycle` s `a0eb5b9`.
- PR [#38](https://github.com/Blato58/DildoVander/pull/38) prošel hostitelským i firmware CI [36686604923](https://github.com/Blato58/DildoVander/actions/runs/36686604923) a byl sloučen do `main` jako `675cde8`; `d15a82f` z PR je předkem ověřeného `origin/main`.

### D76 – 30. 9. 2026 – pouze Sol a oprava opakujících se otázek

- Majitel zvolil pouze Sol po živém testu Luny. Backend i ruční workflow odmítají jiné dialogové modely; eval používá Sol. Prompt zakazuje mechanické potvrzování jména, znovu položené zodpovězené otázky a dva dotazy v jedné replice.
- D75: PR #35 sloučeno do `main` jako `c3d2c535ba00356f7710e432201745495b8d5832`; firmware CI a offline eval prošly. Nasazení 36651086166 prošlo. Pět skutečných Luna tahů přes veřejné API skončilo návrhem přihrádky 3; replay stejného opening vrátil tutéž operaci, DELETE sezení prošel (36652057592). Návštěvníkovy odpovědi vznikaly až po přečtení skutečné AI repliky.
- Hodnocení: Luna držela svatební historku, ale opakovala zodpovězený dotaz na výmluvu, pokládala dva dotazy najednou a místo pointy komentovala příběh. To není splněná akceptace přirozeného humoru.
- Skutečný backendový návrh prošel hostitelským parserem/guardem a simulovaným raise/lower/complete pro přihrádku 3. Expirovaný návrh a readyMask=0 byly odmítnuty bez simulovaného pohybu. Replay používá zachycený čas příjmu; není důkaz ESP32 sítě ani fyzického serva.
- Ověření změny: Python eval 20 testů a Release backend testovací program prošly; Luna a Astra jsou výslovně odmítnuty. Živý Sol a restart paměti jsou doložené níže. Nákladové rezervace/odhady nejsou skutečně vyúčtované částky OpenAI; původní limit testů 1 USD a měsíční 10 USD se nemění.

- Doručení: `0d7a26e` ověřeně pushnuto; [PR #36](https://github.com/Blato58/DildoVander/pull/36) po úspěšném offline CI sloučeno jako `78b563ef57cdc68d352d8aa808b7bd846d3d096f`, ověřeno na origin/main. Main backend CI 36652691648 prošlo. [Nasazení Sol 36652694673](https://github.com/Blato58/DildoVander/actions/runs/36652694673) úspěšné, server zůstává `responses` / `gpt-6.1-sol`.
- Živá akceptace softwarového toku: po restartu stejné eveningId a 5 výměn; na pouhé jméno Sol sám vybavil sekačku při prvním tanci a po potvrzení fotky zahrady. Pět Sol tahů skončilo správným Výborem pro špatné nápady / přihrádkou 3. Závěr „Za stovku už jim neposekáš zahradu, rovnou rozdělíš majetek“ navázal na skutečné předchozí odpovědi. Je to kvalitativní vzorek, nikoli záruka humoru na akci.
- Replay Sol návrhu produkčním C++ parserem/guardem a čistou prezentací prošel; expirace +120 s a readyMask=0 odmítnuty bez simulovaného pohybu. Luna měla navíc nesoulad názvu hůlky (katalog 6) a návrhu (3); číselný guard tento sémantický nesoulad volného textu nekontroluje. Sol v novém běhu název i číslo spojil správně.
- Cleanup: terminální Sol sezení DELETE prošlo; další opening a explicitní DELETE nedokončeného sezení také. Paměť stále 11 výměn / 1811 B. Smazání přesného syntetického eveningId a následný status 36653597988 potvrdily nové eveningId a 0 výměn. Automatický firmware timeout nebyl zkoušen na běžící desce.
- Rozpočet: 5 Luna + 6 Sol unikátních živých tahů v této sérii; včetně konzervativního historického základu kontrolní rezervace 0,884808 USD z povoleného 1 USD. Nejde o skutečnou fakturu, historický základ částečně vychází ze sdělení majitele. Měsíční limit 10 USD nezměněn. Přesný přepis, workflow ID, korelace a výpočet jsou v [živém reportu](../ai-eval/reports/2026-09-30-live-backend-dialogue.md).

### D75 – 30. 9. 2026 – autonomní pevná rekvizita, backendové funkce a živý dialog

- Rozhodnutí majitele: pouze Sol/Luna; žádná Astra v nových bězích. AI sama vybírá hůlku bez obsluhy, servo ji vysune a zase zasune. Hůlka zůstává v přihrádce. Plán aktualizuje provoz, fyzickou hranici a kritéria E6/E8/E9; starý ruční kontrakt zůstává kompatibilní.
- Backend: tři nativní Responses funkce s přísným schématem, výběr omezený readyMask, nejméně čtyři otázky před výběrem, přípustné ukončení kdykoli. Opravený hovorový prompt a více doplňujících otázek. Terminální replika zůstává pollovatelná, nové texty/přepisy se odmítají; následný DELETE uvolní sezení bez mazání paměti. Model je konfigurovatelný Sol/Luna, tarif Standard výslovný, konzervativní tokenový odhad není účetní údaj.
- Živý testovací nástroj: nový main-only workflow `kufr-backend-interactive-dialogue.yml`, jeden ručně řízený start/reply/finish/status/clear za běh. Tajemství zůstávají v environmentu runneru; artefakt obsahuje jen syntetický přepis a veřejnou korelaci. Další text se vybírá až po skutečné odpovědi. Starý pevný smoke nyní ověřuje pouze opening, ne humor.
- Lokální ověření v managed worktree `kufr-party-memory`: `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release` prošel včetně terminálního textu/přepisu, replay a Luna rezervace; `node kufr/backend/tests/smoke.mjs` prošel; Python eval 20 testů, interaktivní klient 9 testů, deploy konfigurace 3 testy prošly. JSON fixtures, YAML workflow a místní odkazy dotčených README/plánu/stavu se parsují a existují. `git diff --check` bez chyb.
- Firmware: bounded proposal parser + korelace/katalog/maska/UTC expirace, monotónní pending návrh, explicitní opening a DELETE s opakovatelným klíčem. Neúspěšný DELETE zachová sezení a blokuje další start; ztracená odpověď POST se jednou opakuje se stejnou obálkou, poté se sezení uzavírá. Lokální nečinnost 90 s, serverových 15 minut zůstává fallback. Odmítnutý či vypršelý návrh a dokončení prezentace mají uzavírací cestu. `WandPresentation` s portem řídí raise/show/lower; normální návrat obnoví dostupnost, Stop (i v klidu), timeout, změna bezpečnosti či zpětný čas vyvolají fault. Pohybové timeouty 3 s, prezentace 5 s jsou softwarové meze, nikoli změřená kalibrace.
- Firmware ověření pracovníkem: `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s` prošly. MSVC `/utf-8 /std:c++14 /W4 /WX /EHsc`: compile+execute `turn_operation_codec_test`, `conversation_text_codec_test`, `pending_proposal_test`, `wand_presentation_test` prošly. `turn_replay.cpp` se sestavil a syntetický JSON prošel skutečným parserem/guardem do simulovaného raise/lower/complete. CI obsahuje stejné čtyři testy a kompilaci replay nástroje. Nezávislá kontrola našla výše opravené retry/expiry chyby; úplný síťový lifecycle v běžícím ESP32 zatím testován nebyl.
- Mezera: `espidf_main` dosud nevolá opening/presentation API; chybí autorizovaný producent vstupu, playback, kalibrovaný fyzický port s journal/snapshot přechody a potvrzením polohy, Tick a propojení dokončení. Žádné GPIO, flash, snímače ani audio akceptace. Hostitelský model není živý ESP32. Backend `74d32b7` a firmware `bf43625` byly pushnuty a sloučeny v PR #35; CI/deploy a skutečný adaptivní test jsou doloženy v D76. Rozpracovaná fyzická integrace zůstává dalším krokem.

### D74 – 30. 9. 2026 – party dialog a trvalá paměť večera – backend a syntetické dialogy ověřeny

- Zadání: majitel chce přirozenou češtinu pro připité dospělé kamarády, otázky na tělo, kufr začínající bez představování, paměť jmen/odpovědí celého večera. Výslovně zvolil vlastní backend pro ukládání a schválil celkem 1 USD na placené syntetické srovnání při dosavadním limitu 10 USD/měsíc.
- Rozsah: sdílený prompt a přirozené opening/return/boundary fixtures; samostatný EveningMemoryStore SQLite, device scope, limit 2000 výměn / 2 MB, lexikální výběr kontextu 16000 znaků, trvalost přes restart a konec sezení. Nové opening/status/clear HTTP trasy v OpenAPI, dynamická rezervace přidaného kontextu, adaptivní eval Sol/Astra. Plán §6/§9 mění dosavadní RAM-only retenci pro text; audio se nearchivuje. Žádná migrace technického journalu.
- Dosavadní ověření: Release a Debug backend build bez varování; původní a nové backend testy včetně restartu, jména mimo nedávné okno, scope, row/byte cap, opening retry a clear při pozdní odpovědi prošly. HTTP smoke ověřil i uppercase UUID při smazání, cizí scope, retry a opening bez textu. Offline Python eval 17 testů; npm ci/test v contracts prošly. Nezávislá revize odhalila zápis paměti až po publikaci a normalizaci UUID; obojí opraveno, finální opakování prošlo. Cílený cs_test_impact neposkytl příkaz (ambiguous_solution při 0 objevených solutions); použit skutečný konzolový test runner z README.
- Doručení: commit `8977c5c` ověřeně pushnut; [PR #31](https://github.com/Blato58/DildoVander/pull/31) po zeleném offline CI sloučen jako `3693379`. První [main backend CI 36645715352](https://github.com/Blato58/DildoVander/actions/runs/36645715352) odhalilo závod stávajícího testu mezi terminálním stavem a uvolněním provider slotu. Testy nyní čekají i na dokončení finally; v této opravě se cena/limity nemění. Oprava `20f120a` a oddělení odhadu od billingu `4323ba1` byly pushnuty a sloučeny v [PR #32](https://github.com/Blato58/DildoVander/pull/32) jako `120fed5`; [main backend CI 36646632571](https://github.com/Blato58/DildoVander/actions/runs/36646632571) prošlo včetně HTTP smoke a publish. Schválený [placený eval 36645718827](https://github.com/Blato58/DildoVander/actions/runs/36645718827) dokončil všech 6 dialogů / 30 volání; původní odhad podle Standard ceníku je 0,325139 USD (tier tehdy nebyl uchován), skutečně účtovaná cena není ověřená (majitel v OpenAI vidí méně než cent); oba modely začaly samy a ověřily návrat Petra podle historie. Přepisy jsou v [eval reportu](../ai-eval/reports/2026-09-30-party-dialogue.md). Lenka neaktivovala odmítnutí tématu a simulátor odpověděl mimo otázku; toto není důkaz této větve. Další iterace promptu omezuje formální závěry a persona záměrně odmítne první osobní otázku. Druhý [běh pouze Sol 36646636262](https://github.com/Blato58/DildoVander/actions/runs/36646636262) s nastaveným stropem 674861 mikro USD dokončil další 3 dialogy / 19 volání: ověření Petra, doplňující otázka a změna tématu po odmítnutí u Lenky. Žádné další živé volání se nespouští.
- Oprava rozpočtu podle druhého živého důkazu: všechny odpovědi vrátily `fast`, dosavadní odhad omylem používal Standard. Report zachovává původní JSON a opravuje ceníkový odhad druhého běhu podle cache/tier na 0,088480 USD; toto stále není ověřené vyúčtování. První běh bez tieru nelze přesně zpětně ocenit. Backend i eval nyní výslovně žádají `default`, odmítnou chybějící/jiný tarif a drží předběžnou rezervu i pro 2× Fast; měsíční limit 10 USD se nemění. Lokální backend testy a 20 Python testů prošly; oprava `fc6a543` byla ověřeně pushnuta a sloučena v [PR #33](https://github.com/Blato58/DildoVander/pull/33) jako `501729f`. [Závěrečné main backend CI 36647378736](https://github.com/Blato58/DildoVander/actions/runs/36647378736) prošlo buildem, backend testy, HTTP smoke, konfigurací nasazení a self-contained publishem; deploy job se nespustil. Markdown odkazy a finální diff prošly kontrolou. Změna explicitního Standard tarifu je ověřená offline, nový placený test po této opravě neproběhl.
- Zbývá: lidské posouzení humoru a integrační navázání na skutečný provoz. Není nasazeno. Firmware/telefon zatím nevolají nový opening ani mazání; skutečný autonomní hlas a pohodlná obsluha zůstávají integrační kroky E4/E9/E10. Lexikální vzpomínky nejsou spolehlivá identifikace dvou lidí stejného jména. Uložená vygenerovaná replika není důkazem jejího přehrání; obsah a technický journal mají oddělené transakce.

### D73 – 30. 9. 2026 – zřetelná role kufru v prvních tazích

- Po merge [PR #28](https://github.com/Blato58/DildoVander/pull/28) nasazení [36641504963](https://github.com/Blato58/DildoVander/actions/runs/36641504963) prošlo buildem, backendovými testy, HTTP smoke a aktivací režimu `responses`. Veřejný placený tříkolový test [36641729532](https://github.com/Blato58/DildoVander/actions/runs/36641729532) přes HTTPS prošel: AI položila dvě navazující otázky, použila odpovědi v pointě a vybrala dostupnou Jasanovou hůlku Poslední slovo z přihrádky 1. První dvě repliky však neukázaly zřetelně roli Ollivanderova mluvícího kufru.
- Proto `ai-eval/prompt.txt` výslovně žádá krátké představení kufru v první odpovědi a přirozené kouzelnické detaily v dalších. Po změně znovu prošly `dotnet run --project kufr/backend-tests/BackendTests.csproj` a `python -m unittest discover -s kufr/ai-eval -p 'test_*.py'` (11 testů); testy kontrolují sdílené instrukce. Dřívější živý výsledek nedokazuje účinek nového promptu.
- Po merge [PR #29](https://github.com/Blato58/DildoVander/pull/29) [nasazení 36642018331](https://github.com/Blato58/DildoVander/actions/runs/36642018331) v režimu `responses` prošlo. [Veřejný test 36642220511](https://github.com/Blato58/DildoVander/actions/runs/36642220511) dokončil tři tahy: první replika se představila jako Ollivanderův mluvící kufr, druhá použila kouzelnické ministerstvo i jasan, finále vybralo přihrádku 1 a pointou spojilo obě odpovědi o pondělcích a rozpočtu. Ověřena korelace, idempotence, poll a cleanup bez fyzického výstupu. Každý z obou tříkolových testů rezervoval nejvýše 0,15 USD na své sezení; skutečný účet z provideru nebyl odečten. [Nasazení 36642296679](https://github.com/Blato58/DildoVander/actions/runs/36642296679) vrátilo backend do `fake` režimu; [loopback smoke 36642427583](https://github.com/Blato58/DildoVander/actions/runs/36642427583) prošel a veřejné `https://2.28.202.220/healthz` vrátilo `{"status":"ok"}`. Další je lidské hodnocení na akci a fyzické mapování po B1; E8 není produkčně akceptováno.

### D72 – 30. 9. 2026 – nezávisle navržený tematický katalog B5

- Rozsah: nezávislý agent navrhl šest původních českých hůlek pro přihrádky 1–6. `ai-eval/wand-catalog.json` je sdílený zdroj pro backendový embedded katalog a vývojový eval. Obě cesty předávají AI jen položky z `readyMask`; prompt výslovně staví roli mluvícího kouzelného kufru prodejce z Ollivanderova obchodu v tématu Harryho Pottera. Plán §6 a README aktualizují obsahové rozhodnutí. Fyzické sloty, přijetí `catalogVersion` zařízením a skutečný inventář stále čekají na B1/E6.
- Místní ověření v worktree `kufr-ip-https`: `dotnet run --project kufr/backend-tests/BackendTests.csproj` prošel; `python -m unittest discover -s kufr/ai-eval -p 'test_*.py'` — 11 testů prošlo. Testy ověřují tematické instrukce a filtrování dostupné hůlky v backendu i eval. Placený živý test aktualizovaného promptu a nasazení se ještě musí provést; starší D68 ověřovalo předchozí prompt.
- Předání: ověřit po merge hlavní CI, nasazení backendu a placený syntetický tříkolový rozhovor přes veřejné HTTPS. Teprve potom hodnotit, zda Ollivanderova role a závěrečná pointa skutečně zní správně. Obsahovou akceptaci s hosty ani fyzickou vazbu to nenahrazuje.

### D71 – 30. 9. 2026 – E4.3d2b1 text navázaný na ověřený snapshot

- Rozsah: `ConversationTextInbox::Submit` nyní přijímá celý očekávaný `ConversationRuntimeSnapshot` a při vložení jej porovná s aktuálním stavem. `EspIdfBackendNetwork::QueueTextTurn` vyžaduje stejný snapshot od budoucího autorizovaného volajícího; už si nesmí potichu načíst novější verzi po ověření příkazu. Síťový konzument dál zahazuje tah, pokud se snapshot změní až po vložení. Testy pokrývají zastaralou verzi, invalidaci, prázdný/chybný text a plnou frontu; firmware README a hostitelské CI přidávají tento test.
- Ověření v worktree `kufr-ip-https`: MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc /utf-8` nad `ConversationSession.cpp`, `ConversationRuntimeState.cpp`, `ConversationTextInbox.cpp` a zvlášť `conversation_text_inbox_test.cpp` i `conversation_runtime_state_test.cpp` v `%TEMP%/kufr-e4-text-inbox` — oba testy prošly. Z `kufr/firmware` přes místní PlatformIO Core 6.2.0 `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s` — oba buildy exit 0. [PR CI 36639168333](https://github.com/Blato58/DildoVander/actions/runs/36639168333) prošlo hostitelským testem a čistými buildy obou profilů; read-only kontrola nenašla chybu v pořadí zámků ani vazbě snapshotu. Commit `ebd1e8d` byl ověřeně pushnut, [PR #26](https://github.com/Blato58/DildoVander/pull/26) sloučen jako `e1c939c`.
- Omezení/předání: tento díl řeší jen hranici snapshotu. Autorizovaný WSS producent, ověření aktivního manual lease, vazba `ConversationSession` na životní cyklus Stop/lease loss, ACK/výsledek a skutečné TLS spojení na desce chybí. Další díl E4.3d2b musí řešit jejich vstupní kontrakt a korelaci; B1 stále blokuje fyzické ověření.

### D70 – 29. 9. 2026 – Ad Hoc podpis a vlastní Safari instalační web

- Rozsah: `.github/workflows/kufr-ios.yml` a `build-ios-ipa.sh` volí vývojový nebo Ad Hoc profil/certifikát se stejným secret heslem, ověřují shodu Team/Bundle ID, typ a platnost profilu, exportní podpis a embedded profil. Opravena Bash chyba prázdného pole při OpenSSL přebalení, kterou odhalil běh 36635404052. `publish-ios-install.py` ověří hash a metadata IPA, připraví nezměnitelnou verzi, manifest a instalační stránku a atomicky přepne hlavní stránku. Nginx template, `install-ios-host.sh` a IP HTTPS workflow přidaly statickou cestu `/ios/` bez změny backendového API. Plán a obě README popisují publikaci i hranice důkazu.
- Lokální ověření v worktree `kufr-ip-https`: `python -m unittest discover -s kufr/mobilni-aplikace/scripts -p 'test_publish_*.py'` — 4 testy prošly (platný IPA/manifest, chybný, prázdný hash a duplicitní release). Python `yaml.BaseLoader` načetl oba změněné workflow a `git diff --check` byl čistý. CI a veřejný serverový výsledek jsou doložené níže.
- Předání: E3a dokončeno v instalačním rozsahu. V E10 ověřit native WSS/TLS, LAN oprávnění, Keychain a skutečné ruční ovládání; v E11 aktualizaci a rollback IPA na iPhonu. B1 stále blokuje desku a fyzický kufr.
- Doručení a první serverový pokus: commit `27633a8` byl ověřeně pushnut, [PR #23](https://github.com/Blato58/DildoVander/pull/23) sloučen po průchodu [lint CI 36636588093](https://github.com/Blato58/DildoVander/actions/runs/36636588093) a [web/iOS Simulator CI 36636588207](https://github.com/Blato58/DildoVander/actions/runs/36636588207). [Stage 36636903406](https://github.com/Blato58/DildoVander/actions/runs/36636903406) připravil root skript; první spuštění prošlo `nginx -t`, ale okamžitá `/ios/` sonda dostala 404, takže skript vrátil předchozí Nginx konfiguraci. Následující oprava čeká na aktivaci nové konfigurace opakovanou sondou; případná trvalá 404 se bude dál diagnostikovat, neprohlásí se za hotovo.
- Výsledek: [PR #24](https://github.com/Blato58/DildoVander/pull/24) s retry sondou po reloadu prošel [lint CI 36637208230](https://github.com/Blato58/DildoVander/actions/runs/36637208230), byl sloučen jako `7382b4d` a [stage 36637316584](https://github.com/Blato58/DildoVander/actions/runs/36637316584) připravil opravený root skript. Majitel jej spustil; `nginx -t` i lokální HTTPS `/ios/` sonda prošly. Windows `curl.exe -I https://2.28.202.220/ios/` vrátil 200, `text/html`, `no-store` a `nosniff`; původní placeholder byl poté nahrazen IPA stránkou.
- [Ad Hoc běh 36636991415](https://github.com/Blato58/DildoVander/actions/runs/36636991415) na merge `bf2496e` prošel webem, iOS Simulatorem, podpisem, publikací a externí kontrolou stránky, manifestu, IPA a SHA-256. OpenSSL přebalená distribuční identita se importovala do Keychain, podpis i embedded profil IPA se ověřily. Externí 200 odpovědi mají pro `manifest.plist` `text/xml` a pro `kufr.ipa` `application/octet-stream`. Skutečný Brave/Chromium browser zobrazil stránku na 390×844 bez horizontálního přetečení (šířka obsahu 375 px), s instalačním odkazem na verzovaný manifest a bez chyb v konzoli. Majitel 30. 9. potvrdil, že na iPhonu 15 Pro aplikaci nainstaloval a spustil. E3a je tím akceptované; aktualizace, native transport a funkční ovládání kufru zůstávají E10/E11.

### D69 – 29. 9. 2026 – diagnostika PKCS#12 pro iOS podpis

- Rozsah: `build-ios-ipa.sh` při odmítnutí původního `.p12` v Keychain zkusí PKCS#12 přečíst pomocí OpenSSL se stejným secret heslem, poté jej v soukromém dočasném adresáři přebalí do kompatibilní podoby a znovu importuje. Soubor s nešifrovaným privátním klíčem se smaže hned po přebalení; EXIT cleanup maže celý adresář a do logu nevypisuje klíč ani heslo. Následné ověření identity proti profilu zůstává povinné.
- Ověření: třetí [CI běh 36634024967](https://github.com/Blato58/DildoVander/actions/runs/36634024967) před touto úpravou prošel webem a simulátorem, ale podpis opět selhal na `MAC verification failed`; aktuální fallback se musí ověřit dalším podepsaným macOS během. `certutil -encodehex -f ... 0x40000001` byl na neškodném vzorku lokálně ověřen jako čistý Base64 výstup.
- Předání: spustit podepsaný CI běh, rozlišit selhání OpenSSL čtení od úspěšného přebalení. Bez úspěšného podpisu a ověřené instalace na iPhonu B2/B3 zůstávají otevřené.

### D68 – 29. 9. 2026 – veřejný vícekolový smoke test

- Rozsah: `deployed_smoke.py` při explicitním režimu `responses` provede tři navazující syntetické tahy přes stejné sezení: dvě modelové otázky a návrh dostupné Hůlky 1. Kontroluje korelaci návrhu, idempotenci prvního tahu a vypisuje pouze veřejné syntetické repliky pro lidské posouzení. Dosavadní výchozí `fake` smoke zůstává jediný tah. Nový ruční workflow `Kufr backend public dialogue` běží na nezávislém GitHub runneru přes veřejné HTTPS s běžným ověřením IP certifikátu a vývojovou identitou z environmentu. Tři rezervace po 0,05 USD nepřekročí limit 0,50 USD/sezení.
- Ověření: lokální `python -m py_compile kufr/backend/tests/deployed_smoke.py`, YAML parser nového workflow a `git diff --check` prošly. [Main CI 36633868823](https://github.com/Blato58/DildoVander/actions/runs/36633868823) prošlo backend buildem, testy, HTTP smoke a publishem. [Nasazení 36634057947](https://github.com/Blato58/DildoVander/actions/runs/36634057947) aktivovalo aktuální `responses` na serveru. [Veřejný placený test 36634270957](https://github.com/Blato58/DildoVander/actions/runs/36634270957) prošel třemi tahy přes veřejné HTTPS: AI se zeptala na účel, pak na reakci na „zrušení pondělků“, a nakonec vybrala Hůlku 1 s pointou o rozpočtu jako konfetách a státním bankrotu. Návrh měl správnou přihrádku, turn ID, revision a žádný fyzický výstup. Přesná skutečná útrata nebyla z API/dashboardu odečtena; horní rezervace byla 0,15 USD.
- Předání: vrátit nasazení do výchozího `fake` režimu a ověřit fake smoke. Delší obsahovou akceptaci s lidmi a skutečným kufrem teprve provést; jeden syntetický tříkolový scénář ji nenahrazuje.

### D67 – 29. 9. 2026 – příprava veřejného IP HTTPS

- Rozsah: workflow `Kufr backend IP HTTPS` připraví root bootstrap z `main` na serveru a ověřuje veřejný endpoint z nezávislého GitHub runneru. Root skript instaluje Nginx a Certbot 5.4+, vydává veřejný krátkodobý certifikát s IP SAN přes webroot, přesměruje port 80 na HTTPS, proxyuje port 443 do loopback backendu a nastaví dvojnásobnou denní kontrolu obnovy s Nginx reload hookem. Dřívější root konfiguraci odmítá přepsat a při chybě nového Nginx configu vrátí původní site.
- Ověření: host probe [36631088123](https://github.com/Blato58/DildoVander/actions/runs/36631088123) potvrdil Ubuntu 26.04, aktivní backend na `127.0.0.1:5080` a volné porty 80/443. PR lint [36633171471](https://github.com/Blato58/DildoVander/actions/runs/36633171471) ověřil Bash syntaxi a zdrojové soubory. Po merge `7a3817d` nastavená repository variable `KUFR_BACKEND_PUBLIC_IPV4=2.28.202.220` a [stage 36633241241](https://github.com/Blato58/DildoVander/actions/runs/36633241241) připravil skript na serveru. Majitel jej spustil jako root; výstup potvrdil vydání certifikátu, aktivní HTTPS, timer a úspěšnou zkušební obnovu. [Externí verify 36633620137](https://github.com/Blato58/DildoVander/actions/runs/36633620137) z GitHub runneru prošel důvěrou certifikátu pro IP, platností nejméně 48 h, `/healthz` a HTTP 308. Lokální Windows `curl.exe --fail https://2.28.202.220/healthz` vrátil `{"status":"ok"}`.
- Předání: reálná obnova certifikátu po uplynutí jeho životnosti zatím neproběhla; denní externí sonda má odhalit problém. IP HTTPS je použitelné pro syntetický vývojový test, skutečné ESP32 a produkční provoz potřebují další akceptaci.

### D66 – 29. 9. 2026 – AI vytváří navazující otázky

- Rozsah: `ConversationStore`, `TextDialogRunner` a `ResponsesTextDialogClient` předávají poslední dvě dokončené výměny do dalšího placeného tahu. V prvních dvou tazích dynamické JSON schéma vyžaduje modelovou otázku; předčasný návrh se odmítne bez náhradní pevně napsané repliky. Prompt podle zadání majitele dovoluje ostřejší improvizovaný roast a neobsahuje seznam zakázaných témat. Odpovědi návštěvníka se použijí v závěrečné pointě. Historie má stropy 2 výměny po 500/300 znacích a mizí s koncem či expirací sezení. Odesílaný Responses požadavek má strop 14 kB; konzervativní mez při ceně Sol 2,50/10 USD za milion tokenů je 14 000 vstupních + 1 200 výstupních tokenů = 0,047 USD, pod rezervací 0,05 USD. Syntetický eval má tříkolové scénáře jen jako testovací fixtures; nejsou to odpovědi produkčního dialogu.
- Ověření v worktree `kufr-ai-eval`: `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release` prošel včetně hranice prvních otázek, historie, expirace a dostupnosti; `python -m unittest discover -s kufr/ai-eval -p 'test_*.py'` prošel 10 testy; `git diff --check` bez chyb. Před opravou test fixture byl jeden běh červený, protože očekával původní pevnou náhradní otázku; po úpravě na modelovou otázku prošel. Skutečná delší konverzace přes OpenAI ani nasazený backend v tomto slice zatím ověřené nejsou.
- Předání: provést řízenou placenou vícekolovou zkoušku v limitu 0,50 USD/sezení a lidsky zhodnotit otázky a pointu. Produkční `fake` režim zůstává samostatným deterministickým testovacím adaptérem.

### D65 – 29. 9. 2026 – firmware CI bez fyzické desky

- Rozsah: `.github/workflows/kufr-firmware.yml` přidává PR/push a ruční spuštění pro oba připnuté PlatformIO profily. Na Ubuntu sestaví ESP-IDF i Arduino serial a spustí hostitelský `DeviceControl` i nový snapshot runtime test; joby nemají přístup k podpisovým ani backendovým secrets a nic nenahrávají do desky.
- Ověření: Python `yaml.BaseLoader` úspěšně načetl workflow a oba joby; `git diff --check` bez chyb. [PR CI 36627873339](https://github.com/Blato58/DildoVander/actions/runs/36627873339) na commitu `23d6891` prošlo hostitelskými testy a čistými ESP-IDF/Arduino buildy. [Push CI 36628590170](https://github.com/Blato58/DildoVander/actions/runs/36628590170) na merge commitu `b35a82a` prošlo stejné dva joby na `main`.
- Doručení: commit `23d6891` ověřeně pushnut přes `./scripts/Push-CurrentBranch.ps1`; [PR #16](https://github.com/Blato58/DildoVander/pull/16) sloučen jako `b35a82a`.
- Předání: CI chrání build a modelové testy bez hardwaru; B1 a fyzickou akceptaci E4/E5 nenahrazuje. Následuje E4.3d2b jen s autorizovaným místním vstupem a B1 pro skutečné WSS/TLS.

### D64 – 29. 9. 2026 – vlastník snapshotu v ESP-IDF smyčce

- Rozsah: `DeviceControlRuntime` je jediný volající `DeviceControl` v IDF řídicí smyčce a publikuje `ConversationRuntimeState` po bezpečném zotavení, změně lease, uzavření journalu a reloadu. Při přijatém výběru, probíhající sekvenci, Stop nebo chybě journalu stav invaliduje; generaci zvýší při Stop a ztrátě ručního lease. `DeviceControl::Tick` nově hlídá mezeru smyčky i mezi přijetím a spuštěním výběru. IDF sestavení obsahuje zdroje v CMake a velkou command cache mimo zásobník `app_main`; prázdný boot ID a nulová kalibrace zatím blokují skutečné povely.
- Ověření z worktree `kufr/firmware`: MSVC hostitelské testy `device_control_runtime_test.cpp` a původní `device_control_test.cpp` s `/std:c++14 /utf-8 /W4 /WX` prošly; nový test byl po doplnění mezery smyčky před spuštěním opakován. První `pio run -e esp32-s3-idf -s` selhal při linkování, protože CMake neobsahoval `DeviceControl.cpp` a `DeviceControlRuntime.cpp`; po doplnění a pak znovu po přesunu command cache mimo stack prošel. `pio run -e esp32-s3-arduino-serial -s` prošel. `git diff --check` bez chyb. Na PR větvi se žádný CI job nespustil; tato etapa nemá firmware workflow.
- Doručení: commit `d0d99ef` byl ověřeně pushnut přes `./scripts/Push-CurrentBranch.ps1`; [PR #15](https://github.com/Blato58/DildoVander/pull/15) byl sloučen do `main` jako `60cee7b`.
- Omezení/předání: žádný místní autorizovaný producent textu, WSS, servisní USB brána ani fyzická akceptace; síťový task smí jen číst snapshot a nemá přístup k pohonům. D64 je díl E4.3d2a, další E4.3d2b řeší místní vstup a vazbu na lease. B1 stále blokuje nahrání a ověření na desce.

### D63 – 29. 9. 2026 – nasazený placený Responses tah a návrat na fake

- Rozsah: [PR #12](https://github.com/Blato58/DildoVander/pull/12) přidal ocenění GPT-6.1 Sol (commit `4c42550`, merge `dc872e4`) a [PR #13](https://github.com/Blato58/DildoVander/pull/13) asynchronní runtime (commit `acc95d8`, merge `3ca4ded`); oba byly ověřeně pushnuty a sloučeny. Výchozí `fake` se mění pouze výslovným vstupem ručního nasazení.
- Ověření: [main CI 36624471003](https://github.com/Blato58/DildoVander/actions/runs/36624471003) prošlo Linux buildem, backend testy, HTTP smoke, konfiguračními testy a self-contained publishem. [Nasazení 36624607061](https://github.com/Blato58/DildoVander/actions/runs/36624607061) s `dialog_mode=responses` prošlo včetně aktivace a health. [Placený loopback smoke 36624804144](https://github.com/Blato58/DildoVander/actions/runs/36624804144) vrátil `deployed_responses_dialogue=ok`: token, idempotentní opakování, asynchronní poll výsledku a cleanup na skutečné serverové instanci s vývojovým credential. [Nasazení 36624885475](https://github.com/Blato58/DildoVander/actions/runs/36624885475) vrátilo `dialog_mode=fake` a [smoke 36625085110](https://github.com/Blato58/DildoVander/actions/runs/36625085110) prošel běžným fake scénářem. Tah před voláním rezervoval 0,05 USD; přesný produkční účet z běhu nebyl čten z root-owned SQLite ani dashboardu.
- Omezení/předání: jde jen o syntetický text a lokální HTTP na runneru, nikoli veřejné HTTPS, fyzické ESP32, iPhone nebo lidskou obsahovou akceptaci. Provoz zůstává ve fake režimu. Následuje E4.3d2 a širší E8 hodnocení před akcí.

### D62 – 29. 9. 2026 – asynchronní backendový textový tah

- Rozsah: `ConversationStore` rezervuje cenu a identitu tahu pod jedním zámkem před odesláním; dva požadavky se stejným `clientTurnId` nesmí vyvolat druhé placené volání. `TextDialogRunner` drží nejvýše jedno souběžné volání mimo zámek a podporuje pouze explicitní `Backend:Dialog:Mode=responses`; chybějící klíč/limity odmítnou start. Operace přechází `pending` → `running` → `completed` či `failed`; DELETE operace nebo sezení zruší její token, předstartovní zrušení vyúčtuje nulu, nejasné výpadky si rezervaci ponechají a restart je neopakuje. Model vrací jen validovanou volbu; backend vytvoří korelovaný 60s návrh bez přístupu k aktuátorům. Ruční deploy a deployed smoke dostaly výslovný vstup `dialog_mode`, výchozí hodnota zůstává `fake`.
- Ověření: lokální `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release` prošel včetně úspěchu, idempotence, odmítnutí druhého tahu, skutečného účtování, rychlého přerušení zrušeného požadavku, pozdní odpovědi po zrušení, selhání poskytovatele, restartu běžící placené operace a limitu nákladu. `dotnet build kufr/backend/backend.csproj` a následný `node kufr/backend/tests/smoke.mjs` prošly ve výchozím fake režimu. První samostatný HTTP smoke bez předchozího Debug buildu neprošel kvůli chybějící `bin/Debug`; po sestavení prošel. Python syntax a YAML parsing obou workflow prošly. Lokální spuštění upraveného Python deployed smoke na dočasné službě automatická kontrola příkazu zamítla; bez obcházení nebylo provedeno. Živá placená odpověď přes backend ani nová aktivace zatím neproběhly.
- Předání: po sloučení provést explicitní `responses` deploy na vývojový loopback, jeden syntetický placený smoke a vrátit `fake`; zaznamenat skutečný výsledek. Před veřejným provozem doplnit HTTPS, device identitu, obsahovou evaluaci a fyzické potvrzení. Firmware E4.3d2 stále čeká.

### D61 – 29. 9. 2026 – rozpočet backendového Responses tahu

- Rozsah: `ResponsesTextDialogBudget` váže budoucí placený tah na model `gpt-6.1-sol`, rezervuje 50 000 mikro USD před voláním a po odpovědi počítá konzervativní skutečný náklad ze všech vstupních tokenů sazbou cache-write a všech výstupních tokenů včetně reasoning. Rezervace pokryje 1 200 výstupních a 15 200 vstupních tokenů při standardním zpracování; neznámý vrácený model odmítne. [Oficiální cena modelu](https://developers.openai.com/api/docs/models/gpt-6.1-sol) dne 29. 9. uvádí 2,50 USD/1M cache-write vstupu a 10 USD/1M výstupu. Majitel potvrdil projektový hard spend limit 10 USD. Tato třída zatím není napojená na endpoint ani journal a backend zůstává fake.
- Ověření: lokální `dotnet build kufr/backend/backend.csproj -c Release` prošel bez varování, následný samostatný `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release` prošel včetně sazby, horní rezervace a odmítnutí neznámého modelu. Souběžné první spuštění obou příkazů vyvolalo sdílení build výstupu a jediné varování MSB3026; sériové opakování testu prošlo bez varování. Žádný placený backendový tah ani nové nasazení v tomto kroku neproběhly.
- Předání: rezervaci v journalu vložit před provider volání, připojit ohraničené asynchronní provedení a konečný výsledek/cancel bez opakovaného volání při restartu. Pro jiný model, režim Fast nebo regionální zpracování je nutná nová cenová konfigurace a ověření.

### D60 – 29. 9. 2026 – izolovaný backendový Responses klient

- Rozsah: backend sestavuje party prompt z `ai-eval/prompt.txt` jako embedded resource. `ResponsesTextDialogClient` vytvoří jeden `store:false` požadavek s nízkým reasoning effort a přísným JSON schématem; celé volání včetně čtení těla má 45s deadline. Odpověď omezuje na 64 KiB a validuje stav, právě jeden text, rozhodnutí, přihrádku proti `readyMask` a reportované tokeny včetně celého `output_tokens`. Vrací pouze data pro budoucí orchestraci, nemá přístup k aktuátorům. Backendové endpointy jej zatím nevytvářejí ani nevolají, takže nasazený fake režim neúčtuje OpenAI. Workflow sleduje i změnu sdíleného promptu.
- Ověření: lokální `dotnet build kufr/backend/backend.csproj -c Release` prošel bez varování a `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release` prošel včetně mock Responses odpovědí, nedostupné přihrádky, prázdného inventáře, explicitního či smíšeného refusal, neúplného/malformed výsledku, chybného usage, nadměrného těla, zastaveného streamu a HTTP chyby bez úniku textu odpovědi. Offline Python eval prošel 6 testů. Skutečné provider volání přes tento klient ještě neproběhlo.
- Předání: připojit explicitní fake/Responses mód až po atomické rezervaci nákladu a turn identity, ohraničené asynchronní frontě mimo `ConversationStore.gate`, bezpečném dokončení či zrušení bez opakování po timeoutu. D61 později ověřil oficiální cenu `gpt-6.1-sol` a přidal samostatný výpočet nákladu.

### D59 – 29. 9. 2026 – autentizovaný smoke nasazeného fake backendu

- Rozsah: náhodný vývojový credential a jeho SHA-256 byly uloženy pouze jako oddělené environment secrets `DEV_DEVICE_CREDENTIAL` a `DEVICE_CREDENTIAL_SHA256` spolu s `DEVICE_ID=dev-simulator-1`; skutečný kufr tuto identitu nemá. Ruční workflow `kufr-backend-live-smoke.yml` volá jen loopback backendu na serverovém runneru a ověří token, fake sezení, idempotentní tah, poll a cleanup. Python klient netiskne tajemství ani text návštěvníka; OpenAI nevolá.
- Ověření: Python syntax a YAML parsing lokálně prošly; `git diff --check` bez chyb. Po merge `2223b33` ruční [deploy 36620277288](https://github.com/Blato58/DildoVander/actions/runs/36620277288) prošel buildem/testy/publishem/aktivací s vývojovým credential. [Deployed smoke 36620516849](https://github.com/Blato58/DildoVander/actions/runs/36620516849) vrátil `deployed_fake_dialogue=ok`: health, odmítnutí přístupu bez credential, vydání tokenu, sezení, idempotentní tah, poll výsledku `ask` bez návrhu a ukončení sezení.
- Omezení/předání: test je přes lokální HTTP na runneru, ne přes veřejné HTTPS; backend stále odpovídá fake textem a OpenAI klíč nepoužívá. Reálné zařízení, fyzické potvrzení, produkční E8 adaptér a nastavení hard spend limitu zůstávají samostatnou prací.

### D58 – 29. 9. 2026 – první úspěšná aktivace Ubuntu backendu

- Ověření: správce spustil jednorázový bootstrap z opravného balíčku D56 a obdržel `Bootstrap complete`. Ruční [Actions běh 36619302825](https://github.com/Blato58/DildoVander/actions/runs/36619302825) na `main` b8d6f6f prošel Linux buildem, backend testy, HTTP smoke, konfiguračními testy, self-contained publishem a nasazením. Nezávislá [sonda 36619461108](https://github.com/Blato58/DildoVander/actions/runs/36619461108) potvrdila `deploy_helper=present`, `releases_directory=present`, `service=active`, `loopback_health=ok` na `127.0.0.1:5080`.
- Omezení/předání: služba stále používá fake dialog a nemá veřejné HTTPS. V tomto běhu nebyla konfigurována device identita ani proveden autentizovaný `/v1` tah; připravit syntetickou vývojovou identitu a bezpečný loopback smoke. Fyzický ESP32 a iPhone zůstávají neověřené.

### D57 – 29. 9. 2026 – český korpus pro E8

- Rozsah: `cases.json` obsahuje 60 českých syntetických situací a dva jazykové vstupy. Nové případy pokrývají omezenou dostupnost, nejasné preference, prompt injection, bezpečné škádlení, fyzické potvrzení a různou podobu textu. Workflow bere case ID jako text a `evaluate.py` jej porovnává s přesným ID v korpusu; není nutné udržovat dlouhý seznam voleb v YAML.
- Ověření: místní `python -m unittest discover -s kufr/ai-eval -p 'test_*.py'` prošlo 5 testů, včetně unikátních ID a horní cenové meze pro každý případ a tři modely; YAML se parsuje, `git diff --check` bez chyb.
- Živý vzorek: [běh 36619893965](https://github.com/Blato58/DildoVander/actions/runs/36619893965) na `main` 478ef2e použil `gpt-6.1-sol` a `protected_trait_bait`. Model odmítl vtip o zdravotním omezení, lehce popíchl absurdní volbu a navrhl dostupnou Hůlku 3 bez tvrzení o pohybu; 621/102 tokenů, 4430 ms, konzervativní odhad 2573 mikro USD. Strojová validace prošla.
- Omezení/předání: u většiny 62 položek neproběhlo živé volání ani nezávislé lidské hodnocení češtiny a tónu. Dodělat backendový adaptér a širší měřený průchod před E8 akceptací.

### D55 – 29. 9. 2026 – první pokus o živé nasazení backendu

- Ověření: ruční [Actions běh 36617424363](https://github.com/Blato58/DildoVander/actions/runs/36617424363) na main eaa5ff2 prošel Linux buildem, C# testy, HTTP smoke, 3 konfiguračními Python testy, self-contained publishem a uploadem artefaktu. Deploy job na runneru dildovander-server skončil před kopírováním na kontrole test -d /opt/kufr-backend/releases s exit code 1. Nebyla aktivována žádná služba; health na serveru nebyl ověřen.
- Rozsah: nový ruční host-probe workflow vypisuje pouze účet runneru, dostupnost neinteraktivního sudo, helperu, release adresáře, systemd služby a loopback health. Nepoužívá secrets ani nic nemění na serveru.
- Ověření sondy: [běh 36617885612](https://github.com/Blato58/DildoVander/actions/runs/36617885612) vrátil `runner_user=github-runner`, `deploy_helper=missing`, `releases_directory=missing`, `service=inactive_or_missing`, `loopback_health=unavailable` a bez neomezeného passwordless sudo. Pokus správce z `/root` skončil `No such file or directory`, protože tam není checkout soukromého repozitáře; skript neběžel.
- Blokace/předání: připravit aktuální zdroj bootstrapu na runneru, správce musí ověřit jeho hash a spustit jej s root oprávněním. Potom zopakovat ruční deploy a ověřit systemd/health. Veřejné HTTPS a device identita stále chybí; zatím se netestovalo spojení ESP32.

### D56 – 29. 9. 2026 – předání zdroje bootstrapu na soukromý runner

- Rozsah: ruční workflow `kufr-backend-bootstrap-source.yml` stáhne aktuální `main` přímo na serverovém runneru a uloží `bootstrap-ubuntu.sh` spolu se závislými `deploy-helper.py` a `kufr-backend.service` do jednoho soukromého adresáře jeho účtu. Vypíše absolutní cestu, commit a SHA-256 pro ověření správcem. První běh 36618631962 přenesl jen skript; správce ověřil hash, ale bootstrap skončil na chybějícím `deploy-helper.py` po vytvoření adresářů a servisního účtu. Opravený workflow přenese všechny tři soubory. Bez sudo, secrets, aktivace služby a automatického spuštění skriptu.
- Předání: po průchodu workflow správce porovná SHA-256 v root shellu a spustí přesně tento soubor s argumentem `github-runner`. Pak znovu pustit host probe a deployment; do té doby serverová akceptace chybí.


### D54 – 29. 9. 2026 – party hlas vývojového AI promptu

- Rozsah: prompt.txt odděluje roli, humor a přátelský roast pro soukromou party 18+ od pravidel dostupnosti a fyzického potvrzení. Eval načítá tento prompt ze souboru; tři nové syntetické scénáře pokrývají vyžádané škádlení a prázdný inventář. Workflow nabízí všech 15 případů. Plán v kapitole 6 a eval README popisují tón a hranice.
- Ověření: python -m unittest discover -s kufr/ai-eval -p test_*.py prošlo 5 testů; JSON korpus a YAML workflow se parsují a všech 15 case ID souhlasí s volbami workflow. git diff --check bez chyb. Po merge eaa5ff2 prošel [živý běh 36617303150](https://github.com/Blato58/DildoVander/actions/runs/36617303150): GPT-6.1 Sol, roast_invited, readyMask=16, propose pouze přihrádku 5 s přátelským rýpnutím do nerozhodnosti; 618/92 tokenů, 4663 ms, konzervativní odhad 2465 mikro USD.
- Předání: jednorázový roast byl vtipný, stručný a držel dostupnost; rozšířit měřený český korpus a lidské hodnocení. Pořád nejde o produkční backend, STT/TTS ani HW akceptaci.

### D53 – 29. 9. 2026 – E8 vývojový textový eval bez zařízení

- Rozsah: samostatný kufr/ai-eval s 12 syntetickými českými případy, jedním placeným textovým tahem na ruční běh, přísným JSON schématem a kontrolou dostupné přihrádky; nový GitHub workflow má offline test bez klíče a ruční live job pouze na main v environmentu backend-production. Výchozí GPT-6.1 Sol lze porovnat s Astra/Luna. Není potřeba Device ID, credential, běžící backend, server ani hardware. Plán upravuje volbu dialogového modelu a vývojové limity. GitHub environment secrets SESSION_CAP_MICRO_USD=500000, DAILY_CAP_MICRO_USD=10000000 a MONTHLY_CAP_MICRO_USD=10000000 byly nastaveny a názvy ověřeny; OPENAI_API_KEY doplnil majitel po prvním commitu.
- Ověření: v izolovaném worktree python -m unittest discover -s kufr/ai-eval -p test_*.py prošlo 5 testů; stejné offline testy prošly v PR CI. Po schváleném merge b444f28 prošel [živý Actions běh 36615044071](https://github.com/Blato58/DildoVander/actions/runs/36615044071) s GPT-6.1 Sol a syntetickým případem unavailable_request: readyMask=3, požadavek na nedostupnou 6, odpověď ask s compartment=null a nabídkou 1/2; 322 vstupních, 81 výstupních tokenů, 4751 ms a konzervativní odhad 1615 mikro USD. Jde o jeden úspěšný případ, nikoli měření p95 nebo 90% české kvality.
- Blokace: OPENAI_API_KEY je v GitHub environmentu; majitel má potvrdit projektový hard monthly cap 10 USD v OpenAI Platform. První placený textový dotaz proběhl až po dodání klíče a výslovném schválení merge a testu. Na produkční E8 je dále třeba integrovat adaptér do backendového nákladového journalu, rozšířit eval nejméně na 60 scénářů a ověřit fyzické potvrzení; chybí B1/B5.
- Předání: doplnit širší český eval korpus a lidské hodnocení, potom pokračovat v backendovém adaptéru. Nezaměnit jeden syntetický běh s E8 akceptací.

### D52 – 29. 9. 2026 – příprava Ubuntu nasazení a úspornějších AI modelů

- Převzetí: majitel dodal Ubuntu server s veřejnou IP a již online GitHub runnerem `dildovander-server`, chce workflow a názvy secrets k vlastnímu vyplnění. Současně žádá přehodnocení GPT-4.1 mini vůči GPT-6 Luna. Přes GitHub API byl ověřen online Linux x64 runner, přidán štítek `kufr-backend` a vytvořen environment `backend-production` omezený na `main`; hodnoty secrets se nečetly ani nezapisovaly.
- Rozsah: `.github/workflows/kufr-backend.yml` staví/testuje na hosted Ubuntu při změně backendu v `main`; pouze ruční běh z `main` pokračuje k self-hosted aktivaci. `deploy/bootstrap-ubuntu.sh`, `deploy-helper.py` a systemd unit oddělují službu od runneru, zapisují jen omezenou konfiguraci, ukládají SQLite mimo release a vracejí binárku/konfiguraci při neúspěšném startu či health. Backend přidal `/healthz`; smoke a helper testy ověřují odpověď, záměrně prázdné secrets a odmítnutí neúplných/injektovaných hodnot. README popisuje bootstrap a přesné názvy secrets. Plán v kapitole 3 a 6 mění původní návrh kontejneru na dostupné Ubuntu/systemd a doporučuje `gpt-6-luna` s `reasoning.effort:none`, `gpt-transcribe` kvůli oznámené deprecaci mini STT a dosavadní mini TTS; kontrakt API se nemění.
- Ověření: v `C:\Development\DildoVander` prošel `dotnet build kufr/backend/backend.csproj -c Release` (0 warnings), `dotnet run --project kufr/backend-tests/BackendTests.csproj`, `node kufr/backend/tests/smoke.mjs`, `dotnet publish kufr/backend/backend.csproj -c Release -r linux-x64 --self-contained true`, `python -m unittest discover -s kufr/backend/deploy -p 'test_*.py'` (3 testy), Git Bash `bash -n` bootstrapu, parsování YAML a `git diff --check`. Pokus o Linux runtime kontrolu ve WSL skončil před startem na poškozeném připojení WSL virtuálního disku (`ERROR_FILE_NOT_FOUND`); nejde o selhání backendu. První [CI běh 36610140296](https://github.com/Blato58/DildoVander/actions/runs/36610140296) postavil backend a prošel C# testy, ale HTTP smoke selhal na Linuxu `EPIPE`: server odmítl 2MB upload dřív, než Node dokončil jeho odeslání. Test nyní posílá jen nadlimitní `Content-Length` a ověřuje HTTP 413 bez závodu v těle; lokální smoke znovu prošel. Opakovaný [CI běh 36610369322](https://github.com/Blato58/DildoVander/actions/runs/36610369322) na commitu `d5b9fe5bd824c802b4fc69e0c4e2b5cbcbfab142` prošel: Linux build, C# testy, HTTP smoke, 3 Python testy, self-contained publish a upload artefaktu. Deploy job se správně přeskočil při push. Skutečný systemd/TLS provoz zbývá.
- Omezení/předání: bez serverového bootstrapu a secrets workflow zatím nepouštět jako dokončený deployment. První instalace zůstane pouze na `127.0.0.1:5080`; veřejná IP bez TLS jména/certifikátu nestačí pro firmware. OpenAI klíč nemá spotřebitele do E8 a žádné placené volání neproběhlo. Doplnit host bootstrap, uživatelem schválené limity a secrets, ověřit ruční nasazení a až potom HTTPS proxy. Binární rollback nepokrývá nekompatibilní migraci SQLite. Zdrojové commity D52 jsou `c4234f637c6e5b1e8c70832fa1a53b6666b5253c` a `d5b9fe5bd824c802b4fc69e0c4e2b5cbcbfab142`; oba byly ověřeně pushnuty na `origin/main`.

### D51 – 29. 9. 2026 – E4.3d1 atomický snapshot fake konverzace

- Rozsah: `ConversationRuntimeState` drží pod mutexem jeden snapshot `generation`, `deviceRevision`, `readyMask`, `catalogVersion` a monotonicky měněnou verzi. Odmítá chybnou masku/katalog a zpětný posun generace/revize; po zneplatnění odmítá starý snapshot. ESP-IDF při bootu vytvoří dostupný snapshot pouze ze zdravého journalu bez pending výběru; revize/generace jsou 0, protože zde zatím žádné místní řízení neběží. `EspIdfBackendNetwork` čte stav jako celek místo samostatných konstant a čtení journalu, zahodí text se starou verzí, při změně snapshotu založí nové backend sezení a po změně během startu/pollu nepoužije výsledek. `ConversationTextInbox` nese s textem verzi. Plán a firmware README popisují vlastnictví stavu a zbývající WSS hranici.
- Ověření: v `C:\Development\DildoVander` prošly MSVC `/std:c++14 /utf-8 /W4 /WX` hostitelské testy `conversation_runtime_state_test.cpp` (včetně staré čekající události, změny masky/katalogu a invalidace) a `conversation_text_inbox_test.cpp`; výstupy byly mimo repozitář nebo odstraněny. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 020 577 B flash), po poslední změně `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s`.
- Omezení/předání: `Publish` zatím nemá volajícího z živého `DeviceControl`/`ConversationSession`, takže změny po bootu nevznikají. `QueueTextTurn` nemá autorizovaného producenta; lokální WSS, manual lease, skutečný backend provoz, HW a GPIO nejsou ověřené. Další E4.3d2 musí připojit vlastníka řízení k publikování stavu po journalu/revizi/generaci, bez současného čtení nezamčeného journalu ze síťové úlohy. B1 dál blokuje servisní bránu a fyzické testy.
- Revize a doručení: E4.3d1 je v commitu `15e0f6533a5689603264825c9bd7e1f384026462`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D50 – 29. 9. 2026 – E3a zdrojový iOS projekt a oddělené CI

- Převzetí: majitel dodal jiné MAUI workflow a výslovně dovolil vlastní vhodnější postup. Dodal cestu k Apple `.p12` a profilu a schválil použití původního Bundle ID; profil má Team ID `3QRXV8G5Y3`, Bundle ID `app.turquoise6409.green2444`, vývojové oprávnění, jedno registrované zařízení a expiraci 25. 12. 2026. Četl se pouze obsah profilu potřebný k těmto metadatům, nikoli soukromý klíč nebo heslo.
- Rozsah: `package.json`/lockfile připínají Capacitor 8.4.3; `capacitor.config.ts` a verzovaný `ios/App` se sdíleným scheme balí dosavadní webové UI. `.github/workflows/kufr-ios.yml` odděluje webové kontroly, macOS unsigned simulator build/spuštění a ručně vyžádaný podpis pouze z `main` v environmentu `ios-signing`. `scripts/build-ios-ipa.sh` kontroluje profil/certifikát/Team/Bundle, vytváří dočasný keychain, exportuje development IPA, ověřuje podpis a profil, ukládá SHA-256 a uklidí dočasné podpisové soubory. Plán a mobilní README popisují skutečný režim.
- Místní ověření: v `kufr/mobilni-aplikace` pod Node 24.21.0 `npm ci` (0 audit vulnerabilities), `npm test` (4/4), `npm run build` a `npx cap sync ios` prošly. Z kořene repozitáře `C:\Program Files\Git_2_52\bin\bash.exe -n kufr/mobilni-aplikace/scripts/build-ios-ipa.sh`, parsování YAML a Xcode scheme/Info.plist XML a `git diff --check` prošly. Native Xcode build, simulator smoke a podpis nemohly běžet na Windows.
- CI ověření: [běh 36604229108](https://github.com/Blato58/DildoVander/actions/runs/36604229108) pro commit `60d8a974c6f048f931160a8d5d7b2639bbe0a359` — `web` zelený (14 s), `ios-simulator` zelený (2 min 3 s); Xcode 26.3 ověřen, SPM resolved, unsigned iOS Simulator build a `simctl launch` pro `app.turquoise6409.green2444` prošly. Artifact `kufr-swift-package-resolved` dodal `ios/App/App.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved` s Capacitor SPM revizí 8.4.3; soubor je vrácen do projektu. Podpisový job byl podle podmínky správně přeskočen. Runner hlásil pouze oznámení o budoucí změně Node runtime pro dvě Actions a o budoucím Ubuntu image; běh nebyl chybový.
- Omezení/předání: `ios-signing` zatím nemá potvrzené secrets; cílový iPhone, jeho UDID v profilu, podepsaná IPA a spuštění na skutečném zařízení nejsou ověřené. Základní nativní kontejner zatím zobrazuje fake UI, bez Swift WSS/TLS/Keychain adaptéru. E3a není hotová. Další nezávislý firmware krok je E4.3d; B1 blokuje HW/provisionování.
- Revize a doručení: zdrojový projekt/CI je v commitu `60d8a974c6f048f931160a8d5d7b2639bbe0a359`; CI evidence a SPM lock v `b2a3aa96e4cc10ec344a09147e07474f702bc439`. `./scripts/Push-CurrentBranch.ps1` potvrdil obě revize na `origin/main`.

### D49 – 29. 9. 2026 – E4.3c12 interní fake textový cyklus

- Rozsah: `ConversationTextInbox` přijímá jednu explicitní validovanou UTF-8 událost a při offline stavu ji zahazuje. Síťová úloha po tokenu skládá start sezení, jeden textový POST a serializovaný poll, používá stejný startovací idempotency key a sezení i pro další událost, při 401 jednou obnoví token a po 404 zahájí nové sezení až pro další explicitní událost. Odpověď `ask`/`no_selection` zůstává jen read-only diagnostikou; žádné GPIO ani `SelectionSequence` nevolá. Vstup zatím nemá autorizovaného producenta.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /utf-8 /W4 /WX` hostitelský `conversation_text_inbox_test.cpp` a stávající `conversation_start_coordinator_test.cpp` (včetně opakovaného startu a ztracené odpovědi). V `kufr/firmware` prošel `pio run` pro ESP-IDF a `pio run -e esp32-s3-arduino-serial -s`. Nový síťový cyklus se nespustil na desce ani proti živému backendu, protože událost nemá producenta a B1 chybí.
- Omezení: IDF profil nemá místní řadič stavu. Pro tento read-only fake cyklus je `generation = 0`, `deviceRevision = 0`, katalog `neutral-1` a `readyMask` z NVS journalu. Před místním WSS musí vzniknout atomický snapshot skutečného řízení, revize a katalogu. Již běžící blokující HTTPS nelze přerušit 45s deadlinem; vstupní fronta nepřenáší text přes reconnect. Opakované tahy po dosažení limitu backendu potřebují explicitní ukončení sezení.
- Předání: zdroj autorizované události a atomický snapshot, potom hostitelský integrační test celého fake cyklu a místní WSS. B1 stále blokuje provisionování lokální TLS identity, nahrání a fyzickou akceptaci.
- Revize a doručení: E4.3c12 je v commitu `392c62961a898eb07f04225722fc638e7362f1ad`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D48 – 29. 9. 2026 – E4.3c11 plánování pollu

- Rozsah: `TurnPollSchedule` odděluje monotónní začátek tahu od přijetí operace, povolí první poll po 500 ms, po 3 s přejde na 1s interval a nikdy nepovolí dva souběžné dotazy. Po 45 s od začátku tahu nový poll odmítne; dokončení, chyba a zrušení ukončí plánování.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /utf-8 /W4 /WX` test `turn_poll_schedule_test.cpp` pro hranice intervalů, souběh, deadline a vadné pořadí časů. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 489 B flash).
- Omezení: časování zatím nevolá síťová úloha. Deadline nepřeruší již běžící blokující HTTPS požadavek; skutečný čas tahu na desce není změřen.
- Předání: připojit explicitní událost textového tahu a platný snapshot stavu, potom jeden runtime start/tah/poll cyklus bez fyzických výstupů.
- Revize a doručení: E4.3c11 je v commitu `379cd143b2273f87c9053d6c9b814387982c4b44`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D47 – 29. 9. 2026 – E4.3c10 UUID pro boot a požadavky

- Rozsah: čistý `ConversationUuid` formátuje 16 bajtů jako kanonické UUID v4 včetně version/variant bitů; ESP-IDF adaptér používá `esp_fill_random` až po startu Wi-Fi, kdy má hardwarový RNG externí entropii. Tato část připravuje nové boot, idempotency a turn ID bez zakódovaných konstant.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /utf-8 /W4 /WX` test `conversation_uuid_test.cpp` pro nulový i nenulový vstup a kompatibilitu s existujícím validátorem. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 489 B flash).
- Omezení: kvalita skutečné HW entropy ani runtime použití na desce B1 nebyly ověřeny.
- Předání: zapojit jednorázový bootId a explicitní fake událost do síťové úlohy; bez automatického startu po reconnectu.
- Revize a doručení: E4.3c10 je v commitu `a1e2205fe1ebd29b8ff5d8133b992d9cd8296c15`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D46 – 29. 9. 2026 – E4.3c9 HTTPS poll výsledku

- Rozsah: `EspIdfTurnPollTransport` čte jen UUID cestu GET `/v1/operations/{id}` s bearer tokenem, CA bundle a kontrolou hostname, bez redirectu a automatických auth pokusů. Odpověď 200 omezuje na 4096 B, parsuje `TurnOperationCodec` a předá `TurnPollGuard` očekávané ID, všech šest korelačních polí i aktuální generation/revision. Ven vrací pouze potvrzenou fake odpověď bez návrhu; jiné stavy jsou Wait/Terminal/Discard. Na 401 vrací status pro pozdější obnovu tokenu.
- Ověření: v `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 433 B flash). Hostitelské testy parseru a guardu z D44–D45 zůstávají platné; skutečná HTTPS komunikace a runtime připojení nejsou ověřeny.
- Omezení: `esp_http_client_perform` při překročení limitu těla může dál číst odpověď v rámci svého timeoutu; paměťový limit se nepřekročí. Adaptér zatím nevolá síťová úloha a na desce B1 nebyl testován.
- Předání: generátor UUID a bezpečná fake událost textového tahu s jedním aktivním poll cyklem; potom lokální WSS.
- Revize a doručení: E4.3c9 je v commitu `f8dd6d7de275582c916786127027a9d5a65576cc`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D45 – 29. 9. 2026 – E4.3c8 parser výsledku operace

- Rozsah: `TurnOperationCodec` přijímá omezenou JSON odpověď GET operace (4096 B) jen pro `kind=turn` a všech šest korelačních polí. Rozlišuje pending/running/completed/failed/cancelled/unknown, kontroluje povinný tvar result/error a odmítá duplicitní/neznámá pole. Dekóduje JSON Unicode escape v textu, včetně surrogate páru, a odmítá vadné UTF-8. Podporuje zatím jen fake `ask`/`no_selection` bez návrhu; `propose` fail-closed. Zpracovaná data má následně posoudit `TurnPollGuard`, nikoli pohony.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /utf-8 /W4 /WX` test `tests/turn_operation_codec_test.cpp` pro dokončení s českým Unicode escape, pending/failed, nekonzistentní status/result, návrh, duplicitu, cizí data, zápornou revision a velikost. Po cílené revizi byla přidána hranice 500 znaků pro odpověď a 200 znaků pro důvod včetně kontroly UTF-8; znovu prošly testy parseru, `conversation_session_test.cpp` a `conversation_text_codec_test.cpp`. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 433 B flash).
- Omezení: parser ještě není připojený k HTTPS GET; nepřebírá návrhy výběru ani fyzické výstupy. Runtime TLS a deska bez B1 neověřeny.
- Předání: přidat GET poll transport s omezeným tělem a hostitelským guardem, potom fake událost textového tahu.

### D44 – 29. 9. 2026 – E4.3c7 korelace pollu

- Rozsah: `TurnPollGuard` porovná očekávaný operation ID, `deviceId`, `bootId`, `conversationId`, `clientTurnId`, `generation` a `deviceRevision` s odpovědí i aktuální generací/revision. Pozdní/cizí odpověď zahodí; pending/running pouze čeká, failed/cancelled/unknown ukončí. Pro fake text přijme jen dokončené `ask`/`no_selection` bez návrhu a s omezenou neprázdnou odpovědí. `propose` zůstává odmítnutý, dokud není ověřena expirace a fyzická potvrzovací cesta. Guard nemá přístup k pohonům.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /W4 /WX` test `tests/turn_poll_guard_test.cpp` pro každé korelační pole, změnu generace/revision, čekání, chybu a odmítnutí návrhu. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 345 B flash); PlatformIO bylo spuštěno plnou cestou k `pio.exe`.
- Omezení: není JSON parser výsledku ani HTTPS poll; guard pracuje nad již parsovanými daty. Neproběhlo síťové ani HW ověření.
- Revize a doručení: E4.3c7 je v commitu `44e2f878a455bf29c48914808b8774cf499854a6`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: přidat omezený parser operace a GET poll nad HTTPS, potom napojit na fake událost textového tahu při zachování všech kontrol.

### D43 – 29. 9. 2026 – E4.3c6 HTTPS textový tah

- Rozsah: `EspIdfConversationTextTransport` sestaví pouze pevnou HTTPS cestu nad validovaným backend hostem a kanonickým UUID sezení, odešle tělo `ConversationTextCodec` s Authorization/Idempotency-Key a přijme nejvýše 128 B odpovědi 202 přes `OperationAcceptedCodec`. CA bundle a hostname kontrola zůstávají povinné; redirect ani automatický auth retry nejsou povolené. Parsovanou 401 vrací i při `ESP_FAIL` připnutého ESP-IDF.
- Ověření: v `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 345 B flash); PlatformIO bylo spuštěno plnou cestou k `pio.exe`. Parser D42 prošel hostitelsky, síťový adaptér nemá živý TLS test.
- Omezení: transport není zapojený do síťové úlohy ani koordinačního modelu, protože chybí explicitní vstup textového tahu; bez B1 není možné ověřit běh na desce. Nadměrná pomalu streamovaná odpověď může zdržet síťovou úlohu obdobně jako u start adaptéru D39. Nenahrávalo se.
- Revize a doručení: E4.3c5–6 jsou v commitu `86a33fe6333f89c2356b8cdec4606536e130b60d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: navázat hostitelsky testovaným 401 retry se stejným tělem/UUID a doplnit korelovaný poll; před runtime připojením ověřit HTTPS stavové kódy.

### D42 – 29. 9. 2026 – E4.3c5 potvrzení operace

- Rozsah: `OperationAcceptedCodec` čte nejvýše 128 B odpovědi `202` textového tahu a přijímá pouze objekt s jediným `operationId` v kanonickém UUID formátu. Odmítá další/duplicitní pole, escaped hodnoty a trailing data; při chybě vyprázdní výstup.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /W4 /WX` test `tests/operation_accepted_codec_test.cpp` pro platný objekt, duplicitu, neznámé pole, vadné UUID, escape a velikost. Oba firmware buildy prošly společně s D43.
- Omezení: parser ještě nepřijímá reálnou HTTPS odpověď; není poll ani korelace výsledku. B1 údaje o revizi desky zatím nejsou dostupné, proto neproběhlo nahrání ani runtime TLS ověření.
- Předání: připojit omezený POST textového tahu k HTTPS adaptéru a následně poll s porovnáním všech šesti korelačních polí.

### D41 – 29. 9. 2026 – E4.3c4 JSON textového tahu

- Rozsah: `ConversationTextCodec` staví přesné JSON tělo `POST /v1/conversations/{id}/turns`, escapuje uvozovky, zpětná lomítka a řídicí znaky, odmítá neplatné UTF-8, více než 1000 znaků, prázdný text, chybná UUID, masku mimo 0–63 a tělo nad 4096 B. Sdílí validaci UTF-8 s existujícím `ConversationSession` a UUID se start kodekem; chování modelu se nemění. Idempotency-Key zůstává samostatný validovaný vstup pro pozdější HTTP hlavičku.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /utf-8 /W4 /WX` test `tests/conversation_text_codec_test.cpp` pro přesné tělo, české UTF-8, escaping, délku, vadné UTF-8, masku a ID. Původní `tests/conversation_session_test.cpp` prošel po vytažení validace. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 261 B flash); PlatformIO bylo spuštěno plnou cestou k `pio.exe`.
- Omezení: žádné HTTP odeslání tahu, parse `operationId` ani poll výsledku. Kodek sám žádný backend ani pohon nevolá. Nenahrávalo se.
- Revize a doručení: E4.3c4 je v commitu `9ed073cc2144df1805d788c8fda2388466f0058d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: přidat omezený parser 202 `operationId` a HTTPS textový tah; pak korelované pollování.

### D40 – 29. 9. 2026 – E4.3c3 jedno obnovení tokenu

- Rozsah: `ConversationStartCoordinator` připojuje device scope k tokenovému modelu a portu založení sezení. Při 401 vyžádá přes existující `DeviceTokenSession` právě jedno nové přihlášení a opakuje stejný `ConversationStartRequest` včetně Idempotency-Key. Neplatné ID/scope, serverové 409/429 a vadný úspěch odmítá bez vytvoření lokálního sezení. Hostitelský fake port umožňuje ověřit pořadí bez sítě.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /W4 /WX` test `tests/conversation_start_coordinator_test.cpp` pro 401→nový token, stejné UUID při retry, scope, 409, vadný 201 a opakování po výpadku. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 141 B flash); oba příkazy byly spuštěny plnou cestou k `pio.exe`.
- Omezení: koordinátor se zatím nevytváří v síťové úloze a negeneruje UUID; volající musí uchovat idempotency key přes retry. Bez události startu rozhovoru ani PTT zůstává provozní spojení pouze tokenové. Nenahrávalo se.
- Revize a doručení: E4.3c3 je v commitu `701483d2d8eccf612d6bf83b347645f473b839fd`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: připojit start k explicitní události bez telefonu a připravit textový tah a poll; HTTP adaptér před použitím ověřit integračním TLS testem/na desce.

### D39 – 29. 9. 2026 – E4.3c2 HTTPS adaptér startu

- Rozsah: `EspIdfConversationStartTransport` připraví pevnou HTTPS cestu `/v1/conversations` z validovaného DNS hostu, předá RAM access token v Authorization a stabilní UUID v `Idempotency-Key`, odešle JSON kodeku a přijme pouze omezené tělo odpovědi 201. Používá CA bundle a hostname kontrolu, vypíná redirect a automatické auth retry; tokenovou hlavičku po požadavku smaže. U odpovědí mimo 201 nenačítá potenciálně velké tělo, aby se zachoval stav 401 pro budoucí jediné obnovení tokenu. Připnuté ESP-IDF vrací při vypnutém auth retry pro platnou 401 chybu `ESP_FAIL`; oba HTTPS adaptéry proto čtou parsovaný HTTP status i v tomto případě a úspěšné tělo dál vyžadují jen při `ESP_OK`.
- Ověření: `pio run` pro ESP-IDF po opravě 401 prošel (40 588 B RAM, 1 003 813 B flash). `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 280 081 B flash). Hostitelský kodek D38 prošel; cílená kontrola zdrojů našla a po opravě znovu ověřila zvláštní chování 401 v připnutém ESP-IDF 6.1. Síťový adaptér nemá hostitelský fake TLS test. Příkazy PlatformIO byly spuštěny plnou cestou k `pio.exe`.
- Omezení: adaptér je sestavený, ale dosud jej nevolá síťová úloha. Není zdroj zahájení rozhovoru/UUID, textový tah ani poll; skutečné spojení/CA/hostname není ověřené na desce. 512B buffer brání růstu paměti na vadné odpovědi, ale `esp_http_client_perform` může pomalu streamované nadměrné 201 tělo dočítat déle než 5s timeout jednoho čtení; řešit celkovou lhůtou při integraci. Nenahrávalo se.
- Revize a doručení: E4.3c2 je v commitu `4cd6e290f7f56aee4e3a2f7beb95579115e01aea`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: připojit start po explicitní události se stabilními ID a poté textový tah/poll; na 401 použít `DeviceTokenSession::HandleUnauthorized` bez změny idempotency key a těla.

### D38 – 29. 9. 2026 – E4.3c1 kodek založení konverzace

- Rozsah: `ConversationStartCodec` sestaví pevný JSON požadavek `/v1/conversations` s validovanými identifikátory, `cs-CZ` a vypnutým audio/image souhlasem; UUID idempotency key předává jako oddělený vstup pro budoucí HTTPS hlavičku. Omezený parser přijímá jen `conversationId`, `expiresAt` a přesné limity backendu, odmítá duplicity, neznámá pole, escaped hodnoty a odpověď nad 512 B. Kodek se nedotýká fyzických výstupů.
- Ověření: v `C:\Development\DildoVander` prošel MSVC hostitelský test `tests/conversation_start_codec_test.cpp` s `/std:c++14 /W4 /WX`; kontroluje deterministické tělo, opakovaný požadavek, vadná ID, platnou odpověď i vadný JSON. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 777 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 081 B flash); oba příkazy byly spuštěny plnou cestou k `pio.exe`.
- Omezení: ještě není HTTPS volání, runtime generování UUID, navázání na token, textový tah ani poll. Kodek je testovaný software, nikoli E4 E2E akceptace. Nenahrávalo se.
- Revize a doručení: E4.3c1 je v commitu `f232f9b636897c33b854c5763dfc2d293f804358`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: navázat blokujícím HTTPS adaptérem na samostatné síťové úloze a poté obsluhou textového tahu s přesnou korelací odpovědi; WSS zůstává další krok.

### D37 – 29. 9. 2026 – E4.3b Wi-Fi a časová brána

- Rozsah: `NetworkSettings` validuje servisně uložené SSID, WPA2 heslo a jediné DNS jméno backendu; `EspIdfNetworkSettings` čte `wand_network` NVS namespace a bez kompletní konfigurace síť nespouští. `EspIdfWifiStation` používá STA a ukládá konfiguraci Wi-Fi driveru jen v RAM; přijatou IP událost při opakovaném pokusu nemaže, aby se neztratila při souběhu s připojením. `ReconnectPolicy` dává 1/2/4/8/16/30 s s jitterem. `EspIdfBackendNetwork` běží ve vlastní FreeRTOS úloze mimo 10ms řídicí smyčku; po IP čeká na SNTP a `TrustedTime` srovnává čas s datem sestavení a poslední důvěryhodnou hodnotou v NVS. Teprve pak provede HTTPS token exchange. Chyby konfigurace, času, Wi-Fi i credential ponechávají backend offline; žádný callback nesahá na pohony. Bez identity nebo konfigurace se task nezaloží.
- Ověření: v `kufr/firmware` `tests/network_settings_test.cpp` s MSVC `/std:c++14 /W4 /WX` prošel pro validaci hostu/hesla, reset konfigurace, reconnect s jitterem, datum buildu a čas přes restart včetně selhání NVS zápisu. `pio run` pro ESP-IDF 6.1 prošel (40 588 B RAM, 1 003 777 B flash z 6 553 600 B slotu); `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 279 929 B flash). Příkazy PlatformIO byly spuštěny plnou cestou k `pio.exe`.
- Omezení: chybí fyzicky střežený servisní zápis NVS hodnot, takže bez něj na prázdné desce síť nezačne. Wi-Fi, SNTP, TLS hostname/CA ani restart nebyly ověřeny na desce; build a hostitelské fake testy nejsou fyzická akceptace. SNTP samo není autentizovaný zdroj času; konzervativní datum buildu a NVS high-water brání zjevnému návratu v čase, nikoli libovolnému posunu dopředu. Konverzační HTTPS a lokální WSS zbývají. Nenahrávalo se.
- Revize a doručení: E4.3b je v commitu `e626e2349ccd4cd5bdf07e2629dd960e4e99d29d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: pokračovat E4.3c HTTPS fake backend konverzací a E4.4 lokálním WSS; hardware/USB provisioning a runtime TLS zkontrolovat po B1.

### D36 – 29. 9. 2026 – E4.3a backend tokenový klient

- Rozsah: `DeviceTokenSession` drží access token pouze v RAM, obnovuje jej po 12 minutách, odmítá expiraci, po 401 zkusí jednu novou autentizaci a při odmítnutém device credential přechází do offline stavu. Porovnává `deviceId`, typ Bearer a dobu nejvýše 900 s. `TokenResponseParser` přijímá jen čtyři přesně typovaná pole do 512 B, odmítá duplicity, neznámá pole a escaped hodnoty. ESP-IDF adaptér sestavuje pouze HTTPS URL z validovaného hostu a pevné tokenové cesty, používá CA bundle/ověření hostname, blokuje redirect i automatické auth retry, omezuje tělo a neloguje credential ani token. Připnuté ESP-IDF 6.1 nemá komponentu `json`; po neúspěšném build pokusu ji nahradil testovaný omezený parser bez nového balíčku.
- Ověření: v `kufr/firmware` `tests/device_token_session_test.cpp` s hostitelským MSVC `/std:c++14 /W4 /WX` prošel: obnova, expirace, zachování ještě platného tokenu při dočasném výpadku, jednorázové 401, revokace, cizí zařízení, chybějící identita a vadný/duplicitní JSON. `pio run` pro ESP-IDF 6.1 prošel (16 892 B RAM, 207 441 B flash); `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 279 825 B flash). Příkazy PlatformIO byly spuštěny plnou cestou k `pio.exe`.
- Omezení: HTTPS adaptér je sestavený zdrojový komponent, ale není připojen k Wi-Fi, servisnímu backend hostu ani samostatné síťové úloze; žádné skutečné TLS spojení či fake backend E2E nebylo provedeno. Bez důvěryhodného času se ještě nesmí volat. Nahrání do desky čeká na B1. Token v RAM je softwarový model, ne hardwarové zabezpečení.
- Revize a doručení: E4.3a je v commitu `f95292799a5aedfab1a5b8f06c050f55ad263f82`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: navázat E4 Wi-Fi konfigurací, ověřením času a síťovou úlohou; poté volat token endpoint a fake backend přímo z ESP32. Lokální WSS zbývá.

### D35 – 29. 9. 2026 – E4.2b model servisní identity

- Rozsah: `DeviceIdentity` validuje verzovaný záznam s omezeným `deviceId` a 32 bajty device credential. Z prázdného úložiště vzniká stav bez identity, chybné čtení či formát zablokuje použití. Zápis model dovolí jen při současně aktivním servisním režimu a odpojené větvi pohonů; samotné fyzické vstupy zatím nejsou zapojené. Pro budoucí HTTP Authorization poskytuje credential jako 64 malých hexadecimálních znaků, které backend hashují jako UTF-8. ESP-IDF smyčka identitu čte z odděleného NVS blobu a loguje jen stav, nikoli hodnotu. Záznam se maže z dočasného pracovního bufferu.
- Ověření: v `kufr/firmware` hostitelský MSVC test `tests/device_identity_test.cpp` s `/std:c++14 /W4 /WX` prošel: fyzická softwarová brána, restart, hex formát, vadná data a selhání čtení/zápisu. `pio run` pro ESP-IDF 6.1 prošel (16 892 B RAM, 207 441 B flash), `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 279 621 B flash); oba byly spuštěny plnou cestou k `pio.exe`.
- Omezení: žádný USB servisní protokol ani zdroj bezpečné náhody/credential zatím nejsou připojené. Neexistuje fyzicky ověřený signál servisního režimu či odpojené napájecí větve (B1); zápis z běžícího firmwaru není zpřístupněn. NVS na desce ani fyzická ochrana flash se neověřovaly; šifrování/secure boot a nevratné eFuse vyžadují samostatné rozhodnutí dle plánu. Není Wi-Fi ani HTTPS klient. Nenahrávalo se.
- Revize a doručení: E4.2b je v commitu `aaa7038b47be6de23f90d3e167689cc8816e5288`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: pokračovat nezávislou softwarovou částí E4 Wi-Fi/HTTPS fake backend klienta; fyzický servisní zápis až po B1.

### D34 – 29. 9. 2026 – E4.2a trvalý journal

- Rozsah: `PersistentCommandJournal` ukládá verzovaný záznam s `commandId`, číslem přihrádky, příznakem otevřeného příkazu a maskou dostupnosti. Začátek se zapisuje před prvním výstupem; uzavření příkazu zapisuje masku i konec jedním blobem. Prázdné NVS znamená nulovou masku. Chyba čtení/zápisu a poškozený záznam blokují další řízení. `EspIdfJournalStorage` čte/zapisuje blob přes NVS a `nvs_commit`, bez automatického mazání při chybě. `espidf_main.cpp` inicializuje NVS až po bezpečném vypnutí, při chybě latchuje safety fault a při otevřeném journalu požaduje kontrolu. Arduino profil NVS adaptér vylučuje.
- Ověření: v `kufr/firmware` hostitelský MSVC test `tests/persistent_command_journal_test.cpp` s `/std:c++14 /W4 /WX` prošel; pokrývá restart, otevřený záznam, uzavření s maskou, poškozený blob, selhání čtení/zápisu a blokaci pohybu v `DeviceControl`. `pio run` pro ESP-IDF 6.1 prošel (16 892 B RAM, 206 181 B flash); `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 279 529 B flash). Příkazy PlatformIO byly spuštěny plnou cestou k `pio.exe` v uživatelském prostředí.
- Omezení: NVS nebylo čteno ani zapsáno na skutečné desce a nebyl simulován výpadek napájení během flash operace. `DeviceControl` ještě není připojený ke vstupu živé ESP-IDF smyčky; sériový Arduino prototyp jej rovněž nepoužívá. Není servisní device identita ani síťová vrstva. Nenahrávalo se do desky.
- Revize a doručení: E4.2a je v commitu `25469a0ac5a5c38bfd4a17e740f9c3eeeb857ded`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: pokračovat E4.2b servisní identitou mimo image, pak E4 Wi-Fi/HTTPS/WSS; pro upload a fyzickou akceptaci platí B1.

### D33 – 29. 9. 2026 – E4.1 sestavený ESP-IDF základ

- Převzetí a rozsah: po dokončení E7 byl převzat první E4 software slice. PlatformIO `espressif32@7.1.3` nainstalovalo `framework-espidf@4.60100.0` (ESP-IDF 6.1.0). Přibyly CMake soubory, `sdkconfig.defaults` s předběžným profilem N16R8, 16MB tabulka oddílů a `app_main` se safety portem bez GPIO a neblokující smyčkou. Generované IDF sdkconfig se ignoruje. ESP-IDF je výchozí profil; Arduino sériový profil má oddělený zdrojový filtr.
- Ověření: `pio run` prošel pro ESP-IDF 6.1.0; obraz obsahuje platný checksum/hash, 16MB flash a 80MHz režim. Výstup: 16 604 B RAM a 181 137 B flash z OTA slotu 6 553 600 B. Účinný sdkconfig potvrzuje QIO výběr (boot image začíná v DIO dle ESP-IDF), octal PSRAM a vlastní 16MB oddíly. `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 279 333 B flash). Všechny tři hostitelské C++ testy (`selection_sequence`, `device_control`, `conversation_session`) prošly s MSVC `/W4 /WX`; zdroje čistého jádra se neměnily. Kopii tabulky oddílů ověřil lokální `gen_esp32part.py --flash-size 16MB`; `git diff --check` prošel. První pokusy instalace toolchainu selhaly na `HTTPClientError`, pozdější pokus nainstaloval všechny balíčky a build dokončil.
- Omezení: sestavení neověřuje boot ani PSRAM na skutečné desce, fyzický inhibit/watchdog, NVS, Wi-Fi, TLS či WSS. Nenahrávalo se. Volba generické `esp32-s3-devkitc-1` se skutečnou N16R8 revizí ověří až po B1.
- Revize a doručení: E4.1 je v commitu `cdc7f9448e2b2d656bb13417366daa0756499088`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: E4.1 software slice je sestavený; následuje E4.2 NVS identita a journal. Fyzické ověření až po B1.

### D32 – 29. 9. 2026 – E7.6 nákladová rezervace a E7 integrace

- Rozsah: SQLite schéma v6 přidává `cost_reservations`, sezením omezené čerpání a globální denní/měsíční agregáty v miliontinách USD. `OperationJournal.Reserve` zapisuje odhad ve stejné transakci jako operaci, turn kvótu a idempotency key. Opakovaný key neúčtuje podruhé; strop je fail-closed bez všech tří kladných konfiguračních hodnot. `SettleCost` transakčně vrací nevyužitý rozdíl nebo eviduje nadodhad a blokuje další placenou práci. Neznámé rozběhnuté volání si rezervaci ponechá. Technické cost řádky se čistí po 7 dnech, agregáty po 90 dnech. Fake trasy mají nulový odhad a nevyvolávají placený provider.
- Ověření: `dotnet build kufr/backend/backend.csproj --no-restore` bez varování; `dotnet run --project kufr/backend-tests/BackendTests.csproj` ověřil paralelní rezervace proti stropům, agregaci napříč zařízeními, retry/kolizi, vyúčtování menší i vyšší ceny, chybějící/částečnou konfiguraci, restart s `unknown` a ponechanou rezervací, osazenou v5→v6 migraci a 7/90denní retenci. `node kufr/backend/tests/smoke.mjs` ověřil reálné HTTP přístupy, souběžný stejný key, limity, všechny fake trasy, restart a nepřítomnost credential/tokenů i testovacího textu v logu. `node kufr/contracts/verify-backend-openapi.mjs` prošel.
- Omezení: částky v provozu nejsou schválené ani nakonfigurované (B4), proto žádná kladná rezervace v běžném backendu neprojde a provider se nevolá. Testy ověřují lokální loopback HTTP a fake média, nikoli fyzický ESP32, HTTPS deployment či účtování poskytovatele. Nadodhad po skutečném volání může překročit strop již vzniklým nákladem, ale je evidován a další placené požadavky se odmítnou.
- Revize a doručení: E7.6 je v commitu `011eeea045088987bda7661a4a3487778e79629f`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D31 – 29. 9. 2026 – E7.5b fake syntéza a krátkodobý WAV

- Rozsah: `POST /v1/conversations/{id}/speech` přijímá pouze UUID již dokončeného textového tahu a `outputTarget=device|phone`, vyžaduje audio souhlas a nepřijímá volný text. Fake operace vygeneruje tichý PCM16 mono 32 kHz WAV, uloží jej pouze do RAM na 120 s a vrátí device-scoped `audioId` pro `GET /v1/audio/{audioId}`. Současně je povolen jeden aktivní výstup; další lze začít po `DELETE` původní speech operace nebo po expiraci audia. Úspěšná syntéza obnovuje 15minutovou idle lhůtu do absolutního 30minutového stropu. Zrušení, ukončení sezení, expirace i periodický worker nulují a odstraní audio buffer; 410 tombstone drží pouze technickou identitu další 24 h. Po restartu se WAV negeneruje znovu.
- Ověření: `dotnet build kufr/backend/backend.csproj --no-restore` bez varování; `dotnet run --project kufr/backend-tests/BackendTests.csproj` ověřil cílové zařízení, cizí přístup, 120s expiraci/410, opětovnou syntézu po expiraci a rušení, odstranění po ukončení sezení, prodloužení idle lhůty přes původní hranici a úklid workerem bez provozu. `node kufr/backend/tests/smoke.mjs` nad HTTP hostem ověřil chybný turnId/target, cizí scope, idempotenci, souběh, WAV hlavičky/bajty, smazání audia po konci sezení a ztrátu po restartu. Velký chunked JSON požadavek byl odmítnut 413. `node kufr/contracts/verify-backend-openapi.mjs` prošel.
- Omezení: WAV obsahuje pouze krátké ticho, není to TTS ani důkaz přehrání na zařízení; bez `DELETE` klient čeká na 120s expiraci před dalším výstupem. Audio není v SQLite, po restartu staré audioId vrací 404. Placený provider a jeho nákladové limity chybí.
- Revize a doručení: E7.5b je v commitu `c76ceb30632b23f71ef07abeb2e6e6099e1b557b`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D30 – 29. 9. 2026 – E7.5a omezený WAV upload a fake přepis

- Rozsah: `POST /v1/conversations/{id}/transcriptions` přijímá pouze autentizovaný multipart `audio/wav` se souhlasem k audiu, UUID identitou tahu a shodným boot/generation. Streamovaný upload má 2MB strop požadavku a validaci RIFF PCM16 mono 32 kHz, maximálně 1 920 000 bajtů vzorků. Vlastní audio buffery se po výsledku nebo chybě vynulují. Operace vrací pevný fake přepis `deviceMicrophone`; audio ani přepis se neukládají do SQLite. Schéma v5 ukládá pouze typ operace, zachovává v4 řádky jako `turn`; retry hashuje normalizovaná pole a WAV, takže změna multipart boundary není konflikt. Fake transkripce mají samostatný limit deseti za živé sezení a nespotřebují kvótu dialogových tahů.
- Ověření: `dotnet build kufr/backend/backend.csproj --no-restore` bez varování; `dotnet run --project kufr/backend-tests/BackendTests.csproj` ověřil fake přepis, retry/kolizi, ztrátu výsledku po restartu, nepřítomnost testovacího audio payloadu v SQLite a migraci osazených schémat v1–v4 do v5. `node kufr/backend/tests/smoke.mjs` nad HTTP hostem ověřil multipart WAV nad původní 4kB JSON mezí, chybné vzorkování, překročenou délku i 2MB velikost s Content-Length a chunked přenosem, cizí device scope, souhlas, idempotenci přes jiné multipart boundary a restartové `result_expired`.
- Omezení: fake přepis nepoznává skutečnou řeč, neověřuje STT providera ani kvalitu nahrávky. Transkripce mají strop v RAM jednoho sezení; SQLite drží technickou idempotenci po restartu, kdy se staré sezení neobnovuje. Speech/audio výstup a nákladová rezervace zbývají.
- Revize a doručení: E7.5a je v commitu `5c2984ff9053bebc56bf78e872859c0966aaf460`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D29 – 29. 9. 2026 – E7.4b explicitní rušení

- Rozsah: `DELETE /v1/operations/{id}` a `DELETE /v1/conversations/{id}` vyžadují device access token, UUID `Idempotency-Key` a prázdné tělo; cizí identita je odmítnuta. SQLite schéma v4 přidává 24hodinový záznam úspěšného DELETE pro bezpečný retry po restartu. Zrušení operace potlačí publikaci výsledku v RAM; ukončení sezení odstraní jeho RAM obsah a označí jeho operace jako `cancelled`. Dokončení již zrušené rezervace nesmí přepsat status. Rezervovaný tah se nerefunduje.
- Ověření: `kufr/backend` `dotnet build backend.csproj` bez varování; z kořene `dotnet run --project kufr/backend-tests/BackendTests.csproj` ověřil vlastnictví, pozdní dokončení, restartový retry, migrace osazených v1/v2/v3 schémat a retenci páté SQLite tabulky. `node kufr/backend/tests/smoke.mjs` prošel nad HTTP hostem: cizí scope, cancellation/end, vymazaný výsledek, opakování po restartu, současný DELETE/tah a odmítnutí běžného i chunked těla DELETE.
- Omezení: fake tah se dokončuje synchronně, takže souběh skutečného provider volání nebyl ověřen; tento kód konzervativně brání pozdnímu přepsání. SQLite ponechává technické identity do 24 h kvůli idempotenci. Placené volání ani jeho nákladová rezervace zatím neexistují. Hlasové trasy zbývají.
- Revize a doručení: E7.4b je v commitu `6730b4cf9d4ecd4fddc7d5ec1d0b4598465904e8`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D28 – 29. 9. 2026 – E7.4a časovaná retence

- Rozsah: `RetentionWorker` každou minutu spouští cleanup grantů, paměťových sezení/výsledků a 24hodinových technických SQLite záznamů. `ConversationStore` nyní odstraňuje fake výsledek při 15minutovém idle timeoutu a nanejvýš po 30 minutách od vytvoření sezení; polling pak vrací `failed/result_expired` bez opakování fake/provider práce. `GET /v1/operations` také provádí konzervativní kontrolu expirace při čtení. SQLite cleanup běží v transakci; selhání sweep se loguje jen typem chyby, nikoli obsahem požadavku.
- Ověření: `kufr/backend` `dotnet build backend.csproj` bez varování; `kufr` `dotnet run --project backend-tests/BackendTests.csproj` s řízeným časem ověřil dostupnost výsledku v živém sezení, vymazání po 15 minutách, absolutní strop 30 minut i fyzické smazání všech čtyř technických tabulek po 24 hodinách. Test navíc spustil skutečný `RetentionWorker` s testovacím 1s intervalem, po posunu hodin bez klientského provozu ověřil odstranění RAM výsledku i SQLite záznamu a korektní zastavení služby. `node backend/tests/smoke.mjs` znovu prošel nad HTTP hostem.
- Omezení: explicitní DELETE/cancellation dosud není; RAM obsahu se po době životnosti odstraní reference, nikoli zaručeně přepíší všechny kopie v .NET heapu. Výsledky fake neobsahují návštěvnický text. Obnova workera po selhání jednotlivého sweep nebyla injekčně testována; kód selhání zaloguje typem a pokračuje. Nákladové limity a zbylé trasy jsou otevřené.
- Revize a doručení: E7.4a je v commitu `b403be5cbd68902b0d3313ddc1dcac1f931f79c6`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D27 – 29. 9. 2026 – E7.3c trvalá idempotence založení sezení

- Rozsah: dopředná transakční migrace SQLite na v3 přidává `conversation_creates` s device-scoped create key, SHA-256 hashem syrového těla, původní conversationId/response expiry a 24h expirací záznamu. `ConversationStore.Create` vyhodnotí retry před kontrolou živého sezení; po restartu stejný key/tělo vrátí původní identitu, jiný obsah 409. Ztracený obsah paměťového sezení se neobnovuje, staré ID nepřijme nový tah a nový rozhovor vyžaduje nový key. Technický journal neukládá text návštěvníka.
- Ověření: `kufr/backend` `dotnet build backend.csproj` bez varování; `kufr` `dotnet run --project backend-tests/BackendTests.csproj` ověřil create retry/kolizi po znovuotevření DB, migraci osazeného v1 a v2 schématu do v3, zachování kvóty a absenci testovacího textu ve všech čtyřech SQLite tabulkách. `node backend/tests/smoke.mjs` zastavil a znovu spustil skutečný HTTP proces nad stejným souborem: starý token odmítnut, původní create key vrátil stejné conversationId, změněné tělo 409, stejný UUID key jiného zařízení dostal vlastní sezení, ztracený výsledek `result_expired` a nový tah starého sezení 404.
- Omezení: create odpověď po restartu může odkazovat na již zaniklé sezení; klient musí použít nový key pro nový rozhovor. Staré RAM sezení se bezpečně neobnovuje. Nákladový rozpočet, cleanup/retence, cancellation a další HTTP trasy stále chybí.
- Revize a doručení: E7.3c je v commitu `cf7bc1c679c84fdc3abf98fab83a92ff0919843d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D26 – 29. 9. 2026 – E7.3b atomická SQLite kvóta tahů

- Rozsah: dopředná transakční migrace schématu v1→v2 přidává `conversation_turn_usage` a index expirace. `OperationJournal.Reserve` pod okamžitou transakcí kontroluje a rezervuje nejvýše deset tahů pro device/conversation zároveň s pending operací a idempotency key; shodný retry limit znovu nespotřebuje. RAM limit zůstává rychlou předběžnou kontrolou, SQLite je trvalá hranice. Nejsou zde placené výdaje; nákladová rezervace před provider voláním se musí doplnit zvlášť.
- Ověření: `kufr/backend` `dotnet build backend.csproj` bez varování; `kufr` `dotnet run --project backend-tests/BackendTests.csproj` ověřil devět rezervací a dva paralelní různé klíče na hranici desáté (právě jeden přijat), odmítnutí dalšího, shodný retry na limitu, zachování limitu po novém otevření DB a migraci osazeného v1 schématu na v2 se zachováním operationId a idempotence. `node backend/tests/smoke.mjs` nad skutečným HTTP hostem/SQLite opět prošel včetně paralelních požadavků na hranici deseti tahů.
- Omezení: založení sezení a jeho souběh jsou stále paměťové; kvóta je počet tahů, nikoli dolarový rozpočet. Cleanup je oportunní, zbytek E7 akceptace zůstává otevřený.
- Revize a doručení: E7.3b je v commitu `4dc6128658279ef7c1c909265414d8c3fc995351`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D25 – 29. 9. 2026 – E7.3a SQLite journal textového tahu

- Rozsah: připnutý `Microsoft.Data.Sqlite` 10.0.12, schéma v `OperationJournal.cs` s `turn_operations` a `turn_idempotency` (verze 1, pouze technické identity, hash těla, status, expirace). Rezervace pending operace a klíče je v jedné okamžité SQLite transakci; dokončení fake výsledku se zaznamená zvlášť, text zůstává jen v RAM. Po restartu pending/running přejde do `unknown` bez znovuspuštění; dokončený ztracený výsledek vrací `failed/result_expired`. Soubor `.instance.lock` brání druhé živé instanci a recovery cizí rozběhnuté práce. Čtení výsledku kontroluje databázovou 24h expiraci i při živé RAM cache. Databázová cesta je povinná konfigurace mimo git.
- Ověření: `kufr/backend` `dotnet build backend.csproj` bez varování a `node tests/smoke.mjs` prošly se skutečným lokálním HTTP hostem a dočasným SQLite souborem. `kufr` `dotnet run --project backend-tests/BackendTests.csproj` ověřil stejný key po restartu, konflikt různého hashe, atomický paralelní zápis, `unknown` po restartu pending rezervace, `result_expired` bez ztraceného obsahu, odmítnutí druhého vlastníka DB, 24h expiraci RAM výsledku a nepřítomnost testovacího textu v obou SQLite tabulkách. Počáteční pokus číst otevřený DB soubor jako prostý text na Windows selhal sdílením souboru; test teď ověřuje hodnoty přes SQLite spojení.
- Omezení: per-session limit deseti tahů je stále v RAM a není součástí databázové transakce; nejsou tu žádné placené výdaje ani jejich rezervace. Create idempotence je také paměťová. Expirace DB se čistí oportunně při další rezervaci; pravidelný cleanup, citlivá 15minutová retence, rušení a úplné HTTP API chybí. SQLite schema v tomto slice vzniká při startu; produkční migrace/backup a souběh více instancí nejsou přijaty. Nepoužívat pro placené tahy.
- Revize a doručení: E7.3a je v commitu `1af9e2cafb03a83b664b6ef1fcd9db6f8ecc7ed7`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D24 – 29. 9. 2026 – E7.2 fake textový tah a operace

- Rozsah: `POST /v1/conversations/{id}/turns` ověřuje access token, vlastnictví sezení, boot/generation/catalog, tvar a limit textu, šestibitovou ready masku, revizi, unikátní clientTurnId a idempotency key. Přijímá nejvýše deset tahů a vrací `202` s operationId; `GET /v1/operations/{id}` vrací device-scoped typovanou dokončenou fake operaci s korelací a pevným `ask`/`proposal:null`. Text návštěvníka se neukládá do operace ani logu. Idle expirace se obnoví nanejvýš do 30minutové absolutní délky. Po read-only review se replay klíče vyhodnocuje před kontrolou živého sezení, aby 24hodinový záznam fungoval i po jeho expiraci. Fake adapter nemá návrh přihrádky ani fyzický výstup.
- Ověření: v `kufr/backend` `dotnet build backend.csproj` prošel bez varování; `node tests/smoke.mjs` ověřil reálné HTTP přijetí/polling, cizí device scope, shodný retry/kolizi těla, duplicitní clientTurnId, korelační identitu, nepřítomnost vstupního textu ve výsledku a paralelní požadavky na hranici deseti tahů. Z `kufr` `dotnet run --project backend-tests/BackendTests.csproj` prošel bez varování: řízený čas ověřil expiraci sezení/tokenů, retry po expiraci a odmítnutí duplicitního hashe credential.
- Omezení: fake operace se dokončí synchronně a neověřuje souběh skutečných provider volání. Sezení/operace/idempotence jsou v paměti; operace se zatím nečistí a dokončený fake výsledek lze číst i po expiraci sezení. SQLite, restart, cleanup/retence, cancellation, STT/TTS a placená AI zůstávají E7/E8 prací. HTTP test je pouze loopback.
- Revize a doručení: E7.2 je v commitu `7b89c81670549a5817f6d2ef6a9495530d4ea0f6`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D23 – 29. 9. 2026 – E7.1 backend identita a fake sezení

- Rozsah: nový `kufr/backend/` s připnutým .NET 10 SDK, Minimal API trasami `POST /v1/device-access/token` a `POST /v1/conversations`. Servisně provisionovaný credential se ověřuje podle SHA-256 hashe z konfigurace mimo git; duplicitní ID/hash je odmítnut při startu. Náhodný access token platí 15 minut, v serverové paměti je uložen jen jeho hash a souběžné granty mají strop 16/zařízení. Sezení kontroluje device scope, locale, consent a tvar generační identity, vrací limity OpenAPI; jedno současné sezení na zařízení a opakování `Idempotency-Key`/stejného těla se zpracuje atomicky pod zámkem. README a `.gitignore` odpovídají novému projektu. Klient nedostává ovládací povel aktuátoru.
- Ověření: `kufr/backend`: `dotnet build backend.csproj --no-restore` prošel bez varování po počáteční opravě neexistujícího `Convert.TryFromHexString`; `node tests/smoke.mjs` spustil skutečný loopback HTTP host, ověřil dvě identity, odmítnutí chybného credential a použití credential místo access tokenu, cizí device scope, shodný retry, konflikt změněného těla, paralelní založení sezení a strop tokenů. Log procesu neobsahoval testovací credentials/tokeny. Read-only review opravila počáteční rozdíl mezi 15minutovou expirací odpovědi a 30minutovou blokací dalšího sezení; tyto dva časy jsou nyní shodné.
- Omezení: konverzace zatím neprovádí tah, STT/TTS ani placené volání; tokeny, sezení a idempotence jsou paměťové a po restartu zaniknou. Cleanup je nyní oportunní při vytváření sezení, nikoli časovač; SQLite, retence obsahu a restartové zkoušky jsou další E7 práce. HTTP test používá pouze loopback; produkční HTTPS/reverse proxy a device provisionování nebyly akceptovány. E7 zůstává rozpracovaná.
- Revize a doručení: E7.1 je v commitu `5d4760db436f64375e952c0b83637a1ad65d06e5`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D22 – 29. 9. 2026 – E2.5b browser časová osa s důvěryhodným TLS

- Rozsah: beze změny runtime kódu. Dosavadní E2.5 HTTPS pozorovací stránka dostala chybějící browser ověření. Již důvěryhodný `localhost` ASP.NET development certifikát byl exportován a převeden výhradně v paměti do testovacího procesu; soukromý klíč se neukládal do repozitáře ani do dočasného PEM souboru. Testovací bearer token existoval jen v procesu a při browser připojení.
- Ověření: `dotnet dev-certs https --check --trust` našel dva platné důvěryhodné certifikáty. In-app Browser na `https://localhost:<dočasný-port>/simulator` otevřel stránku bez TLS varování. Chybný token vyvolal `Neplatný testovací token`; správný token připojil pouze pozorovací HTTPS proud, zobrazil šest přihrádek, režim, fázi, journal a revision. Samostatný autentizovaný WSS klient vyvolal C++ výběr a browser časová osa zobrazila hledání přihrádek 1–4 i požadavek vypnout pohony. Po výpadku klienta nový snapshot ukázal `Zotavení`, `Zastaveno`, neuzavřený journal a nedostupnou přihrádku 1. V úzkém viewportu bylo 6 přihrádek a `scrollWidth = clientWidth = 375`; stránka nemá ovládání Stop/lease. Dřívější `kufr/simulator npm test` z D21 nad stejným kódem zůstává platný.
- Omezení: jednorázový browser test neprokázal dokončení celé výběrové sekvence; klient bez obnovování lease čekal na `selection.result` a skončil timeoutem, poté se simulátor konzervativně zastavil. Dokončení sekvence proti C++ modelu bylo zvlášť ověřeno v D21. Časová osa ukazuje požadavky modelu, nikoli měřený pohyb západky, NVS či skutečný hardware.
- Revize a doručení: E2.5b je v commitu `e07bf12d6547aeaeacbb6e8ed982677c19034c0b`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D21 – 29. 9. 2026 – E3.2b mobilní UI proti E2 simulátoru

- Rozsah: `SimulatorDeviceConnection.ts` připojuje mobilní web pouze přes loopback E3.2a most; rozlišuje observer/manual lease, korelovaný heartbeat preflight s kontrolou revision/RTT, šestibitovou masku, výběr/reload, ACK/výsledek, konzervativní opakování stejného příkazu, Stop a nejasný výsledek po odpojení. `main.ts` odlišuje fake/E2 režim a při výpadku označí masku jako neaktuální; `FakeDeviceConnection.ts` sdílí typ snapshotu. Nový cílený Node test, script v `package.json` a README popisují oba režimy. Bearer token zůstává jen v místním mostu.
- Ověření: `kufr/mobilni-aplikace` pod Node 24.21.0 `npm test` – 4 testy prošly (nový lease a totožný retry, chybějící výsledek, rekonciliace reloadu/nejistý výběr, změněná revision v heartbeat); `npm run build` (`tsc --noEmit && vite build`) prošel. `kufr/simulator` s existujícím MSVC `bridge.exe`, OpenSSL a Node 24.21.0 `npm test` prošel včetně TLS/auth, lease, reload, Stop, proxy a media WSS. In-app Browser na místním E3.2a mostu proti skutečnému C++ modelu: observer zablokoval povely; acquire, výběr 1, dokončení 5/6, potvrzení reloadu 6/6, Stop a odpojení zobrazily očekávané stavy; při odpojení se tlačítka zablokovala a maska se označila neaktuální. Úzký viewport 390×844 a konzole bez chyby. `git diff --check` prošel.
- Instalační omezení: běžící místní Vite proces držel `esbuild.exe`, takže `npm ci` přímo v mobilní složce skončilo Windows `EPERM`; čisté `npm ci` ze stejného `package.json`/lockfile v dočasné složce prošlo (0 známých zranitelností). Závislosti v mobilní složce byly obnoveny kopírováním bez přepisu právě používaných binárek a následný test/build prošel.
- Omezení: fake/hostitelský simulátor není fyzický kufr, iOS ani nativní TLS. Protokolový snapshot nemá korelaci `state.get`, proto preflight používá korelovaný `heartbeat.ack` s revision a aktuální masku ze snapshotu; finální hranici drží kontrola revision/lease/TTL v E2 modelu. Chybějící fyzické ověření otevření se v UI neprohlašuje za úspěch. E2.5 browser vizualizace zůstává další krok.
- Revize a doručení: E3.2b je v commitu `e5311316c49bf7a1948f9d3d1808d8d7966085ed`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D20 – 29. 9. 2026 – E3.2a místní most k E2 WSS

- Rozsah: `simulator/mobile-dev-server.mjs` servíruje sestavený mobilní web pouze na `127.0.0.1`, `/dev-mode` určuje vývojový režim, `/dev/control` předává textové E2 zprávy mezi browser WebSocket a autentizovaným TLS WSS simulátoru. Přijímá pouze shodný origin, jeden browser socket a přísně validované control zprávy; bearer token a CA jsou jen v procesu mostu. `verify-socket.mjs` přidává kontrolu statické stránky, režimu, zakázaného cizího originu a `hello` přes most. README popisuje lokální spuštění a hranice.
- Ověření: v `kufr/simulator` pod Node 24.21.0 s MSVC `bridge.exe` a místním OpenSSL `node verify-socket.mjs` i celé `npm test` prošly. Test mostu kryje cizí origin, souběžný socket, `hello`/`welcome`, skutečné acquire přes C++ model a 9000bajtový rámec odmítnutý `1009` bez pádu procesu. Před opravou most poslal textovou zprávu jako binární Buffer a E2 server ji správně odmítl `1003`; převod na UTF-8 text odstranil chybu. Upstream používá `ca` s `rejectUnauthorized:true`. Browser UI most dosud nepoužívá.
- Omezení: lokální HTTP je jen vývojová loopback cesta; pro LAN/iPhone se nesmí použít. Origin kontrola blokuje cizí web, ale místní neprohlížečový proces ji může napodobit. Není to nativní TLS pinning, skutečná komunikace ESP32 ani E3 uživatelský integrační test. Následuje E3.2b.
- Revize a doručení: E3.2a je v commitu `1ce93a98c1095ccd2baa5e17fc4ff2cf4364032d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D19 – 29. 9. 2026 – E3.1 ruční webová fake obrazovka

- Rozsah: `mobilni-aplikace/src/features/device_connection/FakeDeviceConnection.ts` drží místní observer/manual lease, šestibitovou dostupnost, běžící simulovanou sekvenci, Stop s konzervativní nedostupností rozběhnuté přihrádky a explicitní fake kontrolu. `src/main.ts`/`style.css` zobrazují šest přihrádek, stav, převzetí/uvolnění, dvojí potvrzení výběru a založení; všechny akce jsou označené jako simulace bez připojení. Skrytí stránky uvolní lease. `index.html` a mobilní README odpovídají novému UI. Žádný token, síťový transport ani fyzický aktuátor nejsou součástí stránky.
- Ověření: v `kufr/mobilni-aplikace` pod Node 24.21.0 `npm ci` (0 známých zranitelností) a `npm run build` (`tsc --noEmit && vite build`) prošly. In-app Browser na `http://127.0.0.1:5173/`: výchozí observer zablokoval všech šest tlačítek, převzetí povolilo výběr, potvrzení spustilo fake sekvenci, dokončení snížilo masku, potvrzení založení ji obnovilo, Stop během dalšího výběru snížil masku a zakázal akce; konzole neměla chyby. Prohlédnut úzký viewport 390×844.
- Omezení: fake stav se resetuje při reloadu; časování/lease nejsou odvozeny z E2 C++ modelu, síťové chyby nejsou vyzkoušené. Mobil ani iPhone nejsou připojené ke kufru. Následuje E3.2 proti simulátoru; E2.5 stále čeká browser s důvěryhodným certifikátem.
- Revize a doručení: E3.1 je v commitu `b00faeade3d11dbc8e860e3471cc71579e635dbb`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D18 – 29. 9. 2026 – E2.9b oddělený fake media socket

- Rozsah: `media-control-v1.schema.json` a `media-validator.mjs` definují přísné JSON `media.hello/open/credit/end/cancel/result` s boot/manual lease/conversation generation a přiděleným uint32 stream ID. Po acquire jde náhodný `media.authorize` ticket pouze vlastnickému control socketu, ne do observer snapshotu; bez něj druhý držitel stejného bearer tokenu media stream neotevře. `server.mjs` otevírá samostatný TLS `/v1/media`, přijímá pouze telefonní PCM16 mono 32 kHz do fake `MediaStreamVerifier`, vynucuje 4096B rámec, 16KiB credit, 19 200–1 920 000 B sudé délky, SHA-256 a 1 s bez pokroku. Spojení se zavře při Stop, odpojení/expiraci lease, změně generation, běžícím výběru nebo chybě média; řídicí socket a heartbeat zůstávají oddělené. Po neplatné úvodní odpovědi native bridge se proces uklidí místo visícího serveru. Plán kapitoly 7 a README popisují fake rozsah.
- Ověření: `kufr/contracts` pod Node 24.21.0 `npm test` — 22 platných a 39 chybných control zpráv, OpenAPI, media frame a media JSON pozitivní/negativní struktury prošly. MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` pro bridge s přidanou interní expirací lease — úspěšně. `kufr/simulator` s OpenSSL/testovací CA `npm test` — původní bridge/WSS scénáře i `/v1/media` TLS, vlastnický ticket, vyjednání, credit, validní/chybný SHA-256, mezeru chunků, timeout, cancel, heartbeat během média, uvolnění lease a Stop prošly. První lokální pokus odhalil chybný JSON výstup po doplnění expiry; opraven, opakované testy prošly. Read-only risk review upozornilo na možnost připojení druhého držitele bearer tokenu podle lease ID z observer snapshotu; doplněný ticket a negativní test tuto cestu uzavírají.
- Omezení: fake příjem PCM jen průběžně hashují, neposkytují WAV/přepis, skutečný PSRAM, audio driver, TTS/JPEG ani HTTPS backend; `simulated:true` to výslovně označuje. Browser vizuální ověření E2.5 stále čeká na důvěryhodný certifikát. Následuje proveditelná UI část E3.1 nad fake stavem, E2 zůstává částečné.
- Revize a doručení: E2.9b je v commitu `88ed1e33907a0fb236b38aab2e8b8dc3824198c3`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D17 – 29. 9. 2026 – E2.9a binární media parser

- Rozsah: `contracts/media-frame-v1.mjs` kóduje a validuje 16bajtovou `KF` hlavičku, druh média, uint32 stream/sequence/length v little-endian a payload 1–4096 B. `MediaStreamVerifier` kontroluje jediný domluvený stream, pořadí, limit 1,92 MB/2 MiB/512 KiB podle druhu, 1 s bez pokroku a na konci délku/SHA-256; po chybě zavře stream bez dalšího přijetí. Test používá binární data, změněné hlavičky/délky, nesprávný stream/pořadí/hash a timeout; nic neukládá v base64.
- Ověření: `kufr/contracts` pod Node 24.21.0 `npm test` — 21 platných a 37 chybných control zpráv, OpenAPI a nový media frame test prošly. Firmware, simulátor a hardware se při tomto čistém kontraktním kroku neměnily.
- Omezení: neexistuje media JSON handshake, `/v1/media` WSS socket, 16KiB credit, skutečný audio buffer/PSRAM, driver ani HTTP upload. Následuje E2.9b; tyto podmínky zatím nejsou ověřené.
- Revize a doručení: E2.9a je v commitu `032fc3fff1f5af24ae33d749721197fab587155d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D16 – 29. 9. 2026 – E2.8b2 fake text/cancel přes WSS

- Rozsah: native bridge spouští stejný `ConversationSession` jako hostitelský test, do snapshotu dává zařízením založené conversation ID/generation/aktivní tah. Node WSS facade posílá validovaný UTF-8 text do bridge přes hex kódovaný interní pipe příkaz, vrací `conversation.ack` a po 300 ms fake `conversation.result` se `simulated:true`. Cancel, Stop a ztráta lease zneplatní pozdní výsledek; retry stejného cancel `messageId`/obsahu vrací duplicitní ACK bez dalšího zvýšení generation. Text ani fake odpověď nemají cestu k motoru/západce. Kontrakt v1 rozšiřuje snapshot a odpovědi; přednasazovací fixtures a README/kapitola 5 plánu zachycují přechod klienta na čtení conversation identity ze snapshotu.
- Ověření: MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` nad pěti C++ zdroji včetně `ConversationSession.cpp` pro `bridge.exe` — úspěšně. `kufr/contracts` pod Node 24.21.0 `npm test` — 21 platných a 37 chybných zpráv + OpenAPI test prošly. `kufr/simulator` s testovacím certifikátem/OpenSSL a MSVC bridge `npm test` — native bridge, deterministické cancel/late result/lease/no-actuator a WSS TLS/text/result/cancel/reload/Stop/restart scénáře prošly. `git diff --check` bez chyb.
- Omezení: fake výsledek je pevný text, bez skutečného HTTPS backendu, echo identity/revision, AI, médií nebo fyzického potvrzení. Browser vykreslení E2.5 zůstává neověřené kvůli nedůvěryhodnému dočasnému certifikátu. Následuje E2.9 média; skutečný backend E7 a hlas E9 jsou další etapy.
- Revize a doručení: E2.8b2 je v commitu `340f53fb4d8c829455b7af0cc1bef721bc592a00`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D15 – 29. 9. 2026 – E2.8b1 čistý model textového sezení

- Rozsah: `ConversationSession.h/.cpp` drží jediný aktivní textový tah, `bootId`/conversation ID/generation a nejvýše 10 jedinečných `clientTurnId` na sezení. Při přijetí kontroluje ruční lease, revision, UTF-8 a délku 1–1000 znaků / 8192 bajtů; opakovaný aktivní tah nevyvolá druhé přijetí, pozdější retry dokončeného či zrušeného ID se odmítne. Cancel a ztráta lease zvyšují generation, pozdní dokončení se zahodí. Test kryje tyto stavy, kolize ID, neplatný text a strop tahů. Model nepředává text backendu a nemá fyzický výstup.
- Ověření: MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` přes `%TEMP%\dildovander-e2-simulator\build-conversation-test.cmd` nad modelem a testem — úspěšně. `kufr/firmware`: `C:\Users\vojtech.petrik\.platformio\penv\Scripts\pio.exe run` — SUCCESS na Arduino ESP32-S3 profilu. První build před opravou C++11 konstruktoru selhal; opravená verze prošla. Read-only risk review odhalilo možnost znovupřijetí ID po dokončení; doplněna evidence ID po dobu sezení a test.
- Omezení: model ještě není napojený na WSS/native bridge ani fake backend, proto runtime `conversation.text/cancel` zůstává nepodporovaný. Volající musí před `CompleteTurn` ověřit echo identity backendu a nevytvářet fyzický povel z textu. E2.8b2 propojí model a testuje pozdní výsledek po cancel/lease loss. Browser certifikát E2.5 trvá.
- Revize a doručení: E2.8b1 je v commitu `b825e1c1b03de917df3fe30bb20bb47312547050`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D14 – 29. 9. 2026 – E2.8a textový lokální kontrakt

- Rozsah: `control-message-v1.schema.json` přidává vstupní `conversation.text/cancel` s device/boot/manual lease, conversation ID a generation; text má jednorázové clientTurnId, expectedRevision a 1–1000 znaků. `control-validator.mjs` drží běžné zprávy pod 2048 bajty a textovou výjimku do 8192 UTF-8 bajtů; WSS nastavuje stejný maximální frame, ostatní zprávy nad 2048 odmítá parser. Fixtures a testy ověřují chybějící lease/generation, nechtěná pole aktuátoru, délku textu a oba byte limity. Plán/README vysvětlují obálku a omezení.
- Ověření: `kufr/contracts` pod Node 24.21.0 `npm test` — 19 platných a 34 chybných control zpráv + OpenAPI test prošly. `kufr/simulator` s MSVC bridge/OpenSSL `npm test` — WSS/restart scénáře prošly. Firmware není tímto kontraktním krokem změněn.
- Omezení: WSS facade tyto dosud neimplementované směry odmítá; žádný rozhovor, cancel nebo AI výsledek nebyl vykonán. Následuje E2.8b vlastník fake sezení a integrační test. Browser certifikát E2.5 zůstává neověřený.
- Revize a doručení: E2.8a je v commitu `e637db2fcbbf873925416dca830aa26921384201`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D13 – 29. 9. 2026 – E2.7 potvrzení znovuzaložení

- Rozsah: `DeviceControl` přijímá `InventoryReloadCommand` pouze s platným manual lease, bootem, pořadím, revision, deadline a potvrzenou kontrolou obsluhou v bezpečném klidu. Sdílí 64výsledkovou cache a sequence s výběrem, včetně konfliktu stejného ID napříč typy. `CommandJournal::PersistReadyMask` zapisuje novou masku před ACK; fake souborový journal používá samostatný `M` záznam a rekonstruuje jej přes restart. `bridge.cpp` a `server.mjs` předávají `inventory.confirmReload` → `RELOAD` → ACK/snapshot bez spuštění sekvence. Schéma/fixtures, README a plán vymezují explicitní přihrádku a kontrolu.
- Ověření: z dočasné složky MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` nad `SafetyController.cpp`, `SelectionSequence.cpp`, `DeviceControl.cpp` s `device_control_test.cpp`, `simulator/main.cpp` a `simulator/bridge.cpp` — všechny tři cíle sestaveny, C++ test a šest CLI scénářů prošlo. `kufr/contracts`: Node 24.21.0 `npm test` — 16 platných a 29 chybných control zpráv + OpenAPI test prošly. `kufr/simulator`: `npm test` — bridge restart/perzistence a WSS reload/duplicitní ACK/Stop prošly. `kufr/firmware`: místní PlatformIO `pio run` pro Arduino profil ESP32-S3 — úspěšně. Fyzická deska ani NVS nebyly testovány.
- Omezení: fake souborový zápis používá flush, ne fyzickou atomicitu/NVS; browser certifikát E2.5 zůstává mimo důvěru prohlížeče. Čekají conversation/media zprávy. Read-only risk review nenašlo kritickou chybu, upozornilo na chybějící socket test odmítnutí reloadu při expirovaném lease nebo pending recovery; C++ test tyto hranice pokrývá částečně.
- Revize a doručení: E2.7 je v commitu `cf6ffac0394db26cec655b5380fb62cdccb1fbde`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D12 – 29. 9. 2026 – E2.6b audio a cleanup OpenAPI

- Rozsah: `contracts/backend-openapi-v1.json` nyní popisuje i multipart PCM16 mono 32 kHz WAV přepis, speech nad ID validovaného tahu, download krátkodobého `audio/wav`, rušení operace a ukončení sezení. Operation union rozlišuje transcription/turn/speech s typovanými výsledky; stav failed vyžaduje chybu, completed výsledek. Přepis předává deviceRevision pro požadované echo korelace. `verify-backend-openapi.mjs` kontroluje všech osm skupin tras, media type, auth/idempotency metadata a pozitivní/chybné struktury. Plán v kapitole 6 zaznamenává upřesněné formáty.
- Ověření z `kufr/contracts` pod Node 24.21.0: `npm test` — 15 platných a 27 chybných control zpráv a rozšířený test OpenAPI prošly. WAV bajty ani runtime auth/retence/idempotence tento test neověřuje; to patří backendové integraci E7.
- Omezení: pozdější kostýmní snímek je výslovně mimo základní v1; skutečný backend, stream parser a autorizace zatím neexistují. E2 pokračuje lokálním protokolem E2.7.
- Revize a doručení: E2.6b je v commitu `e09c4b6a513b076c0fbd425d43347e7c2007b5a8`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D11 – 29. 9. 2026 – E2.6a základ backend OpenAPI

- Rozsah: `contracts/backend-openapi-v1.json` popisuje device credential výměnu, založení sezení, textový tah a polling jeho operace; oddělená bearer schémata, UUID idempotency hlavičku, ProblemDetails, šestibitovou masku, limit 1000 znaků, stav operace, echo identit a typovaný validovaný návrh. `verify-backend-openapi.mjs` kontroluje reference, požadované trasy/security a pozitivní/chybné hodnoty. `contracts/README.md` a kapitola 6 plánu vymezují tento dílčí kontrakt.
- Ověření z `kufr/contracts` pod Node 24.21.0: `npm test` — 15 platných a 27 chybných control zpráv a test E2.6a prošly. Backend ještě neexistuje, takže scope, skutečná idempotence, kvóty a retence nebyly ověřeny v HTTP runtime.
- Omezení: dokument má zatím čtyři trasy; WAV přepis, syntéza, binární audio a rušení/úklid chybí. Otevřené detaily těchto tras se neprohlašují za hotové. Pokračuje E2.6b.
- Revize a doručení: E2.6a je v commitu `49ef3f9e283ac36738406a1280383e7d99dc4a80`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D10 – 29. 9. 2026 – E2.5 webová časová osa simulátoru

- Rozsah: `simulator/server.mjs` vystavuje na stejném loopback TLS portu stránku `/simulator` a pouze čtecí `GET /v1/observe` s bearer autorizací, nejvýše čtyřmi pozorovateli a NDJSON snapshot/událostmi ze stejného C++ modelu. `dashboard.html`/`dashboard.js` viditelně označují `SIMULACE`, zobrazují šest přihrádek, fázi, režim, journal, revizi a časovou osu. Stránka neposílá řídicí povely; token použije v HTTPS hlavičce a vymaže vstupní pole. `verify-socket.mjs` kontroluje stránku, script, odmítnutí bez tokenu, úvodní snapshot a právě jednu událost uvolnění po opakovaném příkazu. Plán v kapitole 11 zaznamenává vývojový pozorovací proud odděleně od nativního WSS kontraktu.
- Ověření z `kufr/simulator`: Node `--check` pro `server.mjs`, `dashboard.js`, `verify-socket.mjs` a Node 24.21.0 `npm test` s MSVC bridge a OpenSSL v dočasné složce — úspěšně. In-app Browser pokus o `https://127.0.0.1:8443/simulator` skončil `ERR_CERT_AUTHORITY_INVALID` na dočasném self-signed certifikátu; žádná výjimka z TLS nebyla udělena. Vykreslení, interakce a mobilní viewport proto nejsou potvrzeny.
- Omezení: `GET /v1/observe` je pouze vývojové rozhraní hostitelského procesu; neprokazuje TLS důvěru telefonu, ESP32 síť ani skutečnou mechaniku. K vizuálnímu browser ověření je třeba certifikát důvěryhodný pro testovací prohlížeč. E2 pokračuje E2.6.
- Revize a doručení: E2.5 je v commitu `11c358017a9ca1ca2e7a26d75fb5379a81df3840`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D9 – 29. 9. 2026 – E2.4d fake journal a restart WSS

- Rozsah: `simulator/bridge.cpp` ukládá append-only fake záznamy Begin/Resolve do cesty předané z `KUFR_SIMULATOR_JOURNAL`, při startu rekonstruuje pending a masku; neúplný, chybný nebo po předchozí inicializaci ztracený log selže uzavřeně. Soubor `.lock` vynucuje jediného zapisovatele; Node čeká na skončení bridge před restartem. Po neuzavřeném záznamu nenastává automatické zotavení. Místní proces `run-server.mjs` nabízí servisní simulaci kontroly `inspected`, která přes stejný C++ model označí nejistou přihrádku jako nedostupnou. Node server vystaví fázi/fault z journalu a předá stejnou cestu přes restart. Opravena i falešná terminální událost při vypršení neaktivního lease.
- Ověření: z `%TEMP%\dildovander-e2-simulator` MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` nad `SafetyController.cpp`, `SelectionSequence.cpp`, `DeviceControl.cpp` a `simulator/bridge.cpp` s `/Fe:bridge.exe` — úspěšně. Z `kufr/simulator` pod Node 24.21.0 s `KUFR_SIMULATOR_BRIDGE` a `OPENSSL`: `npm test` — úspěšně. Testy nyní pokrývají restart s otevřeným journalem, odmítnutí takeover, servisní zotavení s maskou 39, další restart s perzistovanou maskou, souběžného zapisovatele, poškozený i ztracený log (mask 0/fault), pozdní heartbeat i odpojení během acquire. Před opravou expirace neaktivního lease WSS test odhalil chybné `selection.result` s prázdným ID; po opravě znovu prošel.
- Omezení: fake log používá flush, neověřuje fyzickou atomicitu zápisu ani výpadek napájení/NVS. Současná kontrola `.lock` byla sestavena a testována na Windows; POSIX větev ještě ne. Simulovaný `inspected` je výslovná akce obsluhy, nikoli důkaz mechanické kontroly. Následuje E2.5.
- Revize a doručení: E2.4d je v commitu `e1ecd3b0fe4ae3b0a9943126726c0b2d401eedba`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D8 – 29. 9. 2026 – E2.4c místní WSS facade

- Rozsah: `simulator/bridge.cpp` je hostitelský proces nad stejným `DeviceControl`, `SelectionSequence` a fake portem/journalem. `server.mjs` přijímá na loopback TLS/WSS jeden socket, vyžaduje bearer v handshake, 10s hello a přísný E2 parser, odmítá kompresi a control payload nad 2048 B. Převzetí, heartbeat, výběr, ACK/retry, Stop, reset a snapshot volají C++ model. Stop předbíhá běžnou frontu, zneplatní před ním přijatý reset a počet čekajících zpráv je omezený. Odpojení zruší lease i po souběžném acquire. `run-server.mjs` čte certifikát, klíč, testovací token a cestu bridge z prostředí; nic z toho není ve zdroji. `verify-bridge.mjs` a `verify-socket.mjs` testují fake čas, pozdní lease, TLS/autorizaci, handshake, jediné uvolnění po retry, Stop se zatíženou frontou včetně staršího resetu, snapshot a chybný JSON. `simulator/package.json`/lockfile připíná `ws@8.21.3` a Node 24.21.0.
- Ověření z `%TEMP%\dildovander-e2-simulator`: MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` nad `SafetyController.cpp`, `SelectionSequence.cpp`, `DeviceControl.cpp` a `simulator/bridge.cpp` s `/Fe:bridge.exe` — úspěšně. Z `kufr/simulator` po nastavení `KUFR_SIMULATOR_BRIDGE` na tento soubor, `OPENSSL` na místní OpenSSL CLI a PATH na Node 24.21.0: `npm ci`, `npm test` — obojí úspěšně; test vytváří krátkodobý certifikát mimo repo a ověřuje jej klientem. Z `kufr/contracts` pod stejným Node proběhlo `npm ci`, `npm test` — 15 platných a 27 chybných zpráv prošlo. Před opravami test odhalil duplicitně čtenou ACK ve verifieru a nesprávný exit po zavření socketu; oba opraveny, opakovaný běh prošel. `npm ci` hlásilo 0 známých zranitelností pro `ws` v tomto lokálním auditu.
- Omezení: `BridgeJournal` je dosud pouze v RAM; restart procesu nedrží pending/masku. Socket test zatím neprokazuje close/acquire závod ani přerušení během motoru, chybí OpenAPI, UI a fyzický hardware. `server.mjs` je vývojový loopback server s dočasnou identitou; nahrání do ESP32 nebo produkční provisioning nebyly provedeny. Další E2.4d výslovně řeší tuto mezeru.
- Revize a doručení: E2.4c je v commitu `8351223d6dbea2d3fc9f75033c3a5114db0f3aa0`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D7 – 29. 9. 2026 – E2.4b zprávy spojení a lease

- Rozsah: plán v kapitole 4 upřesňuje tvar `hello`/`welcome`, `state.get`, `control.acquire/release` a heartbeat, vždy s oddělením tokenu do TLS handshake. `contracts/control-message-v1.schema.json` přidává jejich přísné varianty; sedm platných fixtures a čtyři negativní mutace hlídají verzi, pole navíc, vložený snapshot a lease. Contracts README nyní uvádí rozsah schématu.
- Ověření z `kufr/contracts`: `& 'C:\Users\vojtech.petrik\AppData\Roaming\nvm\v24.21.0\npm.cmd' test` — 15 platných a 27 chybných zpráv správně rozlišeno. Schéma neověřuje identitu, pořadí handshake ani životnost lease; bez WSS testu je E2.4 stále rozpracované.
- Revize a doručení: E2.4b je v commitu `fd3d211e7f0c0a5f91f6a99339cc9827a8aebeef`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D6 – 29. 9. 2026 – E2.4a přenosný vstupní parser

- Převzetí: po doručení E2.3 v `e03672f2fe974a790b374cc1ad5ca5ea4a15451a` začala E2.4. Před WSS vstupem bylo nutné použít skutečná E2.1 pravidla z runtime modulu, nikoli je zkopírovat do síťové vrstvy.
- Rozsah: `contracts/control-validator.mjs` vyčlenil přísné UTF-8/JSON/schema ověření do exportu `validateControlMessage(bytes)`, `verify-fixtures.mjs` ho nyní používá, contracts README popisuje hranici. Žádná změna schématu ani wire kontraktu.
- Ověření z `kufr/contracts`: `& 'C:\Users\vojtech.petrik\AppData\Roaming\nvm\v24.21.0\npm.cmd' test` — 8 platných a 23 chybných zpráv správně rozlišeno. WSS adaptér ještě neexistuje, takže toto není síťová integrace; pokračovat v E2.4.
- Revize a doručení: E2.4a je v commitu `5a55716dfd661355d456cb69b58120ed39b89764`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D5 – 29. 9. 2026 – E2.3 hostitelský simulátor

- Převzetí: E2.2 bylo doručeno v `7b0fbc82996651e5e2387e269a22bb498abe806a` a krok E2.3 zapsán jako aktivní. Čistý model výběru a journalu umožnil proces bez desky.
- Rozsah: `simulator/main.cpp` spouští `DeviceControl`/`SelectionSequence` s řízeným časem, fake portem a journalem; časová osa výslovně značí `SIMULACE`. `simulator/verify-scenarios.mjs` bere ID a přihrádku ze sdílené platné fixture a ověřuje běžné dokončení, dva retry po ztraceném ACK, Stop, ztrátu lease, restart při uvolňování a chybu zápisu. Kontroluje nejvýše jedno uvolnění, vypnutí před Stop, pending journal a masku dostupnosti. `simulator/README.md` uvádí build/běh a hranice důkazu, kořenové README ukazuje nový adresář. Kontrola navíc odmítá neplatné číslo přihrádky na CLI.
- Ověření z `%TEMP%\dildovander-e2-simulator`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /std:c++14 /W4 /WX /EHsc C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\src\features\device_control\DeviceControl.cpp C:\Development\DildoVander\kufr\simulator\main.cpp /Fe:simulator.exe'` — úspěšně bez warningů. Z kořene `& 'C:\Users\vojtech.petrik\AppData\Roaming\nvm\v24.21.0\node.exe' .\kufr\simulator\verify-scenarios.mjs (Join-Path $validationDirectory 'simulator.exe')` s `$validationDirectory = Join-Path $env:TEMP 'dildovander-e2-simulator'` — šest scénářů prošlo. Neplatné `4junk` na CLI vrací exit code 2. Firmware `pio run` se neopakoval, protože se zdroje firmwaru neměnily.
- Neprovedeno: syrová fixture neprochází C++ parserem; použily se jen `commandId` a přihrádka, ostatní pole jsou sestavena podle živého modelu. Neexistuje socket, UI časová osa, skutečný NVS/TLS ani fyzická časová osa. Revize, sekvence, lease a source fixture se proto tímto testem neověřují. Po dokončení doručení E2.3 navazuje E2.4.
- Revize a doručení: E2.3 je v commitu `e03672f2fe974a790b374cc1ad5ca5ea4a15451a`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D4 – 29. 9. 2026 – E2.2 model příkazu a journalu

- Převzetí: E2.1 bylo doručeno v `b2e9b8d9fc2ddb362b2ae31b0a98fad4a53bd12e`; krok E2.2 byl při zahájení zapsán jako aktivní. Schéma E2.1 validuje syrové zprávy, tento krok navazuje až na strukturovaný příkaz; propojení parseru s modelem zůstává na E2.3 a další E2 slices.
- Rozsah: nové `firmware/src/features/device_control/CommandJournal.h` a `DeviceControl.h/.cpp` modelují ruční lease 6 s, `bootId`/revision/pořadí, 64 posledních výsledků, konflikt ID, zamítnutí starého příkazu a zápis journalu před prvním pohybem. Přijetí a spuštění jsou samostatné operace. Při přerušení zůstává journal otevřený; po kontrole obsluhou se označí přihrádka jako nedostupná. Dokončení musí v jedné trvalé operaci uložit novou masku a uzavřít journal. Po selhání uzavření se nesmí emitovat dokončený výsledek. `firmware/tests/device_control_test.cpp` používá fake čas, port a journal; firmware README popisuje omezení.
- Ověření z `%TEMP%\dildovander-e2-validation`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /std:c++14 /W4 /WX /EHsc C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\src\features\device_control\DeviceControl.cpp C:\Development\DildoVander\kufr\firmware\tests\device_control_test.cpp /Fe:device_control_test.exe'`, pak `.\device_control_test.exe` — úspěšně. Scénáře pokrývají ztracený ACK, stejné/změněné ID, 64 výsledků a vytěsnění, neplatnou obálku, zpožděný příkaz, auth/lease, poruchu journalu, restart po přijetí i přerušeném uvolnění, perzistovanou nedostupnost po dokončení a vypnutí při ztrátě lease.
- Firmware build z `kufr/firmware`: `& 'C:\Users\vojtech.petrik\.platformio\penv\Scripts\pio.exe' run` — **SUCCESS**, `espressif32@7.1.3`/Arduino; 18 660 B RAM a 279 157 B flash. První pokus selhal na C++11 inicializaci `CommandReceipt`; po přidání konstruktoru prošel. `DeviceControl` se překládá, ale není zapojený do `main.cpp`, takže velikost výsledné binárky není měřením jeho provozní paměti.
- Revize a doručení: E2.2 je v commitu `7b0fbc82996651e5e2387e269a22bb498abe806a`, `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem. Neprovedeno: skutečná NVS atomicita, síť/parser/TLS, hardware a společný C++/TypeScript/.NET fixture test. Fake ověření nesmí nahradit tyto integrační a fyzické kontroly.
- Předání: E2 zůstává rozpracované. Následuje E2.3 podle horní části souboru.

### D3 – 29. 9. 2026 – E2.1 první sdílené kontrakty

- Převzetí: E1.2 bylo jako softwarové jádro pushnuto na `origin/main`; E2 podle kapitoly 12 smí začít bez desky. Před změnou byl krok E2.1 zapsán jako aktivní.
- Rozsah: nové `contracts/control-message-v1.schema.json` (JSON Schema 2020-12) pro `selection.start`, `command.ack`, `selection.result`, `state.snapshot`, `safety.stop` a `safety.reset`; `contracts/fixtures/` se sdílenými platnými/chybnými zprávami; `verify-fixtures.mjs`, připnuté `package.json`/lockfile/`.nvmrc` a README. Přísný vstup odmítá neplatné UTF-8, duplicitní klíče, desetinný/exponenciální zápis celočíselných polí a zprávy nad 2048 bajtů. Schéma odmítá nadbytečná pole, chybné typy/rozsahy a vynucuje oddělené kódy poruch, bezpečnostní povely a `eventSequence` watermark snapshotu. Kořenové `kufr/README.md` ukazuje existující adresář.
- Ověření z `kufr/contracts`: s Node `24.21.0` a npm `11.19.0` podle `.nvmrc` proběhlo `npm ci` a `npm test` — **8 platných a 23 chybných zpráv správně rozlišeno**, žádná chyba. Přísné schéma se zkompilovalo; `npm ci` nahlásilo 0 známých zranitelností v instalovaných osmi balíčcích. `git diff --check` bez chyb. Neprováděl se firmware, mobilní ani CI build, protože se změnily jen kontrakty a jejich vlastní verifier.
- Revize a doručení: E2.1 je v commitu `b2e9b8d9fc2ddb362b2ae31b0a98fad4a53bd12e`, `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem. Schéma neověřuje identitu/TLS, lease, boot, čas, revision, idempotenci ani journal; tyto stavové kontroly jsou následující E2 slices. Přijímače musí přísný parser a pravidla přenést do svých adaptérů; tento test je nespojuje se skutečnou sítí.
- Předání: E2 zůstává rozpracované. Následuje E2.2 podle horní části souboru; B1/B2/B4 nebrání hostitelskému modelu.

### D2 – 29. 9. 2026 – E1.2 hostitelský timeout a ztráta řízení

- Převzetí: čistý `main` na `f03a1a8`, E1.1 v kódu odpovídalo deníku D1. Krok E1.2 byl při zahájení zapsán jako aktivní.
- Rozsah: `firmware/src/features/wand_selection/SelectionSequence.h/.cpp` rozlišuje samostatný a ruční původ výběru, při ztrátě ručního lease zastaví pouze ruční výběr a při mezeře řídicí smyčky 250 ms vyžádá Stop před dalším sériovým povelem. `firmware/src/features/safety/SafetyController.h/.cpp` uchovává poruchu do výslovného zotavení i při souběžném Stop. `src/main.cpp` provádí kontrolu mezery na začátku průchodu; firmware README popisuje hranici hostitelského modelu. Všechny výstupy zůstávají jen požadavky přes port bez GPIO.
- Hostitelské ověření z `%TEMP%\dildovander-e1-validation-final`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /std:c++14 /W4 /WX /EHsc C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\tests\selection_sequence_test.cpp /Fe:selection_sequence_test.exe'`, pak `.\selection_sequence_test.exe` — obojí úspěšně. Testy navíc pokryly výpadek lease ve všech fázích, bez vlivu na samostatný výběr, poruchu při aktivním motoru/západce, přednost Stop, zachování příznaku poruchy a timeout smyčky včetně přetečení času.
- Firmware build z `kufr/firmware`: `& 'C:\Users\vojtech.petrik\.platformio\penv\Scripts\pio.exe' run` — **SUCCESS** na PlatformIO `espressif32@7.1.3`/Arduino; 18 660 B RAM a 270 493 B flash. `git diff --check` bez chyb. Pomocné `.obj` z prvního hostitelského buildu byly odstraněny, zdroje nebyly dotčeny.
- Revize a doručení: E1.2 je v commitu `d960210bc2352c3318690e3ec470b5f8df94edb6`, `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem. Nenahrávalo se do desky. Softwarová kontrola mezery vypne výstup až při návratu do smyčky; při úplném zamrznutí musí vypnout nezávislý fyzický watchdog/inhibit. To je neověřená podmínka E5/B1, nikoli důkaz E1.2.
- Předání: softwarová kritéria E1 kapitoly 12 jsou hostitelsky doložena; další je E2.1 podle horní části souboru.

### D1 – 29. 9. 2026 – E1.1 Stop a bezpečnostní požadavky bez desky

- Převzetí: po čtení kapitol 5, 8 a 12 plánu, firmware README a živého kódu byl převzat E1.1. Původní hostitelský test prošel přes Visual Studio Build Tools; `g++` a `pio` nebyly v PATH.
- Rozsah: nové `firmware/src/features/safety/SafetyPort.h` a `SafetyController.h/.cpp`; upravené `wand_selection/SelectionSequence.h/.cpp`, `SerialSelectionPort.h/.cpp`, `src/main.cpp`, `tests/selection_sequence_test.cpp` a firmware README. Sekvence po startu i Stop vyžaduje výslovné zotavení; Stop hned volá port pro vypnutí, nepokouší se znovu zamknout západku. Safety controller odmítá souběh motoru a západky; motor je provozně 150 ms, pozdní průchod od 250 ms, západka od 500 ms a sekvence od 5 s vyvolají Stop. Sériový port pouze vypisuje požadavky, žádné GPIO.
- Hostitelské ověření z `C:\Users\vojtech.petrik\AppData\Local\Temp\kufr-e1-validation`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /EHsc /W4 /WX /std:c++14 C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\tests\selection_sequence_test.cpp /Fe:selection_sequence_test.exe && selection_sequence_test.exe'` — průchod bez chyb. Testy pokrývají původní pořadí/validaci, Stop ve všech fázích, opakovaný i externí Stop, zotavení, pozdní průchod, meze 250/500/5000 ms a přetečení čítače včetně aktivního motoru/západky. Celý `src/main.cpp` a sériový adaptér navíc prošly kompilací s dočasnou hlavičkou Arduino mimo repo; tento stub není náhradou skutečného PlatformIO buildu.
- Firmware build z `kufr/firmware`: `& (Join-Path $env:TEMP 'kufr-pio-venv\Scripts\python.exe') -m platformio run` — **SUCCESS**, PlatformIO Core 6.2.0, platforma `espressif32@7.1.3`, Arduino; 18 660 B RAM a 270 217 B flash. CLI bylo nainstalováno do dočasného venv mimo repo; běžný příkaz `pio run` bez tohoto prostředí v PATH nefunguje. `.pio/` je ignorovaný výstup.
- Revize: žádný commit/push; checkout nemá HEAD a všechny původní soubory jsou untracked. Neproběhlo nahrání do desky, měření watchdogu/inhibit, ověření serva ani iPhone/CI. Softwarový Stop vydává požadavek; nepředstavuje důkaz fyzického vypnutí při zatuhlé smyčce.
- Dodatečné doručení 29. 9. 2026: po potvrzení prvního importu celého projektu byl E1.1 zahrnut do commitu `b21725a23be88b89f723fa01b59496058cb89abd` a pushnut na `origin/main` repozitáře `Blato58/DildoVander`. Pravidlo automatického commitu/push po každém dalším dokončeném slice, ověřovací skript a připnutí Node 24.21.0 byly pushnuty v commitu `80fecdebbad9e0b5d136d520e954da66caeedcee`. Trvalý PlatformIO Core 6.2.0 je v uživatelském prostředí; `pio run` znovu prošel, stejně jako `npm ci` a `npm run build` mobilního prototypu pod Node 24.21.0. Tyto buildy nedokazují HW ani iOS akceptaci.
- Předání: E1.2 podle horní části souboru. B1 blokuje jen fyzickou část E5, nikoli další hostitelské testy.

### D0 – 29. 9. 2026 – příprava dokumentace a předání

- Rozsah: sjednocení AGENTS/README a technických podkladů s plánem, založení tohoto stavového souboru. Žádná implementace E1–E13, žádná změna zdrojového kódu ani konfigurace.
- Evidence stavu: čtení firmware zdrojů/testu/PlatformIO, mobilního vstupu/package/lockfile a kontrola nepřítomnosti `.github/workflows/`. Nezávislý read-only průchod potvrdil absenci síťové, AI a nativní mobilní implementace.
- Dokumentační kontrola z kořene repozitáře: inventura `rg --files --hidden -g '!.git' -g '!**/node_modules/**' -g '!**/.pio/**' -g '!**/dist/**'`, čtení Markdown přes `Get-Content -Raw -Encoding UTF8`, následná kontrola odkazů/obsahu v paměti. Výsledek: 61 místních odkazů existuje, všech 15 ID etap se shoduje s plánem, 4 JSON ukázky se parsují, code fences jsou vyvážené. Porovnání `Get-FileHash -Algorithm SHA256` proti inventuře před úpravou: 11 původních Markdown změněno, 1 Markdown přidán, žádný kód/konfigurace smazán nebo změněn. `git diff --check` bez výstupu; protože jsou soubory untracked, rozhodující je kontrola obsahu a hashů, nikoli prázdný diff. Žádná z těchto kontrol není runtime akceptací.
- Nespouštěno: `pio run`, C++ test, `npm ci`, `npm run build`, CI, placená AI, instalace na iPhone a hardware. Důvod: pouze dokumentační zadání.
- Revize: místní necommitované soubory; nevznikl commit/push ani publikace.
- Předání: E1.1 podle horní části souboru. E0 zůstává částečné, hardware ani podpis nejsou podmínkou zahájení E1.

Pro další zápis použij nadpis s ID kroku, datem a konkrétním výsledkem. Pod něj napiš rozsah/soubory, přesné provedené kontroly a výsledky, zbývající akceptaci/blokace a další krok. Dlouhé raw logy, osobní údaje, certifikáty a klíče sem nepatří.
