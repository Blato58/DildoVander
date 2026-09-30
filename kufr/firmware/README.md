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
[stavu implementace](../docs/stav-implementace.md). ESP-IDF profil má
read-only síťový základ a nyní i interní cestu pro jeden explicitně předaný
fake textový tah nad atomickým snapshotem stavu. Snapshot vydává vlastník
`DeviceControlRuntime` pouze po bezpečných přechodech řízení. Autorizovaný zdroj události,
místní WSS a fyzické výstupy zatím připojené nejsou.

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
    wand_presentation/
      WandPresentation.h/.cpp               autonomní vyzdvižení, ukázání a návrat rekvizity
    device_control/
      DeviceControl.h/.cpp                  hostitelský model ručního příkazu
      DeviceControlRuntime.h/.cpp           jediný vlastník změn a snapshotu v IDF smyčce
      CommandJournal.h                     port pro zápis před pohybem
      PersistentCommandJournal.h/.cpp      trvalý journal a maska dostupnosti
      JournalStorage.h                     port trvalého úložiště pro testy
      EspIdfJournalStorage.h/.cpp           NVS blob v ESP-IDF profilu
    local_control/
      LocalControlSession.h/.cpp            hello a ruční lease jednoho ověřeného socketu
      LocalConversationGateway.h/.cpp        text/cancel brána vlastníka řízení
      LocalConversationNetworkPort.h         síťový port brány
    provisioning/
      DeviceIdentity.h/.cpp                 formát identity a servisní brána
      ProvisioningStorage.h                 port úložiště identity
      EspIdfProvisioningStorage.h/.cpp      NVS identita v ESP-IDF profilu
      LocalPairingCredential.h/.cpp         jednorázové párování a otisk místního tokenu
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
      ConversationUuid.h/.cpp               kanonický UUID v4 formát
      ConversationTextInbox.h/.cpp           jediná explicitní čekající textová událost
      ConversationTurnFailureInbox.h/.cpp     terminální chyba přijatého tahu
      ConversationRuntimeState.h/.cpp         atomický stav a verze pro síťový tah
      EspIdfConversationStartTransport.h/.cpp  HTTPS start sezení
      EspIdfConversationTextTransport.h/.cpp   HTTPS opening/text/DELETE
      EspIdfConversationUuid.h/.cpp         UUID z HW RNG po startu Wi-Fi
      EspIdfTurnPollTransport.h/.cpp       HTTPS GET a korelace výsledku
      OperationAcceptedCodec.h/.cpp         omezená odpověď 202
      TurnPollGuard.h/.cpp                  korelace pollovaného výsledku
      TurnPollSchedule.h/.cpp               intervaly a deadline pollu
      TurnOperationCodec.h/.cpp            omezený JSON poll výsledku
      PendingProposal.h/.cpp               expirovaný datový návrh pro vlastníka prezentace
tests/
  selection_sequence_test.cpp               test bez hardwaru
  device_control_test.cpp                   test idempotence a restartu
  device_control_runtime_test.cpp           test verzování a invalidace snapshotu
  persistent_command_journal_test.cpp      test trvalého záznamu a selhání zápisu
  device_identity_test.cpp                  test identity a servisní brány
  device_token_session_test.cpp             test tokenu, obnovy a parseru
  network_settings_test.cpp                 test konfigurace, času a reconnectu
  conversation_session_test.cpp             test textu, zrušení a generace
  conversation_runtime_state_test.cpp       test atomického snapshotu a změny verze
  conversation_start_codec_test.cpp         test HTTP těla a odpovědi startu
  conversation_start_coordinator_test.cpp   test retry a scope startu
  conversation_text_codec_test.cpp          test UTF-8 a JSON textového tahu
  operation_accepted_codec_test.cpp         test operationId odpovědi
  turn_poll_guard_test.cpp                  test pozdních/cizích výsledků
  turn_operation_codec_test.cpp             test JSON odpovědi pollu
  pending_proposal_test.cpp                 test expirace a invalidace návrhu
  wand_presentation_test.cpp                úspěch, Stop, timeout a dvojí návrh
