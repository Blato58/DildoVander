# Firmware AI kufru

Samostatný projekt PlatformIO pro ESP32-S3 N16R8 (16 MB flash, 8 MB PSRAM).
Volba `esp32-s3-devkitc-1` je zatím základ pro sestavení. [Dodaná pinová
mapa](../docs/pin-mapa-esp32-s3.md) ukazuje funkce konektoru kamery, MicroSD,
PSRAM a USB; předběžné přidělení externích pinů A/B obsahuje kapitola 10
[implementačního plánu](../docs/implementacni-plan.md). Nejde o ověřené zapojení. Před
nahráním ověřit revizi skutečné desky, typ flash/PSRAM a USB rozhraní.
PlatformIO používá připnutou platformu Espressif 32 7.1.3. Zvolená tabulka
oddílů dává ze 16 MB flash každému ze dvou OTA slotů 6,25 MiB pro aplikaci.

Výchozí sestavení `pio run` používá **ESP-IDF 6.1** v profilu
`esp32-s3-idf`. Původní sériový Arduino prototyp je dostupný samostatně přes
`pio run -e esp32-s3-arduino-serial`. Oba profily byly sestaveny lokálně;
žádný firmware se zatím nenahrával do desky.
Podrobný postup a skutečně provedené kontroly jsou ve
[stavu implementace](../docs/stav-implementace.md). Popis níže je současný
sériový prototyp, nikoli implementované síťové či bezpečnostní řízení.

## Architektura

Firmware roste po vertical slices. Každá funkce drží své rozhodování a své
vstupní/výstupní porty pohromadě. `main.cpp` jen skládá instance a volá je
ve smyčce s časováním bez `delay()`. ESP-IDF `espidf_main.cpp` skládá stejné
bezpečnostní jádro nad FreeRTOS smyčkou. Nezavádíme globální vrstvu
service/repository ani společné rozhraní pro každou periferii předtím, než
bude potřeba.

