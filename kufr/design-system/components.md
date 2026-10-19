# Komponenty a stavy Vapour Chrome

Vykonatelná aplikace používá typované Vue komponenty v
`mobilni-aplikace/src/components/` a doménové komponenty v `src/features/`.
Nejprve prohlédněte existující komponenty, sloty a události; nezavádějte další
HTML-string obrazovku. [Sdílené API](framework.md) a
[interaktivní katalog](playground/index.html) dokumentují i kompatibilní
`ui.js`/`ui.d.ts` pro lokální servisní nástroje a staré distribuce. Níže uvedené
třídy zůstávají společným CSS kontraktem, nikoli důvodem kopírovat chování.

| Vzor | Třídy | Pravidlo |
| --- | --- | --- |
| Karta | `aw-card`, `aw-card-heading` | Jeden účel, vlastní nadpis |
| Stavový štítek | `aw-badge` + `--success`, `--warning`, `--danger`, `--info` | Textový stav; neslouží jako tlačítko |
| Hlavní akce | `aw-button` | Jedna hlavní akce v logické skupině |
| Vedlejší akce | `aw-button--secondary` | Zřetelný obrys a text |
| Stop / chyba | `aw-button--danger` | Explicitní text a skutečný stav aplikace |
| Vstup | `aw-field`, `aw-input` | Viditelný label, pomocný text přes `aria-describedby` |
| Upozornění | `aw-notice`, `aw-notice--danger`, `aw-notice--info`, `aw-notice--success` | Stručně vysvětluje dopad a další krok |
| Přihrádky | `aw-slots`, `aw-slot` | 3 sloupce desktop, 2 mobil; číslo a stav slovem |
| Telemetrie | `aw-facts`, `aw-mono` | Sémantické `dl`; neznámé hodnoty mají pomlčku |
| Události | `aw-timeline` | Čas, událost, kontext; čas v monospace |
| Tabulka | `aw-table-wrap`, `aw-table` | Sémantická hlavička, lokální scroll |

Každá akce má výchozí, hover, focus a disabled stav. Při čekání na výsledek
ponechte rozměr ovládacího prvku, ukažte „Připojuji…“ nebo skutečný průběh
a zabraňte duplicitnímu odeslání původní aplikační logikou. CSS smí pouze
vykreslit stav; nemění autorizaci ani podmínky bezpečné akce.

Neznámé, odpojené a chybové hodnoty musí být rozlišeny od „Připraveno“.
Úspěch modelu je úspěch modelu, nikoli důkaz fyzického dosažení polohy.
Nedostupná přihrádka má popisek a přerušovaný obrys; samotná barva nestačí.
Ilustrativní ukázky mají akční tlačítka disabled a trvalé označení UKÁZKA.

Příklad skutečného vstupu v JavaScriptu/TypeScriptu:

```js
import { field } from '../design-system/ui.js'; // cesta podle vlastníka aplikace

const html = field({
  id: 'device',
  label: 'Adresa kufru',
  control: '<input class="aw-input" autocomplete="url">',
  description: 'Použijte adresu z místního připojení.',
});
```

`control` je důvěryhodný aplikační HTML fragment. Hodnoty z dat do něj
nevkládejte bez escapování; label, description a error escapuje renderer.

Používejte nativní `button`, `a`, `input`, `select` a `details` podle účelu.
Klikací `div` a dekorativní ikony bez textu nezavádějte.

## Vizuální hierarchie

Používejte primární tlačítko s `--aw-accent` a textem `--aw-on-accent`.
Vedlejší akce má neutrální obrys. Stop používá červený obrys i text a vždy
zůstává doslovně označený. Navigace a výběr používají `--aw-accent-soft`;
barva je doplněk k popisku, `aria-current` a případnému obrysu.

`aw-status-line` je střídmý řádek stavu bez samostatné karty pro každý údaj.
`aw-section` a `aw-section-heading` oddělují větší části stránky prostorem.
`aw-empty` vysvětluje skutečně prázdný výsledek, `aw-field-error` viditelnou
chybu vstupu propojenou pomocí `aria-describedby`. Vstup používá
`aria-invalid="true"` při validované chybě. `aria-busy="true"` pouze vykresluje
čekání; aplikace zajišťuje zákaz duplicitního požadavku.

Stejná struktura funguje v tmavém i světlém tématu. Používejte sémantické
tokeny pro text a plochy, lokální pevné barvy pro ně nezavádějte.

## Kontrakt stavů

Tyto názvy jsou společný slovník UI, nikoli nová pole zařízení nebo požadavek
na změnu protokolu. Komponenta je odvozuje ze svého skutečného zdroje dat.
Některé stavy existují současně: odpojené zařízení může mít starý log;
načtený panel může zobrazovat neznámou polohu.

