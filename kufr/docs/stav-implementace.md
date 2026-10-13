# Stav implementace AI kufru

## D167 — Dance for me — softwarové ověření 13. 10. 2026

Zadání majitele: mobil spustí náhodnou skladbu z vlastního seznamu
na SD kufru, společně s efekty do rytmu. Nový rozsah vlastní
`firmware/src/features/dance`, rezervace SD/audio/řídicího runtime,
místní WSS kontrakt a mobilní panel. Nastavení: PCM16 mono 24 kHz,
BPM/offset a RGB/volitelné vibrace; bez pohybu serv a bez AI/internetu.
Nezávislé review zachytilo nepodmíněný Stop hlasového audia při výpadku
Wi-Fi. Oprava respektuje vlastníka audia a má regresi v přehrávacím testu.
Plán a [návod](dance-for-me.md) popisují E14, přípravu seznamu a migraci
nového mobilu před novým firmware. E14/B1 zůstávají fyzicky neakceptované.

Změněny: nová firmware feature a její testy/stuby, SD rezervace,
backendové rušení vlastního audia, runtime a místní owner/parser/output,
CMake/composition/capabilities, WSS schema/fixtures/interoperabilita,
mobilní klient/panel/main/styl/testy/harness, Python příprava SD,
firmware CI a dotčené README/plán/stav/návod.

Provedeno: 4 C++ dance/SD hostitelské binárky a původní device-control/
runtime, parser, output a owner regrese přes přesné CI source listy,
`-std=c++14 -Wall -Wextra -Werror -pthread`, všechny prošly na VPS
GCC 15.2 v soukromé dočasné složce bez změny nasazené aplikace. Lokální
WSL nelze spustit (chybí jeho VHD). 54 skutečných firmware JSON zpráv
prošlo sdílenou striktní v1 validací. `npm test` mobilu: 60/60;
`npm run build`: TypeScript/Vite prošly. Kontrakty: 30 validních/
44 nevalidních fixtures plus stávající OpenAPI/media kontroly prošly.
Python příprava SD: 6/6 a py_compile. Nezávislé review po opravě
vlastnictví audia nemá zbývající nález.

Browser na localhost:5197 ověřil skutečný panel v celé aplikaci i označený
syntetický harness: start, soubor/tempo/průběh, Stop, chybu SD, dohrání
a odpojení. Na šířce 390 px nebylo vodorovné přetékání; konzole bez
chyb/varování. Ověřeny jsou 4 nové lokální dokumentační odkazy a běžná
kontrola diff whitespace. Kontrola `ota_release.py check` pro skutečnou
lokální embedded verzi potvrdila N16R8 image/partition hranice.

První `pio run` skončil interní chybou GCC v nezměněném SDK
`esp_lcd_panel_rgb.c`; opakování `pio run -j 2` prošlo: RAM
94 972/327 680 B, flash 1 758 689/6 553 600 B po poslední bráně zotavení. Nebyla měněna závislost
ani zdroj SDK. Implementační commit
`98cbefe7a26a80820c6f026e3d35d7c9d97ca8a6` byl úspěšně pushnut
helperem na `codex/dance-for-me`; vzdálený SHA byl ověřen. Slice je
přenesen na aktuální `main` `dacfdba` bez mechanických změn původního
checkoutu; úplný aktuální deník ostatních prací zůstal zachovaný.
Firmware/mobilní zdroje a testy jsou po přenosu totožné s ověřenými;
nový main build včetně third-party notices a všech 60 mobilních testů
prošel znovu po `npm ci`.
Skutečné skladby majitele nejsou v repozitáři. Native podpis, instalace,
flash, placená AI ani nasazení nejsou součástí tohoto zadání.

Další konkrétní krok: majitel připraví svoje skladby podle návodu;
po fyzickém ověření profilu B/SD/audio/efektů vyzkoušet celou show,
změřit beat/DMA, Stop a iPhone ovládání. E14 ani B1 nejsou uzavřené.

## F2 DFA / Workshop — 12. 10. 2026

