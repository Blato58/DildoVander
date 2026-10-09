# Zapojení potvrzeného hardwaru – 9. 10. 2026

Použijeme **jednu ESP32-S3 N16R8, jednu PCA9685, MCP23017, šest serv,
šest U059, 36 WS2812B, dva DFR0954 a oba 8Ω reproduktory**.
Druhá PCA je rezerva. Přesné počty koupených dílů jsou v
[potvrzené objednávce](../../docs/objednavka-doplneni-kufru-2026-10-06.md).
Powerbanka UGREEN PB720 / 25188 je vybraná v předchozím zadání.
Majitel potvrdil rozměr **139×80×26,5 mm** a požaduje jednoduché vyjmutí
bez demontáže okolních dílů. Všechny rozměry jsou v
[jednom přehledu](../mechanika/rozmery-nakoupenych-soucastek.md).

Rozmístění, označení svazků, obsazení tří pracovních svorek a pořadí
montáže určuje [kabelový plán F2](../mechanika/final/KABELAZ.md).
PD trigger, měnič a jistič jsou společně vlevo u powerbanky. Rozbočení
výkonu tvoří samostatné izolované měděné vodiče, nikoli plošné spoje
univerzálních desek. Na jednom portu Degson je jeden odpovídající vodič;
pět společných portů samo neposkytne odbočku pro každou zátěž.
Plán zachovává níže uvedené piny a elektrické sítě; délky jsou montážní
odhady a fyzická přejímka ještě neproběhla.

Implementovaný profil **B-effects** zachovává kamerové a SD piny.
Kamerový driver/snímek E13 a SD driver zatím nejsou dokončené.
Majitel potvrdil stávající desku Drátek 232701 s konektorem OV5640;
samotná kamera je objednaná a čeká na doručení. Druhý řadič se nekupuje.
Firmware řídí koupené pohony bez dalších modulů či polohových snímačů.
Servo polohu odvozuje
z individuálně kalibrovaného pulzu a doby pohybu, **polohu neměří**.
`physicalVerification` ve výsledcích zůstává `unavailable`.

Pinout vychází z dodané mapy a komunikačního testu PCA D124.
Není dokladem měření celé sestavy. NVS vyžaduje ověření zapojení
a individuální kalibraci; prázdná NVS žádný pohyb nepovolí.

**Proudové špičky omezujeme pořadím efektů:** současně smí běžet pouze
jeden pohon celkem, tedy jedno servo nebo jeden U059. Vibrace a pohyb
serva se nepřekrývají. Ostatní servo kanály jsou FULL_OFF; mechanismus
musí polohu držet pasivně. Proud při rozběhu a zatížení se změří na
prototypu, samotné omezení souběhu nedokládá dodržení limitu 5 A.

## Napájení a vypínače

```text
UGREEN USB-C1 PD (12 V / 3 A)
  → koupený C–C kabel 60 W / 3 A
  → koupený PD trigger, nastavit 12 V
  → Joy-IT SBC-Buck02 / SEG-28582, vstup 9–35 V, výstup 5 V / max. 5 A
  → koupený vratný jistič 5 A
  → červená hlavní kolébka
  → 5V hvězdicový rozvod
      ├─ ESP32 přes USB-UART napájení
      ├─ WS2812B + Pixel Shifter
      ├─ DFR0954 č. 1 + vlastní reproduktor
      ├─ DFR0954 č. 2 + vlastní reproduktor
      └─ zelená servisní kolébka
          → přímo PCA V+ a červené vodiče šesti U059
```

3V3 ESP napájí logiku PCA **VCC**, MCP **VIN/VCC** a mikrofon.
PCA **V+** je oddělených 5 V pro serva. Všechny GND propojit;
zem pohonů nevypínat. 12 V vede pouze do vstupu měniče.
Nejdřív změřit výstup měniče a polaritu, potom připojit elektroniku.

Hlavní rozvod: koupená dvojlinka 1,5 mm²; krátké odbočky: 0,5 mm².
Délky určit při montáži kabelových tras; jejich měření neblokuje rozvržení.
Degson je pořízený, čeká na doručení. Spojuje všech pět pozic: samostatné bloky pro
+5 V, +5 V za zelenou kolébkou a GND. Jeden blok nesmí obsahovat oba póly.
Dupont použít na signály. Ze zásoby 470 µF dát dvě paralelně u LED,
dvě u pohonů a dvě u audio/elektroniky, dodržet polaritu.
Kapacita pohonů po vypnutí krátce drží energii; změřit vybití.

