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
ostatních commitů. Viditelnost zdrojového repozitáře zůstává soukromá.

Lokálně dokončeno: oddělení workflow, CAD upgrade a úplná regenerace,
odstranění referenčních PNG a vlastní generovaný native icon. Git rewrite
a přechod runnerů se dokládají samostatně podle skutečného provedení. Podepisování vyžaduje původní main,
ruční spuštění a prostředí ios-signing; do veřejného CI nepatří produkční
runnery ani provider/diagnostické credentialy. Konkrétní postup vede
[CI/CD návod](../../docs/ci-cd.md).

Soukromý deployment zdroj je připraven a odeslán; read-only token byl
potvrzen pouze podle metadata. Nový hosted běh GitHub zablokoval před
spuštěním kvůli Actions budgetu účtu. Migrace VPS runnerů/credentialů a
skutečná native kompilace/podpis čekají na samostatné ověření.

GitHub PR refs/cache nelze přepsat běžným pushem. Historické kopie v těchto
odkazech a klonech musí být vyřešeny zvlášť; samotný rewrite není důkazem
úplného odstranění dat. Úspěšné kroky, nové kontroly a zbývající blokace se
při předání doplní pouze podle skutečného výsledku.

## Předání další AI

1. Dokončit a ověřit publikační slice, bez změny viditelnosti a bez placené AI.
2. Zachovat již přijaté servisní výsledky samostatné ESP desky; nevracet je
   znovu do stavu bez důkazu jen kvůli zkrácení deníku.
3. Další funkční krok je skutečné napájení/OE, inspection, kalibrace a bezpečná
   akceptace jedné přihrádky. Neprohlašovat časovanou polohu za měřenou.
4. Hotspot/failover, LAN WSS/TLS, celý hlasový/obrazový průchod, iPhone a
   dlouhodobá mechanická akceptace zůstávají samostatné otevřené scénáře.
