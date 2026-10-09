# Tisk, montáž, servis a přejímka F1

## Pořadí výroby

1. Vytisknout `fit_coupon`. Zkusit šrouby, skutečné PTT a kolébku, zasunutí
   tepelných vložek a materiálovou vůli vodítka. Nulovat odchylky v parametrech,
   nikoli měřítkem ve sliceru.
2. Vytisknout jednu sadu `lift_base`, `wand_bed`, `dwell_cam`. Zkoušet ji na
   stole se skutečnou servopákou, U059, LED a hůlkou. Teprve po úspěchu vyrobit
   dalších pět. Test není potřeba provádět se sestavenou celou skořepinou.
3. Sestavit základnu a víko, osadit kovové kování, nosný postroj a omezovací pásky.
4. Namontovat boční servisní sestavy, elektrické rozvody a jednotlivé moduly.
5. Osadit kryty a látku. Ověřit pohyb, servis a transport ve finálním materiálu.

### Nastavení tisku

PETG, tryska 0,4; vrstva 0,2; nejméně 4 obvody, 5 horních/spodních vrstev.
Vačka, pouzdra vodítek a kořeny stojek 5–6 obvodů, lokálně 80–100 % výplň.
Korpus přibližně 30–40 %, lůžka a základny 40 %. Jsou to výchozí nastavení,
ne doklad pevnosti. Materiál nesmí zůstat v rozpáleném automobilu.

STL jsou již položené na podložku. Lůžka, stolky a čtyři velké kryty se tisknou
lícem dolů: vodítka, nohy a clony potom rostou nahoru. Vačka se tiskne osou
Z, drážkou vzhůru. Není třeba vyplnit podpěrami vnitřek krytů/stolků.
**Lokální podpory jsou potřeba pouze pod volnými 12mm přesahy horní poloviny
polopřeplátování dna/pláště.** Namalovat je na spodní přesahy dlaždic,
ne do celého otevřeného kufru. Hloubka dutiny pod přesahem je jen 2 mm;
použít snadno odstranitelnou podporu s rozhraním. Po odstranění zkontrolovat
rovinnost styku. Malé horizontální otvory 3,4–4,5 a kapsa páky široká 6,4
počítají s krátkým mostem. Kamerový otvor podle výsledku tisku případně dočistit.

Větší kryty mají půdorys až 214 ×202,7. 3mm brim se vejde přesně do 220 mm
na delší ose; purge line a automatické ochranné okraje nesmějí zabrat tiskovou
plochu dílu. Výška všech dílů je nejvýše 118,3. Na suché, odmaštěné podložce
zkontrolovat první vrstvu a zvedání rohů. Na estetický povrch je vhodná matná
tmavě hnědá barva; vodítka a drážku nebarvit.

## Skořepina a nosnost

Šest dílů spojit nejprve volně na rovné desce. Poloha je určena 12mm
přeplátováním, mezera mezi okraji 0,25. Dno má 13 šroubovaných spojů M4,
svislé stěny dalších 12; stejné počty platí pro víko. Dnové zápustné hlavy
jsou vně dole. Použít M4×8 s podložkou 0,5 a běžnou maticí 3,2; závit musí
zabrat celou matici, nesmí přesahovat výšku 9 mm a dotýkat se základen.
Před hromadným sestavením fyzicky ověřit reálnou délku a hlavu šroubu.
Při jiné toleranci použít přizpůsobený šroub/zahloubení, neskrývat kolizi.

U svislých spojů dát nízkou hlavu dovnitř a matici ven. Krytá matice může
tvořit nenápadný kovový detail. Při dlouhém šroubu zakrýt nebo zkrátit závit;
vnější zbylé konce nesmějí řezat postroj. Dotahovat po malých krocích,
bez promáčknutí PETG. Proti povolování používat mechanické zajištění nebo
rozebíratelný přípravek pouze na kovové závity; ověřit jeho snášenlivost s plastem.