```text
src/
  main.cpp                                  Arduino sestavení a smyčka
  espidf_main.cpp                           rozpracovaná IDF smyčka bez GPIO
  features/
    safety/
      SafetyPort.h                          požadavky na vypnutí/pohony
      SafetyController.h/.cpp               Stop/porucha a vzájemné vyloučení výstupů
    wand_selection/
      SelectionSequence.h/.cpp              čistá, časovaná posloupnost
      SerialSelectionPort.h/.cpp            vývojové příkazy a výpis požadavků
    device_control/
      DeviceControl.h/.cpp                  hostitelský model ručního příkazu
      CommandJournal.h                     port pro zápis před pohybem
      PersistentCommandJournal.h/.cpp      trvalý journal a maska dostupnosti
      JournalStorage.h                     port trvalého úložiště pro testy
      EspIdfJournalStorage.h/.cpp           NVS blob v ESP-IDF profilu
    provisioning/
      DeviceIdentity.h/.cpp                 formát identity a servisní brána
      ProvisioningStorage.h                 port úložiště identity
      EspIdfProvisioningStorage.h/.cpp      NVS identita v ESP-IDF profilu
    backend_access/
      DeviceTokenSession.h/.cpp             obnova access tokenu v RAM
      TokenResponseParser.h/.cpp            omezená odpověď token endpointu
      EspIdfTokenExchangeTransport.h/.cpp   blokující HTTPS výměna tokenu
      NetworkSettings.h/.cpp                validace Wi-Fi a backend hostu
      ReconnectPolicy.h/.cpp                1–30s čekání mezi pokusy
      TrustedTime.h/.cpp                    datum buildu a poslední známý čas
      EspIdfBackendNetwork.h/.cpp           oddělená síťová úloha
      EspIdfNetworkSettings.h/.cpp          čtení servisních hodnot z NVS
      EspIdfWifiStation.h/.cpp              Wi-Fi STA bez zápisu driver NVS
      EspIdfTrustedTimeStore.h/.cpp         uložený čas v NVS
    conversation/
      ConversationSession.h/.cpp            čistý model generace textového sezení
      ConversationStartCodec.h/.cpp         omezený JSON startu backend sezení
      ConversationStartCoordinator.h/.cpp   jediné obnovení tokenu při 401
      ConversationStartPort.h               port pro test a HTTPS adaptér
      ConversationTextCodec.h/.cpp          validace a JSON textového tahu
      EspIdfConversationStartTransport.h/.cpp  HTTPS start sezení
      EspIdfConversationTextTransport.h/.cpp   HTTPS textový tah
      OperationAcceptedCodec.h/.cpp         omezená odpověď 202
tests/
  selection_sequence_test.cpp               test bez hardwaru
  device_control_test.cpp                   test idempotence a restartu
  persistent_command_journal_test.cpp      test trvalého záznamu a selhání zápisu
  device_identity_test.cpp                  test identity a servisní brány
  device_token_session_test.cpp             test tokenu, obnovy a parseru
  network_settings_test.cpp                 test konfigurace, času a reconnectu
  conversation_session_test.cpp             test textu, zrušení a generace
  conversation_start_codec_test.cpp         test HTTP těla a odpovědi startu
  conversation_start_coordinator_test.cpp   test retry a scope startu
  conversation_text_codec_test.cpp          test UTF-8 a JSON textového tahu
  operation_accepted_codec_test.cpp         test operationId odpovědi
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
Průchod řídicí smyčkou hlídá mezeru 250 ms a při jejím dosažení vyžádá Stop
před zpracováním dalšího sériového příkazu. Model umí samostatně přijmout
poruchu a ztrátu ručního lease; ta zastaví jen výběr spuštěný v ručním režimu,
nikoli samostatný výběr při odpojení pozorovatele. Porucha zůstane odlišená od
Stop a oba stavy vyžadují kontrolu a výslovné zotavení. Tyto vstupy jsou zatím
hostitelský model bez síťového detektoru. Software rozpozná zaseknutí smyčky
až při dalším průchodu; nezávislé vypnutí při úplném zamrznutí musí zajistit
budoucí hardwarový watchdog a inhibit.
Časování je zatím výchozí pro prototyp; délku pulzů a polohu serva
bude třeba doladit podle skutečné mechaniky a měření proudu.

`DeviceControl` je čistý model E2.2/E2.7 pro pozdějšího jediného vlastníka povelů.
Po autentizaci/ověření obálky přijme ruční výběr jen v platném lease, s
očekávaným `bootId`, pořadím a revision. Uchovává posledních 64 výsledků,
takže stejný `commandId` a obsah znovu nespustí výběr. Před pohybem vyžaduje
úspěšný zápis přes `CommandJournal`, potom odděluje přijetí od spuštění
sekvence. Dokončení musí atomicky uložit novou masku dostupnosti a uzavřít
journal; při přerušení záznam zůstává otevřený až do kontroly obsluhou.
Potvrzení znovuzaložení jedné přihrádky sdílí lease, pořadí a cache ID,
vyžaduje kontrolu v klidu a samostatně ukládá novou masku před ACK bez
spuštění výběru nebo pohonu.
`PersistentCommandJournal` ukládá stav před pohybem a po dokončení uzavírá
záznam společně s novou maskou dostupnosti. Chyba čtení, poškozený záznam nebo
chyba zápisu drží řízení v bezpečném stavu; prázdná NVS nezačne s dostupnými
přihrádkami. ESP-IDF profil otevírá NVS adaptér při startu bez automatického
mazání. Hostitelské testy ověřují restart a selhání zápisu přes paměťový port.
`DeviceControl` zatím není připojený k živému vstupu ESP-IDF smyčky; skutečný
NVS běh na desce, síťový parser povelů a servisní USB zápis zbývají.

`DeviceIdentity` čte oddělený verzovaný NVS záznam s `deviceId` a 32 bajty
device credential. Pro HTTP Authorization jej lze vyjádřit 64 malými
hexadecimálními znaky; backend porovnává SHA-256 přesně těchto UTF-8 znaků.
Případný zápis vyžaduje v modelu současně aktivní fyzický servisní režim a
odpojenou větev pohonů. ESP-IDF smyčka zatím identitu pouze čte a oznamuje
stav bez její hodnoty. Skutečné USB servisní rozhraní, ověření fyzické brány,
náhodné generování credential mimo image a běžící TLS spojení zbývají.

`DeviceTokenSession` je čistý model obnovy backend access tokenu po 12 minutách,
jeho 15minutové expirace a jediného nového přihlášení po 401. Drží token pouze
v RAM a odmítá odpověď s jiným `deviceId`, typem tokenu nebo dobou platnosti.
ESP-IDF HTTPS adaptér má pevnou cestu token endpointu, ověřuje server přes CA
bundle a hostname, nepovoluje redirect a omezuje odpověď na 512 bajtů.
`TokenResponseParser` přijímá pouze čtyři očekávaná pole a odmítá duplicity.
ESP-IDF nyní spouští samostatnou síťovou úlohu pouze s platnou device identitou
a kompletní servisní NVS konfigurací v `wand_network` (`ssid`, `password`,
`backend_host`). Wi-Fi konfigurace zůstává v driveru v RAM. Úloha nejprve
získá IP, čeká na SNTP a kontroluje čas proti datu buildu i poslednímu
důvěryhodnému času v NVS. Teprve pak může přes HTTPS vyměnit credential za
token. Výpadek Wi-Fi vyvolá čekání 1/2/4/8/16/30 s s jitterem; síťová úloha
nemá přístup k portu pohonů. Servisní zápis s fyzickou bránou zatím chybí,
takže na prázdné desce síť nezačne. Skutečné Wi-Fi, SNTP ani TLS spojení nebylo
na desce ověřeno. V image není URL ani credential.

`ConversationSession` je čistý model jednoho textového sezení. Kontroluje
ruční lease, boot/session/generation, revision a délku UTF-8 textu. V jednom
sezení přijme nejvýše deset jedinečných `clientTurnId`; opakovaný aktivní tah
rozpozná a po dokončení či zrušení již stejné ID znovu nespustí. Zrušení nebo
ztráta lease zvýší generation a pozdní dokončení se odmítne. Model zatím není
zapojený do WSS ani backendu a neřídí žádný fyzický výstup.
`ConversationStartCodec` sestaví přesné JSON tělo pro založení textového
backend sezení s vypnutým audio/image souhlasem a omezeně parsuje odpověď
včetně serverových limitů. Idempotency-Key je samostatný UUID vstup pro
HTTPS adaptér. `EspIdfConversationStartTransport` volá pevnou cestu
`/v1/conversations`, ověřuje certifikát a hostname přes CA bundle, nepovoluje
redirect ani automatickou autentizaci a vyžaduje odpověď 201 s platným tělem.
`ConversationStartCoordinator` kontroluje device scope a při 401 obnoví token
právě jednou; druhé volání dostane původní tělo a Idempotency-Key. Po chybě
musí volající pro opakování ponechat stejné UUID požadavku.
`ConversationTextCodec` sestaví tělo textového tahu s bezpečným JSON escapingem,
validací UTF-8/1000 znaků, UUID, masky 0–63 a konečným limitem 4096 bajtů.
`OperationAcceptedCodec` přijímá nejvýše 128B odpověď 202 s jediným UUID
`operationId`. `EspIdfConversationTextTransport` posílá validovaný textový tah
jen na pevnou HTTPS cestu daného UUID, s CA bundle, kontrolou hostname,
Authorization a Idempotency-Key. Potvrzení 202 přijme jen s platným ID operace.
Transport ještě není zapojený do runtime; poll výsledku chybí.
Zatím jej nevolá síťová úloha: chybí událost zahájení rozhovoru, UUID generátor,
textové tahy a pollování. Žádná odpověď z backendu neřídí fyzické výstupy.

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
zatím neinicializuje. Sériový Arduino profil neobsahuje přihlašovací údaje
ani síťové připojení. Nahrání do desky je až další krok po kontrole konkrétního
modulu.

## Sestavení a test

Ve složce `kufr/firmware`:

```text
pio run
```

Výchozí profil používá PlatformIO `espressif32@7.1.3`, připnutý ESP-IDF 6.1,
`sdkconfig.defaults` a vlastní kopii 16MB tabulky oddílů. Zatím pouze
vyžaduje bezpečný výstup, prochází čistým jádrem s monotónním časem a vypisuje
požadavky do logu. Inicializuje NVS journal, čte identitu a při kompletním
servisním nastavení spouští síťovou úlohu bez fyzických výstupů. Neobsahuje
sériové příkazy ani GPIO adaptér pohonů.
Skutečný výsledek sestavení a mezery akceptace jsou ve stavu implementace.
Původní sériový prototyp lze sestavit `pio run -e esp32-s3-arduino-serial`.

Jednotkový test čisté sekvence lze sestavit libovolným hostitelským C++
kompilátorem (C++11 nebo novějším), např.:

```text
g++ -std=c++11 -Wall -Wextra -Werror src/features/safety/SafetyController.cpp src/features/wand_selection/SelectionSequence.cpp tests/selection_sequence_test.cpp -o <docasna-slozka>/selection_sequence_test
<docasna-slozka>/selection_sequence_test
```

Test pokrývá pořadí a časování kroků, neplatný či souběžný výběr, Stop v každé
fázi, opakovaný Stop, explicitní zotavení, přetočení časovače, zpožděný průchod
smyčkou, poruchu, ztrátu ručního lease a vzájemné vyloučení výstupů. Nepotřebuje
ESP32 ani knihovnu Arduino.
Na Windows lze použít také Visual Studio Developer Command Prompt a `cl`.

Hostitelský test modelu příkazů používá stejné čisté zdroje a navíc
`device_control/DeviceControl.cpp`:

```text
g++ -std=c++11 -Wall -Wextra -Werror src/features/safety/SafetyController.cpp src/features/wand_selection/SelectionSequence.cpp src/features/device_control/DeviceControl.cpp tests/device_control_test.cpp -o <docasna-slozka>/device_control_test
<docasna-slozka>/device_control_test
```

Pokrývá ztracený ACK a opakování, konflikt ID, mezery v pořadí, vytěsnění z
cache, chybu zápisu/uzavření journalu, restart po dokončení i přerušeném
uvolnění, potvrzený reload a jeho trvalou masku, Stop a výpadek ručního lease při aktivním výstupu. Neověřuje NVS ani
skutečnou síť.

Hostitelský test trvalého journalu používá také čistý `DeviceControl` a
paměťový port úložiště:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/safety/SafetyController.cpp src/features/wand_selection/SelectionSequence.cpp src/features/device_control/DeviceControl.cpp src/features/device_control/PersistentCommandJournal.cpp tests/persistent_command_journal_test.cpp -o <docasna-slozka>/persistent_command_journal_test
<docasna-slozka>/persistent_command_journal_test
```