Zelená kolébka odpojuje pohony; původní červená je hlavní vypínač,
nová červená rezerva. U tříkontaktové kolébky změřit vstup, spínaný
výstup a LED/GND, nepředpokládat dva spínací póly. LED patří na 5 V.
Stav zelené kolébky se do firmware nesnímá. Před odpojením pohonů
nejdřív vyvolat Stop; po opětovném zapnutí provést ruční kontrolu a
zotavení. Firmware nemůže rozpoznat pohyb provedený bez napájení serv.

USB-UART je ověřený servisní konektor. Při připojení PC odpojit externí
napájení ESP, dokud není prokázaná ochrana proti backfeedu.
USB-A napájecí zásuvka ze starého košíku dosud není potvrzená.

## Signály ESP32

| GPIO | Kam připojit | Poznámka |
| ---: | --- | --- |
| 1 | PCA SDA + MCP SDA | Společná I2C sběrnice, 100 kHz |
| 14 | PCA SCL + MCP SCL | Logika/pull-upy výhradně 3,3 V |
| 48 | Pixel Shifter vstup | Od GPIO před onboard LED, externí řetězec začíná pixelem 0 |
| 2 | Přímo PCA OE | HIGH = výstupy vypnuté; cca 10kΩ pull-up na 3,3 V |
| 21 | ICS43434 SCK + oba DFR0954 BCLK | Společné audio hodiny |
| 41 | ICS43434 WS + oba DFR0954 LRC | Philips I2S, 32 kHz / 32bit stereo |
| 42 | ICS43434 SD | Mikrofon 3,3 V, L/R nastavit levý slot |
| 47 | DIN obou DFR0954 | Mono řeč duplikovaná do obou stereo slotů |

GPIO38/39/40 zůstávají rezervované pro SD, nejsou připojené k jiné funkci.
Kamerový konektor podle [pinové mapy](pin-mapa-esp32-s3.md):
4 SIOD, 5 SIOC, 6 VSYNC, 7 HREF, 8 Y4, 9 Y3, 10 Y5, 11 Y2,
12 Y6, 13 PCLK, 15 XCLK, 16 Y9, 17 Y8, 18 Y7.
GPIO19/20 zůstávají USB; paměťové/strapping piny se nepoužívají.
41/42 nejsou externí JTAG, konzole zůstává přes USB-UART.

## PCA9685 a světla

PCA na **0x40**, VCC 3,3 V, V+ za zelenou kolébkou.
**0–5 = serva přihrádek 1–6**, **6–11 = DIN U059 1–6**.
PCA používá 50 Hz; U059 pouze digitální FULL_ON/FULL_OFF.
U059 má vlastní výkonový driver: červená 5 V, černá GND, žlutá DIN,
bílá NC. Na každém DIN 100kΩ pull-down.
Serva: signál na PCA, napájení V+, společná GND.

**OE přímo na GPIO2**, výchozí pull-up kolem 10 kΩ na 3,3 V, `OUTNE=00`.
Ze zakoupených rezistorů použít dva 22kΩ paralelně (11 kΩ);
nevyžaduje to další nákup. Po zapojení proměřit OE při resetu.
Případný pull-down OE na modulu nejdřív identifikovat a odstranit/upravit;
OE musí být HIGH při resetu i odpojeném ESP. Ověřit také zátěž onboard LED2.
Firmware před změnou pohonu nastaví OE HIGH, vymaže všech 16 kanálů,
nastaví jediný výstup a teprve pak OE LOW. Při Stop/I2C chybě dá OE HIGH
přímo před dalším zápisem. OE zastavuje signály, **neodpojuje 5V napájení**.
Při inicializaci ponechá OE HIGH, nastaví `OUTNE=00` a všech 16 kanálů
FULL_OFF. Výstupy povolí až pro přijatý pohyb po kalibraci a místní kontrole.
Napájení pohonů odpojuje zelená kolébka. Software zastaví po návratu
z opožděné smyčky; úplné zamrznutí není dokladem automatického odpojení.

Pixel Shifter podle potisku, jeho 5V výstup přes 330 Ω na DIN prvního
úseku, potom DOUT → DIN dalších: **šest úseků po šesti LED**.
Napájení úseků vést z rozvodu. Firmware vždy přepíše celý řetězec,
choreografie F2 může rozsvítit jednotlivé skupiny i všech 36 LED;
každá složka RGB zůstává nejvýše 64/255. Sdílené časování hlavní a krátké
varianty je v [popisu efektů](../mechanika/final/EFEKTY.md).
Onboard LED48 zrcadlí první pixel, neposouvá externí indexy.

