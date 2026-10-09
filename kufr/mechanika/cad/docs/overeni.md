# Předání jednoduché CAD verze – 9. 10. 2026

Skutečně vygenerovaný prototyp. Fyzická montáž, únosnost a transport dosud
nejsou potvrzené. Revize vychází z nově přiložených fotografií a požadavku
na jednoduchý tisk, suchý zip a přenášení bez hůlek a banky.

## Soubory a změna rozsahu

| Výstup | Skutečný rozsah |
| --- | --- |
| Parametrické zdroje | `parameters.json`, `reference/case.py`, `components/hardware.py`, `printable/*.py`, `scripts/*.py` |
| Tisk | 18 STL a 18 odpovídajících STEP; 56 výtisků včetně měrky |
| Referenční kufr | 5 STEP: tělo, víko, montážní prostor, prázdný otevřený/zavřený |
| Sestavy | 9 STEP v `assemblies/`, včetně otevřeného/zavřeného transportu bez hůlek a banky |
| Hardware | 40 STEP v `components/`, včetně souhrnného instalovaného hardwaru |
| Instance | 261 interních instancí, z toho 243 materiálových a 18 rezerv |
| Obrazové výstupy | 9 PNG, `renders/viewer.html`, `renders/viewer-transport.html` |
| Kusovník a pozice | `exports/print-bom.csv`, `assemblies/placements.csv`, `assemblies/inventory.json` |
| Předání | `exports/kufr-complete-provisional.zip` a `.zip.sha256` |

Předchozí verze měla 56 typů STL a 162 výtisků. Odstraněné jsou dělené lišty,
spojky, samostatné držáky serv/dorazy/sedla, závěsné klece U059 a 54 servisních
šroubových rozhraní. Nahradily je jednodílné mechanismy a čtyři boční kazety.
Původní historické modely se nemění, staré výstupy této CAD větve nahrazuje
vlastní generátor. Editovatelným zdrojem je Python/JSON; STEP je BREP bez
nativního FreeCAD stromu. Veškeré modelování provedl hlavní agent.

## Ověření

- Fáze A před osazením: platné referenční objemy a spojené listy pantů,
  **106 vzorkovaných poloh víka** bez průniku.
- Celá sklopená sestava: 396 přesných průnikových kontrol
  po obálkovém filtru všech dvojic; **0 nepovolených kolizí**.
- **101** kinematických poloh, **21** materiálových poloh. Mechanika, elektronika,
  dřevo, otevřené víko a kabelové koridory bez zjištěné kolize. Oddělení sousedních
  mechanismů v X je 2 mm i při rozdílném náklonu. Náklon 7,80745°, zdvih přední
  hrany přibližně 30.973 mm. Sklopená mechanika končí v Z103, rezerva 11,3 mm.
- Nejvyšší celek je banka v Z111; návrhová rezerva pod okrajem spodní části
  je 3,3 mm. Zavření je ověřeno pouze pro sklopená lůžka.
- Transportní model: šest prázdných zajištěných lůžek, banka a hůlky odebrané;
  388 přesných kontrol, **0 kolizí**, vyhovující
  hranice kufru a všech 106 poloh zavírání. Dva horní můstky mají také modelované pásky.
- Svislé vytažení powerbanky, lokální konektorové rezervy, dva kabelové koridory
  a nominální středová osa kamery bez průniku. Zorné pole neznámého objektivu
  ani celá ohebná kabeláž tím nejsou ověřené.
- **18/18 skutečných STL**: nenulové uzavřené manifold sítě, konzistentní orientace,
  kladný objem, odpovídající obálka, Z0 na podložce. Maximální rozdíl objemu proti
  CAD přibližně 0.0349 %, obálky
  0.0044 mm.
- Všechny díly se vejdou do nastavených 220 × 220 × 250 mm. Dlouhé základny
  se tisknou diagonálně, obálka 202,233 × 202,233 mm. Reálná podlahová plocha
  základny i lůžka na bedu přesahuje 8 000 mm²; nejde o model visící na jednom oku.
- **9 mechanických a 5 integračních regresí** prošlo. Servisní kontrola ověřila
  všech 11 skutečně exportovaných STL v dočasném adresáři a 19 sad retenčních
  metadat/cest. Kontrolovány i klíče patek, jejich dosednutí a výztuha.
- Nezávislé read-only review odhalilo ignorovaný parametr délky hůlky a možné
  plovoucí patky při změně výšky. Obojí opraveno, dvě cílené regrese prošly také
  u reviewera. Délka mimo 170..216 mm nyní vyžaduje změnu návrhu a export je odmítnut.
- Generátor a následný `verify_delivery.py` načítají zpět všech 18 STEP dílů,
  hlavní sestavu i skutečné STL. Kontrolují přesné názvy, rozměry, SHA sítí a
  zdrojů i ZIP CRC. [Strojový záznam](../exports/delivery-validation.json) je
  rozhodující pro aktuální balík; změna zdrojů vyžaduje novou generaci.
- Vizuálně prohlédnuty skutečné rendery osazení, servisní vrstvy a transportu.
  Nejde o AI vytvořenou představu výsledku, ale zobrazení modelovaných objemů.
  HTML transportní náhled se načetl, výběr samostatného lůžka i návrat na sestavu
  fungovaly; při této kontrole nebyla zaznamenaná chyba konzole.

Průnikový práh je 0,1 mm³, u lokální mechaniky 0,01 mm³. Vzorkování pohybu
není spojitý matematický důkaz. Modely hůlek jsou konzervativní kolizní obálky,
nikoli plastická kopie jejich ozdob.

## Zbývající omezení

Neověřené zůstávají hmotnost/průměry šesti hůlek, dodaná servopáka, konkrétní
kamera/FPC, některé konektory a výšky PCB, přesné svorky a jejich rozdělení,
tiskárna, zatížení, únosnost spojů, akustika a teploty. Stěny3 mm jsou volba
majitele; půdorys latě90 mm je návrhová rezerva podle její orientace na fotografii.
[Fotografická evidence](fotografie-a-omezeni.md) výslovně rozlišuje tyto údaje.

Suchý zip a lepené patky mají popsanou nosnou cestu, ale skutečná přilnavost
k mořenému dřevu není z CAD prokazatelná. [Montážní návod](montaz.md) obsahuje
zkoušku uchycení a přenášení se zajištěnými prázdnými lůžky. Zkouška na skutečném
kufru dosud neproběhla, sada proto není označená za výrobně uvolněnou.

Neproběhly změny firmware/backendu, elektrického zapojení, upload do hardware,
push, PR, merge ani nasazení. Další fyzický krok: měrka, jeden modul a zkouška
uchycení; poté dopasování neznámých rozměrů a tisk celé sady.
