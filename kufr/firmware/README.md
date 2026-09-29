# Firmware AI kufru

Samostatný projekt PlatformIO pro ESP32-S3 N16R8 (16 MB flash, 8 MB PSRAM).
Volba `esp32-s3-devkitc-1` je zatím základ pro sestavení. [Dodaná pinová
mapa](../docs/pin-mapa-esp32-s3.md) ukazuje funkce konektoru kamery, MicroSD,
PSRAM a USB; předběžné přidělení externích pinů A/B obsahuje kapitola 10
[implementačního plánu](../docs/implementacni-plan.md). Nejde o ověřené zapojení. Před
nahráním ověřit revizi skutečné desky, typ flash/PSRAM a USB rozhraní.
PlatformIO používá připnutou platformu Espressif 32 7.1.3. Zvolená tabulka
oddílů dává ze 16 MB flash každému ze dvou OTA slotů 6,25 MiB pro aplikaci.

Aktuální runtime je **Arduino**; přechod na ESP-IDF je plánovaná etapa E4.
Podrobný postup a skutečně provedené kontroly jsou ve
[stavu implementace](../docs/stav-implementace.md). Popis níže je současný
sériový prototyp, nikoli implementované síťové či bezpečnostní řízení.

## Architektura

Firmware roste po vertical slices. Každá funkce drží své rozhodování a své
vstupní/výstupní porty pohromadě. `main.cpp` jen skládá instance a volá je
ve smyčce s časováním bez `delay()`. Nezavádíme globální vrstvu
service/repository ani společné rozhraní pro každou periferii předtím, než
bude potřeba.

```text
src/
  main.cpp                                  sestavení a smyčka
  features/
    safety/
      SafetyPort.h                          požadavky na vypnutí/pohony
      SafetyController.h/.cpp               stop latch a vzájemné vyloučení výstupů
    wand_selection/
      SelectionSequence.h/.cpp              čistá, časovaná posloupnost
      SerialSelectionPort.h/.cpp            vývojové příkazy a výpis požadavků
tests/
  selection_sequence_test.cpp               test bez hardwaru
```

První slice řídí výběr jedné ze šesti hůlek. `SelectionSequence` přijímá
číslo přihrádky 1–6 a čas v milisekundách. Po spuštění vyžaduje výslovné
zotavení po kontrole obsluhou; po Stop/timeoutu znovu odmítá výběr až do
zotavení. Postupně vydává kroky hledání,
zvýraznění, tři pulzy vibrace, uvolnění západky a klidového světla. Při
probíhajícím výběru další příkaz odmítne. Používá rozdíl `uint32_t` času,
takže běží i přes přetočení `millis()`. Každé volání vydá nejvýše jeden krok.
Stop vyžádá vypnutí pohonů okamžitě bez čekání na další `Update()` a během
uvolňování se nepokouší vrátit západku. Provozní motorový pulz trvá 150 ms;
při zpožděném průchodu od 250 ms, uvolňování od 500 ms nebo celé sekvenci od
5 s jádro zastaví. `SafetyController` brání souběhu motoru a západky.
Časování je zatím výchozí pro prototyp; délku pulzů a polohu serva
bude třeba doladit podle skutečné mechaniky a měření proudu.

`SerialSelectionPort` je první vstup/výstup pro vývoj bez zapojené elektroniky.
Příkazy na sériovém monitoru (115200 Bd, ukončení řádku LF nebo CRLF):

```text
select 4
stop
reset
status
help
```

`reset` znamená, že obsluha předtím zkontrolovala mechaniku. Sériový adaptér
vypisuje také požadavky safety portu; skutečné vypnutí nelze z těchto výpisů
odvodit. Žádný GPIO pin, motor, servo ani LED se
zatím neinicializuje. Firmware neobsahuje přihlašovací údaje ani síťové
připojení. Nahrání do desky je až další krok po kontrole konkrétního modulu.

## Sestavení a test

Ve složce `kufr/firmware`:

```text
pio run
```

Jednotkový test čisté sekvence lze sestavit libovolným hostitelským C++
kompilátorem (C++11 nebo novějším), např.:

```text
g++ -std=c++11 -Wall -Wextra -Werror src/features/safety/SafetyController.cpp src/features/wand_selection/SelectionSequence.cpp tests/selection_sequence_test.cpp -o <docasna-slozka>/selection_sequence_test
<docasna-slozka>/selection_sequence_test
```

Test pokrývá pořadí a časování kroků, neplatný či souběžný výběr, Stop v každé
fázi, opakovaný Stop, explicitní zotavení, přetočení časovače, zpožděný průchod
smyčkou a vzájemné vyloučení výstupů. Nepotřebuje ESP32 ani knihovnu Arduino.
Na Windows lze použít také Visual Studio Developer Command Prompt a `cl`.

## Další slices a podmínky zapojení

Pořadí a akceptace se řídí kapitolou 12 plánu; rozpracovaný krok se vede
výhradně ve stavu implementace. E1.1 přidává Stop, safety port,
časové limity a testy bez desky. Další krok určuje stav; migrace
runtime a přímé HTTPS/WSS spojení patří E4. E5/E6 teprve připojí jednu
a potom šest skutečných přihrádek po ověření zapojení a bezpečnostního obvodu.

Cílový firmware vlastní samostatný PTT rozhovor s backendem, návrh AI,
fyzické potvrzení, audio a bezpečnou sekvenci. Telefon komunikuje místně
přes WSS jako volitelná záloha; backend neposílá povely k otevření.
Slices a jejich odpovědnosti jsou v kapitole 3 plánu. Čisté jádro zachovat
testovatelné bez Arduino/ESP-IDF; síťový callback nesmí přímo spínat GPIO.

Pro skutečné výstupy platí jas zpočátku kolem 25 %, nejvýše jeden motor,
žádný motor při uvolňování západky a nezávislá vypínaná větev pohonů
s watchdogem. Při Stop/resetu se nesmí automaticky zavírat západka.
OV5640 + SD vyžadují pozdější profil B/E13 a nové ověření pinů.

Výše uvedené build/test příkazy se týkají současného prototypu; po migraci
je ověřit a aktualizovat. U hostitelského testu směruj výstup `-o` do
dočasné/build složky a použij spuštění odpovídající místnímu shellu.
Chybějící toolchain zaznamenej jako neprovedenou kontrolu, nikoli úspěch.

Vazby pinů na desce popisuje
[`../docs/pin-mapa-esp32-s3.md`](../docs/pin-mapa-esp32-s3.md). Fyzické
zapojení a energetický limit popisuje
[`../docs/efekty-a-napajeni.md`](../docs/efekty-a-napajeni.md), kamerové
omezení [`../docs/kamera.md`](../docs/kamera.md). Před připojením ESP32 je
nutné změřit 5V výstup měniče; 20 V smí být pouze na vstupu měniče.