Pokrývá restart s otevřeným záznamem, společné uzavření s maskou, chyby
čtení/zápisu, poškozený blob a blokaci pohybu před úspěšným zápisem.

Formát identity a servisní bránu lze hostitelsky ověřit samostatně:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/provisioning/DeviceIdentity.cpp tests/device_identity_test.cpp -o <docasna-slozka>/device_identity_test
<docasna-slozka>/device_identity_test
```

Stav tokenu a parser omezené HTTP odpovědi:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/provisioning/DeviceIdentity.cpp src/features/backend_access/DeviceTokenSession.cpp src/features/backend_access/TokenResponseParser.cpp tests/device_token_session_test.cpp -o <docasna-slozka>/device_token_session_test
<docasna-slozka>/device_token_session_test
```

Validace servisní sítě, časové brány a odstupů reconnectu:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/backend_access/BuildDate.cpp src/features/backend_access/NetworkSettings.cpp src/features/backend_access/ReconnectPolicy.cpp src/features/backend_access/TrustedTime.cpp tests/network_settings_test.cpp -o <docasna-slozka>/network_settings_test
<docasna-slozka>/network_settings_test
```

Čistý model textového sezení lze ověřit samostatně:

```text
g++ -std=c++11 -Wall -Wextra -Werror src/features/conversation/ConversationSession.cpp tests/conversation_session_test.cpp -o <docasna-slozka>/conversation_session_test
<docasna-slozka>/conversation_session_test
```

JSON tělo a omezenou odpověď startu backend sezení lze ověřit samostatně:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/conversation/ConversationStartCodec.cpp tests/conversation_start_codec_test.cpp -o <docasna-slozka>/conversation_start_codec_test
<docasna-slozka>/conversation_start_codec_test
```

