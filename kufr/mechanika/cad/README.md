# CAD doručeného kufru

Parametrická sestava pro potvrzený vnitřek **431,8 × 266,7 × 114,3 mm**.
Všechno osazení je ve spodní části; víko zůstává prázdné. **Návrh je
prototypový: ověření geometrie není potvrzením fyzické montáže ani uvolněním
celé sady k výrobě.** Stěny 3 mm jsou výslovná návrhová volba majitele.

Používáme existující **CadQuery 2.7 / OpenCascade**, nikoli nový CAD systém.
Python a JSON jsou editovatelné parametrické zdroje. STEP obsahuje pojmenované
samostatné díly; po importu do FreeCADu jde o přesné BREP, ne o nativní strom
FreeCAD parametrických operací. STL jsou skutečně exportované objemové díly.

## Otevření výsledků

- [Otočný náhled, filtrování kategorií a jednotlivých dílů](renders/viewer.html).
- [Kompletní otevřená sestava](assemblies/complete-open.step),
  [zavřená sestava](assemblies/complete-closed.step),
  [prezentace třetí hůlky](assemblies/presentation.step),
  [rozložená sestava](assemblies/exploded.step).
- [Servisní konstrukce](assemblies/service-layer.step) a
  [jedna kompletní přihrádka](assemblies/single-compartment.step).
- [Samostatné prostorové rezervy konektorů a kabelů](assemblies/clearance-reservations.step);
  v hlavních STEP sestavách nezakrývají skutečné díly.
- [Balík zdrojů, CAD, STL, náhledů a dokumentace](exports/kufr-complete-provisional.zip).
- [Tiskový kusovník](exports/print-bom.csv), [kontrola skutečných STL](exports/stl-validation.json),
  [úplný report geometrie](assemblies/validation.json),
  [poloha každé instance v mm](assemblies/placements.csv).
- [Zpětné načtení STEP a kontrola balíku](exports/delivery-validation.json).

Podrobnosti stavby: [montáž, kabeláž a akceptace](docs/montaz.md).
Výsledky předání: [soubory, provedené kontroly a zbývající omezení](docs/overeni.md).
Základní rozměrový podklad z worktree f0e4 je totožný s
[lokálním přehledem](../rozmery-nakoupenych-soucastek.md); staré 310 × 450 × 200 mm
není rozměrem tohoto návrhu.

## Zdroje a pořadí regenerace

| Soubor/adresář | Účel |
| --- | --- |
| `parameters.json` | Rozměry kufru, kloubové mechaniky, tiskárny a stav znalostí |
| `reference/case.py` | Tělo, dno, výztuha, víko, panty, madlo, zámky a kování |
| `components/hardware.py` | Katalogové obálky, model reproduktoru, kamery a ovladačů |
| `printable/mechanics.py` | Adaptace původní ověřované kloubové mechaniky; importuje `../model.py` |
| `printable/service.py` | Konkrétní rozmístění elektroniky a její montážní díly |
| `printable/frame.py` | Dělené podlahové lišty a servisní desky bez vrtání do dřeva |
| `scripts/generate.py` | Celý postup, brány validace, exporty, kusovník, náhledy, ZIP |
| `scripts/validate.py` | Nezávislé objemové průniky, obálky a skutečné binární STL |
| `scripts/verify_delivery.py` | Zpětný import STEP, shoda zdrojů, kontrola ZIP a jeho SHA-256 |

Výstupy jsou lokální, ignorované Gitem; trvalý zdroj jsou uvedené skripty,
JSON a dokumentace. Balík obsahuje také závislost na původním `model.py` a
`parametry.json`. Původní modely ani jejich `output/` se nemažou. Generátor
obnovuje vlastní CAD/STL/náhledy v `cad/`; staré modely nezahrnuje do nové sady.

