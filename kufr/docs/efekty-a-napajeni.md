# Efekty, zapojení a napájení kufru

Aktualizováno 6. 10. 2026 podle faktur a rozhodnutí majitele. Nákupní zdroj
pravdy je [samostatná objednávka](../../docs/objednavka-doplneni-kufru-2026-10-06.md).
Architektura a akceptace zůstávají v kapitolách 8 a 10
[implementačního plánu](implementacni-plan.md). Níže je konkrétní návrh pro
montáž prototypu; elektrické měření ani zapojení firmware dosud neproběhlo.

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
230 V → vnější USB-C PD zdroj Akyga AK-CH-31
      → koupený kabel C–C 60 W / 3 A
      → koupený PD modul nastavený na 20 V
      → Joy-IT 9–35 V / pevně 5 V / max. 5 A
      → koupený vratný jistič 5 A
      → koupená hlavní kolébka (5V strana)
      → společný rozvod 5 V
          ├─ ESP32 přes USB-A modul + kabel A–C
          ├─ dva MAX98357A, LED a Pixel Shifter
          └─ nová servisní kolébka pohonů
              → MOSFET load switch Pololu 2814
              → V+ PCA9685 + napájení šesti U059
```

Logika PCA9685, MCP23017 a bezpečnostního obvodu je napájená **3,3 V**
z ESP32 a nezávisí na servisní kolébce. Všechny země společné, zem pohonů
se neodpojuje. Rozvod výkonu nevede přes 5V pin ESP32, Dupont ani univerzální
PCB tenkými cestami. Joy-IT má společnou zem pro vstup/výstup: řídit se
potiskem svorek, ne domyšleným pořadím. Před připojením desek změřit 5 V.

PD modul máme: [Drátek 1782992576](https://dratek.cz/arduino-platforma/232511-power-modul-usb-c-pd-s-nastavitelnym-napetim-a-prepinacem.html)
uvádí max. 3 A. Pro tuto sestavu vyhovuje; při 20 W výstupu a 85% účinnosti
měniče je vstupní odběr asi 1,18 A při 20 V. Nepotřebujeme nový PD modul,
5A USB kabel ani 100W zdroj. Zdroj AK-CH-31 používat pro kufr samotný,
nevynucovat současné nabíjení dalších zařízení na jeho druhém portu.
Případná powerbanka musí poskytovat 20V PD profil alespoň 1,5 A (lépe 2 A).

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
| Nová druhá kolébka | PHYSICAL_ENABLE: fyzické odpojení pohonů při servisu | Přerušuje jen +5 V před load switchem; logika, světla a hlas zůstávají napájené |

Kolébka má tři kontakty, **nejde o dva nezávislé spínací póly**: jeden je
pro LED/zem. Identifikovat kontakty podle potisku a měřením. Snímat napětí
za servisní kolébkou, ještě před MOSFET switchem, přes dělič **15 kΩ nahoře
+ 22 kΩ k GND**; nominálně cca 2,97 V při 5 V. Tento uzel je vstup fyzického
povolení do logiky a MCP GPA3, ne přímý 5V vstup do GPIO. Při vypnutí je
stažený k zemi. LED kolébek jsou podle prodejce pro 5–12 V, proto nepatří
na 20V stranu. Po hlavním vypnutí zůstává PD/měnič pod napětím, úplně
odpojit USB-C pro práci na vstupní části. Druhé momentové tlačítko netřeba.

## Konkrétní návrh nezávislého watchdogu a inhibit

Vybrané součástky doplňují dosud chybějící kusovník; **nejde o již otestovaný
bezpečnostní obvod**. Stav E0/E5/B1 zůstává neuzavřený do měření.

- Pololu **2801** napájený 3,3 V sleduje RC IN. Použijeme jeho **GOOD**, ne
  silový výstup (žádný nemá) a ne stav OUT závislý na prahu/safe-start.
- RC IN dostane jeden softwarový pulz cca **1 ms každých 50 ms**, pouze po
  úspěšném průchodu řídicí úlohou. Samovolně běžící LEDC/RMT/PCA PWM není
  heartbeat. V B je zdroj MCP GPA2; musí být ověřeno, že I2C umí pulz
  dokončit v rozsahu 0,5–2,5 ms i při zátěži. Pomocná úloha smí dokončit
  jediný vyžádaný pulz, nesmí sama opakovat ani přehrávat starou frontu.
- [Pololu manuál](https://www.pololu.com/docs/0J60/all), §8, uvádí ztrátu
  signálu po **130 ms bez aktivity**, přípustné pulzy 0,5–2,5 ms / 10–333 Hz
  a tři dobré pulzy před GOOD. To odpovídá návrhovému limitu 250 ms;
  konkrétní reakci při HIGH/LOW a zamrzlé I2C změřit.
- CD4093BE je napájený **3,3 V**, s 100 nF přímo mezi VDD a GND. Jeho čtyři
  NAND hradla skládají `ACTUATOR_EN = RUN_REQUEST AND GOOD AND PHYSICAL_ENABLE`
  a `OE = NOT ACTUATOR_EN`. Napájení 5 V by vyžadovalo nové ověření logických
  úrovní a není tímto návrhem povolené.
- Pololu **2814** je high-side switch pouze pohonů. Vestavěný posuvník musí
  zůstat **OFF**; pin **ON** řídí ACTUATOR_EN. Nepoužívat pin SW jako ON.
  [Výrobce](https://www.pololu.com/product/2814) uvádí ON LOW/nepřipojený =
  vypnuto, HIGH nad cca 1 V = zapnuto. Integrovaný posuvník v ON by obešel
  digitální inhibit. Mechanická servisní kolébka zůstává nezávislou cestou
  odpojení při selhání MOSFETu.
- RUN_REQUEST, RC IN a ON mají výchozí pull-down; OE výchozí pull-up
  na 3,3 V, například 100 kΩ. Motorové DIN vlastní pull-down. Ověřit
  pull rezistory na skutečné PCA/MCP desce: případný OE pull-down odstranit
  nebo upravit, aby vznikl zaručený HIGH při bootu a hradlo bylo schopné
  řídit obě úrovně. CD4093 má slabší výstupy než HC logika; zátěž OE/ON
  se musí změřit. Žádný nezapojený logický vstup nezůstane plovoucí.

Logický rozklad pro schéma: N1=NAND(RUN_REQUEST,GOOD), N2=NAND(N1,N1),
N3=NAND(N2,PHYSICAL_ENABLE)=OE, N4=NAND(N3,N3)=ACTUATOR_EN.
DIP14 pinout převzít z [TI datasheetu CD4093B](https://www.ti.com/lit/ds/symlink/cd4093b.pdf),
nezapojovat pouze podle pořadí hradel v této větě.

PCA nastavit neinvertovaně, `OUTNE=00` (OE HIGH → výstupy LOW); ověřit,
že výstupy do odpojených serv/U059 nezpůsobují zpětné napájení. Logické
napájení PCA zůstává zapnuté. Přímé RUN_REQUEST v B vede z GPIO2, nikoli
přes MCP. Při I2C timeoutu 20 ms stáhnout RUN_REQUEST bez dalšího I2C zápisu.
Watchdog se nejdříve prokáže při RUN_REQUEST=0, pak lze povolit pohony.
Obnovení heartbeat samo neobnoví sekvenci: firmware musí držet fault latch.

## Připojení jednotlivých částí

- **PCA9685:** nejjednodušší jedna deska, adresa 0x40, kanály 0–5 serva,
  6–11 digitální FULL_ON/FULL_OFF pro U059. Druhá koupená je rezerva.
  VCC a I2C pull-upy 3,3 V, V+ ze spínané 5V větve. Jeden prescaler 50 Hz
  platí pro serva; nelze jej vydávat za 10kHz PWM vibrací.
- **MCP23017:** modul Adafruit 5346, 3,3 V, adresa 0x20. Pomocné piny podle
  B, I2C GPIO1/14. Souběh kamery používá jiné piny než profil A.
- **Vibrace:** vlastní Grove kabel → adaptér → 0,5mm² napájecí odbočka.
  U059 má vlastní driver: červená 5 V, černá GND, žlutá DIN, bílá NC.
  [M5Stack](https://docs.m5stack.com/en/unit/Unit_Vibrator) uvádí 424,35 mA
  při 5 V / 10kHz PWM / 50 %. Není to maximální proud ani spotřeba při
  našem digitálním FULL_ON řízení, proto rezerva a měření.
- **LED:** data DOUT → DIN všech šesti úseků; +5 V/GND do každého zvlášť.
  Pixel Shifter a 330 Ω na začátku řetězce. JST-SM konektor na vstupu
  úseku dovolí servisní odpojení; výstupní data vést zvlášť do dalšího.
  Odkrýt IP65 u pájení, zaizolovat spoje, ověřit šipky a polaritu.
- **Audio:** oba DFR0954 sdílejí BCLK/WS/DIN, pro jednoduchý společný hlas
  zachovat shodné kanálové nastavení. Každý má vlastní 8Ω reproduktor přes
  PH2.0 kabel. Jejich SPK+ / SPK− nikdy neparalelizovat a SPK− není GND.
  Reproduktory 5 W jsou zatížitelnost, MAX98357A při 8 Ω dodá přibližně
  1,8 W na kanál. Mute/SD obou ověřit podle konkrétního modulu a nastavit
  fail-muted boot; mikrofon ICS43434 napájet 3,3 V.
- **Kamera/SD:** OV5640 ze sady stejné řady, revizi/FPC ověřit před montáží.
  Karta 32 GB FAT32 pro lokální assety, ne archiv rozhovorů. Předběžný
  [profil B a piny](pin-mapa-esp32-s3.md) vyžadují ověření GPIO2/48 a SD.
  Sada obsahuje rezervní řadič, který je alternativou při nevyhovění desky,
  ne oprávnění improvizovat neověřenou mapu.
- **Rozvod:** 1,5mm² hlavní vedení, 0,5mm² krátké odbočky, společná zem.
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

1. Změřit kontakty kolébek, polaritu kabelů a zem; 20 V pouze na vstupu měniče.
2. Změřit 5 V bez zátěže i při LED + jednom pohonu + Wi-Fi/kamera/audio,
   klidový odběr zbytku, poklesy u ESP32 a zahřívání v konečné montáži.
3. Bez pohonů prokázat boot/reset inhibit, OE/ON úrovně, watchdog pulzy,
   HIGH/LOW zamrznutí a odpojení heartbeat do 250 ms. Pro samotné časování
   nestačí multimetr: logický analyzátor/osciloskop nebo měření stavů přes
   nezávislý řadič při prototypování; nástroj není v nákupním součtu.
4. S jedním pohonem ověřit Stop, I2C timeout, servisní kolébku, vybití
   kondenzátorů a absenci zpětného napájení. MCU reset nesmí vyvolat pohyb.
5. Ověřit pasivní držení přihrádky, mechanické dorazy, jeden aktivní úsek
   a jeden pohon včetně přechodů. Pak rozšířit na všech šest přihrádek.
6. Změřit vnitřní prostor, držáky a kabelové trasy; síťový adaptér ponechat
   mimo kufr. Úspěšný nákup není uzavření E5/B1 ani veřejná HW akceptace.
