# DildoVander: společná webová a iOS aplikace

Vue 3 SFC s Composition API, TypeScriptem a Vue Routerem sdílejí stejné
obrazovky na webu a v Capacitor WKWebView. Vapour Chrome dodává autoritativní
tokeny a styl; `src/components/` obsahuje typované Vue komponenty.
`src/application/App.vue` drží navigaci **Domov, Kufr, Dílna, Archiv, Nastavení**
a dostupný Stop. Feature služby vlastní zařízení, účty, archiv a viewer;
Swift adaptéry dál vlastní Bluetooth, Keychain, audio a nativní transport.

Režie Show Pack je skutečná Vue obrazovka na `/suitcase/director`, dostupná z Provozu.
Starý vstup `/#director` přesměruje na tuto cestu. Používá přihlášený účet,
konkrétní nakonfigurovaný kufr a stejný omezený transport na webu i iOS;
obrazovka nevyžaduje adresu serveru ani raw bearer.
Backend kontroluje read/operate/manage grant. Citlivé odhalení, herní grant,
import offline výsledků a konec večera vyžadují potvrzení a čerstvý passkey.
Běžné zásahy a Stop nečekají na nové přihlášení. Stop má přednostní cestu i při
čekajícím běžném požadavku a zůstává v globálním Stop po odchodu z režie.
Přepnutí účtu nebo vybraného zařízení ruší starý kontext synchronně.

Režie zachovává revize, generaci návštěvy a TTL zásahů 30 s; nejistý výsledek
se nikdy automaticky neopakuje. Backendové přijetí není potvrzení zvuku nebo
fyzického účinku. Stop porovnává čerstvý report stejné deviceStopRevision.
Místní offline preflight/Arm/start/ticho/ambient mají vlastní autentizované WSS;
Arm vyžaduje výslovně získaný ruční lease v Provozu. Import označuje výsledky
operator_relay a potvrzuje outbox až po přijetí shodného operationId serverem.

Souboj vyžaduje dvě dokončené identity a souhlas obou hostů. Herní odkazy mají
jednorázově zachycený fragment a žijí pouze v paměti; opuštění pohledu, změna
účtu/zařízení/večera nebo ukončení souboje odstraní operátorovy kopie.
Veřejný přehled grant neobsahuje a jeho origin dodává backend.
`/archive/duel` a `/archive/chronicle` jsou Vue cesty; staré
`/keepsake/duel.html` a `/keepsake/chronicle.html` zůstávají přesné aliasy.
Soukromé znění proroctví zůstává návštěvníkovi. Ukončení večera nemaže archiv.

Opakovatelné syntetické browser ověření běží proti sestavenému Vue dist pomocí
`node scripts/check-director-browser.mjs`; nemění backend, nevolá provider ani
hardware. `check-director-flows.mjs` a `check-director-remote-stop.mjs` jsou
kompatibilní vstupy stejné udržované sady. Skutečné account/backend testy vlastní
izolovaný HTTPS fixture. Žádný browser výsledek neprokazuje přijetí na iPhonu.


`/connections` soustředí uložené zařízení a BLE párování; `/suitcase`
hlas, tanec, text a ruční řízení. `/suitcase/diagnostics` obsahuje nativní
testy a místní USB servis. `/workshop` integruje model, montáž, simulaci,
pozorování a replay. `/archive` a `/keepsake/` zachovávají veřejný a soukromý
přístup hostů. `/settings` a `/settings/admin` vlastní účty a správu.

Přepnutí pohledu zachovává aktivní operace; revize a změna cíle ruší čekající
potvrzení fyzického povelu. Telemetrie zachovává stabilní vstupy a focus.
Prohlížeč začíná bez připojení: ukázku nebo simulátor volíte výslovně.
Preference tématu migruje `aw-theme` do `dildovander.theme`; účet ani přístupové
tokeny se do localStorage neukládají. Oprávnění účtu, lokální párování,
spojení a ruční lease jsou samostatné hranice. Webové testy neprokazují iPhone
ani fyzický kufr. [Architektura a migrační mapa](../docs/application-architecture.md).

Následující datované záznamy popisují starší předání hardware a zachované
nativní schopnosti; nedokládají přejímku nové Vue aplikace.
> **D167 – Dance for me:** panel v Ovládání spouští náhodnou vlastní
> skladbu ze SD kufru a ukazuje soubor, BPM, průběh a srozumitelné chyby.
> Zastavit tanec ukončí show, Stop kufru také zachová bezpečnostní zotavení.
> Telefon zvuk nepřehrává a jeho odpojení autonomní show neruší. Pro start
> uvolněte ruční řízení a ukončete rozhovor. [Příprava SD a hranice](../docs/dance-for-me.md).
> Starý firmware bez `dance` dál funguje, panel zůstane vypnutý; před
> instalací nového firmware aktualizujte mobil. Browser je vývojové
> ověření, nové ovládání na iPhonu zatím není fyzicky akceptované.

> D166: secured BLE status can include current STA IPv4 (`n.ip`). The iOS native
> layer uses it for the paired device for up to 60 seconds while that BLE session
> remains connected. The explicit Control connect tap refreshes the live BLE
> status first, even while its panel is hidden. Manual address selection takes precedence. The stored token
> and certificate pin still authenticate WSS; the route is saved in Keychain only
> after authenticated connection succeeds. Offline/missing/invalid IP and BLE
> disconnect clear the hint. Bonjour failure remains visible; BLE can supply an
> address independently. Each socket captures the selected host/device ID before
> asynchronous listener registration; BLE loss cannot switch that attempt to an
> older hostname. Hardware service/calibration gates remain unchanged.

## Aktuální předání D166

Majitel zadal ručně `192.168.0.59` a obdržel servisní stav, který UI ukazuje
až po validním snapshotu: tento důkaz potvrzuje autentizované pinned WSS přes IP.
Hostname timeout byl pozorovaný; konkrétní příčina DNS/multicast na iPhonu není doložená.
D166 předává aktuální STA IPv4 jako volitelné `n.ip` pouze přes zabezpečené BLE;
offline/změna generace ho vynechá a při 512 B limitu se vynechá jen tento hint.
Nativní RAM hint je vázaný na session/device ID a platí 60 s. Connect obnoví status
živé BLE session i ve skrytém panelu; pending/skipped/failed/stale refresh pokus
zastaví. Failed refresh vymaže logické BLE spojení, takže další tap může použít
uloženou/ruční route. Ruční adresa má přednost. Host/device ID se zachytí před
async listener await; nativní validace přijme oba platné odpovídající údaje nebo
oba chybějící. Keychain pin/token zůstávají stejné a route se uloží až po auth onOpen.
Nejisté povely ani hardware gates se touto změnou neobcházejí.

