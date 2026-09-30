# RGB, zatřesení a napájení kufru

Technický podklad k [implementačnímu plánu](implementacni-plan.md), zejména
kapitolám 8 a 10. Níže jsou návrhové hodnoty; měření konkrétní desky,
bezpečnostního obvodu a pohonů se zapisuje do [stavu implementace](stav-implementace.md).

## Jedna přihrádka

Pod každou ze šesti hůlek bude přibližně 20cm úsek adresovatelného 5V RGB pásku WS2812B (30 LED/m, tedy asi šest LED). Šest fyzicky oddělených úseků zapojíme datově za sebou; v programu bude mít každá přihrádka vlastní rozsah pixelů. Pro svícení jednotlivých přihrádek tak stačí jedna datová linka. Napájení +5 V a GND povedeme rozvodem s odpovídajícím průřezem, nikoli přes tenké datové propojky. Přesnou délku potvrdíme po změření kufru.

Pod každou přihrádkou počítáme s vlastním malým vibračním modulem. Při výběru se rozvibruje pouze vybraná hůlka nebo její lehce oddělená vložka; poté servo vysune přihrádku vzhůru. Prototyp jedné přihrádky ověří, zda modul vyvolá viditelný pohyb bez rozechvění celého kufru. Rozhodnutí majitele 30. 9.: hůlka zůstává jako rekvizita. Servo po prezentaci vrátí přihrádku do spodní polohy, aby další návštěvník nepotřeboval ruční reset. Zdvih, pulzy, mechanické dorazy a schopnost držet polohu bez proudu se musí ověřit na prototypu. Při Stop/poruše se automatický návrat nespouští.

Zamýšlená sekvence: krátké postupné světelné hledání → barva na vybrané přihrádce → tři pulzy vibrace → zdvih servem → prezentace → návrat dolů → klidové světlo. Motor a servo se při výběru nesmějí překrývat; aktivní je nejvýše jeden motor. Starší ruční sekvence pouze vypisuje kroky na sériový port. Nový autonomní model má simulovaný port a hostitelské testy; fyzický adaptér a měření dosud chybí.

## Napájení

Powerbanku si majitel vybere a koupí samostatně. Pro navržené zapojení musí poskytovat USB-C PD profil 20 V, doporučeně alespoň 2 A; potřebný je také kompatibilní kabel USB-C → USB-C. Z USB-C výstupu vyžádáme 20 V přes PD modul a měničem vytvoříme společnou 5V větev pro kufr. Měnič je udávaný na nejvýše 5 A, pro trvalý provoz však počítáme nejvýše se 4 A. Deska ESP32 se napájí přes USB-C z ověřeného 5V výstupu měniče; servo driver má zvlášť 3,3V logické napájení a 5V napájení serv. Všechny země propojit. Vypínač s kontrolkou patří na 5V stranu měniče. Před připojením řídicí desky změřit výstup měniče; 20 V je pouze mezi PD modulem a jeho vstupem. Po vypnutí na 5V straně je potřeba odpojit či vypnout powerbanku, pokud její PD výstup zůstává aktivní.

Pásek 30 LED/m má maximální příkon přibližně 9 W/m. Šest 20cm úseků tedy může při plné bílé dosáhnout asi 10,8 W (2,16 A na 5 V). K tomu přibudou kamera/deska, zvuk, jedno servo a jeden vibrační motor. Proto v programu omezíme globální jas a budeme spouštět jen jeden motor a jedno servo v daném kroku; první návrh počítá s limitem jasu kolem 25 %. Skutečnou rezervu a dobu provozu potvrdíme měřením, ne jen teoretickým součtem.

Na vstup RGB řetězce patří převod logiky 3,3 → 5 V, sériový rezistor 330 Ω a na 5V větev u pásku přibližně 1000 µF. Totéž je vhodné u větve motorů/serv. V nákupním seznamu jsou čtyři dostupné kondenzátory 470 µF / 50 V; dvě dvojice zapojené paralelně dají po 940 µF. Pokud bude zvuk rušený nebo se deska při pohybu restartuje, upravíme napájení až podle naměřeného problému.

## Bezpečnostní větev pohonů podle plánu

Za společným 5V zdrojem oddělit trvale napájenou logiku/RGB/audio od **spínané
větve serv a vibračních modulů**. Povolení pohonů je hardwarová logika
`ACTUATOR_EN = RUN_REQUEST AND WATCHDOG_OK AND PHYSICAL_ENABLE`;
PCA9685 `OE = NOT ACTUATOR_EN`. RUN_REQUEST má externí pull-down.
Watchdog musí sledovat skutečný postup řídicí úlohy, ne autonomní PWM.
Reset, Stop či zamrzlá řídicí úloha musí pohony odpojit podle časových limitů plánu.

PCA9685 kanály 0–5 řídí serva při společné servo frekvenci, kanály 6–11
digitálně FULL_ON/OFF ovládají vstupy modulů U059. PCA nenapájí motory;
holý motor vyžaduje vlastní driver. Samotné vypnutí servo PWM nezaručuje
odpojení energie; proto samostatný load switch/watchdog a fyzické enable.
Snímání enable do firmware nenahrazuje přímé hardwarové blokování.

Konkrétní watchdog, logika, proudově dimenzovaný spínač, rezistory a mechanický
servisní vypínač dosud nejsou vybrané ani započtené v košíku. Připravit
schéma a ověřit resetové úrovně, zpětné napájení přes signály, proudy,
I²C chybu a zamrznutí v E0/E5. Ztráta napájení nesmí sama nebezpečně
pohnout západkou; bez mechanického ověření nelze prohlásit celek za bezpečný.
Nenahrazovat tyto kontroly úspěšným softwarovým testem.

## Před dokončením nákupu

- Změřit vnitřní délku a šířku šesti přihrádek pro RGB úseky a vibrační modul.
- Podle [dodané pinové mapy desky](pin-mapa-esp32-s3.md) ověřit předběžné profily A/B z plánu; vazby obrázku ověřit na doručené revizi.
- Doplnit rozpočet bezpečnostní větve, případného MCP23017 pro B a provozního routeru; historické součty nákupních seznamů tyto položky neobsahují.
- Prototypem ověřit účinek jednoho vibračního modulu; při společném nákupu šesti kusů zůstane jedna zásilka z Botlandu.
- Ověřit zapnutí, vypnutí a chování powerbanky při nízkém odběru a při pohybových špičkách.
