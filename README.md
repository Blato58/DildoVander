# DildoVander

Repozitář pro AI kufr: výběr ze šesti hůlek, hlas, světla, mechanické uvolnění a mobilní ovládání.

[Seznam elektroniky a odkazy k nákupu](docs/elektronika.md) obsahuje podklady pro kufr a jeho montážní materiál.

## Vývoj AI kufru

ESP32-S3 samo komunikuje přes HTTPS s backendem a vlastní rozhovor i fyzickou sekvenci. iOS aplikace slouží jako místní ovladač a diagnostika. Repozitář obsahuje ESP-IDF firmware, ASP.NET Core backend se SQLite a adaptéry OpenAI, TypeScript/Vite + Capacitor/Swift aplikaci, hostitelský simulátor a parametrické CAD zdroje. Nativní iOS sestavení a podpis používají macOS GitHub Actions.

Jde o rozpracovaný prototyp pro soukromou party 18+: persony obsahují dospělý humor. Softwarové testy, náhled CAD ani úspěšný build neprokazují bezpečnost a úplnou akceptaci skutečného kufru nebo iPhonu. Přesné dokončené části a otevřené fyzické kontroly vede stav implementace.

- [Implementační plán](docs/implementacni-plan.md) určuje architekturu, kontrakty a akceptační kritéria etap E0–E13 včetně E3a.
- [Vývojová koordinace](docs/development-coordination.md) popisuje privátní úkoly a předání agentů. Veřejná Issues slouží veřejným příspěvkům; implementační PR obsahují technické změny a ověření.
- [Show Pack](docs/show-pack.md) popisuje živou režii, identity a proroctví, kroniku, souboje, Director iOS/web a místní offline režim včetně připravenosti audio banky.
- [AGENTS.md](AGENTS.md) určuje postup další AI.

## Struktura repozitáře

Komponenty jsou přímo v kořeni; sdílené kontrakty a vzhled mají samostatné zdroje.

| Složka | Obsah |
| --- | --- |
| [firmware/](firmware/README.md) | Jeden PlatformIO projekt ESP32-S3, produkce, diagnostika, hostitelské testy a servisní nástroje |
| [firmware/simulator/](firmware/simulator/README.md) | Nativní digitální dvojče nad stejnými C++ funkcemi, fake adaptéry a lokální server |
| [backend/](backend/README.md), [backend-tests/](backend-tests/BackendTests.csproj) | ASP.NET Core backend a jeho testovací program |
| [mobilni-aplikace/](mobilni-aplikace/README.md) | Společná Vue aplikace pro web a Capacitor/iOS |
| [contracts/](contracts/README.md) | Sdílená schémata a validátory protokolů |
| [design-system/](design-system/README.md) | Vapour Chrome tokeny, komponenty a pravidla přístupnosti |
| [mechanika/](mechanika/README.md) | Mechanické zdroje a modelový viewer |
| [web/](web/README.md) | Balení a kontrola sestavené aplikace |
| [ai-eval/](ai-eval/README.md) | Persony, katalog a hodnoticí nástroje |
| [docs/](docs/implementacni-plan.md) | Architektura, hardware, provoz a vývojové návody |
| [devtools/workspace/](docs/development-workspace.md), [scripts/](scripts/) | Vývojový workspace a společné kontroly repozitáře |

Pro zahájení další práce stačí: **„Začni implementovat aplikaci podle implementačního plánu.“** Pro navázání: **„Pokračuj v implementaci podle implementačního plánu.“** AI nejprve porovná stav s kódem, pokračuje v nejbližším malém kroku a zapíše ověření i předání. Rozsah lze omezit například na mobilní aplikaci; chybějící hardware neblokuje nezávislou simulaci. Aktuální stav se vede v GitHub Issues, ne v souborech repozitáře; jedna změna nepotřebuje dva PR.

## Vývojové nástroje

