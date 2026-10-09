# Mobilní ovládání kufru

## Aktuální předání D163

Development OTA **0.1.160** s opravou přijetí WebSocket extension offers je
[publikovaná](https://github.com/Blato58/DildoVander/actions/runs/37929155208).
[Podpis 37929159117](https://github.com/Blato58/DildoVander/actions/runs/37929159117)
prošel native pinned/debug/BLE harnessy i archive/export pro **1.0 / build 1000.0.21**.
[Safari publikace 37929535139](https://github.com/Blato58/DildoVander/actions/runs/37929535139)
prošla; [instalační stránka](https://2.28.202.220/ios/) nabízí tento build.
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
se automaticky neopakují a mohou vyžadovat fyzickou servisní obnovu.
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
[BLE návodu](../../docs/ios-ble-provisioning.md).

## Autonomní Live a playback checkpoint (D143)

Katalog a výběr personality dál načítá dosavadní Debug → Server. Novou
autonomní větev aktivuje backendová konfigurace, nikoli nový seznam
postav v telefonu. Swift Live transport přijme `playback_checkpoint`,
ověří přesný počet PCM bajtů a potvrdí jej až po skutečném
`dataPlayedBack` drain. Reader při tom dál běží a GPT-Live zachovává
současný capture/playback s původním Apple voice processing. Starý
klient bez checkpointu patří k legacy režimu. Nový nativní fixture
obsahuje explicitní drain i ztracený ACK; jeho běh vyžaduje macOS.

[D143 návod](../docs/autonomous-live-memory.md) rozlišuje konfiguraci,
lokální testy a dosud neprovedený poslech/iPhone/HW. Tato změna sama
nepublikuje novou IPA, nepřepíná server a nedokládá změřené AEC.

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

## Debug D108/D109/D110/D112 a Realtime

**Historické předání D162:** [Safari instalace](https://2.28.202.220/ios/) nabízela verzi 1.0,
build **1000.0.20** ([podpis 9. 10. 2026](https://github.com/Blato58/DildoVander/actions/runs/37923948887),
[publikace](https://github.com/Blato58/DildoVander/actions/runs/37924314161)).
D161 přidává tlačítko Wi-Fi párování přes ověřené BLE a nativní Keychain;
nativní harnessy prošly v podpisovém workflow. Odpovídající development
OTA firmware je0.1.157. Majitel po publikaci potvrdil scan a párování;
na stejné Wi-Fi v Ovládání selhává LAN spojení a opakuje připojování.
Přesné instalované verze nejsou doložené; WSS a Keychain retention zůstávají otevřené.
D158 přidává nativní bezpečný BLE provisioning/hotspot/fallback a OTA ovládání;
GPT-Live a stávající hlas zůstávají zachované. Nativní testy, skutečný Xcode
archive/export, Ad Hoc podpis a veřejná kontrola manifestu/IPA/SHA-256 prošly.
D159 ověřilo skutečné BLE/PoP a domácí Wi-Fi/status backendu na iPhonu.
Řízená aktualizace aplikace s Keychain retention, hotspot/failover, LAN
párování/lifecycle a poslech zůstávají neověřené.
První uložení credentialu po přechodu z RAM-only verze
je potřeba zopakovat. Profil uložený od 1000.0.2 se dál načítá ze stejného
Keychain; skutečnou aktualizaci a její zachování ověří registrovaný iPhone.

Samostatný podpisový workflow čísluje nové IPA `1000.x.y` (vyšší než původní
build 37); stejné Bundle ID a tým umožňují aktualizaci s uloženým profilem.

Sekce **Večery a paměť rozhovorů** načte pojmenované večery samostatné debug
identity. **Založit a aktivovat nový večer** začne s prázdnou pamětí a zachová
staré rozhovory. Vyberte uložený večer a stiskněte **Přepnout aktivní večer**
pro návrat k jeho paměti. Seznam obsahuje jen názvy, datum a počty výměn.
Přepínání je možné mezi rozhovory; při souběžné změně obnovte seznam.
Platí nejvýše 100 večerů, název 1–80 Unicode znaků. Samotná správa nevolá AI.

**Zkušební rozhovor a výběr hůlky** drží samostatné virtuální sezení.
Nastavte dostupné přihrádky (výchozí všech šest), hlas Cedar/Marin/Onyx/Coral
a tempo, obnovte režimy serveru a spusťte AI nebo fake rozhovor. Odpovídejte
ve výchozím **Realtime · bez tlačítek** prostě nahlas: mikrofon vyžaduje
povolení iOS a během poslechu průběžně odesílá PCM16 mono 24 kHz backendu.
Upload používá jedno síťové sezení pro celý rozhovor a přesné 200ms dávky;
hardwarové mikrofonní bloky se průběžně skládají bez změny pořadí. Fronta
zůstává omezená na 1 s. Kód `realtime_live_audio_overflow` označuje zaplnění
této fronty, sám nedokazuje nestabilní připojení. Pozdní blok z předchozího
poslechu se zahodí a neoznačí jako další odpověď. D137 navíc přiřazuje
asynchronní overflow chybu k epoše vstupu: po zavření mikrofonu nebo novém
poslechu stará notifikace nesmí ukončit závěr ani další rozhovor. Aktuální
nezvládnutý backlog stále končí chybou; limit ani pořadí audia se nemění.
Tato nativní ochrana vyžaduje novou IPA. Backendové dřívější zavření
mikrofonu na závěru GPT-Live funguje i se stávající aplikací.
Po 650 ms ticha odpoví kufr, během jeho řeči je vstup zavřený a po skutečném
dohrání se automaticky otevře další poslech. Telefon začne přehrávat první
bloky před dokončením odpovědi. Hands-free audio používá běžný režim iOS
s výchozím výstupem do reproduktoru; telefonní režim `voiceChat` mohl výstup
zeslabovat. Hlasitost nastavujte bočními tlačítky při přehrávání odpovědi.
Spouští se jeden rozhovor, nejvýše 8 min/10
tahů; Stop, pozadí, zamčení nebo audio přerušení ukončí mikrofon i provider.
Bez přerušování právě mluvícího kufru. Vlastní přepnutí audio session při
startu nepřerušuje rozhovor, pokud běžící engine, aktivní cesta a formát
zůstávají stejné. To platí i pro oznámení nově dostupného zařízení při
startu GPT-Live. Před otevřením AI streamu aplikace čeká na živé mikrofonní
buffery a 300 ms stabilního běhu. Engine zastavený během počáteční
konfigurace obnoví nejvýše třikrát do 3 s při nezměněné zvukové cestě.
Po zahájení hovoru se zastavený engine automaticky neobnovuje.
Odpojení zařízení nebo skutečná změna konfigurace hovor
bezpečně ukončí. Debug rozlišuje kódy změny cesty, režimu, PCM formátu,
zastaveného enginu, přerušení a resetu zvukové služby. Prázdný výstup při rozběhu převodníku není chyba.
Problém včetně nákladového limitu má viditelnou hlášku a technický kód
pro předání při diagnostice. **Realtime · tlačítka** a **Řetězený** umožňují text
nebo nahrávání nejvýše 30 s do RAM. Fake STT vrací syntetický přepis;
skutečná vlastní hlasová odpověď potřebuje OpenAI režim hlasu.
Volba Řetězený zachovává původní Sol/fake tok; Realtime nepodporuje hlas
Onyx. V Debug se ukazují zvlášť čas do prvního audia a celkový čas tahu. Celý přepis je přechodný a nepatří do logu. Backend může
text zachovat v paměti testovacího večera; Ukončit a smazat rozhovor maže pouze
přepis telefonu a ukončuje sezení, nikoli paměť večera na serveru.

Po čtyřech otázkách fake `debug-server-admin` navrhne dostupnou hůlku;
AI ji vybírá z odpovědí. Maska i návrh jsou simulované a nespouští pohon.
Časy posledního tahu rozlišují dialog, TTS a stažení. WAV se přijímá po blocích,
přehrává se před úklidem serverové audio operace. Skutečné TTS stále čeká na
celý provider výstup. Výběr hlasu a tempo platí pro interaktivní rozhovor;
původní úvodní test zachovává marin/1.0. Kvalitu, latenci a permission dialog
musí potvrdit registrovaný iPhone; syntetický browser bridge je nenahrazuje.

Záložka **Debug** je dostupná vedle Ovládání. Ukazuje zdroj spojení, skutečnou
fázi přijatého snapshotu, masku, revizi, eventSequence, boot ID, uptime vzorku,
poslední zprávu a naměřené heartbeat RTT. Při odpojení telemetrie zmizí;
web fake a hostitelský simulátor jsou označené. Pouhé zobrazení nepřebírá lease.
Stop zůstává dostupný také v Debug. Log má nejvýše 200 událostí, filtr,
pozastavení zobrazení (sběr pokračuje), smazání a JSON export; na iOS se otevře
nativní sdílení textu JSON. Zaznamenává pouze stavové metadata a pevné kódy akcí,
nikoli syrové rámce, rozhovory, credential, párovací tajemství ani audio.
Není to sériový log firmware.

Sekce **Server** (D109) používá stejný nativní profil se samostatným credential
správce a ID `debug-server-admin`. **Obnovit stav serveru** čte autorizované
`GET /v1/admin/modes`; volby Dialog Fake / AI Sol a Hlas Fake / AI STT-TTS
se odešlou tlačítkem **Použít režimy serveru**. Změna platí pro celý backend
do jeho restartu, kdy se obnoví konfigurace nasazení. Během rozhovoru je změna
odmítnuta; zastavte/ukončete jeho vlastní sezení a znovu obnovte stav.
Revize brání přepsání změny z jiného telefonu. Při ztrátě odpovědi se režim
nepředpokládá a je nutné ho obnovit. Nedostupný provider nelze zapnout.
Běžný device credential ani access token nesmí spravovat server. Samotné
přepnutí nespouští AI a neovládá žádný pohon. Web nabízí pouze označený harness.

Hlasové testy fungují v nativní iOS aplikaci:

1. **Test reproduktoru telefonu** použije český systémový hlas bez sítě.
2. **Nastavit serverový / testovací přístup** otevře nativní secure dialog: HTTPS kořenová
   adresa, předem registrované samostatné device ID `debug-*` a jeho backend
   credential. Výchozí URL je `https://2.28.202.220/` a ID `debug-server-admin`.
   Lokální WSS bearer se nikdy nepoužívá. D112 ukládá profil do samostatného
   nesynchronizovaného Keychain přístupného při odemčení; stejná podpisová
   identita ho zachová při aktualizaci. V nové verzi credential jednou uložte:
   starší verze držela přístup jen v RAM. Dialog předvyplní poslední URL/ID;
   prázdný credential zachová uložený jen pro stejný server a identitu.
   **Zapomenout uložený přístup** smaže Debug profil, nikoli ESP párování.
3. **Ověřit backend** vymění credential za access token a čte
   `GET /v1/diagnostics`, bez volání AI. Starší nasazený backend bez D108 vrací
   chybu; musí se nejprve aktualizovat. Nasazení a novou IPA D109 eviduje stav
   implementace; podpis/publicace se automaticky nespouštějí každým push.
4. **Test AI hlasu** se zpřístupní pouze pro `voiceMode=openai`. Použije stávající
   sezení/opening/speech/poll/audio API, consent audio, readyMask 0 a výstup
   `phone`. Backend zachovává svůj AI limit a trvalou paměť testovacího večera.
   **Test fake audio toku** vyžaduje fake dialog i hlas a přehraje tiché WAV bez
   volání provideru. Režimy se ověří před create i po něm, kdy už jsou pro
   existující rozhovor neměnné. AI test může čerpat serverové limity.
5. **Zastavit hlas**, pozadí, uzamčení nebo audio přerušení zruší práci a playback.
   Opožděná odpověď nesmí začít hrát. Aplikace uklízí jen své získané sezení,
   nesmaže večerní paměť ani nepřebere cizí aktivní sezení. Create a cleanup mají
   jedno opakování se stejným klíčem; při nevyřešené ztrátě odpovědi/úklidu
   může být další test odmítnut 409 až do expirace.

Vstupní WAV je omezen na 30 s, PCM16 mono 32 kHz, standardní 44B hlavičku a žije pouze
v RAM. HTTPS používá systémové PKI, nepovoluje redirect ani ATS výjimku.
JavaScript spouští akce a čte jen režim/fázi; nečte credential ani audio.
Tento test nepřehrává přes ESP32/I2S a neověřuje B1 ani nativní iPhone akceptaci.
`bash scripts/test-native-debug.sh` na macOS ověřuje Keychain mezi procesy,
bezpečné ponechání credentialu, režim, celý výběr/STT, večery a retry,
korelaci, WAV, zrušení a cleanup bez placeného provideru. Syntetický HTTP
adaptér doplňuje skutečný loopback test omezeného příjmu a redirect/cancel.
Stejný harness ověřuje NDJSON Realtime parser s prvním PCM blokem před `completed`,
chybnou korelací a limitem dekódovaného audio bloku.
Hands-free loopback harness ověřuje úvod a dvě hlasové odpovědi v jednom
streamu, obnovení vstupu, konec a cleanup. Záměrně pošle další `listening`
před HTTP odpovědí `/played`. AVAudioEngine a skutečný mikrofon musí
ověřit Xcode/iPhone; Foundation harness je nenahrazuje.
Samostatný upload harness navíc zachytává 50ms bloky při zpožděných HTTP
odpovědích, ověřuje skutečné 200ms PCM dávky/pořadí a opětovné použití
jednoho TCP spojení. Pokrývá limit fronty, pozdní conversion po změně
poslechu, odmítnutí redirectu a zrušení rozpracovaného požadavku.

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
nové párování vyžaduje fyzickou servisní obnovu. Totéž platí při ztrátě úspěšné
párovací odpovědi nebo selhání Keychain zápisu po spotřebování tajemství.
Neověřená/prázdná NVS nebo kalibrace 0 na desce se z telefonu neobchází.
Živý ESP32, LAN permission, Keychain po aktualizaci a uspání iPhonu zůstávají
samostatnou akceptací B1/E10; nové IPA se tímto slice nepodepisuje/nepublikuje.

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

Ve složce `kufr/mobilni-aplikace` použijte Node.js 24.21.0 z `.nvmrc`:

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

Build provádí `tsc --noEmit && vite build`; test kontroluje nové lease, opakování totožného příkazu, chybějící výsledek a rekonciliaci po odpojení. Ani jedna kontrola neověřuje iOS podpis, LAN nebo fyzický kufr. Ve webovém fake režimu lze převzít řízení, potvrdit výběr nebo založení 1–6 a vyzkoušet Stop. Ztráta viditelnosti stránky uvolní fake lease; rozběhnutý výběr skončí jako nejistý a vyžaduje výslovnou simulovanou kontrolu. Stav se neukládá přes reload stránky.

TypeScript 7 poskytuje CLI pro typovou kontrolu; testovací moduly převádí
Node.js `stripTypeScriptTypes` v režimu `transform`, který podporuje také
parametrové vlastnosti konstruktorů. Node zatím vypisuje upozornění na
experimentální API. Deklarace `vite/client` pokrývají import CSS a assetů.

Pro E2 simulátor nejprve sestavte mobilní web pomocí `npm run build`, spusťte E2 WSS server a pak místní most podle [návodu simulátoru](../simulator/README.md). Stránka na `http://127.0.0.1:5174` vybere simulátor podle odpovědi `/dev-mode`; Vite stránka zůstává na fake adaptéru. Web nevidí bearer token ani certifikát upstreamu. Po ztrátě spojení zablokuje povely a ukáže nedostupný stav; nejasný výsledek předchozího výběru vyžaduje kontrolu mimo UI. Snapshot a ACK simulátoru nejsou důkaz fyzického otevření.

Horní přepínače Ovládání/Debug respektují iOS safe area. Viewport má
`viewport-fit=cover`; sticky lišta přidává `safe-area-inset-top` k hornímu
odsazení a boční insets k minimálním okrajům. Hodnoty dodává WebKit podle
zařízení a orientace, takže výřez nepřekrývá taby ani po scrollování.
Ve webovém režimu s nulovými insets zůstává původní odsazení.

## Etapy a vzdálený iOS build

E3a má zdrojový projekt `ios/App/App.xcodeproj`, sdílené scheme `App` a [webový CI workflow](../../.github/workflows/kufr-web.yml). Capacitor `@capacitor/core`, CLI a iOS jsou připnuté na 8.5.2. Po změně webu lze na Macu synchronizovat balíček příkazy:

```text
npm ci
npm ci --prefix ../contracts
npm run build
npx cap sync ios
```

CLI používá `xcode`, jehož závislost `uuid` je přes cílené npm override
připnutá na 11.1.1 kvůli GHSA-w5hq-g745-h8pq. Tato verze zachovává CommonJS
API `v4`, které parser projektu používá; uuid 12 už CommonJS neposkytuje.
Swift `Package.swift` generuje `cap sync ios`; `Package.resolved` pochází
ze skutečného `xcodebuild -resolvePackageDependencies` na macOS.

Na Windows lze tyto příkazy použít pro kontrolu zdrojů. Každé PR spouští
přes `ci.yml` mobilní testy a webový build na Linuxu; nativní iOS build ani
iOS Simulator se v PR nebo obnovovacím CI nespouští. Web se buildí jednou
a ruční macOS build převezme jeho artefakt. Po merge ruční workflow
**Build signed Kufr IPA** (`kufr-ios-sign.yml`) na `main` dostane `build_run_id`
úspěšného CI, ověří Xcode 26.3 a nativní transport/debug testy a sestaví
podepsaný device cíl v environmentu `ios-signing`. Používá výhradně Ad Hoc
distribuční profil pro nasazení; artefakt má retenci 14 dní. Samostatné
**Deploy Kufr IPA** (`kufr-ios-deploy.yml`) dostane
ID úspěšného podepsaného buildu a publikuje instalační stránku na
`https://2.28.202.220/ios/` bez dalšího buildu. Oba kroky ověřují shodu zdrojového
stromu s aktuálním `main`; [CI/CD návod](../../docs/ci-cd.md) popisuje obnovovací build.
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

**Zkušební rozhovor → Otestovat personalitu · AI / fake** ukazuje aktivní
jméno a používá stávající hlasový tok. Změnu personality nejprve použijte
v nastavení serveru. Fake test má pro nový kufr dvě otázky a pro původní
čtyři; neověřuje humor ani nesyntetizuje skutečný hlas. AI používá stejný
vybraný prompt v Solu, Realtime i GPT-Live a dosavadní technické meze.
Softwarové peněžní limity byly v D157 zrušené; náklady řídí OpenAI účet.
[Inventář, přesné zdroje a postup](../ai-eval/personalities.md).
K použití na telefonu je nutná nová IPA a backend s D142; webový harness
ani offline provider testy nejsou fyzický poslech.

## D146: historický doklad publikace

Dne 6. 10. 2026 byla na [instalační stránce](https://2.28.202.220/ios/)
publikována Ad Hoc **1.0 / build 1000.0.16** pro dosavadní registrovaný iPhone.
Tento oddíl zachycuje tehdejší verzi a serverovou konfiguraci D146;
historické předání D162 je **1.0 / build 1000.0.20**; aktuální D163 vede přehled nahoře.
[Podpis 37429759564](https://github.com/Blato58/DildoVander/actions/runs/37429759564)
ověřil Xcode 26.3, nativní transport/debug/audio testy a podpis;
[publikace 37429982348](https://github.com/Blato58/DildoVander/actions/runs/37429982348)
ověřila manifest, MIME a checksum stažené IPA přes HTTPS.
Aktualizaci spusťte v Safari; instalace této verze a živý poslech ještě
nejsou ověřené. Stávající Bundle ID zachovává identitu aplikace a Keychain.

Tato verze umí D143 `playback_checkpoint`: potvrzuje přesný PCM drain
po skutečných `dataPlayedBack` callbacks, idempotentně opakuje ztracený ACK
a při poslechu i řeči zachovává full duplex. Nativní fixture je zelený;
neprokazuje fyzickou slyšitelnost nebo změřené AEC na iPhonu. Backend po
D145 stále používá `LegacyBackendScripted`; autonomní Live adaptace se
zapíná serverovou konfigurací, nikoli samotnou instalací nové IPA.
[Konfigurace a A/B postup](../docs/autonomous-live-memory.md).

## D128: porovnání GPT-Live + Sol

Debug nabízí vedle Realtime a řetězeného hlasu **GPT-Live + Sol**.
Používá mikrofon a reproduktor telefonu s Apple voice processing; rychlost
je 1×. Režim přepínej před zahájením nového rozhovoru. **Hlas kufru** má
stejnou novou volbu a posílá `voice.configure mode:gpt-live` přímo do ESP.
Nastavení kufru je do restartu; během probíhajícího hovoru ho nelze měnit.
Aplikace ani server neomezují peněžní rozpočet sezení. Náklady, kredit a limity dostupnosti řídí OpenAI účet; technické meze audia a délky tahu zůstávají.
Nová funkce vyžaduje nasazený D128 backend a novou aplikaci/firmware;
webový build sám neověřuje nativní iPhone audio.
[Postup a omezení testování](../docs/hlas-a-humor-research.md#d128-testovaci-gpt-live-rezim).

### Zvuk při hands-free testu

Debug při hovoru ukazuje výstup (reproduktor, telefonní sluchátko nebo
externí zařízení), aktuální systémovou hlasitost a délku přijatého audia
oproti bufferům potvrzeným přehrávačem. „Přehrávám“ samotné znamená příjem
audia, nikoli ověřenou slyšitelnost. Hlasitost zvyšte bočním tlačítkem během
hovoru; hlasový režim může mít jinou úroveň než běžná média. Při startupu
aplikace převádí vestavěné sluchátko na reproduktor a respektuje externí
výstup. Žádné audio se kvůli těmto údajům neukládá.

D134 ověřuje před otevřením AI streamu také skutečný průchod tichého 200ms
bufferu přehrávačem. Při startu/restartu obnoví výstupní spojení v aktuálním
formátu a resetuje clock přehrávače. Vstup s běžícím enginem sám o sobě
nestačí. Za hovoru ukončí frontu bez potvrzeného přehrávání po 2,5 s kódem
`realtime_live_audio_output_stalled`; příchozí audio tento timeout neoddaluje.
Test grafu používá macOS offline render, takže fyzický poslech na iOS je
stále samostatná akceptace.


### Volitelná fotka kostýmu

Debug nabízí **Se souhlasem vyfotit kostým** před každým ze čtyř režimů.
iOS požádá o fotoaparát teprve při této akci. Systémová kamera umožňuje
náhled, opakování, Použít fotku a Zrušit. Schválená fotka se překreslí na
nejvýše 640×640, převede na JPEG do 256 KiB a zbaví APP1–15/COM metadat.
Zůstává v RAM; do webu přejde jen jednorázové UUID platné 120 sekund.
Spotřebování, zrušení, Stop, pozadí nebo expirace ji odstraní. Fotka ani
odhad vzhledu nepoužívají galerii, soubory, Keychain ani localStorage.

Po vytvoření rozhovoru s explicitním `consent.image:true` se odešle JPEG
na `/image`, vyčká na výsledek a případně předá ruční popis na `/costume-context`.
Popis má přednost a funguje i bez fotky. Poté začne úvod/hlas. Chyba fotky
ukáže varování. Nejprve se potvrdí úklid starého sezení (včetně rozpracovaného image
provideru), pak vznikne nové sezení bez souhlasu k fotce a použije se případný
ruční popis. Neúspěšný nebo nepotvrzený úklid nový hlas nespustí; upload se neopakuje.
Klient si v RAM ponechá vlastněné ID i stejný idempotency klíč DELETE až do
potvrzeného 204. Neúspěšný úklid zablokuje Start a ponechá Stop dostupný pro
opakování, i když příprava ještě nevrátila objekt sezení. Po potvrzení úklidu
se záznam s přístupovými údaji odstraní. Již potvrzený úklid se neposílá znovu.
Odmítne-li server token při úklidu jako neplatný (401), klient obnoví device
access a zopakuje stejné DELETE ID a klíč. Selhání obnovy zachová čekající
úklid pro další Stop; úspěšně obnovený token zůstane v RAM do potvrzeného 204.
Ztracené či neplatné potvrzení ručního popisu se jednou obnoví přes stejný
klíč, payload a korelaci PUT. Opakovaný nejednoznačný výsledek zobrazí chybu,
zablokuje tento začátek hlasu a uklidí vlastněné sezení; netvrdí, že popis
nebyl uložen. Tato obnova neopakuje fotku ani provider práci.
Stop během přípravy zruší práci a smaže právě vytvořené sezení, takže
opožděný výsledek nespustí hlas. Odhad je nejistá nápověda s TTL 120 sekund,
není identifikací osoby. Běžný přepis se dále uchovává v paměti večera;
surová fotka ani strukturovaný odhad do ní nevstupují. Již použitá nápověda
může zůstat ve stávajícím provider spojení do jeho uzavření.

**Hlas kufru** má samostatný nezaškrtnutý souhlas k jedné fotce z kamery kufru,
povolený jen pro `cameraAvailable:true`. Po každém Start a při pozadí se
souhlas resetuje. Bez souhlasu se posílá původní `voice.start` bez dalšího pole.
Foto fáze a varování se ukazují odděleně od hlasové chyby; selhání fotky
nezastaví dialog. Vývojový web reálnou fotku nepořizuje. Syntetický nativní
harness `tests/native-ui-harness.html` nabízí schválení/zrušení/permission/chybu/čekání.
`./scripts/test-native-debug.sh` na macOS testuje HTTP pořadí a cleanup offline.
Systémový náhled, práva kamery a nativní audio vyžadují podepsaný iPhone build
přes přijaté workflow a fyzickou akceptaci.