Tři ocelové panty: 40mm délková obálka; připravené drážky mají rozteč
24 mm a dovolují výškové přizpůsobení. Vnitřní kapsy schovávají hlavy,
aby nezasahovaly do krytů. Vybrat kování odpovídající obálce a otvorům;
jeho referenční model není objednaný výrobní výkres. Osa je 3 mm za zadní
stěnou uprostřed 1mm spáry. Panty nesmějí sloužit jako koncový doraz.

Oba 8mm textilní omezovače provléknout bočními štěrbinami a zajistit
šroubovacími svorkami/přezkami. Výchozí účinná vzdálenost ok přibližně
141 mm; nastavit současný tah při 105°. Založení pásků před zavřením je
po bočních stěnách, ne přes elektroniku. CAD ukazuje natažený stav, nikoli
simulaci složené textilie. Víko má jen odnímatelný samet na obvodových
podložkách suchého zipu; prostor neobsahuje zařízení ani elektrický kabel.

Pro přenos použít **hotový nosný textilní postroj s doloženou nosností**,
ne improvizované švy zajištěné kancelářským lepidlem. Dva pásy šířky 25
obepínají kufr v X=130/310 a procházejí pod dnem. Spojovací rukojeť nese
oba pásy. Návrhová transportní hmotnost je 5 kg. Návrhová 3g síla je
147 N celkem, přibližně 74 N na pás při rovnoměrném rozdělení. Zvolit
postroj alespoň na 10 kg a ověřit jeho skutečné uložení; čísla nejsou
certifikací vytištěné skříně. Primární dráha síly je dno → pásy → rukojeť.

## Jedna přihrádka

Základna stojí na 5mm výstupcích dna; čtyři M3×20 ji připevňují skrz dno.
Dvě M4×90 procházejí vodicími sloupky a vyztužují je. Dole nízké hlavy,
nahoře Ø12 podložky a matice. Podložky jsou zároveň pojistka proti vysunutí
lůžka; mezi nimi a horní polohou lůžka zůstává vůle. Nepřetáhnout a nedeformovat
vodítko. Vodicí plochy začistit, bez vazelíny u sametu.

Servo má tvrdé opěry proti pootočení a dvě průchozí drážky pro vyměnitelné
stahovací pásky. Usadit tak, aby skutečná osa souhlasila s osou vačky.
Pozice osy vůči deklarované obálce serva není změřená; případnou tenkou
podložku či korekci sedla ověřit na prvním modulu. Nedotahovat pásky přes kabel.
Základna záměrně neobsahuje vymyšlenou rozteč montážních uší.

Použít původní oboustrannou páku 35 mm a původní šroub serva. Plastový
tisícihran se netiskne. Vačka má zadní kapsu pro páku, centrální přístup
k šroubu a protilehlé radiální drážky. Připevnit dvěma M2 šrouby do skutečných
otvorů páky, **ideálně na poloměru 8–10 mm**. Rozteč dodané páky není ověřená.
Model počítá s poloměrem 9 mm (`horn_fastener_radius`), M2×10 s hlavou
maximálně Ø3,5 ×1,3 vzadu a dvěma maticemi zapuštěnými do přední plochy vačky.
Matice nesmějí zůstat nad plochou, kde by zasáhly do nálitku válečku.
Při změně poloměru se regenerují také kapsy matic; praktická zkouška musí
zahrnout celý oběh spojů. V případě neshody upravit pouze drážky/adaptér,
ne vrtat náhodně servo. Střední šroub musí zůstat dostupný.

