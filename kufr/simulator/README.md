# Hostitelský simulátor výběru

Simulátor E2.3 spouští čistý C++ model `DeviceControl` a `SelectionSequence`
proti fake portu a journalu. Každý řádek časové osy začíná `SIMULACE`.
Požadavky na motor a západku neovládají hardware.

Z kořene repozitáře sestavte čtyři zdroje C++14 do dočasné složky. Na Windows
ve Visual Studio Developer Command Prompt například:

```text
cl /nologo /std:c++14 /W4 /WX /EHsc kufr\firmware\src\features\safety\SafetyController.cpp kufr\firmware\src\features\wand_selection\SelectionSequence.cpp kufr\firmware\src\features\device_control\DeviceControl.cpp kufr\simulator\main.cpp /Fe:<docasna-slozka>\simulator.exe
node kufr\simulator\verify-scenarios.mjs <docasna-slozka>\simulator.exe
```

Ověřovač bere číslo přihrádky a ID povelu ze sdílené platné E2.1 fixture.
Spouští šest deterministických scénářů: běžný výběr, ztracený ACK se dvěma
opakováními, Stop, ztrátu lease, restart při uvolňování a chybu zápisu journalu.
Kontroluje časovou osu, nejvýše jedno uvolnění, stav journalu a dostupnost
přihrádky. Tato fixture se v tomto kroku nepředává přes JSON parser do C++;
propojení syrového protokolu a modelu je další integrační práce E2.

Ruční běh: `<docasna-slozka>\simulator.exe normal 4 <commandId>`.
Tento CLI běh je pouze simulace. Neověřuje fyzické vypnutí, NVS atomicitu,
TLS, skutečné WSS spojení ani časování na ESP32.

## Místní WSS facade E2.4

`bridge.cpp` zpřístupňuje stejný C++ `DeviceControl` procesu Node. Pro sestavení
ve Visual Studio Developer Command Prompt z kořene repozitáře:

```text
cl /nologo /std:c++14 /W4 /WX /EHsc kufr\firmware\src\features\safety\SafetyController.cpp kufr\firmware\src\features\wand_selection\SelectionSequence.cpp kufr\firmware\src\features\device_control\DeviceControl.cpp kufr\simulator\bridge.cpp /Fe:<docasna-slozka>\bridge.exe
```

Node verze 24.21.0 je v `.nvmrc`. V `kufr/contracts` i `kufr/simulator`
spusťte `npm ci`. V `kufr/simulator` nastavte `KUFR_SIMULATOR_BRIDGE` na
absolutní cestu sestaveného procesu a `OPENSSL` na dostupné `openssl` CLI;
`npm test` v dočasné složce vytvoří jednodenní certifikát pro localhost a
ověří TLS spojení s důvěryhodným certifikátem, bearer autorizaci, handshake,
lease, retry, Stop, snapshot a odmítnutí chybné zprávy.

`run-server.mjs` vyžaduje také proměnné `KUFR_SIMULATOR_CERT`,
`KUFR_SIMULATOR_KEY`, `KUFR_SIMULATOR_TOKEN` a `KUFR_SIMULATOR_JOURNAL`
(absolutní cesta mimo repozitář) před spuštěním. Token ani
soukromý klíč nedávejte do repozitáře. Server naslouchá pouze na
`127.0.0.1:8443`, přijímá jeden autorizovaný control socket, odmítá kompresi
a zprávy nad 2048 bajtů; token patří do `Authorization: Bearer` při WSS
handshake. Vstup prochází sdíleným přísným E2 parserem. Certifikát klient
musí ověřit nebo připnout, test jej používá jako explicitní CA.

Souborový fake journal zachová nedokončený výběr a masku dostupnosti přes
restart procesu. Pomocný soubor `.lock` brání současnému zápisu dvou procesů
a označuje již inicializovaný journal. Při neúplném, poškozeném nebo chybějícím
záznamu u dříve použité cesty uzavře výběr v chybovém
stavu. Po restartu s neuzavřeným záznamem se kufr simulovaně zotaví až po
kontrole obsluhou: do konzole `run-server.mjs` zadejte `inspected`.
Tento souborový log není NVS ani důkaz atomicity či odolnosti proti výpadku
napájení; používá `flush`, nikoli fyzicky ověřený fsync. Journal ani jeho
`.lock` soubor nemažte jen kvůli opětovnému otevření přihrádky.

Aktuální WSS facade nepokrývá média, Bonjour ani běh ESP32. Je to vývojový
simulátor, nikoli produkční server nebo náhrada fyzické akceptace.
