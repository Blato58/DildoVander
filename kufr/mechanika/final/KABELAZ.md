# Kabeláž F1: příprava, montáž a servis

Tento plán převádí [potvrzené zapojení](../../docs/zapojeni-potvrzeneho-hardwaru.md)
do montážních svazků. Nemění GPIO ani kanály PCA. Používá tři z deseti
koupených Degsonů, koupené dvojlinky 1,5 a 0,5 mm², původní kabely modulů,
Dupont, dvě univerzální desky, rezistory a bužírky. Dalších sedm Degsonů,
druhá PCA, červená kolébka a čtyři kondenzátory zůstávají odpojené rezervy.
Žádné nové JST-SM, Grove adaptéry ani konektorové sady nejsou součástí návrhu.

`harness.py` vytváří `cable-plan.json` a `cable-cut-list.csv` přes hlavní build.
Každý vodič má ID, oba konce, síť, průřez, trasu, délkové rezervy a dva štítky.
Souřadnice jsou **návrhové kotevní body**, nikoli změřené polohy konektorů.
Porty Degson 1–5 jsou montážní štítky, ne tvrzení o orientaci svorky.
Všech pět pozic jednoho bloku je jeden vodivý uzel.

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
| 11 | USB-ESP, F_AUDIO+-03 | Připravená USB větev zůstává nezapojená, dokud není ověřen existující vhodný napájecí adaptér |
| 12 | Všechny ID | Ručně ověřit plný pohyb každé přihrádky, nezávislý pohyb sousedů, zavření krytů a snadné vyjmutí powerbanky |

GND i V+ serv se vedou přes jejich původní třípinové kabely do PCA;
PCA má vlastní 1,5mm² V+ a výkonový návrat. Spoje nikdy nejsou v ohybu.
100kΩ pull-down U059 patří na stranu modulu, aby platil i při odpojeném
signálu z PCA. SD/GAIN zesilovačů jsou nezapojené, R1 se neodpájí.
SPK− není GND: oba reproduktorové páry jsou oddělené od země i od sebe.
I2C pull-upy musí být jen na 3V3; případný odpor OE proti GND na modulu
identifikovat a upravit podle potvrzeného zapojení před prvním zapnutím.

## Délky, trasy a pohyblivé smyčky

Výpočet používá součet pravoúhlých úseků trasy, **40 mm servisní rezervy**
a **20 mm na zakončení**, zaokrouhlený nahoru na 10 mm. Nejde o přímou
vzdálenost konektorů ani průkaz bezpečného ohybu. Napájecí kmen je v
rezervě Y205–219 / Z40–50 (14×10 mm, osa Y212/Z45); signály v přední
rezervě Y22–31 / Z32–44. Větve položit ve vrstvách bez křížení napříč
výškou, skutečné zaplnění dvojlinkami a bužírkami ověřit před zavřením.
K U059 vede výkon od zadního kmene po vnějším okraji jednotlivého lůžka
k přednímu místu smyčky, aby nevznikla přímá trasa přes vačku a servo.
Levá napájecí větev a vývody kondenzátorů 1/2 mají svislou trasu v půdorysu
po X60, na pravé straně powerbanky; nepokládat je přes její tělo ani do
dráhy vyjmutí. Přechod přes vnitřní boční kryt používá zadní kabelový otvor.
Výkonové přívody PCA a Pixel Shifteru se od zadní trasy vracejí po pravém
vnějším okraji X365 do přední trasy Y27/Z38; nepoložit je přímo napříč
vačkami či servy. Zaplnění úzkého pravého okraje ověřit skutečnými kabely.
Sestup horní elektroniky používá kabelové otvory v servisních stolcích:
vlevo X61–69 / Y88–98, kotevní bod X65/Y93; vpravo X371–379 / Y44–56,
kotevní bod X376/Y50. Zesilovače, měnič a místní výkonový řetězec mají
trasu přes příslušný otvor. Vodiče ovladačů nejdřív vedou nad reproduktorem
do Y62, zvednou se na Z84, přesunou na X65 a vedou do Y93 mimo bok
měniče, teprve potom projdou levým otvorem dolů. USB-C kabel powerbanky
vede nejdřív do Y99/Z84, pak X65/Y99 a X65/Y93 mezi měničem a zesilovačem.
U ESP se kabel nejdřív
přesune nad pravým stolkem na X380 při zachování vlastního Y/Z, pak na
X380/Y50 a X376/Y50 a teprve sestoupí do přední trasy. Stejně vést horní
přívody pravého zesilovače a USB napájení; podélný úsek na X376 u Y120
by křížil vysokou podpěru pravého předního stojanu.
Pravé větve po sestupu projdou X376/Y50/Z38 do X380/Y50/Z38,
pak X380/Y27/Z38 a X365/Y27/Z38
a teprve na X365 pokračují k zadní trase Y212/Z45, aby neprocházely
řadami nohou servisních stolků na X376.
Skutečné polohy vývodů ani průchod konektorů nejsou tímto odhadem doloženy.
Při pokládce nezakrýt vedení, vačku, brzdu, páku, mikrofon ani chlazení.