Váleček je nylonový Ø6/4,2 ×3 na mosazném pouzdře Ø4/3,2 ×4,5.
Pouzdro sevře běžný M3×12 s nízkou kulovou hlavou ISO7380 proti 4mm nálitku;
na vnější straně je podložka 0,5 a matice výšky 2,4. Hlava maximálně Ø5,7 ×1,65
se vejde do drážky 6,8 široké a 6 hluboké (X=33..39). Nálitek leží X=40..44,
matice končí X=46,9 a špička šroubu X=47,5, před sousedním modulem na X=48.
Váleček leží X=35,7..38,7; mezikus Ø6/4,2 ×1,1 u nálitku ponechá celkovou
axiální vůli 0,4. Utahovací sílu nese mosazné pouzdro, ne otočný nylon.
Pouzdro zaříznout kolmo a odjehlit; váleček nesmí běžet přímo po závitu.
Matici zajistit na kovovém závitu. Výměna je možná po povolení jediného šroubu.
Pod vačkou je mělké odlehčení základny, zbývá 2,5 mm dna.
Rozměry nízké hlavy M2 vycházejí z [katalogu Accu](https://www.accu.co.uk/socket-button-screws/252906-SSB-M2-16-A2-R360),
hlava M3 z [tabulky ISO 7380 McMaster](https://www.mcmaster.com/products/screws/specifications-met~iso-7380/).
Jde o ověření obálky typu hlavy; konkrétní balení ani žádný materiál nebyly objednány.
U059 přiložit přes tenký tuhý izolační
suchý zip pod lůžko mezi dorazy a zajistit dvěma pásky; silná měkká pěna by
tlumila požadovanou vibraci.

Do děleného předního pouzdra vložit dva tenké pásky plsti (výchozí 0,3 mm
na protilehlé stěny). M3×20 s podložkami seřizuje sevření. Brzda potřebuje
přibližně 3 N kluzného odporu při návrhové hůlce 150 g; přesnou hodnotu
přepočítá `verification.json` z objemu lůžka. Samotné utažení šroubu není
důkaz pasivní stability. Doladit na váze/siloměru a zaznamenat skutečný odpor.
Vačka s uzavřenou drážkou táhne i dolů, takže se nezastaví nad klesající
otevřenou vačkou jen kvůli této brzdě.

Geometrie: kruhové drážky R10/R26, zdvih 16; přechod 20–140° a horní
klidový úsek 140–180°. Mechanická pracovní rotace 0–160°. Oba konce
kalibrovat individuálně tak, aby ležely uprostřed klidových úseků, s rezervou
od elektrických dorazů serva; žádné společné domyšlené PWM pro všech šest kusů.
Orientační minimální testovací moment s 1,5 rezervou vychází 0,114 N·m,
nikoli měřený výkon koupeného serva. Ověřit proud, plynulost a sílu pod zátěží.

## Kabeláž, látka a ovládání

Začít [kabelovým plánem](KABELAZ.md): připravit a označit svazky na stole,
ověřit izolované rozbočky a porty svorek, potom osazovat kufr. Napájení
se sestavuje vlevo: PD pod stolkem, měnič nahoře, jistič na izolované
spodní straně. Levý stolek je nyní v Z76; jeho kabelový výřez má střed
X65/Y93. Pravý přední stolek má výřez X376/Y50. Vodiče vést těmito výřezy
před připevněním stolků, jejich konektory se otvorem neprotahují.
Větrání měniče je v levém
bankovém krytu; nesmí se zakrýt látkou ani rezervou USB kabelu.

Výkonový svazek je veden napříč základnami za vačkami, kolem Y=212,
Z=45 v rezervě 14 × 10 mm. Odbočky mají odlehčení tahu. Pravý stolek obsahuje spodní kabelový
průchod; clony krytů mají průchody hluboko pod lůžky, ne odkryté otvory v líci.
Signály vedou přední servisní zónou. Nepřikládat mikrofonní vodiče těsně
k rozběhovým proudům serv; napájení se větví hvězdicově podle závazného souboru.

Servo kabel 150 mm nepokryje každou trasu. Použít příslušné části koupených
200mm M–F propojek pro signály a odpovídající napájecí vedení; konektorové
spoje pojistit a označit 1–6. U059 má 200mm kabel. Pro pohyblivé LED/U059
vytvořit před vačkou pružnou smyčku, návrhový poloměr
alespoň 12 mm, a odlehčit oba konce existujícími drážkami lůžka a základny.
Vyhrazený prostor: lokální X=17..41, Y=52..78, globální Z=36..60.
Při zdvihu se kontroluje tato obálka; skutečný svazek ověřit při celém
zdvihu a návratu. Poloměr 12 mm je návrh, případný větší limit výrobce má přednost.
Nepředpokládat, že samotná CAD rezerva prokazuje ohyb kabelu. Rezervy kabelů
vázat do osmičky, nedávat 1m USB smotek k vačce nebo k měniči.

Látku lepit/sponkovat pouze na odnímatelné kryty nebo používat textilní suchý
zip. Rovné desky a clony potáhnout samostatnými obdélníky, roh přeložit
bez nánosu lepidla. 0,8mm návrhová tloušťka, kolem pevných dělení alespoň
1 mm volné hrany. Na vodítka, vnitřek drážky, LED, akustické otvory a
nezateplené větrání měniče látka nepatří. Lůžko má jednoduchý sametový pás
na horní ploše, dvě měkké příčné opory a dvě rozepínatelné smyčky kolem hůlky.
Samet nepřehýbat do vodicích otvorů. Hůlka se při prezentaci nevyndává;
pro vložení/vyjmutí obsluha jednoduše otevře obě smyčky.

Bílé tlačítko je vpředu vlevo, střed X38/Y19. Jeho plocha je mírně pod
okrajem kufru. Dvě kolébky jsou za ním (X22 a56, Y40,5) ve vlastní spodní
polici. Krycí samet volně překlenuje dvě okna 28×27; **nad kolébkou je 3 mm
volného prostoru**, předpokládaná látka jej nesmí předepínat. Žádná tvrdá
tyčka trvale netlačí na kontakt. Stisk přes látku působí přímo na příslušnou
polovinu kolébky; obě poloviny pro ON/OFF musí zůstat dosažitelné.
Směr ON/OFF ověřit podle skutečného kusu a potisku, nikoli barvy vodiče.

Na sametu červené pozice udělat jednu nenápadnou hmatovou čárku, zelené dvě.
Obsluha tak rozliší hlavní vypínač a servisní odpojení pohonů bez odhalení barev.
Před vypnutím zelené vyvolat Stop bílým; po zapnutí zkontrolovat všech šest
poloh a potvrdit zotavení dle firmware. Skryté kolébky se nepřeznačují na
nouzová certifikovaná tlačítka. Ochrana proti náhodnému stisku je volná membrána,
meziprostor a obslužná poloha; prakticky ji ověřit, zvláště po napnutí sametu.

## Servisní cesty

| Zásah | Co se snímá / povoluje |
| --- | --- |
| Vyjmout powerbanku | Jeden levý zadní kryt L, konektor C1, dva pásky; tah přímo vzhůru. Ostatní desky a lůžka zůstávají |
| Vyměnit ESP / připojit USB | Pravý přední kryt L, pásek/suchý zip desky; pro PC odpojit externí napájení proti backfeedu |
| Kamera | Stejný kryt, dva šrouby malého držáku, originální FPC; bez pohybu okolních modulů |
| Servo / vačka | Odpojit pohony, rozepnout hůlku, vyjmout horní dvě matice vodítek a čep válečku, zvednout jedno lůžko; dva pásky serva |
| LED / U059 | Odpojit banku. U059 rozpojit původním Grove. LED výkonový spoj je pájený: odkrýt označenou izolovanou odbočku a odpájet konkrétní větev, DATA rozpojit samostatně. JST-SM páry nejsou koupené; vyjmutí LED přeruší DATA řetězec |
| Reproduktor | Boční kryt a čtyři matice příslušného stolku, odložit stolek na servisní smyčce; dva pásky a PH2 daného reproduktoru |
| Měnič / jistič / PD / levé audio | Levý zadní kryt; čtyři matice levého stolku a zvednutí na servisní rezervě pro spodní jistič/PD; banka nemusí ven. Reset jističe pouze bez napájení |
| Obě univerzální PCB | Pravý zadní kryt; pro dolní patra čtyři matice a zvednutí stolku, krátké servisní smyčky |
| Bílé tlačítko | Levý přední kryt L, matice tlačítka; ponechat přibližně 200–300mm servisní smyčku |
| Červená / zelená | Tentýž kryt; čtyři M3 spodní police, uvolnění pojistek kolébky bez trhání látky |

Stolky jsou stažené M3 závitovými tyčkami skrz nohy a základnu, maticemi
nahoře/dole; jsou to průchozí kovové spoje. Spodní matice se vloží před
přišroubováním základny ke dnu. Rámy jsou do dna připevněné pomocí M3
tepelných vložek přístupných zespodu před montáží; jejich zesílené kořeny mají
Ø10. Spodní police kolébek používá čtyři horní M3 vložky v Ø8 hlavách stojek.
Žádný opakovaně servisovaný spoj není snap-fit ani samořezka do tenké stěny.

## Přejímka skutečného kusu – zatím neprovedena

- Měrka a jeden modul: hůlka se vejde s potahem, servopáka/osa sedí,
  váleček se nesevře, kompletní kabeláž se nikde nenapíná ani nedře.
- Se skutečnou zátěží zvolit alespoň 21 poloh zdvihu, vypnout impulzy i
  zelenou kolébku; po 60 s musí být samovolný posun nejvýše 1 mm. V koncích
  nesmí být servo drženo proti dorazu. Brzda nesmí po zahřátí/tečení povolit.
- Po seřízení provést nejméně 100 cyklů jedné přihrádky; znovu změřit vůle,
  tření, proud a teplotu. Při nedostatečné síle nebo pasivní stabilitě
  zastavit rozšíření na šest kusů a upravit mechaniku; nevypnout brzdění
  ani nevynucovat trvalý proud servem jako náhradu.
- Ověřit Stop, výpadek během zdvihu i návratu, restart a ruční zotavení.
  Geometrie nevytváří snímač polohy. Bezpečnostní kritéria E5/E6/B1 zůstávají otevřená.
- Ověřit obě poloviny každé skryté kolébky po potažení; deset stisků a
  deset snímání/nasazování krytu nesmí vyvolat nechtěné přepnutí. Zkontrolovat,
  že zavřené víko a transportní popruhy na ovladače netlačí.
- Při běžném nejhorším dovoleném souběhu měřit napětí, proud a teplotu
  po 30 minutách; maximálně jeden pohon celkem. Měnič/látka nesmějí překročit
  limity výrobce ani se zhoršit proudění. Zkoušet otevřený provoz i uzavřený
  elektrický stav, který uživatel skutečně dovolí; transport je bez powerbanky.
- Zvážit osazený kufr bez hůlek a banky. Do 5 kg: statická zkouška postroje
  a skříně s 10kg inertní zátěží těsně nad měkkou podložkou, bez lidí pod ní,
  60 s, následná kontrola spojů. Poté 20 běžných zvednutí a 100 otevření víka.
  Nejde o pádovou certifikaci. Vyšší skutečná hmotnost vyžaduje nový návrhový limit.

Při přenášení vyjmout všech šest hůlek i banku, vrátit prázdná lůžka dolů
a každé zajistit samostatným 8mm páskem skrz připravené otvory základny/lůžka.
Zafixovat všechny rezervní díly, zavřít kovové západky a dotáhnout vnější postroj.
Zatížení motoru ani jeho převodovka neslouží jako transportní zámek.
