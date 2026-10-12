# F2 — ovládání 3D vieweru

Viewer je samostatný offline WebGL dokument. Neposílá příkazy do kufru,
nespouští AI a nepředstavuje fyzickou přejímku ani důkaz montovatelnosti.
Používá zdrojový [Arcane Workshop](../../design-system/README.md), bez npm
závislostí a bez síťového načítání fontů.

## Model a panely

Model vyplňuje volný prostor obrazovky. Na telefonu lišta **Díly**, **Simulace**
a **Detaily** otevírá sbalitelné panely; otevřený panel má vlastní scroll.
V portrétu zůstává model nad panelem, v nízkém landscape vedle něj. Zavírací
tlačítko a Escape vrací klávesnicový focus na příslušnou akci lišty.
Rozložení respektuje safe areas a dynamickou výšku browseru.

- Jeden prst nebo levé tlačítko: orbit.
- Dva prsty: posun kamery a pinch zoom současně.
- Pravé tlačítko: posun; kolečko: zoom.
- Šipky při focusu canvasu: orbit; plus/minus: zoom; Home: celek.
- **Pohled**: izometrie, shora, zepředu, zezadu, zleva, zprava, celek a reset.
- **Vybrat díl** nebo Shift: inspekce, skutečné návrhové vodiče a piny.
  Tap bez tažení vybírá díl i bez zapnutí Inspect. Strom a seznam objektů
  poskytují cestu bez canvasu, také ke skrytým dílům.

Výběr ukazuje název, funkci, CAD obálku, polohu, podsestavu a montážní kroky,
pokud je payload obsahuje. **Zaměřit** plynule přiblíží výběr se zachováním
orientace. Reduced Motion přepne okamžitě. Skrýt/Zobrazit a Izolovat nemění
kabelovou topologii; **Obnovit vrstvy / izolaci** ruší ruční izolaci a skrytí.

V panelu Díly jsou režimy **Celá sestava**, **Nosná konstrukce**,
**Elektronika** (okolí průhledné) a **Odkrytý vnitřek** (skrytý korpus a kryty).
Odkrytí je vizuální pomůcka, nikoli geometrický řez. Geometrie se nemění.
Neimplementuje se umělá explozivní animace ani nepodložená dráha instalace.

Kamera omezuje zoom a posun podle obálky modelu; reset obnovuje úplný model.
Gestům se nepřidává setrvačnost: zastaví se s prstem. Plynulost je u cíleného
zaměření (240 ms). Renderer pracuje na vyžádání, DPR je nejvýše 2; změna
velikosti canvasu a browser viewportu obnoví kreslení. Pointer cancel,
ztráta capture a blur ruší aktivní gesta.

## Montážní průvodce

**Montáž** otevře pokyny. Kompaktní lišta drží Předchozí, Instrukce s počítadlem
a Další i při sbalení sheetu. Seznam kroků umožňuje skok; progress ukazuje
pozici v návodu, nikoli fyzické dokončení. K dispozici je zaměření oblasti,
restart a ukončení. Počet kroků je převzatý z `assembly-guide.json`.

Průvodce zobrazuje kumulativní fyzické osazení v kufru z `caseParts`
(s fallbackem na `parts` u starších payloadů), mosazné zvýraznění dílů aktuálního
kroku, slovní seznam následujících dílů, instrukce, materiál, nástroje,
předchůdce, kontrolu a volitelné údaje o místě, směru, přístupu, konektorech
a demontáži. Volitelné údaje chybějící ve starším payloadu nejsou vyžadované.

