# Předání CAD – 9. 10. 2026

Výsledek je **vygenerovaný a geometricky ověřený prototyp**, nikoli fyzicky
akceptovaný výrobek. Referenční vnitřek je 431,8 × 266,7 × 114,3 mm;
3mm stěny jsou volba majitele. Všechny komponenty jsou ve spodní části.

## Skutečné výstupy

| Výstup | Umístění a rozsah |
| --- | --- |
| Parametry a zdroje | `parameters.json`, `reference/`, `components/`, `printable/`, `scripts/` |
| Reference kufru | 5 STEP v `reference/`: tělo, víko, montážní prostor, otevřený a zavřený kufr |
| Sestavy | 7 STEP v `assemblies/`: otevřená, zavřená, prezentace, servis, jedna přihrádka, rozložená a prostorové rezervy |
| Hardware | 44 STEP v `components/`, včetně souhrnné sestavy osazeného hardwaru; katalogové obálky jsou označené podle důvěryhodnosti |
| Tisknutelné díly | 56 různých STL a 56 odpovídajících STEP v `exports/stl/` a `exports/step/` |
| Kusovník | 162 tisknutých kusů včetně dvou měrek; `exports/print-bom.csv` |
| Pozice | 555 interních instancí: 528 materiálových modelů a 27 prostorových rezerv; `assemblies/inventory.json` a `placements.csv` |
| Náhledy | 8 PNG a samostatný offline `renders/viewer.html` |
| Balík | `exports/kufr-complete-provisional.zip`, kontrolní součet `.zip.sha256` |

Kompletní STEP zachovávají pojmenované samostatné díly. Prostorové rezervy
jsou zvlášť, aby v běžném CAD pohledu nezakrývaly komponenty. Zdrojové modely
jsou Python/CadQuery; import STEP do FreeCADu nevytváří nativní parametrický
strom. Původní `model.py` je zachovaná závislost přizpůsobené mechaniky a je
součástí ZIP. Starý adresář `output/` se nepřepisuje.

## Provedené kontroly

- Referenční tělo, víko a kování: platné BREP, spojené listy pantů,
  **106 poloh víka** bez nepovoleného průniku. Fáze A proběhla před osazením.
- Celá sklopená sestava: všechny dvojice posouzené obálkovým filtrem,
  681 přesných průnikových kontrol; **0 nepovolených průniků**.
- Osazení vůči dřevu i kování, rozsah pohybu proti rámu, elektronice a
  otevřenému víku, dráha zavírání se sklopenými hůlkami: **0 kolizí**.
- Uzavření kinematického řetězce ve 101 polohách; kontrola materiálů a
  integrace ve 21 polohách. Sousední moduly mají oddělené intervaly X s
  3mm mezerou i při rozdílných úhlech. Zdvih přední hrany 30,747 mm,
  náklon 7,7503°, sklopená mechanika končí v Z=111 mm (rezerva 3,3 mm).
  Nejvyšší pevný prvek celé zástavby je držák kamery v Z=112 mm; celková
  rezerva pod hranicí spodní části je tedy **2,3 mm**.
- Konektorové rezervy, dvě příčné kabelové trasy a nominální středová osa
  kamery bez průniku. Zorné pole neznámého objektivu se tím nepotvrzuje.
- Vytažení powerbanky vzhůru, 54 M3 rozhraní, průchody šroubů, boční vložení
  matic a 36 cest pro pásky prošly. Kamera má ověřené polohy 35/40/45/50°.
- **56/56 STL**: nenulová uzavřená manifold síť, souhlasná orientace,
  kladný objem, odpovídající obálka a Z=0. Největší zjištěná odchylka
  objemu od CAD přibližně **0,0596 %**, obálky **0,0134 mm**.
- Všechny díly se vejdou do nastavených 220 × 220 × 250 mm. Největší
  rozměry napříč exporty jsou X=210, Y=210,2, Z=75 mm; nejde o jeden díl.
- Zpětný import 56 STEP dílů a hlavní otevřené sestavy prošel. Následuje
  nové čtení skutečných STL proti importovanému STEP, kontrola přesných názvů,
  SHA-256 jednotlivých sítí i zdrojů a ZIP CRC. Strojové výsledky jsou v
  `assemblies/validation.json`, `exports/stl-validation.json` a
  `exports/delivery-validation.json`.
- Vizuálně prohlédnuty skutečné rendery, v prohlížeči vyzkoušené filtry,
  výběr samostatného racku, otáčení a zoom. Konzole při této kontrole bez chyb.

Průnikové testy používají objemový práh 0,1 mm³ a obálkovou toleranci;
vzorkování pohybu není důkazem bezkoliznosti v každém mezilehlém úhlu.

## Reprodukce a regresní testy

Použité prostředí: Python 3.11.3, CadQuery 2.7.0, existující závislosti
`mechanika/requirements.txt`. Skutečně spuštěný příkaz z kořene repozitáře:

```powershell
./kufr/mechanika/cad/Regenerate.ps1 -Python C:/Development/DildoVander/kufr/mechanika/.venv/Scripts/python.exe
```

Regenerace dokončila skutečné exporty, náhledy, ZIP i ověření předání.
S vlastním prostředím lze použít standardní příkaz z [README](../README.md).

| Kontrola | Výsledek |
| --- | --- |
| `scripts/test_mechanics.py -v` | 5 testů PASS |
| `scripts/test_service.py` | 15 skutečných STL a všechny servisní geometrické kontroly PASS |
| `scripts/test_integration_contracts.py -v` | 5 testů PASS, včetně zneplatnění nedokončeného balíku při přerušení |
| Nezávislé review | Nálezy opravené: spojitost pantů, matice/šrouby, průchody pásků, poloha optické osy, integrita exportů |

První export racku odhalil deset hran se čtyřmi sousedními trojúhelníky.
Příčné nosníky se dotýkaly vyvýšených podpor jen hranou. Oprava přidává
0,2mm objemový přesah; testuje se skutečné STL bez změkčení validátoru.

## Hranice platnosti a další fyzický krok

Fotografie uvedené v zadání nebyly dostupné. Exteriér, kování, hloubka víka,
tloušťka dna a půdorys výztuhy jsou parametrické předpoklady. Hůlky 200 mm,
hlavice Ø40 a dřík Ø16 jsou pracovní obálky. Ověřit skutečnou servopáku,
výstupní osu, konkrétní kameru/FPC, neznámé konektory a tiskovou plochu.

Čtyři umístěné Degson bloky představují provizorní rozvod. Zbývajících šest
nakoupených není označeno za nepotřebné; nejprve rozpočítat póly a větve.
Pružný svazek, veškeré šrouby, akustika, teploty, únosnost a zajištění celého
rámu při přenášení nejsou plně ověřené. Některé M3 šrouby vyžadují zkrácení
a odjehlení, aby hlavy lícovaly a závit nevystoupil do PCB.

Další krok: měrky a jedna přihrádka, ruční zkouška, zkouška zatížení a
výpadku napájení, poté zapojení a kalibrace podle [montážního návodu](montaz.md).
Celá sada zůstává **PROVISIONAL**. Fyzická kritéria E5/E6/E13 nejsou uzavřena.
Firmware, backend, skutečný hardware a vzdálený repozitář se touto prací nemění.
