# F2 — vložka do skutečného dřevěného kufru

F2 nahrazuje chybný tištěný korpus F1. Skutečný kufr se nevrtá ani neřeže.
Uvnitř je čtyřdílný PETG rám s integrovanými předními/zadními žebry, šest
nezávislých vačkových zdvihů a pevné držáky elektroniky. Čtyři tištěné
podlahové kotvy připojují rám k holému dřevu DNA přes spodní epoxidový film
a odlité mechanické zámky Ø7,5/Ø10,5. Konstrukce se neopírá o neověřenou
chemickou adhezi epoxidu k PETG. Hůlky, powerbanka a textil jsou odnímatelný obsah;
nejsou nutné pro soudržnost vložky.

**Návrh není fyzicky přijatý ani uvolněný k sériovému tisku.** CAD ověření
nepotvrzuje skutečné rozměry hůlek a konektorů, soudržnost dřeva/lepidla,
moment serva ani jeho chování bez PWM. Tyto podmínky zůstávají výslovně
otevřené v [předání](STAV.md) a [montážním návodu](MONTÁŽ.md).

Výstupy lze přijmout pouze s ověřením, jehož zdrojové SHA-256 odpovídají
aktuálním souborům; starší ověření nepokrývá zjednodušení montáže. Návrh má
53 typů STL / 78 výtisků včetně čtyř samostatných zkušebních měrek pro čep, drážku
a boční kapsy matic M3/M4 i epoxidový zámek s odvzdušněním. Každý typ má svůj orientovaný STL, STEP, modelový 3MF a ověřovaný profil v [tiskovém manifestu](exports/printing-manifest.csv). [Výrobní návod](TISK.md) uvádí podporu NONE, rozebíratelné spoje a omezení kontroly. Skutečné počty
exportovaných dílů uvádí aktuální tiskový seznam.

## Výrobní revize F2-SF-DFA — 13. 10. 2026

Servo používá od 21. 10. 2026 [STEP dodaný majitelem](reference/SG90-Servo.md)
včetně montážních patek a vývodu kabelů. Osa serva, vačky a páky je o 1 mm
výše (lokální Z37); upravené lůžko a vedení zachovávají jejich návaznost.
Tištěná [servopáka a měrka drážkování](SERVO-HORN.md) jsou prototypy pro trysku 0,4 mm.
Tato změna vyžaduje nové výrobní ověření; starší výrobní výstupy ji nepokrývají.

Elektronika a pevné svazky vznikají na stole před vložením rámu a před
pohyblivými lůžky. Dvě boční kazety mají sedm šroubů přístupných shora;
nohy procházejí skutečnými servisními drážkami vedlejších nosičů.
Vodicí M4 se vkládají zespodu před rámem a jistí se horními maticemi.
V kufru se nejprve dotáhnou všechny základny, potom nasadí hotové horní
skupiny. Rám stojí samostatně bez banky, hůlek, krytů i textilu.

[Úplný postup v 16 krocích](POSTUP.md) se generuje ze stejných dat jako
montážní průvodce; [ovládání vieweru](VIEWER.md) popisuje dotykové gesto,
pohledy, díly a kompaktní mobilní panely. Starší patnáctikroková montáž
v historických záznamech nepokrývá tuto revizi. Aktuální ověření stále
neprokazuje fyzickou akceptaci.

## Co otevřít

- [Interaktivní sestava](exports/viewer.html): provoz, servis, kabeláž,
  montáž vpřed/zpět a přeprava; šest samostatných zdvihů a dvě sekvence.
- [Balíček F2](exports/F2-kouzelny-kufr.zip): zdroje, STEP, STL, rendery,
  montážní a kabelové podklady; otisk v `exports/package-sha256.txt`.
- [Výsledek geometrických kontrol](exports/verification.json),
  [tiskový seznam](exports/print-bom.csv), [materiál k doplnění](mechanicky-material.csv),
  [seznam elektroniky](elektronika.csv), [řezný seznam kabelů](exports/cable-cut-list.csv).