```

Původní E1 prototyp řídí výběr jedné ze šesti hůlek se západkou; nová cílová
prezentace používá zdvih a spuštění servem. `SelectionSequence` přijímá
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
`DeviceControlRuntime` nyní běží v ESP-IDF smyčce jako jediný vlastník
`DeviceControl` a publikuje atomický snapshot až po zotavení a bezpečném
uzavření journalu. Přijetí výběru, aktivní pohyb, Stop a porucha jej invalidují;
změny lease, revision a masky publikují novou verzi. Sdílené boot ID již vzniká
při startu, nulová kalibrace zatím záměrně nedovoluje spustit pohon. Živý vstup příkazů,
skutečný NVS běh na desce, síťový parser a servisní USB zápis zbývají.
Po autorizovaném zrušení textového rozhovoru `CancelConversation` zvýší stejnou
generation jako ruční Stop nebo ztráta lease a vydá nový snapshot. Kontroluje
vlastníka lease, monotónní čas před expirací a očekávanou generation. Místní
vlastník nejprve neměnně ověří `ConversationSession::ValidateCancel`, pak zvýší
generation v řízení a potvrdí `ConversationSession::Cancel` na stejné řídicí
úloze. Po Stop nebo ztrátě lease přenese generation do sezení přes
`SynchronizeGeneration`, čímž zahodí starý textový tah. Hostitelská
`LocalConversationGateway` toto pořadí vykonává; živý WSS vstup ji zatím
nevytváří.
`LocalControlSession` je čistý model jednoho control socketu po jeho budoucí
TLS/token autentizaci. Na řídicí úloze vyžaduje `hello` verze 1 do 10 s,
správné device/boot ID a UUID lease. Ruční lease vytváří a obnovuje výhradně
existující `DeviceControlRuntime`. Odpojení pozorovatele nemění samostatný
režim; odpojení vlastníka ukončí lease a zvýší generation. Po vypršení lease
je další takeover možný teprve po místním zotavení. Třída sama neověřuje
token, nepřijímá JSON ani nevystavuje WSS. ESP-IDF smyčka ji zatím nevytváří.

`DeviceIdentity` čte oddělený verzovaný NVS záznam s `deviceId` a 32 bajty
device credential. Pro HTTP Authorization jej lze vyjádřit 64 malými
hexadecimálními znaky; backend porovnává SHA-256 přesně těchto UTF-8 znaků.
Případný zápis vyžaduje v modelu současně aktivní fyzický servisní režim a
odpojenou větev pohonů. ESP-IDF smyčka zatím identitu pouze čte a oznamuje
stav bez její hodnoty. Skutečné USB servisní rozhraní, ověření fyzické brány,
náhodné generování credential mimo image a běžící TLS spojení zbývají.

`LocalPairingCredential` je oddělený čistý model pro místní telefonní token.
Servisně přijme nejméně 16bajtové jednorázové tajemství jen při fyzické bráně;
do záznamu ukládá pouze jeho SHA-256 otisk. Po ověření tajemství si vyžádá
nový 256bitový token od portu s hardwarovou entropií, uloží pouze jeho otisk
a jednorázové tajemství zneplatní. Token se vydá volajícímu právě při párování;
model ho neukládá. Po restartu porovnává otisky bez předčasného ukončení.
Poškozený záznam či neúspěšný zápis odmítá. Zatím existuje jen model s
hostitelskými fake porty; skutečný SHA-256/RNG/NVS adaptér, párovací AP,
TLS klíč/certifikát a WSS handshake nejsou připojené.

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
ruční lease, boot/session/generation, revision a délku UTF-8 textu. Místní
`LocalConversationGateway` ověří lease a snapshot; duplicitní tah vrátí bez
nového síťového požadavku. Nový tah nejprve potvrdí v sezení, pak jej předá
síťové úloze. Při okamžitém odmítnutí fronty vrátí jen tento dosud
nezařazený tah a původní ID lze zkusit znovu. Při pozdějším síťovém selhání
aktivní tah uvolní, ale ID ponechá spotřebované: backend ho mohl přijmout.
Změna snapshotu zneplatní i uloženou či právě předávanou repliku. Všechna
volání brány patří jediné řídicí úloze. Zrušení síťové fronty je vázané na
přijaté ID rozhovoru a verzi snapshotu, takže pozdní událost starého socketu
nevymaže nový tah. V jednom sezení přijme nejvýše deset
jedinečných `clientTurnId`; opakovaný aktivní tah
rozpozná a po dokončení či zrušení již stejné ID znovu nespustí. Zrušení nebo
ztráta lease zvýší generation a pozdní dokončení se odmítne. Model zatím není
zapojený do WSS a neřídí žádný fyzický výstup. `EspIdfBackendNetwork`
implementuje síťový port brány a předává terminální chybu přes omezenou
schránku. IDF smyčka bránu zatím nevytváří ani nevolá `PollNetworkFailure`.
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
Transport používá síťová úloha pro textový tah i explicitní opening bez
vymyšleného textu; umí také DELETE při ukončení sezení.
`TurnPollGuard` porovnává operation ID i všech šest korelačních polí s aktuální
generací a revision. U návrhu kontroluje také ID sezení a tahu, boot,
generaci, revision, katalog, dostupnou přihrádku a UTC expiraci do 60 s.
`TurnOperationCodec` přijímá nejvýše 4096B JSON operace
`turn`, dekóduje UTF-8 i JSON Unicode escape a odmítá duplicity, neznámá pole,
nekonzistentní status/result/error a neúplný typovaný návrh. `EspIdfTurnPollTransport`
čte GET `/v1/operations/{id}` jen přes ověřené HTTPS, omezuje tělo na 4096 B
a propouští odpověď pouze po `TurnPollGuard` korelaci.
`TurnPollSchedule` povoluje vždy jediný dotaz, první po 500 ms, další po
500 ms a od 3 s po 1 s; po 45 s od začátku tahu už další poll nepovolí.
`ConversationUuid` formátuje 16 náhodných bajtů jako UUID v4;
`EspIdfConversationUuid` čerpá HW RNG až po startu Wi-Fi. Síťová úloha jím
odděluje nové sezení a jeho idempotency key od zrušeného sezení.
`PendingProposal` drží pouze data s lokálním monotónním deadlinem a při změně
autoritativního snapshotu je nevydá. Žádná odpověď z backendu zatím neřídí
fyzické výstupy.
`ConversationReplyInbox` podrží nejvýše jednu validovanou textovou repliku
svázanou s přesným snapshotem. Síťová úloha ji po úspěšném pollu předá přes
`TakeReply` s původním `conversationId`, `clientTurnId` a tokenem; nový tah ani autonomní
prezentace nezačnou do
`FinishReply`. Změna snapshotu nebo explicitní zrušení token zneplatní.
Volající musí `IsReplyCurrent` kontrolovat před výstupem i během něj a při
zrušení zastavit skutečný výstupní port. Přehrávací/TTS port v IDF smyčce
zatím neexistuje, takže replika dosud není slyšet ani zobrazena.
`WandPresentation` je čistý autonomní model pro hůlku, která zůstává v přihrádce.
Po jednorázovém převzetí validovaného návrhu vyžádá zdvih, čeká na potvrzenou
horní polohu, ukáže rekvizitu 5 s, vyžádá spuštění a čeká na potvrzenou klidovou
polohu. Úspěch dovolí další cyklus bez ručního resetu. Stop, timeout nebo
selhání portu vypne pohony a zablokuje další cyklus do kontroly polohy.
Rozhraní `WandPresentationPort` vyžaduje kalibraci, bezpečnostní větev,
journal a publikaci stavu od skutečného adaptéru. Takový adaptér ani senzor
polohy nyní neexistují; model se v `espidf_main.cpp` nespouští.
`DeviceControlRuntime` má pro budoucí port autonomní transakci bez ručního
lease: před zdvihem ověří přesný snapshot a trvale otevře journal, čímž
zneplatní dostupnost. Potvrzený návrat journal atomicky uzavře se zachovanou
maskou a zveřejní novou revizi. Stop nebo porucha nechá záznam otevřený k
fyzické kontrole. Hostitelský fake port ověřuje celý cyklus; není to připojení
servo výstupu do smyčky ESP32.

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
Před Wi-Fi krátce zapne interní zdroj entropie, vytvoří jediné `bootId` pro
`DeviceControl` i backendového klienta a zdroj opět vypne. Stejné ID může
později dostat místní WSS vrstva; skutečný běh RNG na desce zatím nebyl ověřen.
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

Test vlastníka snapshotu přidává `DeviceControlRuntime.cpp`,
`ConversationRuntimeState.cpp`, `PendingProposal.cpp` a
`WandPresentation.cpp` k uvedeným čistým zdrojům a spouští
`tests/device_control_runtime_test.cpp`. Ověřuje dostupnost až po zotavení,
invalidaci při čekajícím journalu a Stop, novou verzi po výběru a reloadu,
selhání zápisu a vypršení nečinného ručního lease. Není to test GPIO, NVS ani
skutečné síťové souběžnosti na ESP32.
`tests/local_control_session_test.cpp` proti stejnému čistému řídicímu modelu
ověřuje hello deadline, boot/lease ID, heartbeat, odpojení pozorovatele,
vypršení vlastníka a zotavení před dalším takeover. Spouští jej firmware CI;
neověřuje autentizaci TLS/WSS.
`tests/autonomous_presentation_lifecycle_test.cpp` skládá stejné čisté zdroje
s `PendingProposal.cpp` a `WandPresentation.cpp` do hostitelského cyklu.
Ověřuje zápis journalu před zdvihem, invalidaci a obnovení snapshotu,
opakovaný cyklus, Stop, expirovaný/starý návrh a chyby zápisu. Stejný test
spouští firmware CI; neověřuje polohu ani bezpečnost fyzického serva.

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

Korelace pozdních odpovědí:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/conversation/TurnPollGuard.cpp tests/turn_poll_guard_test.cpp -o <docasna-slozka>/turn_poll_guard_test
<docasna-slozka>/turn_poll_guard_test
```