`installation.kind = bench` znamená práci **na stole mimo kufr**. Viewer
v tomto kroku zobrazí kumulativní stolní podsestavu z `benchParts`.
U staršího payloadu izoluje současné díly a cíle z `installation.targets`.
`preparedParts` označuje díly pouze připravované na stole: zobrazí a zvýrazní
se v bench náhledu, ale nepřičtou se ke kumulativnímu osazení. Teprve jejich
následující instalační krok je přidá přes `caseParts` (ve starším payloadu
přes `parts`). Montážní krok vložení může převést celou stolní podsestavu
a zvýrazní všechny nově umístěné díly, nejen spojovací materiál. Elektrický bench krok bez
nových dílů nebo cílů zachová hotovou podsestavu bez korpusu. Ukazuje jejich
navržené vzájemné uspořádání; skrytý kufr a výrazný popisek brání záměně za
zasouvání do sestavy. `vertical` zobrazuje výslednou montážní polohu bez
pohybové animace. `installation.absent` jsou podmínky pro skutečný postup,
nikoli příkaz skrýt chybným pořadím osazené díly. `removal` je servisní text.

Volitelné `subassemblies` propojují díl s funkcí, instalací a demontáží.
Payload a pravidla mechanické montáže vlastní generátor; viewer je nemění.
Souřadnice, obálky a kabelové trasy zůstávají návrhové údaje.

## Simulace a hranice

Panel Simulace zachovává šest nezávislých zdvihů, sdílené sekvence, LED skupiny,
lokální tóny, deadline a zastavení se servisním zotavením. **Stop simulace**
zůstává v liště dostupný i po sbalení běžící simulace. Přepnutí režimu během
pohybu stále vyvolá bezpečný modelový Stop. Žádné ovládání nepotvrzuje polohu
skutečného serva, provoz iPhonu, moment, lepený spoj nebo bezpečnost přepravy.

## Zdroj, generování a kontrola

Upravují se `viewer.js`, `viewer-template.html`, `viewer-workshop.css`, nikoli
samotný export. [preview.py](preview.py) vloží CAD payload, sdílený styl,
fonty i lokální skript do `exports/viewer.html`. Indexovaný payload obsahuje
pro každý mesh sdílené `vertices` (ploché XYZ) a `triangles` (ploché indexy).
Viewer před uploadem spočítá jednotkovou normálu každé facety a rozbalí
pozice/normály do GPU bufferu; původní pole pak uvolní. Tessellace a pořadí
vrcholů se nemění. Degenerované facety s normálou menší než 1e-12 se vynechají.
Starší payload s rozbaleným `data` je podporovaný. Indexování zmenšuje export
a jeho přenos/parsing; nezmenšuje GPU buffer ani počet skutečných facet. Z kořene final ve skutečném
CAD prostředí lze spustit `python preview.py --viewer-only`; pro změny geometrie
je potřeba nejprve skutečná kompletní generace a geometrické ověření.

Automatická kontrola z kořene repozitáře:

```powershell
node --check kufr/mechanika/final/viewer.js
node --test kufr/mechanika/final/test_viewer.cjs
```

Fake GPU testy ověřují reálné event handlery, stavy, matematiku kamery,
rozhraní payloadu a zachování inspekce/efektů. Neověřují vykreslený pixel,
browser layout ani skutečný dotykový hardware. Nad přegenerovaným exportem
ověřte 360 px portrét, landscape, desktop, otevření/sbalení sheetů, gesta,
klávesnici, reduced motion, čitelnost a konzoli. Reálná montáž vyžaduje prototyp.


## Rozměry skutečného kufru

Horní průchozí obdélník má potvrzené X 430 × Y 269 mm mezi výztuhami lemu.
Výztuha lemu má celkem 20 mm včetně 5mm stěny; přední klesá 40 mm a zadní
30 mm od horního lemu. Dolní profil YZ má tečné vzdálenosti 45 × 45 mm od
hlavních vnitřních stěn. Středová výztuha dna má Y 80 × Z 5 mm přes celé X 430.
Hlavní výška 114,3 mm je dříve potvrzená; dno 5 mm a víko 79,7 mm zůstávají
předpoklady. Rozhoduje plášť BREP z `chassis.shell()`, nikoli prázdný obdélník.
`safe_zone`/`safe_origin` vymezují návrhové meze a nezaručují volný kvádr.
Popisky rozměrů a inspektor tuto hranici zachovávají; pant používá skutečnou
osu z payloadu. Výkres řezu vzorkuje přímo hrany řezu stejného BREP, včetně
oblouků, výztuh lemu a předpokládaného víka. Není to výkres k výrobě kufru.