- [Specifikace](SPECIFIKACE.md), [měření a předpoklady](PODKLADY.md),
  [montáž](MONTÁŽ.md), [kabeláž](KABELAZ.md), [efekty](EFEKTY.md).
- [Úplný inventář a potvrzené zapojení](exports/reference-sources.html)
  jsou přibalené také pro čtení mimo repozitář.

## Rozměry a návrh

Měření majitele z 12. 10. určuje volný horní obdélník **430 × 269 mm**,
stěnu 5 mm, přední/zadní výztuhu širokou 20 mm včetně stěny a vysokou
40/30 mm dolů od okraje. Spodní boční oblouky mají tečné rozměry 45 × 45 mm;
CAD je modeluje jako R45. Výztuha dna je 80 × 5 mm uprostřed přes celou
šířku. Podrobnosti a předpoklady jsou v [měření kufru](../rozmery-doruceneho-kufru.md).
Rám 400 × 240 mm je nad výztuhou; čtyři kotvy mají zakřivený spodek,
0,5mm svislý film a vynechávají pás výztuhy. Kvádrová obálka nenahrazuje
kontrolu skutečných těles. V klidu zástavba zůstává pod okrajem 114,3 mm.
Dno 5 mm, víko a panty ještě vyžadují suchou montáž.
Zvoleno šest modulů na rozteči 48 mm, s délkou 212 mm a zdvihem 16 mm.
Majitel 10. 10. 2026 potvrdil délku hůlky do 200 mm a šířku hlavice 38 mm
včetně případného pouzdra. Ostatní tvarové rozměry zůstávají předpoklady.
Zachycená vačka řídí zdvih i návrat přes integrální PETG kluzný čep Ø6
na lůžku. Lůžko má dvě polohy pro tisk: levou polovinu vnější X stranou dolů (−90° Y), pravou vnější X stranou dolů (+90° Y), čep vzhůru. Dvojice klíčů a dva M3×45 / DIN439 spojují každé lůžko na stole. Otvory vodítek mají tisknutelné rohové úkosy; pracovní obálka sloupku 8×10 zůstává volná. Nevyhovující vůli řeší změna profilu/parametru a nový tisk;
montáž nepotřebuje pilník, pilku ani vrtačku. Brusný papír patří pouze
k odstranění povrchové úpravy v lepených stopách dřeva. Dole lůžko spočívá na pevných
podpěrách; firmware vypne impulzy serva. Pro přepravu každé prázdné lůžko
samostatně zachytí výrazný pásek široký 8 mm. Bez snímače se odjištění ověřuje ručně.
Stop nepředstírá návrat dolů a vyžaduje kontrolu před zotavením.

ESP a kamera jsou vzadu uprostřed, mikrofon vpředu uprostřed a reproduktory
po stranách. Aktivní jsou jen díly skutečně používané v potvrzeném zapojení;
rezervní nakoupené kusy zůstávají v inventáři mimo kufr. Každá deska má
pevné dorazy a pásky, nosiče vlastní spojení s rámem. Powerbanku lze po
odpojení USB a uvolnění krytu a dvou pásků vyjmout nahoru.
Pasivní USB-A zásuvka a kabel pro napájení ESP jsou uvedené k doplnění
v kabelovém plánu; jejich dostupnost není potvrzeným nákupem. Zadání
výslovně dovoluje tyto pasivní montážní díly; podklad je ve specifikaci.

## Ovládání vieweru

Přepínač režimu obnovuje příslušné vrstvy a polohy. Strom dovoluje skrýt,
izolovat a přiblížit díl; průhlednost a kóty jsou pouze zobrazení.
Pohledy: celek, shora, zepředu a zboku. Tažení otáčí, kolečko přibližuje.
Držení Shift nebo tlačítko Inspect zvýrazní povrch; klik připne kartu,
Esc nebo klik do prázdna výběr zruší. Díly lze volit ve stromu nebo kartě;
piny, vodiče, svazky a celé sítě v kabelovém panelu. Vedení používá data jednotlivých
vodičů z kabelového plánu, nikoli náhradní spojnice středů desek.