JSON odpověď fake pollu:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/conversation/ConversationSession.cpp src/features/conversation/ConversationStartCodec.cpp src/features/conversation/TurnPollGuard.cpp src/features/conversation/TurnOperationCodec.cpp tests/turn_operation_codec_test.cpp -o <docasna-slozka>/turn_operation_codec_test
<docasna-slozka>/turn_operation_codec_test
```

Formát UUID v4 pro ESP-IDF požadavky:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/conversation/ConversationStartCodec.cpp src/features/conversation/ConversationUuid.cpp tests/conversation_uuid_test.cpp -o <docasna-slozka>/conversation_uuid_test
<docasna-slozka>/conversation_uuid_test
```

Časování pollu a jediný aktivní dotaz:

```text
g++ -std=c++14 -Wall -Wextra -Werror src/features/conversation/TurnPollSchedule.cpp tests/turn_poll_schedule_test.cpp -o <docasna-slozka>/turn_poll_schedule_test
<docasna-slozka>/turn_poll_schedule_test
```

Vstupní schránku textového tahu lze ověřit samostatně:

```text
g++ -std=c++14 -Wall -Wextra -Werror -pthread src/features/conversation/ConversationSession.cpp src/features/conversation/ConversationStartCodec.cpp src/features/conversation/ConversationRuntimeState.cpp src/features/conversation/ConversationTextInbox.cpp tests/conversation_text_inbox_test.cpp -o <docasna-slozka>/conversation_text_inbox_test
<docasna-slozka>/conversation_text_inbox_test
```

