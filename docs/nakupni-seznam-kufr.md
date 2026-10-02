# Nákup elektroniky pro AI kufr: dva obchody

Ověřeno 28. 9. 2026. Jde o elektroniku **pouze pro kufr**. Powerbanku kupuje majitel samostatně a v cenách níže není. Ceny a dostupnost se mohou změnit; před odesláním objednávky zkontrolovat košíky a odečíst již pořízené díly. Stav dosavadních nákupů není potvrzen. Ostatní díly kostýmu, mechanika přihrádek a doprava nejsou zahrnuty.

Tato kombinace zachovává ESP32-S3 připravené na kameru, I²S mikrofon a reproduktor, šest barevně ovladatelných RGB úseků, šest polohovacích serv a šest vibračních modulů. Vše uvedené lze objednat u **Drátku a Botlandu**. Mikrofon ICS43434 nahrazuje dříve navržený INMP441; při psaní firmwaru nastavíme jeho I²S kanál a formát.

## 1. Drátek — řízení, napájecí moduly a montáž

**Rozsah košíku:** ceny níže nebyly při sjednocení dokumentace znovu ověřovány. Podle [implementačního plánu](../kufr/docs/implementacni-plan.md) ještě chybí výběr a ocenění bezpečnostní větve pohonů (watchdog, logika, load switch, PHYSICAL_ENABLE), případného MCP23017 pro profil B a provozního routeru. Tyto položky nejsou v dosavadních součtech; před nákupem ověřit revizi desky a doplnit kusovník/rozpočet. Postup eviduje [stav implementace](../kufr/docs/stav-implementace.md).

