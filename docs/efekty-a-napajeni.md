# Efekty, zapojení a napájení kufru

Aktualizováno **9. 10. 2026**. Potvrzené nákupy jsou v
[objednávce](objednavka-doplneni-kufru-2026-10-06.md), konkrétní
kabelový plán a implementovaný profil v
[zapojení potvrzeného hardwaru](zapojeni-potvrzeneho-hardwaru.md).
Máme měnič Joy-IT 5 V / 5 A, MCP23017, dva DFR0954, dvě PH2 vedení,
oba reproduktory a montážní materiál. Zvolená powerbanka je UGREEN PB720.
Firmware nyní obsahuje fyzické adaptéry; elektrické měření nové sestavy
neproběhlo a prázdná NVS pohony nepovoluje.

Pohony mají 5V napájení přímo za zelenou servisní kolébkou.
GPIO2 přímo řídí PCA OE. Jednotlivé kalibrované časované pohyby
nevyžadují další moduly ani polohové snímače. Servo poloha není měřená;
pulzy, proudy a mute rozhraní se musí ověřit na skutečné sestavě.

## Provozní limity a mechanika

- Šest úseků WS2812B po cca 20 cm, šest LED v úseku, celkem cca 1,2 m / 36 LED.
- Svítí nejvýše jeden úsek. Zhasnuté úseky zůstávají napájené a přenášejí data.
- Aktivní je nejvýše **jedno servo NEBO jeden U059** v celém kufru.
- LED může svítit současně s aktivním pohonem. Kamera, Wi-Fi a audio jsou
  samostatná zátěž a nezapočítávají se jako pohon.
- Neaktivním servům vypnout impulzy, nepožadovat silové držení polohy.
  Samotné odstranění impulzů není zaručené odpojení jejich elektroniky;
  klidový odběr všech připojených serv se změří. Mechanika musí držet polohu
  bez proudu. Když to nesplní, upravit mechaniku nebo dimenzování napájení.

Sekvence: postupné světelné hledání po jedné přihrádce → vybraný úsek →
pulzy vibrace → servo zdvih → prezentace → servo návrat → klid.
Hůlka zůstává v kufru. Stop/porucha nevyvolává automatický návrat.
Zdvih, dorazy a kalibrace pulzů se ověřují na jedné skutečné přihrádce.

## Napájení: jeden měnič

```text
UGREEN PB720 USB-C1 PD (případně vnější vhodný USB-C PD zdroj)
      → koupený kabel C–C 60 W / 3 A
      → koupený PD modul nastavený na 12 V
      → Joy-IT SBC-Buck02 / SEG-28582, 9–35 V / pevně 5 V / max. 5 A
      → koupený vratný jistič 5 A
      → koupená hlavní kolébka (5V strana)
      → společný rozvod 5 V
          ├─ ESP32 přes USB-A modul + kabel A–C
          ├─ dva MAX98357A, LED a Pixel Shifter
          └─ nová servisní kolébka pohonů
              → V+ PCA9685 + napájení šesti U059
```

Logika PCA9685 a MCP23017 je napájená **3,3 V**
z ESP32 a nezávisí na servisní kolébce. Všechny země společné, zem pohonů
se neodpojuje. Rozvod výkonu nevede přes 5V pin ESP32, Dupont ani univerzální
PCB tenkými cestami. Joy-IT má společnou zem pro vstup/výstup: řídit se
potiskem svorek, ne domyšleným pořadím. Před připojením desek změřit 5 V.