Schránku odpovědi a její vazbu na snapshot ověřuje
`tests/conversation_reply_inbox_test.cpp` spolu s
`ConversationSession.cpp`, `ConversationRuntimeState.cpp` a
`ConversationReplyInbox.cpp`; stejný test spouští firmware CI.
`tests/conversation_turn_correlation_test.cpp` skládá obě schránky,
normalizaci UUID, backendovou korelační bránu a model sezení; ověřuje, že
původní místní ID tahu i sezení projdou až do předané odpovědi. Také jej
spouští firmware CI.

`tests/turn_replay.cpp` je hostitelský CLI průchod skutečným JSON výsledkem
operace přes parser, korelační bránu, dočasný návrh a fake servo port. Přijímá
v pořadí `operation.json operationId deviceId bootId conversationId clientTurnId
generation revision catalogVersion readyMask nowUtcSeconds`; při úspěchu
vypíše `SIMULATED raise/lower/complete`. Parametry musí pocházet z odeslaného
tahu a živého snapshotu, nikoli z vrácené odpovědi. Výstup je simulace bez
GPIO a pro replay je nutná dosud neexpirovaná operace.

ESP-IDF síťová úloha přijme přes `QueueTextTurn` nejvýše jeden čekající
validovaný UTF-8 text s místním UUID `conversationId` a `clientTurnId`. Po důvěryhodném čase a tokenu
založí nebo znovu použije
backend sezení, odešle tah a omezeně polluje výsledek. `QueueOpening` přijímá
jen explicitní místní událost a odesílá `/opening` bez textu; reconnect ji
nevytváří. Po `no_selection`, chybě/timeoutu pollu či 90 sekundách nečinnosti
úloha volá DELETE. Při chybě DELETE drží ID sezení a stejný idempotency key,
opakuje s čekáním 1–30 s a nepovolí založit další sezení. Po 204/404 vymaže
koordinátor a vymění create key. Ověřený
`propose` uloží jen jako dočasná data dostupná přes `ReadPendingProposal`.
Textový tah posílá backendu stejné `clientTurnId` jako budoucí místní
`conversation.text` v kanonickém malém zápisu; opening bez klientského tahu
dostane nové lokální UUID. Původní zápis ID se vrací budoucímu místnímu
výstupu a idempotency key zůstává oddělený. Nulové UUID se odmítá.
Místní `conversationId` je odlišné od backendového ID; síťová úloha drží
vazbu pro jediné aktivní sezení. Nový opening odstraní čekající text jiného
místního sezení. Tah s jiným ID se odmítne i při neúspěšném backendovém
startu, dokud nový opening nebo změna snapshotu nevytvoří novou vazbu. Opening
dostává ID od místního vlastníka rozhovoru; reconnect je nevytváří. Odpověď
vrací původní místní ID, backendová korelace používá backendové ID.
Po doběhnutí či poruše cyklu má vlastník řízení zavolat `FinishPresentation`;
nepřevzatý návrh se po vypršení lokálního deadline uklidí automaticky.
Volání `QueueTextTurn` a `QueueOpening` zatím nemají v runtime autorizovaného
producenta; odpověď zatím nemá TTS/přehrání a návrh nemá kalibrovaný servo
adaptér. `BeginPendingPresentation` je samostatný vstup vlastníka řízení do
čistého modelu; síťová úloha jej sama nevolá. `QueueCancel` ruší čekající
události a vyžádá DELETE. Volající musí předat přesný snapshot, proti kterému už ověřil
vstup; fronta jej těsně při přijetí znovu porovná a odmítne starou nebo
nedostupnou verzi. `ConversationRuntimeState` dává síťové úloze jeden uzamčený
snapshot generace, revize, masky a katalogu; textová schránka uchovává jeho
verzi a po pozdější změně stavu se starý tah zahodí. Inicializace čte dostupnost z NVS journalu,
katalog je dočasná verze `neutral-1` a revize/generace jsou 0, protože IDF
profil dosud nemá běžící místní řízení. Budoucí vlastník řízení musí po každé
změně journalu, revize nebo generace publikovat snapshot a při poruše jej
zneplatnit. Bez autorizovaného WSS a platného manual lease se text nepřijímá.

