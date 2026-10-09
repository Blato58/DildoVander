# Tisknutelná konstrukce kufru – revize 8. 10. 2026

Šest pevných rekvizit, dva reproduktory a společný držák ESP32-S3 s kamerou.
Fotografie majitele uvádí hůlku **19–20 cm**. Model počítá s délkou 200 mm,
dříkem do 16 mm a ozdobnou hlavicí do **40 mm**. Průměry jsou rezervy podle
fotografie, **ne změřené rozměry**. Hlavice má širší přední část lůžka;
není třeba tisknout kopii ornamentu.

**Doručený kufr – potvrzení majitele 9. 10. 2026:** vnitřní šířka je
17″ (431,8 mm), délka 10,5″ (266,7 mm) a výška spodní části od dna
po horní okraj 4,5″ (114,3 mm). Zaokrouhlená obálka spodní části je tedy
**432 × 267 × 114 mm (šířka × délka × výška)**. Kufr se bude prezentovat
otevřený; veškerá mechanika a elektronika patří do spodní části a víko
zůstane prázdné. Prostor víka se do zástavby nepočítá.
Samostatný záznam: [rozměry doručeného kufru](rozmery-doruceneho-kufru.md).
Rozměry kufru i součástek doplněné majitelem a katalogové zdroje jsou
pohromadě v [přehledu rozměrů](rozmery-nakoupenych-soucastek.md).

Současný CAD a `parametry.json` stále používají původní pracovní prostor
**310 × 450 × 200 mm (X × Y × Z)** a novým rozměrům nejsou přizpůsobené.
Před tiskem upravit rozložení a znovu ověřit obálky i pohyb. Fotografie
ukazují příčnou výztuhu na dně; majitel doplnil tloušťku **5 mm** a její
další údaje nepožaduje pro rozvržení. Potom vytisknout měrky a jednu přihrádku. Hotový CAD
nedokazuje usazení do skutečné truhly ani bezpečný pohyb pod zatížením.

## Soubory pro tisk a stavbu

Po regeneraci jsou v `output/`:

- `kufr-mechanika-prototyp.zip`: kompletní STL, STEP, návod, parametry a zdroj.
- `stl/`: každý soubor je jeden pevný díl, jednotky mm.
- `single-compartment.step` a `case-assembly.step`: přesná sestava.
- `nahled.html`: offline otočný náhled, posuvník skutečně otáčí lůžko, páku i táhlo.
- `nahled.png` a `rozlozeni.png`: celkový model, detail a plán rozmístění.
- `tiskovy-kusovnik.csv`: počty pro celý kufr; `prvni-prihradka.csv`: první modul.
- `montazni-material.csv`: výchozí šrouby a další netištěný materiál.
- `verification.json` a `stl-verification.json`: výsledky konkrétního exportu.

Výstupy jsou lokální a ignorované Gitem. Trvalý zdroj je `model.py`,
`electronics.py`, `preview.py` a `parametry.json`; stejný zdroj je uvnitř ZIP.

## Jednoduchý mechanismus

Hůlka leží v otevřeném lůžku a zůstává zajištěná dvěma pružnými pásky.
**Zadní konec lůžka se otáčí na jednom M4 čepu.** Servo přes původní páku,
tištěný adaptér a krátké táhlo nadzvedne přední konec. Při návratu stejné
táhlo vrací lůžko dolů. Nejsou potřeba dlouhé vodicí tyče, ložiska ani ozubené
rozvody. Majitel doplnil dvě páky: **35 mm oboustranná s upevněním a osou
uprostřed** a **20 mm jednostranná s upevněním a osou na konci**. Jde
o páky serva, nikoli montážní uši jeho těla nebo rozteče otvorů.
Další rozměry páky nejsou podmínkou rozvržení; při fyzické kalibraci
se ověří skutečný pohyb každého serva a jeho pulzy.

Při výchozích parametrech: lůžko 240 mm, dvě 120mm poloviny, šířka dříku
28 mm, přední 70mm část široká 46 mm. Rozteč šesti modulů je 48 mm.
Přední hrana se zvedne **30,7 mm**, sklon lůžka je **7,74°**; táhlo má
osovou délku **33,91 mm**. To jsou geometrické výsledky, nikoli naměřený
zdvih serva. Hlavice směřuje k návštěvníkovi, tenký konec k zadnímu čepu.

Spodní poloha dosedá na tištěnou podpěru. Čep používá seřiditelné tření:
M4 šroub, podložky, pružnou podložku a pojistnou matici. Tištěné 1mm
`hinge_spacer` určují mezeru; po zkoušce lze použít stejně tlusté třecí
podložky vhodného materiálu. **Zadržení při výpadku proudu není ověřené.**
Tření, tečení PETG a moment serva se musí zkoušet v celém rozsahu; pokud
nedrží zatížené lůžko bez proudu, je třeba změnit zajištění. Není dovoleno
považovat servo převodovku nebo utaženou matici za ověřený bezpečnostní zámek.
Po Stop/chybě žádný automatický návrat; podmínky E5 a bezpečnostní větve
zůstávají v [implementačním plánu](../docs/implementacni-plan.md).

