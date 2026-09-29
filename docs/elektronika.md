# Elektronika k objednání

Stav návrhu k 28. 9. 2026. Ceny jsou orientační z průzkumu a mohou se změnit; před platbou zkontrolovat variantu, sklad a cenu a odečíst již pořízené díly. Prioritu v rozpočtu 10 000 Kč má **AI kufr**. Powerbanku si majitel pořídí samostatně, proto není v nákupních součtech. Součet není cena celého kostýmu a nezahrnuje dopravu, kufry, knihu, hůlky ani mechanické díly. Překročení celkového rozpočtu vyžaduje schválení majitele.

## 1. AI kufr — koupit jako první

**Doplnění podle [implementačního plánu](../kufr/docs/implementacni-plan.md):** tabulky a součty níže jsou historický nákupní podklad, nikoli kompletní kusovník finálního systému. Chybí konkrétní watchdog, bezpečnostní logika/spínač napájení pohonů a PHYSICAL_ENABLE včetně snímání. Pro profil B může přibýt MCP23017 a příslušenství, pro provoz router/AP; při nevyhovění desky další řadič. Výběr a ceny doplnit po E0/E5, před objednáním přepočítat rozpočet. Stav nákupů ani měření tento dokument nepotvrzuje; viz [stav implementace](../kufr/docs/stav-implementace.md).

Počítáme se šesti hůlkami, každou s krátkým adresovatelným RGB páskem, vibračním modulem a západkou uvolňovanou servem. Kufr má vlastní mikrofon, reproduktor a tlačítko; mobilní aplikace později zajistí ovládání a záložní vstup. AI služba ani její případné poplatky nejsou v této tabulce. [Nákup rozdělený do dvou obchodů](nakupni-seznam-kufr.md) · [Návrh efektů a napájení](../kufr/docs/efekty-a-napajeni.md).

