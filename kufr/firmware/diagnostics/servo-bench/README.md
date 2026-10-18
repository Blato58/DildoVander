# První zkouška jednoho serva přes PCA9685

Samostatný ESP-IDF 6.1 firmware pro doručené ESP32-S3 N16R8 (PlatformIO
`espressif32@7.1.3`). Používá jen UART, I2C a OE; bez Wi-Fi, AI, kamery,
LED, vibrací a zápisu NVS. Nemění provozní kalibraci ani její bezpečnostní
brány. Je určený **výhradně pro volné servo odpojené od mechanismu**.
První povel do středu může znamenat velký pohyb z neznámé výchozí polohy.
Není to test zdvihu přihrádky ani fyzická přejímka E5.

V [nákupním seznamu](../../../../docs/nakupni-seznam-kufr.md) je polohovací
[mikroservo 180° Drátek 897](https://dratek.cz/arduino-platforma/897-eses-servo-motor-9g.html).
Prodejce uvádí napájení 3–7,2 V; zde používáme projektových **5 V**.
Nepoužívat variantu 360° ani zkoušet mechanické dorazy.

## Zapojení pro ESP napájené z počítače

Nejdřív odpoj USB i PD zdroj. Pro první test připoj pouze jedno servo,
ostatní výstupy PCA nech prázdné. Kameru ani SD kartu nepřipojuj.
Již zapojenou výkonovou větev zkontroluj podle schématu:

```text
PD zdroj → PD trigger 12 V → IN měniče
OUT měniče +5 V → jistič 5 A → vypínač pohonů → PCA V+ (+ svorka)
OUT měniče GND ────────────────────────────→ PCA GND (− svorka)
PC USB → ESP konektor USB-UART
```

Vypínač pohonů má být po ruce pro okamžité odpojení serva. Projekt používá
zelenou kolébku; u tříkontaktové varianty nejdřív proměř spínací kontakty,
neodhaduj pořadí vstup/výstup/GND. Jistič sám nenahrazuje ovládání Stop.
Zem nevypínej. Pokud je PCA nyní přímo za jističem, zařaď vypínač do
kladného 5V vedení. **12 V smí vést pouze do vstupu měniče.**

| Odkud | Kam |
| --- | --- |
| ESP `3V3` | PCA `VCC` (logika, nikoli `V+`) |
| ESP `GND` | PCA `GND`, společná s mínusem měniče |
| ESP `1` / GPIO1 | PCA `SDA` |
| ESP `14` / GPIO14 | PCA `SCL` |
| ESP `2` / GPIO2 | PCA `OE` |
| PCA `OE` | Pull-up na `3V3`: dva koupené 22kΩ rezistory paralelně (11 kΩ) |
| Servo signál, obvykle oranžový/žlutý | PCA kanál **0**, kontakt `S`/PWM |
| Servo červený vodič | Kanál 0, kontakt `V+` |
| Servo hnědý/černý vodič | Kanál 0, kontakt `GND` |

Pořadí tří kontaktů určuj podle potisku konkrétní PCA, nikoli podle
domněnky, že červený je vždy u stejného okraje. Vstupní napájení PCA veď
svorkami a vhodným výkonovým vodičem; servo nekrm z ESP `3V3`, `5V` ani
z jeho USB. **Externích 5 V nepropojuj s ESP `5V`**, když je ESP v PC;
ochrana proti zpětnému napájení nebyla ověřena. Společná je pouze GND.
PCA `VCC` a I2C pull-upy musí být na 3,3 V; `VCC` a `V+` nespojuj.
Adresové můstky A0–A5 ponech ve výchozím stavu pro adresu `0x40`.

U pohonů použij dva koupené 470µF kondenzátory paralelně mezi V+ a GND,
s odpovídajícím napěťovým ratingem nad 5 V, dodrž polaritu. Po vypnutí
mohou krátce držet energii. Před přepojováním počkej na vybití a ověř napětí.

Nejdřív bez serva ověř multimetrem přibližně 5 V a polaritu na `V+`/GND,
3,3 V na `VCC`/GND a HIGH na `OE` i při resetu ESP. Některé moduly mají
OE pull-down: musí být identifikovaný a upravený podle
[kanonického zapojení](../../../docs/zapojeni-potvrzeneho-hardwaru.md#pca9685-a-světla).
Pokud OE při resetu klesá, test se servem nezahajuj. Firmware nemůže
softwarově ověřit externí pull-up, napětí, proud ani správnou polaritu.

## Sestavení a nahrání

Z kořene repozitáře:

```powershell
python kufr/firmware/diagnostics/servo-bench/tests/run_tests.py
pio run -d kufr/firmware/diagnostics/servo-bench -j 2
```

Sestavení nic nenahrává. Před případným nahráním odpoj napájení serv,
ověř aktuální port (`pio device list`) a identitu N16R8 podle
[USB diagnostiky](../board-memory/README.md). `COM7` je pouze dříve ověřený
port, nemusí být aktuální. Nahrání je samostatná akce vyžadující souhlas
majitele. Před náhradou firmware zachovej soukromou zálohu celé 16MiB flash
mimo Git a její SHA-256 podle USB diagnostiky; záloha může obsahovat credentialy.
Neprováděj erase flash ani změny eFuse. Upload nahrazuje bootloader,
partition table a aplikaci; samotný program NVS neinicializuje.

Po autorizaci nahrání, z kořene a se správným portem:

```powershell
pio run -d kufr/firmware/diagnostics/servo-bench -j 2 --target upload --upload-port COM7
pio device monitor -d kufr/firmware/diagnostics/servo-bench --port COM7 --baud 115200
```

## První pohyb

1. Vypínač pohonů vypnutý, servo bez táhla a bez zatížení, vodiče mimo páku.
   Nech startovat ESP přes USB-UART a požaduj `SERVO_BENCH READY outputs disabled`.
   `NOT READY`/`FAULT` znamená vypnout pohony, zkontrolovat kabely a restartovat.
2. Připojení serva prováděj při vypnutých zdrojích; po zapnutí USB a úspěšném
   startu zapni 5V pohony. Bez příkazu nemá běžet PWM.
3. Napiš `arm` + Enter. Do deseti sekund napiš `pulse 0 1500` + Enter.
   Firmware generuje asi 50 Hz po nejvýše 500 ms běžící smyčky, potom dá OE HIGH.
   Poloha není změřená ani zaručená. Pokud už servo stojí poblíž středu,
   nemusí být první pohyb patrný.
4. Pro malý posun znovu `arm` + Enter, potom `pulse 0 1450` + Enter.
   Další samostatná zkouška: `arm` + Enter, potom `pulse 0 1550` + Enter.
   Posílej jednotlivé řádky ručně; nevkládej dávky příkazů.
5. `!` vypíná PWM hned po přijetí znaku, bez Enter; `stop` + Enter také vypíná.
   Při cukání, drhnutí, vrčení proti dorazu, zahřívání nebo poklesu napětí
   okamžitě vypni napájení serva a zkontroluj mechaniku i polaritu.
6. Před odpojením USB nebo výměnou serva nejdřív `stop`, vypnout 5V pohony,
   odpojit oba zdroje a ověřit vybití. Test dalších kusů dělej stejným postupem
   po jednom na kanálu 0; kanály 0–5 lze volit explicitně, nikdy 6–15.

Přípustná šířka je pevně **1400–1600 µs**, nikoli kalibrační rozsah pro
celou dráhu. Každý pulz spotřebuje `arm`; během pulzu další pohybový příkaz
test zastaví. Chyba I2C při přípravě/povelu trvale blokuje pohyb do restartu.
Ostatních 15 výstupů je před pohybem FULL_OFF. Po Stop je OE HIGH bez
čekání na I2C. `OUTNE=00` zajišťuje při OE HIGH nízkou úroveň výstupů.
Výpočet pulzu používá nominální 25MHz oscilátor PCA; přesnou šířku/frekvenci
potvrdí až měření. Viz [datasheet NXP](https://www.nxp.com/docs/en/data-sheet/PCA9685.pdf).

**OE vypíná signál, nikoli napájení ani zaručeně sílu serva.** Servo může
držet poslední polohu; jistotu odpojení dá vypínač 5 V. Limit 500 ms řídí
jedna smyčka (UART čtení po 10 ms); úplné zamrznutí ESP není garantovaný
automatický Stop. Odpojení terminálu/USB se samostatně nedetekuje.
Úspěšný build a host testy nedokládají fyzický pohyb, proud, reset OE ani
bezpečnost mechaniky. Přesné pulzy a proud zaznamenej před kalibrací přihrádky.

Pro návrat k provoznímu firmware nech pohony vypnuté a použij ověřený
původní obraz/zálohu nebo hlavní projekt podle jeho README; běžný upload
nenahrazuje úplnou obnovu dat a partition table ze soukromé zálohy.