Každý ze šesti zdvihů lze prohlédnout zvlášť. Hlavní a krátká sekvence
používají společná data `effects.json`; čas jízdy ve vieweru je ilustrativní,
v reálném firmware jej určí kalibrace. Stop zmrazí neznámou mezilehlou
polohu; reset vyžaduje servisní režim. Generátor předává všechny fáze
sekvence a odmítá nepodporované pořadí, kanály nebo souběhy. Závěrečný
klid trvá 250 ms s vypnutými výstupy. Zvuk prohlížeče je volitelný.
Svítí jen jeden LED úsek. Pevný limit celé prezentace je 15 s; při jeho
dosažení se sekvence zastaví s požadavkem na kontrolu a servisní reset.
Montážní režim ukazuje kumulativní stav s krokem, materiálem, nástroji a
kontrolou. Přeprava odstraní banku, hůlky a textil a ukáže šest pásků i
odpojený parkovaný USB konektor. Přepnutí režimu ovládá pouze model.

## Reprodukovatelnost

Zdroje CAD a prohlížeče jsou v této složce. `parameters.json` řídí rozměry,
`harness.py` jednotlivé vodiče, `assembly-guide.json` postup a `effects.json`
choreografii. Exportovaný plášť je referenční STEP; nemá žádné STL k tisku.
STL jsou orientované pro tisk, sestavy STEP v souřadnicích celého kufru.
X doprava, Y k pantům, Z nahoru. XY nula je přední levý roh volného
horního obdélníku mezi výztuhami; přední stěna je pod hranou o 15 mm
dál v záporném Y. Z=0 je vnitřní rovina dna.

Použity jsou Python 3.11, CadQuery 2.8.0, OCP 7.9.3.1.1, VTK 9.6.2,
Node.js a závislosti z `requirements.txt`. OCP a VTK jsou připnuté také
samostatně, aby instalace zachovala ověřenou verzi CAD jádra a renderování.
Rozměrové vstupy jsou textové; regenerace nevyžaduje referenční fotografie.
Z této složky na Windows:

```powershell
.\Regenerate.ps1 -Python ..\.venv\Scripts\python.exe
```

Python testy sdílejí jeden proces a memoizovanou CAD geometrii. Generování,
ověření a balení mají vlastní čisté procesy: triangulace při renderování
nesmí ovlivnit referenční geometrii ověřovacích kontrol. Každý krok vypisuje
své trvání. Opakovaný běh použije hotový ověřený balíček pouze při shodě
všech zdrojových otisků, Pythonu/OS a instalovaných závislostí a SHA-256
všech exportů, ověřovacího reportu, ZIPu a jeho kontrolního součtu.
Identita zahrnuje také dostupný PrusaSlicer, jeho spouštěč a nativní
provenienci. Před použitím cache se znovu ověří místní G-code, účinné
profily a logy všech dílů. Tyto auditní soubory zůstávají mimo výrobní ZIP;
obnova cache nesmí přeskočit kontrolu skutečného řezání bez podpor.
Chybějící, změněný nebo neúplný výstup vyvolá plnou regeneraci.
Firmware choreografie a JavaScriptové testy se kontrolují při každém běhu.
V CI se ověřený výsledek obnovuje z neměnného artefaktu úspěšného běhu
na `main`; původ a všechny bytes se kontrolují před použitím. Oddělené PR
artefakty se mohou použít ve stejném PR po ověření skutečného stromu
úspěšného běhu. Po potvrzeném merge může `main` tentýž výsledek převzít
pouze se shodou celého spustitelného stromu, přesných vstupů a bytes; provenience
zachová původní ověření. Cache není součástí výrobního ZIPu.

Pro vynucení všech testů a generování použít `-Force` (přímo v Pythonu
`python regenerate.py --force`). Současné regenerace ve stejné složce
`exports/` nejsou povolené; po násilně ukončeném procesu odstranit
`exports/.regeneration-lock` až po ověření, že proces už neběží.

