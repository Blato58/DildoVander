# Objednávka chybějící elektroniky kufru – 6. 10. 2026

**Zjednodušeno 8. 10. 2026:** objednat jen tabulky 1 a 2. Počítáme
s jedním už koupeným zesilovačem a reproduktorem, připájenými kabely
a kamerou, kterou majitel zvolil 6. 10. Automatické odpojení pohonů
je odložené; tento košík je pro nezatížený laboratorní prototyp pod
dohledem, nikoli kompletní hardware pro provoz s publikem.

**Objednávat podle tohoto souboru, nikoli podle historického nákupního seznamu.**
Seznam odečítá díly doložené fakturou Botland FOS_3325_09_2026 a fotografií
faktury Drátku. Nový provozní limit majitele: svítí nejvýše jeden 20cm LED
úsek a běží nejvýše **jedno servo NEBO jedna vibrace**. LED a pohon se mohou
překrývat. Kamera, mikrofon, Wi-Fi a zvuk jsou zahrnuté v návrhu napájení.

Dva obchody: **Botland + Drátek**. Součty používají orientační ceny
z 6. 10. 2026; nejde o nové ověření všech cen ani skladů. Doprava
a mechanika nejsou zahrnuté. Nákup součástek
není doklad hotového zapojení: montážní kontroly jsou v
[návrhu napájení](../kufr/docs/efekty-a-napajeni.md).

## 1. Botland – objednat