- Firmware: PlatformIO Core CLI a jeho závislosti podle `firmware/platformio.ini`; samotný C++ hostitelský test vyžaduje C++ kompilátor. Instalace PlatformIO IDE ve VS Code je volitelná.
  Ve VS Code lze otevřít přímo kořen `DildoVander-app` (`code .`): kořenový
  `platformio.ini` načítá stejné firmware environments a zobrazí jejich PlatformIO
  tasks. Z kořene funguje `pio run` (produkce), například
  `pio run -e diag-board-memory` (diagnostika). Po přidání konfigurace do již
  otevřeného okna použij **Developer: Reload Window**. Stávající
  `pio run -d firmware` zůstává podporované; oba vstupy mají oddělené `.pio`
  a SDK konfigurace. Kořenový CMake obnovuje ignorovanou kopii partition tabulky
  ze `firmware/`; upravuj pouze tento zdroj. Dependency lock a managed components
  zůstávají ve `firmware/`.
- Mobil: Node.js ve verzi `mobilni-aplikace/.nvmrc`, `npm ci` a `npm run build`. Na Windows lze verzi vybrat pomocí nvm-windows.
- Backend: .NET 10 SDK podle `global.json` a místní SQLite mimo repozitář. Výchozí fake režim nepotřebuje OpenAI klíč. Nativní iOS build/test potřebuje Xcode na macOS a pro distribuci vlastní Apple identitu.
- [Společný vývojový workspace](docs/development-workspace.md) popisuje setup obou samostatných checkoutů, `code .`, multi-root PlatformIO, firmware tasks a testy.
- [Vývojové spuštění bez hardwaru](docs/public-development.md) uvádí minimální příkazy a potřebné nástroje; [architektura a hranice ověření](docs/public-architecture.md) propojují komponenty a kontrakty.


## CI a standardy repozitáře

PR CI ověřuje dotčené projekty; změny samotné dokumentace nevyžadují
nesouvisející buildy. Každý push na `main` spouští ověřené release buildy
a podpis IPA; oddělená soukromá orchestrace nasazuje úspěšný aktuální `main`
včetně TestFlight. Živé placené testy vyžadují samostatné zadání. Konfiguraci popisuje
[CI/CD návod](docs/ci-cd.md). Pravidla předání jsou v [CONTRIBUTING.md](CONTRIBUTING.md)
a hlášení zranitelností v [SECURITY.md](SECURITY.md).

Zdrojový repozitář je od 12. 10. 2026 veřejný. Buildy používají standardní
GitHub-hosted runnery; produkční runnery nejsou ke zdroji připojené.
Podepsaná IPA vzniká ve stávajících macOS workflow s prostředími
`ios-signing` a `ios-app-store`, vyvolaných controllerem `main-release.yml`
nebo ručně pro zotavení. Produkční nasazení serveru, OTA a distribuce IPA mají oddělenou
orchestraci v soukromém repozitáři `Blato58/DildoVander-deploy`; místní fake
vývoj tento repozitář nepotřebuje. Přesun konfigurace sám neprokazuje nasazení.

## Data, licence a příspěvky

[Zpracování dat](docs/public-privacy.md) popisuje mikrofon, volitelnou fotografii,
paměť rozhovorů a technické logy. Pro běžný vývoj používej fake adaptéry a
syntetická data.

Vlastní zdrojový kód, dokumentace a CAD zdroje projektu jsou pod
[GNU Affero General Public License v3.0](LICENSE), SPDX `AGPL-3.0-only`.
Licence třetích stran, včetně fontů a ikon, platí samostatně; AGPL nedává
oprávnění k neověřeným cizím obrázkům nebo značkám.
[Inventář a připsání zdrojů](docs/public-assets.md) uvádí doložený původ,
odstraněné obrazové podklady a zbývající distribuční podmínky. Kopie dodavatelské
pinové mapy, šest referenčních fotografií CAD a čtyři nativní PNG šablony byly
odstraněny; iOS ikona se generuje z vlastního geometrického zdroje.

DildoVander používá původní divadelní role Stínový čaroděj a Mistr hůlek.
Aktuální produktové texty a prompty nejsou vystavěné na cizích knižních či
filmových postavách. Starší interní identifikátory zůstávají kvůli kompatibilitě
uložených nastavení a zařízení; nejsou názvy nabízených rolí.
Pro spolupráci platí [pravidla chování](CODE_OF_CONDUCT.md).
