# F1 – nový kouzelný kufr

Samostatný návrh z 9. 10. 2026 a jediný aktivní CAD projektu. Geometrie
tištěných dílů vznikla od základu. Návrh osobně provedl jeden agent;
nebyli použiti subagenti. Elektrické zapojení ani firmware se nemění.

**Výsledkem je parametrický výrobní podklad pro první fyzický prototyp.**
Geometrické ověření, které skutečně proběhlo, uvádí
[strojový report](exports/verification.json). Únosnost, tření brzdy, sílu serv,
zahřívání a rozměry dosud nedoručené kamery nelze potvrdit CAD modelem.
Provedení těchto zkoušek zůstává výslovně otevřené; nejsou zaškrtnuté jako hotové.

## Hotové soubory

- [Kompletní balíček](exports/F1-kouzelny-kufr.zip) – zdroje, dokumentace, všechny exporty a fotografie.
- [Otočný náhled](exports/viewer.html) – otevřít přímo v prohlížeči, funguje bez internetu.
- [Otevřený kufr](exports/images/open.png), [servis](exports/images/service.png),
  [transport](exports/images/transport.png), [detail mechanismu](exports/images/mechanism.png).
- [Rozměrový výkres](exports/layout.svg) a [kinematika](exports/kinematika.svg).
- [STL pro tisk](exports/stl/), [STEP jednotlivých dílů](exports/step/),
  [STEP sestav](exports/assembly/).
- [Tiskový kusovník](exports/print-bom.csv), [osazení s koordináty](exports/installed-hardware.csv),
  [mechanický materiál](mechanicky-material.csv), [montáž](MONTÁŽ.md).

## Zvolená konstrukce

Nový tištěný korpus zachovává potvrzený vnitřní půdorys 431,8 × 266,7 mm.
Vnější půdorys je **439,8 × 274,7 mm**, spodní část vysoká 118,3 mm,
víko 30 mm, mezera 1 mm. Celková výška uzavřené skořepiny je **149,3 mm**
bez kování a popruhů. Nejde o tvrzení, že takové rozměry má starý dřevěný kufr.
Požadavek na nový korpus je zde realizován jako úplná tištěná náhrada;
starý kufr slouží jako rozměrová a vzhledová reference.

Korpus a víko mají každý šest polopřeplátovaných dílů. Šrouby M4 spoje
zarovnávají a stahují. Tři kovové panty, dvě kovové přezky a dva textilní
omezovače drží víko při otevření na 105°. Víko neobsahuje elektroniku.
Pro přenos je navržen uzavřený textilní postroj vedený **pod dnem**;
tištěné stěny a rukojeť nenesou celý náklad v několika malých šroubech.

Šest stejných lůžek má rozteč 48 mm a délku 212 mm. Každé používá tři
tištěné díly: základnu, lůžko a drážkovou vačku. Koupené servo přes původní
35mm páku otáčí vačkou; zachycený váleček zvedá i stahuje lůžko o **16 mm**.
Koncové kruhové úseky drží zatížení bez hnacího momentu. Seřiditelná plstěná
brzda vodítka má udržet také mezilehlou polohu po Stop. Její skutečná
funkce a souběžná dostatečná síla serva jsou podmínkou zkoušky prvního kusu.

Prostor zakrývají **čtyři velké kryty tvaru L** se svislými clonami. Přední
a zadní lišty jsou jejich součástí. Montáž nepotřebuje čtyři další krycí díly.
Servisní stolky a čtyři základny vytvářejí rovné plochy pro suchý zip;
reproduktory, pohony, vysoké sestavy a rezervní díly mají navíc pásky.
Powerbanka je nastojato v mechanickém lůžku vlevo, porty dopředu.

Celkem **29 typů STL, 44 výtisků včetně jedné měrky**. Největší půdorys
je 214 × 202,7 mm; s 3mm brimem 220 × 208,7 mm. U přístroje s menší
skutečně použitelnou plochou omezit brim na 2 mm; díly nezmenšovat měřítkem.

## Zdrojové podklady a meze

Závazné jsou [potvrzené zapojení](../../docs/zapojeni-potvrzeneho-hardwaru.md),
[rozměry nákupů](../rozmery-nakoupenych-soucastek.md),
[rozměry doručeného kufru](../rozmery-doruceneho-kufru.md),
[kamera](../../docs/kamera.md) a [plán](../../docs/implementacni-plan.md).
Podrobná evidence obrázků, rezerv a hardwaru je v [PODKLADY.md](PODKLADY.md).
Předání tohoto výhradně mechanického zadání je v [STAV.md](STAV.md).

Absolutní délku hůlky odvozuji z popisku fotografie 19–20 cm. Šířka hlavice
38 mm, výška 28 mm a průměr dříku 16 mm jsou **rezervy, nikoli měření**.
Na fotografiích není samostatné pouzdro hůlky; modeluje se vlastní otevřené
lůžko. Rozměry a tolerance jsou v [parameters.json](parameters.json).
Kamera, FPC, obálka svorek, poloha osy serva a detaily servopáky obsahují
označené předpoklady. Pro ně jsou vytvořené montážní rezervy a zkušební postup.

