# Arcane Workshop

Společný vizuální jazyk DildoVanderu: klidná technická dílna s mosaznými
akcenty, přesnou telemetrií a čitelnými bezpečnostními stavy. Dekorace má
připomínat řemeslo; nesmí zastínit údaje ani ovládání.

- [Styl a tokeny](style-guide.md)
- [Komponenty a stavy](components.md)
- [Přístupnost](accessibility.md)
- [Specifikace obrazovek](screens.md)
- [Desktop Workshop](mockups/desktop-workshop.html)
- [Mobilní Domov / ovládání](mockups/mobile-home.html)
- [Simulation Lab](mockups/simulation-lab.html)
- [Licence a původ fontů](fonts/README.md)

Každá ukázka je ilustrativní statické HTML s pevnými daty. Neprovádí povely,
síťové požadavky ani práci s fyzickými pohony. Prohlížejte přes místní HTTP
server z kořene repozitáře (například `python -m http.server 8765`) nebo přes
publikovaný přehled. Ukázky odkazují navzájem a sdílí jednu stylovou vrstvu.

Pro novou webovou obrazovku importujte `arcane-workshop.css` a na kořen
přidejte `aw-root`. Komponenty jsou opt-in třídy `aw-*`; CSS nepřidává chování
a nezasahuje do existujících kontraktů. Mobilní bundler importuje stejný
soubor ze zdroje. Offline CAD viewer vloží obsah importů i fontů do svého
generovaného artefaktu, aby nepotřeboval síť.

Při změně tokenů ověřte všechny tři ukázky i skutečné spotřebitele:
mobilní aplikaci, Simulation Lab a generovaný Workshop viewer. Sdílená
CSS vrstva je zdroj pravdy; lokální styly řeší pouze rozložení dané obrazovky.
