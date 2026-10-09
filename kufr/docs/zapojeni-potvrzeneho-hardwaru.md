# Zapojení potvrzeného hardwaru – 9. 10. 2026

Pro současný kufr použijeme **jednu ESP32-S3 N16R8, jednu PCA9685,
MCP23017, šest serv, šest U059, 36 WS2812B, dva DFR0954 a oba 8Ω
reproduktory**. Druhá PCA je rezerva. Zakoupené díly a přesné počty jsou
v [potvrzené objednávce](../../docs/objednavka-doplneni-kufru-2026-10-06.md).
Powerbanka UGREEN PB720 / 25188 je vybraná v předchozím zadání.

Zvolený implementovaný profil je **B-effects-no-SD**. Audio používá
nekamerové piny, takže kamera může zůstat u ESP v zadním držáku.
Slot microSD zůstává prázdný: GPIO38 nyní čte stav nezávislého watchdogu.
Původní profil B se současnou microSD je v plánu návrh další varianty;
tento firmware jej nepovoluje. Kamerový driver/snímek E13 není touto
implementací efektů dokončený a koupi samotné OV5640 nové snímky nedokládají.

Pinout je podložený dodanou mapou a komunikačním testem PCA D124,
ne měřením všech spojů. Firmware vyžaduje servisní záznam revize/zapojení,
kalibrace a inhibit; prázdná NVS žádný pohyb nepovolí.

## Napájení a vypínače

```text
UGREEN USB-C PD (20 V)
  → koupený C–C kabel 60 W / 3 A
  → koupený PD trigger, nastavit 20 V
  → Joy-IT SEG-28582, vstup 9–35 V, výstup 5 V / max. 5 A
  → vratný jistič 5 A
  → červená hlavní kolébka
  → 5V hvězdicový rozvod
      ├─ ESP32 přes USB-UART napájení
      ├─ WS2812B + Pixel Shifter
      ├─ DFR0954 č. 1 + vlastní reproduktor
      ├─ DFR0954 č. 2 + vlastní reproduktor
      └─ zelená servisní kolébka
          → nezávislý inhibit/load switch
          → PCA V+ a červené vodiče šesti U059
```

3V3 ESP napájí logiku PCA **VCC**, MCP **VIN/VCC** a mikrofon.
PCA **V+** je oddělených 5 V pro serva. Všechny GND propojit; společnou
zem pohonů nevypínat. 20 V vede pouze do vstupu měniče. Nejdřív změřit
jeho výstup a polaritu, teprve potom připojit elektroniku.

Hlavní rozvod: koupená dvojlinka 1,5 mm²; krátké odbočky: 0,5 mm².
Délky koupených dvojlinek přeměřit. Degson spojuje všech pět pozic:
samostatné bloky pro +5 V, spínaných +5 V a GND. Jeden blok nikdy nesmí
obsahovat oba póly. Dupont použít na signály. Ze zásoby 470 µF použít dvě
paralelně u LED, dvě u pohonů a dvě u audio/elektroniky, dodržet polaritu.
U pohonů změřit vybití po vypnutí, kapacita může krátce držet energii.

Nová zelená kolébka je servisní vypínač; původní červená hlavní vypínač,
nová červená rezerva. U tříkontaktové kolébky změřit vstup, spínaný výstup
a LED/GND, nepředpokládat dva spínací póly. LED patří na 5 V, ne 20 V.
Za zelenou kolébkou, před elektronickým switchem, snímat +5 V děličem
15 kΩ nahoře / 22 kΩ dolů na GND. Jeho uzel má nominálně 2,97 V.

USB-UART je ověřený servisní konektor. Při připojení PC odpojit externí
napájení ESP, dokud není prokázaná ochrana proti backfeedu. USB-A napájecí
zásuvka ze starého košíku dosud není na nových snímcích potvrzená.

## Signály ESP32

| GPIO | Kam připojit | Poznámka |
| ---: | --- | --- |
| 1 | PCA SDA + MCP SDA | Jedna koordinovaná I2C sběrnice, 100 kHz |
| 14 | PCA SCL + MCP SCL | Logika/pull-upy výhradně 3,3 V |
| 48 | Pixel Shifter vstup | Od GPIO před onboard LED; externí řetězec má vlastní pixel 0 |
| 2 | RUN_REQUEST do nezávislého inhibit | Přímý GPIO; ověřit osazenou LED a bezpečný LOW při resetu |
| 38 | GOOD watchdogu | 3,3V výstup, 100kΩ pull-down; SD nepoužívat |
| 21 | ICS43434 SCK + oba DFR0954 BCLK | Společné audio hodiny |
| 41 | ICS43434 WS + oba DFR0954 LRC | Philips I2S, 32 kHz / 32bit stereo |
| 42 | ICS43434 SD | Mikrofon 3,3 V, L/R nastavit levý slot |
| 47 | DIN obou DFR0954 | Stejná mono řeč duplikovaná do obou stereo slotů |

