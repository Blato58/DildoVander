# Rozměry kufru a jeho součástek

Rešerše a doplnění majitelem **9. 10. 2026**. Inventář a množství vycházejí z
[potvrzených nákupů](../../docs/objednavka-doplneni-kufru-2026-10-06.md#4-už-koupené--znovu-neobjednávat).
Tabulky pokrývají všech 32 položek tohoto inventáře. Kamera a vybraná
powerbanka jsou uvedené zvlášť. Svorky Degson jsou pořízené, čekají na doručení.

Všechny rozměry jsou v **mm**, pokud není uvedeno jinak. Rozměry doplněné
nebo potvrzené majitelem mají přednost před původní katalogovou rešerší;
u ostatních údajů zůstávají uvedené zdroje.
Pořadí os přebíráme ze zdroje; nejde automaticky o osy X/Y/Z modelu.
Rozměry balení nepoužíváme jako rozměry součástky. Kabely, pájené vývody,
montážní vůle a prostor pro manipulaci vyžadují další rezervu.

Kufr má potvrzenou vnitřní obálku spodní části **431,8 × 266,7 × 114,3 mm
(šířka × délka × výška)**. Víko zůstává prázdné.
[Příčná výztuha](rozmery-doruceneho-kufru.md) má podle majitele **5 mm**;
její další rozměry nejsou podmínkou rozvržení kufru.
Tento přehled je rozměrovým podkladem pro [návrh F2](final/README.md);
samotné katalogové a potvrzené obálky nedokládají fyzické usazení.

## Řízení, zvuk a efekty

| Nakoupený díl | Počet | Rozměr a zdroj | Poznámka k rozvržení |
| --- | ---: | --- | --- |
| ESP32-S3 N16R8, Drátek 232701, konektor OV5640 | 1 | **57 × 28 × 13** – [Drátek](https://dratek.cz/arduino-platforma/232701-vyvojova-deska-esp32-s3-s-konektorem-pro-kameru-ov5640-wifi-bluetooth.html) | Ponechat přístup k USB a kamerovému konektoru. |
| PCA9685, Drátek 1686 | 2 | **25 × 70 × 15 – údaj majitele**, nahrazuje katalogový půdorys 60 × 25 z původní rešerše | Druhá deska je rezerva. Další měření není podmínkou rozvržení. |
| Polohovací mikroservo 180°, Drátek 897 | 6 | Tělo **22 × 11,5 × 27**, kabel **150** – [Drátek](https://dratek.cz/arduino-platforma/897-eses-servo-motor-9g.html). Páka podle majitele: **35 oboustranná / 20 jednostranná** | Oboustranná páka má upevnění/osu uprostřed délky 35; jednostranná na konci délky 20. Nejde o rozměr montážních uší těla ani rozteč otvorů. Další rozměry majitel pro rozvržení nepožaduje. |
| M5Stack U059 N20 + Grove | 6 | **32 × 24 × 8**, kabel **200** – [M5Stack](https://docs.m5stack.com/en/unit/Unit_Vibrator) | Majitel zvolil údaj výrobce. Odlišný údaj prodejce se pro rozvržení nepoužívá; další měření není podmínkou. |
| WS2812B, 30 LED/m, IP65 | 1 role | Délka **5 000**, šířka **10** – [Botland](https://botland.cz/adresovatelne-led-pasky/5447-rgb-led-pasek-ws2812b-digitalni-adresovany-ip65-30-led-m-9w-m-5v-5m-5904422303068.html) | Plánovaných 6×200 je délka použitých úseků. Tloušťku majitel pro rozvržení nepožaduje. |
| Adafruit Pixel Shifter 6066 | 1 | **25,5 × 15 × 10,2** – [Adafruit](https://www.adafruit.com/product/6066) | Použít katalogovou obálku a ponechat prostor pro vodiče. |
| Adafruit ICS43434 6049 | 1 | Půdorys desky **16,51 × 12,7** – [Botland](https://botland.cz/mikrofony-a-detektory-zvuku/26730-ics43434-mems-mikrofon-s-rozhranim-i2s-adafruit-6049-5904422388041.html), [Adafruit](https://www.adafruit.com/product/6049) | Výšku majitel pro rozvržení nepožaduje. Akustický port ponechat volný. |
| DFRobot DFR0954 / MAX98357A | 2 | Půdorys **18 × 18** – [DFRobot](https://wiki.dfrobot.com/dfr0954/) | Výšku majitel pro rozvržení nepožaduje. Každý zesilovač patří k jednomu reproduktoru. |
| Waveshare 14595, 8 Ω / 5 W | 2 | Vnější rozměry jednoho kusu **100 × 45 × 21** – [Waveshare](https://www.waveshare.com/product/accessories/8ohm-5w-speaker.htm) | Další údaje o otvorech a přírubách majitel pro rozvržení nepožaduje. |
| MCP23017 Adafruit 5346 / ADA-21037 | 1 | **43 × 18 × 5** – [Adafruit](https://www.adafruit.com/product/5346) | Použít katalogovou obálku a ponechat prostor pro vodiče. |

## Napájení a ovládací prvky

| Nakoupený díl | Počet | Rozměr a zdroj | Poznámka k rozvržení |
| --- | ---: | --- | --- |
| USB-C PD modul s přepínačem, kód 1782992576 | 1 | **28 × 14 × 11** – [Drátek 232511](https://dratek.cz/arduino-platforma/232511-power-modul-usb-c-pd-s-nastavitelnym-napetim-a-prepinacem.html) | Přesah USB kabelu/vodičů a prostor pro přepínač. |
| Joy-IT SBC-Buck02 / SEG-28582 | 1 | **31 × 46 × 19** – [Joy-IT](https://joy-it.net/en/products/SBC-Buck02) | Ponechat prostor pro vodiče a chlazení. Toto je koupený měnič, starší XY3606 se do inventáře nepřidává. |
| Bíle podsvícené momentové PTT, Drátek 121714 | 1 | **14 × 14 × 35 – údaj majitele**. Průměr závitu **12** – [Drátek](https://dratek.cz/arduino-platforma/121714-led-vodotesny-spinac-12mm-bile-podsviceni.html) | Pro rozvržení použít obálku majitele. Závit Ø12 není potvrzený průměr montážního otvoru. |
| Červeně podsvícená kolébka | 2 | Původní varianta [Drátek 1242](https://dratek.cz/arduino-platforma/1242-prepinac-kolebkovy-led-dioda-cervena.html): **24 × 24 × 30**, otvor **Ø19** (3/4″ podle prodejce) | Jedna je rezerva. Další měření majitel pro rozvržení nepožaduje; katalogový otvor se týká odkazované varianty. |
| Zeleně podsvícená kolébka | 1 | Odpovídající katalogová varianta [Drátek 1241](https://dratek.cz/arduino-platforma/1241-prepinac-kolebkovy-led-dioda-zelena.html): **24 × 24 × 30**, otvor **Ø19** | Další měření majitel pro rozvržení nepožaduje; katalogový otvor se týká odkazované varianty. |
| Tepelný vratný jistič 5 A, Drátek 232509 | 1 | **45 × 28 × 13**, otvor **Ø10,5–12**, max. tloušťka montáže **6**, tlačítko **7,3** – [Drátek](https://dratek.cz/arduino-platforma/232509-nadproudovy-tepelny-jistic-vratna-pojistka-5a.html) | Použít katalogovou obálku. Usazení do panelu patří ke zkoušce sestavy, neblokuje rozvržení. |
| Kondenzátor 470 µF / 50 V | 10 | Válcové pouzdro **Ø10 × 17** – [Drátek 7826](https://dratek.cz/arduino-platforma/7826-kondenzator-470uf-50v.html) | Rozteč a délka vývodů; finální výška po montáži. |
| Degson 5pin páčková rychlosvorka | 10 pořízených, dosud nedoručených | Vnější rozměry neuvedené – [Drátek 177181](https://dratek.cz/arduino-platforma/177181-rychlosvorka-bezsroubova-degson-5pin.html) | Podle majitele nebrání rozvržení kufru; před rozvržením se jejich měření nepožaduje. |

## Kabely, montážní materiál a nástroj

| Nakoupený díl | Množství | Dohledaný rozměr a zdroj | Omezení |
| --- | ---: | --- | --- |
| USB-C → USB-C 60 W / 3 A | 1 | Historicky vybraný [Baseus](https://dratek.cz/arduino-platforma/180410-baseus-napajeci-kabel-usb-c-na-usb-c-60w-1-metr-cerno-sedy.html): délka **1 000** | Katalogový model; přesný model kusu inventář neuvádí. Další měření není podmínkou rozvržení. |
| USB-A → USB-C datový kabel | 1 | Historicky vybraný [Drátek](https://dratek.cz/arduino-platforma/180220-datovy-a-nabijeci-kabel-usb-c-usb-a-1m-cerny.html): délka **1 000** | Další měření není podmínkou rozvržení; ponechat přístup k USB desky. |
| Dupont M-F, 40 vodičů | 2 balení | Délka **200**, rozteč **2,54** – [JustPi/Botland](https://botland.cz/spojovaci-vodice-samice-samci/19621-sada-propojovacich-kabelu-justpi-samice-samec-20cm-40ks-5903351243025.html) | Přesah nasazených plastových konektorů není specifikovaný. |
| Dupont F-F, 40 vodičů | 2 balení | Délka **200**, rozteč **2,54** – [JustPi/Botland](https://botland.cz/spojovaci-vodice-samice-samice/19620-sada-propojovacich-kabelu-justpi-samice-samice-20cm-40ks-5903351243032.html) | Přesah nasazených plastových konektorů není specifikovaný. |
| JST PH 2pin, DNG-06563 | 2 | Kabel **100**, rozteč **2,0** – [Botland](https://botland.cz/napajeci-kabely-a-konektory/6563-jst-prima-2kolikova-zastrcka-roztec-20-mm-s-dratem-5904422334789.html) | Rozteč není šířka konektoru. Další měření není podmínkou rozvržení. |
| CYH 2×0,50 mm² | 1 objednávková položka | [Drátek](https://dratek.cz/arduino-platforma/3024-dvojlinka-cyh-2x0-5-cernorudy.html) uvádí výrobce TEKABEN; jeho [katalog u prodejce](https://dratek.cz/docs/produkty/1/1863/651_595.pdf) má jmenovitý plášť **2,10 × 4,50** | Informativní katalogový údaj; doručená délka není doložená. Další měření majitel pro rozvržení nepožaduje. |
| CYH 2×1,50 mm², VM00VH-H | 1 objednávková položka | [Drátek](https://dratek.cz/arduino-platforma/123159-dvojlinka-cyh-2x1-5-cernorudy-vm00vh-h.html) neuvádí vnější obálku | Katalog TEKABEN má pro 2×1,50 hodnotu 2,70 × 5,30, ale shoda varianty není doložená. Další měření majitel pro rozvržení nepožaduje. |
| Smršťovací bužírky | 1 sada | Historicky zvolená [barevná sada Botland 5375](https://botland.cz/smrstovaci-buzirky/5375-sada-smrstovacich-buzirek-100-ks-rozdilne-barvy-5901969414615.html): délka **100**, průměry před smrštěním **1,5 / 2,5 / 4 / 6 / 10 / 13** | Inventář neuvádí přesné provedení sady; ověřit shodu, průměry nejsou finální tloušťka izolovaného spoje. |
| Univerzální deska | 2 | **50 × 70**, rozteč pájecích otvorů **2,54** – [Botland 2745](https://botland.cz/univerzalni-desky/2745-univerzalni-oboustranny-plosny-spoj-50x70mm-5904422303303.html) | Tloušťka a montážní otvory neuvedené; nejde o výšku budoucí osazené desky. |
| Rezistor 330 Ω / 0,25 W | 10 | Pouzdro **Ø2,4 × 6,3** – [Drátek](https://dratek.cz/arduino-platforma/7655-rezistor-330r-0.25-w-1.html) | Bez vývodů; montážní rozteč závisí na jejich ohnutí. |
| Rezistor 100 kΩ / 0,25 W / 1 % | 20 | Pouzdro **Ø2,4 × 6,3** – [Drátek](https://dratek.cz/arduino-platforma/7653-rezistor-100k-0.25-w-1.html) | Bez vývodů. |
| Rezistor 15 kΩ / 0,25 W / 1 % | 10 | Pouzdro **Ø2,4 × 6,3** – [Drátek](https://dratek.cz/arduino-platforma/174960-metalizovany-rezistor-15k-0-25w-1.html) | Bez vývodů. |
| Rezistor 22 kΩ / 0,25 W / 1 % | 10 | Pouzdro **Ø2,4 × 6,3** – [Drátek](https://dratek.cz/arduino-platforma/174961-metalizovany-rezistor-22k-0-25w-1.html) | Bez vývodů. |
| Multimetr PROSKIT MT-1210 | 1 | **147 × 74 × 35** – [výrobce Pro'sKit](https://www.proskit.com/Product/MT-1210?hl=en-US) | Servisní nástroj, nezapočítávat jako trvale instalovaný díl kufru. |

## Objednaná kamera a vybraná powerbanka

| Díl | Stav | Rozměry |
| --- | --- | --- |
| Kamera pro OV5640 konektor ESP32 | Objednaná, nedoručená; přesný kód/provedení chybí | Rozměry skutečně objednané varianty nejsou doložené. Označení senzoru OV5640 neurčuje obálku modulu ani FPC; držák kamery se dopřesní podle doručeného modulu, neblokuje rozvržení ostatních dílů. |
| UGREEN PB720 / 25188, 20 000 mAh / 100 W | Zvolený model; rozměr potvrzený majitelem | **139 × 80 × 26,5**, potvrzeno majitelem, shodně [UGREEN, SKU 25188](https://eu.ugreen.com/en-fi/products/25188). **Musí jít jednoduše vyjmout.** Použít otevřený držák s uvolnitelnými pásky, přístupem k portům a volnou cestou pro vyjmutí bez demontáže okolních dílů. Starší větší obálka CAD není rozměrem tohoto kusu. |

## Rozvržení podle rozhodnutí majitele

Tento soubor je společný podklad pro rozměry kufru, elektroniky,
servopák a powerbanky. Pro rozvržení používat zde uvedené údaje.
Svorky čekající na doručení, další výšky mikrofonu/zesilovačů,
montážní otvory reproduktorů, podrobnosti kolébek, LED a kabelů ani
další údaje o výztuze nejsou podle majitele podmínkou rozvržení.
Původní seznam požadovaných měření se ruší; nevydávat neuvedené
rozměry za změřené a nevymýšlet rozteče otvorů.

Powerbanku musí jít snadno vyjmout z otevřeného držáku po uvolnění pásků
a odpojení kabelu; držák a okolní díly nesmějí cestu vyjmutí blokovat.
Fyzické usazení, pohyb, elektrické kontroly a individuální kalibrace
se ověřují na sestavě podle plánu. Samotná aktualizace dokumentace
nemění CAD ani jeho starší pracovní obálky.

Další postup a fyzická akceptace zůstávají ve
[stavu implementace](../docs/stav-implementace.md) a
[implementačním plánu](../docs/implementacni-plan.md).
