# Montáž a fyzická přejímka F2

Návod používat s [materiálem](mechanicky-material.csv),
[kabeláží](KABELAZ.md), [řezným seznamem](exports/cable-cut-list.csv)
a montážním režimem vieweru. Dokud nejsou potvrzené níže uvedené vstupy,
tisknout měrku a jeden zkušební modul. Vrtají se pouze nové profily a tisk,
nikdy skutečný kufr. Návrh není fyzicky přijatý.

## Příprava

Délku hůlky do 200 mm a šířku hlavice 38 mm včetně případného pouzdra
majitel potvrdil 10. 10. 2026. Ověřit zbývající výškové/tvarové rozměry
a usazení, osu serva a páku, USB vidlice/zásuvky, Degson a doručenou
kameru s FPC. Nasucho vložit šablonu 400 × 240 mm nad
výztuhu vysokou 5 mm a ověřit celý chod víka. [PODKLADY.md](PODKLADY.md)
rozlišuje měření a odhady.
Překročenou obálku řešit parametry a regenerací. Šest pozic platí pro
navržené obálky; širší skutečné pouzdro může změnit rozhodnutí na pět.

PETG: STL již mají tiskovou orientaci. Použít vrstvu 0,2 mm, nejméně
4 perimetry, 5 plných horních i spodních vrstev a výplň přibližně 40 %.
Namáhaná vodítka a
vačky nesmějí mít nedotisky. Podpory podle skutečných převisů; vyčistit
vnitřní drážku. Měrkou ověřit otvory a vůle. Díly neopravovat změnou měřítka.

## Kotvy a rám

Čtyři hliníkové úhelníky 40 × 40 × 3 mm, každý dlouhý 50 mm, mají svislou
nosnou lepenou plochu 50 × 40 mm. Vodorovná ramena leží mezi Z = 5 a 8 mm,
rám mezi Z = 8 a 12 mm. Nad horní hranou výztuhy zbývá 3 mm.
Kotevní otvory jsou na souřadnicích X = 27 a 404,8 mm, Y = 30 a 243 mm;
přesnou vrtací šablonou je STEP. Odlehčovací díry pro okolní šrouby patří pouze
do nových úhelníků. Odjehlit všechny hrany v dosahu vodičů.