## Regenerace

### Inspect v 3D náhledu

Držet **Shift** a najet na díl: zvýrazní se skutečný viditelný povrch a
zobrazí se název, popis, rozměrový podklad, CAD obálka a kabelové vazby.
Shift nad prázdnou plochou bez připnutého výběru zobrazí názvy všech dílů
zapnutých vrstev, jejichž středy jsou v záběru; popisky mají spojnice k objektům.
**Klik** připne kartu. Připnutý výběr zachová jen vlastní propojení i při
držení Shiftu. **Klik do prázdna** výběr zruší i bez Shiftu; tažení výběr zachová.
**Puštění Shiftu** vypne Inspect a umožní otáčení pohledu, připnutá karta
a její vazby zůstávají. Připnutí samo Inspect nezapíná trvale.
Tlačítko **Inspect** režim zapne bez držení klávesy; puštění Shiftu jej
opět vypne. Výběr **Objekt** v kartě umožňuje ovládání klávesnicí.
**Esc** inspektor zavře.
Pohled **Servis** odkryje elektroniku. Vypnuté vrstvy se kurzorem nevybírají.

Barva se mění pro konkrétní ID dílu a ukládá do `localStorage` prohlížeče;
obnoví se po zavření a opětovném otevření stejné stránky v témže profilu.
Tlačítko **Původní barva** smaže danou volbu. Pokud prohlížeč úložiště
zakáže, karta výslovně oznámí, že barva platí jen do zavření. Jiná adresa,
jiný profil, soukromé okno či vymazání dat nejsou stejným úložištěm.

Červené spojnice značí napájení, modré signály a šedé společnou GND;
přerušovaná spojnice vede ke skrytému dílu. Seznam vazeb obsahuje GPIO,
kanály PCA, napětí a poznámky. Jde o elektrické vztahy podle potvrzeného
zapojení, nikoli o změřené kabelové trasy. Rezervní zakoupené díly jsou
z náhledu i seznamu Inspect odstraněné.
ID svorek Degson a kondenzátorů nemají v podkladu přiřazenou konkrétní síť;
prohlížeč ji neodvozuje z polohy CAD. Pořadí ID úseků LED je označené jako
konvence vizualizace.

`inspect_data.py` načítá oba závazné Markdown podklady při generování.
Jejich přesné texty a SHA-256 se ukládají do `reference/inspect-sources.json`
pro regeneraci z vybaleného ZIPu. V repozitáři mají přednost aktuální
podklady; ověření odmítne zastaralou kopii. `viewer.js` se vkládá přímo
do HTML, náhled nepotřebuje síť ani soubory vedle sebe.
Kabelové vazby jsou ručně přepsané podle ověřené verze zapojení. Její
SHA-256 je v `inspect_data.py` jako `REVIEWED_WIRING_SHA256`; změna dokumentu
zastaví obnovu podkladů i generování. Po změně je nutné zkontrolovat
`connection_data()`, aktualizovat vazby a regresní testy, teprve potom
záměrně změnit tento hash a znovu generovat. Platí to i pro zdrojový ZIP.

Pouze náhled, bez nového exportu STEP/STL a PNG, lze obnovit příkazem:

```powershell
..\.venv\Scripts\python.exe preview.py --viewer-only
```

Tento příkaz neobnovuje výrobní balíček ani jeho geometrické ověření.
Pro celý balíček použít následující postup.

Použito Python 3.11 a CadQuery 2.7.0. Úplná regenerace navíc vyžaduje
Node.js v PATH pro kontrolu syntaxe a regresní testy prohlížeče.
Žádný plugin ani GUI CAD není nutný.
Z této složky:

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\Regenerate.ps1 -Python .\.venv\Scripts\python.exe
```

V této práci byl použit již existující interpreter
`C:\Development\DildoVander\kufr\mechanika\.venv\Scripts\python.exe`.
Závislosti se kvůli tomu neměnily. Skript exportuje CAD, ověří STEP a STL,
ověří pohyb a servis, vykreslí modely a vytvoří ZIP. Selhání libovolného
kroku zastaví balení. STL už mají orientaci pro tisk a spodní plochu na Z=0.
Sestavy jsou v globálních souřadnicích: X doprava, Y od obsluhy dozadu, Z nahoru.

Formát exportů odpovídá [oficiální dokumentaci CadQuery](https://cadquery.readthedocs.io/en/latest/importexport.html).
Výstupy nejsou renderové makety: obsahují uzavřené STL sítě a skutečné BREP/STEP.
Číslování `body_1..6` a `lid_1..6`: zleva doprava přední řada 1–3, zadní 4–6
v tiskové orientaci. Při osazení víka se orientace X obrátí podle sestavy STEP.

Generované soubory jsou v `exports/`, reprodukovatelné zdroje a dokumentace
zůstávají v této složce. Nic se nezapisuje do starých modelů, aplikace nebo serveru.
