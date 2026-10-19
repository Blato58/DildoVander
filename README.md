# DildoVander

Jeden repozitář pro dvě samostatné elektronické rekvizity.

| Projekt | Obsah | Priorita |
| --- | --- | --- |
| [AI kufr](kufr/README.md) | Výběr ze šesti hůlek, hlas, světla, mechanické uvolnění a mobilní ovládání | **Pracujeme nyní** |
| [Obludné obludárium](obludarium/README.md) | Samostatná animatronická kniha bez AI | Až po dokončení kufru |

[Seznam elektroniky a odkazy k nákupu](docs/elektronika.md) jsou rozdělené podle priorit. Komediální kufr Dildovander nepotřebuje elektroniku, proto pro něj nyní nevzniká softwarový projekt.

Firmware obou rekvizit jsou oddělené projekty PlatformIO. Mobilní ovládání patří pouze ke kufru. Každá rekvizita má vlastní napájení; společné jsou jen typy montážního materiálu.

## Vývoj AI kufru

ESP32-S3 samo komunikuje přes HTTPS s backendem a vlastní rozhovor i fyzickou sekvenci. iOS aplikace slouží jako místní ovladač a diagnostika. Repozitář obsahuje ESP-IDF firmware, ASP.NET Core backend se SQLite a adaptéry OpenAI, TypeScript/Vite + Capacitor/Swift aplikaci, hostitelský simulátor a parametrické CAD zdroje. Nativní iOS sestavení a podpis používají macOS GitHub Actions.

Jde o rozpracovaný prototyp pro soukromou party 18+: persony obsahují dospělý humor. Softwarové testy, náhled CAD ani úspěšný build neprokazují bezpečnost a úplnou akceptaci skutečného kufru nebo iPhonu. Přesné dokončené části a otevřené fyzické kontroly vede stav implementace.

- [Implementační plán](kufr/docs/implementacni-plan.md) určuje architekturu, kontrakty a akceptační kritéria etap E0–E13 včetně E3a.
- [GitHub Issues](https://github.com/Blato58/DildoVander/issues) obsahují aktuální práci a blokace, [sdílená koordinace agentů](https://github.com/Blato58/DildoVander/issues/224) umožňuje souběžnou práci. [Starý tracking](docs/project/README.md) je historický archiv.
- [Show Pack](kufr/docs/show-pack.md) popisuje živou režii, identity a proroctví, kroniku, souboje, Director iOS/web a místní offline režim včetně připravenosti audio banky.
- [AGENTS.md](AGENTS.md) určuje postup další AI; [README kufru](kufr/README.md) odkazuje na jednotlivé komponenty a technické podklady.

Pro zahájení další práce stačí: **„Začni implementovat aplikaci podle implementačního plánu.“** Pro navázání: **„Pokračuj v implementaci podle implementačního plánu.“** AI nejprve porovná stav s kódem, pokračuje v nejbližším malém kroku a zapíše ověření i předání. Rozsah lze omezit například na mobilní aplikaci; chybějící hardware neblokuje nezávislou simulaci. Aktuální stav se vede v GitHub Issues, ne v souborech repozitáře; jedna změna nepotřebuje dva PR.

## Vývojové nástroje

- Firmware: PlatformIO Core CLI a jeho závislosti podle `kufr/firmware/platformio.ini`; samotný C++ hostitelský test vyžaduje C++ kompilátor. Instalace PlatformIO IDE ve VS Code je volitelná.
- Mobil: Node.js ve verzi `kufr/mobilni-aplikace/.nvmrc`, `npm ci` a `npm run build`. Na Windows lze verzi vybrat pomocí nvm-windows.
- Backend: .NET 10 SDK podle `kufr/global.json` a místní SQLite mimo repozitář. Výchozí fake režim nepotřebuje OpenAI klíč. Nativní iOS build/test potřebuje Xcode na macOS a pro distribuci vlastní Apple identitu.
- [Vývojové spuštění bez hardwaru](docs/public-development.md) uvádí minimální příkazy a potřebné nástroje; [architektura a hranice ověření](docs/public-architecture.md) propojují komponenty a kontrakty.


## CI a standardy repozitáře

PR CI ověřuje dotčené projekty; změny samotné dokumentace nevyžadují
nesouvisející buildy. Nasazení, podpis a živé placené testy jsou oddělené
správcovské operace. Postup a konfiguraci CI popisuje
[CI/CD návod](docs/ci-cd.md). Pravidla předání jsou v [CONTRIBUTING.md](CONTRIBUTING.md)
a hlášení zranitelností v [SECURITY.md](SECURITY.md).

Zdrojový repozitář je od 12. 10. 2026 veřejný. Buildy používají standardní
GitHub-hosted runnery; produkční runnery nejsou ke zdroji připojené.
Podepsaná IPA vzniká ve stávajícím ručním macOS workflow s prostředím
`ios-signing`. Produkční nasazení serveru, OTA a distribuce IPA mají oddělenou
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

DildoVander je neoficiální fanouškovský projekt inspirovaný světem Harryho
Pottera. Není přidružený k držitelům práv ani jimi schválený. Toto sdělení
nenahrazuje svolení k použití cizích postav, značek, fotografií nebo děl.
Pro spolupráci platí [pravidla chování](CODE_OF_CONDUCT.md).
