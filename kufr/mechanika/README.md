# Tisknutelná konstrukce kufru – revize 8. 10. 2026

Šest pevných rekvizit, dva reproduktory a společný držák ESP32-S3 s kamerou.
Fotografie majitele uvádí hůlku **19–20 cm**. Model počítá s délkou 200 mm,
dříkem do 16 mm a ozdobnou hlavicí do **40 mm**. Průměry jsou rezervy podle
fotografie, **ne změřené rozměry**. Hlavice má širší přední část lůžka;
není třeba tisknout kopii ornamentu.

Pracovní vnitřní prostor je **310 × 450 × 200 mm (X × Y × Z)**.
Majitel dříve uvedl kufr 450 × 310 × 200 mm; vnitřní rozměr, výstelka,
panty a prostor pod víkem stále nejsou potvrzené. Nejdříve vytiskni měrky
a jednu přihrádku. Hotový CAD nedokazuje usazení do skutečné truhly ani
bezpečný pohyb pod zatížením.

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
rozvody. Pro každé servo se musí fyzicky ověřit osa, původní páka a pulzy.

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
| UGREEN PB720 / 25188 | Otevřená kolébka vlevo vzadu, dva pásky, tlačítko a porty přístupné. |
| 2× Waveshare 14595, 8 Ω / 5 W | Dvě samostatné tištěné podpěry vzadu vlevo/vpravo, čela k návštěvníkovi. Obálka jednoho kusu 100 × 45 × 21 mm; otvory a příruby nezměřené. |
| ESP32-S3 + OV5640 | Jedna horní zadní platforma; výměnný držák kamery míří přibližně 20° vzhůru. ESP leží přímo za kamerou, USB a FPC jsou přístupné. |
| ICS43434 | Přední prostřední výškově posuvný držák, akustický port k návštěvníkovi. Oddělit PCB tenkou pěnou; port nezakrýt. |
| PCA9685 | Pravá univerzální deska se štěrbinami, místo pro kabely ke všem šesti servům a U059. |
| 2× zesilovač DFR0954 / MAX98357A | Dvě malé desky na pravé polici. Historický seznam potvrzuje jeden koupený zesilovač; druhý musí být pro dva samostatné reproduktory potvrzený/osazený. |
| Joy-IT SBC-Buck02, PD trigger, pojistka a svorky | Spodní servisní deska pod powerbankou. Výška komponent včetně kabelů musí odpovídat volnému prostoru; nejsou to změřené tovární modely. |
| MCP23017, Pixel Shifter a drobná logika | Zadní univerzální servisní deska. Zachovat elektrické oddělení a přístup ke konektorům. |
| PTT, hlavní vypínač, PHYSICAL_ENABLE | Odnímatelný přední boční panel; otvory jsou pracovní a musí se porovnat s doručenými prvky. Dřevěný kufr se modelem neřeže. |
| 6× U059 a 6× 20cm RGB | U059 v kleci pod lůžkem, RGB na jeho dně pod rekvizitou. Vést pružnou smyčku mimo čep, páku a táhlo. |

UGREEN má podle [výrobce](https://eu.ugreen.com/en-fi/products/25188)
139 × 80 × 26,5 mm; [zvolená nabídka Alza](https://www.alza.cz/ugreen-20000mah-pd-100w-power-bank-d8021597.htm)
uvádí 139 × 80,8 × 26,7 mm. Model používá větší obálku a navíc boční vůli
1,2 mm. U kladného Y konce powerbanky zbývá jen 15,5 mm k nominální stěně:
ověřit skutečný konektor, použít vhodný úhlový kabel nebo kolébku posunout.
100 W je maximální výkon powerbanky, nikoli limit nebo záruka 5V rozvodu kufru.
Elektrický návrh se řídí [napájením](../docs/efekty-a-napajeni.md).

Kamera a ESP jsou na společném držáku, plánované oblasti konektorů jsou
asi 23 mm od sebe. **Délka/orientace originálního FPC není známá**:
tento údaj není potvrzení jeho vejití. Použít originální spárovaný FPC,
bez náhodného prodlužování, a upravit polohu desky podle skutečných konektorů.
Sklon kamery změnit podle výšky stolu a návštěvníka. Kufr nemá změřené víko
ani zorné pole; kamera potřebuje volný výhled přes lůžka a nezakryté víko.
Podrobnosti varianty kamery jsou v [dokumentaci](../docs/kamera.md).

Mikrofon je přibližně 32 cm od obou reproduktorů. Jeho držák se musí
nastavit nad skutečný přední lem; rozestup sám nezaručuje potlačení ozvěny.
Reproduktory jsou na otevřených podpěrách, ne v navržených akustických
ozvučnicích. Poslech, vibrace a mikrofon vyzkoušet v sestaveném kufru.

## Nejprve jeden modul

1. Změř vnitřek kufru, hlavici a dřík hůlky. Vytiskni `fit_coupon` pro M3/M4
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
   rozteč M2 otvorů 6/12 mm je pracovní a musí odpovídat doručené páce.
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
