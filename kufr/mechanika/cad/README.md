# CAD doručeného kufru – jednoduchá modulární verze

Skutečná parametrická sestava pro vnitřek **431,8 × 266,7 × 114,3 mm**.
Vše je ve spodní části kufru, víko zůstává prázdné. Python/CadQuery 2.7 a JSON
jsou editovatelné zdroje; STEP jsou přesné pojmenované BREP sestavy, STL skutečné
objemové exporty. Import STEP do FreeCADu nevytváří jeho nativní strom operací.

Nová verze nahrazuje dělené lišty a šroubovaný rack: **18 typů STL, 56 výtisků
včetně jedné měrky** místo původních 56 typů / 162 výtisků. Šest samostatných
mechanismů a čtyři boční kazety se připevní suchým zipem. Drobné patky se jednou
vlepí do vodicích kapes; při servisu se vyjímá celý modul. Transport je navržen
**bez hůlek a powerbanky**, s páskem kolem každého prázdného lůžka.

**Stav: geometricky ověřovaný prototyp, fyzická přejímka dosud neprovedena.**
Fotografie určují konstrukční uspořádání a dodavatelský údaj délky hůlky
190–200 mm. Průměry, hmotnost, lepidlo na skutečném dřevě a tiskárna zůstávají
neověřené. Tisk první přihrádky a zkouška uchycení předchází tisku celé sady.

## Otevření výsledků

- [Interaktivní 3D náhled](renders/viewer.html), [pohled shora](renders/04-top.png),
  [servisní kazety](renders/08-service.png), [transport](renders/09-transport.png).
- [Otevřená sestava](assemblies/complete-open.step), [zavřená sestava](assemblies/complete-closed.step),
  [prezentace třetí hůlky](assemblies/presentation.step), [rozložená sestava](assemblies/exploded.step).
- [Transport otevřený](assemblies/transport-open.step), [transport zavřený](assemblies/transport-closed.step).
- [Jedna přihrádka](assemblies/single-compartment.step), [servisní vrstva](assemblies/service-layer.step),
  [samostatné prostorové rezervy](assemblies/clearance-reservations.step).
- [Balík zdrojů, CAD, STL a návodů](exports/kufr-complete-provisional.zip).
- [Kusovník: názvy, počty, rozměry, účel a tisk](exports/print-bom.csv),
  [pozice všech dílů](assemblies/placements.csv), [inventář](assemblies/inventory.json).
- [Geometrická validace](assemblies/validation.json), [STL validace](exports/stl-validation.json),
  [zpětný import a integrita balíku](exports/delivery-validation.json).

[Montáž a fyzické zkoušky](docs/montaz.md) · [výsledky předání](docs/overeni.md) ·
[rozměry zakoupených součástek](../rozmery-nakoupenych-soucastek.md).

## Parametry a evidence

Počátek je levý přední vnitřní roh na povrchu dna; X doprava, Y k pantům,
Z nahoru. Všechny délky jsou v mm. Tloušťky dřeva rostou ven z vnitřní obálky.

| Parametr | Hodnota a stav |
| --- | --- |
| Vnitřek spodní části | 431,8 × 266,7 × 114,3; potvrzený majitelem |
| Čtyři stěny | 3; výslovná volba majitele, nikoli měření |
| Dno / víko uvnitř / horní deska víka | 3 / 30 / 3; pracovní předpoklady |
| Výztuha dna | Výška 5 potvrzena; hloubka 90 je konzervativní návrhová rezerva, nikoli odečet z fotografie |
| Výztuha ve víku | Fotografie dokládá příčnou lať; pracovní obálka 90 × 5 |
| Panty, zámky, madlo | Parametrická referenční geometrie podle jejich funkce a polohy na fotografiích; rozměry neměřené |
| Otevření víka | Nominálně 105°; skutečný doraz neověřen |
| Mechanismy | Rozteč 49; délka 240; základna Z10, lůžko Z54, osa zadního čepu Z61 |
| Hůlky | Dodavatelský obrázek uvádí 190–200; model délka 200, dřík do16, ozdobná obálka do40, oba průměry neověřené |
| Tiskárna | 220 × 220 × 250; konfigurovatelný předpoklad, není potvrzeným strojem |

