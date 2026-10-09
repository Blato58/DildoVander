# Připravenost na akci a provozní checklist

Stav k 9. 10. 2026: software obsahuje BLE konfiguraci, hotspot/fallback, OTA
a volitelné SD assety. Lokální testy potvrzují dílčí softwarové chování;
**připravenost celé fyzické sestavy na akci ještě není doložená**.
Nová fyzická ESP/iPhone akceptace ani macOS/Xcode ověření v této úloze
neproběhly. Finální společný `pio run` prošel, binary má 1732080 B
z 6553600 B slotu a skutečný OpenSSL podpis/verify prošel. Vzdálené CI,
VPS publikace a podepsaný iOS build čekají Git předání.
Jejich skutečné výsledky, verze a běhy zapisuje
[stav implementace](../kufr/docs/stav-implementace.md); plánovaný běh není úspěch.

## Příprava před akcí

- [ ] Mít první OTA-capable obraz, bootloader a shodné partition layout
  skutečně instalované v ověřené N16R8. Uchovat soukromou zálohu flash/NVS
  a poslední přijatý USB recovery balíček podle [OTA návodu](firmware-ota.md).
- [ ] Dokončit servisní identitu/backend host, PoP a veřejnou OTA důvěru.
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
| Hotspot není dostupný | Zapnout jej ručně, ověřit SSID/heslo a 2,4 GHz; počkat na timeout/backoff. Nastavený fallback se zkouší po selhání preferovaného profilu. Návrat na hotspot se sondou zkouší nejvýše jednou za minutu a pouze v klidu. |
| Obě sítě nefungují | V autentizovaném BLE okně opravit profil nebo použít **Test**. Po minutě offline se jednou za boot otevírá další pětiminutové recovery okno. Pokud vypršelo, provést vědomý power cycle až po bezpečném ukončení aktivity; nesmazat NVS. Offline ruční bezpečnostní řízení nepotřebuje AI/WAN. |
| Nové heslo nefunguje | Zkontrolovat konečný `connection_failed`/`storage_failed`. Neúspěšná náhrada zachovává předchozí uložený profil a pokouší se vrátit původní spojení. Neopakovat naslepo více změn během čekající operace. |
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

## Matice všech 47 požadovaných scénářů

**S = dílčí softwarová simulace/test prošel. K = statická/source kontrola;
neprokazuje runtime. NE = neprovedeno v uvedeném prostředí.**
Žádný řádek zde nemá novou fyzickou ESP/iPhone akceptaci. Host adaptér OTA
simuluje HTTP, flash a kryptografické odpovědi; skutečný podpis balíčku ověřují
samostatné release/backend testy. BLE host stub potvrzuje zapojení Security1,
nikoli skutečný X25519 handshake přes rádio. Úspěšný web build není Swift build.

Lokálně prošel backendový .NET testovací runner, cílené firmware host testy,
release/service testy a 32 mobilních testů s webovým buildem. Finální firmware
build a vzdálené kroky výše zůstávají v tomto přehledu čekající; přesné příkazy
a finální výsledky patří do stavu implementace.