[PR165](https://github.com/Blato58/DildoVander/pull/165) se sloučilo jako
`b22f478ffcba03bf052b8073a9ed4fd1a53be47b` ze source
`e4d215cfe10b2d293c74cdf9e581efc974ae80a4`.
[CI 37945851773](https://github.com/Blato58/DildoVander/actions/runs/37945851773), run number 167,
prošlo; shodný tree CI/main je `dc21879478bd6f691b67b3c8934e2d731f8e0f6f`.
[OTA publikace 37946520625](https://github.com/Blato58/DildoVander/actions/runs/37946520625)
zveřejnila **0.1.168**; veřejné HTTPS, ECDSA podpis, hash a embedded verze byly ověřené.
[iOS podpis 37946514768](https://github.com/Blato58/DildoVander/actions/runs/37946514768)
prošel všemi třemi native harnessy (pinned LAN/Keychain/WSS/captured route,
native Debug backend a secured BLE whitelist), Xcode archive a Ad Hoc export.
Podepsaná IPA má ověřené Bundle ID `app.turquoise6409.green2444` a verzi
**1.0 / build 1000.0.23**.
[Safari publikace 37947083377](https://github.com/Blato58/DildoVander/actions/runs/37947083377)
prošla všemi joby. Veřejná [instalační stránka](https://dildovander.de/ios/)
a manifest potvrzují **1000.0.23**; veřejná IPA a checksum odpovídají podepsanému artefaktu.
Application-only zápis **0.1.168** na COM7 do `0x10000` prošel hash kontrolou;
NVS, otadata a partition table zůstaly zachované. Normální RTS boot potvrdil
**0.1.168**, start control loop a místního TLS serveru; PC BLE scan našel
`KUFR_4B6048` s očekávaným service UUID. Aktuální dodaná sestava je
**0.1.168 / 1000.0.23**. D165 **0.1.162 / 1000.0.22** zůstává historické.
Přesné dodání vede
[stav implementace](../docs/stav-implementace.md#předání-další-ai).
Majitel na přesný dotaz k aktualizaci **1000.0.23**, zabezpečenému BLE
s uloženým párováním a připojení bez psaní IP odpověděl **„Connection worked“**.
To potvrzuje automatické připojení D166 v této zkoušce. Další krok: fyzická
kalibrace, dlouhodobé WSS, Keychain po restartu/aktualizaci a negativní DHCP/
síťové/lifecycle scénáře. Úplná fyzická akceptace zůstává otevřená.

## Historické předání D165

Po **Zapomenout párování** iPhone smazal místní profil, ESP ale zachovalo token
hash a další BLE párování vracelo `already_paired`. D165 nabídne nativní
**Obnovit párování kufru?**; až po potvrzení odešle `recover-local` s `confirm:true`
přes stejné ověřené Security1/PoP BLE. Zrušení ani automatický reconnect párování
nemění. Zdravý Paired záznam získá nový token; QR/chybějící/poškozená storage
se neobchází. Připojený ovladač, handshake, čekající povely/disconnect, ruční lease,
hlas a maintenance vracejí `busy`. TLS identita a Wi-Fi zůstávají zachované.
Token/pin zůstávají nativní; nový credential se vloží jen do prázdného Keychain
profilu, existující profil se nepřepisuje.

[PR163](https://github.com/Blato58/DildoVander/pull/163), merge
`f752c1e0009d25b6827657f789bd35ff943c74f4`, source
`50b6b0605279884be5c2ed969254e2a57f32411a`, prošlo
[CI 37936213640](https://github.com/Blato58/DildoVander/actions/runs/37936213640)
s firmware **0.1.162**.
[OTA publikace 37936948294](https://github.com/Blato58/DildoVander/actions/runs/37936948294)
prošla; veřejné HTTPS, ECDSA podpis, hash a embedded verze byly ověřené.
COM7 app-only zápis na `0x10000` prošel hash kontrolou bez změny NVS. Normální RTS
boot potvrdil **0.1.162**, main stack 16384 B / minimum remaining 12540 B,
BLE advertising, control loop a TLS server v 18899 ms.
[iOS podpis 37936943860](https://github.com/Blato58/DildoVander/actions/runs/37936943860)
prošel všemi třemi native harnessy, Xcode archive i Ad Hoc exportem pro
**1.0 / build 1000.0.22**. [Safari publikace 37937381222](https://github.com/Blato58/DildoVander/actions/runs/37937381222)
prošla všemi třemi joby. Veřejná instalační stránka a manifest uvádějí
`app.turquoise6409.green2444` / **1000.0.22**; IPA 1873296 B a veřejný checksum
byly ověřené, SHA-256 `f5cb611018ea898b215701bda0982c76dcd0dff7d1738421e9d13299fb39ff3e`.
PC BLE scan po flashi našel `KUFR_4B6048` s očekávaným service UUID.
Majitel následně potvrdil opakované párování, ale LAN discovery/připojení
stále selhává. PC Bonjour a neautentizované TLS zůstávají dostupné; token/pin
telefonu, stabilní WSS a Keychain po aktualizaci zatím nejsou ověřené.
Přesný rozsah vede [stav implementace](../docs/stav-implementace.md#předání-další-ai).

Pozdější ruční IP test zobrazil device service state; source ho vydá až po validním
snapshotu, tedy dokládá přímé IP WSS. Hostname timeout zůstává otevřený.
Hardware/calibration gate se neobchází; automatická adresa a dlouhodobé WSS/
Keychain kontroly zbývají.

## Historické předání D164

Development OTA **0.1.161** prošla
[CI 37931935316](https://github.com/Blato58/DildoVander/actions/runs/37931935316)
a [publikací 37933188173](https://github.com/Blato58/DildoVander/actions/runs/37933188173).
[PR161](https://github.com/Blato58/DildoVander/pull/161) zvětšilo main stack
pro načtení existující TLS identity. Po majitelem povolené USB obnově aplikace
bez smazání NVS skutečná deska při normálním RTS restartu spustila control loop,
BLE advertising i místní TLS server; PC scan našel `KUFR_4B6048` s očekávaným UUID.
Před obnovou končila **0.1.157** main stack overflow ještě před BLE startem.

V D164 iOS zůstávalo **1.0 / build 1000.0.21** z
[podpisu 37929159117](https://github.com/Blato58/DildoVander/actions/runs/37929159117)
a [Safari publikace 37929535139](https://github.com/Blato58/DildoVander/actions/runs/37929535139);
v D164 se znovu nepodepisovalo ani nepublikovalo. PC scan a firmware boot
nepotvrzují nový iPhone handshake, stabilní Ovládání přes pinned WSS ani
uchování Keychain po aktualizaci. Majitel následně potvrdil novou zkoušku na
stejné Wi-Fi: „yes I am connected“. Připojení v Ovládání je tím potvrzené;
dlouhodobý provoz a Keychain po aktualizaci zůstávají otevřené. Přesné předání vede
[stav implementace](../docs/stav-implementace.md#předání-další-ai).

## Historické předání D163

Development OTA **0.1.160** s opravou přijetí WebSocket extension offers je
[publikovaná](https://github.com/Blato58/DildoVander/actions/runs/37929155208).
[Podpis 37929159117](https://github.com/Blato58/DildoVander/actions/runs/37929159117)
prošel native pinned/debug/BLE harnessy i archive/export pro **1.0 / build 1000.0.21**.
[Safari publikace 37929535139](https://github.com/Blato58/DildoVander/actions/runs/37929535139)
prošla; [instalační stránka](https://dildovander.de/ios/) nabízí tento build.
Veřejný manifest, IPA 1870043 B a checksum byly ověřené; SHA-256 IPA je
`8dc624add9451524953476e54889b6d87886ee7078c32a9cf0d1e651aaba4a7d`.
Nativní Apple WSS test potvrdil čtyři nabídky extensions; server je nemusí vybrat.
Mobil nyní ukazuje pouze bezpečný failureCode a omezuje reconnect.
Majitel před opravou potvrdil scan/párování, ale Ovládání se na stejné Wi-Fi
opakovaně odpojovalo. Nová instalace, stabilní pinned WSS a Keychain po aktualizaci
zůstávají fyzickou akceptací podle [stavu implementace](../docs/stav-implementace.md#předání-další-ai).

## BLE nastavení sítě a firmware

Po ověřeném BLE připojení zvolte **Spárovat ovládání přes Wi-Fi**.
Adresa kufru, SPKI pin a první místní token se převezmou přes šifrované BLE
bez dalšího zadávání. Token a pin zůstávají v nativním nesynchronizovaném
Keychain `WhenUnlockedThisDeviceOnly`; stabilní Bundle ID/Team ID zachová
přístup při aktualizaci aplikace. JavaScript dostane jen ID a adresu.
V záložce **Ovládání** pak zvolte **Připojit uložený kufr**. Telefon a ESP
musí být ve stejné LAN; Bonjour hledání i uložené `.local` jméno řeší adresu.
Samotné discovery nenahrazuje autentizaci a TLS pin.

Tlačítko vyžaduje již ověřené BLE/PoP a firmware s `pair-local` (D161).
Existující profil jiného kufru se nepřepisuje; starší servisní QR párování
se nezneplatňuje. Ztracená odpověď či selhání Keychain po vydání tokenu
se automaticky neopakují. D165 přidává explicitní obnovu přes zabezpečené BLE:
po Zapomenout párování stiskněte stejné tlačítko a potvrďte Obnovit párování.
Starý přístup se zneplatní, nový token se vloží do Keychain bez přepsání
jiného uloženého kufru. Kufr musí být nečinný a ostatní ovladače odpojené.
Hesla Wi-Fi se do Keychain neukládají. PoP má vlastní oddělený profil z D160.

Záložka **Síť kufru** v nativní iPhone aplikaci vyhledá `KUFR_` zařízení
s projektovým BLE service UUID a použije oficiální Espressif ESPProvision
**3.2.0**, CoreBluetooth a Security1. Kufr musí mít otevřené omezené servisní
nebo obnovovací okno a provisionovaný jedinečný 32znakový PoP. PoP se zadává
v nativním zabezpečeném dialogu z vnitřního servisního štítku. Místní WSS
párovací token ani backend credential nejsou BLE PoP. Bez ověřeného
Security1 a povinného PoP klient neposílá síťové údaje.

Pro iPhone hotspot otevřete **Nastavení → Osobní hotspot**, zapněte
**Povolit připojení ostatním** a **Maximalizovat kompatibilitu** (2,4 GHz).
V aplikaci zvolte iPhone Osobní hotspot a zadejte název iPhonu (SSID) a heslo
z Nastavení. Aplikace hotspot nezapíná ani jeho heslo/SSID automaticky nečte;
tlačítko Nastavení používá pouze veřejné `UIApplication.openSettingsURLString`
a otevře nastavení oprávnění aplikace. Telefon se k vlastnímu hotspotu
nepřipojuje jako Wi-Fi klient. Při prvním připojení může být nutné ponechat
obrazovku hotspotu otevřenou a potom BLE znovu vyhledat a ověřit.

Hotspot je přednostní síť, **Záložní Wi-Fi** je alternativní profil.
Nativní dialog **Nastavit / nahradit údaje** přijme SSID 1–32 UTF-8 bajtů
a WPA heslo 8–63 bajtů. Po potvrzení se oba údaje odešlou přes autentizovaný,
šifrovaný `kufr-control` endpoint; JavaScript ani aplikační log heslo a PoP
nepřijímají. Wi-Fi hesla telefon neukládá. Úspěšně ověřený PoP ukládá nativně
do odděleného Keychain pro konkrétní BLE kufr, jen na tomto odemčeném iPhonu,
bez iCloud synchronizace. Při dalším připojení lze použít uložený PoP nebo
zadat nový; nahradí se až po úspěšném ověření. SDK logging je
výslovně vypnuté před jeho použitím. Swift/iOS/SDK mohou během operace držet
vlastní paměťové kopie; zdroj netvrdí garantované vynulování těchto kopií.

Stav se při otevřené záložce obnovuje každou sekundu bez souběžných BLE
požadavků. Ukazuje firmware, nastavené SSID, aktivní a poslední úspěšný
profil, výsledek Wi-Fi testu, backend a OTA. **Otestovat připojení sítě**
ověřuje uložený profil. Při výměně firmware zachová původní údaje až do
úspěšného testu nové sítě; chyba nesmí zrušit platný profil. **Smazat vybranou
síť** vyžaduje další nativní potvrzení a nemaže zařízení, párování ani druhou
síť. Mazání je dostupné až po ověřené BLE relaci.

**Zkontrolovat firmware**, **Nainstalovat ověřenou aktualizaci** a tlačítka
politiky automatické instalace používají stejné autentizované rozhraní.
Instalaci/změnu politiky potvrzuje nativní dialog. Firmware rozhoduje o
kompatibilitě, ověření podpisu a bezpečném klidu před instalací/restartem.
Telefon nikdy neflashuje paměť přímo. BLE timeout může znamenat nejistý
výsledek: znovu ověřte stav; aplikace změnu automaticky neopakuje.

Odchod do pozadí nebo uzamčení zruší nativní BLE relaci a vymaže její
přechodné PoP. Uložený PoP v Keychain zůstává; návrat vyžaduje nové explicitní
vyhledání/ověření, při kterém lze uložený PoP použít bez opisování.
Zavření nebo odpojení BLE neukončuje samostatný provoz kufru. Web zobrazuje
návod a nedostupný stav; nepoužívá Web Bluetooth a nesimuluje připojený kufr.

Lokální webové testy zahrnují zrušení/pozdní odpovědi, polling, ztrátu BLE
a zákaz opakování nejisté změny. `bash scripts/test-native-provisioning.sh`
na macOS ověřuje whitelist diagnostiky včetně vyřazení tajných polí,
nekompatibilního protokolu a nadměrného rámce. Xcode package resolver používá
přímo přesně připnutou závislost ESPProvision z projektu; generovaný
`CapApp-SPM/Package.swift` se kvůli ní neupravuje. Zdrojové napojení,
webový build a harness nenahrazují skutečný iPhone/BLE/hotspot/OTA test.

D159 na skutečném iPhonu s build1000.0.19 doložilo discovery, PoP/Security1
spojení a nastavení domácí Wi-Fi jako fallback. Majitel potvrdil automatické
Wi-Fi znovupřipojení a připojený backend i po OTA0.1.1 a dalším resetu ESP;
NVS readback a serverové ESP token POST200 byly ověřené nezávisle. OTA proběhla
automaticky na ESP, nikoli testem telefonních check/install tlačítek.
Hotspot/failover, wrong-PoP, BLE lifecycle/error scénáře, LAN TLS/pairing/Keychain
a skutečné audio zůstávají samostatnou akceptací. Přesný rozsah a důkazy jsou ve
[stavu implementace](../docs/stav-implementace.md) a
[BLE návodu](../docs/ios-ble-provisioning.md).

## Postava a přehrávání Live

Debug správa zachovává DV01 Reserved/Dry/Adult/Chaos, Blocked/Limited a reset
anonymní pestrosti pro nové rozhovory. Používá personalitu `ollivander-trunk`;
není zde přepínač legacy/scripted enginu. [DV01 návod](../docs/dv01.md)
odděluje deterministické kontroly od skutečné Live/iPhone/fyzické akceptace.

`audio_cancel` ruší přehrávání a související checkpoint callbacks při zachování
mikrofonu a session. `playback_checkpoint` se potvrzuje až po skutečném
`dataPlayedBack` drain. Reader přitom pokračuje. Lokální/macOS test grafu
neprokazuje fyzickou slyšitelnost ani změřené AEC. Tato změna sama nepublikuje
novou IPA ani nepřepíná nasazení.

## Hlas kufru v Ovládání (D119)

Sekce **Hlas kufru** nastavuje režim Vypnutý / Realtime a hlas
Marin/Cedar/Coral na spárovaném ESP. **Použít nastavení hlasu** čeká na
potvrzení konkrétního požadavku a bootu; heartbeat nepřepisuje rozpracovanou
volbu. Nastavení platí do restartu kufru. Pro **Spustit hlas na kufru**
uvolněte manual řízení a počkejte na bezpečný ready snapshot.
Spuštění uděluje audio consent tohoto rozhovoru; backend ukládá přepis do
paměti večera. **Zastavit hlas** a **Stop kufru** jsou odlišné akce.

Kufr používá vlastní mikrofon/reproduktor a backend; telefon nepřenáší
PCM ani provider klíč. Odpojení pozorovatele neukončí samostatný hlas.
UI ukazuje poslech, přípravu, řeč, dokončení i nákladový/audio/AI limit.
Start/configure se automaticky neopakují po ztracené ACK nebo reconnect.
Bez ověřeného audio profilu na ESP je Realtime nedostupné; webový fake a
stávající hostitelský simulátor to výslovně označují. Nejde o přenesení
samostatného telefonního testovacího rozhovoru z Debugu.

Současný kód nabízí obrazovku se šesti přihrádkami, ručním převzetím, potvrzením výběru/založení a Stop. Přes Vite používá místní fake stav; přes E3.2a vývojový most komunikuje s E2 C++ simulátorem. Oba vývojové režimy jsou výslovně označené. E10.1 přidává nativní iOS párování a přímý místní WSS transport se SPKI pinem a Keychain; jeho fyzická akceptace ještě neproběhla. Zvolenou architekturu určuje [implementační plán](../docs/implementacni-plan.md), skutečný postup a předání [stav implementace](../docs/stav-implementace.md).

## Cílová role aplikace

První platforma je **iOS/iPhone**: zachovat TypeScript/Vite UI a doplnit Capacitor se Swift adaptéry. Zdrojové soubory vyvíjet na Windows, nativní build/test/podpis provádět vzdáleně na macOS GitHub Actions. Webový režim slouží pro vývoj a simulátor, ne jako finální produkční ovladač.

ESP32 samo komunikuje s backendem; aplikace není cloudový prostředník a nemá AI provider klíč. Místní WSS nabízí ruční výběr 1–6, potvrzení/Stop, snapshot, opravu AI návrhu, text a případně telefonní mikrofon přes stejné device sezení. Offline ovládání musí fungovat bez WAN. Připojení je nejprve pozorovatelské; explicitní převzetí vytvoří ruční lease. Odchod do pozadí nebo ztráta lease zastaví ruční řízení, odpojení pozorovatele neovlivní samostatný kufr.

Nativní adaptér vlastní TLS pin, oprávnění LAN a Keychain. E10.1 používá ručně zadané údaje z vnitřního QR; E10.2 přidává text/cancel/result a E10.3 Bonjour hledání i ruční změnu adresy. Audio navazuje po E9. UI nesmí přímo časovat motory ani přijímat AI text jako povel. Konkrétní slices a kontrakty jsou v kapitolách 3–5 plánu.

## Rozhovor v nativní aplikaci

Produkční nabídka má právě **GPT Live (`gpt-live-1`)**, hlavní zkušenost,
a **GPT Realtime 2.1 (`gpt-realtime-2.1`)**, záměrnou alternativu. Volba se
ukládá v `localStorage` pod `kufr.conversation.engine.v1`. Retained hodnoty
`gpt-live` a `realtime` zůstávají zachované; staré `realtime-ptt` se výslovně
převádí na `realtime` s oznámením. `chained`, neznámá nebo prázdná uložená
hodnota vyžaduje novou volbu. Nedostupný engine se nikdy nenahrazuje druhým.

V Diagnostice nastavte samostatný servisní backend profil a obnovte stav
serveru. Credential zůstává v Keychain; webový režim ho nedostává. Server
vrací `engines.schemaVersion=1`, model, místní připravenost konfigurace,
dostupné hlasy, podporu tempa, zdroje kamer a vstupní režimy. UI blokuje
neznámou verzi, chybějící konfiguraci i nepodporované nastavení. Místní
připravenost nepotvrzuje vzdálený přístup účtu k modelu: ten ověří spojení.
Při neúspěchu operátor dostane chybu a může obnovit stav nebo vědomě zvolit
jiný engine. Live má pevné tempo 1×; Realtime nabízí podporovaný rozsah.
Staré přepínače Sol/fake a STT/TTS už nejsou produkčními režimy.

**Zahájit rozhovor s novým hostem** vytvoří nové sezení. **Ukončit rozhovor**
je oddělené od **Stop kufru**, které vlastní fyzickou bezpečnost. UI zachovává
generaci akcí a serializuje start, úklid i čtení stavu; pozdní odpověď starého
hosta nepřepíše nového. Skutečný engine/model a souběžná aktivita vstupu,
výstupu a nástrojů pocházejí z nativního stavu, nikoli z odhadu UI.

Fotka je volitelná, jen po souhlasu a potvrzení nativního náhledu. Je možné ji
pořídit i během aktivního hlasu; fotoaparát má vlastní čekání a zrušení.
Zrušení/selhání fotografie neukončuje rozhovor. Připravený snímek vyprší za
dvě minuty a patří jednomu rozhovoru. V aktivním sezení se schválená fotka
odesílá ihned; při selhání zůstane tlačítko pro opakování. Úspěch UI ukáže
až po potvrzení `status=analysed`, image ID a revizi serveru. Změna enginu,
ukončení nebo nový host zneplatní její aktivní kontext. Neodeslaný koncept
se neukládá do galerie; odeslaná fotografie zůstává v soukromém serverovém
archivu rozhovoru do výslovného ručního smazání. Vzhled je nejistá nápověda
a neověřuje identitu. Zrušení kontextu a smazání archivu jsou odlišné operace.

Vstupní PCM proudí jedním autentizovaným WebSocket spojením daného
rozhovoru a streamu. Rámce zůstávají pořadově označené; 200ms rámec už
nečeká na samostatné HTTP potvrzení před odesláním dalšího. Nativní vrstva
vlastní zvukovou frontu, tlak na přenos, přerušení a úklid. Technický limit
fronty není důkaz fyzické slyšitelnosti.

**Identita a souhlas pro nového hosta** umožňuje vracejícímu se hostovi
použít stejné stálé evidenční ID. ID rozlišuje velikost písmen a má přesně
1–80 znaků ze sady `[A-Za-z0-9._-]`; mezery ani Unicode se nenormalizují
a musí se opravit. Nejde o odhadnuté jméno. Obsluha výslovně ověří identitu; dlouhodobá osobní
paměť a cloudový export mají oddělené souhlasy. Nativní vrstva sváže nově
vytvořený rozhovor před prvním provider spojením. Chyba vazby zastaví start;
nevznikne anonymní náhradní rozhovor. Pole se po startu nebo ukončení smažou,
a další host proto nepřebírá identitu. Bez vyplněného ID začne anonymní host.

Sekce **Paměť hosta** ukazuje aktuální/poslední ID rozhovoru a dovoluje
načíst jiný konkrétní rozhovor pod stejným servisním profilem. Zobrazí
nejvýše 50 oprávněných záznamů, jejich zdroj a revizi, skutečnou vazbu
identity a stav místní/cloudové paměti. Nevydává lokálně nakonfigurovanou
službu za vzdáleně ověřenou dostupnost. Před smazáním je nutné ukončit
rozhovor, načíst aktuální revizi a zaškrtnout potvrzení pro tento rozhovor.
Při změně revize je třeba čtení a potvrzení opakovat. UI rozlišuje potvrzené
místní odstranění a dosud čekající cloudové odstranění; nemaže jiné osobní
scope ani archiv památek. Text paměti se nezapisuje do technického logu.

Pro reproduktor zůstává samostatný český systémový test bez AI. Testovací
simulované přihrádky ani návrh v Diagnostice nespouštějí pohon. Přechodný
přepis telefonu se neukládá do technického logu. Ukončení sezení nemaže
historii večera na serveru.

`tests/native-ui-harness.html` je výslovně označený syntetický bridge bez
sítě, mikrofonu, kamery a kufru. Podporuje nedostupný engine pomocí
`?engineUnavailable=gpt-live` nebo `realtime` a scénáře zrušení, zamítnutí,
selhání a zpožděné fotografie. Browser a unit testy neprokazují iPhone,
povolení kamery, skutečnou latenci ani fyzický výstup; ty vyžadují zařízení.

## Nativní připojení E10.1

Na iOS se vývojový fake automaticky nezapne. Zadejte adresu bez schématu/portu,
device ID a SHA-256 **DER SubjectPublicKeyInfo** z vnitřního QR (64 malých hex
znaků; není to otisk celého certifikátu). Jednorázové párovací tajemství zadáte
v zabezpečeném nativním dialogu. HTTPS `POST /v1/pair` připíná tento klíč;
bearer token se uloží pouze do nesynchronizovaného Keychain
`WhenUnlockedThisDeviceOnly`. JavaScript ho nečte a nemá cloudové přístupy.
Lokální pin je autoritou pouze pro toto oddělené spojení v LAN, včetně
self-signed certifikátu; nejde o změnu cloudového PKI. Redirect se odmítá.
Nejsou povolené ATS výjimky a logování Capacitor bridge je vypnuté.

Tlačítko Připojit otevírá pozorování přes `wss://<adresa>:443/v1/control`.
Teprve explicitní převzetí získá ruční lease. Stejný protokolový řadič jako
simulátor hlídá snapshot, čas, retry a nejistý výsledek; nativní zprávy navíc
procházejí úplným sdíleným v1 schématem a strict JSON parserem. Token není v URL.
Události nesou UUID spojení, takže opožděné callbacky neovládnou nové spojení.
Odchod do pozadí nebo uzamčení uzavře nativní socket; návrat vyžaduje nové
explicitní připojení. Odpojení ručního vlastníka řeší také firmware.

Zapomenutí má nativní potvrzení: původní token nelze z kufru znovu získat,
nové párování lze u zdravého spárovaného ESP obnovit přes Security1/PoP BLE
a explicitní nativní potvrzení D165. Totéž platí při ztrátě úspěšné
párovací odpovědi nebo selhání Keychain zápisu po spotřebování tajemství.
Neověřená/prázdná NVS nebo kalibrace 0 na desce se z telefonu neobchází.
Živý ESP32, LAN permission, Keychain po aktualizaci a uspání iPhonu zůstávají
samostatnou akceptací B1/E10. Původní E10 slice IPA nepublikoval; přesný
podpis a nasazení opravy D165 vede stav implementace.

macOS CI navíc spouští `bash scripts/test-native-transport.sh`: porovná SPKI
EC P-256/P-384 a RSA s OpenSSL, ověří Keychain a skutečné loopback HTTPS/WSS
včetně odmítnutí jiného pinu, tokenu, redirectu a nepovolených rámců. Test
používá jednorázové syntetické identity; nepotřebuje kufr ani placený provider.

Nativní spojení vrací jen bezpečný failureCode; UI rozlišuje síť, pin,
autentizaci a protokol. Neplatná identita/pin, odmítnutá autentizace/handshake,
TLS nebo protokol zastaví retry ihned. Ostatní chyby včetně dočasné ztráty
sítě mají nejvýše tři retry s backoffem 0,5/1/2 s a jitterem. Po welcome
zůstane ovládání v pozorování; limit obnoví až pět sekund platných snapshotů.
Po zastavení lze zkusit **Připojit uložený kufr**. Výslovné odpojení retry
zruší; uložené párování zůstává v Keychain.
Testovací `/tests/native-ui-harness.html` nabízí syntetický bridge pouze přes
Vite pro kontrolu formuláře a recovery UI. Není v produkčním bundlu a nenahrazuje
native alert, UIKit lifecycle ani zkoušku oprávnění na iPhonu.

## Místní hledání E10.3

Tlačítko Hledat spustí nativní `NWBrowser` pro `_kufr._tcp.local`, nejvýše na
5 sekund a s nejvýše 16 resolvery. Telefon přijme pouze port 443 a public TXT
`deviceId`, `protocol=1`, `capabilities` (CSV). Nalezená jména jsou adresní
vodítka; otisk ani tajemství z Bonjour nepochází. Vybrání neotevře socket.
U spárovaného kufru hledání ukazuje pouze odpovídající device ID; TLS pin
a bearer z Keychain zůstávají autoritou i při podvrženém inzerátu.

Bez nálezu, při odmítnutí LAN nebo změně adresy lze zadat IP/DNS i u již
spárované identity. Adresa bez schématu/portu zůstává v RAM do úspěšného
připnutého autentizovaného WSS, pak se uloží se stejným pinem a tokenem.
Nové připojení vyžaduje tlačítko Připojit. Pozadí hledání ukončí a zruší resolvery.
UI při nedostupné LAN vysvětluje povolení v Nastavení; router může použít DHCP
rezervaci. Plist obsahuje pouze skutečně používanou Bonjour službu.

macOS harness ověřuje public TXT/host/port parser, odmítnutí pin/token polí,
jednorázový cancel, pětisekundovou deadline a skutečné NWBrowser/NetService
vyhledání lokálního syntetického inzerátu na macOS. Neprokazuje multicast mezi
skutečným ESP32 a iPhonem ani systémový dialog LAN. Čtení QR kamerou a
servisní AP/Wi-Fi provisioning zatím nejsou implementované; párovací formulář
je ruční a fyzické provisionování zůstává podmíněné B1.

## Textový vstup E10.2

Ruční vlastník může při čerstvém ready snapshotu s capability `conversationText`
poslat jednu odpověď (1–1000 Unicode znaků, zpráva do 8192 UTF-8 bajtů) nebo
zrušit aktivní tah. Telefon posílá device/boot/lease/session/generation/revision
kontext pouze kufru; backend volá ESP32. UI před odesláním uvádí cloudové
zpracování a trvalou paměť večera. Text se neukládá do telefonu, logů ani
odložené fronty. Vývojový simulátor má výslovně pevnou simulovanou repliku.

Reply musí odpovídat přijatému clientTurnId a aktuální generation/bootu, mít
novější eventSequence a projít nativním v1 parserem. Vykresluje se jako text,
nikdy HTML ani číslo aktuátoru. Cancel ihned skryje starý výsledek a čeká na
ACK a autoritativní snapshot nové generation. Stop, revision/lease/boot změna,
pozadí a odpojení obsah zneplatní. ACK timeout je 3 s, čekání na reply 45 s;
nejasný výsledek obnoví spojení bez opakování textu. Během tahu nejde potvrdit
ruční výběr přihrádky; Stop zůstává dostupný. Telefonní audio zůstává po E9.

## Současný lokální webový vývoj

### Diagnostika přihlášení a aplikačních akcí

Sdílený transport účtu zaznamenává na webu i iOS posledních 200 dokončených
požadavků v paměti tohoto spuštění: čas, anonymizovanou cestu, HTTP metodu,
výsledek, délku a serverové `X-Request-Id`. Bez těla požadavku/odpovědi,
parametrů URL, identifikátorů účtu nebo zdrojů, hesel a tokenů. Odhlášení nebo
změna účtu přehled vymaže; opožděné výsledky jej znovu nenaplní.
Průběh passkey ověření se zaznamená i při lokálním přerušení před HTTP odpovědí.

V Nastavení → Diagnostika připojení lze připravit JSON pro podporu nebo přehled
vymazat. Soubor se nikam automaticky neodesílá. Tester Flow přidává tento přehled
do svého technického exportu a u HTTP chyby ukazuje ID požadavku. Serverové
logy podle stejného ID dohledají konkrétní akci; jejich čtení vyžaduje stávající
oprávnění správce. Běžný uživatel tím nezíská přístup k serverovým logům.

Obnovení testu zachovává dostupné části stavu i při chybě jiného čtení. Úspěšné
obnovení odstraní chybu předchozího čtení; nepotvrzená změna nebo zpráva zůstane
označená a sama se znovu neodešle.

### Spuštění a kontroly

Ve složce `mobilni-aplikace` použijte Node.js 24.21.0 z `.nvmrc`:

```text
npm ci
npm ci --prefix ../contracts
npm run dev
```

Kontrola sestavení:

```text
npm run build
npm test
```

Build kontroluje shodu generovaného validátoru kontraktu, Vue typy přes
`vue-tsc --noEmit` a sestaví Vite bundle. `npm test` ověřuje protokoly,
lease, rekonciliaci a skutečné Vue komponenty; `npm run test:browser` po buildu
ověří sestavené routy, soukromé odkazy, focus, CSP a responzivní rozložení.
Tyto kontroly neověřují iOS podpis, LAN ani fyzický kufr.

TypeScript 6.0.3 je připnutý kvůli kompatibilitě aktuálního `vue-tsc`;
TypeScript 7 změnil exporty kompilátoru, které tento nástroj potřebuje.
Testovací moduly převádí
Node.js `stripTypeScriptTypes` v režimu `transform`, který podporuje také
parametrové vlastnosti konstruktorů. Node zatím vypisuje upozornění na
experimentální API. Deklarace `vite/client` pokrývají import CSS a assetů.

Pro E2 simulátor sestavte aplikaci, spusťte E2 WSS server a místní most podle
[návodu simulátoru](../firmware/simulator/README.md), potom jej výslovně vyberte
v Připojení. Aplikace se podle `/dev-mode` nepřipojuje automaticky. Web nevidí
bearer token ani certifikát upstreamu. Po ztrátě spojení zablokuje povely;
nejasný výsledek vyžaduje kontrolu mimo UI. Snapshot ani ACK nejsou důkaz
fyzického otevření.

Vzhled **Vapour Chrome** používá společné tokeny a lokálně balené fonty Inter
a IBM Plex Mono z `../design-system/arcane-workshop.css`. Ovládání, Diagnostika a Síť kufru zachovávají
stávající funkce; hlas a textový rozhovor zůstávají v Ovládání. Horní lišta
ukazuje Stop kufru (ve vývoji Stop simulace) také při přepnutí do Diagnostiky nebo
Síť kufru. Používá stejný povel a stejnou dostupnost podle přijatého stavu,
nepřidává potvrzení a neobchází zotavení. Instalační Safari stránka sdílí vzhled
přes generovaný canonical CSS blok vložený přímo do samostatného publisheru;
`/design-system/arcane-workshop.css` dodává lokální fonty při připojení. Archiv
offline používá společné systémové fontové fallbacky. Blok se generuje z
`tokens.json` a skutečných sdílených komponent, paletu ručně nekopírujte.

Spodní navigace Ovládání/Síť kufru/Diagnostika a horní lišta respektují iOS safe
area. Viewport má `viewport-fit=cover`; horní lišta přidává `safe-area-inset-top`
k odsazení, spodní navigace používá `safe-area-inset-bottom` a obsah má rezervu
pod posledním ovládacím prvkem. Boční insets chrání okraje v orientaci na šířku.
Hodnoty dodává WebKit podle zařízení a orientace. Ve webovém režimu s nulovými
insets zůstává základní odsazení.

## Etapy a vzdálený iOS build

E3a má zdrojový projekt `ios/App/App.xcodeproj`, sdílené scheme `App` a [webový CI workflow](../.github/workflows/kufr-web.yml). Capacitor `@capacitor/core`, CLI a iOS jsou připnuté na stejnou přesnou verzi uvedenou v `package.json`. Po změně webu lze na Macu synchronizovat balíček příkazy:

```text
npm ci
npm ci --prefix ../contracts
npm run build
npx cap sync ios
```

CLI používá `xcode`, jehož závislost `uuid` je přes cílené npm override
připnutá na 11.1.1 kvůli GHSA-w5hq-g745-h8pq. Tato verze zachovává CommonJS
API `v4`, které parser projektu používá; uuid 12 už CommonJS neposkytuje.
Swift `Package.swift` generuje `cap update ios` nebo `cap sync ios`;
`Package.resolved` pochází ze skutečného `xcodebuild -resolvePackageDependencies`
na macOS. [Dependabot automatizace](../docs/ci-cd.md#generovane-zavislosti-v-dependabot-pr)
při změně Capacitoru oba soubory přegeneruje a commitne do stejného PR.
Finální macOS PR CI pak ověřuje nativní testy a build.

Na Windows lze tyto příkazy použít pro kontrolu zdrojů. PR se změnou mobilu
nebo jeho závislostí spouští přes `ci.yml` mobilní testy a webový build na Linuxu
a `kufr-ios.yml` na macOS s Xcode 26.3: nativní Swift testy transportu/TLS,
backendového klienta a hlasu, BLE statusu a nepodepsaný iOS Simulator build.
Selhání iOS ověření blokuje `CI required`. Stejné ověření lze spustit ručně
volbou `project=ios` nebo `all`; build ani testy nepotřebují podpisové secrets.
Testy běží v macOS harnessu, Simulator cíl se sestavuje bez spuštění aplikace;
fyzické chování na iPhonu vyžaduje vlastní zkoušku. PR kontroluje balení
bez ukládání artefaktu; po merge spusť ručně `ci.yml` na `main` s `project=web`.
Tento webový artefakt má retenci jeden den a macOS build jej převezme bez překladu.
Ruční workflow
**Build signed Kufr IPA** (`kufr-ios-sign.yml`) na `main` dostane `build_run_id`
úspěšného CI, ověří Xcode 26.3 a nativní transport/debug testy a sestaví
podepsaný device cíl v environmentu `ios-signing`. Používá výhradně Ad Hoc
distribuční profil pro nasazení; podepsaný artefakt má retenci tři dny. Samostatné
**Deploy Kufr IPA** (`kufr-ios-deploy.yml`) dostane
ID úspěšného podepsaného buildu a publikuje instalační stránku na
`https://dildovander.de/ios/` bez dalšího buildu. Oba kroky ověřují shodu zdrojového
stromu s aktuálním `main`; [CI/CD návod](../docs/ci-cd.md) popisuje obnovovací build.
Do environmentu patří secrets `IOS_DISTRIBUTION_CERTIFICATE_BASE64`, `IOS_DISTRIBUTION_PROFILE_BASE64`, `IOS_P12_PASSWORD` a `IOS_KEYCHAIN_PASSWORD`. Vývojový certifikát a profil tento workflow nepoužívá. Heslo se neukládá do artefaktu. Certifikáty a profily jsou Base64 celého souboru; žádný ze souborů ani hesel nepatří do repozitáře. Environment může mít vlastní schvalovací pravidla.

Nativní identita má Bundle ID `app.turquoise6409.green2444` a Team ID `3QRXV8G5Y3`. Majitel potvrdil iPhone 15 Pro s iOS 26.6.1 a jeho UDID v dodaném profilu. Vývojový profil expiruje 25. 12. 2026; Ad Hoc profil pro stejný tým, Bundle ID a jedno zařízení expiruje 24. 12. 2026. CI kontroluje shodu identity, platnost a certifikát, exportuje IPA a ověřuje její podpis a embedded profil. Při chybě importu `.p12` zkusí OpenSSL čtení a kompatibilní přebalení. Publikační job kontroluje SHA-256, Bundle ID a metadata IPA, vytvoří manifest a z externího runneru znovu stáhne stejné soubory přes HTTPS. [Ad Hoc běh 36636991415](https://github.com/Blato58/DildoVander/actions/runs/36636991415) prošel všemi joby a majitel 30. 9. 2026 potvrdil úspěšnou instalaci a spuštění na iPhonu. Stabilní identita je důležitá pro aktualizaci a Keychain. E10.1–3 doplňují native TLS/Keychain, text a Bonjour a fyzický běh těchto funkcí ani aktualizace přes Safari zatím nejsou ověřené. GitHub Release, Pages, feed a ntfy zde nejsou zapnuté.

## Serverové logy v Debug D118

**Debug → Log serveru** používá uložený nativní profil správce. Nabízí načtení
nových událostí, filtr Všechny / Varování a chyby / Chyby, volitelné obnovování
každé 2 sekundy a nativní sdílení JSON. V telefonu drží nejvýše 200 událostí.
Čtení běží nezávisle na hlasovém rozhovoru; Stop/pozadí/uzamčení ho ruší.
Změna profilu/filtru odstraní staré zobrazení; mezera v serverové historii a
selhání zápisu jsou viditelné. Neúspěšné načtení ponechá poslední označený
snímek a vypne automatické obnovování. Starší server bez D118 vyžaduje upgrade.

Log obsahuje čas UTC, technický event/code, stav a trvání HTTP, UUID požadavku,
operace a rozhovoru. UUID nebo JSON lze sdílet pro
[společný debug s Codexem](../backend/codex/README.md). Native credential zůstává
v Keychain; JS nikdy nedostává token, těla požadavků, audio ani provider chyby.
Webový vývoj funkci označuje jako nativní; syntetický UI harness ji simuluje.

## D142: inventář personalit a debug test

**Debug → Server → Personalita → Použít nastavení serveru** přepíná mezi
původním party kufrem a Ollivanderovým kouzelným kufrem. Volba platí pro
celý backend včetně samostatného ESP a ukládá se i přes restart. Přepíná
se mezi rozhovory s existujícím admin přístupem; změna sama AI nevolá.
Seznam, popis a kontrolní hash promptu dodává server. Starší backend
vyžaduje aktualizaci; starší aplikace při změně režimů volbu zachová.

**Zkušební rozhovor → Zahájit rozhovor s novým hostem** používá právě vybraný
Live nebo Realtime engine se stejnou aktivní personalitou. Tón, skutečnou
latenci a poslech ověřuje samostatná zkouška na iPhonu. [Inventář zdrojů](../ai-eval/personalities.md).

## D146: historický doklad publikace

Dne 6. 10. 2026 byla na [instalační stránce](https://dildovander.de/ios/)
publikována Ad Hoc **1.0 / build 1000.0.16** pro dosavadní registrovaný iPhone.
Tento oddíl zachycuje tehdejší verzi a serverovou konfiguraci D146;
historické předání D162 je **1.0 / build 1000.0.20**; aktuální D166 vede přehled nahoře.
[Podpis 37429759564](https://github.com/Blato58/DildoVander/actions/runs/37429759564)
ověřil Xcode 26.3, nativní transport/debug/audio testy a podpis;
[publikace 37429982348](https://github.com/Blato58/DildoVander/actions/runs/37429982348)
ověřila manifest, MIME a checksum stažené IPA přes HTTPS.
Aktualizaci spusťte v Safari; instalace této verze a živý poslech ještě
nejsou ověřené. Stávající Bundle ID zachovává identitu aplikace a Keychain.

Původní provozní postupy D128/D145 se současného rozhovorového systému
netýkají. Aktuální dva enginy, transport, kamera a operátorská paměť jsou
popsané výše; historické provozní detaily zůstávají v historii Git.

## Public entry and native display

The public home page offers iPhone installation, models and public stories.
Suitcase, pairing, diagnostics and Director routes require a verified account session
and an Owner, Administrator or Operator role; administration requires Owner or
Administrator. Server resource grants and the native device's pairing credentials
remain independent checks. Logout or role removal unmounts operational views and
clears their retained UI state. A web route guard is not a substitute for API or
device authorization.

The browser cannot provide iPhone BLE, Keychain or the pinned local transport.
Use the installer at `/ios/`; web demo and simulator views remain explicitly
labelled and do not establish physical-device acceptance. PC control is not
claimed as supported.

The native iOS shell uses portrait orientation and a fixed page scale, including
input focus and pinch gestures. This policy is injected only into the native
main frame; browser zoom is retained. iPad runs full-screen to preserve the
portrait-only contract. Native changes require a newly signed IPA and device
update. macOS compilation and synthetic viewport tests do not prove actual
keyboard/rotation behavior on an installed iPhone.

Offline local control preserves the last verified native account in a
ThisDeviceOnly Keychain entry, bound to the exact origin and account cookie.
It expires at the earlier of the server session expiry and twelve hours after
verification. Connectivity-only failures can reuse that snapshot for local
suitcase, pairing and diagnostics routes, including after app restart. Fallback
reads never renew it or publish cloud CSRF authority. TLS, cancellation, HTTP,
decode errors, confirmed logout/denial and expiry do not admit fallback.
Director, settings and administration remain online-gated. Role revocation is
rechecked on reconnection; the independent paired ESP identity remains required
for local physical control. The additive `/app/v1/session.expiresAt` value is the
actual database expiry; older clients may ignore it, and older servers cannot
create a native offline snapshot.
