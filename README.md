# DildoVander

Jeden repozitář pro dvě samostatné elektronické rekvizity.

| Projekt | Obsah | Priorita |
| --- | --- | --- |
| [AI kufr](kufr/README.md) | Výběr ze šesti hůlek, hlas, světla, mechanické uvolnění a mobilní ovládání | **Pracujeme nyní** |
| [Obludné obludárium](obludarium/README.md) | Samostatná animatronická kniha bez AI | Až po dokončení kufru |

[Seznam elektroniky a odkazy k nákupu](docs/elektronika.md) jsou rozdělené podle priorit. Komediální kufr Dildovander nepotřebuje elektroniku, proto pro něj nyní nevzniká softwarový projekt.

Firmware obou rekvizit jsou oddělené projekty PlatformIO. Mobilní ovládání patří pouze ke kufru. Každá rekvizita má vlastní napájení; společné jsou jen typy montážního materiálu.

## Vývoj AI kufru

Schválený směr: ESP32 samo komunikuje s backendem a vede hlasový rozhovor; iOS aplikace je volitelná ruční/textová záloha. Vyvíjí se na Windows, nativní iOS sestavení a podpis jsou plánované přes macOS GitHub Actions. Dnes existuje sériový firmware prototyp a statický TypeScript/Vite ovladač; backend, síť, AI a iOS balení zatím implementované nejsou.

- [Implementační plán](kufr/docs/implementacni-plan.md) určuje architekturu, kontrakty a akceptační kritéria etap E0–E13 včetně E3a.
- [Stav implementace a předání práce](kufr/docs/stav-implementace.md) obsahuje dokončené části, důkazy ověření, blokace a nejbližší proveditelný krok.
- [AGENTS.md](AGENTS.md) určuje postup další AI; [README kufru](kufr/README.md) odkazuje na jednotlivé komponenty a technické podklady.

Pro zahájení další práce stačí: **„Začni implementovat aplikaci podle implementačního plánu.“** Pro navázání: **„Pokračuj v implementaci podle implementačního plánu.“** AI nejprve porovná stav s kódem, pokračuje v nejbližším malém kroku a zapíše ověření i předání. Rozsah lze omezit například na mobilní aplikaci; chybějící hardware neblokuje nezávislou simulaci. Stav se vede v repozitáři, není potřeba dohledávat tento chat.

## Vývojové nástroje

- Firmware: PlatformIO Core CLI a jeho závislosti podle `kufr/firmware/platformio.ini`; samotný C++ hostitelský test vyžaduje C++ kompilátor. Instalace PlatformIO IDE ve VS Code je volitelná.
- Mobil: Node.js ve verzi `kufr/mobilni-aplikace/.nvmrc`, `npm ci` a `npm run build`. Na Windows lze verzi vybrat pomocí nvm-windows.
- Budoucí backend: .NET 10 SDK. Nativní iOS build/test potřebuje Xcode na macOS runneru; na Windows se Xcode neinstaluje.
- Po každém dokončeném implementačním slice se provede commit a push podle [AGENTS.md](AGENTS.md). Pro repozitář se dvěma přihlášenými účty GitHub používá push skript `scripts/Push-CurrentBranch.ps1`.


## CI a standardy repozitáře

PR s kódem spustí build/test CI pouze pro dotčené projekty; změny samotné
dokumentace buildy nespouštějí. Krátká synchronizace PR přimíchá cílovou
větev před buildy; konflikty ponechá k ručnímu řešení. Nasazení přebírá ověřené artefakty
bez nového buildu; postup, cache a dostupnost ochrany větve popisuje
[CI/CD návod](docs/ci-cd.md). Pravidla předání jsou v [CONTRIBUTING.md](CONTRIBUTING.md)
a hlášení zranitelností v [SECURITY.md](SECURITY.md).
