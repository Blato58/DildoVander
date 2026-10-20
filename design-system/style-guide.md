# Styl a tokeny

Autoritativní hodnoty jsou v `tokens.json`. Soubory pod `tokens/`,
`tokens.mjs`, jejich typy a backendový C# export jsou generované pomocí
`scripts/generate_design_tokens.py`; ruční změnu proveďte ve zdrojovém JSON.
Typografie zahrnuje výšky řádků, váhy a tracking; layout zahrnuje rozestupy,
dotykové rozměry, vrstvy, safe-area a breakpointy pro adaptéry. Rozložení
konkrétního nástroje může mít odůvodněný vlastní bod zlomu.

## Vapour Chrome

Přesná technická práce potřebuje klidné plochy a čitelná rozhodnutí.
Používejte promyšlené řazení obsahu, odlišnou váhu nadpisů a velkorysé
oddělení sekcí. Jedna skupina má jednu hlavní akci. Dekorativní gradienty,
světelné aureoly a opakované karty bez významu do systému nepatří.

Identita DildoVanderu zůstává v názvech, příbězích, modelu kufru a skutečných
hůlkách. Humor může doplnit text, ale nesmí nahradit akci, chybu nebo stav.
Stop se vždy jmenuje Stop. Simulaci a nejistou polohu označujte doslova.

## Vizuální identity

Výchozí **Lord Voldemort** používá neutrální `#0f0f11`, text `#e3e4e6`
a akcent `#606acc`. Úspěch je `#69c967`, chyba `#ff7e78`, zvláštní role
`#c2a1ff`. **Olivander** zachovává níže popsanou původní identitu Vapour Chrome.
Obě identity používají lokální Inter a IBM Plex Mono. Paletu i rozměry
prezentace řídí `themeRegistry` v `tokens.json`; [téma a obnova](THEMES.md)
popisují přepínání a zálohu. Pro drobný akční text používejte `--aw-link`,
protože přesný nový akcent není určen pro běžný text na všech plochách.

## Zachovaná primitiva Olivanderu

| Primitivum | Hodnota | Úloha |
| --- | --- | --- |
| `--aw-mist` | `#EEF1F5` | Vapour Mist; světlé plochy a text tmavého tématu |
| `--aw-midnight` | `#1B2430` | Chrome Midnight; tmavé plochy a text světlého tématu |
| `--aw-steel` | `#8A94A6` | Steel Grey; doplňková grafika, nikoli automaticky text |
| `--aw-periwinkle` | `#B8C0FF` | Digital Periwinkle; tmavý akcent |

Primitiva nepřepínejte podle tématu. Spotřebitelé používají sémantické role:

| Token | Tmavé / světlé téma | Použití |
| --- | --- | --- |
| `--aw-void` | Midnight / Mist | Základní plocha |
| `--aw-surface` | `#222D3B` / `#FFFFFF` | Obsahové panely |
| `--aw-elevated` | `#2D394A` / `#E2E7EF` | Vrstvy a hover |
| `--aw-ivory` | Mist / Midnight | Primární text; název je kompatibilní role |
| `--aw-muted` | `#B4BECE` / `#586579` | Čitelný pomocný text |
| `--aw-accent` | Periwinkle / `#4E5594` | Akce, odkazy a výběr |
| `--aw-on-accent` | Midnight / bílá | Text hlavního tlačítka |
| `--aw-accent-soft` | `#303C58` / `#E7E9FC` | Vybraný řádek nebo přihrádka |
| `--aw-border` | `#7B899E` / `#758295` | Rozpoznatelné ovládání |
| `--aw-border-subtle` | `#3B485B` / `#CDD4DF` | Doplňkové oddělení obsahu |

Světlý akcent je odvozený odstín pro čitelný text. Steel Grey není univerzální
pomocný text: na světlém pozadí potřebuje roli `--aw-muted`. Úspěch, chyba,
varování a informace mají samostatné sémantické barvy a vždy slovní popis.
Bezpečnostní červená zůstává vyhrazena pro Stop, chybu a související stav.
`--aw-brass` je pouze kompatibilní alias akcentu; nepoužívejte jej v novém UI.

## Typografie a prostor

Inter pro celé UI včetně značky a nadpisů. IBM Plex Mono pro časy, revize,
ID, jednotky a diagnostiku; nepoužívejte jej pro souvislou prózu. Všechny
fonty jsou lokální. Cinzel není součást aktivního UI. Nadpisy používají
sentence case, bez ozdobných kapitálek. Hlavní nadpis je stručný a odpovídá
účelu stránky. Delší vysvětlení omezte přibližně na 70 znaků řádku.

Typografická API je v `tokens/typography.css`: `--aw-text-display` pro veřejný
hlavní nadpis, `--aw-text-title` pro název nástroje, `--aw-text-heading` a
`--aw-text-subheading` pro sekce, `--aw-text-body` pro obsah,
`--aw-text-label`, `--aw-text-caption`, `--aw-text-button` a
`--aw-text-technical` pro příslušné role. Pro konkrétní kompozici lze použít
responzivní velikost, ale nevytvářet vlastní paralelní typografickou stupnici.

Základní krok je 4 px. Používejte 8–12 px uvnitř skupiny, 16–24 px mezi
skupinami a 32–48 px mezi sekcemi. Ovládání má nejméně 44 × 44 px.
Poloměr je 6 px pro vstup a tlačítko, 10 px pro panel. Stavový štítek má
malý poloměr; nemusí vypadat jako tlačítko. Stíny jsou střídmé a mají význam
vrstvy; běžné panely odděluje plocha a jemná hranice.

## Pohyb a témata

Přechody používají sdílené časy 150/200/250 ms a `--aw-ease-out`.
Pohyb vysvětluje změnu stavu. Trvalé pulzování, odpočítávání bez zdroje a
animovaný úspěch místo potvrzení nejsou přípustné. Reduced motion přechody
vypíná. Bezpečnostní změna se zobrazí okamžitě.

Výchozí je `data-theme="lord-voldemort"`; původní identita používá
`data-theme="olivander"`. Sdílený runtime řeší persistenci i bezpečný fallback.
Historické light/dark/system selektory zůstávají pro staré integrace.
Stejné komponenty musí být čitelné v obou identitách bez lokálních pevných
barev pro text a plochy.
