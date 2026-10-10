# Komponenty a stavy

| Vzor | Třídy | Pravidlo |
| --- | --- | --- |
| Karta | `aw-card`, `aw-card-heading` | Jeden účel, vlastní nadpis |
| Stavový štítek | `aw-badge` + `--success`, `--warning`, `--danger`, `--info` | Textový stav; neslouží jako tlačítko |
| Hlavní akce | `aw-button` | Jedna hlavní akce v logické skupině |
| Vedlejší akce | `aw-button--secondary` | Zřetelný obrys a text |
| Stop / chyba | `aw-button--danger` | Explicitní text a skutečný stav aplikace |
| Vstup | `aw-field`, `aw-input` | Viditelný label, pomocný text přes `aria-describedby` |
| Upozornění | `aw-notice` | Stručně vysvětluje dopad a další krok |
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

Příklad skutečného vstupu:

```html
<label class="aw-field" for="device">Adresa kufru</label>
<input class="aw-input" id="device" aria-describedby="device-help">
<p id="device-help" class="aw-muted">Použijte adresu z místního připojení.</p>
```

Používejte nativní `button`, `a`, `input`, `select` a `details` podle účelu.
Klikací `div` a dekorativní ikony bez textu nezavádějte.
