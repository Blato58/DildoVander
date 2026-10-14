# Montáž a fyzická přejímka F2

Návod používat s [materiálem](mechanicky-material.csv),
[kabeláží](KABELAZ.md), [řezným seznamem](exports/cable-cut-list.csv)
a [úplným postupem](POSTUP.md) generovaným ze stejných dat jako montážní režim vieweru. Dokud nejsou potvrzené níže uvedené vstupy,
tisknout tři výrobní měrky a jeden zkušební modul. Otvory, kapsy a kluzný
čep vznikají tiskem. Kufr, plastové díly ani kovové šrouby se nepilují,
neřežou a nevrtají. Návrh není fyzicky přijatý.

## Příprava a tisk

Délku hůlky do 200 mm a šířku hlavice 38 mm včetně případného pouzdra
majitel potvrdil 10. 10. 2026. Ověřit zbývající výškové/tvarové rozměry
a usazení, osu serva a páku, USB vidlice/zásuvky, Degson a doručenou
kameru s FPC. Nasucho vložit šablonu 400 × 240 mm přes volný horní obdélník
430 × 269 mm nad středovou výztuhu 80 × 5 mm. Ověřit oba spodní oblouky
45 × 45 mm šablonou a tvarované kotvy; potom celý chod víka. [PODKLADY.md](PODKLADY.md) rozlišuje měření
a odhady. Překročenou obálku řešit parametry a regenerací.

Veškeré finální STL/3MF jsou již orientované na Z=0. Použít [TISK.md](TISK.md) a per-part profily z `exports/profiles/`; podpory NONE, raft 0. Pouze mikrofonní držák a dva úzké levé kazetové rámy mají doložený 3mm brim kvůli skutečnému varování Prusa o adhezi vysokého dílu. Ostatní díly brim 0. Konzervativní společný předpoklad: PETG, tryska 0,4 mm, vrstva 0,2 mm, prostor 220×220×250 mm; konkrétní domácí tiskárna není známá. Kotvy a díly zdvihu/měrky mají šest perimetrů a 100% výplň; ostatní čtyři perimetry/40% gyroid. Skutečnou spotřebu a čas uvádí slicování, 3kg položka je zásoba s rezervou pro fyzické zkoušky.

Nejprve vytisknout `fit_coupon`, `follower_groove_coupon`, `floor_anchor_coupon` a jeden kompletní dvoudílný modul. Obrácená podlahová měrka prověřuje skutečnou orientaci kotev, tunely matic, mosty a kuželové epoxidové zámky. Vložit/vyjmout skutečné matice a hlavy bez opracování. Neúspěch řešit profilem/parametrem a novým tiskem. Uvnitř dutin nepoužít podporu. Rohově zkosený otvor vodítka zachovává čelní vůli 0,45 mm a úkosy 0,6 mm nechávají minimální diagonální vůli 0,212 mm k obdélníkovému sloupku 8×10. Vybrat vůli podle měrky.

Každé lůžko spojit ze `wand_bed_left` a `wand_bed_right`: nasunout klíče, dvěma příčníky vést standardní M3×45 ISO7380 a vložit dvě tenké M3 DIN439 (1,8 mm). Hlavy jsou vpravo, matice vlevo; nepoužít vyšší DIN934. Teprve potom upevnit U059/LED a nasadit skupinu na vodítka. Integrální čep patří pravé polovině, další váleček ani kovový kolík se nepřidává.

Ve 40 M3 otvorech jsou dvoustupňové střechy: pilot 0,4 mm a uzávěr 0,4 mm, lokálně nejvýše 0,8 mm. Na stole je otevřít hladkým ručním šídlem a odstranit drobný úlomek; přesné polohy/počet uvádí výrobní manifest. Kapsy ani nosné dosedací plochy neupravovat. Vyžaduje-li spoj sílu, změnit profil a přetisknout. Standardní hlavy/nuty mají vlastní navržené vůle; šrouby se nezkracují.

Brusný papír se smí použít jen k přípravě skutečných lepených stop dřeva.

## Rám mimo kufr a podlahové kotvy