Rozsah: montovatelná vložka do skutečného kufru a mobilní CAD viewer.
Implementace je v `mechanika/final/`: dvě servisní kazety s horními M3,
předzapojení na stole, montáž vodítek před vložením, přístup ke čtyřem M4
a 24 modulovým M3 před horními skupinami. Červený spínač posunut o 6 mm
kvůli imbusu, levý zesilovač o 1 mm vpřed a zadní servisní osa na Y111,7;
rezerva hlavy k pásku zesilovače je přes 1 mm. Podpěra kazety a dvě lokální
kabelové trasy obcházejí skutečná tělesa. Pinout a elektrické obvody jsou
zachované. Korpus se nevrtá.
`assembly-guide.json` má 16 kroků a rozlišuje přípravu na stole od osazení
v kufru; ze stejných dat vzniká `POSTUP.md`. Viewer má mobilní panely,
dotykový orbit/pan/pinch, pohledy, zaměření a montážní průvodce.

Doplnění majitele během práce 12. 10. nahradilo geometrii kufru:
430 × 269 mezi horními výztuhami, stěna 5, výztuhy celkem 20 včetně stěny,
40/30 dolů od okraje; tečné rozměry spodních oblouků 45/45 a středová
výztuha 80 × 5. Kotvy mají tvarovaný spodek, 0,5mm svislou lepicí spáru,
103 zámků a matice vložené před lepením. Tisknou se horní stranou dolů.
Předchozí geometrické výsledky nepřijímají tuto novou rozměrovou revizi.

Ověřeno na aktuálních zdrojích: 138 Python testů (včetně 12 CAD a 8 montážních),
47 Node testů, syntaxe a montážní kontrakt 16/542/135. Chrome/WebGL
prošel šest viewportů od 320×568 po 1440×900 včetně landscape, skutečné
CDP dotykové události, focus/pohledy, režimy a průvodce/Stop; bez chyb
konzole a requestů. Viewer má přibližně 25,6 místo 70 MB díky indexům
tessellace. Úplná regenerace prošla: 37 STL typů / 52 výtisků, STEP round-trip,
uzavřené souvislé sítě, tři stavy sestavy, 102 pohybových vzorků všech šesti
modulů, sedm spojitých drah nástroje/hlavy, vložení rámu a servis šesti
horních skupin po 25 vzorcích. Audit 135 kabelů v klidu/přepravě a při
0/80/160° prošel 26 991 BREP párů bez kolizí nebo výpadků mezí. Report
`exports/verification.json` má `PASS_CAD_NOT_PHYSICAL_ACCEPTANCE`; ZIP
41 558 082 B má ověřený inventář, readback a SHA-256 v `package-sha256.txt`.
Všech 78 lokálních odkazů existuje. Nejde o iPhone/HW přejímku.
Fyzická montáž, konektory/prsty, kabelové rezervy, kamera/FPC, dřevo/epoxid,
zatížené servopohyby a přeprava zůstávají nepřijaté. Návrh není uvolněný
k finálnímu tisku. Další krok po softwarovém ověření: rozměrový a montážní
prototyp podle `mechanika/final/MONTÁŽ.md`, bez nahrání firmware v tomto slice.
Git předání: implementační commit `59c4b9f50b7fcf4a0f37c771fcd4ba4fe0a983ca`
na větvi `codex/cad-assembly-viewer-overhaul-public`. Push projektovým
`scripts/Push-CurrentBranch.ps1` uspěl; vzdálený SHA byl ověřen shodný.

Poslední aktualizace: **12. 10. 2026**. Tento dokument zachycuje aktuální
stav a další kroky; neobsahuje historický deník nasazení ani technické logy.
[Implementační plán](implementacni-plan.md) určuje akceptační kritéria,
[AGENTS.md](../../AGENTS.md) pracovní postup. Zkrácení historie nemění
akceptaci: software, simulátor, fyzický kufr a iPhone mají oddělené důkazy.

## Aktuální systém

- ESP32-S3 používá ESP-IDF 6.1, vlastní samostatný rozhovor a místní
  bezpečnostní bránu. Backend přes HTTPS vrací validovaná data; neovládá GPIO.