## Inkrementální CAD a CI

CAD je oddělen od automatického a povinného CI/CD. Lokální rychlý postup
`scripts/cad.ps1 build` / `publish` a ruční generation-only fallback popisuje
[CAD workflow](CAD-WORKFLOW.md). Běžné softwarové CI ponechává cílené viewer
testy; jeho úspěch není mechanická akceptace. Výrobní kontroly zůstávají
výslovnou volbou `scripts/cad.ps1 verify --manufacturing`.
Lokální inkrementální regenerace a bezpečnostní i provenienční kontroly
zůstávají povinné při mechanických změnách. Politiku vstupů nadále sdílí
`source_inputs.py` a `scripts/ci/cad_impact.py`.

Lokální dopad lze zjistit bez CadQuery, sliceru i renderování z kořene:

```text
python scripts/ci/cad_impact.py --base HEAD
python -m unittest discover -s scripts/ci -p test_cad_impact.py
node --test mechanika/final/test_viewer.cjs
python -m unittest discover -s mechanika/final -p test_refresh_package.py
```

Při skutečné CAD změně se z důvěryhodného předchozího výsledku mohou převzít
jednotlivé nezměněné STL/STEP/surové 3MF. Jejich cache v
`exports/.part-cache/` váže obsah BREP, vlastníka dílu a jeho importované
zdroje, parametry, exportér a skutečný runtime na přesné bytes exportu.
Poškození nebo chybějící soubor znamená nové generování. Živé tvary, sestavy,
tisk a globální kolizní/montážní kontroly se při mechanické změně stále
ověřují společně; cache dílu sama není mechanická akceptace.
Změna samotného `verify.py` nebo testů přebírá nepozměněný generovaný snapshot,
odstraní staré potvrzení a ZIP a spustí nové testy a verifikaci. Geometrie ani
rendery se při této cestě negenerují. Neznámý mix závislostí použije plnou cestu.

Pro lokální inkrementální běh použít `Regenerate.ps1`. Parametr `-Force`
obchází i cache dílů a vyžaduje výslovné zadání nebo zdůvodnění závislosti.
Při podezření na stale výstup zkontrolovat zdrojové identity a provenienci;
neopravovat hash ručně. Neznámou závislost doplnit do politiky/fingerprintu
a regresního testu, potom provést odpovídající lokální regeneraci.

V samostatně vybaleném ZIPu vytvořit prostředí a instalovat
`requirements.txt`, potom předat cestu jeho Pythonu.
V repozitáři se navíc kontroluje shoda generovaného firmware souboru
`PresentationScore.h`; samostatný ZIP tuto kontrolu vynechá, pokud adresář
firmware není přítomný. Balíček obsahuje mechanické podklady a sdílená data
efektů; jeho regenerace není ověřením sestavení firmware. Generování nejprve
vytvoří CAD, viewer a výkresy a sváže jejich obsah se zdroji v
`generation-snapshot.json`. Ověření kontroluje zdrojové otisky, oba podklady
Inspect, geometrii, STEP/STL, mechanismy, montážní a kabelová data a zapíše
otisky všech exportů. Balení porovná každý soubor s tímto ověřeným záznamem;
změněný obrázek, CSV nebo viewer odmítne stejně jako CAD.
Starší ZIP/STL nejsou součástí F2. Samostatné `preview.py --viewer-only`
nepotvrzuje nové ověření ani neobnovuje výrobní balíček.
Panel **DV02** ve vieweru zobrazuje zdrojově svázaný plán a read-only import
core stop s replay/diff; postup a nové lokální testy jsou ve
[VIEWER.md](VIEWER.md#dv02-timeline-a-core-replay) a [DV02](../../docs/dv02.md).

Pro spolehlivé otevření používat místní HTTP server v `exports/`, například
`python -m http.server 8915 --bind 127.0.0.1`, a adresu
`http://127.0.0.1:8915/viewer.html`. Žádná data se tím neposílají mimo počítač.
