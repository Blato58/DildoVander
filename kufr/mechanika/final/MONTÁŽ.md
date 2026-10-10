# Montáž a fyzická přejímka F2

Návod používat s [materiálem](mechanicky-material.csv),
[kabeláží](KABELAZ.md), [řezným seznamem](exports/cable-cut-list.csv)
a montážním režimem vieweru. Dokud nejsou potvrzené níže uvedené vstupy,
tisknout dvoudílnou měrku a jeden zkušební modul. Otvory, kapsy a kluzný
čep vznikají tiskem. Kufr, plastové díly ani kovové šrouby se nepilují,
neřežou a nevrtají. Návrh není fyzicky přijatý.

## Příprava a tisk

Délku hůlky do 200 mm a šířku hlavice 38 mm včetně případného pouzdra
majitel potvrdil 10. 10. 2026. Ověřit zbývající výškové/tvarové rozměry
a usazení, osu serva a páku, USB vidlice/zásuvky, Degson a doručenou
kameru s FPC. Nasucho vložit šablonu 400 × 240 mm nad výztuhu vysokou
5 mm a ověřit celý chod víka. [PODKLADY.md](PODKLADY.md) rozlišuje měření
a odhady. Překročenou obálku řešit parametry a regenerací.

PETG: STL již mají tiskovou orientaci. Použít vrstvu 0,2 mm, nejméně
4 perimetry a 5 plných horních i spodních vrstev. Čtyři podlahové kotvy,
`lift_base`, `wand_bed` a `dwell_cam` tisknout se 100% výplní, aby nominální
nosné průřezy nebyly nahrazené řídkou výplní. Ostatní díly přibližně 40 %.
Přemostění vnitřních kapes matic a epoxidových zámků nejprve zkontrolovat
v náhledu sliceru a na zkušebním výtisku; uvnitř těchto dutin nezavřít podpory.
Připravit 3 kg PETG. Součet plných CAD objemů všech 52 výtisků při hustotě
1,27 g/cm³ odpovídá přibližně 2,40 kg; samotné povinně plné díly přibližně
1,45 kg. Skutečnou spotřebu ostatních dílů, podpor, lemů a zkoušek určí
slicer. Původní 1kg položka pro tuto variantu nestačí.
Namáhaná vodítka, vačky, kluzný čep a střechy matic nesmějí mít nedotisky.
Lůžko je otočené +90° kolem Y; integrální čep Ø6 směřuje vzhůru. Kluzná
plocha čepu ani drážky nesmí mít kontaktní plochy podpor. Nejdříve ověřit
čep a odpovídající drážku dvoudílnou měrkou. Vůli řešit profilem nebo
parametrem a novým tiskem, nikdy pilníkem, vrtákem nebo měřítkem STL.
Brusný papír se smí použít jen k přípravě skutečných lepených stop dřeva.

## Rám mimo kufr a podlahové kotvy

Nejprve na stole spojit čtyři rámové PETG panely osmi M3 × 6 a osadit
všechny prázdné nosiče včetně jejich spodních šroubů a matic. Přední a
zadní příčná žebra jsou integrální součásti panelů. Spodní hlavy nosičů
mají v kotvách odlehčovací kapsy. Nosiče se v kufru odspodu nemontují.
Moduly v této fázi nejsou připojené.

Čtyři tištěné kotvy `floor_anchor_front_left`, `floor_anchor_front_right`,
`floor_anchor_rear_left` a `floor_anchor_rear_right` podpírají rám a
modulové úchyty přímo z podlahy. Kotvy leží mezi Z0,5 a 8 mm, rám mezi
Z8 a 12 mm. Kotvy se lepí na holé dřevo DNA, nikoli na boční stěny.
Nasucho je ustavit podle sestaveného rámu a zkontrolovat skutečnou polohu
výztuhy; model předpokládá její rozsah Y115–175 mm.