## Rozmístění elektroniky

Návštěvník stojí před kufrem (**Y−**). Otevřené police a držáky stojí vlastními
nohami na dně, netíží kryty PCB. Zadní část začíná za přihrádkami; pod levou
policí je rozvod, na ní powerbanka, na pravé PCA a dva zesilovače.

| Součást | Umístění a uchycení |
| --- | --- |
| UGREEN PB720 / 25188 | Potvrzená obálka 139 × 80 × 26,5 mm. Otevřená kolébka vlevo vzadu, dva uvolnitelné pásky, tlačítko a porty přístupné. Musí jít snadno vyjmout bez demontáže okolních dílů. |
| 2× Waveshare 14595, 8 Ω / 5 W | Dvě samostatné tištěné podpěry vzadu vlevo/vpravo, čela k návštěvníkovi. Obálka jednoho kusu 100 × 45 × 21 mm; další měření otvorů/přírub majitel pro rozvržení nepožaduje. |
| ESP32-S3 + OV5640 | Jedna horní zadní platforma; výměnný držák kamery míří přibližně 20° vzhůru. ESP leží přímo za kamerou, USB a FPC jsou přístupné. |
| ICS43434 | Přední prostřední výškově posuvný držák, akustický port k návštěvníkovi. Oddělit PCB tenkou pěnou; port nezakrýt. |
| PCA9685 | Obálka majitele 25 × 70 × 15 mm. Pravá univerzální deska se štěrbinami, místo pro kabely ke všem šesti servům a U059. |
| 2× zesilovač DFR0954 / MAX98357A | Dvě malé desky na pravé polici. Oba zesilovače jsou od 9. 10. potvrzené nákupy; fyzické osazení a propojení ještě vyžadují ověření. |
| Joy-IT SBC-Buck02, PD trigger, pojistka a svorky | Spodní servisní deska pod powerbankou. Katalogové obálky jsou ve společném přehledu; svorky jsou pořízené a čekají na doručení, rozvržení neblokují. |
| MCP23017, Pixel Shifter a drobná logika | Zadní univerzální servisní deska. Zachovat elektrické oddělení a přístup ke konektorům. |
| PTT, hlavní vypínač, PHYSICAL_ENABLE | PTT má podle majitele 14 × 14 × 35 mm. Odnímatelný přední boční panel; rozměr obálky není průměr otvoru. Další měření kolébek není podmínkou rozvržení. Dřevěný kufr se modelem neřeže. |
| 6× U059 a 6× 20cm RGB | Pro U059 používat obálku výrobce 32 × 24 × 8 mm podle rozhodnutí majitele. U059 v kleci pod lůžkem, RGB na jeho dně pod rekvizitou. Vést pružnou smyčku mimo čep, páku a táhlo. |