Nejprve na stole spojit čtyři rámové PETG panely osmi M3 × 6 a osadit
všechny pevné nosiče včetně jejich spodních šroubů a matic. Sedm matic bočních kazet vložit zespodu do rámu; levou kazetu upevnit třemi a pravou čtyřmi M3 × 8 shora. Kazety nejprve sestavit ze dvou bočních rámů a tří pater podle [výrobního návodu](TISK.md). Dvě pera u každé spojky určují polohu; vůle 0,25 mm není lisovaný spoj. Potom osadit desky a dokončit kabeláž na stole. Spodní PCB zapojit před horní policí; reproduktory až po utažení kazetových šroubů. Přední a
zadní příčná žebra jsou integrální součásti panelů. Spodní hlavy nosičů
mají v kotvách odlehčovací kapsy. Nosiče se v kufru odspodu nemontují.
Moduly v této fázi nejsou připojené.

Svěrné kazetové spoje dotáhnout bez USB v konečné trase. Potom na stole
bez banky a reproduktoru položit neporušený C–C kabel: oba konektory
odpojené, kabel ještě nesvinutý. Místní přední průchod vložit otevřeným
okrajem −Y a zadní +Y, pod spodním patrem použít otevřenou drážku.
Konektory neprotahovat malým otvorem. Cívku sestavit až nakonec; skutečný
ohyb a prostor pro prsty ověřit na dodaném kabelu. Pravý přední kryt má
malý zadní výřez lemu pro kazetové matice, horní deska a uchycení zůstávají celé.

Čtyři tištěné kotvy `floor_anchor_front_left`, `floor_anchor_front_right`,
`floor_anchor_rear_left` a `floor_anchor_rear_right` podpírají rám a
modulové úchyty přímo z podlahy. Zakřivený spodek kopíruje dno se svislou spárou 0,5 mm. Matice 24 M3 a 4 M4 vložit ZBOKU na stole PŘED lepením, protože oblouk po přilepení omezuje vstupy. Před lepením dočasnými šrouby přitáhnout matice k nosným střechám: v kapsách mají svislou vůli. Odstranit dočasné šrouby, chránit závity i otevřené spodní přístupy před epoxidem. Při lepení spáru na rovné části dna určit čtyřmi dočasnými měřenými 0,5mm PET podložkami pod přístupnými vnějšími okraji každé kotvy na rovném dně (přední kolem Y60, zadní kolem Y200). Otisky podložek po vytvrzení vyjmout; nesmějí zaplnit zámky ani kapsy. Kotvy leží mezi Z0,5 a 8 mm, rám mezi
Z8 a 12 mm. Kotvy se lepí na holé dřevo DNA, nikoli na boční stěny.
Nasucho je ustavit podle sestaveného rámu a zkontrolovat skutečnou polohu
výztuhy; potvrzený středový pás je Y94,5–174,5 mm.

Lepidlo Bison Epoxy Metal 24 ml, výrobek 6305443: označit skutečné lepené
stopy kotev, pouze v nich odstranit lak brusným papírem na pevné suché
dřevo a vysát prach. Nezalepit kapsy matic, boční vstupy ani šroubové
otvory; použít odstranitelnou ochranu před nanášením. Tištěné průchozí
zámky mají krček Ø7,5 a rozšířenou hlavu Ø10,5; mezi epoxidem a kapsami matic
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
10293,300 mm² a 27/30 zámků; zadní levá/pravá každá 11609,8625 mm² a 22/24
zámků. Celkem jde o 43 806,325 mm² projektovaných stop a 103 zámků. Zakřivený
film má svislou tloušťku 0,5 mm a objem 21,903 ml; zámky přidají 33,572 ml.
CAD minimum je 55,475 ml bez ztrát. Tři balení po 24 ml dávají 72 ml;
rozdíl přibližně 16,525 ml
je rezerva pro zkušební vzorek, přebytky a ztráty při míchání/nanášení.
Původní výpočet 4 × 2000 mm² / 4 ml patřil odstraněnému návrhu a neplatí.
Dřevo s odlupujícími se vrstvami nebo spoj na laku nepřijmout. Katalogových
22 MPa platí pro hliník, nikoli dřevo tohoto kufru nebo tištěný PETG.

Po přejímce dřeva, epoxidových zámků a tištěných kotev ověřit čistotu
závitů 24 matic M3 a 4 matic M4, které byly vložené PŘED lepením. M3 jsou
v Z2,6–5 mm a M4 v Z1,8–5 mm, pod souvislou 3mm střechou mezi Z5 a 8 mm.
Matice jsou zachycené proti vytržení, mají však svislou montážní vůli; jejich přístupové chodby jsou zespodu otevřené až k vlastnímu slepému dnu buňky. Před lepením musí být dočasně přitažené k nosné střeše a chráněné před epoxidem. Potom vložit rám s
nosiči a upevnit čtyřmi standardními M4 × 10 ISO4762: hlava Ø7 × 4 mm,
imbus 3 mm, hlava zapuštěná 0,5 mm. Nepřitahovat deformovaný rám silou.

