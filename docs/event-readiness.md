# Připravenost na akci a provozní checklist

Aktuální gates a blokace: [STATUS](project/STATUS.md) a
[work items](project/work-items). Tento dokument zachovává provozní postup;
aktuální výsledky se zapisují do JSON a receipts, nikoli do druhé matice.
[Archiv D158–D166](project/archive/dv08/docs/event-readiness.md) uchovává
všech 47 scénářů a historicky přijaté servisní výsledky.

## Příprava před akcí

Následující body jsou provozní postup, nikoli potvrzení splnění.
Datovaná servisní přijetí D159 a aktuální neověřené části jsou v JSON a archivu.

- Na desce `kufr-68ee8f4b6048` mít první OTA-capable obraz, bootloader a shodné partition layout
  skutečně instalované v ověřené N16R8. Uchovat soukromou zálohu flash/NVS
  a poslední přijatý USB recovery balíček podle [OTA návodu](firmware-ota.md).
- Na této desce dokončit servisní identitu/backend host, PoP a veřejnou development OTA důvěru.
  Prázdná tovární NVS nemá sdílené výchozí heslo: první bezpečné zprovoznění
  vyžaduje [USB servis](../firmware/diagnostics/network-service/README.md).
  Po servisním obrazu obnovit aplikaci bez smazání NVS.
- Nainstalovat přijatou podepsanou iOS aplikaci a na konkrétním iPhonu
  ověřit BLE handshake, hotspot, jeho výpadek a návrat. Webový simulátor
  nenahrazuje CoreBluetooth ani skutečnou instalaci.
- Soukromě připravit PoP kufru, SSID/heslo hotspotu a případného fallbacku.
  PoP, device credential a OTA podpisová autorita mají různé role.
- Změřit napájení a dokončit kalibrace B-effects. Neautorizovat fyzicky
  neověřenou kameru/SD servisním flagem. GPIO38–40 nepoužívat současně pro JTAG.
  Podrobnosti jsou v [hardware integraci](hardware-software-integration.md).
- Poslední aktualizaci provést včas před příchodem návštěvníků; ověřit
  novou verzi po rebootu a stabilní stav. Pro den akce vědomě zvolit politiku
  automatické instalace; ruční kontrola sama instalaci nespouští.
- Vyzkoušet Stop, fyzické odpojení pohonů zelenou kolébkou a následné
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
| Zapomenuté místní párování / `already_paired` | S firmware/aplikací D165 se připojit přes ověřené PoP BLE, použít **Spárovat ovládání přes Wi-Fi** a potvrdit **Obnovit párování kufru?**. Kufr musí být nečinný a ostatní ovladače odpojené. Zrušení nic nemění; obnova zneplatní starý token a nemění TLS/Wi-Fi. Chybu `busy` řešit ukončením vlastních spojení/aktivity, nikoli smazáním NVS nebo automatickým opakováním. Firmware **0.1.162** je publikovaný a jeho boot ověřený; iOS **1000.0.22** je publikované. Majitel následně potvrdil opakované párování; automatické LAN připojení a jeho úplnou fyzickou akceptaci řeší D166. |
| Telefon nebo BLE se odpojil | Samostatný kufr pokračuje; pozorovatelské odpojení samo nezastavuje rozhovor. Ztráta ručního lease má vlastní bezpečné ukončení. Nové připojení neobnovuje automaticky starý hlasový tah ani fyzický povel. |
| OTA hlásí selhání | Uchovat verzi/chybový kód, ověřit síť a OTA veřejnou důvěru. Neplatný podpis/hash/target neopravovat vypnutím kontrol. Starý běžící slot zůstává při neúspěšném downloadu použitelný; nová opravená verze je potřeba po odmítnuté boot validaci stejné verze. |
| OTA po rebootu neprojde zdravím | Nový obraz vyžaduje souvislé 5 s pasivního zdraví a sítě do 120 s; během validace nepovoluje interakce. Ověřit návrat staré verze a credentialů. Bez platného fallback slotu použít přijatý USB recovery postup se zálohou. Skutečný rollback musí být před akcí fyzicky otestovaný. |
| Stop, chyba pohybu nebo nejistá poloha | Stop ukončí hlas i sekvenci; podle situace odpojit pohony zelenou kolébkou. Provést místní kontrolu a předepsané zotavení. Časovaná poloha serva není údaj z fyzického snímače; nevyvolávat automatický návrat při chybě. |
| Kamera nebo SD nejsou dostupné | Použít základní hlas/řízení bez volitelné periferie. Nezapínat neověřený servisní flag. SD slouží omezeným lokálním assetům a fotografie se na ni neukládají; aplikace kartu neformátuje. |

Serverové chyby dohledávat pomocí UUID z **Debug → Log serveru** podle
[Codex diagnostiky](../backend/codex/README.md). Zapisovat čas UTC,
verzi firmware/aplikace/backendu, profil, chybový kód a provedený krok.
Do sdíleného záznamu nevkládat hesla, PoP, tokeny ani obsah paměti návštěvníků.


## Matice všech 47 požadovaných scénářů (historické výsledky D158/D159)

Každý scénář S01–S47 má revizované kritérium a mapování v
[migration.json](project/migration.json). Datovaná tvrzení zůstávají v archivu;
software test není nový fyzický důkaz.

## Připravenost subsystémů a vnější podmínky

[STATUS](project/STATUS.md) počítá všechny povinné gates, včetně skrytých
blockerů. Stable podpis, volitelný obraz/SD/Dance a PB720 mají oddělený rozsah.
Publikace, instalace a fyzická přejímka jsou různé události.
