# Připravenost na akci a provozní checklist

**Aktuální předání: D163.** [CI 37928528451](https://github.com/Blato58/DildoVander/actions/runs/37928528451)
prošlo; development OTA **0.1.160** je [publikovaná 37929155208](https://github.com/Blato58/DildoVander/actions/runs/37929155208)
s ověřeným veřejným podpisem, hashem a embedded verzí.
iOS **1.0 / build 1000.0.21** prošlo [native testy a podpisem 37929159117](https://github.com/Blato58/DildoVander/actions/runs/37929159117)
a [Safari publikací 37929535139](https://github.com/Blato58/DildoVander/actions/runs/37929535139);
instalační stránka, manifest, veřejná IPA a checksum byly ověřené.
Server přijímá nabídku WS extensions
bez jejího výběru; čtyři skutečné Apple WSS požadavky v harnessu ji obsahovaly.
Mobil vypisuje bezpečný failureCode a omezuje reconnect. Instalace nových obrazů
a stabilní LAN provoz na fyzické sestavě zbývají podle
[stavu implementace](../kufr/docs/stav-implementace.md#předání-další-ai).

**Historická publikace D162.** Rozhodující záznam je v
[předání ve stavu implementace](../kufr/docs/stav-implementace.md#předání-další-ai):
development OTA **0.1.157**, [publikace37923929435](https://github.com/Blato58/DildoVander/actions/runs/37923929435),
a iOS **1.0 / build1000.0.20**, [podpis37923948887](https://github.com/Blato58/DildoVander/actions/runs/37923948887)
a [publikace37924314161](https://github.com/Blato58/DildoVander/actions/runs/37924314161).
Publikace sama nepotvrzuje instalaci ani fyzickou akceptaci těchto verzí.
Majitel následně hlásí funkční vyhledání a párování, ale LAN spojení se opakovaně
odpojuje. D163 opravuje zdrojovou nekompatibilitu, ale stabilní LAN ovládání
na sestavě zatím není přijaté.

Stav k 9. 10. 2026: software obsahuje BLE konfiguraci, hotspot/fallback, OTA
a volitelné SD assety. Lokální testy potvrzují dílčí softwarové chování;
**připravenost celé fyzické sestavy na akci ještě není doložená**.
**Následující výsledky D158/D159 jsou historické, nikoli zkouška aktuální publikace.**
V D159 byl ověřen první USB servis, flash a NVS readback konkrétní desky;
majitel potvrdil iPhone BLE spojení a domácí Wi-Fi i po restartu.
Fyzický test odhalil a source/regrese opravily token POST bug ESP-IDF6.1.
Deska sama provedla podepsanou OTA0.1.0→0.1.1, potvrdila VALID boot,
zachovala NVS settings a přihlásila se k backendu i po dalším resetu;
majitel potvrdil připojenou Wi-Fi/backend na iPhonu.
Hotspot, zátěž a úplná fyzická akceptace zbývají. Skutečný macOS15/
Xcode26.3 package resolver CI37887830815 prošel; nativní build/podpis má
samostatné ověření. Plný CI37895236311 prošel včetně hlavního ESP-IDF a čtyř
servisních buildů. Firmware CI37912208656 a development publish37912902381
prošly pro0.1.1 (1732288 B z 6553600 B slotu). Development OTA a backend
jsou nasazené; public HTTPS bajty/hash a skutečný podpis byly ověřeny.
macOS15/Xcode26.3 run37899385679 prošel všemi native testy, archive, export,
Ad Hoc podpisem i profile kontrolou. iOS1.0/build1000.0.19 je zveřejněn přes
run37899776001 s ověřeným public HTTPS manifestem/IPA/SHA-256.
Jejich skutečné výsledky, verze a běhy zapisuje
[stav implementace](../kufr/docs/stav-implementace.md); plánovaný běh není úspěch.

## Příprava před akcí

- [x] Na desce `kufr-68ee8f4b6048` mít první OTA-capable obraz, bootloader a shodné partition layout
  skutečně instalované v ověřené N16R8. Uchovat soukromou zálohu flash/NVS
  a poslední přijatý USB recovery balíček podle [OTA návodu](firmware-ota.md).
- [x] Na této desce dokončit servisní identitu/backend host, PoP a veřejnou development OTA důvěru.
  Prázdná tovární NVS nemá sdílené výchozí heslo: první bezpečné zprovoznění
  vyžaduje [USB servis](../kufr/firmware/diagnostics/network-service/README.md).
  Po servisním obrazu obnovit aplikaci bez smazání NVS.
- [ ] Nainstalovat přijatou podepsanou iOS aplikaci a na konkrétním iPhonu
  ověřit BLE handshake, hotspot, jeho výpadek a návrat. Webový simulátor
  nenahrazuje CoreBluetooth ani skutečnou instalaci.
- [ ] Soukromě připravit PoP kufru, SSID/heslo hotspotu a případného fallbacku.
  PoP, device credential a OTA podpisová autorita mají různé role.
- [ ] Změřit napájení a dokončit kalibrace B-effects. Neautorizovat fyzicky
  neověřenou kameru/SD servisním flagem. GPIO38–40 nepoužívat současně pro JTAG.
  Podrobnosti jsou v [hardware integraci](hardware-software-integration.md).
- [ ] Poslední aktualizaci provést včas před příchodem návštěvníků; ověřit
  novou verzi po rebootu a stabilní stav. Pro den akce vědomě zvolit politiku
  automatické instalace; ruční kontrola sama instalaci nespouští.
- [ ] Vyzkoušet Stop, fyzické odpojení pohonů zelenou kolébkou a následné
  místní zotavení. Zkušební výběr jedné přihrádky, hlas a volitelné periferie
  přijmout podle skutečného výsledku, nikoli pouze podle stavové zprávy.

## Zapnutí v místě akce

1. Na iPhonu ručně zapnout **Nastavení → Osobní hotspot → Povolit připojení
   ostatním** a **Maximalizovat kompatibilitu** pro 2,4 GHz. Zkontrolovat
   mobilní data, skutečný název hotspotu, heslo a nabití telefonu/powerbanky.
2. Zapnout kufr. Otevřít **Síť kufru**, vyhledat `KUFR_…` a zadat PoP do
   nativního dialogu. BLE okno běží pět minut po bootu.
3. Vybrat hotspot jako preferovaný profil; v případě změny zadat SSID/heslo.
   Volitelně nastavit běžnou Wi-Fi jako fallback. Počkat na konečný výsledek,
   ne pouze na přijetí příkazu. Jeden pokus o připojení čeká až 15 s na DHCP,
   po selhání následuje backoff.
4. Odděleně zkontrolovat **připojení k Wi-Fi** a **backend**. IP adresa
   nepotvrzuje funkční WAN, správný čas, přijatou identitu ani dostupnou AI.
5. Zkontrolovat, že OTA nečeká na boot validaci/instalaci a nejsou aktivní
   údržbové operace. Před pohybem provést místní kontrolu všech šesti
   přihrádek dole a přidržet momentové tlačítko 2 s.
6. Na již přijatém zapojení krátce ověřit hlas, světla a zvolenou přihrádku.
   Telefon může zůstat pozorovatelem; kufr komunikuje s backendem přímo.
   Je-li řízení LAN z telefonu poskytujícího hotspot nedostupné, použít BLE
   diagnostiku během jejího okna; telefon se nepřipojuje jako klient vlastního
   hotspotu. Neobcházet TLS ověřování.

## Zotavení během provozu

| Projev | Postup |
| --- | --- |
| Hotspot není dostupný | Zapnout jej ručně, ověřit SSID/heslo a 2,4 GHz; počkat na timeout/backoff. Nastavený fallback se zkouší po selhání preferovaného profilu. Návrat na hotspot se sondou zkouší nejvýše jednou za minutu a pouze v klidu; úspěšný explicitní configure/test fallbacku i rollback začíná nový 60s interval do další sondy. |
| Obě sítě nefungují | V autentizovaném BLE okně opravit profil nebo použít **Test**. Po minutě offline se jednou za boot otevírá další pětiminutové recovery okno. Pokud vypršelo, provést vědomý power cycle až po bezpečném ukončení aktivity; nesmazat NVS. Offline ruční bezpečnostní řízení nepotřebuje AI/WAN. |
| Nové heslo nefunguje | Zkontrolovat konečný `connection_failed`/`storage_failed`. Chybné heslo zachová předchozí profil. Storage failure zachová starý stav RAM a jednou zkusí obnovit uložený blob i původní spojení; trvalá chyba NVS může zmařit obnovu. Po zotavení ověřit skutečný uložený profil, případně použít kontrolovaný USB servis. Neopakovat naslepo více změn během čekající operace. |
| Wi-Fi je připojená, backend ne | Ověřit internet na telefonu a přečíst stav/technické logy. Prověřit UTC čas, identitu a skutečně nasazenou verzi. Nevypínat TLS; provider klíč nepatří do telefonu ani ESP. |
| Telefon nebo BLE se odpojil | Samostatný kufr pokračuje; pozorovatelské odpojení samo nezastavuje rozhovor. Ztráta ručního lease má vlastní bezpečné ukončení. Nové připojení neobnovuje automaticky starý hlasový tah ani fyzický povel. |
| OTA hlásí selhání | Uchovat verzi/chybový kód, ověřit síť a OTA veřejnou důvěru. Neplatný podpis/hash/target neopravovat vypnutím kontrol. Starý běžící slot zůstává při neúspěšném downloadu použitelný; nová opravená verze je potřeba po odmítnuté boot validaci stejné verze. |
| OTA po rebootu neprojde zdravím | Nový obraz vyžaduje souvislé 5 s pasivního zdraví a sítě do 120 s; během validace nepovoluje interakce. Ověřit návrat staré verze a credentialů. Bez platného fallback slotu použít přijatý USB recovery postup se zálohou. Skutečný rollback musí být před akcí fyzicky otestovaný. |
| Stop, chyba pohybu nebo nejistá poloha | Stop ukončí hlas i sekvenci; podle situace odpojit pohony zelenou kolébkou. Provést místní kontrolu a předepsané zotavení. Časovaná poloha serva není údaj z fyzického snímače; nevyvolávat automatický návrat při chybě. |
| Kamera nebo SD nejsou dostupné | Použít základní hlas/řízení bez volitelné periferie. Nezapínat neověřený servisní flag. SD slouží omezeným lokálním assetům a fotografie se na ni neukládají; aplikace kartu neformátuje. |

Serverové chyby dohledávat pomocí UUID z **Debug → Log serveru** podle
[Codex diagnostiky](../kufr/backend/codex/README.md). Zapisovat čas UTC,
verzi firmware/aplikace/backendu, profil, chybový kód a provedený krok.
Do sdíleného záznamu nevkládat hesla, PoP, tokeny ani obsah paměti návštěvníků.

## Matice všech 47 požadovaných scénářů (historické výsledky D158/D159)

**S = dílčí softwarová simulace/test prošel. K = statická/source kontrola;
neprokazuje runtime. H = kontrola skutečné desky přes USB/UART a korelované
serverové logy. M = výsledek
na telefonu potvrzený majitelem. NE = neprovedeno v uvedeném prostředí.**
D159 doplňuje pouze výslovně uvedené historické výsledky skutečné desky a telefonu;
nelze je přenést na D163 bez nové zkoušky. Aktuální publikace a hlášené LAN odpojování
jsou uvedené nahoře. Host adaptér OTA
simuluje HTTP, flash a kryptografické odpovědi; skutečný podpis balíčku ověřují
samostatné release/backend testy. BLE host stub potvrzuje zapojení Security1,
nikoli skutečný X25519 handshake přes rádio. Úspěšný web build není Swift build.

Lokálně prošel backendový .NET testovací runner, cílené firmware host testy,
release/service testy a 32 mobilních testů s webovým buildem. Finální firmware
build prošel lokálně; vzdálené kroky mají vlastní výsledky. Přesné příkazy
a aktuální CI/publikační výsledky patří do stavu implementace.

| # | Požadovaný scénář | Aktuálně doložená softwarová část | Zbývající ESP/iPhone/native nebo provozní kontrola |
| --- | --- | --- | --- |
| 1 | Tovární zařízení může zahájit provisioning | S: bez PoP zůstává BLE zavřené; servisní generátor validuje počáteční konfiguraci. | H: první USB servis, `saved`, NVS readback a obnova CI aplikace na N16R8 bez původní identity/PoP; M: následné BLE spojení. Bez PoP není bezpečný tovární handshake dostupný. |
| 2 | iOS objeví zařízení | K: native CoreBluetooth/ESPProvision discovery, UUID a UI; skutečný Xcode archive prošel. | M: majitel našel fyzický kufr a připojil se v Síť kufru. |
| 3 | iOS naváže autorizované BLE spojení | S/K: host konfigurace povinného PoP/Security1, nativní SDK integrace. | M: majitel potvrdil spojení s privátním device-specific PoP. |
| 4 | Chybná autorizace je odmítnuta | S: chybějící PoP zakazuje službu; K: management je za Security1. | NE: špatný PoP proti skutečnému šifrovanému handshake. |
| 5 | Hotspot credentialy se přenesou bezpečně | S: bounded codec/endpoint a whitelist diagnostiky; K: native secure dialog a Security1. | M: PoP handshake a nastavení domácí Wi-Fi doložené. NE: hotspot přenos a ověření nepřítomnosti citlivých údajů v nativních logách. |
| 6 | ESP připojí hotspot | S: station worker/fake DHCP, preferovaný profil. | NE: skutečný iPhone hotspot, 2,4 GHz, operátor/iOS. |
| 7 | Firmware hlásí stav připojení | S: station status a BLE JSON/codec. | M: po OTA0.1.1 a dalším resetu telefon zobrazil domácí Wi-Fi i backend připojený. Další rádio přechody neověřené. |
| 8 | ESP připojí backend | S: síť/token/time a backend testy; nová regrese token adaptéru opravuje bodyless POST na SDK6.1. | H: skutečný ESP POST200 v09:45:03Z po OTA0.1.1 a09:47:43Z po dalším resetu; M: Backend připojený. Cesta přes iPhone hotspot zůstává NE. |
| 9 | Credentialy přežijí reboot | S: profiles record save/load/reboot simulace. | H: devět identity/PoP/network/OTA klíčů z NVS beze změny po skutečné OTA; nový slot VALID a další reboot0.1.1. M: Wi-Fi a backend znovu připojené bez zadání hesla, stejný PoP. |
| 10 | Chybné heslo dává srozumitelnou chybu | S: `connection_failed`, UI/status projekce. | NE: skutečné odmítnutí Wi-Fi hesla a zobrazení na iPhonu. |
| 11 | Credentialy lze bezpečně nahradit | S: idle gate, test kandidáta a atomický record commit. | NE: náhrada přes BLE, skutečný DHCP a NVS. |
| 12 | Neúspěšná náhrada nezničí funkční konfiguraci | S: špatné heslo zachová profil; storage failure drží starou RAM a jednou zkusí persistentní rollback/obnovu spojení. | NE: rádio/NVS failure a návrat skutečného spojení. Trvalá chyba NVS může zmařit i rollback; nejde o záruku při power-loss. |
| 13 | Provisioning lze obnovit bez Wi-Fi | S: oddělený BLE lifecycle a bounded recovery okno. | NE: offline iPhone spojení, vypršení okna a bezpečný power cycle. |
| 14 | Cizí BLE klient nezmění konfiguraci | S/K: služba vyžaduje PoP/Security1; bounded management bez veřejného konfiguračního endpointu. | NE: negativní test skutečného klienta bez správného PoP. |
| 15 | Preferovaný hotspot je nedostupný | S: timeout/backoff/volba dalšího profilu. | NE: vypnutý hotspot na telefonu. |
| 16 | Fallback Wi-Fi je dostupná | S: uložení/volba fallbacku a fake připojení. | NE: skutečný access point, DHCP a backend. |
| 17 | Obě sítě jsou nedostupné | S: bounded reconnect/offline stav a BLE lifecycle. | NE: skutečný offline běh, recovery okno a ruční řízení. |
| 18 | Hotspot se vrátí | S: omezená preferred probe a idle gate. | NE: návrat telefonu/hotspotu a přechod z funkčního fallbacku. |
| 19 | Hotspot vypadne během sezení | S: ztráta IP/cancel a maintenance admission pravidla. | NE: přerušení živého audio/sezení při fyzickém výpadku. |
| 20 | Backend je dočasně nedostupný | S: transport/retry/failure testy; Wi-Fi a backend stav jsou oddělené. | NE: cílený výpadek nasazeného backendu v celé sestavě. |
| 21 | Zařízení se samo znovu připojí | S: worker reconnect/backoff bez manuálního restartu. | NE: více skutečných výpadků a návratů sítí/WAN. |
| 22 | Síťové přechody nespustí nebezpečné pohony | S: společná maintenance brána blokuje nové lease/voice/text/prezentace; Stop zůstává dostupný. | NE: měřený souběh rádia, serv, vibrací a bezpečnostní smyčky. |
| 23 | Není nová OTA verze | S: porovnání verzí/same-build/anti-downgrade. | NE: fyzická kontrola aktuálního release bez instalace. |
| 24 | Je platná OTA aktualizace | S: manifest parser/compatibility a adapter nabídka; release/backend testy ověřují skutečný podpis. | H: konkrétní ESP přijal podepsaný development manifest a automaticky nainstaloval0.1.1 se svým provisionovaným trust. |
| 25 | Firmware se úspěšně stáhne | S: fake HTTP → inactive flash → promotion, bounded size. | H: ESP HTTPS download200 a skutečný boot0.1.1 v ota_1; public/CI image hash a podpis ověřené nezávisle. |
| 26 | Poškozený obraz je odmítnut | S: adapter mismatch digest zakazuje boot promotion; release testy ověřují skutečné bajty/hash. | NE: poškozený obraz odmítnutý skutečným ESP. |
| 27 | Neplatný podpis/autenticita je odmítnuta | S: adapter verifier failure a release/backend skutečná signature verification. | NE: neplatný podpis ověřený mbedTLS na desce. |
| 28 | Obraz pro jiný hardware je odmítnut | S: hardware/profile/channel/min-updater/slot-size policy a image metadata kontroly. | NE: skutečný nekompatibilní ESP image bez změny boot slotu. |
| 29 | Download je přerušen | S: short/interrupted HTTP vede k OTA abort, bez promotion. | NE: výpadek hotspotu/WAN při zápisu flash. |
| 30 | Během aktualizace vypadne napájení | K: inactive slot a OTA metadata používají oficiální IDF API. | NE: řízené fyzické přerušení v různých bodech flash/boot metadata. Není doložené simulací napájení. |
| 31 | Po instalaci firmware rebootuje | S: reboot callback až při safe idle, držená rezervace. | H: automatický reboot na ota_1/0x650000 a UART verze0.1.1; read-only otadata seq2/VALID/CRC. Stejný slot/verze přežil další reset. |
| 32 | Selže validace prvního bootu | S: reset stability/deadline, unhealthy stav žádá rollback. | NE: skutečný vadný první boot a údržbová izolace. |
| 33 | Proběhne automatický rollback | S: policy a IDF rollback callback; K: rollback konfigurace. | NE: bootloader vrátí skutečný platný slot; potvrdit také případ bez fallbacku. |
| 34 | Trvalé credentialy zůstanou zachované | S: adapter nemění uložená trust/settings; K: NVS je mimo app sloty. | H: přesný logický readback devíti identity/PoP/network/OTA klíčů před/po skutečné OTA je shodný, bez zveřejnění tajemství. NE: retention po skutečném rollbacku. |
| 35 | Selhaná OTA neznefunkční zařízení | S: download/hash/descriptor failure nepřepne boot a uvolní reservation. | NE: flash/power-loss/metadata a USB recovery na skutečné desce; absolutní odolnost není prokázaná. |
| 36 | Instalace čeká na bezpečný klid | S: deferred install/reboot a společné maintenance regression testy. | NE: požadavek OTA při skutečném hlasu, lease, návrhu a pohybu. |
| 37 | Funguje ruční OTA kontrola | S: RequestCheck/worker a BLE management codec. | NE: skutečný iPhone příkaz → HTTPS manifest → zobrazený výsledek. |
| 38 | Fungují periodické kontroly | S: initial delay, interval 60–86400 s, offline/unsafe defer a capped backoff. | H: automatická počáteční kontrola manifestu i po dalších bootech. NE: dlouhý fyzický běh se změnou dostupnosti serveru a celý hodinový interval. |
| 39 | GPT-Live dál funguje | S: stávající backend actor/flow regression testy v offline/fake prostředí. | NE: nový placený provider test, skutečný rozhovor/audio a lidská akceptace. |
| 40 | Výběr osobnosti je zachován | S: stávající katalog/personality testy v backend runneru; katalog nebyl nahrazen. | NE: lidská akceptace skutečného provider rozhovoru s katalogem. |
| 41 | Výběr hůlky funguje | S: selection sequence/proposal/journal a lokální brány. | NE: kalibrovaná fyzická přihrádka 1–6, prezentace a návrat. |
| 42 | Firmware/backend protokol je kompatibilní | S: stávající codecs/backend kontrakty a bounded nové management/release endpointy. | H/M: skutečný ESP0.1.1 token/backend a iOS1000.0.19 BLE network status prošly. NE: celý rozhovor/hlas/pohyb nad nasazenými verzemi. |
| 43 | Audio funguje na zvoleném hardwaru | S: I2S/PCM transport/drain a half-duplex testy; K: zachované B-effects piny. | NE: oba DFR0954/reproduktory, mikrofon, clipping, echo, latence a resetové ticho. |
| 44 | Kamera a SD jsou ověřené v dostupném rozsahu | S: costume photo/camera fake adapter, SD flat-path/bounds/read-only/cleanup. | NE: OV5640/FPC, fotografie, SD karta/pullupy a souběh s audiem. |
| 45 | Servo/LED/ostatní efekty dál fungují | S: existing physical/sequence safety testy, pin kontrakt zachován. | NE: pulzy, dorazy, PCA/MCP/U059/WS2812B, proud a skutečný mechanický návrat. |
| 46 | Existující deployment workflow zůstává kompatibilní | K/S: plné CI `37895236311`, backend deploy `37896345546` attempt 2, firmware CI `37912208656`/development publish `37912902381`, macOS native testy/archive/export `37899385679` a iOS publish `37899776001` prošly; veřejné manifesty, artefakty a checksumy byly načteny a ověřeny. | H: development trust, USB provisioning a skutečná OTA0.1.1/VALID boot/reconnect prošly. NE: úplná fyzická akceptace a další backend deploy po přidání identity. Stable autorita/trust mají samostatný provisioning. |
| 47 | Celý systém se zotaví z dočasných chyb | S: dílčí síť/OTA/session/journal/fault recovery testy. | NE: souvislá zkouška celé sestavy v místě akce, výpadky a návraty bez nebezpečného pohybu. |

## Připravenost subsystémů a vnější podmínky

Řádky s D158/D159 zachovávají historickou akceptaci příslušných verzí;
aktuální předání je D163 podle odkazu nahoře.

| Subsystém | Stav při tomto zápisu | Co ještě brání úplné akceptaci |
| --- | --- | --- |
| BLE provisioning a hotspot/fallback firmware | IMPLEMENTED — PARTIALLY HARDWARE VERIFIED | D159 USB servis/NVS a majitelem potvrzený PoP handshake/domácí Wi-Fi prošly. Zbývá wrong-PoP, hotspot/failover, radio coexistence a úplné reconnect zkoušky. |
| iOS Síť kufru | IMPLEMENTED — PARTIALLY HARDWARE VERIFIED | Web 32 testů/build, skutečné native testy/Xcode archive/Ad Hoc export i public IPA prošly; majitel použil aplikaci pro BLE/domácí Wi-Fi. Hotspot/error/recovery scénáře zbývají. |
| Lokální LAN ovládání | NOT ACCEPTED | Majitel potvrdil vyhledání a párování, ale LAN spojení se opakovaně odpojovalo. D163 přidává opravu WS extension offers a omezení reconnect; fyzický stabilní provoz nové verze zbývá. |
| OTA firmware | IMPLEMENTED — PARTIALLY HARDWARE VERIFIED | D159 prošlo skutečnou podepsanou OTA0.1.0→0.1.1, VALID boot, credential retention a další reset/reconnect. Zbývají negativní image/signature, přerušení/power-loss a rollback. |
| Release distribuce a VPS | READY v ověřeném development/server rozsahu | Root GitHub verifier, signed OTA/backend/iOS pipelines a public HTTPS bajty/hash/signature prošly; D159 také skutečně přijalo development OTA na ESP. Stable autorita má samostatný provisioning. |
| Produkční stable autorita | BLOCKED pro nasazení bez správné důvěry | Provisionovat příslušný produkční podpisový klíč mimo repo a shodný veřejný trust na zařízení; development klíč není automaticky produkční oprávnění. |
| Backend/GPT-Live/katalog | PARTIALLY READY | Plný runner i čerstvé nasazení/health/modes/store metadata kontroly prošly; nebyla volána nová placená inference ani provedena lidská konverzační akceptace celé sestavy. |
| Fyzické efekty a audio | IMPLEMENTED — NOT HARDWARE VERIFIED | Měřené zapojení/napájení/kalibrace a souběžný provoz na celé sestavě. |
| Volitelná kamera a SD | IMPLEMENTED — NOT HARDWARE VERIFIED | Skutečná periferie, měření, souběh a teprve poté servisní flags. |
| Telemetrie PB720 | NOT IMPLEMENTED | Není ověřený hardware/API kontrakt; dobu provozu potvrdit měřením. |
| Celá sestava pro návštěvníky | PARTIALLY READY | Dokončit fyzické kritické scénáře výše a společnou zkoušku před akcí. |

Za **READY** lze označit konkrétní přijatý rozsah až s doloženou verzí,
prostředím a výsledkem. Historický boot desky, zelené unit testy, zveřejněný
soubor nebo podepsaná IPA samostatně nepotvrzují provoz celé sestavy.