| Hotovo | Položka / přímý odkaz | Objednat | Cena za jednotku | Celkem | K čemu |
| --- | --- | ---: | ---: | ---: | --- |
| ☐ | [Akyga AK-CH-31 USB-C PD, 65 W](https://botland.cz/plug-in-napajeci-zdroje/27953-napajeci-zdroj-s-usb-typu-c-a-usb-typu-a-5-20v-135-3a-akyga-ak-ch-31-5906708750672.html) | 1 ks, jen pokud nemáme vhodný zdroj | 387 Kč | 387 Kč | Vynechat, máme-li USB-C PD zdroj/powerbanku s 20 V alespoň 1,5 A, lépe 2 A |
| ☐ | [Joy-IT měnič 9–35 V → pevně 5 V / 5 A](https://botland.cz/step-down-prevadece/28582-snizujici-menic-9v-35v-na-5v-5a-4250236824017.html) | 1 ks | 148 Kč | 148 Kč | Jediný měnič, připojení na šroubové svorky |
| ☐ | [JST PH 2,0 mm, dvoupinová zástrčka s vodiči](https://botland.cz/napajeci-kabely-a-konektory/6563-jst-prima-2kolikova-zastrcka-roztec-20-mm-s-dratem-5904422334789.html) | 1 ks | 11 Kč | 11 Kč | Jeden reproduktor do už koupeného zesilovače; vynechat, pokud vhodný kabel už máme |
| ☐ | [Modul USB-A zásuvky s připájenými piny](https://botland.cz/prislusenstvi-kontaktnich-desek/16154-modul-se-zasuvkou-usb-typu-a-pajene-konektory-5904422300692.html) | 1 ks | 21 Kč | 21 Kč | Výstup 5 V pro koupený USB-A → USB-C kabel k ESP32 |
| ☐ | [Adafruit 5346 MCP23017, modul GPIO expanderu](https://botland.cz/expandery-vystupu/21037-modul-mcp23017-gpio-pin-expander-16kanalovy-i2c-stemma-qt-qwiic-adafruit-5346.html) | 1 ks | 197 Kč | 197 Kč | Pomocné I/O pro kamerový profil B; napájet 3,3 V |

**Botland: 764 Kč se zdrojem / 377 Kč s vlastním vhodným zdrojem.**
U MCP23017 počítat s připájením přibalené lišty. Neobjednávat holý čip
místo modulu. MCP zůstává potřebný pro zvolený kamerový profil B;
PCA9685 neumí nahradit jeho tlačítkové a stavové vstupy. Kamera i MCP
lze odložit společně jen při vědomém návratu k profilu A bez kamery.

## 2. Drátek – objednat

| Hotovo | Položka / přímý odkaz | Objednat | Cena za jednotku | Celkem | K čemu |
| --- | --- | ---: | ---: | ---: | --- |
| ☐ | [Sada ESP32-S3 N16R8 + kamera OV5640](https://dratek.cz/arduino-platforma/232702-vyvojova-deska-esp32-s3-s-kameru-ov5640-wifi-bluetooth-nespajeny-s-pinovou-listou-v-baleni.html) | 1 sada | 479 Kč | 479 Kč | Kamera z varianty stejné řady desek; další ESP32 jako rezerva |
| ☐ | [Dvojlinka CYH 2×0,5 mm²](https://dratek.cz/arduino-platforma/3024-dvojlinka-cyh-2x0-5-cernorudy.html) | 5 m | 13 Kč/m | 65 Kč | Krátké napájecí odbočky; množství 5 znamená 5 metrů |
| ☐ | [Dvojlinka CYH 2×1,5 mm²](https://dratek.cz/arduino-platforma/123159-dvojlinka-cyh-2x1-5-cernorudy-vm00vh-h.html) | 2 m | 26 Kč/m | 52 Kč | Hlavní 5V rozvod a přívod k pohonům |
| ☐ | [Degson páčková rychlosvorka, 5pin](https://dratek.cz/arduino-platforma/177181-rychlosvorka-bezsroubova-degson-5pin.html) | 10 ks | 10 Kč | 100 Kč | Rozbočení napájení, každý blok propojuje všech pět otvorů |
| ☐ | [Dupont F-F, 40 vodičů](https://dratek.cz/arduino-platforma/835-eses-40-x-f-f-dupont-kabel.html) | 1 balení | 31 Kč | 31 Kč | Signály mezi osazenými samčími piny |
| ☐ | [Kolébkový vypínač s červenou LED](https://dratek.cz/arduino-platforma/1242-prepinac-kolebkovy-led-dioda-cervena.html) | 1 ks navíc | 18 Kč | 18 Kč | Samostatný servisní vypínač pohonů; hlavní kolébku už máme |
| ☐ | [Rezistor 100 kΩ / 0,25 W / 1 %](https://dratek.cz/arduino-platforma/7653-rezistor-100k-0.25-w-1.html) | 10 ks | cca 1 Kč | cca 10 Kč | Šest motorových DIN, PCA OE a výchozí stavy řízení; drobná montážní rezerva |
| ☐ | [Rezistor 15 kΩ / 0,25 W / 1 %](https://dratek.cz/arduino-platforma/174960-metalizovany-rezistor-15k-0-25w-1.html) | 5 ks | cca 1 Kč | cca 5 Kč | Pull-up PTT a horní odpor děliče servisního vypínače; drobná rezerva |
| ☐ | [Rezistor 22 kΩ / 0,25 W / 1 %](https://dratek.cz/arduino-platforma/174961-metalizovany-rezistor-22k-0-25w-1.html) | 5 ks | cca 1 Kč | cca 5 Kč | Spodní odpor děliče 5 V → cca 2,97 V; drobná rezerva |

**Drátek: přibližně 765 Kč. Celkem: přibližně 1 529 Kč se zdrojem,
nebo 1 142 Kč s vlastním vhodným zdrojem, bez dopravy.**
Proti původnímu základu 1 999 Kč jde o úsporu cca 470 Kč. Kabely a svorky
jsou montážní odhad pro šest přihrádek; přesnou spotřebu určí délky tras.
Rezistory nevyhazovat spolu s watchdogem: drží definované úrovně a dělič
chrání 3,3V vstup. Ani samotný pull-up OE nenahrazuje automatické vypnutí.

Kamera: nekupovat náhodný modul pouze podle označení OV5640 ani Raspberry Pi
kameru. Stejný senzor nezaručuje stejný FPC konektor a pořadí pinů. Sada je
doložená nabídka kamerové varianty naší řady; před zasunutím porovnat revizi,
konektor a orientaci kontaktů obou desek. Při rozdílu použít párovanou novou
desku se sadou a původní ponechat jako rezervu. Prodloužení FPC zatím
nekupovat: nejjednodušší je umístit desku poblíž objektivu.

## 3. Teď neobjednávat – volitelné doplňky

| Stav | Položka / odkaz | Původní množství | Cena celkem | Proč vynechat |
| --- | --- | ---: | ---: | --- |
| Vynechat | [Grove → čtyři šroubové svorky](https://botland.cz/grove-prevadece/11348-grove-4pinovy-sroubovy-konektor-5904422340063.html) | 6 ks | 258 Kč | Pohodlí při montáži. Lze použít koupené Grove kabely: jeden konec upravit, vodiče napojit pájením a izolovat; konektor u U059 zachovat |
| Vynechat | [JST-SM třípinová zástrčka + zásuvka s vodiči](https://botland.cz/prislusenstvi-pro-led-pasky/11125-konektor-pro-led-pasky-a-pasky-jst-sm-3kolikove-sparkfun-cab-14575-5904422317249.html) | 6 párů | 390 Kč | Pohodlné odpojení LED. Pro funkci stačí připájené a izolované vodiče, se zachovaným DIN/DOUT |
| Vynechat | [Goodram M1A4 microSD 32 GB + adaptér + USB čtečka](https://botland.cz/pametove-karty-microsd-sd/6946-goodram-all-in-one-m1a4-32-gb-100-mb-s-microsd-pametova-karta-trida-10-adapter-ctecka-otg-5908267930274.html) | 1 sada | 274 Kč | Volitelné lokální assety. Boot, základní hlas a kamera kartu nepotřebují; základní nouzové hlášky jsou podle plánu ve flash |
| Vynechat | [DFRobot DFR0954 MAX98357A](https://botland.cz/mp3-wav-ogg-midi-prehravace/21992-modul-zesilovace-zvuku-max98357-dfrobot-dfr0954-6959420922703.html) + druhý PH2 kabel | 1 + 1 ks | 164 Kč | Potřebné až pro druhý reproduktor. Pro základní mono hlas už máme jeden zesilovač a reproduktor |

Oproti původní objednávce se všemi doplňky za 2 921 Kč je nynější
košík se zdrojem levnější přibližně o **1 392 Kč**. Druhý reproduktor
ponechat jako rezervu; výstupy zesilovačů nikdy neparalelizovat.

Grove napájí jednotlivý modul, nikoli celou motorovou větev. Do páčkových
svorek nedávat tenké Dupont vodiče: Degson je určený od 0,2 mm². Pro LED
nepoužívat tlakové klipsy bez ověření rozteče a silikonového krytu IP65;
vybrané konektory se jednou připájí a zaizolují.

### Odložené automatické vypnutí pohonů

CD4093BE sám proud pohonů nespíná: obsahuje čtyři NAND hradla
([TI](https://www.ti.com/product/CD4093B)). V návrhu skládá povolení
z běhu firmware, watchdogu a servisní kolébky a ovládá PCA OE i silový
spínač. Proto se z aktuálního košíku odkládá **celá trojice**, nejen čip:

| Teď neobjednávat | Původní množství | Orientační cena | Role v úplném návrhu |
| --- | ---: | ---: | --- |
| [Pololu 2801 RC Switch](https://botland.cz/radiove-moduly/3733-rc-prepinac-s-digitalnim-vystupem-pololu-2801-5904422383398.html) | 1 ks | 120 Kč | Sledování heartbeat; podle [výrobce](https://www.pololu.com/docs/0J60/all) ztráta aktivity po 130 ms |
| [Pololu 2814 MOSFET Slide Switch MP](https://botland.cz/elektronicke-a-digitalni-spinace/5119-velky-posuvny-spinac-mosfet-mp-45-40v-8a-s-ochranou-proti-zpetnemu-proudu-pololu-2814-5903351244893.html) | 1 ks | 107 Kč | Automatické odpojení 5V napájení pohonů |
| [CD4093BE](https://botland.cz/logicke-obvody/17147-logic-cd4093be-4xnand-se-schmittovou-spousti-5ks-5904422327019.html) | původně balení 5 ks | 49 Kč | Logika povolení; majitel hlásí nedostupnost, nepředepisujeme náhradu bez kontroly úrovní |
| [100 nF / 50 V](https://dratek.cz/arduino-platforma/7829-kondenzator-100nf-50v.html) | původně 10 ks | 10 Kč | Blokování pomocné logiky; pro samotný CD4093 stačí jeden přímo u napájení |

Ruční vypnutí hlavní kolébkou nebo vytažení USB-C zastaví přívod energie,
ale až po zásahu člověka. Při zamrznutí ESP32 může samostatná PCA9685 dál
vydávat poslední nastavené impulzy; tepelný jistič při běžném proudu
nezasáhne. Vytažení kabelu také nezaručuje bezpečnou polohu mechaniky.
Odklad tedy umožňuje pokračovat v laboratorním ověřování, **neuzavírá E5/B1
a nemění požadavek nezávislého watchdogu pro provoz s publikem**.
Tento seznam není schéma pro obejití inhibit ve firmware. Levná druhá
kolébka a snímání jejího stavu zůstávají pro servis; automatický Stop
při zamrznutí nenahrazují.

## 4. Už koupené – znovu neobjednávat

| Díly | Doložené množství | Použití |
| --- | ---: | --- |
| ESP32-S3 N16R8, bez kamery | 1 | Hlavní řadič, osazené samčí lišty |
| PCA9685 | 2 | Jedna deska stačí pro 6 serv + 6 digitálních motorových vstupů, druhá jako rezerva |
| Polohovací mikroservo 180° | 6 | Vždy jen jedno aktivní; ostatní nemusí silou držet polohu |
| M5Stack U059 N20 včetně Grove kabelů | 6 | Vždy jen jedna vibrace; výkonový driver už obsahuje modul |
| WS2812B 5 V, 30 LED/m, IP65 | 1 role 5 m | Použijeme 6×20 cm, zbytek rezerva |
| Adafruit Pixel Shifter 6066 | 1 | Datová linka RGB 3,3 → 5 V |
| Adafruit ICS43434 | 1 | Mikrofon, napájení 3,3 V |
| DFRobot DFR0954 | 1 | Stačí pro základní mono hlas, druhý nyní nekupovat |
| Waveshare 14595, 8 Ω / 5 W | 1 pár | Použít jeden, druhý rezerva |
| USB-C PD modul s přepínačem, Drátek 1782992576 | 1 | 20 V, max. 3 A; pro nový návrh vyhovuje |
| USB-C → USB-C 60 W / 3 A | 1 | Zdroj → PD modul; nový 5A kabel netřeba |
| USB-A → USB-C datový kabel | 1 | 5V USB-A zásuvka → USB-UART ESP32 |
| Bíle podsvícené momentové tlačítko | 1 | **Zahájení rozhovoru / PTT**, nikoli hlavní napájení |
| Červeně podsvícená kolébka | 1 | **Hlavní vypínač celého 5V rozvodu**, za měničem |
| Tepelný vratný jistič 5 A | 1 | Hlavní 5V přívod; není watchdog ani okamžité elektronické proudové omezení |
| Rezistory 330 Ω | 10 | RGB data, pomocné sériové odpory |
| Kondenzátory 470 µF / 50 V | 10 | Dvojice paralelně u LED, pohonů a elektroniky |
| Dupont M-F 20 cm, 40 vodičů | 2 balení | Signály; nejsou náhrada F-F |
| Smršťovací bužírky | 1 sada | Izolace pájených spojů |
| Univerzální desky 50×70 mm | 2 | Pomocná rozhraní, později bezpečnostní logika |

Neobjednávat Pololu D24V90F5, 100W zdroj, Adafruit 5807 ani hlavní tlačítkový
vypínač Pololu 2813 z dřívějšího návrhu. Pololu **2814** měl jinou roli:
automatické odpojení pohonů, nyní je spolu s watchdogem odložený.
Další momentové tlačítko není
pro nynější ovládání potřeba.

## 5. Co ještě zajistit pro montáž a provoz

- Páječku a multimetr řeší majitel mimo tento košík. Připravit také pájecí
  drát s tavidlem, odizolovací a štípací nástroj; není doloženo, že jsou doma.
- Vývody obou kolébek lze připájet a izolovat koupenou bužírkou. Fastony
  ani krimpovací kleště tedy nejsou nutné pro tento způsob montáže.
- Držáky desek, serv, kamery a reproduktorů, šrouby a mechaniku přihrádek
  určit podle skutečných otvorů a prostoru kufru; rozměry dosud nejsou
  změřené. Malé moduly bez montážních otvorů umístit do izolačního držáku,
  nevrtat skrz jejich PCB. Nezakrýt chlazení měniče ani mikrofonní otvor.
- Zajistit existující 2,4GHz Wi-Fi s internetem (router/hotspot pro kufr).
  Nový router se automaticky nekupuje; není nutný pro místní offline řízení.
- Kamera vyžaduje profil B a ověření GPIO2/48, ne jen zasunutí modulu.
  Ovládání hardwaru a watchdogu ještě není implementované/změřené.
- Nynější košík není kompletní kusovník automaticky chráněných pohonů.
  Zbývá ověřit montážní rozměry, proudy, OE, resetové stavy a audio mute
  rozhraní podle technického dokumentu; případné doplnění dílů určit ze
  skutečného schématu a měření, ne z úspěšného nákupu.