`strength.py` vyhodnocuje nynější podpíraná PETG žebra, lepené plochy,
odlité zámky a střechy matic. Starý odhad samostatné kovové výztuhy se na
tento návrh nepřenáší. Návrhová hmotnost je 5 kg, zrychlení 3 × g a dvě
zatížené kotvy. Každá dostává současně normálovou i vodorovnou sílu
73,575 N s ramenem 170 mm; normálová síla se postupně umístí do každé
skutečné matice. Výpočet zahrnuje smíšený moment plochy a nejhorší
vodorovný směr, včetně šikmého. U zámků posuzuje jednotlivé síly, u dřeva
lineární rozdělení napětí v projektované XY stopě. Skutečné rozdělení trakce na oblouku není tímto zjednodušeným výpočtem potvrzené. Materiálové meze jsou
pouze uvedené návrhové předpoklady. Výpočet nemůže prokázat skutečné
odlupování, soudržnost dřeva, vady plnění ani tečení PETG; fyzická
přejímka zůstává otevřená.

## Zkušební mechanismus až po instalaci rámu

Holé základny a dlouhé vodicí šrouby připravit na stole PŘED vložením rámu. Dvanáct M4 × 100 vložit zespodu, osadit základny a ihned zajistit finálními horními podložkami/maticemi na Z95/95,8. Neotáčet rám za elektroniku. Zespodu se šrouby po vložení do kufru nedají instalovat. Lůžka zatím zůstávají mimo vedení.
Servo má tvarovou kolébku a zajištění proti nadzvednutí. Původní páku dlouhou 35 mm připojit dvěma M2
do drážek vačky. Středový šroubek musí odpovídat hřídeli konkrétního serva;
jeho provedení není doložené. Skutečné otvory původní páky musejí přijmout
M2 bez vrtání. Pokud nevyhoví, změnit tištěnou spojku nebo použít vhodnou
standardní neupravenou páku před finálním tiskem. Vodicí osy zachytit
spodními hlavami a horními podložkami/maticemi před přenosem rámu. Standardní M4 × 100
použít bez zkracování a obrábění; pracovní část vedení musí být hladká.
Plný závit nepovažovat za rovnocennou náhradu bez zkoušky tření.

Integrální PETG čep Ø6 je součástí lůžka a klouže v drážce vačky bez
samostatného čepu, pouzdra, podložky nebo matice. Vačku s připojenou
pákou a servem nasunout AXIÁLNĚ na čep lůžka, které ještě není na vodítkách.
Horní skupina zatím zůstává mimo kufr. Až po následujícím vložení a
utažení rámu ji společně spustit na vodítka a usadit servo do kolébky.
Nasucho bez napájení zkontrolovat ruční pohyb, přitom servo přidržovat
rukou; poté ihned obnovit horní podložky/matice a trvalé pásky serva.
Čep nelze vypáčit stranou při lůžku zachyceném na vodítkách.

Vložit předzapojenou vložku s holými základnami, banku nechat mimo kufr.
Reproduktory a pravou kazetu dočasně odpojit a odložit pro přístup ke
čtyřem M4 kotvám. Utáhnout 4 × M4 × 10 a všech 24 × M3 × 14 základny,
ještě před nasazením lůžek. Reproduktory a pravou kazetu vrátit a připojit.
Finální podložky/matice po dobu přenosu drží vodicí šrouby; jednotlivou
dvojici sejmout shora až bezprostředně před nasazením její horní skupiny.
Lůžko, vačku, servo, LED a U059 předem sestavit a zapojit na stole;
spouštět společně svisle po vedení. Servo zajistit dvěma trvalými pásky,
vrátit horní podložky/matice a připojit označené vývody. Každý další
modul montovat až po kontrole pohybu předchozího. Základny ani nosiče
se neopakovaně nerozebírají pro instalaci elektroniky.

Zdvih je 16 mm, úhel 0–160°, krajní 20° úseky kruhové. Dole lůžko dosedá
na pevné podpěry a kalibrace nesmí dlouhodobě tlačit do dorazu. Nahoře čep
zůstává zachycený. Přerušení uprostřed může způsobit pokles; lůžko ručně
podepřít a kontrolovat. Návrh neslibuje mezilehlou brzdu ani návrat bez proudu.

