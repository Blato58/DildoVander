# Vapour Chrome

Sdílený vizuální jazyk DildoVanderu. Přesný, klidný a současný prostor pro
ovládání kufru, simulaci i technickou práci. Hierarchii vytváří typografie,
volný prostor a zřetelné skupiny; Digital Periwinkle označuje akci a výběr.
Příběh hůlek patří do obsahu. Ovládání a bezpečnostní stav mluví doslova.

- [Styl a tokeny](style-guide.md)
- [Komponenty a stavy](components.md)
- [Přístupnost](accessibility.md)
- [Specifikace obrazovek](screens.md)
- [Aktivní cesty a vlastnictví stavů](journeys.md)
- [Přehled ukázek](mockups/index.html)
- [Desktop Workshop](mockups/desktop-workshop.html)
- [Mobilní ovladač](mockups/mobile-home.html)
- [Simulation Lab](mockups/simulation-lab.html)
- [Lokální fonty a licence](fonts/README.md)
- [Offline ikony a licence](icons/README.md)
- [Architektura a mapa spotřebitelů](architecture.md)
- [Vykonatelné komponenty a kontrakty](framework.md)
- [Interaktivní katalog skutečných komponent](playground/index.html)

## Integrace a kompatibilita

Ve společné web/iOS aplikaci importujte `arcane-workshop.css` jednou přes
`mobilni-aplikace/src/application/application.css` a používejte Vue komponenty
v `src/components/` a doménové feature komponenty. `ui.js` zůstává kompatibilitou
pro lokální servisní nástroje a starší neměnné distribuce. Nové obrazovky patří
do společného Vue Routeru, nikoli do HTML-string rendereru. Historický název
vstupního souboru, prefix `--aw-*` a třídy `aw-*` zůstávají integračním
kontraktem. Název souboru neoznačuje aktuální vizuální identitu. Nový kód
používá `--aw-accent`; `--aw-brass` je pouze alias `var(--aw-accent)` pro
starší spotřebitele. Sémantické role se zachovávají napříč tématy.

Výchozí vizuální identita je **Lord Voldemort** (`lord-voldemort`), tmavý
Codex-inspired vzhled. **Olivander** (`olivander`) zachovává původní Vapour
Chrome. Společný synchronní runtime `theme.js` řeší počáteční téma, místní
persistenci a okamžité přepnutí; Vue používá `theme.mjs`. Výběr je v nastavení
a v hlavičce aplikace i v samostatných nástrojích. Historické dark/light/system
CSS selektory zůstávají kompatibilní s archivovanými integracemi.

Podrobnosti, hranice synchronizace, přidání tématu a obnovu původního designu
popisuje [Theme system](THEMES.md).

Inter se používá pro titulky i UI, IBM Plex Mono pro technické údaje.
Fonty jsou lokální a nevyžadují CDN. Offline CAD viewer musí vložit importy
a fonty svou skutečnou generační cestou. Lokální styly řeší rozložení,
nikoli další barevnou paletu. Barvy fyzických materiálů modelu se nemění.

## Ověření

Zdroj tokenů je `tokens.json`; CSS, typované ES moduly i C# export se generují
pomocí `python scripts/generate_design_tokens.py`. Generované soubory neupravujte.
Stejný generátor vlastní RGB pozadí nativního launch storyboardu a označený CSS
blok samostatného iOS publisheru. Ostatní XML/layout a publikační logika zůstávají
v jejich vlastních zdrojích; spuštění generátoru nic nepodepisuje ani nenasazuje.
Před novou komponentou prohlédněte `ui.js` a interaktivní katalog. Sdílené
renderery přijímají data a vracejí bezpečně escapované HTML; stav a transport
zůstávají ve vlastníku funkce. iOS zobrazuje stejné komponenty v Capacitoru.

Z kořene repozitáře:

```text
python scripts/generate_design_tokens.py --check
npm test --prefix design-system
python scripts/check_design_system.py
```

Browser regresní kontroly používají uzamčený Playwright v tomto balíčku:
`npm ci --prefix design-system`, instalaci Chromium přes jeho CLI a
`npm run test:browser --prefix design-system`. Podrobnosti a hranice jsou
v [architektuře](architecture.md). CI ověřuje skutečné komponenty a dotčené
spotřebitele; změna UI sama nespouští výrobní CAD generování.

Ukázky jsou statické HTML s pevnými daty. Vždy nesou označení ukázky a
neodesílají povely ani síťové požadavky. Prohlížejte je přes místní HTTP
server z kořene repozitáře, například `python -m http.server 8765`.

Po změně sdíleného CSS ověřte ukázky a skutečné spotřebitele: mobil,
Simulation Lab, generovaný Workshop viewer a veřejný přehled. Kontrolujte
obě témata, focus, úzkou šířku, chybové stavy a reduced motion. Ukázka ani
webový build neprokazují iPhone, živé zařízení nebo fyzický výsledek.
