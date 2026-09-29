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
cl /nologo /std:c++14 /W4 /WX /EHsc kufr\firmware\src\features\safety\SafetyController.cpp kufr\firmware\src\features\wand_selection\SelectionSequence.cpp kufr\firmware\src\features\device_control\DeviceControl.cpp kufr\firmware\src\features\conversation\ConversationSession.cpp kufr\simulator\bridge.cpp /Fe:<docasna-slozka>\bridge.exe
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
a běžné zprávy nad 2048 bajtů; `conversation.text` má smluvní limit 8192
bajtů. Facade předává text a cancel do C++ modelu, vrací ACK a po fake
dokončení pevnou odpověď se `simulated:true`. Text nikdy nevydá událost
motoru či západky. Token patří do `Authorization: Bearer` při WSS
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
Záznam `M` ukládá potvrzené znovuzaložení přihrádky mimo transakci výběru;
WSS příkaz `inventory.confirmReload` vyžaduje platný ruční lease, kontrolu
obsluhou, čerstvou revision a pořadí. Bridge vrací ACK a snapshot, bez
události motoru či západky.

Snapshot nyní obsahuje zařízením založené `conversationId`, `generation`
a aktivní tah. Stejný aktivní `clientTurnId` se pouze potvrdí znovu;
již dokončené či zrušené ID se v rámci sezení znovu nepřijme. Cancel,
Stop a ztráta ručního lease zneplatní pozdní fake výsledek. Čistý
`ConversationSession` má hostitelský test a `verify-conversation.mjs`
kontroluje propojení s bridge, ale skutečný HTTPS backend a ověření jeho
echo identity zde zatím nejsou.

## Webová časová osa E2.5

Při spuštěném `run-server.mjs` otevřete `https://127.0.0.1:8443/simulator`.
Stránka má výrazné označení `SIMULACE` a ukazuje stav šesti přihrádek,
fázi, režim, journal a posledních 100 událostí C++ modelu. Neobsahuje
ovládání výběru ani pohonů. Prohlížeč musí důvěřovat místnímu vývojovému
certifikátu; neobcházejte jeho bezpečnostní upozornění.

Testovací token z `KUFR_SIMULATOR_TOKEN` zadejte do pole stránky. Stránka
jej po zahájení spojení vymaže z pole a použije jen v `Authorization: Bearer`
hlavičce požadavku na `GET /v1/observe`. Tento místní HTTPS NDJSON proud
posílá snapshot a události stejného C++ modelu, má nejvýše čtyři pozorovatele
a neuděluje manual lease. Je to vývojové rozhraní pro webovou stránku;
nativní telefonní kontrakt zůstává na `wss://127.0.0.1:8443/v1/control`.
Token nikdy nevkládejte do URL, repozitáře ani snímků obrazovky.

## Místní media socket E2.9b

`/v1/media` má vlastní TLS/WSS socket se stejnou bearer autentizací. Po
acquire dostane vlastnický control socket náhodný `mediaTicket` ve zprávě
`media.authorize`; pozorovací snapshot jej neobsahuje. Telefon pošle
`media.hello` s ticketem, bootem, platným ručním lease a conversation
ID/generation ze snapshotu; simulátor přidělí `streamId` a povolí pouze PCM16 mono 32 kHz
z telefonu do kufru. Binární `KF` rámce mají payload nejvýše 4096 B a
pořadí od nuly. Klient smí odesílat jen v přiděleném 16KiB credit okně;
`media.end` vyžaduje 19 200–1 920 000 B sudé délky a přesný SHA-256.
`media.cancel` stream zahodí. Chybný rámec/hash, 1 s bez pokroku, Stop nebo
ztráta lease spojení ukončí. Řídicí heartbeat a Stop používají vlastní
socket. `verify-media-socket.mjs` testuje místní TLS, oddělení druhého
držitele bearer tokenu bez ticketu, credit, dobrý/chybný hash, mezeru
v pořadí, timeout, cancel, uvolnění lease a Stop.

Výsledek příjmu je výslovně `simulated:true`: PCM se pouze průběžně hashují,
nevzniká přepis, WAV ani HTTP upload. Přehrávání/JPEG a skutečný PSRAM buffer
zůstávají dalšími kroky. Facade stále nepokrývá Bonjour ani běh ESP32. Je to
vývojový simulátor, nikoli produkční server nebo náhrada fyzické akceptace.