LED a U059 se s přihrádkou pohybují o **16 mm**. Každý jejich napájecí
i signálový vodič má navíc **64 mm volné délky**: 2×16 mm na relativní
pohyb a 32 mm návrhové rezervy pro oblouk. Smyčka musí mít odlehčení na
pevné základně i pohyblivé přihrádce; bužírka ani pájený spoj nesmí tvořit
ohybný úsek. Smyčku položit v přední vyhrazené oblasti mimo vodítka a
vačku: v každé přihrádce místní X17–41, globální Y86–112 / Z36–60.
Trasy vedou přes pevný návrhový bod místní X29 / Y99 / Z45 a pohyblivý
bod X29 / Y99 / Z59. Od modulu nejdřív sestoupí na Z59 při zachování
jeho X/Y, pak vedou k Y99 a X29; vysoká vodorovná trasa by křížila
žebra LED či páteř přihrádky. LED kotevní bod je místní X47, nominálně
2 mm vně žebra; není to změřená poloha pájecí plošky.
Obě strany RGB propojky
navštíví vlastní smyčku; čára mezi kotevními body nepředstavuje pružný kabel.
Konkrétní poloměr, tuhost a přichycení ověřit se skutečným kabelem.
Spoje DOUT/DIN mezi sousedními LED mají dvě oddělená pohyblivá odlehčení;
ověřit jeden úsek nahoře a sousední dole i opačně. Zvolené délky ani CAD
obálka smyčky tuto fyzickou kontrolu nenahrazují. Servo zůstává na pevné
základně, proto jeho kabel nedostává pohybovou rezervu. Jeho přívody
vedou z přední trasy nejdřív přes místní X26 / globální Y30 / Z38,
tam stoupají na Z55 a potom vedou do Y145 / Z55,
přes místní X14 a nakonec dolů ke kotevnímu bodu serva Z45. Tím návrhová
střednice obchází přední pouzdro vodítka a lůžko serva.
Vývody kondenzátorů 3/4 u PCA se přesunou po Y35 při zdrojové výšce Z23,
potom do cílového Y55 stále při Z23 a až tam sestoupí na Z17, aby nekřížily
druhý kondenzátor ani okraj základny. Kondenzátory 3/4 mají střed X174/187,
Y55/Z17; posun z Y43 uvolňuje matici ve středu X181/Y43. Nominální součet
poloměru kondenzátoru 5 mm a obálky matice 3,4 mm je 8,4 mm; tuto vzdálenost
středů návrh překračuje, fyzické usazení se stále ověřuje při montáži.

`estimated_total_mm` je návrhová délka celého vodiče. `estimated_cut_mm`
je návrhový přídavek z koupené dvojlinky po odečtení známého původního
vedení: servo 150 mm, Grove 200 mm, PH2 100 mm. Nula znamená, že nemusí
být potřeba prodloužení; **původní kabel nekrátit před zkouškou**. U
Dupontu zachovat potřebné koncovky F-F/M-F z koupených 200mm vodičů,
delší signál prodloužit pájeným 0,5mm² vodičem a bužírkou; neřetězit
další plastové konektory. Jejich krátké koncové pahýly výpočet preventivně
neodečítá. Použít skutečně potřebnou pohlavnost podle osazení lišt, ne
domnělé orientace modelu. U dvou intaktních USB kabelů je `stock_mm=1000`;
jejich zbytek uložit na pevné straně s velkým obloukem, nekřížit dráhu
vyjmutí powerbanky. Žádný řádek neprokazuje celkovou dostupnou délku
koupené dvojlinky; délku zásoby zkontrolovat před stříháním. Kladné
hodnoty jsou odhady, konečné délky určit přiložením k montované sestavě.

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
Kryty odnímat
po uvolnění uchycení, vodiče nesmějí nést váhu krytu. U powerbanky
odpojit pouze její USB-C kabel a uvolnit pásky; ostatní svazky mají
odlehčení na pevné konstrukci mimo svislou dráhu vyjmutí.

ESP se napájí přes ověřený USB-UART konektor. USB-A napájecí modul ze
starého košíku není potvrzená koupě, a proto plán **nepovažuje tuto větev
za kompletní**. Připravené konce 5V/GND zaizolovat a nepřipojovat k
5V pinu ESP. Při připojení PC odpojit externí napájení ESP, dokud není
prokázaná ochrana proti backfeedu. Tato mezera neblokuje rozvržení a
přípravu ostatních kabelů; před autonomním napájeným provozem ji vyřešit
ověřením skutečného vhodného existujícího adaptéru.

Programové testy kontrolují kapacitu svorek, oddělení sítí, pořadí kanálů,
izolované NC, zákaz SPK/GND a délkové rezervy. Elektrická měření,
skutečné provedení pájení a izolace, teploty a pohyb kabelů **neproběhly**.
