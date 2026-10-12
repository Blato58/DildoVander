# Stav implementace AI kufru

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
