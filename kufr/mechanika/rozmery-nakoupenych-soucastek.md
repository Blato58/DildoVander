# Rozměry nakoupených součástek kufru

Rešerše **9. 10. 2026**. Inventář a množství vycházejí z
[potvrzených nákupů](../../docs/objednavka-doplneni-kufru-2026-10-06.md#4-už-koupené--znovu-neobjednávat).
Tabulky pokrývají všech 32 položek tohoto inventáře. Kamera a vybraná,
dosud nákupem nedoložená powerbanka jsou uvedené zvlášť.

Všechny rozměry jsou v **mm**, pokud není uvedeno jinak. Jde o katalogové
údaje ověřené na odkazovaných stránkách, nikoli o měření doručených kusů.
Pořadí os přebíráme ze zdroje; nejde automaticky o osy X/Y/Z modelu.
Rozměry balení nepoužíváme jako rozměry součástky. Kabely, pájené vývody,
montážní vůle a prostor pro manipulaci vyžadují další rezervu.

Kufr má potvrzenou vnitřní obálku spodní části **431,8 × 266,7 × 114,3 mm
(šířka × délka × výška)**. Víko zůstává prázdné;
[příčná výztuha a lokální vůle](rozmery-doruceneho-kufru.md) ještě nejsou změřené.
Tento podklad nemění CAD ani `parametry.json` a nedokládá fyzické usazení.

## Řízení, zvuk a efekty

| Nakoupený díl | Počet | Dohledaný rozměr a zdroj | Co zůstává pro montáž neznámé |
| --- | ---: | --- | --- |
| ESP32-S3 N16R8, Drátek 232701, konektor OV5640 | 1 | **57 × 28 × 13** – [Drátek](https://dratek.cz/arduino-platforma/232701-vyvojova-deska-esp32-s3-s-konektorem-pro-kameru-ov5640-wifi-bluetooth.html) | Zdroj nevymezuje zahrnutí lišt/konektorů; ověřit celý osazený kus, otvory a USB/FPC prostor. |
| PCA9685, Drátek 1686 | 2 | **60 × 25**, výška neuvedená – [Drátek](https://dratek.cz/arduino-platforma/1686-iic-i2c-modulovy-driver-servo-motoru-pro-arduino-pca9685-16-kanalu-12-bit-pwm.html) | Výška se svorkou a zapojenými servo konektory, průměry/rozteče otvorů. Druhá deska je rezerva. |
| Polohovací mikroservo 180°, Drátek 897 | 6 | **22 × 11,5 × 27**, kabel **150** – [Drátek](https://dratek.cz/arduino-platforma/897-eses-servo-motor-9g.html) | Obálka včetně montážních uší a nasazené páky, otvory a poloha osy; katalog nedává jejich samostatné rozměry. |
| M5Stack U059 N20 + Grove | 6 | Výrobce **32 × 24 × 8**, kabel **200** – [M5Stack](https://docs.m5stack.com/en/unit/Unit_Vibrator); prodejce uvádí **48 × 24 × 12** – [Botland](https://botland.cz/moduly-jednotek-rozsirujici-moduly/17999-vibracni-motor-n20-rozsirujici-modul-unit-pro-vyvojove-moduly-m5stack-6972934170972.html) | **Rozpor zdrojů: změřit doručený modul**, konektor a oba montážní otvory před návrhem klece. |
| WS2812B, 30 LED/m, IP65 | 1 role | Délka **5 000**, šířka **10** – [Botland](https://botland.cz/adresovatelne-led-pasky/5447-rgb-led-pasek-ws2812b-digitalni-adresovany-ip65-30-led-m-9w-m-5v-5m-5904422303068.html) | Tloušťka včetně silikonového krytu. Plánovaných 6×200 je délka použitých úseků, ne další nákup. |
| Adafruit Pixel Shifter 6066 | 1 | **25,5 × 15 × 10,2** – [Adafruit](https://www.adafruit.com/product/6066) | Prostor pro vodiče ve svorkách; zdroj nepopisuje výškovou referenci ani uchycení. |
| Adafruit ICS43434 6049 | 1 | Půdorys desky **16,51 × 12,7** – [Botland](https://botland.cz/mikrofony-a-detektory-zvuku/26730-ics43434-mems-mikrofon-s-rozhranim-i2s-adafruit-6049-5904422388041.html), [Adafruit](https://www.adafruit.com/product/6049) | Výška s osazenými vývody a přesná poloha akustického portu pro držák. |
| DFRobot DFR0954 / MAX98357A | 2 | Půdorys **18 × 18** – [DFRobot](https://wiki.dfrobot.com/dfr0954/) | Výška s PH2 konektorem a použitým osazením lišty, obálka zapojeného kabelu. Výrobce nabízí mechanický výkres/CAD, ale samotná specifikace výšku neuvádí. |
| Waveshare 14595, 8 Ω / 5 W | 2 | Vnější rozměry jednoho kusu **100 × 45 × 21** – [Waveshare](https://www.waveshare.com/product/accessories/8ohm-5w-speaker.htm) | Montážní příruby, průměry/rozteče otvorů a přesah zapojeného konektoru. |
| MCP23017 Adafruit 5346 / ADA-21037 | 1 | **43 × 18 × 5** – [Adafruit](https://www.adafruit.com/product/5346) | Lišty se dodávají zvlášť; konečná výška závisí na osazení a připojení. |

## Napájení a ovládací prvky

| Nakoupený díl | Počet | Dohledaný rozměr a zdroj | Co zůstává pro montáž neznámé |
| --- | ---: | --- | --- |
| USB-C PD modul s přepínačem, kód 1782992576 | 1 | **28 × 14 × 11** – [Drátek 232511](https://dratek.cz/arduino-platforma/232511-power-modul-usb-c-pd-s-nastavitelnym-napetim-a-prepinacem.html) | Přesah USB kabelu/vodičů a prostor pro přepínač. |
| Joy-IT SBC-Buck02 / SEG-28582 | 1 | **31 × 46 × 19** – [Joy-IT](https://joy-it.net/en/products/SBC-Buck02) | Otvory a výška se zapojenou svorkou; zachovat prostor pro chlazení. Toto je koupený měnič, starší XY3606 se do inventáře nepřidává. |
| Bíle podsvícené momentové PTT, Drátek 121714 | 1 | Průměr závitu **12** – [Drátek](https://dratek.cz/arduino-platforma/121714-led-vodotesny-spinac-12mm-bile-podsviceni.html) | **Celá obálka nenalezená:** průměr límce, délka za panelem včetně kontaktů, délka závitu a matice. Závit Ø12 není ověřený montážní otvor. |
| Červeně podsvícená kolébka | 2 | Původní varianta [Drátek 1242](https://dratek.cz/arduino-platforma/1242-prepinac-kolebkovy-led-dioda-cervena.html): **24 × 24 × 30**, otvor **Ø19** (3/4″ podle prodejce) | U nově koupené kolébky chybí v inventáři přesný kód. Ověřit shodu, hloubku s kontakty a požadovanou tloušťku panelu. Jedna je rezerva. |
| Zeleně podsvícená kolébka | 1 | Odpovídající katalogová varianta [Drátek 1241](https://dratek.cz/arduino-platforma/1241-prepinac-kolebkovy-led-dioda-zelena.html): **24 × 24 × 30**, otvor **Ø19** | Přesný kód koupeného kusu není zapsaný; **rozměry jsou podmíněné shodou varianty**. Změřit před výrobou panelu. |
| Tepelný vratný jistič 5 A, Drátek 232509 | 1 | **45 × 28 × 13**, otvor **Ø10,5–12**, max. tloušťka montáže **6**, tlačítko **7,3** – [Drátek](https://dratek.cz/arduino-platforma/232509-nadproudovy-tepelny-jistic-vratna-pojistka-5a.html) | Zdroj neurčuje zahrnutí kontaktů do obálky; ověřit doručený kus a závit před otvorem. |
| Kondenzátor 470 µF / 50 V | 10 | Válcové pouzdro **Ø10 × 17** – [Drátek 7826](https://dratek.cz/arduino-platforma/7826-kondenzator-470uf-50v.html) | Rozteč a délka vývodů; finální výška po montáži. |
| Degson 5pin páčková rychlosvorka | 10 | **Vnější rozměry nenalezené** – [Drátek 177181](https://dratek.cz/arduino-platforma/177181-rychlosvorka-bezsroubova-degson-5pin.html) | Změřit délku × šířku × výšku se zavřenými páčkami a výšku s otevřenou páčkou. |

## Kabely, montážní materiál a nástroj

| Nakoupený díl | Množství | Dohledaný rozměr a zdroj | Omezení |
| --- | ---: | --- | --- |
| USB-C → USB-C 60 W / 3 A | 1 | Historicky vybraný [Baseus](https://dratek.cz/arduino-platforma/180410-baseus-napajeci-kabel-usb-c-na-usb-c-60w-1-metr-cerno-sedy.html): délka **1 000** | Potvrzený inventář neuvádí přesný model; ověřit délku a rozměr koncovek skutečného kusu. |
| USB-A → USB-C datový kabel | 1 | Historicky vybraný [Drátek](https://dratek.cz/arduino-platforma/180220-datovy-a-nabijeci-kabel-usb-c-usb-a-1m-cerny.html): délka **1 000** | Stejně ověřit shodu a koncovky, potřebné pro přístup k USB desky. |
| Dupont M-F, 40 vodičů | 2 balení | Délka **200**, rozteč **2,54** – [JustPi/Botland](https://botland.cz/spojovaci-vodice-samice-samci/19621-sada-propojovacich-kabelu-justpi-samice-samec-20cm-40ks-5903351243025.html) | Přesah nasazených plastových konektorů není specifikovaný. |
| Dupont F-F, 40 vodičů | 2 balení | Délka **200**, rozteč **2,54** – [JustPi/Botland](https://botland.cz/spojovaci-vodice-samice-samice/19620-sada-propojovacich-kabelu-justpi-samice-samice-20cm-40ks-5903351243032.html) | Přesah nasazených plastových konektorů není specifikovaný. |
| JST PH 2pin, DNG-06563 | 2 | Kabel **100**, rozteč **2,0** – [Botland](https://botland.cz/napajeci-kabely-a-konektory/6563-jst-prima-2kolikova-zastrcka-roztec-20-mm-s-dratem-5904422334789.html) | Rozteč není šířka konektoru. Obálku zapojeného páru ověřit na reproduktoru/zesilovači. |
| CYH 2×0,50 mm² | 1 objednávková položka | [Drátek](https://dratek.cz/arduino-platforma/3024-dvojlinka-cyh-2x0-5-cernorudy.html) uvádí výrobce TEKABEN; jeho [katalog u prodejce](https://dratek.cz/docs/produkty/1/1863/651_595.pdf) má jmenovitý plášť **2,10 × 4,50** | Katalog označuje hodnoty jako informativní. Skutečná doručená délka není doložená; změřit. Průřez mědi není vnější rozměr kabelu. |
| CYH 2×1,50 mm², VM00VH-H | 1 objednávková položka | [Drátek](https://dratek.cz/arduino-platforma/123159-dvojlinka-cyh-2x1-5-cernorudy-vm00vh-h.html) neuvádí vnější obálku | **Změřit plášť i délku.** Katalog TEKABEN má pro 2×1,50 hodnotu 2,70 × 5,30, ale shoda výrobce/varianty tohoto kusu není doložená. |
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
| Kamera pro OV5640 konektor ESP32 | Objednaná, nedoručená; přesný kód/provedení chybí | **Nenalezené pro skutečně objednanou variantu.** Označení senzoru OV5640 neurčuje rozměr celého modulu, objektivu ani FPC. Dodat odkaz objednávky, po doručení změřit modul, objektiv a FPC. |
| UGREEN PB720 / 25188, 20 000 mAh / 100 W | Zvolená; aktuální nákupní podklady koupi nepotvrzují | Výrobce **139 × 80 × 26,5** – [UGREEN, SKU 25188](https://eu.ugreen.com/en-fi/products/25188). Současný CAD používá větší pracovní obálku 139 × 80,8 × 26,7; ta není měřením. Ověřit konkrétní kus a USB kabel za porty. |

## Co potřebujeme změřit

Nejprve chybějící nebo rozporné údaje, které brání přesným držákům:

1. **Degson 5pin:** délka × šířka × výška, navíc výška otevřené páčky.
2. **Bílé PTT:** průměr límce, délka těla za panelem včetně kontaktů,
   délka závitu a rozměr matice.
3. **U059:** celá obálka včetně konektoru, rozteč středů a průměr obou
   otvorů. Výrobce a prodejce uvádějí rozdílnou velikost.
4. **PCA9685, ICS43434 a oba DFR0954:** celková výška po osazení,
   ideálně i s připojenými kabely. U PCA rozteče středů a průměry otvorů.
5. **Serva:** celá šířka včetně uší, rozteč/průměr otvorů, poloha osy
   vůči tělu a výška s vybranou pákou. Stačí jeden reprezentativní kus,
   potom ověřit, že ostatní jsou stejné.
6. **Reproduktory:** rozteče středů a průměry montážních otvorů,
   příruby a prostor za zasunutým PH2 konektorem.
7. **Nová červená a zelená kolébka:** potvrdit variantu nebo změřit
   límec, tělo pro otvor a hloubku včetně kontaktů.
8. **Kabely a LED:** skutečné délky obou dvojlinek, vnější šířka/tloušťka
   1,5mm² dvojlinky, prostor za nasazenými USB koncovkami a tloušťka
   LED pásku včetně silikonového krytu.
9. **Kamera po doručení:** rozměry nosiče, přesah/průměr objektivu,
   délka/šířka FPC a orientace kontaktů; předtím pomůže odkaz objednávky.
10. **Kufr:** výška a šířka příčné výztuhy a její vzdálenost od stěn.
    **Hůlka:** skutečná délka, největší průměr dříku/hlavice a délka
    hlavice; současné průměry 16/40 mm jsou pouze pracovní rezervy.

Před konečným návrhem uchycení doplnit také:

- **ESP32:** půdorys/výšku osazené desky, otvory a přesahy lišt,
  USB a FPC. **Pixel Shifter a MCP23017:** výšku se zapojenými vodiči
  a prostor pro uchycení. **Mikrofon:** polohu akustického portu.
- **Joy-IT a PD modul:** půdorys osazeného kusu a obálku zapojených
  svorek/kabelů; u měniče polohy případných otvorů. Chlazení ponechat
  přístupné, rozměr potřebné tepelné mezery katalog neurčuje.
- **Jistič:** závit, límec a hloubku za panelem včetně kontaktů.
  **Univerzální desky:** tloušťku a polohy/průměry montážních otvorů.
  **Kondenzátory:** rozteč a použitelnou délku vývodů pro osazení desky.
- **Powerbanka, pokud se pořídí zvolená varianta:** vnější obálku
  a prostor za porty se zasunutým kabelem; ověřit skutečné délky obou
  USB kabelů. **Bužírky:** potvrdit sadu a vhodný průměr při izolování;
  přesný rozměr rezistorových vývodů ani multimetru pro držáky kufru
  není potřeba měřit.

Měřit odpojené díly; rozteč otvorů od středu ke středu. K údajům připsat,
zda zahrnují konektor, kabel a servo páku. U dílů s katalogovou obálkou
ověřit shodu doručeného kusu před konečným tiskem; zejména ESP32,
měnič a powerbanka potřebují i rezervu pro zapojení a servis.

Další postup a fyzická akceptace zůstávají ve
[stavu implementace](../docs/stav-implementace.md) a
[implementačním plánu](../docs/implementacni-plan.md).
