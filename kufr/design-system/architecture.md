# UI architektura Vapour Chrome

Všechny současné obrazovky používají nativní HTML a ES moduly; Companion
přidává TypeScript/Vite a na iOS Capacitor. Proto sdílíme přímo vykonatelné
HTML komponenty a DOM interakce. React/React Native/Expo/Tamagui by vyžadovaly
přepis fungujících aplikací i offline vieweru a nepřinesly zde další nativní
obrazovky ke sdílení. Swift zajišťuje BLE/LAN, oprávnění, audio a životní cyklus,
nikoli druhou implementaci UI. Existující protokolové typy zůstávají v
`kufr/contracts`; framework nezavádí paralelní zařízeníové modely.

## Vlastnictví a rozšíření

- `tokens.json` je jediný ručně upravovaný zdroj designových rozhodnutí.
  Generátor validuje schéma, hodnoty, aliasy a cykly a vytváří čtyři CSS soubory,
  neměnné `tokens.mjs`, typy a backendové `DesignTokens.g.cs` pro offline karty.
- `ui.js` a `ui.d.ts` jsou společné renderery a přístupné interakce. Tlačítka,
  pole, stavové štítky, oznámení, prázdné stavy a karty hůlek sdílejí stejný kód.
  `bindTabs` vlastní výběr, roving tabindex a šipky/Home/End; `bindDialog`
  používá nativní modalitu a návrat focusu. API je v [kontraktu](framework.md).
- `components/patterns.css` a `components/framework.css` vlastní vzhled a stavy.
  Lokální CSS řeší rozložení, viewport, hustotu nebo skutečný model, nikoli
  druhou paletu či kopii vzhledu tlačítek. Použijte sémantické tokeny.
- Vlastník funkce drží data, autorizaci, debounce/polling, dostupnost, obnovu
  spojení a události. Framework nesmí udělovat lease, potvrdit naměřenou polohu,
  povolit upload ani změnit veřejný/soukromý přístup.

Novou obecnou komponentu přidejte sem, pokud je užitečná ve skutečném toku;
doménový renderer patří sem při opakovaném použití napříč aplikacemi.
Ověřte nejprve existující komponenty, jejich stavy a kontrakty. Text a atributy
se escapují; `field.control` a `emptyState.action` smějí obsahovat pouze důvěryhodné
aplikační HTML, nikdy obsah návštěvníka. Nativní HTML zachovává formulářové
odeslání, klávesnici a systémové ovládání bez další knihovny primitiv.

## Skutečné spotřebitele a generační cesty

| Plocha | Zdroj a sdílené provedení | Balení a hranice |
| --- | --- | --- |
| Companion web/iOS, discovery, provisioning, Debug, hlas, tanec a ovládání | `mobilni-aplikace/src/main.ts` a feature panely importují `ui.js`; společné taby, pole, akce, stav a karty | Vite vytvoří tentýž UI bundle pro browser i Capacitor; Swift adaptéry zachovány |
| Veřejná hlavní stránka | `web/index.html`, společná tlačítka a tokeny | Statické hlavní informace fungují bez JavaScriptu; `web/build-site.py` |
| Soukromá památka, portrét a owner dialogy | `web/keepsake/app.js`, sdílené renderery a dialog | Canonical keepsake-only/full-site builder; consent, portrét a owner scope zůstávají soukromé |
| Veřejný přehled večera a příběh | `web/keepsake/evening/evening.js`, skutečný sdílený renderer karet, oznámení a akce | Veřejné GET-only API; žádný owner, fotografie ani individuální token |
| Simulation Lab / observer | `simulator/dashboard-ui.js`, společná pole, akce, stav a prázdné výsledky | Lokální server explicitně povoluje CSS/JS; site builder kopíruje oba moduly a označí read-only statický náhled |
| Workshop / CAD, Twin, live a media, timeline | `mechanika/final/viewer_ui.js`, `viewer.js`, `twin_viewer.js`, `twin_media.js`, `show_timeline.js` | `viewer_document.py` vloží runtime i CSS; bez síťových importů. Generované snapshoty uchovávají samostatnou obnovu z výrobního ZIPu |
| Servisní servo panel | `firmware/diagnostics/servo-bench/web/panel-ui.mjs` | Existující omezený Python server doručuje společné assety; bez výběru USB se zařízení neotevře |
| Instalační iOS stránka | `backend/deploy/ip-https/ios-placeholder.html`, canonical CSS | Statický serverový stav; žádný alternativní nativní UI framework |
| Offline archivní karty | `backend/Features/Keepsakes/KeepsakeExports.cs` | Generované C# konstanty místo ručně kopírované palety; artwork není UI token |
| Designové ukázky a test harnessy | `design-system/mockups`, `design-system/playground`, mobilní test harnessy | Ukázky zůstávají označené; interaktivní katalog používá skutečný `ui.js` |