| Stav | Povinné zobrazení a chování |
| --- | --- |
| `loading` | „Načítám…“, stabilní rozložení a `aria-busy`; bez předstíraných hodnot. Nové závislé akce nejsou dostupné. |
| `empty` | Známý prázdný výsledek, vysvětlení a případný další krok. „Žádné události“ není totéž co nedostupný server. |
| `ready` | Čerstvá data a slovní stav se zdrojem. Připravený panel neuděluje manual lease, kalibraci ani bezpečnostní oprávnění. |
| `pending` | Čekání na odpověď s popisem probíhající akce; stabilní focus a zákaz duplicitního odeslání. Stop zůstává dostupný podle skutečného transportu. |
| `error` | Věcný důvod, dopad a bezpečný další krok; `role="alert"` při nové chybě. Technické ID lze kopírovat, tajemství nikoli. |
| `disconnected` | „Odpojeno“, poslední údaje označené jako staré; žádná zelená indikace živého spojení. Obnovení spojení ověří identitu a načte nový snapshot. |
| `unknown` | „Neznámé“ nebo pomlčka s vysvětlením. Chybějící ACK, timeout, Stop a restart nedokazují dokončení ani dosaženou polohu. |

Známá nula se zobrazuje jako nula; neznámá hodnota jako pomlčka. Časovač ani
animace nesmí převést `pending` na úspěch. Automatická obnova čtení používá
existující politiku klienta; fyzický povel se po chybě neopakuje novým ID jen
kvůli UI. Respektujte existující idempotenci, revizi a pravidla zotavení.
Při přepnutí zařízení zneplatněte starý výsledek v jeho skutečném vlastníkovi.

## Pojmenované komponenty

Následující odpovědnosti jsou implementační kontrakt; nevytvářejí novou
knihovnu komponent. Při úpravě použijte existující strukturu spotřebitele
a [CSS vzory výše](#komponenty-a-stavy), při potřebě nového chování jeho
vlastní test. Nová routa nebo hardware funkce vyžaduje samostatný slice.

| Komponenta | Kontrakt |
| --- | --- |
| `Tabs` | Viditelný aktivní panel; `tablist`/`tab`/`tabpanel`, `aria-selected`, `aria-controls`, jeden tab stop. Šipky a Home/End fungují pouze uvnitř tablistu; Enter/Space aktivují při manuální aktivaci. Skrytý panel není fokusovatelný. Přepnutí panelu nesmaže rozpracovaný formulář. |
| `StatusIndicator` | Ikona/barva a přesný text. Uvádí zdroj a rozdíl mezi připravenými daty, živým spojením, busy, chybou a nejistotou. Zelená nezastupuje fyzicky změřený stav. Běžné změny oznamuje zdvořile, nikoli při každém heartbeat. |
| `DeviceCard` | Název/identita kufru, spojení, režim, fáze, dostupnost a aktuálnost dat. Prázdný nebo odpojený stav poskytne konkrétní krok k připojení; nikdy ukázkovou identitu jako skutečnou. Akce odvozuje z existujícího snapshotu, lease a bezpečnostní brány. |
| `ConfirmDialog` | Pro existující ruční potvrzovací akce pojmenuje přesný dopad a přihrádku. Dialog má nadpis, popis, `aria-modal`, řízený focus a návrat focusu k invokeru; Escape ruší před odesláním. Výchozí focus je na bezpečné akci. Potvrzení obsluhou není měření polohy. Nezavádí potvrzení autonomního AI výběru ani dialog před Stop. |
| `InspectorPanel` | Výběr objektu, jeho název/ID, rozměry, vazby a zdroj podkladu. Bez výběru vysvětlí jak vybrat. V mobilním overlay má pojmenované zavření, Escape a návrat focusu; výběr ve stromu funguje bez myši. Designované, aproximované a změřené údaje výslovně rozlišuje. |
| `LogViewer` | Omezený technický log s časem, zdrojem, úrovní a korelačním ID; `empty`/`loading`/`error` jsou odlišné. Uvede zkrácení/stránkování tam, kde existuje. Scroll nekrade focus ani nepřepíše čtenářovu pozici při přidání. Kopírování zachovává redakci tokenů a osobního obsahu. |
| `Timeline` | Události nebo fáze mají text, pořadí, čas a zdroj. Zvýraznění aktuálního kroku doprovází `aria-current`; neurčuje konec podle vzhledu. Simulace je označena a požadavek motoru není potvrzení pohybu. Častý progress nezahlcuje live region. |
| `ConnectionBanner` | Rozlišuje připojování, ověřené připojení, odpojení, chybu a veřejný náhled. Stručně uvádí další krok. Trvalé problémy neschovává pouze do toastu. Veřejný náhled nepřijímá token, místní token není v URL, historii ani storage. |
| `EmergencyStop` | Trvale dostupný a doslovně označený Stop, 44 × 44 px, bez potvrzovacího dialogu a animační prodlevy. Používá existující bezpečnostní povel. Po odeslání hlásí čekání na potvrzení; při ztrátě spojení uvádí, že vypnutí není potvrzené a je nutná místní kontrola. Neuděluje lease ani automatické zotavení. |

Pokud Stop nemá použitelnou transportní cestu, stav musí vysvětlit
nedostupnost; kliknutí ani lokální červená karta nesmí tvrdit fyzické vypnutí.
ACK potvrzuje přijetí povelu. Aplikační dokončení potvrzuje výsledek daného
modelu/zařízení. Bez snímače nebo fyzické kontroly neoznačujte polohu za
změřenou. Ilustrační UI má vypnuté akce a nevyvolává transport.