Kamerový konektor ponechat podle [pinové mapy](pin-mapa-esp32-s3.md):
4 SIOD, 5 SIOC, 6 VSYNC, 7 HREF, 8 Y4, 9 Y3, 10 Y5, 11 Y2,
12 Y6, 13 PCLK, 15 XCLK, 16 Y9, 17 Y8, 18 Y7.
Neosazovat kamery s neověřeným FPC. GPIO19/20 zůstávají USB;
35–37 PSRAM a ostatní paměťové/strapping piny se nepoužívají.
41/42 nyní nejsou externí JTAG, konzole zůstává přes USB-UART.

## PCA9685 a světla

PCA na **0x40**, VCC 3,3 V, V+ ze spínané pohonové větve.
Kanály **0–5 = serva přihrádek 1–6**, kanály **6–11 = DIN U059 1–6**.
PCA používá 50 Hz. U059 používá pouze digitální FULL_ON/FULL_OFF,
nejde o 10kHz PWM; má vlastní výkonový driver. Červená U059 = 5 V,
černá = GND, žlutá = DIN, bílá = NC. Na DIN každého 100kΩ pull-down.
Servo má signál na příslušném PCA kanálu, napájení V+ a společnou GND.
Pulzy jsou individuální kalibrace, žádný úhel z CAD není univerzální pulz.

PCA OE řídí **nezávislá logika inhibit**, není napevno na GND.
OE má výchozí pull-up na 3,3 V a `OUTNE=00`; ověřit odpory na modulu.
Firmware před novým pohonem stáhne RUN_REQUEST a vymaže všech 16 kanálů,
potom nastaví jediný výstup. Při I2C chybě stáhne RUN_REQUEST přímo,
ještě před dalším pokusem o zápis. Napájení/PWM ostatních serv je vypnuté.

Pixel Shifter napájet a připojit podle potisku modulu. Jeho 5V výstup
vede přes 330 Ω na DIN prvního úseku. Pak DOUT → DIN následujícího:
šest úseků po šesti LED, **36 LED v jediném datovém řetězci**.
Napájení jednotlivých úseků vést z rozvodu, ne přes datové propoje.
Firmware maže celý řetězec a rozsvítí jen vybraných šest LED;
hodnoty RGB jsou omezené na 64/255. Onboard LED48 zrcadlí první pixel,
není vložená před vnější řetězec a neposouvá jeho indexy.

## MCP23017, tlačítko a zpětná vazba

Adafruit 5346 na **0x20**, A0–A2 LOW. VIN/VCC 3,3 V, GND společná.
INT není potřeba. Logické vstupy nepřipojovat k 5 V.

| MCP pin | Funkce | Zapojení |
| --- | --- | --- |
| GPA0 | Bílé momentové tlačítko | Kontakt na GND, 15kΩ pull-up na 3,3 V; LED samostatně |
| GPA1 | Společné povolení obou zesilovačů | HIGH = zvuk, LOW = mute; rozhraní níže |
| GPA2 | RC IN nezávislého watchdogu | 100kΩ pull-down, jednotlivý softwarově vyžádaný pulz |
| GPA3 | PHYSICAL_ENABLE | Uzel děliče 15 kΩ / 22 kΩ za zelenou kolébkou |
| GPA4–GPA7, GPB0–GPB1 | Horní kontakty přihrádek 1–6 | NO kontakt na GND v horní poloze, aktivní LOW |
| GPB2–GPB7 | Spodní kontakty přihrádek 1–6 | NO kontakt na GND ve spodní poloze, aktivní LOW |

**Krajní kontakty nejsou v potvrzeném nákupu.** Pro tuto realizaci doplnit
12 vhodných mikrospínačů nebo ekvivalentní dvojí koncové snímání.
MCP poskytuje pull-upy; přerušený vodič se čte jako nepotvrzená poloha.
Současně aktivní horní i spodní kontakt je chyba. Potvrzení polohy musí být
stabilní 30 ms. Špatně připojený či zkratovaný kontakt tento jednoduchý
systém nedokáže vždy diagnostikovat; ověřit každý kontakt i cestu z jedné
polohy do druhé, mechanické dorazy a chování při odpojení.

Stávající 9g serva mají jen napájení a vstup PWM. Nevracejí polohu.
Bez zpětné vazby nelze podle běhu časovače prohlásit hůlku za vrácenou;
proto nejsou tyto snímače nahrazené domnělým časovým potvrzením.
Otevřený/zablokovaný pohyb překračující kalibrované okno do 500 ms skončí
poruchou a ponechá journal nedokončený.

Krátký stisk v klidu spustí/zastaví samostatný hlas, při prvním startu
zvolí GPT-Live/Marin, pokud režim nebyl zvolený. Při aktivní prezentaci
stisk vyvolá Stop. Přidržení 2 s v klidu je místní potvrzení kontroly
všech šesti spodních poloh; teprve potom je možné zotavení po bootu/chybě.
Tlačítko má debounce 30 ms. Odpojení pozorovatelského telefonu nezastaví
autonomní prezentaci; ztráta ručního lease ji v ručním režimu zastaví.

## Dva zesilovače a bezpečný mute