Lepidlo Bison Epoxy Metal 24 ml, výrobek 6305443: vymezit čtyři plochy, odstranit
lak na pevné suché dřevo, zdrsnit a vysát. Hliník zdrsnit a odmastit
acetonem mimo kufr; rozpouštědlem nenamáčet jeho lak a původní spoje.
Smíchat složky v poměru 1:1. Nominální vrstva silná 0,5 mm spotřebuje
celkem 4 ml směsi, navíc počítat s okraji a zkušebním vzorkem.
Ustavit přípravkem, který se nepřilepí, fixovat 10 h, při 20 °C nechat 24 h
vytvrdnout. Doporučená aplikace 10–35 °C podle
[listu výrobce](https://www.bison.net/getmedia/d54542f5-f56f-40b2-9406-3ad692c92a30/Print%20PDF.pdf).
Lepení trvale mění místní povrch. Dřevo s odlupujícími se vrstvami nebo
spoj na laku nepřijmout; dekorativní suchý zip není náhradou kotvy.

Čtyři PETG panely mají přeplátované spoje dlouhé 12 mm s M3 maticemi.
Nosné lišty jsou hliníkové profily L 20 × 20 × 3 mm, dlouhé 400 mm, ze
slitiny EN AW-6060 T66. Doložit mez kluzu alespoň 150 MPa.
Konce svislých ramen upravit dle CAD, vodorovná ramena zachovat až k M4
kotvám. Samotná PETG deska silná 4 mm bez lišt této konstrukci neodpovídá. Spodní matice
a nepřístupné šrouby osadit na stole před vložením rámu. Každý modul má
vlastní čtyři šrouby M3; jeho sejmutí neuvolní sousedy.

`strength.py` počítá s hmotností 5 kg a zrychlením 3 × g, tedy silou
147,15 N. Při nepříznivém nesení mohou zatížení nést jen dvě kotvy,
každá 73,58 N. Kritický plochý konec lišty má
vyložení 45,8 mm, I = 45 mm⁴, odhad napětí 112,32 MPa a průhybu 0,748 mm.
Poměr k 150 MPa je 1,34; není to součinitel bezpečnosti celé sestavy.
Odhad nezahrnuje lokální otvory, torzi, únavu, tečení PETG ani dřevěný kufr.
Celkovou upevněnou hmotnost 5 kg bez přepočtu nezvyšovat.
Průměrné smykové napětí lepené plochy je 0,0368 MPa. Při excentricitě 40 mm
je odhad normálového napětí včetně ohybu 0,2575 MPa.
Skutečné odlupování a soudržnost dřeva neznáme.
Katalogová pevnost epoxidu na hliníku není důkazem pevnosti tohoto spoje.

## Zkušební mechanismus

Servo má tvarovou kolébku a zajištění proti nadzvednutí. Původní páku dlouhou 35 mm
připojit dvěma M2 do drážek vačky. Středový šroubek musí odpovídat hřídeli
konkrétního serva; jeho provedení není doložené. Vodicí osy zachytit spodními
hlavami a horními podložkami/maticemi podle modelu před upevněním modulu.
Pracovní část vodítka musí být hladká; plný závit nepovažovat za rovnocennou
náhradu bez zkoušky tření. M3 čep kladky svírá mosaznou trubičku; nylonový
váleček se otáčí volně. Kovové matice zajistit proti povolení.

Zdvih 16 mm, úhel 0–160°, krajní 20° úseky kruhové. Dole lůžko dosedá na
pevné podpěry a kalibrace nesmí dlouhodobě tlačit do dorazu. Nahoře kladka
zůstává zachycená. Přerušení uprostřed může způsobit pokles; lůžko ručně
podepřít a kontrolovat. Návrh neslibuje mezilehlou brzdu ani návrat bez proudu.

Moment v `exports/verification.json` vychází z CAD objemu lůžka,
PETG o hustotě 1,27 g/cm³, hůlky o hmotnosti 150 g, dalších 25 g a odhadu
tření ve výši 50 % tíhy. Maximální směrnice vačky určí moment a požadavek
s násobnou rezervou 1,5. Hmotnost i tření
jsou předpoklady. Katalogový moment při zastavení hřídele (stall) není
bezpečný provozní moment.
První kus musí zvednout zátěž bez zadrhnutí, nadproudu a přehřátí.

## Elektronika, vedení a textil

Desky přidržují obvodové dorazy a dva pásky, ne vymyšlené PCB otvory.
Pásky nesmějí mačkat drobné součástky, membránu ani mikrofonní otvor.
Izolovat pájecí strany. Měnič má volné větrání, reproduktory otevřenou
akustickou cestu nahoru. Kamera je vzadu uprostřed ve výměnném držáku,
mikrofon vpředu. Víko zůstává bez elektroniky.

Kabelový plán určuje každý vodič/pin/síť. Před střihem protáhnout vzorek.
Každý pohyblivý konec LED/U059 má vlastní rezervu, pevné odlehčení tahu
na obou stranách a spoje mimo ohyb. USB cívka má vlastní držák nezávislý
na bance. Odpojený konektor parkovat v klipu. SPK− není GND; reproduktorové
výstupy nespojovat spolu ani se zemí. Výkon nevede přes signálové PCB plošky.

Textil silný 0,8 mm a podložení silné 1,2 mm jsou snímatelné a nenosné. Založit kraje,
odstranit volná vlákna; nedat textil do dráhy vačky, vodítek, chlazení,
akustických otvorů ani přes štítky aretace. Boční LED lišta osvětluje
hůlku a stíní přímý pohled na čipy. Kryty mají vlastní mechanické upevnění.

## Fyzická přejímka

1. Po vytvrzení vyzkoušet spoj na stejném podkladu. Odlupující se dřevo,
   lak, praskání nebo trhlina znamenají nepřijatý spoj.
2. Se zachyceným rámem a postupně přidávanou náhradní zátěží zkusit 10 kg
   celkem, vždy po dobu 10 min: vodorovně, na boku jako při nesení za madlo a opatrně
   převráceně nad měkkou podložkou. Ohroženou elektroniku předem vyjmout.
   Změřit vůle a průhyb před/po. Nesmí zůstat posun, trhlina, povolená
   matice ani dotyk s výztuhou. Tato statická zkouška nedokazuje odolnost
   proti rázům o zrychlení 3 × g.
3. Jeden modul nejméně 100 pomalých zatížených cyklů, potom 20 efektových;
   zapsat proud, oteplení a koncové dosednutí. Opakovat na všech šesti
   pozicích, včetně sousedů a skutečných pohyblivých kabelů.
4. Test dolní polohy: po dosednutí FULL_OFF konkrétního PCA kanálu;
   porovnat proud, zvuk a mírný odpor páky s fyzicky odpojeným V+.
   Nepřetlačovat převody. Pokud servo dál aktivně drží, požadavek není
   splněn. Zelená kolébka odpojí celou větev; nový spínací modul se nepřidává.
5. Stop a výpadek proudu na začátku/uprostřed/nahoře. Výstupy mají zůstat
   vypnuté bez automatického návratu. Podepřít, prohlédnout a teprve pak
   potvrdit zotavení. Test bez návštěvníků a prstů v mechanismu.
6. Přeprava: obě kolébky vypnout, banku, hůlky a snímatelný textil vyjmout.
   Šest lůžek v dolní poloze zajistit výraznými pásky širokými 8 mm,
   USB zaparkovat. Zavřít bez tlaku víka.
   Krátce nést za madlo, opatrně naklánět, zkontrolovat. Potom běžná jízda
   s kufrem zajištěným v autě proti posunu. Znovu prohlédnout kotvy, spoje,
   kabely a zkusit zdvih. Žádná z těchto zkoušek zde nebyla provedena.
7. PETG ani dřevěný spoj nejsou přijaté pro rozpálené zaparkované auto;
   takové použití vyžaduje vlastní tepelnou zkoušku.

Přeprava s hůlkami/bankou uvnitř není přejímána. Před provozem odstranit
všech šest pásků a ručně zkontrolovat dráhu. Elektronika aretaci nedetekuje.