| # | Požadovaný scénář | Aktuálně doložená softwarová část | Zbývající ESP/iPhone/native nebo provozní kontrola |
| --- | --- | --- | --- |
| 1 | Tovární zařízení může zahájit provisioning | S: bez PoP zůstává BLE zavřené; servisní generátor validuje počáteční konfiguraci. | NE: první instalace/USB servis a následné BLE okno na nové desce. Bez PoP není bezpečný tovární handshake dostupný. |
| 2 | iOS objeví zařízení | K: native CoreBluetooth/ESPProvision discovery, UUID a UI; web simulace není rádio. | NE: Xcode sestavení a fyzický scan iPhonu. |
| 3 | iOS naváže autorizované BLE spojení | S/K: host konfigurace povinného PoP/Security1, nativní SDK integrace. | NE: skutečný handshake mezi iPhonem a ESP. |
| 4 | Chybná autorizace je odmítnuta | S: chybějící PoP zakazuje službu; K: management je za Security1. | NE: špatný PoP proti skutečnému šifrovanému handshake. |
| 5 | Hotspot credentialy se přenesou bezpečně | S: bounded codec/endpoint a whitelist diagnostiky; K: native secure dialog a Security1. | NE: radio handshake, přenos a ověření nepřítomnosti citlivých údajů v nativních logách. |
| 6 | ESP připojí hotspot | S: station worker/fake DHCP, preferovaný profil. | NE: skutečný iPhone hotspot, 2,4 GHz, operátor/iOS. |
| 7 | Firmware hlásí stav připojení | S: station status a BLE JSON/codec. | NE: stav přijatý skutečným telefonem během rádio přechodů. |
| 8 | ESP připojí backend | S: síť/token/time a backend testy; K: zachovaný přímý HTTPS klient. | NE: skutečné ESP → hotspot → WAN → nasazený backend s provisionovanou identitou. |
| 9 | Credentialy přežijí reboot | S: profiles record save/load/reboot simulace. | NE: fyzický restart a opětovné připojení z NVS. |
| 10 | Chybné heslo dává srozumitelnou chybu | S: `connection_failed`, UI/status projekce. | NE: skutečné odmítnutí Wi-Fi hesla a zobrazení na iPhonu. |
| 11 | Credentialy lze bezpečně nahradit | S: idle gate, test kandidáta a atomický record commit. | NE: náhrada přes BLE, skutečný DHCP a NVS. |
| 12 | Neúspěšná náhrada nezničí funkční konfiguraci | S: špatné heslo i storage failure zachovají předchozí profil a obnoví starý pokus. | NE: rádio/NVS failure a návrat skutečného spojení. |
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
| 24 | Je platná OTA aktualizace | S: manifest parser/compatibility a adapter nabídka; release/backend testy ověřují skutečný podpis. | NE: zveřejněný manifest přijatý konkrétním ESP s jeho trust. |
| 25 | Firmware se úspěšně stáhne | S: fake HTTP → inactive flash → promotion, bounded size. | NE: skutečný HTTPS download a zápis do flash. |
| 26 | Poškozený obraz je odmítnut | S: adapter mismatch digest zakazuje boot promotion; release testy ověřují skutečné bajty/hash. | NE: poškozený obraz odmítnutý skutečným ESP. |
| 27 | Neplatný podpis/autenticita je odmítnuta | S: adapter verifier failure a release/backend skutečná signature verification. | NE: neplatný podpis ověřený mbedTLS na desce. |
| 28 | Obraz pro jiný hardware je odmítnut | S: hardware/profile/channel/min-updater/slot-size policy a image metadata kontroly. | NE: skutečný nekompatibilní ESP image bez změny boot slotu. |
| 29 | Download je přerušen | S: short/interrupted HTTP vede k OTA abort, bez promotion. | NE: výpadek hotspotu/WAN při zápisu flash. |
| 30 | Během aktualizace vypadne napájení | K: inactive slot a OTA metadata používají oficiální IDF API. | NE: řízené fyzické přerušení v různých bodech flash/boot metadata. Není doložené simulací napájení. |
| 31 | Po instalaci firmware rebootuje | S: reboot callback až při safe idle, držená rezervace. | NE: skutečný reboot, zvolený slot a oznámení nové verze. |
| 32 | Selže validace prvního bootu | S: reset stability/deadline, unhealthy stav žádá rollback. | NE: skutečný vadný první boot a údržbová izolace. |
| 33 | Proběhne automatický rollback | S: policy a IDF rollback callback; K: rollback konfigurace. | NE: bootloader vrátí skutečný platný slot; potvrdit také případ bez fallbacku. |
| 34 | Trvalé credentialy zůstanou zachované | S: adapter nemění uložená trust/settings; K: NVS je mimo app sloty. | NE: porovnání soukromé NVS konfigurace před/po skutečné OTA a rollbacku bez zveřejnění tajemství. |
| 35 | Selhaná OTA neznefunkční zařízení | S: download/hash/descriptor failure nepřepne boot a uvolní reservation. | NE: flash/power-loss/metadata a USB recovery na skutečné desce; absolutní odolnost není prokázaná. |
| 36 | Instalace čeká na bezpečný klid | S: deferred install/reboot a společné maintenance regression testy. | NE: požadavek OTA při skutečném hlasu, lease, návrhu a pohybu. |
| 37 | Funguje ruční OTA kontrola | S: RequestCheck/worker a BLE management codec. | NE: skutečný iPhone příkaz → HTTPS manifest → zobrazený výsledek. |
| 38 | Fungují periodické kontroly | S: initial delay, interval 60–86400 s, offline/unsafe defer a capped backoff. | NE: dlouhý fyzický běh se změnou dostupnosti serveru. |
| 39 | GPT-Live dál funguje | S: stávající backend actor/flow regression testy v offline/fake prostředí. | NE: nový placený provider test, skutečný rozhovor/audio a lidská akceptace. |
| 40 | Výběr osobnosti je zachován | S: stávající katalog/personality testy v backend runneru; katalog nebyl nahrazen. | NE: lidská akceptace skutečného provider rozhovoru s katalogem. |
| 41 | Výběr hůlky funguje | S: selection sequence/proposal/journal a lokální brány. | NE: kalibrovaná fyzická přihrádka 1–6, prezentace a návrat. |
| 42 | Firmware/backend protokol je kompatibilní | S: stávající codecs/backend kontrakty a bounded nové management/release endpointy. | NE: celý skutečný ESP/backend/iPhone průchod nad shodně nasazenými verzemi. |
| 43 | Audio funguje na zvoleném hardwaru | S: I2S/PCM transport/drain a half-duplex testy; K: zachované B-effects piny. | NE: oba DFR0954/reproduktory, mikrofon, clipping, echo, latence a resetové ticho. |
| 44 | Kamera a SD jsou ověřené v dostupném rozsahu | S: costume photo/camera fake adapter, SD flat-path/bounds/read-only/cleanup. | NE: OV5640/FPC, fotografie, SD karta/pullupy a souběh s audiem. |
| 45 | Servo/LED/ostatní efekty dál fungují | S: existing physical/sequence safety testy, pin kontrakt zachován. | NE: pulzy, dorazy, PCA/MCP/U059/WS2812B, proud a skutečný mechanický návrat. |
| 46 | Existující deployment workflow zůstává kompatibilní | S/K: lokální backend/web/release helper/provenance kontroly; zachované CI/signing cesty. | NE při tomto zápisu: finální firmware CI, VPS publish/readback a macOS signing workflow. Výsledky doplnit ze skutečných běhů. |
| 47 | Celý systém se zotaví z dočasných chyb | S: dílčí síť/OTA/session/journal/fault recovery testy. | NE: souvislá zkouška celé sestavy v místě akce, výpadky a návraty bez nebezpečného pohybu. |