UGREEN má majitelem potvrzené rozměry **139 × 80 × 26,5 mm**, shodně
s [výrobcem](https://eu.ugreen.com/en-fi/products/25188). Při úpravě CAD
používat tuto obálku a samostatnou montážní vůli. Stávající model dosud
používá starší větší obálku 139 × 80,8 × 26,7 mm a boční vůli 1,2 mm;
není tímto dokumentačním zápisem přegenerovaný. Nové rozložení musí
umožnit jednoduché vyjmutí powerbanky: volná cesta ven, přístupné porty
a uvolnitelné pásky bez demontáže okolních dílů.
100 W je maximální výkon powerbanky, nikoli limit nebo záruka 5V rozvodu kufru.
Elektrický návrh se řídí [napájením](../docs/efekty-a-napajeni.md).

Kamera a ESP jsou na společném držáku, plánované oblasti konektorů jsou
asi 23 mm od sebe. **Délka/orientace originálního FPC není známá**:
tento údaj není potvrzení jeho vejití. Použít originální spárovaný FPC,
bez náhodného prodlužování, a upravit polohu desky podle skutečných konektorů.
Sklon kamery změnit podle výšky stolu a návštěvníka. Rozměry víka nejsou
podmínkou rozvržení: víko zůstává prázdné. Kamera potřebuje volný výhled
přes lůžka; zorné pole se ověří na sestavě.
Podrobnosti varianty kamery jsou v [dokumentaci](../docs/kamera.md).

Mikrofon je přibližně 32 cm od obou reproduktorů. Jeho držák se musí
nastavit nad skutečný přední lem; rozestup sám nezaručuje potlačení ozvěny.
Reproduktory jsou na otevřených podpěrách, ne v navržených akustických
ozvučnicích. Poslech, vibrace a mikrofon vyzkoušet v sestaveném kufru.

## Nejprve jeden modul

1. Použij [společný přehled rozměrů](rozmery-nakoupenych-soucastek.md)
   a přizpůsob mu CAD. Před tiskem lůžka ověř hlavici a dřík hůlky.
   Vytiskni `fit_coupon` pro M3/M4
   a `head_fit_gauge`: otevřené výřezy zleva 32 / 36 / 40 / 44 mm.
2. Vytiskni díly podle `prvni-prihradka.csv`. Výchozí materiál PETG,
   tryska 0,4 mm, vrstva 0,2 mm, 4 obvody, 35–50 % výplně. Pevnost je
   třeba ověřit; samotné nastavení sliceru ji nezaručuje.
3. Dvě základny spoj jedním `base_splice`, dvě poloviny lůžka jedním
   `tray_splice`. **Lůžko používá čtyři M3 zápustné šrouby DIN 965 s hlavou
   nejvýše Ø6 mm**, zarovnané s podlahou. Osadit před LED páskem; běžné
   válcové hlavy by zasahovaly do něj. Spodní matice musí zůstat přístupné.
4. `base_floor_pad` patří pod konce základny; spodní vybrání schová hlavy
   šroubů a nechá prostor pod spojkou. Upevnění ke dnu kufru vol podle
   skutečné tloušťky a materiálu, nenaslepo dlouhými vruty.
5. Namontuj `hinge_support`, M4 čep, dva 1mm mezikroužky a zadní část lůžka.
   `lower_rest` je pod širokým předním koncem. Vyzkoušej ruční vyklopení.
6. Servo vlož do `servo_strap_cradle`, zajisti pásky a přišroubuj přes
   **vnější montážní uši**. Tištěný adaptér se šroubuje k původní páce;
   rozteč M2 otvorů 6/12 mm patří pouze ke stávajícímu pracovnímu CAD,
   není potvrzenou roztečí páky ani podmínkou rozvržení. Oboustranná
   35mm páka se upevňuje uprostřed, jednostranná 20mm na konci;
   adaptér se při úpravě CAD přizpůsobí zvolenému provedení.
7. Táhlo má dva M3 čepy a na obou `link_spacer_3mm`. Hlavy čepových
   šroubů jsou na vnější straně táhla; vnitřní matice potřebují prostor
   mimo tělo serva. Výchozí délka M3×14, delší závit nesmí zasáhnout servo.
   Zde použij **běžnou M3 matici tloušťky nejvýše 2,4 mm**, bez další
   podložky v této osové sestavě; vyšší samojistná matice se nevejde.
   Nastav volný pohyb a závit zajisti rozebíratelným prostředkem vhodným
   pro kovové spojení, který se nedostane na plast. Matice nesmí sevřít
   táhlo. Ověř celý ruční rozsah se servem odpojeným.
8. U059 uchyť do klece pod lůžkem, sedla připevni po zkoušce pěnou/lepidlem
   nebo vhodným páskem. Založ hůlku, zadrž ji dvěma pružnými smyčkami.
   Kabely zajisti na obou koncích; první smyčka alespoň 40 mm je odhad,
   skutečný posuv/ohyb zkontroluj v celém rozsahu.
9. Zvaž celou pohyblivou část a proveď ruční i zatíženou zkoušku bez proudu
   ve spodní, mezilehlých i horní poloze. Teprve po ověření mechanického
   držení a elektrické bezpečnostní větve patří na řadu poháněný test.

Tištěné lůžko a základny lze položit plochým dnem; zadní závěs a otvory
potřebují lokální podpěry. Adaptér a táhlo tiskni širokou boční plochou,
mezery/spacery osou otvoru vzhůru. Police a horní platformu orientuj deskou
dolů, podpěry pod montážními patkami/PCB podložkami; věže reproduktorů
a mikrofonu nejlépe na zadní ploše. STL jsou posunuté nad Z=0, **ne automaticky
natočené pro tisk**. Náhled ukazuje montážní orientaci.

## Regenerace a kontroly

V `kufr/mechanika/`, Python 3.11+:

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m unittest test_model -v
.\.venv\Scripts\python.exe model.py
.\.venv\Scripts\python.exe verify_exports.py
```

Kontroly: uzavření táhla ve 101 polohách, jeho zpětný návrat, 21 vzorků
průniků pohyblivých dílů i vybraných obálek šroubů/součástek, oddělení
elektroniky od pohybu, jediný validní solid každého výtisku, obálka
220 × 220 × 250 mm, nominální prostor kufru v klidu i po zdvihu a skutečné
STL s uzavřenými hranami, kladným objemem a shodou s CAD do 2 %.

Tyto kontroly nezahrnují všechny šrouby, kabely, nejmenší ohyb FPC,
pevnost, teplotu, nepřetržitou dráhu mezi vzorky, akustiku, ani zadržení
při výpadku proudu. Podmínky HW akceptace se nemění. Rozvržení desek a
držáků je konkrétní; změna JSON není automatický návrh libovolné elektroniky.
Při jiné hlavici/servu/PCB uprav držák a znovu proveď celý export a zkoušku.