- Backend ASP.NET Core/SQLite obsahuje fake adaptéry, Responses, Realtime,
  volitelný GPT-Live a oddělenou paměť. Provider klíč zůstává na backendu.
- Mobilní ovladač je TypeScript/Vite + Capacitor/Swift. LAN, BLE provisioning,
  diagnostika a textová/hlasová záloha mají vlastní nativní adaptéry.
- Parametrická mechanika F2 se regeneruje ze zdrojů; návrh, geometrické
  kontroly a viewer nejsou fyzickou akceptací konstrukce.
- Vlastní zdroje, dokumentace a CAD zdroje používají AGPL-3.0-only.
  Licence třetích stran a práva k tematickým názvům zůstávají samostatné.

## Etapy

| Etapa | Aktuální stav | Zbývající akceptace |
| --- | --- | --- |
| E0 – provozní předpoklady | Rozpracováno | Napájení, prostředí akce a provozní podmínky dat. |
| E1 – bezpečné jádro | Hostitelská softwarová kritéria doložena | Skutečné OE/napájení a chování pohonů ověřit s E5. |
| E2 – kontrakty a simulátor | Softwarová kritéria doložena | Fake není NVS, skutečné audio ani fyzická sekvence. |
| E3 – ruční mobil bez HW | Webová/simulátorová kritéria doložena | Native LAN a fyzický kufr patří E10/E5. |
| E3a – iOS CI základ | Existující macOS podpis a native harnessy | Změny pipeline/ikon znovu ověřit v macOS workflow; nový IPA build zde není doložen. |
| E4 – runtime a přímá síť | Rozpracováno; USB provisioning, domácí Wi-Fi a backend HTTPS dříve přijaty majitelem | Hotspot/failover, LAN TLS/pairing/WSS a celý device rozhovor. |
| E5 – jedna bezpečná přihrádka | GPIO adaptéry a časovaná sekvence existují | Fyzická inspection, OE, napájení a kalibrace první přihrádky. |
| E6 – šest přihrádek | Fyzická akceptace nezahájena | Navazuje na E5; cykly, proudy a zotavení. |
| E7 – backend s fake adaptéry | Softwarová kritéria doložena | Skutečný provider a ESP průchod mají samostatnou akceptaci. |
| E8 – skutečný textový dialog | Rozpracováno; SDK/adaptéry a korpus existují | Úplné obsahové a device scénáře. |
| E9 – samostatný hlas kufru | Rozpracováno | Skutečné audio na ESP a end-to-end bezpečné ukončení. |
| E10 – mobilní záloha | Rozpracováno | Native LAN a aktuální IPA na iPhonu proti skutečnému kufru. |
| E11 – provozní balení | Rozpracováno | Dlouhodobý provoz, zotavení, distribuce a event readiness. |
| E12 – obraz telefonu | Rozpracováno | Úplný scénář explicitního souhlasu a zařízení. |
| E13 – kamera/SD profil B | Rozpracováno | Skutečná kamera/audio/SD a celý profil na hardware. |

## Ověření softwarové základny

Lokální audit 12. 10. 2026 doložil backendový C# harness a fake HTTP smoke,
kontrakty, 56 mobilních testů a Vite build, nativní C++ simulátor a WSS/media
scénáře, ESP32 i Arduino Nano build, 25 offline AI testů, původní i nové CI provenance
regrese a actionlint (přesné počty jednotlivých sad jsou v auditním protokolu). Tyto výsledky neověřují fyzické pohony ani dnešní IPA.

Nové ověření zahrnuje také 121 CAD Python regresí, 34 viewer testů,
37 STL typů / 52 výtisků a pět STEP sestav, kontrolu mesh/round-trip,
pohybových a kabelových kolizí. Status je
`PASS_CAD_NOT_PHYSICAL_ACCEPTANCE`; fyzická přejímka zůstává otevřená.
Viewer byl ověřen v prohlížeči při šířce 1280, 390 a 320 px. Instalovaný
CAD graf 46 balíčků prošel dependency auditem bez známých zranitelností.