## Další slices a podmínky zapojení

Pořadí a akceptace se řídí kapitolou 12 plánu; rozpracovaný krok se vede
výhradně ve stavu implementace. E1 přidalo Stop, safety port, časové limity a
testy bez desky; E2.2/E2.7 rozvíjí model ručního příkazu. Další krok určuje stav; migrace
runtime a přímé HTTPS/WSS spojení patří E4. E5/E6 teprve připojí jednu
a potom šest skutečných přihrádek po ověření zapojení a bezpečnostního obvodu.

Cílový firmware vlastní samostatný PTT rozhovor s backendem, návrh AI,
autonomní zdvih/ukázání/spuštění rekvizity, audio a bezpečnou sekvenci.
Telefon komunikuje místně
přes WSS jako volitelná záloha; backend neposílá povely k otevření.
Slices a jejich odpovědnosti jsou v kapitole 3 plánu. Čisté jádro zachovat
testovatelné bez Arduino/ESP-IDF; síťový callback nesmí přímo spínat GPIO.

Pro skutečné výstupy platí jas zpočátku kolem 25 %, nejvýše jeden pohon
v pohybu a nezávislá vypínaná větev pohonů s watchdogem. Po Stop/resetu
není poloha rekvizity známá bez kontroly, proto se automaticky nespouští
další prezentace.
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