Každý DFR0954 má vlastní 8Ω reproduktor mezi SPK+ / SPK− přes vlastní
PH2 kabel. SPK− není GND, výstupy dvou modulů nikdy nespojovat.
Oba zesilovače sdílejí pouze napájení/GND, I2S vstupy a řízení SD.
GAIN zpočátku ponechat nezapojený, firmware používá konzervativní hlasitost.

[Schéma DFRobot](https://dfimg.dfrobot.com/wiki/20560/DFR0954_max98357a-i2s_schematics_1.0.pdf)
ukazuje R1 mezi SD a VCC. [Wiki výrobce](https://wiki.dfrobot.com/dfr0954/)
popisuje různé hodnoty podle kanálu. Při 5V napájení by tento pull-up
mohl přivést 5 V na vypnutý/nezapojený 3,3V MCP. Vybrané jednoduché
rozhraní je **odpojit pull-up SD→VCC na obou modulech** (ve schématu R1,
na skutečném kusu nejdřív identifikovat měřením), potom na SD každého
přidat **15kΩ pull-down na GND** a spojit oba SD s GPA1.
HIGH 3,3 V volí levý kanál; firmware má stejnou řeč v obou slotech.
LOW musí být pod 0,16 V. Po úpravě proměřit při bootu, resetu MCP,
odpojení 3V3 a zastavení I2S; teprve poté zaznamenat `mute_ok`.

Bez této ověřené úpravy SD k MCP nepřipojovat. Alternativou je zvlášť
navržený převodník úrovní s fail-muted výchozím stavem; obyčejný 100kΩ
pull-down proti osazenému pull-upu nezaručuje ztlumení. Nový firmware
neudržuje I2C zámek ve spinlocku; mute a playback serializuje task mutex.

## Co brání finálnímu oživení

Odložený watchdog, bezpečnostní logika a high-side load switch stále
nejsou koupené/ověřené. Jejich funkce zůstává podle
[návrhu napájení](efekty-a-napajeni.md):
`ACTUATOR_EN = RUN_REQUEST AND GOOD AND PHYSICAL_ENABLE`,
OE je negace povolení. Kandidáti Pololu 2801/2814 a příslušná logika
jsou dřívější návrh; dostupnou náhradu CD4093 se musí elektricky ověřit.
GPIO38 čte GOOD, ale fyzické inhibit funguje i bez jeho čtení.

Heartbeat není PCA/RMT PWM. Po validním průchodu řídicí smyčkou je jednou
za 50 ms vyžádaný jeden asi 1ms pulz na GPA2. Vyhrazená úloha nevytváří
samostatný opakovač; celý pulz drží koordinovaný I2C zámek. Stará žádost,
timeout I2C nebo příliš dlouhý pulz vyvolají přímé vypnutí RUN_REQUEST.
Skutečné časování i vypnutí při zamrznutí HIGH/LOW změřit nezávisle;
multimetr sám nedoloží 250ms limit.

Bez ověřeného inhibit, kontaktů a kalibrace hlavní obraz firmware
pohony nepovoluje. Nákup nových položek není nákup chybějící ochrany.
Pro jednotlivé nezatížené zkoušky pod dohledem zůstává dřívější režim
laboratorního prototypu, nikoli skryté obejití brány v hlavním firmware.

## Servisní zprovoznění

1. S odpojenými pohony ověřit revizi/GPIO2/48/38, I2C adresy a 3,3V logiku,
   napájení, resetové stavy OE/ON/RUN_REQUEST a fail-muted SD obou modulů.
2. Prokázat nezávislé vypnutí, pull rezistory, čas vybití a chybové stavy.
3. Na jednom nezatíženém servu individuálně změřit bezpečné pulzy,
   potom chod mechanismu, kontakty a okno pohybu včetně 30ms potvrzení.
   Doplnit ostatní přihrádky a pasivní držení při ztrátě proudu.
4. Připravit skutečnou konfiguraci podle
   [servisního projektu](../firmware/diagnostics/hardware-service/README.md).
   Příklad má všechny brány vypnuté a nulové pulzy. Servisní obraz mění
   pouze `wand_hw`, nesmí se hromadně vymazat NVS s identitou/TLS.
   Nahrání vyžaduje samostatnou autorizaci; při této implementaci neproběhlo.
5. Obnovit aplikační obraz, ověřit klidný boot a kontrolovat jednu celou
   prezentaci: hledání → zvýraznění → tři vibrace → zdvih → 5 s ukázka
   → návrat → potvrzený spodní kontakt → atomické obnovení dostupnosti.
6. Ověřit Stop v každé fázi, zaseknuté servo, chybějící/zkratovaný kontakt,
   vypnutí zelené kolébky, I2C timeout, restart a ztrátu ručního lease.
   Při Stop se nevrací servo automaticky a nevydává úspěšné dokončení.

Provozní proudový cíl je do 4 A trvale z udávaného 5A měniče.
Teplotu, poklesy napětí a klidový odběr změřit v uzavřené montáži.
Úspěšný build a fake testy nejsou fyzickou akceptací E5/E6/B1/E9/E13.
