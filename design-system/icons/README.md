# Kanonické ikony Lucide

Offline SVG jsou nezměněné soubory z oficiálního
[lucide-icons/lucide](https://github.com/lucide-icons/lucide/tree/0.548.0/icons),
tag `0.548.0`, commit `e560b8bf3a4f962b4ce2a357366d91271add06a9`.
Před stažením byl tag ověřen přes oficiální GitHub API proti tomuto commitu.
[provenance.json](provenance.json) obsahuje přesnou URL a SHA-256 každého SVG.
[LICENSE.txt](LICENSE.txt) uchovává ISC licenci i oznámení MIT pro části
odvozené z Feather; přibalte ho k distribuovaným ikonám a offline artefaktům.

| Soubor | Význam v UI |
| --- | --- |
| [house.svg](house.svg) | Domov / přehled |
| [sliders-horizontal.svg](sliders-horizontal.svg) | Ovládání / parametry |
| [mic.svg](mic.svg) | Hlas |
| [wrench.svg](wrench.svg) | Servis |
| [boxes.svg](boxes.svg) | Sestava / objekty |
| [flask-conical.svg](flask-conical.svg) | Simulation Lab |
| [wifi.svg](wifi.svg) | Připojení s textovým stavem |
| [wifi-off.svg](wifi-off.svg) | Odpojení s textovým stavem |
| [circle-check.svg](circle-check.svg) | Potvrzený aplikační výsledek |
| [circle-alert.svg](circle-alert.svg) | Upozornění / nejistota |
| [octagon-x.svg](octagon-x.svg) | Stop |
| [scroll-text.svg](scroll-text.svg) | Technický log / časová osa |

Při následné migraci spotřebitele vložte SVG inline nebo existujícím lokálním
mechanismem; nevytvářejte runtime požadavek na CDN. `currentColor` dědí barvu
v inline SVG, nikoli z nadřazeného HTML při samostatném `<img src>`.
Zachovejte viewBox `0 0 24 24`, stroke-width `2`, kulaté konce/rohy.
Běžná velikost je 20–24 px; klikací obal má nejméně 44 × 44 px.

U tlačítka s viditelným názvem má SVG `aria-hidden="true"` a
`focusable="false"`. Samostatné ikonové tlačítko musí mít přesný `aria-label`
(např. „Zastavit kufr“), viditelný focus a tooltip dostupný také klávesnicí.
Tooltip ani `<title>` sám nenahrazuje accessible name ovládacího prvku.
Významová samostatná ilustrace má `role="img"` a český accessible name;
opakovaná dekorace je skrytá asistivním technologiím.

Barva úspěchu a `circle-check` znamenají potvrzený výsledek z daného zdroje,
nikoli změřenou polohu. Nejistá poloha má explicitní text a `circle-alert`.
Migrace existujících glyphů je samostatný následný UI krok s ověřením
klávesnice, balení, CSP a offline načítání.