Jednorázové obnovení tokenu a opakování založení sezení:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/provisioning/DeviceIdentity.cpp src/features/backend_access/DeviceTokenSession.cpp src/features/conversation/ConversationStartCodec.cpp src/features/conversation/ConversationStartCoordinator.cpp tests/conversation_start_coordinator_test.cpp -o <docasna-slozka>/conversation_start_coordinator_test
<docasna-slozka>/conversation_start_coordinator_test
```

Omezené JSON tělo textového tahu:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/conversation/ConversationSession.cpp src/features/conversation/ConversationStartCodec.cpp src/features/conversation/ConversationTextCodec.cpp tests/conversation_text_codec_test.cpp -o <docasna-slozka>/conversation_text_codec_test
<docasna-slozka>/conversation_text_codec_test
```

Odpověď 202 s ID operace:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/conversation/ConversationStartCodec.cpp src/features/conversation/OperationAcceptedCodec.cpp tests/operation_accepted_codec_test.cpp -o <docasna-slozka>/operation_accepted_codec_test
<docasna-slozka>/operation_accepted_codec_test
```

## Další slices a podmínky zapojení

Pořadí a akceptace se řídí kapitolou 12 plánu; rozpracovaný krok se vede
výhradně ve stavu implementace. E1 přidalo Stop, safety port, časové limity a
testy bez desky; E2.2/E2.7 rozvíjí model ručního příkazu. Další krok určuje stav; migrace
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