| Součástka | Počet | Orientačně | Odkaz / poznámka |
| --- | ---: | ---: | --- |
| ESP32-S3 N16R8 s konektorem OV5640 | 1 | 287 Kč | [Drátek](https://dratek.cz/arduino-platforma/232701-vyvojova-deska-esp32-s3-s-konektorem-pro-kameru-ov5640-wifi-bluetooth.html); kamera není součástí této varianty |
| Mikroservo, polohovací 180° | 6 | 300 Kč při odběru 6 ks | [Drátek](https://dratek.cz/arduino-platforma/897-eses-servo-motor-9g.html); množstevní cena 50 Kč/ks, sílu pro konkrétní západku ověřit prototypem |
| Driver serv PCA9685, 16 kanálů | 1 | 167 Kč | [Drátek](https://dratek.cz/arduino-platforma/1686-iic-i2c-modulovy-driver-servo-motoru-pro-arduino-pca9685-16-kanalu-12-bit-pwm.html); šetří piny pro kameru, logika 3,3 V a napájení serv 5 V odděleně |
| I²S mikrofon Adafruit ICS43434 | 1 | 262 Kč | [Botland](https://botland.cz/mikrofony-a-detektory-zvuku/26730-ics43434-mems-mikrofon-s-rozhranim-i2s-adafruit-6049-5904422388041.html); napájet 3,3 V |
| I²S mono zesilovač MAX98357A | 1 | 153 Kč | [Botland](https://botland.cz/mp3-wav-ogg-midi-prehravace/21992-modul-zesilovace-zvuku-max98357-dfrobot-dfr0954-6959420922703.html) |
| Reproduktory 5 W / 8 Ω, 100 × 45 × 21 mm | 1 balení po 2 ks | 98 Kč | [Botland](https://botland.cz/analogove-reproduktory/16399-reproduktor-5w-8-100x45x21mm-2-ks-waveshare-14595-5904422378158.html); nejprve použít jeden s mono zesilovačem, hlasitost ověřit |
| Momentové tlačítko s podsvícením, otvor 12 mm | 1 | 59 Kč | [Drátek](https://dratek.cz/arduino-platforma/121714-led-vodotesny-spinac-12mm-bile-podsviceni.html); PTT / spuštění rozhovoru |
| Adresovatelný barevný RGB pásek WS2812B, 30 LED/m, 5 V, IP65 | 5 m | 652 Kč | [Botland](https://botland.cz/adresovatelne-led-pasky/5447-rgb-led-pasek-ws2812b-digitalni-adresovany-ip65-30-led-m-9w-m-5v-5m-5904422303068.html); každá LED umí RGB barvy, šest úseků asi po 20 cm, zbytek rezerva |
| Převodník datové úrovně Adafruit Pixel Shifter 6066 | 1 | 136 Kč | [Botland](https://botland.cz/prislusenstvi-pro-led-pasky/28640-pixel-shifter-prevodnik-logickych-urovni-33-v-5-v-pro-adresovatelne-led-diody-adafruit-6066-5906623498802.html); 3,3V signál ESP32 na 5V datovou linku WS2812B |
| Rezistor 330 Ω | 1 | 1 Kč | [Drátek](https://dratek.cz/arduino-platforma/7655-rezistor-330r-0.25-w-1.html); do datové linky před první LED |
| Vibrační modul M5Stack U059 | 6 celkem | 492 Kč | [Botland](https://botland.cz/moduly-jednotek-rozsirujici-moduly/17999-vibracni-motor-n20-rozsirujici-modul-unit-pro-vyvojove-moduly-m5stack-6972934170972.html); **nejprve 1 kus na prototyp**, dalších 5 po ověření účinku |
| Powerbanka s USB-C PD 20 V | 1 | mimo košík | Majitel ji koupí samostatně; před zapojením ověřit profil 20 V a vhodný USB-C kabel |
| USB-C PD modul s volbou 20 V | 1 | 32 Kč | [Drátek](https://dratek.cz/arduino-platforma/232511-power-modul-usb-c-pd-s-nastavitelnym-napetim-a-prepinacem.html); pouze na vstup 5V měniče |
| DC/DC měnič 9–30 V → 5 V / 5 A s USB-A výstupem | 1 | 57 Kč | [Drátek](https://dratek.cz/arduino-platforma/180391-step-down-menic-xy3606-9-30v-dc-5a-usb-vystup-dc5-5x2-1-vstup-svorkovnice.html); jedna 5V větev pro kufr, před připojením změřit |
| Kabel USB-A → USB-C pro desku | 1, pokud není doma | 72 Kč | [Drátek](https://dratek.cz/arduino-platforma/180220-datovy-a-nabijeci-kabel-usb-c-usb-a-1m-cerny.html); z USB-A měniče do desky ESP32 |
| Kabel USB-C → USB-C, 60 W / 20 V 3 A | 1, pokud není doma | 185 Kč | [Drátek](https://dratek.cz/arduino/180410-baseus-napajeci-kabel-usb-c-na-usb-c-60w-1-metr-cerno-sedy.html); mezi samostatně kupovanou powerbankou a PD modulem |
| Kolébkový vypínač | 1 | 18 Kč | [Drátek](https://dratek.cz/arduino-platforma/1242-prepinac-kolebkovy-led-dioda-cervena.html) |
| Vratná pojistka 5 A | 1 | 18 Kč | [Drátek](https://dratek.cz/arduino-platforma/232509-nadproudovy-tepelny-jistic-vratna-pojistka-5a.html) |
| Elektrolytický kondenzátor 470 µF / 50 V | 4 | 8 Kč | [Drátek](https://dratek.cz/arduino-platforma/7826-kondenzator-470uf-50v.html); vždy dva paralelně k 5V větvi RGB a pohonů |

**Orientační součet kufru k objednání: cca 2 997 Kč včetně šesti vibračních modulů a obou kabelů, bez samostatně kupované powerbanky, společného materiálu a dopravy.** Se společným materiálem 326 Kč činí košíky dvou obchodů přibližně **3 323 Kč**. Pokud jsou vhodné kabely už doma, odečíst 72 Kč a 185 Kč. První prototyp může použít jeden vibrační modul; zbývajících pět představuje dalších 410 Kč, ale i další zásilku. Serva a RGB napájet mimo 5V pin řídicí desky, omezit jas a spouštět pohony postupně. [Pinová mapa desky](../kufr/docs/pin-mapa-esp32-s3.md) je dodaná, ale zapojení externích prvků zatím není schválené.

**Volitelné budoucí rozšíření:** kamera pro rozpoznání kostýmů/postav. Zvolená deska už má konektor OV5640, ale kamera není v ceně 287 Kč. Alternativně lze místo ní koupit [sadu desky s kamerou OV5640 za cca 479 Kč](https://dratek.cz/arduino-platforma/232702-vyvojova-deska-esp32-s3-s-kameru-ov5640-wifi-bluetooth-nespajeny-s-pinovou-listou-v-baleni.html), tedy asi o 192 Kč více. První pokus může využít kameru telefonu. [Návrh rozšíření](../kufr/docs/kamera.md).

## 2. Obludné obludárium — připraveno pro pozdější nákup

Tuto část zatím neobjednávat na úkor rozpočtu kufru. Kniha bude mít vlastní napájení a program, bez AI a bez mobilní aplikace.

| Součástka | Počet | Orientačně | Odkaz / poznámka |
| --- | ---: | ---: | --- |
| Arduino Nano, USB-C, CH340 | 1 | 136 Kč | [Drátek](https://dratek.cz/arduino-platforma/121745-usb-c-klon-arduino-nano-ch340.html) |
| ToF senzor VL53L0X | 1 | 88 Kč | [LaskaKit](https://www.laskakit.cz/laserovy-senzor-vzdalenosti-gy-vl53l0x-i2c/) |
| Servo MG996R s kovovými převody | 1 | 137 Kč | [Drátek](https://dratek.cz/arduino-platforma/1694-servo-motor-s-kovovymi-prevody-mg996r-pro-rc-modely.html); sílu a rozměry potvrdit po otevření plyšové knihy |
| Vibrační jednotka M5Stack U059 | 1 | 82 Kč | [Botland](https://botland.cz/moduly-jednotek-rozsirujici-moduly/17999-vibracni-motor-n20-rozsirujici-modul-unit-pro-vyvojove-moduly-m5stack-6972934170972.html); jemné třesení |
| Převodový motor GA12-N20, 6 V | 1 | od 104 Kč | [LaskaKit](https://www.laskakit.cz/ga12-n20-6v-motor-s-prevodovkou/); **konkrétní otáčky vybrat až podle mechanismu** |
| Motorový driver TB6612FNG | 1 | 94 Kč | [LaskaKit](https://www.laskakit.cz/dvoumotorovy-radic-tb6612fng/) |
| DFPlayer Mini | 1 | 57 Kč | [Drátek](https://dratek.cz/arduino-platforma/4857-hlasovy-modul-s-integrovanym-mp3-prehravacem-dfplayer.html) |
| Reproduktor 3 W / 4 Ω | 1 | cca 119 Kč | [TME](https://www.tme.eu/cz/details/ld-sp-msi40/reproduktory/loudity/) |
| microSDHC 16 GB | 1 | 299 Kč | [DATART](https://www.datart.cz/pametova-karta-verbatim-premium-micro-sdhc-16gb-class-10-80r-10w-adapter-44082.html); pro DFPlayer naformátovat FAT32 |
| Powerbanka | 1 | mimo košík | Majitel ji koupí samostatně; pro zde navržené napájení zkontrolovat USB-C PD profil 20 V |
| USB-C PD trigger, nastavitelný | 1 | 32 Kč | [Drátek](https://dratek.cz/arduino-platforma/232511-power-modul-usb-c-pd-s-nastavitelnym-napetim-a-prepinacem.html); nastavit na 20 V pouze pro vstup měniče |
| DC/DC měnič 9–35 V → 5 V / 5 A | 1 | 87 Kč | [Drátek](https://dratek.cz/arduino-platforma/1740-step-down-synchronni-napajeci-zdroj-dc-dc-buck-9-35v-na-5v-5a.html) |
| Kolébkový vypínač | 1 | 18 Kč | [Drátek](https://dratek.cz/arduino-platforma/1242-prepinac-kolebkovy-led-dioda-cervena.html) |
| Vratná pojistka 5 A | 1 | 18 Kč | [Drátek](https://dratek.cz/arduino-platforma/232509-nadproudovy-tepelny-jistic-vratna-pojistka-5a.html) |
| Elektrolytický kondenzátor 1000 µF, alespoň 10 V | 1 | cca 26 Kč | [TME](https://www.tme.eu/cz/details/mal213664102e3/elektrolyticke-kondenzatory-tht/vishay/) |

**Orientační součet Obludária: cca 1 300 Kč bez samostatně pořizované powerbanky, plus podíl na společném materiálu a doprava.** Volba převodového motoru, serva a skutečný odběr závisí na mechanice knihy. Před napájením elektroniky změřit výstup měniče na 5 V; 20 V z PD modulu se nesmí připojit přímo na součástky.

## Společný montážní materiál

| Součástka | Počet | Odkaz |
| --- | ---: | --- |
| Dvojlinka 2 × 0,5 mm² | 5 m | [Drátek](https://dratek.cz/arduino-platforma/3024-dvojlinka-cyh-2x0-5-cernorudy.html) |
| Dupont vodiče M–F, 40 cm | 2 balení | [Drátek](https://dratek.cz/arduino-platforma/121747-40-x-m-f-dupont-kabel-40-cm.html) |
| Sada smršťovacích bužírek | 1 | [Drátek](https://dratek.cz/arduino-platforma/1617-127-ks-sada-cernych-odolnych-smrstujicich-trubic.html) |
| Univerzální deska 50 × 70 mm | 2 | [Drátek](https://dratek.cz/arduino-platforma/843-univerzalni-plosny-spoj-50mm-x-70mm.html) |
| Páčková rychlosvorka Degson 5pin | 8 | [Drátek](https://dratek.cz/arduino-platforma/177181-rychlosvorka-bezsroubova-degson-5pin.html) |

**Orientačně cca 326 Kč při nákupu všech uvedených množství.** Při první objednávce lze vzít společný materiál pro kufr; materiál Obludária může počkat.

Nyní má nákupní prioritu pouze kufr. Rozpočet Obludária je starší orientační návrh a jeho položky zatím neobjednávat. Pro kufr budou navíc potřeba mechanické díly západek a výplň; přesné konektory a upevnění doplníme až po změření skutečných rekvizit.