Moment v novém `exports/verification.json` musí vycházet z aktuálního CAD
objemu lůžka a předpokladů hmotnosti/tření kluzného čepu. Pro předpokládané
μ = 0,4 vychází přibližně 0,12794 N·m a s násobkem 1,5 zkušební cíl 0,19191 N·m;
maximální tlakový úhel je 41,22°. Tato čísla nedokládají zaručený moment
serva, bezpečné namáhání PETG ani skutečné tření. Starší výsledek se nepoužije. Hustota PETG 1,27 g/cm³, hůlka 150 g i další zatížení jsou
návrhové vstupy; katalogový stall moment není bezpečný provozní moment.
První kus musí zvednout zátěž bez zadrhnutí, nadproudu a přehřátí.

## Servis modulu a rámu

Vypnout napájení, odpojit banku a pohyblivé vodiče serva/LED/U059.
Vyjmout hůlku a volnou měkkou vložku příslušného lůžka.
Pro přístup ke čtyřem šroubům základny odstranit horní matice a podložky
vodítek a odstřihnout dva pásky serva. Lůžko, zachycenou vačku a servo
zvedat společně z vodítek, potom získat přístup ke čtyřem M3 × 14 základny.
Teprve mimo vodítka lze vačku axiálně stáhnout z čepu. Při opětovné
montáži použít dva NOVÉ pásky ze zásoby a opakovat ruční kontrolu pohybu.
Nikdy nepáčit čep stranou ani nesprávnou vůli upravovat pilníkem/vrtákem.
Jde o navržené pořadí; fyzický přístup, skutečná páka a kabely potřebují přejímku.

Pro vyjmutí celého rámu odpojit elektroniku/kabely, samostatně odstranit
horní moduly, oba reproduktory a pravou kazetu, povolit 24 M3 × 14
základen a čtyři M4 × 10. Čtyři lepené kotvy zůstávají na dně.
Kryty a powerbanku lze běžně vyjmout vlastními spoji bez této demontáže.

Přední levý M4 má svislý přístup skrz otvor Ø4,2 v polici ovladačů.
Červený spínač je na Y49 místo původních Y43 mm; jeho elektrické vývody
a funkce se nemění. Pro utažení odstranit kryt, použít imbus 3 mm se
stopkou nejvýše Ø4 mm a rukojetí nad hranou kufru.
Levý zesilovač je posunut z X46 na X45 mm pro průchod hlavy zadního
kazetového šroubu. Rozměry desky a zapojení zůstávají stejné.

## Servis bočních kazet

Odpojit zdroj a označené konektory, sejmout servisní kryty. Pravá kazeta
má čtyři M3 přístupné shora. Pro levou nejprve uvolnit oba pásky levého
reproduktoru, vyjmout banku a vyparkovat odpojený USB konektor. Klip je
přišroubovaný přímo ke kazetě a zvedne se s ní.
Před zvednutím levé kazety odpojit a vyjmout červený/zelený spínač a PTT
z horní desky. U skutečně pájeného přívodu použít odpájení a obnovu
izolace; nevymýšlet rychlokonektor. Vyšroubovat všechny čtyři horní M3
spoje ovladačů, zvednout `switch_shelf` i s jeho dvěma maticemi krytu,
potom vyjmout `switch_frame_right` s maticemi spojů 2 a 4. Levý boční
rám, jeho matice spojů 1 a 3 i oba původní spodní šrouby zůstávají
v rámu kufru. Pravý boční rám drží horní deska; nemá vlastní spodní
šroub. Samotné odstranění spínačů bez desky a pravého rámu nestačí.
Teprve pak uvolnit tři kazetové M3 shora a kazetu zvednout svisle.
Její matice zůstanou v rámu. Při obnově nejprve vložit kazetu, potom
pravý rám a horní desku ovladačů, dotáhnout čtyři spoje a až nakonec
osadit a připojit spínače. Prostor západek spínačů a skutečnou rukojeť
nástroje ověřit na prototypu; digitální kontrola nezahrnuje prsty.
Pevný nosič reproduktoru i banky má tiskové přístupové drážky pro skutečné
nohy kazety. Použít rovný 2mm imbus se stopkou nejvýše Ø3,4 mm, rukojeť
nad hranou kufru; objem rukojeti v úzkém bočním prostoru není předpokládán.
Před úplným zvednutím všechny vývody odpojit; kabel nesmí nést kazetu.
Výměna pájeného prvku nebo LED vyžaduje pájení a obnovení izolace, nikoli
předstírání neexistujícího rychlokonektoru. Po servisu obnovit pásky a kontroly.

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
