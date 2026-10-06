# AI kufr — hlavní projekt

Aktuální priorita. Cíl: šest hůlek, krátký adresovatelný RGB pásek a vibrační motor pod každou přihrádkou, samostatné servo zdvihy přihrádek, mikrofon, reproduktor a tlačítko. **ESP32-S3 komunikuje přímo s backendem; telefon není potřebný pro běžný rozhovor.** AI sama vybere dostupnou hůlku. Po místním ověření ESP32 ji servo vysune, předvede a zase zasune bez potvrzení obsluhy. Hůlka zůstává jako rekvizita; normální cyklus nepotřebuje ruční reset. Stop nebo chyba vyžadují kontrolu a zotavení. iOS aplikace je volitelný ruční ovladač a záložní textový/hlasový vstup. Při nedostupné AI/WAN musí zůstat místní ruční volba.

D143 přidává opt-in **autonomní GPT-Live a hybridní paměť**: Live tvoří
vlastní řeč, backend ověřuje skutečný výběr, SQLite vlastní revizovanou
paměť a OpenAI Vector Store její povolený odvozený index. Výchozí režim
i katalog personalit zůstávají. [Aktivace, rollback, stores a retence](docs/autonomous-live-memory.md)
jsou připravené; skutečný cloud, poslech a mechanika vyžadují samostatné
ověření podle [stavu implementace](docs/stav-implementace.md).

Vybraná rekvizita: [sada dvou dřevěných truhel Marketos](https://www.marketos.cz/marketos-truhla-drevena-2-ks-vintage-hneda/). Pro AI kufr je zamýšlená větší truhla; skutečný vnitřní prostor změříme po doručení. Menší truhla patří komediálnímu kufru, který je bez elektroniky.

## Složky

- [`firmware/`](firmware/) — samostatný projekt PlatformIO pro ESP32-S3.
- [`mobilni-aplikace/`](mobilni-aplikace/) — současný TypeScript/Vite základ; plánované iOS balení Capacitor/Swift, vývoj Windows a vzdálený macOS CI build.
- [`contracts/`](contracts/) — první JSON Schema lokálního protokolu v1 a sdílené pozitivní/chybné zprávy.
- [`simulator/`](simulator/) — spustitelná hostitelská časová osa čistého modelu výběru a deterministické scénáře.
- [`mechanika/`](mechanika/README.md) — parametrické STL/STEP prototypy šesti zdvihacích přihrádek, držáků vibrací a krabiček; skutečné usazení a pasivní stabilita čekají na měření.
- [`backend/`](backend/) — ASP.NET Core 10 s device tokenem, SQLite journalem a pamětí večera, fake režimem a Responses dialogem Sol.
- [`docs/implementacni-plan.md`](docs/implementacni-plan.md) — rozhodnutí, protokol, bezpečnost, předběžné piny A/B, testování a etapy.
- [`docs/stav-implementace.md`](docs/stav-implementace.md) — jediný průběžný přehled realizace, ověření, blokací a dalšího kroku.
- [`docs/kamera.md`](docs/kamera.md) — volitelné rozšíření o obraz; vybraná deska má kamerový konektor.
- [`docs/efekty-a-napajeni.md`](docs/efekty-a-napajeni.md) — návrh RGB, vibrací a napájení kufru.
- [`docs/pin-mapa-esp32-s3.md`](docs/pin-mapa-esp32-s3.md) — dodaná pinová mapa desky, její omezení a odkaz na předběžná přidělení A/B.

## Jak pokračovat

Přečti [AGENTS.md](../AGENTS.md), plán a stav implementace. Plán říká **co a za jakých podmínek stavět**, stav říká **co existuje, co bylo ověřeno a co dělat dál**. Podrobné závislosti a kritéria jsou pouze v kapitole 12 plánu; zde se nevede druhý checklist.

Začíná se bezpečným jádrem E1 a kontrakty/simulátorem E2. Následuje ruční aplikace E3 a její iOS CI E3a; souběžně lze rozvíjet backend E7 bez desky. Přímá síť firmware E4, měřený hardware E5/E6 a dialog E8 umožní samostatný hlas E9. E10 dokončí mobilní zálohu, E11 provozní balení. Kamera E12/E13 je volitelné pozdější rozšíření. E0 sbírá provozní a hardwarové předpoklady průběžně; chybějící měření blokují hardware, nikoli hostitelské testy.

Backend E7 existuje, iOS projekt/CI a první instalace jsou doložené ve stavu. ESP-IDF runtime skládá provisionovaný místní TLS/WSS textový vstup a backendový výstup; fyzické porty a nativní iPhone transport čekají na své závislosti. Simulátor pokrývá hostitelský model výběru a fake místní transport; sdílené kontrakty popisují lokální protokol i základní HTTP API. Po každém implementačním kroku aktualizovat stav i README skutečně změněné komponenty. Build není důkaz fyzického účinku ani úspěšné instalace IPA.

Nákupní seznam je v [`docs/elektronika.md`](../docs/elektronika.md). Firmware má hostitelsky testované bezpečné jádro, journal, přímého backendového klienta a místní textový transport. Mobil používá simulátor; skutečné GPIO/audio/ESP32 a native WSS akceptace zůstávají otevřené.