## Připravenost subsystémů a vnější podmínky

| Subsystém | Stav při tomto zápisu | Co ještě brání úplné akceptaci |
| --- | --- | --- |
| BLE provisioning a hotspot/fallback firmware | IMPLEMENTED — NOT HARDWARE VERIFIED | ESP/iPhone handshake, radio coexistence, skutečné NVS a reconnect zkoušky. |
| iOS Síť kufru | PARTIALLY READY | Web 32 testů/build prošel; dokončit macOS/Xcode testy, podpis, instalaci a fyzické BLE/hotspot scénáře. |
| OTA firmware | IMPLEMENTED — NOT HARDWARE VERIFIED | Finální build, první USB instalace rollback-capable bootloaderu, skutečný digest/signature/power-loss/rollback. |
| Release distribuce a VPS | PARTIALLY READY | Lokální testy/package verifikace; vzdálený publisher, CI provenance, živé HTTPS readback a konkrétní trust na ESP ještě musí mít vlastní důkaz. |
| Produkční stable autorita | BLOCKED pro nasazení bez správné důvěry | Provisionovat příslušný produkční podpisový klíč mimo repo a shodný veřejný trust na zařízení; development klíč není automaticky produkční oprávnění. |
| Backend/GPT-Live/katalog | PARTIALLY READY | Lokální runner prošel; čerstvá nasazená verze, případný placený test a lidská konverzační akceptace jsou oddělené kontroly. |
| Fyzické efekty a audio | IMPLEMENTED — NOT HARDWARE VERIFIED | Měřené zapojení/napájení/kalibrace a souběžný provoz na celé sestavě. |
| Volitelná kamera a SD | IMPLEMENTED — NOT HARDWARE VERIFIED | Skutečná periferie, měření, souběh a teprve poté servisní flags. |
| Telemetrie PB720 | NOT IMPLEMENTED | Není ověřený hardware/API kontrakt; dobu provozu potvrdit měřením. |
| Celá sestava pro návštěvníky | PARTIALLY READY | Dokončit fyzické kritické scénáře výše a společnou zkoušku před akcí. |

Za **READY** lze označit konkrétní přijatý rozsah až s doloženou verzí,
prostředím a výsledkem. Historický boot desky, zelené unit testy, zveřejněný
soubor nebo podepsaná IPA samostatně nepotvrzují provoz celé sestavy.