Windows Linux/POSIX testy mají doložené mezery (RID lockfile, Unix práva a
nástroje); nový hosted Linux běh se musí uvést samostatně. Staré raw logy,
serverové adresáře, device identity a jednotlivé provozní incidenty sem nepatří.

## Příprava publikace

Majitel schválil oddělený soukromý deployment repozitář, zachování buildů a
podepsané IPA v tomto zdrojovém repozitáři, kompatibilní CAD upgrade,
odstranění PNG podkladů a vyčištění jejich i stavové historie při zachování
ostatních commitů. Zdrojový repozitář byl se souhlasem majitele zveřejněn
12. 10. 2026; deployment repozitář zůstává soukromý.

Dokončeno a odesláno: oddělení workflow, CAD upgrade a úplná regenerace,
odstranění PNG a vlastní generovaný native icon. Historie 59 původních větví
byla přepsána se zachováním metadata commitů. Dodatečná větev
`codex/sanitized-history` zachovává i vyčištěné commity původně dostupné pouze
z PR. Nový remote mirror ověřil všech 653 přepsaných commitů, žádné PNG a
odstranění původního stavového deníku; obnoven je pouze stručný aktuální stav.

Zdroj nemá VPS runnery ani produkční prostředí/secrets. Dva backend runnery
jsou pouze v soukromém deployment repozitáři. Root-owned hook povoluje přesný
zkontrolovaný private main commit; skutečný odmítnutý job nespustil žádný
krok včetně `always()`. Pět aktivních secrets bylo přeneseno šifrovaně;
diagnostika ověřila soukromý ADMIN credential bez AI volání. Produkční release,
proces backendu a health se nezměnily. Soukromé CI skutečně prošlo na VPS:
36 Node testů, 23 CI Python testů (tři root-only kontroly přeskočeny),
46 deploy testů, 13 diagnostických, 24 offline AI a čtyři IPA helper testy.
Root hook má samostatné mock i skutečné acceptance důkazy.

Buildy a podpis zůstávají ve zdroji na GitHub-hosted Ubuntu/macOS; signing
vyžaduje ruční main a prostředí `ios-signing`. Standardní hosted execution
je po publikaci zdarma. Hosted CI běh `38245975321` prošel po opakování
dočasného GitHub HTTP 504: Secret scan, Repository checks a CI required.
Úplná native akceptace se dokládá samostatně. Na výslovný
souhlas majitele bylo odstraněno všech 18 zbývajících artifactů účtu,
385 769 675 bajtů. Kvota se přepočítává samostatně; úspěšný nový upload
ani IPA zatím nejsou doloženy. Fyzická akceptace zůstává otevřená.
Konkrétní postup vede [CI/CD návod](../../docs/ci-cd.md).

GitHub stále nabízí 175 read-only PR refs se starými kopiemi; běžný push je
nepřepíše. Majitel výslovně rozhodl tyto refs/cache ponechat; Support žádost
nebyla odeslána. Public main ruleset, read-only výchozí workflow token,
schvalování externích PR workflow, SHA pinning a neveřejné hlášení
zranitelností byly zapnuty a ověřeny přes GitHub API.
Původní lokální klony/worktrees zůstaly zachovány pro jejich vlastníky;
veřejnou práci zahájit z nového klonu, nepřipojovat starou historii zpět.

## Předání další AI

1. Zachovat public zdroj a private deployment hranici; placenou AI nespouštět.
2. Zachovat již přijaté servisní výsledky samostatné ESP desky; nevracet je
   znovu do stavu bez důkazu jen kvůli zkrácení deníku.
3. Další funkční krok je skutečné napájení/OE, inspection, kalibrace a bezpečná
   akceptace jedné přihrádky. Neprohlašovat časovanou polohu za měřenou.
4. Hotspot/failover, LAN WSS/TLS, celý hlasový/obrazový průchod, iPhone a
   dlouhodobá mechanická akceptace zůstávají samostatné otevřené scénáře.
