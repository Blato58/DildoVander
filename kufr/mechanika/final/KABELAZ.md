# Kabeláž F2: příprava, montáž a servis

Tento plán převádí [potvrzené zapojení](exports/reference-sources.html#zapojeni)
do montážních svazků. Nemění GPIO ani kanály PCA. Používá tři z deseti
koupených Degsonů, koupené dvojlinky 1,5 a 0,5 mm², původní kabely modulů,
Dupont, dvě univerzální desky, rezistory a bužírky. Dalších sedm Degsonů,
druhá PCA, červená kolébka a čtyři kondenzátory zůstávají odpojené rezervy.
Rezervy jsou mimo aktivní sestavu. Doplnit jednu pasivní zásuvku USB-A samice
nebo pigtail ≥3 A / 5 V a jeden intaktní USB-A/C kabel 300 mm ≥3 A.
Žádný nový napájený modul. GPIO, PCA kanály a všech 135 ID vodičů zůstávají
zachované. ESP/kamera jsou vzadu uprostřed, mikrofon vpředu uprostřed,
reproduktory po stranách míří nahoru; plášť ani víko se nevrtají.

`harness.py` vytváří `cable-plan.json` a `cable-cut-list.csv` přes hlavní build.
Každý vodič má ID, konkrétní piny obou konců, síť, svazek, průřez, barvu,
vnější průměr, zakončení, trasu, délkové rezervy a dva štítky.
Piny mají samostatné obálkově relativní kotvy z `parameters.json`;
souřadnice jsou **návrhové kotevní body**, nikoli změřené polohy konektorů.
Návrhový vnější průměr signálních vodičů 0,14 mm² je 1,2 mm; u výkonových
větví 0,5 mm² se počítá s 2,1 mm a u kmenových vodičů 1,5 mm² s 2,7 mm.
Pro USB kabel se počítá se 4,5 mm. Jde o předpoklady pro návrhovou obálku,
nikoli o měření skutečně koupených vodičů; všechny průměry ověřit na materiálu.
Porty Degson 1–5 jsou montážní štítky, ne tvrzení o orientaci svorky.
Všech pět pozic jednoho bloku je jeden vodivý uzel.
Náhled rozvádí každou obsazenou pozici až ke skutečným součástkám přes
označené pájené rozbočky, včetně navazující logické země. Každá větev
zachovává v popisku ID portu a síť; rozbočka je elektrický spoj, ne další
součástka či konektor. Připravená USB napájecí větev se zobrazuje u ESP
s poznámkou o doplnění pasivní zásuvky a cestě přes USB-UART. Ve vieweru
se odbočky označují `F_NAME:joint`, starší surová data zachovávají `F_NAME`.

## Tři bloky a izolované rozbočky

| Blok / síť | 1 | 2 | 3 | 4 | 5 |
| --- | --- | --- | --- | --- | --- |
| D1 / MAIN5 | Z červené OUT | Zelená IN | F_LED+ | F_AUDIO+ | F_PANEL+ |
| D2 / DRIVE5 | Ze zelené OUT | PCA V+ | F_U123+ | F_U456+ | Prázdný |
| D3 / GND | Měnič OUT− | PCA výkonová GND | F_LED− | F_U− | F_AUDIO_PANEL− |

Do každé obsazené pozice patří právě jeden vodič 1,5 mm². Tím má každý
blok jeden přívod a nejvýše čtyři další vedení. Rozbočení za blokem řeší
předem připravené **izolované pájené měděné spoje**: k vodiči 1,5 mm²
připojit samostatné 0,5mm² větve. Spoje rozložit podél svazku a jednotlivě
izolovat bužírkou; více pólů nesmí sdílet odkrytý spoj. Každá větev má svůj
přístupný pájený spoj a štítek, aby šla vyměnit bez rozpojení ostatních větví.
Žádné dva vodiče se nemačkají do jedné pozice. Tenký Dupont ani tenké
původní vodiče Grove se do Degsonu nevkládají (minimum 0,2 mm²).

Výkon nevede přes plošky či pocínované můstky univerzálních desek ani
5V pin ESP. Desky nesou pouze nízkoproudé sdílené signální spoje I2C/I2S;
pomocné odpory se pájejí místně k příslušnému signálu a izolují. Napájecí
rozbočky a návraty jsou vodičové spoje mimo PCB. Nevyužité plošky nemají
přístup k výkonové větvi. Obě desky se upevní izolovaně na rezervovaných
pravých podložkách, nic se nevrtá skrz elektroniku.

| Rozbočka | Individuální větve |
| --- | --- |
| F_LED+ / F_LED− | Pixel Shifter a šest úseků LED, každý s vlastním napájením a GND |
| F_AUDIO+ | Dva zesilovače a připravená větev pro napájení ESP přes USB-UART |
| F_PANEL+ | Pouze samostatný kladný vývod LED bílého tlačítka |
| F_U123+ / F_U456+ | Červené vodiče U059 1–3 / 4–6; ne napájení z PCA signálových pinů |
| F_U− | Šest černých vodičů U059 |
| F_AUDIO_PANEL− | Dva zesilovače, USB napájení ESP, tři LED ovladačů a místní logická zem F_LOGIC− |
| F_LOGIC− | MCP a mikrofon; PCA používá svou GND vedenou D3-2 |
| F_LOGIC3 | Z ESP 3V3 samostatně PCA VCC, MCP VIN/VCC a mikrofon 3V3 |

GND se zelenou kolébkou nepřerušuje. MAIN5 a DRIVE5 se spojují pouze
sepnutou zelenou kolébkou, nikoli rozbočkou. Rozvod 12 V končí na vstupu
měniče. Tříkontaktový Joy-IT má fyzicky IN+, společnou GND a OUT+;
označení `buck:IN-` a `buck:OUT-` v seznamu jsou logické aliasy jedné GND,
nikoli čtvrtá svorka. Do jejího kontaktu vložit jeden vodič 1,5 mm²,
za ním připravit izolovanou pájenou rozdvojku stejného průřezu na návrat
PD OUT− a přívod D3-1. Dva neověřené vodiče nevkládat společně do kontaktu.
Síť PD_RETURN je elektricky stejná GND, pojmenovaná podle části vedení.
Kondenzátory 1/2 jsou paralelně na LED napájení, 3/4 u PCA V+
a 5/6 na audio napájení, každý vlastními izolovanými vývody; nepřidávají
pozice v Degsonu. U všech šesti ověřit polaritu. Po vypnutí pohonů může
dvojice 3/4 ještě držet energii; dobu vybití změřit před servisem.

## Svazky a pořadí stavby

DFA 12. 10.: všechny pevné svazky sestavit a ověřit na pracovní vložce
mimo kufr, před lůžky a kryty. Spodní desku pravé kazety zapojit před
horní deskou; levou kazetu od jističe/zesilovače k měniči. Vývody do
servo/Grove/LED označit a nechat volné do instalace horních skupin.
Kazety mají sedm horních M3, odpojitelná rozhraní a servisní přístup;
bez dostatečné fyzicky ověřené délky pro odpojení vývodů kazetu nezvedat.
LED nemají nový rychlokonektor. Pinout, sítě, průřezy a potvrzené zapojení
zůstávají stejné; nové mechanické příruby podléhají plnému CAD auditu
135 konečných vodičů včetně ohybů. Před střihem ověřit skutečné kabely.


| Pořadí | ID v řezném seznamu | Montáž a kontrola |
| ---: | --- | --- |
| 1 | USB-PD, P12+/− | Intaktní koupený C–C kabel, PD nastavit na 12 V; krátké vodiče do vstupu měniče, změřit polaritu |
| 2 | P5-FUSE, P5-MAIN | Měnič 5 V → jistič → červená IN. Před další montáží změřit 5 V bez elektroniky |
| 3 | D1-1…5, D2-1…4, D3-1…5 | Připravit označené 1,5mm² přívody; proměřit oddělení sítí a funkci obou kolébek |
| 4 | F_* | Na stole připravit rozbočky, každou 0,5mm² větev označit na obou koncích; zaizolovat a odlehčit |
| 5 | C1+…C6−, L3-FEED, MCP-A0…A2, MIC-LR | Kondenzátory a místní logika; MCP adresa 0x20, mikrofon levý slot; žádný 5V pull-up |
| 6 | SIG-SDA*, SIG-SCL*, SIG-OE | PCA 0x40, 100 kHz; OE přímo GPIO2, dva 22kΩ paralelně na 3V3 u PCA; při resetu ověřit HIGH |
| 7 | S1-*…S6-*, U1-DIN…U6-DIN | PCA 0–5 serva 1–6, PCA 6–11 U059 1–6; Grove u modulu zachovat, žlutá DIN s místním 100kΩ na GND, bílá NC samostatně izolovaná |
| 8 | SIG-RGB, RGB-1…6, F_LED* | GPIO48 → Pixel Shifter → 330Ω → DIN LED1 → DOUT/DIN až LED6; šest úseků po šesti LED; napájení úseků ze svazku |
| 9 | SIG-BCLK*, SIG-WS*, SIG-DIN*, SIG-MIC, SPK-* | I2S GPIO21/41/47, mikrofon SD GPIO42; každý PH2 pouze svůj zesilovač a reproduktor |
| 10 | SIG-PTT, PTT-GND, F_PANEL* | GPA0 kontakt proti GND s místním 15kΩ na 3V3, LED tlačítka odděleně na 5 V; LED kolébek má vlastní LED-GND návrat |
| 11 | USB-ESP, F_AUDIO+-03 | Doplnit pasivní zásuvku USB-A samice ≥3 A, VBUS z MAIN5, GND ze společné země; USB-A/C 300 mm do USB-UART |
| 12 | Všechny ID | Ručně ověřit plný pohyb každé přihrádky, nezávislý pohyb sousedů, zavření krytů a snadné vyjmutí powerbanky |

GND i V+ serv se vedou přes jejich původní třípinové kabely do PCA;
PCA má vlastní 1,5mm² V+ a výkonový návrat. Spoje nikdy nejsou v ohybu.
100kΩ pull-down U059 patří na stranu modulu, aby platil i při odpojeném
signálu z PCA. SD/GAIN zesilovačů jsou nezapojené, R1 se neodpájí.
SPK− není GND: oba reproduktorové páry jsou oddělené od země i od sebe.
I2C pull-upy musí být jen na 3V3; případný odpor OE proti GND na modulu
identifikovat a upravit podle potvrzeného zapojení před prvním zapnutím.

## Délky, trasy a rezervy pro pohyb

Všechny souřadnice vycházejí z aktuálního `parameters.json`; není zachována
servisní police F1 ani stará trasa v osách X/Y. `harness.endpoint_registry()`
přiřazuje každému konkrétnímu pinu vlastní předběžnou kotvu vztaženou k obálce,
včetně aliasů společné země měniče. `harness.routes()` je jediným zdrojem
lomených tras ve vieweru i výkresu kabeláže; středy součástek nenahrazují
trasy fyzických pinů. Napájení a signály mají nad rámem oddělenou výšku trasy.
Pevné zadní desky vedou do zadního koridoru; mikrofon prochází přední částí
a potom pevným pravým servisním koridorem. Každá ze šesti kabelových smyček
přihrádek má místní rezervu X17…41, Y52…78 a Z(rovina rámu+24)…(rovina rámu+48).
Vodiče serv jsou pevné a nedostávají rezervu pro zdvih.

Odhad délky k ustřižení = délka lomené trasy + 40 mm servisní rezerva
+ 20 mm na zakončení + 64 mm za KAŽDÝ samostatně pohyblivý konec; výsledek
se zaokrouhluje nahoru na 10 mm. RGB-2…6 mají pohybovou rezervu 128 mm;
RGB-1 a samostatné napájecí větve LED/U059 mají 64 mm. Každý pohyblivý konec
má vlastní smyčku, pevné uchycení a pohyblivé uchycení. Spoje, rezistory
a bužírky patří mimo úseky, které se ohýbají. Ověřit pohyb sousedních přihrádek
v opačných krajních polohách. Lom trasy nedokládá poloměr ohybu: ohyb vodiče
8 mm a zdrojového USB kabelu 20 mm jsou předběžné hodnoty ke kontrole při
fyzickém sestavení, nikoli potvrzené poloměry.

`estimated_total_mm` označuje celou potřebnou délku vodiče. `estimated_cut_mm`
odečítá pouze skutečnou délku původního kabelu serva 150 mm, Grove 200 mm,
PH2 100 mm nebo intaktního USB kabelu. Délky kabelů v zásobách nedokládají
skutečně koupené množství vodiče CYH. Původní kabely nezkracovat před
zkušebním sestavením. Zachovat potřebné koncovky Dupont; prodloužení pájet,
izolovat a použít průřez uvedený pro daný vodič v CSV. Konektory Dupont
nikdy neřetězit jako napájecí kmen.

## Zdroj USB, pasivní zásuvka pro ESP a přeprava

Koupený kabel USB-C/C 1 m, 60 W/3 A, zůstává neporušený mezi konektorem C1
powerbanky a modulem PD. PD nastavit na 12 V a před připojením desek změřit
na výstupu měniče 5 V. Přebytek kabelu se ukládá do samostatné svislé smyčky
v osách Y/Z VEDLE vyjímatelné powerbanky; `harness.USB_COIL` vymezuje
předběžnou rezervu 18×80×65 mm v bodě [49,165,20]. Tři závity tvoří
průběžnou spirálu s bočním posunem a postupným růstem poloměru;
nejmenší návrhový oblouk je přibližně R20 mm, vzorkovaný po 10°.
Celá trasa měří 939,0 mm; s 40 mm servisní rezervy a 20 mm na konce
se požadavek po zaokrouhlení rovná celému 1 m koupeného kabelu.
Další délkovou rezervu tedy nezapočítávat. Obálka počítá s průměrem
nejvýše 4,5 mm a kontrola nepřipouští návrat kabelu přes vlastní závit.
Tyto návrhové hodnoty nedokládají skutečnou délku tuhých koncovek ani
dovolený ohyb koupeného kabelu. Kabel
nasucho vyzkoušet; nezmenšovat poloměr ohybu kvůli vměstnání do rezervy
a nezasahovat do dráhy vyjmutí powerbanky. Cívku uchytit dvěma stahovacími
pásky PA66 3,6×250 mm v samostatné kolébce.

Po odpojení C1 se volná koncovka připne do pevného držáku vlevo.
`transport_points` nahrazuje část trasy vedoucí k powerbance;
`transport_from` je `usb_park_clip:unplugged_C` a elektrický stav je
DISCONNECTED (ODPOJENO). Zobrazená kabeláž se po vyjmutí powerbanky
nepřipojuje k jejímu volnému místu. Konec u PD i smyčka zůstávají pevné.
Powerbanka se samostatně jistí dvěma pásky na suchý zip 8×300 mm.
Při přepravě se prázdná lůžka zajistí šesti pásky na suchý zip 8×250 mm.

Na pevný zadní nosič DOPLNIT přesně jednu PASIVNÍ zásuvku nebo pigtail USB-A
samice ≥3 A / 5 V: F_AUDIO+-03 → VBUS, F_AUDIO_PANEL--03 → GND. D+/D− zůstávají
nepřipojené a každý samostatně izolovaný; nepoužít napájený adaptér, druhý
měnič, paralelní výstup powerbanky ani přívod na 5V pin ESP. K USB-UART
DOPLNIT jeden neporušený kabel USB-A/C 300 mm ≥3 A. Koupený kabel A/C 1 m
zůstává mimo aktivní sestavu jako servisní zásoba. Dokud není změřena ochrana
proti zpětnému napájení, odpojit externí zdroj USB-A před připojením k PC.

## Materiál a nákup

| Materiál | Potřeba pro aktivní sestavu | Zásoba / doplnění |
| --- | --- | --- |
| USB-C/C 1 m, 60 W, 3 A | 1 neporušený kabel; zdroj → PD | Koupeno |
| Pasivní zásuvka/pigtail USB-A samice ≥3 A, 5 V | 1; pouze VBUS/GND | Doplnit; starší nákupní košík nákup nedokládá |
| USB-A/C 300 mm ≥3 A | 1 neporušený kabel; zásuvka → USB-UART | Doplnit |
| Degson, 5 pozic | 3 oddělené sítě | Z 10 koupených; dodání a rozměry neověřeny |
| Třípinový kabel serva / Grove / PH2 | 6×150 / 6×200 / 2×100 mm zachovat a doplnit délkami dle CSV | Koupeno; zachovat původní koncovky |
| Pružný měděný vodič 1,5 mm² | Součet jednotlivých `estimated_cut_mm` pro 1,5 mm² + 20 % | Položka koupena; dodaná délka v metrech neznámá |
| Pružný měděný vodič 0,5 mm² | Součet jednotlivých `estimated_cut_mm` pro 0,5 mm² + 20 % | Položka koupena; dodaná délka neznámá; případný nedostatek doplnit |
| Signální vodič 0,14 mm² | Součet jednotlivých řezů 0,14 mm² + 20 % | Množství určit podle aktuálního CSV |
| Dupont 200 mm M–F/F–F | Pohlaví podle dodaných protikusů; nejvýše 2 koncovky na vodič | Koupeno 80 M–F + 80 F–F; nepoužívat pro kmenové napájení |
| 22 kΩ / 100 kΩ / 15 kΩ / 330 Ω | 2 / 6 / 1 / 1 | Koupeno z balení po 10 / 20 / 10 / 10 |
| 470 µF, 50 V | 6; tři paralelní dvojice | Koupeno z balení po 10 |
| Univerzální deska 50×70 mm | 2; pouze signálové spoje | Koupeno |
| Smršťovací bužírka, štítky, snímatelné pásky | Izolovat každý spoj; 270 štítků konců; dva úchyty na každý pohyblivý konec; 2× PA66 3,6×250 mm pro USB smyčku, 2× suchý zip 8×300 mm pro powerbanku, 6× suchý zip 8×250 mm pro přepravu | Bužírka koupena; štítky a pásky započítat při sestavení |

CSV udává délku jednotlivých vodičů, nikoli dvojlinky. Při nákupu CYH sečíst
kladné a záporné vodiče do dvojic a nepárové signály vykázat samostatně;
nepočítat automaticky každý jednotlivý vodič jako celou dvojlinku. Pro každý
průřez zvlášť sečíst délky `estimated_cut_mm` v aktuálně vygenerovaném CSV
a přidat 20% rezervu. Konečné potřebné metry tedy vycházejí až z tohoto CSV
a skutečné zásoby; tabulka neurčuje pevný počet metrů. Před nákupem porovnat
výsledek se skutečně dodaným materiálem. Řezat až po zkušebním sestavení;
aktuální vygenerované CSV má přednost před staršími součty ve výkresech.
Protikus fastonu na vývodech jističe a kolébek není změřený: před výběrem
protikusů určit jeho velikost. Plán připouští izolované připájené vývody
kolébek; velikost fastonu neodhadovat.

Kamera-FPC je samostatná LOGICKÁ návaznost mezi blízkými ESP a kamerou
uprostřed zadní části. GPIO4/5/6/7/8/9/10/11/12/13/15/16/17/18 zůstávají
beze změny. Konektor, stranu kontaktů, délku a poloměr ohybu určuje dodaná
varianta kamery; konektor ani náhradní volný GPIO kabel se nevymýšlí.
I2S mikrofonu zůstává na GPIO21 SCK, 41 WS, 42 SD; zesilovače sdílejí
21 BCLK, 41 LRC a 47 DIN. SD/GAIN zůstávají volné, R1 se zachovává a každý
pár PH2 SPK+/− zůstává oddělený od GND i druhého zesilovače.

## Co lze při servisu rozpojit

Před servisem Stop, zelenou OFF, červenou OFF, odpojit USB-C zdroj,
změřit vybití a ověřit nepřítomnost napětí. Žádný konektor není určen
pro odpojování pod zatížením. Původní Grove se odpojí u U059, Dupont u
PCA či logiky, původní třípinový konektor u serva a PH2 u vlastního
reproduktoru. Výkonové odbočky mají označené přístupné pájené spoje;
pro výměnu jedné přihrádky se její LED napájení a DOUT/DIN odpájí
(případně odřízne konkrétní spoj s rezervou pro obnovu) a znovu izoluje.
LED nemají koupený rychlorozpojitelný konektor. Toto uspořádání šetří
svorky a drží tři sítě přehledně oddělené, ale individuální výměna LED
vyžaduje pájení. Výměna větve nesmí změnit číslo přihrádky ani pořadí LED.

Kolébkám připájet vodiče a zaizolovat vývody koupenou bužírkou;
nepředpokládat dva spínací póly, proměřit IN/OUT/LED-GND. Tříkontaktové
kolébky nemají čtvrtý externí LED+ vývod: kladné napájení jejich LED
je uvnitř z OUT, samostatně připojit pouze LED-GND. Pokud skutečný kus
neodpovídá tomuto zapojení, před fyzickým připojením upřesnit jeho funkci.
Kryty lze odejmout po uvolnění uchycení; vodiče nesmějí nést jejich váhu.
U powerbanky
odpojit pouze její USB-C kabel a uvolnit pásky; ostatní svazky mají
odlehčení na pevné konstrukci mimo svislou dráhu vyjmutí.

Napájení ESP je navrženo přes USB-UART s pasivní zásuvkou popsanou výše.
Jde o návrhovou rezervaci, nikoli fyzicky ověřený konektor. Větev nebude
fyzicky dokončená, dokud nebude zásuvka i krátký kabel pořízen a nebude
ověřena polarita a ochrana proti zpětnému napájení. To nebrání sestavení
nezávislých částí ani nepotvrzuje akceptaci zařízení pod napětím.

Programové testy kontrolují kapacitu svorek, oddělení sítí, pořadí kanálů,
izolované NC, zákaz SPK/GND a délkové rezervy. Elektrická měření,
skutečné provedení pájení a izolace, teploty a pohyb kabelů **neproběhly**.