Vnější rozměry dřeva odvozené z těchto předpokladů jsou 437,8 × 272,7 × 150,8.
Fotografie není milimetrovým výkresem. Příčná výztuha na snímcích je široká;
patky proto stojí mimo pracovní 90mm pás, základny ho přemosťují s vůlí.
Barevné válce hůlek jsou konzervativní kolizní obálky, nikoli kopie ornamentů.
Otevřená V sedla s měkkou výstelkou umožňují přizpůsobení skutečnému dříku.

## Rozmístění a servis

Středy přihrádek X = **93,4; 142,4; 191,4; 240,4; 289,4; 338,4**; Y =133,35.
Ornamenty směřují dopředu. Servo s původní pákou, adaptérem a táhlem zdvihá přední
část lůžka kolem zadního M4 čepu a vrací ji. Nejde o vyhazování rekvizity.

| Varianta | Prostor / výroba / servis | Rozhodnutí |
| --- | --- | --- |
| Veškerá elektronika pod mechanismy | Výšková kolize s pohony, obtížný servis | Zamítnuto |
| Elektronika za mechanismy | Zbývá pouze 26,7mm hloubky celkem | Zamítnuto |
| Boční kazety na suchém zipu | Snadné vyjmutí, několik nízkých vrstev a lehké odnímatelné můstky | Implementováno |

Powerbanka stojí vlevo nad čtyřmi provizorními svorkami; jde vyjmout vzhůru.
Mikrofon a PTT jsou vpředu vlevo, napájecí kolébky vpředu vpravo. Reproduktory
směřují do středu; mezera vedle mechanismů vede zvuk vzhůru. Akustické zastínění
se musí poslechnout, návrh netvrdí laděnou ozvučnici. Kamera na pevném 45° klínu
míří dopředu a nahoru, nad ESP; skutečný objektiv/FPC stále nejsou doložené.

Servis serva vyžaduje jen vyjmutí jeho modulu a uvolnění jeho čepů/pásků.
Svorky jsou přístupné po vyjmutí powerbanky. Spodní pravé PCB po uvolnění pásků
horních můstků. Do dřeva se nevrtá; přilnavost suchého zipu je podmínkou přejímky.

## Reprodukování

Z `kufr/mechanika/`:

```powershell
.\cad\Regenerate.ps1 -Python C:/Development/DildoVander/kufr/mechanika/.venv/Scripts/python.exe
```

Na jiném počítači vytvořit `.venv` podle `requirements.txt` a vynechat `-Python`.
`-CheckOnly` ověří geometrii, `-SkipRenders` vynechá obrázky. Menší tisková
plocha může vyžadovat další dělení; generátor nadměrný díl odmítne, nezmenší hardware.

`reference/case.py` nejprve vytvoří a ověří tělo/víko. `components/hardware.py`
obsahuje zakoupené obálky. `printable/mechanics.py`, `service.py`, `frame.py`
vytvoří díly a umístění. `scripts/generate.py` kontroluje kolize, pohyb a zavření,
potom exportuje skutečné STL/STEP, kusovník, devět renderů a ZIP.
`scripts/verify_delivery.py` znovu načte STEP/STL, ověří SHA zdrojů i sítí a ZIP CRC.

```powershell
.\.venv\Scripts\python.exe .\cad\scripts\test_mechanics.py -v
.\.venv\Scripts\python.exe .\cad\scripts\test_service.py
.\.venv\Scripts\python.exe .\cad\scripts\test_integration_contracts.py -v
```

Původní `../model.py` zůstává pouze zdrojem kinematického řešení a obecných
geometrických funkcí; nové díly tvoří nová CAD vrstva. Původní výstupy se nemažou.
Generované CAD/STL jsou lokální a ignorované Gitem; trvalé jsou zdroje a návod.
Každá změna parametrů vyžaduje novou generaci i fyzické přezkoušení dotčených dílů.