| Položka | Množství | Cena celkem | Odkaz |
| --- | ---: | ---: | --- |
| ESP32-S3 N16R8 s konektorem OV5640, bez kamery | 1 | 287 Kč | [Vývojová deska](https://dratek.cz/arduino-platforma/232701-vyvojova-deska-esp32-s3-s-konektorem-pro-kameru-ov5640-wifi-bluetooth.html) |
| Polohovací mikroservo 180° | 6 | 300 Kč | [Servomotor](https://dratek.cz/arduino-platforma/897-eses-servo-motor-9g.html), množstevní cena 50 Kč/ks od 5 ks; nekupovat variantu 360° |
| PCA9685, 16kanálový driver serv | 1 | 167 Kč | [Driver](https://dratek.cz/arduino-platforma/1686-iic-i2c-modulovy-driver-servo-motoru-pro-arduino-pca9685-16-kanalu-12-bit-pwm.html) |
| Rezistor 330 Ω / 0,25 W | 1 | 1 Kč | [Rezistor](https://dratek.cz/arduino-platforma/7655-rezistor-330r-0.25-w-1.html) |
| USB-C PD trigger s volbou 20 V | 1 | 32 Kč | [PD modul](https://dratek.cz/arduino-platforma/232511-power-modul-usb-c-pd-s-nastavitelnym-napetim-a-prepinacem.html) |
| XY3606, měnič 9–30 V → 5 V / 5 A | 1 | 57 Kč | [Měnič](https://dratek.cz/arduino-platforma/180391-step-down-menic-xy3606-9-30v-dc-5a-usb-vystup-dc5-5x2-1-vstup-svorkovnice.html) |
| Datový kabel USB-A → USB-C 1 m pro ESP32, pokud není doma | 1 | 72 Kč | [Kabel k desce](https://dratek.cz/arduino-platforma/180220-datovy-a-nabijeci-kabel-usb-c-usb-a-1m-cerny.html) |
| Napájecí kabel USB-C → USB-C 60 W / 20 V 3 A mezi powerbankou a PD modulem, pokud není doma | 1 | 185 Kč | [PD kabel](https://dratek.cz/arduino/180410-baseus-napajeci-kabel-usb-c-na-usb-c-60w-1-metr-cerno-sedy.html) |
| Vodotěsné momentové tlačítko s bílým podsvícením, otvor 12 mm | 1 | 59 Kč | [Tlačítko](https://dratek.cz/arduino-platforma/121714-led-vodotesny-spinac-12mm-bile-podsviceni.html); bílé je pouze podsvícení tlačítka, nikoli RGB pásek |
| Kolébkový vypínač | 1 | 18 Kč | [Vypínač](https://dratek.cz/arduino-platforma/1242-prepinac-kolebkovy-led-dioda-cervena.html) |
| Vratná pojistka 5 A | 1 | 18 Kč | [Pojistka](https://dratek.cz/arduino-platforma/232509-nadproudovy-tepelny-jistic-vratna-pojistka-5a.html) |
| Elektrolytický kondenzátor 470 µF / 50 V | 4 | 8 Kč | [Kondenzátor](https://dratek.cz/arduino-platforma/7826-kondenzator-470uf-50v.html); vždy dva paralelně na 5V větev LED a pohonů, dodržet polaritu |

**Řídicí a napájecí díly: přibližně 1 204 Kč.** Kabely lze vynechat, jsou-li vhodné doma nebo součástí powerbanky.

Montážní materiál z téhož obchodu:

| Položka | Množství | Cena celkem | Odkaz |
| --- | ---: | ---: | --- |
| Dvojlinka 2 × 0,5 mm² | 5 m | 65 Kč | [Dvojlinka](https://dratek.cz/arduino-platforma/3024-dvojlinka-cyh-2x0-5-cernorudy.html), objednat množství 5 |
| Dupont M–F, 40 cm, 40 vodičů | 2 balení | 114 Kč | [Propojovací vodiče](https://dratek.cz/arduino-platforma/121747-40-x-m-f-dupont-kabel-40-cm.html) |
| Smršťovací bužírky, sada 127 ks | 1 | 45 Kč | [Bužírky](https://dratek.cz/arduino-platforma/1617-127-ks-sada-cernych-odolnych-smrstujicich-trubic.html) |
| Univerzální deska 50 × 70 mm | 2 | 22 Kč | [Deska](https://dratek.cz/arduino-platforma/843-univerzalni-plosny-spoj-50mm-x-70mm.html) |
| Páčková rychlosvorka Degson 5pin | 8 | 80 Kč | [Svorka](https://dratek.cz/arduino-platforma/177181-rychlosvorka-bezsroubova-degson-5pin.html) |

**Montážní materiál: přibližně 326 Kč. Drátek celkem: přibližně 1 530 Kč.** Přesné konektory a upevnění se mohou po sestavení mechaniky ještě změnit.

**Doplnění po doručení (2. 10. 2026):** ESP32 má osazené samčí lišty;
pro přímé propojení s osazenými samčími I2C piny PCA9685 jsou potřeba
alespoň čtyři Dupont F-F (samice-samice) propojky, například z
[JustPi F-F 20 cm, 40 ks](https://botland.cz/spojovaci-vodice-samice-samice/19620-sada-propojovacich-kabelu-justpi-samice-samice-20cm-40ks-5903351243032.html).
Výše uvedené M-F je
přímo nenahradí. Tato chybějící položka není zahrnutá ve starších součtech.
Majitel nemá páječku ani multimetr: USB napájená logika může začít po
doplnění F-F, ale externí výstup XY3606 se před připojením elektroniky
musí změřit. XY3606 má pevný 5V výstup, nikoli nastavovací trimr.

## 2. Botland — zvuk, RGB pásek a vibrace

| Položka | Množství | Cena celkem | Odkaz |
| --- | ---: | ---: | --- |
| I²S mikrofon Adafruit ICS43434 | 1 | 262 Kč | [Mikrofon](https://botland.cz/mikrofony-a-detektory-zvuku/26730-ics43434-mems-mikrofon-s-rozhranim-i2s-adafruit-6049-5904422388041.html); napájet 3,3 V, akustický otvor směrem ven |
| I²S mono zesilovač DFRobot MAX98357A | 1 | 153 Kč | [Zesilovač](https://botland.cz/mp3-wav-ogg-midi-prehravace/21992-modul-zesilovace-zvuku-max98357-dfrobot-dfr0954-6959420922703.html) |
| Reproduktory 5 W / 8 Ω, 100 × 45 × 21 mm | 1 balení po 2 kusech | 98 Kč | [Reproduktory](https://botland.cz/analogove-reproduktory/16399-reproduktor-5w-8-100x45x21mm-2-ks-waveshare-14595-5904422378158.html); nejprve připojit jeden kus k mono zesilovači |
| Adresovatelný RGB pásek WS2812B, 5 V, 30 LED/m, IP65 | 1 role 5 m | 652 Kč | [RGB pásek](https://botland.cz/adresovatelne-led-pasky/5447-rgb-led-pasek-ws2812b-digitalni-adresovany-ip65-30-led-m-9w-m-5v-5m-5904422303068.html); každá LED může svítit libovolnou RGB barvou |
| Vibrační modul M5Stack U059 s motorem N20 | 6 plánovaně | 492 Kč | [Vibrační modul](https://botland.cz/moduly-jednotek-rozsirujici-moduly/17999-vibracni-motor-n20-rozsirujici-modul-unit-pro-vyvojove-moduly-m5stack-6972934170972.html) |
| Adafruit Pixel Shifter 6066, 3,3 V → 5 V | 1 | 136 Kč | [Převodník pro adresovatelné LED](https://botland.cz/prislusenstvi-pro-led-pasky/28640-pixel-shifter-prevodnik-logickych-urovni-33-v-5-v-pro-adresovatelne-led-diody-adafruit-6066-5906623498802.html) |

**Botland celkem: přibližně 1 793 Kč při nákupu všech šesti vibrací.** Pásek je skutečný RGB, nikoli pouze bíle svítící; pro šest úseků po přibližně 20 cm využijeme asi 1,2 m. IP65 silikonový kryt bude nutné v místě střihu odkrýt a spoje znovu izolovat. Úseky se stříhají na označených místech a datově propojí do jedné řady. Z balení reproduktorů se nejprve použije jeden kus; při 8 Ω dá zvolený mono zesilovač nejvýše přibližně 1,8 W. Pokud bude hlasitost nedostatečná, rozmístění a zapojení obou reproduktorů ověříme na prototypu.

## Součet a před objednáním

- **Dva obchody, všech šest vibračních modulů:** přibližně **3 323 Kč** za elektroniku, oba kabely a uvedený montážní materiál; bez powerbanky, dopravy a mechaniky.
- **Dva obchody, na začátek jeden vibrační modul:** přibližně **2 913 Kč**; zbylých pět modulů později za 410 Kč plus případná doprava. To přidá další zásilku, proto pro jednu objednávku z Botlandu počítat rovnou se šesti.
- Pokud jsou kabely k dispozici, odečíst 72 Kč za USB-A → USB-C a 185 Kč za USB-C → USB-C.
- Vlastní napájecí zdroj musí umět USB-C PD profil 20 V (doporučeně alespoň 2 A). Výstup měniče nejprve změřit na 5 V. Převodník Pixel Shifter slouží pouze pro datový signál LED, výkon pro LED jde přímo z 5V větve.
- Volitelnou kameru zatím nekupovat. Deska má konektor, takže ji lze později doplnit bez změny řídicí desky.

Výběr je doložen jednotlivými produktovými stránkami výše. Stav skladu a finální cenu určuje až košík prodejce při nákupu.

## Doplnění montážního materiálu po objednávce z Drátku (29. 9. 2026)

Řídicí díly z Drátku jsou podle majitele objednané, montážní materiál z tabulky výše v této objednávce chyběl. Původní součty jsou součty nákupního plánu, nikoli nová částka k objednání. Pro doplnění v Botlandu jsou ověřené tyto dostupné náhrady:

| Původní položka | Náhrada z Botlandu | Množství | Cena celkem | Rozdíl |
| --- | --- | ---: | ---: | --- |
| Dupont M–F, 40 cm, 2×40 vodičů | [JustPi M–F, 20 cm, 40 vodičů](https://botland.cz/spojovaci-vodice-samice-samci/19621-sada-propojovacich-kabelu-justpi-samice-samec-20cm-40ks-5903351243025.html) | 2 balení | 72 Kč | Poloviční délka; použít jen tam, kde skutečně dosáhnou, a jen pro signály. |
| Smršťovací bužírky, 127 ks černých | [Sada 100 ks různých barev](https://botland.cz/smrstovaci-buzirky/5375-sada-smrstovacich-buzirek-100-ks-rozdilne-barvy-5901969414615.html) | 1 | 55 Kč | Méně kusů a jiné barvy. |
| Univerzální deska 50 × 70 mm | [Oboustranná deska 50 × 70 mm](https://botland.cz/univerzalni-desky/2745-univerzalni-oboustranny-plosny-spoj-50x70mm-5904422303303.html) | 2 | 50 Kč | Stejný rozměr, oboustranné provedení. |

**Tyto tři náhrady stojí 177 Kč bez dopravy.** Propojky blíž původní délce: [M–F, 30 cm, 50 vodičů](https://botland.cz/spojovaci-vodice-samice-samci/1438-propojovaci-kabely-female-male-30cm-barevne-50ks-5904422349400.html) za 164 Kč/balení; dvě balení by zvýšila dílčí součet na 433 Kč, stále však nejde o původních 40 cm.

Zbylé dvě položky nemají v ověřené nabídce Botlandu bezpečně zaměnitelný výrobek:

- **Dvojlinka 2 × 0,5 mm², 5 m:** pro rozvod napájení se musí zachovat vhodný průřez a materiál vodiče. Signálové vodiče Dupont ani hotový síťový kabel s koncovkami ji nenahrazují.
- **Páčková svorka pro spojení pěti vodičů, 8 ks:** pět samostatných svorkových pozic na plošném spoji není totéž co jedna svorka, která všech pět vodičů vodivě spojí. Před výběrem alternativy ověřit vhodnost pro lankový vodič a proud dané větve.

Tyto dvě položky je nutné dokoupit jinde nebo změnit návrh rozvodu napájení po fyzickém rozvržení součástek. Ceny a dostupnost uvedené pro Botland byly ověřeny 29. 9. 2026.