Lepidlo Bison Epoxy Metal 24 ml, výrobek 6305443: označit skutečné lepené
stopy kotev, pouze v nich odstranit lak brusným papírem na pevné suché
dřevo a vysát prach. Nezalepit kapsy matic, boční vstupy ani šroubové
otvory; použít odstranitelnou ochranu před nanášením. Tištěné průchozí
zámky mají krček Ø6 a rozšířenou hlavu Ø9; mezi epoxidem a kapsami matic
musí zůstat nejméně 2 mm plastu. Vytvrzený zámek mechanicky zachytí kotvu;
chemická přilnavost k PETG se nepovažuje za doloženou nosnou cestu.

Smíchat složky v poměru 1:1. Pastu dodanou stěrkou nejprve napěchovat
do komor z otevřené SPODNÍ strany každé kotvy. Kotvu potom usadit na
mokrý spodní film silný 0,5 mm. Dva horní otvory Ø2,5 mm každého zámku
slouží odvzdušnění a vizuálnímu potvrzení naplnění; nepokoušet se pastu
tlakem vstřikovat skrz tyto otvory. Mokré přebytky otřít před vytvrzením;
vytvrzené přebytky se nemají odstraňovat opracováním konstrukce.
Rám při ustavení nepřilepit. Fixovat 10 h, při 20 °C nechat 24 h vytvrdnout.
Výrobce uvádí práci při 10–35 °C a přibližně 45 minut zpracovatelnosti
při 20 °C v [technickém listu](https://www.bison.net/getmedia/d54542f5-f56f-40b2-9406-3ad692c92a30/Print%20PDF.pdf).
Nenamáčet lak ani původní spoje kufru rozpouštědlem.

Geometrické metriky čtyř kotev: přední levá/pravá mají každá stopu
8626,650 mm² a 20/22 zámků; zadní levá/pravá každá 9789,0125 mm² a 21/22
zámků. Celkem jde o 36 831,325 mm² holého dřeva a 85 zámků. Spodní film
0,5 mm má 18,416 ml, zámky přidají 18,542 ml: CAD minimum je 36,958 ml
bez ztrát. Dvě balení po 24 ml dávají 48 ml; rozdíl přibližně 11,042 ml
je rezerva pro zkušební vzorek, přebytky a ztráty při míchání/nanášení.
Původní výpočet 4 × 2000 mm² / 4 ml patřil odstraněnému návrhu a neplatí.
Dřevo s odlupujícími se vrstvami nebo spoj na laku nepřijmout. Katalogových
22 MPa platí pro hliník, nikoli dřevo tohoto kufru nebo tištěný PETG.

Po přejímce dřeva, epoxidových zámků a tištěných kotev zasunout ZBOKU
24 standardních matic M3 a 4 matice M4 před vložením rámu. M3 jsou
v Z2,6–5 mm a M4 v Z1,8–5 mm, pod souvislou 3mm střechou mezi Z5 a 8 mm.
Matice nejsou volné ani v kapsách otevřených shora. Potom vložit rám s
nosiči a upevnit čtyřmi standardními M4 × 10 ISO4762: hlava Ø7 × 4 mm,
imbus 3 mm, hlava zapuštěná 0,5 mm. Nepřitahovat deformovaný rám silou.

`strength.py` vyhodnocuje nynější podpíraná PETG žebra, lepené plochy,
odlité zámky a střechy matic. Starý odhad samostatné kovové výztuhy se na
tento návrh nepřenáší. Návrhová hmotnost je 5 kg, zrychlení 3 × g; finální
geometrický výpočet a fyzická přejímka zůstávají otevřené. Výpočet nemůže
prokázat skutečné odlupování, soudržnost dřeva ani tečení PETG.

## Zkušební mechanismus až po instalaci rámu

Každý modul sestavit samostatně na stole až po upevnění rámu. Nejdříve
sestavit základnu s vodicími šrouby; lůžko zatím ponechat MIMO vodítka.
Servo má tvarovou kolébku a zajištění proti nadzvednutí. Původní páku dlouhou 35 mm připojit dvěma M2
do drážek vačky. Středový šroubek musí odpovídat hřídeli konkrétního serva;
jeho provedení není doložené. Skutečné otvory původní páky musejí přijmout
M2 bez vrtání. Pokud nevyhoví, změnit tištěnou spojku nebo použít vhodnou
standardní neupravenou páku před finálním tiskem. Vodicí osy nejprve
zachytit spodními hlavami; horní podložky/matice osadit až po nasazení lůžka. Standardní M4 × 100
použít bez zkracování a obrábění; pracovní část vedení musí být hladká.
Plný závit nepovažovat za rovnocennou náhradu bez zkoušky tření.

Integrální PETG čep Ø6 je součástí lůžka a klouže v drážce vačky bez
samostatného čepu, pouzdra, podložky nebo matice. Vačku s připojenou
pákou a servem nasunout AXIÁLNĚ na čep lůžka, které ještě není na vodítkách.
Lůžko, vačku a servo potom společně spustit na vodítka a usadit servo do
kolébky. Nasucho bez napájení zkontrolovat ruční pohyb, přitom servo
přidržovat RUKOU. Trvalé pásky serva ani horní podložky a matice zatím
neosazovat. Čep nelze vypáčit stranou při lůžku zachyceném na vodítkách.

Po zkušebním pohybu celou zachycenou podsestavu lůžko/vačka/servo opět
sejmout z vodítek. Odkrytou základnu přišroubovat čtyřmi M3 × 14 do matic
již zachycených v podlahových kotvách; s nasazeným lůžkem tyto šrouby
nejsou přístupné. Teprve potom podsestavu vrátit na vodítka, usadit servo,
zajistit dvěma trvalými pásky a osadit horní podložky/matice. Celkem jde
o 12 trvalých pásků serv pro šest modulů; žádné další při zkoušce nejsou
nutné. Pokud se použije dočasný pásek, po odstřižení potřebuje náhradu
ze zásoby. Skutečné nasazení a přístup potvrdí prototyp. Sejmutí jednoho
modulu neuvolní sousedy. Používají se jen standardní neupravené kovové díly.

Zdvih je 16 mm, úhel 0–160°, krajní 20° úseky kruhové. Dole lůžko dosedá
na pevné podpěry a kalibrace nesmí dlouhodobě tlačit do dorazu. Nahoře čep
zůstává zachycený. Přerušení uprostřed může způsobit pokles; lůžko ručně
podepřít a kontrolovat. Návrh neslibuje mezilehlou brzdu ani návrat bez proudu.

Moment v novém `exports/verification.json` musí vycházet z aktuálního CAD
objemu lůžka a předpokladů hmotnosti/tření kluzného čepu. Pro předpokládané
μ = 0,4 vychází 0,11282 N·m a s násobkem 1,5 požadavek 0,16923 N·m;
maximální tlakový úhel je 41,22°. Tato čísla nedokládají zaručený moment
serva, bezpečné namáhání PETG ani skutečné tření. Starší výsledek se nepoužije. Hustota PETG 1,27 g/cm³, hůlka 150 g i další zatížení jsou
návrhové vstupy; katalogový stall moment není bezpečný provozní moment.
První kus musí zvednout zátěž bez zadrhnutí, nadproudu a přehřátí.

## Servis modulu a rámu

Vypnout napájení, odpojit banku a pohyblivé vodiče serva/LED/U059.
Pro přístup ke čtyřem šroubům základny odstranit horní matice a podložky
vodítek a odstřihnout dva pásky serva. Lůžko, zachycenou vačku a servo
zvedat společně z vodítek, potom získat přístup ke čtyřem M3 × 14 základny.
Teprve mimo vodítka lze vačku axiálně stáhnout z čepu. Při opětovné
montáži použít dva NOVÉ pásky ze zásoby a opakovat ruční kontrolu pohybu.
Nikdy nepáčit čep stranou ani nesprávnou vůli upravovat pilníkem/vrtákem.
Jde o navržené pořadí; fyzický přístup, skutečná páka a kabely potřebují přejímku.

Pro vyjmutí celého rámu odpojit elektroniku/kabely, samostatně odstranit
moduly a uvolnit čtyři M4 × 10. Čtyři lepené kotvy zůstávají na dně.
Kryty a powerbanku lze běžně vyjmout vlastními spoji bez této demontáže.

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
   lak, praskání nebo trhlina znamenají nepřijatý spoj. Ověřit naplnění
   zámků i jejich mechanické zachycení a střechy matic ještě před vložením rámu.
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