PD modul máme: [Drátek 1782992576](https://dratek.cz/arduino-platforma/232511-power-modul-usb-c-pd-s-nastavitelnym-napetim-a-prepinacem.html)
uvádí max. 3 A. Pro tuto sestavu vyhovuje; při 20 W výstupu a 85% účinnosti
měniče je vstupní odběr asi 1,96 A při 12 V. Nepotřebujeme nový PD modul,
5A USB kabel ani 100W zdroj. Zdroj AK-CH-31 používat pro kufr samotný,
nevynucovat současné nabíjení dalších zařízení na jeho druhém portu.
Případná powerbanka musí poskytovat 12V PD profil 3 A.

| Zátěž při 5 V | Návrhová hodnota | Povaha údaje |
| --- | ---: | --- |
| Jeden 20cm úsek LED plná bílá | cca 0,36 A / 1,8 W | Výpočet z 9 W/m |
| ESP32, kamera, SD, mikrofon a pomocná logika | rezerva 1 A | Odhad, změřit souběh Wi-Fi/kamery |
| Oba zesilovače | rezerva 1 A | Odhad pro 8Ω reproduktory, ověřit hlasitý zvuk |
| Jedno servo NEBO vibrace | rezerva 1,5 A | Proud serva při zablokování a U059 při 100 % není doložený |
| Zhasnuté LED a neaktivní serva | rezerva 0,1 A | Odhad, skutečný klidový odběr změřit |
| Celkem | cca 3,96 A / 19,8 W | Návrhová rezerva, nikoli změřené maximum |

Měnič má udávané maximum 5 A; cílit na nejvýše 4 A trvale a ověřit špičky,
napětí u ESP32 a teplotu v konečné skříni. První zkoušky LED na cca 25 %
jasu, po měření může jeden úsek svítit plně. Firmware musí limit jednoho
úseku a jednoho pohonu vynucovat; chybné rozsvícení všech 36 LED nesmí být
považováno za schválený provozní stav. Tepelný jistič není rychlá ochrana
každé tenké odbočky a nenahrazuje proudové omezení zdroje/měniče.

## Ovládací prvky

| Prvek | Role | Zapojení |
| --- | --- | --- |
| Koupené bíle podsvícené momentové tlačítko | Začátek rozhovoru / PTT, případně servisní potvrzení podle plánu | Kontakt na GND, 15kΩ pull-up na 3,3 V, profil B MCP GPA0; LED odděleně podle skutečné specifikace tlačítka |
| Koupená červená kolébka | Hlavní zapnutí/vypnutí kufru | Přerušuje +5 V za měničem, LED na 5V stranu |
| Koupená zelená kolébka | PHYSICAL_ENABLE: fyzické odpojení pohonů při servisu | Přerušuje přímo +5 V serv/U059; logika, světla a hlas zůstávají napájené |

Kolébka má tři kontakty, **nejde o dva nezávislé spínací póly**: jeden je
pro LED/zem. Identifikovat kontakty podle potisku a měřením. Stav zelené
kolébky se nesnímá; GPA3 je volný. Před jejím vypnutím vyvolat Stop,
po opětovném zapnutí ručně zkontrolovat přihrádky a potvrdit zotavení.
LED kolébek zapojit na 5 V. Pro práci na vstupu odpojit USB-C zdroj.

## Přímé řízení PCA OE

OE na GPIO2, pull-up kolem 10 kΩ (2×22 kΩ paralelně = 11 kΩ ze zásoby) na 3,3 V. HIGH vypíná signály (`OUTNE=00`).
Ověřit případný pull-down OE na PCA a onboard LED2; výchozí HIGH musí
platit při bootu/resetu/odpojení MCU. Firmware nastaví OE HIGH před
FULL_OFF a každou změnou kanálu. Při I2C chybě nebo Stop dá OE HIGH
přímo, pak maže kanály. OE nevypíná napájení serv/U059; to dělá zelená
kolébka. Při úplném zamrznutí řídicí úlohy software sám odpojení nedoloží.

## Připojení jednotlivých částí

- **PCA9685:** nejjednodušší jedna deska, adresa 0x40, kanály 0–5 serva,
  6–11 digitální FULL_ON/FULL_OFF pro U059. Druhá koupená je rezerva.
  VCC a I2C pull-upy 3,3 V, V+ ze spínané 5V větve. Jeden prescaler 50 Hz
  platí pro serva; nelze jej vydávat za 10kHz PWM vibrací.
- **MCP23017:** modul Adafruit 5346, 3,3 V, adresa 0x20. Pomocné piny podle
  B, I2C GPIO1/14. Souběh kamery používá jiné piny než profil A.
- **Vibrace:** původní Grove konektor a kabel zachovat na straně U059;
  opačný konec zapojit jednotlivými izolovanými vodiči podle
  [montážního kabelového plánu F1](../mechanika/final/KABELAZ.md).
  Napájení pokračuje označenou pájenou 0,5mm² odbočkou, DIN do PCA;
  plán nepoužívá dodatečný Grove adaptér.
  U059 má vlastní driver: červená 5 V, černá GND, žlutá DIN, bílá NC.
  [M5Stack](https://docs.m5stack.com/en/unit/Unit_Vibrator) uvádí 424,35 mA
  při 5 V / 10kHz PWM / 50 %. Není to maximální proud ani spotřeba při
  našem digitálním FULL_ON řízení, proto rezerva a měření.
- **LED:** data DOUT → DIN všech šesti úseků; +5 V/GND do každého zvlášť.
  Pixel Shifter a 330 Ω na začátku řetězce. Současná sestava F1 má
  označené pájené odbočky +5 V/GND a samostatné spoje DOUT/DIN;
  JST-SM konektory nejsou koupené a nejsou součástí montážního plánu.
  Servis jednotlivého úseku vyžaduje odpojit zdroj, ověřit vybití,
  odkrýt a odpájet jeho konkrétní výkonové i datové spoje, poté je
  obnovit a znovu zaizolovat podle [KABELAZ.md](../mechanika/final/KABELAZ.md#co-lze-při-servisu-rozpojit).
  Odkrýt IP65 u pájení, zachovat délkovou rezervu pro servis, ověřit
  šipky a polaritu. LED úseky nemají rychlorozpojitelný konektor.
- **Audio:** dva potvrzené DFR0954 a dva samostatné 8Ω reproduktory,
  společné BCLK/WS/DIN. SPK výstupy nikdy nespojovat; SPK− není GND.
  Každý má vlastní PH2 kabel. Mikrofon ICS43434 napájet 3,3 V.
  Oba DFR0954 ponechat v původním smíšeném mono režimu, SD/GAIN nechat
  nezapojené a R1 neupravovat. Hlasitost/mute řeší software PCM/I2S,
  ticho při bootu/resetu není garantované. Detaily v
  [zapojení](zapojeni-potvrzeneho-hardwaru.md#dva-zesilovače-a-mute).
- **Kamera/SD:** stávající ESP32-S3 Drátek 232701 má konektor OV5640;
  samotná kamera je objednaná, čeká na doručení. Revizi/FPC ověřit před montáží.
  Profil B-effects zachovává GPIO38–40 pro SD; D158 přidává read-only SDMMC
  asset driver gated servisním `sd_verified`. Fyzické ověření zbývá. Předběžný
  [profil B a piny](pin-mapa-esp32-s3.md) vyžadují ověření GPIO2/48 a SD.
  Druhá deska se nekupuje; souběh periferií ověřit na stávajícím řadiči.
- **Rozvod:** 1,5mm² hlavní vedení, 0,5mm² krátké odbočky, společná zem.
  Svorky Degson jsou pořízené, čekají na doručení a neblokují rozvržení kufru.
  Degson má všech pět pozic společných: pro +5 V a GND samostatné bloky,
  nikdy oba póly do stejné svorky. Nepoužívat ji na tenký Dupont pod 0,2 mm².
- **Kondenzátory:** ze zakoupených 10×470 µF použít 2 paralelně u LED,
  2 na spínané větvi pohonů, 2 u elektroniky/audio; zbytek rezerva.
  U pohonů ověřit čas vybití po vypnutí: kondenzátory mohou krátce držet
  napětí. Větší kapacita není automaticky lepší bezpečné vypnutí.
- **USB:** externí 5V napájení a připojení k PC nespojovat současně bez
  ověření ochrany proti backfeedu. Pro servis odpojit externí napájecí
  kabel k ESP32 a používat USB-UART podle dosavadních ověřených postupů.

## Kontroly před uzavřením kufru

1. Změřit kontakty kolébek, polaritu kabelů a zem; 12 V pouze na vstupu měniče.
2. Změřit 5 V bez zátěže i při LED + jednom pohonu + Wi-Fi/kamera/audio,
   klidový odběr zbytku, poklesy u ESP32 a zahřívání v konečné montáži.
3. Bez pohonů proměřit GPIO2/OE HIGH při bootu/resetu a přímé OE při
   I2C chybě; konfigurace PCA má výstupy LOW při OE HIGH.
4. S jedním pohonem ověřit Stop, I2C timeout, servisní kolébku, vybití
   kondenzátorů a absenci zpětného napájení. MCU reset nesmí vyvolat pohyb.
5. Ověřit pasivní držení přihrádky, mechanické dorazy, jeden aktivní úsek
   a jeden pohon včetně přechodů. Pak rozšířit na všech šest přihrádek.
6. Na sestavě ověřit usazení držáků a kabelové trasy podle
   [společného přehledu rozměrů](../mechanika/rozmery-nakoupenych-soucastek.md).
   Vnitřek kufru je potvrzený, výztuha má 5 mm a další měření neblokují
   rozvržení. Powerbanka 139×80×26,5 mm musí jít snadno vyjmout;
   síťový adaptér ponechat mimo kufr. Úspěšný nákup není uzavření E5/B1
   ani veřejná HW akceptace.