Cesty v tabulce jsou relativní ke `kufr/`. V repozitáři není samostatné
SwiftUI UI, React aplikace ani další firmware portál. Budoucí jiný renderer
má spotřebovat generované tokeny a zachovat kontrakty; není důvod zavádět ho
před existencí takové aplikace. Pro jinou platformu rozšiřte generátor,
nekopírujte paletu ručně.

Offline viewer má odůvodněnou generovanou kopii CSS/runtime v `final/`, protože
samostatný výrobní ZIP neobsahuje okolní repozitář. Generuje ji existující
`viewer_style.snapshot_styles()` a `viewer_runtime.snapshot_runtime()`. Není
to druhý autoritativní zdroj. Geometrii kvůli UI nepřepočítávejte; pro lokální
render použijte existující payload přes `refresh_viewer.py` nebo výslovně
syntetický `preview_ui_fixture.py`. UI preview není výrobní export.

## Ověření a CI

`npm test --prefix kufr/design-system` ověřuje escapování, URL/atributy,
formulářovou sémantiku, taby, dialog, neměnnost tokenů a generátor. Drift
generovaných výstupů a vazby exportů kontroluje `scripts/check_design_system.py`.
TypeScript kontroluje props u Companionu; JavaScript má stejné dokumentované
kontrakty a testované bezpečné fallbacky pro neznámé varianty.

Pro browser kontroly spusťte `npm ci --prefix kufr/design-system` a
`npx --prefix kufr/design-system playwright install chromium`, pak
`npm run test:browser --prefix kufr/design-system`. `UI_BROWSER_CHANNEL=msedge`
umožňuje instalovaný Edge; výchozí Chromium používá CI. Kontroly ověřují skutečné
renderování, theme tokeny, šířky, focus, reduced motion a izolaci simulace.
`UI_SCREENSHOT_DIR` volitelně uloží screenshoty; měřitelné layout/style kontrakty
chrání proti regresím bez obří sady rastrových snapshotů závislých na OS.
`UI_MOBILE_DIST` přidá již sestavený Companion s base `/companion/`.
`UI_VIEWER_HTML` přidá skutečný výstup `preview_ui_fixture.py`, ověří kameru,
mobilní panely, timeline a samostatné otevření přes `file://` s vypnutou sítí.
Publikované assety neobsahují vývojové závislosti, testy ani npm lockfile.

Existující syntetické DV10 browser kontroly zachovávají privacy, XSS, consent,
stale odpovědi, export a úzké rozložení. Site CI provádí tyto kontroly v Chromium
po sestavení Companionu. `scripts/ci/select_projects.py` rozlišuje známé UI
soubory od výrobních vstupů; neznámý mechanický soubor zůstává konzervativně CAD
vstupem. Změna vzhledu nespouští geometrii. Ruční úplný main site build stále
vyžaduje ověřené výrobní artefakty.

Browser/syntetické fixture nedokazují fyzickou polohu, nahrání firmware,
skutečné audio/kameru ani iPhone. macOS workflow nadále ověřuje Swift a
nepodepsaný Simulator build; podpis a nasazení zůstávají oddělené.