V `kufr/mechanika/` vytvořte prostředí podle existujícího `requirements.txt`:

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\cad\Regenerate.ps1
```

Lze předat existující Python: `./cad/Regenerate.ps1 -Python C:/cesta/python.exe`.
`-CheckOnly` zkontroluje geometrii a referenci, ale nevytvoří novou tiskovou
sadu. `-SkipRenders` vytvoří geometrii a balík bez obrázků, nepřibalí staré
náhledy. Chybějící prostředí se neinstaluje potichu.

Generátor nejprve **vytvoří a ověří fázi A**, teprve potom sestaví komponenty,
rozmístění a držáky. Při kolizi zastaví export. Každý tisknutelný model musí být
jediný validní solid, vejít se do tiskové obálky a projít kontrolou skutečného
STL: nenulové trojúhelníky, uzavřené manifold hrany, orientace stěn, kladný
objem, rozdíl objemu vůči CAD do 2 % a shoda obálky do 0,15 mm.
Regenerační příkaz poté znovu načte exportované STEP díly a hlavní sestavu,
zkontroluje shodu zdrojů s reportem a integritu ZIP. Kontrolní součet balíku
je vedle něj v souboru `.zip.sha256`.

Regresní kontroly:

```powershell
.\.venv\Scripts\python.exe .\cad\scripts\test_mechanics.py -v
.\.venv\Scripts\python.exe .\cad\scripts\test_service.py -v
.\.venv\Scripts\python.exe .\cad\scripts\test_integration_contracts.py -v
```

## Parametry a jejich důvěryhodnost

Počátek `[0,0,0]` leží v levém předním vnitřním rohu na horním povrchu dna.
X jde doprava, Y dozadu k pantům, Z nahoru. Dno a stěny se modelují směrem
ven z potvrzené vnitřní obálky, jejich tloušťka nezmenšuje využitelný prostor.

| Parametry v JSON | Výchozí hodnota | Stav |
| --- | --- | --- |
| `case.inside_width/depth/height` | 431,8 / 266,7 / 114,3 | Potvrzeno majitelem |
| `case.wall_left/right/front/back` | 3 každý | Volba majitele pro tuto úlohu, neměřeno |
| `case.floor_thickness` | 3 | Návrhový předpoklad |
| `case.brace_height` | 5 | Potvrzeno majitelem |
| `case.brace_depth`, `brace_y_fraction` | 30, 0,5 | Předpoklad příčné výztuhy přes šířku |
| `case.lid_inside_height`, `lid_top_thickness` | 30, 3 | Předpoklad, nezapočteno do zástavby |
| `case.lid_gap`, `hinge_rear_offset` | 0,5, 5 | Předpoklad zavírání |
| `case.hinge_x_fractions`, `hinge_radius` | 0,2 / 0,8; 2,5 | Nominální kování, vyžaduje porovnání s kusem |
| `case.lid_open_angle` | 105° | Návrhová otevřená poloha |
| `mechanism.pitch`, `tray_length`, `base_z` | 50, 240, 10 | Konstrukční volba |
| `mechanism.wand_length/head_diameter/shaft_diameter` | 200 / 40 / 16 | Pracovní obálka, průměry neměřené |
| `manufacturing.printer_bed_mm` | 220 × 220 × 250 | Konfigurovatelný předpoklad, tiskárna nepotvrzena |

Vnější šířka je součtem vnitřní šířky a obou stěn, výchozí **437,8 mm**;
vnější hloubka **272,7 mm**. Zavřená výška dřeva včetně dna vychází
**150,8 mm**, kování ji může překročit. Tyto odvozené rozměry nejsou měření.
Fotografie zmíněné v zadání nebyly v aktuální konverzaci dostupné, proto
nejde o potvrzenou fotogrammetrickou kopii povrchů a kování.

Změna JSON jednoznačně regeneruje geometrii. Není to automatický optimalizátor
libovolného kufru: při změně rozměrů se znovu kontroluje každá kolize a tisková
obálka. Nepasující komponenty se nezmenšují. Menší tiskárna může vyžadovat
další dělení konstrukce; generátor nadměrný díl odmítne.

## Zvolené uspořádání

| Varianta | Prostor a vzhled | Výroba, spolehlivost a servis | Rozhodnutí |
| --- | --- | --- | --- |
| Celá elektronika pod šesti lůžky | Čistý pohled, ale vrstva se střetává se servy/táhly; výška 114,3 mm nestačí bez změny zdvihu | Hůře přístupné konektory, vyžaduje rozsáhlejší přestavbu | Nepoužito |
| Elektronika vzadu za lůžky | Málo potřebných vrstev | 240mm lůžka ponechávají jen 26,7 mm hloubky celkem | Nepoužito |
| Šest lůžek uprostřed, boční servisní kazety | Využije šířku kufru, přiznává servisní techniku po stranách | Zachová kloubovou mechaniku, powerbanka jde ven samostatně, horní pravý rack je snímatelný | **Implementováno** |

Středy modulů X = **90,9; 140,9; 190,9; 240,9; 290,9; 340,9 mm**,
Y = 133,35 mm. Rekvizity míří ozdobným koncem dopředu. Servo přes původní
páku, tištěný adaptér a táhlo naklopí lůžko na zadním M4 čepu. Zachovává se
požadované vysunutí/prezentace/návrat pevné rekvizity; nejde o vyhazování hůlky.

Powerbanka stojí na hraně vlevo; oba reproduktory jsou svisle podél boků,
ozvučnou plochou ke středu. Jejich vyzařování může mechanika částečně zastínit:
model netvrdí návrh naladěné ozvučnice. Mikrofon má volný horní port vlevo
vpředu. ESP a provizorní kamera sdílejí přední pravou platformu; kamera míří
nahoru a k návštěvníkovi. Její přesný modul, FPC i zorné pole nejsou známé.

Na PCB se nevymýšlejí otvory: používají se otevřené podpěry hran a uvolnitelné
izolační pásky. Montážní otvory CAD patří tištěným nosičům. Serva lze vyjmout
po sejmutí vlastní hůlky/lůžka a uvolnění vlastního táhla; ostatních pět modulů
se nerozebírá. Spodní pravá elektronika je přístupná po odšroubování společného
horního racku, nikoli okamžitě bez demontáže.

## Co přesně ověřujeme

- Veškeré tuhé modelované díly ve sklopené poloze proti sobě i vůči dřevu.
- Uzavření táhla ve 101 polohách; tuhé díly mechanismu ve 21 polohách.
- Sousední moduly mají oddělené intervaly X při jakékoli kombinaci náklonů.
- Otevřené víko a boční osazení proti pohybu; dráhu zavírání se **sklopenými** hůlkami.
- Volný svislý prostor k vytažení powerbanky po odpojení kabelu a pásků.
- Lokální konektorové rezervy a skutečnou modelovanou středovou osu kamery;
  neznámé zorné pole tím není ověřené.
- Cesty 54 servisních spojů M3, přístup bočních matic a zapuštění hlav
  vůči dnu/výztuze; přesné délky šroubů se dopasují při montáži.
- BREP a skutečné STL, jejich rozměry a orientaci na Z=0; rozdělené podlahy a lůžka.

Mezi vzorky není dokázaná nepřetržitá bezkoliznost. Pružné kabely, jejich
poloměry ohybu, některé montážní šrouby, skutečný FPC, pružné pásky a prsty
montéra nejsou úplně geometricky ověřené. Není provedena FEA, měření zatížení,
teplot ani akustiky. Nezavírat se zdviženou přihrádkou. Mechanické zajištění
pro přenášení a zadržení při výpadku napájení musí projít fyzickým testem.