## MCP23017 a tlačítko

Adafruit 5346 na **0x20**, A0–A2 LOW, VIN/VCC 3,3 V, společná GND.
INT není potřeba; vstupy nepřipojovat k 5 V.

| MCP pin | Funkce | Zapojení |
| --- | --- | --- |
| GPA0 | Bílé momentové tlačítko | Kontakt na GND, 15kΩ pull-up na 3,3 V; LED samostatně |
| GPA1–GPA7, GPB0–GPB7 | Volné | Nezapojovat, firmware je používá jako vstupy bez funkce |

Krátký stisk v klidu spustí/zastaví hlas, při prvním startu zvolí
GPT-Live/Marin, pokud režim nebyl zvolený. Při prezentaci stisk vyvolá Stop.
Přidržení 2 s v klidu potvrzuje **ruční kontrolu všech šesti přihrádek
ve spodní poloze** a umožní zotavení po bootu/chybě. Firmware tuto
polohu sám nesnímá. Po přerušeném pohybu nejdřív pohony odpojit zelenou
kolébkou, zkontrolovat/ručně vrátit mechanismus, pak kolébku zapnout a
potvrdit kontrolu tlačítkem. Debounce je 30 ms.

## Dva zesilovače a mute

Každý DFR0954 má vlastní 8Ω reproduktor mezi SPK+ / SPK− přes vlastní
PH2 kabel. SPK− není GND; výstupy modulů nespojovat.
Společné jsou napájení/GND a I2S vstupy. GAIN zpočátku nezapojený.

[DFRobot](https://wiki.dfrobot.com/dfr0954/) uvádí výchozí smíšený mono
kanál. **Oba moduly ponechat v původním stavu, SD/GAIN nepřipojovat k MCP,
R1 neodpájet.** Sdílejí BCLK/LRC/DIN a firmware posílá stejnou řeč v obou
stereo slotech. Hlasitost začíná na 25 % v PCM. Mute/Stop zavře software
pro nové PCM; již zařazená DMA se vyprázdní do nulových dat díky
`auto_clear`. Normální dokončení navíc zapisuje a proplachuje ticho.
Osm 10ms DMA bufferů znamená až přibližně 80 ms již zařazeného zvuku;
časování je nutné proměřit. Ticho při bootu/restartu tím není zaručené.
GPA1 se nepoužívá.

## Kalibrace a zprovoznění

1. Bez pohonů změřit napájení, I2C úrovně/adresy, GPIO2/OE, resetové stavy
   a společné I2S audio. Na jednom nezatíženém servu proměřit bezpečné pulzy.
2. Pro každou přihrádku změřit odlišné `rest_us` / `raised_us`
   v rozsahu 500–2500 µs a individuální `travel_ms` v rozsahu 20–3000 ms.
   Zahrnout potřebnou rezervu pro skutečně zatíženou přihrádku.
   Softwarová mez pohybu je tento čas + 250 ms; celý cyklus do 15 s. Servo pak dostane pulz po kalibrovanou
   dobu a signály se vypnou; ucpané servo nebo vytažený kabel se polohově
   nerozpozná. Mechanika musí držet obě polohy bez servo napájení.
3. Připravit skutečnou konfiguraci podle
   [servisního projektu](../firmware/diagnostics/hardware-service/README.md).
   Příklad má flag false a nulové pulzy. Zápis mění pouze `wand_hw`,
   zachovává identity/TLS. Nahrání je samostatně autorizovaná akce;
   při této implementaci neproběhlo.
4. Obnovit aplikaci, ručně ověřit klidovou polohu a potvrdit kontrolu.
   Sekvence: hledání → zvýraznění → tři vibrace → zdvih → 5s ukázka
   → časovaný návrat → atomické obnovení dostupnosti.
5. Ověřit Stop ve všech fázích, I2C chybu, restart, vypnutí zelené kolébky
   a ztrátu ručního lease. Stop nevrací servo automaticky.
6. Změřit proudy, pokles napětí a teplotu v uzavřeném kufru;
   návrhový cíl je do 4 A trvale z udávaného 5A měniče.

Build a fake testy dokládají programové chování, nikoli skutečný pohyb.
