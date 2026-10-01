# Mobilní ovládání kufru

Současný kód nabízí obrazovku se šesti přihrádkami, ručním převzetím, potvrzením výběru/založení a Stop. Přes Vite používá místní fake stav; přes E3.2a vývojový most komunikuje s E2 C++ simulátorem. Oba vývojové režimy jsou výslovně označené. E10.1 přidává nativní iOS párování a přímý místní WSS transport se SPKI pinem a Keychain; jeho fyzická akceptace ještě neproběhla. Zvolenou architekturu určuje [implementační plán](../docs/implementacni-plan.md), skutečný postup a předání [stav implementace](../docs/stav-implementace.md).

## Cílová role aplikace

První platforma je **iOS/iPhone**: zachovat TypeScript/Vite UI a doplnit Capacitor se Swift adaptéry. Zdrojové soubory vyvíjet na Windows, nativní build/test/podpis provádět vzdáleně na macOS GitHub Actions. Webový režim slouží pro vývoj a simulátor, ne jako finální produkční ovladač.

ESP32 samo komunikuje s backendem; aplikace není cloudový prostředník a nemá AI provider klíč. Místní WSS nabízí ruční výběr 1–6, potvrzení/Stop, snapshot, opravu AI návrhu, text a případně telefonní mikrofon přes stejné device sezení. Offline ovládání musí fungovat bez WAN. Připojení je nejprve pozorovatelské; explicitní převzetí vytvoří ruční lease. Odchod do pozadí nebo ztráta lease zastaví ruční řízení, odpojení pozorovatele neovlivní samostatný kufr.

Nativní adaptér vlastní TLS pin, oprávnění LAN a Keychain. E10.1 používá ručně zadané údaje z vnitřního QR; E10.2 přidává text/cancel/result a E10.3 Bonjour hledání i ruční změnu adresy. Audio navazuje po E9. UI nesmí přímo časovat motory ani přijímat AI text jako povel. Konkrétní slices a kontrakty jsou v kapitolách 3–5 plánu.

## Debug D108/D109/D110/D112 a Realtime

Aktuální [Safari instalace](https://2.28.202.220/ios/) nabízí verzi 1.0,
build **1000.0.5** (publikace 1. 10. 2026, D117: oprava startu Realtime audia).
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
Po 650 ms ticha odpoví kufr, během jeho řeči je vstup zavřený a po skutečném
dohrání se automaticky otevře další poslech. Telefon začne přehrávat první
bloky před dokončením odpovědi. Spouští se jeden rozhovor, nejvýše 8 min/10
tahů; Stop, pozadí, zamčení nebo audio přerušení ukončí mikrofon i provider.
Bez přerušování právě mluvícího kufru. Vlastní přepnutí audio session při
startu nepřerušuje rozhovor; skutečná změna mikrofonu/reproduktoru jej
bezpečně ukončí. Prázdný výstup při rozběhu převodníku není chyba.
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

Přerušené nativní spojení za běhu opakuje připojení s 0,5–8s backoffem a
jitterem; po welcome zůstane v pozorování. Výslovné odpojení retry zruší.
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

E3a má zdrojový projekt `ios/App/App.xcodeproj`, sdílené scheme `App` a [CI workflow](../../.github/workflows/kufr-ios.yml). Capacitor `@capacitor/core`, CLI a iOS jsou připnuté na 8.5.2. Po změně webu lze na Macu synchronizovat balíček příkazy:

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

Na Windows lze tyto příkazy použít pro kontrolu zdrojů; Xcode build a iOS Simulator
provádí macOS CI. Každé PR spouští přes `ci.yml` web a nepodepsaný simulator.
Web se buildí jednou a macOS převezme jeho artefakt. Po merge ruční workflow
**Build signed Kufr IPA** (`kufr-ios-sign.yml`) na `main` dostane `build_run_id`
úspěšného CI a sestaví podepsaný device cíl v environmentu `ios-signing`.
Volba `signing_kind=development` vytvoří 14denní artefakt; `ad_hoc` použije
distribuční profil. Samostatné **Deploy Kufr IPA** (`kufr-ios-deploy.yml`) dostane
ID úspěšného podepsaného buildu a publikuje instalační stránku na
`https://2.28.202.220/ios/` bez dalšího buildu. Oba kroky ověřují shodu zdrojového
stromu s aktuálním `main`; [CI/CD návod](../../docs/ci-cd.md) popisuje obnovovací build.
 Do environmentu patří secrets `IOS_BUILD_CERTIFICATE_BASE64`, `IOS_PROVISION_PROFILE_BASE64`, `IOS_DISTRIBUTION_CERTIFICATE_BASE64`, `IOS_DISTRIBUTION_PROFILE_BASE64`, `IOS_P12_PASSWORD` a `IOS_KEYCHAIN_PASSWORD`. Současné dvě `.p12` podle majitele používají stejné heslo; CI jej neukládá do artifactu. Certifikáty a profily jsou Base64 celého souboru; žádný ze souborů ani hesel nepatří do repozitáře. Environment může mít vlastní schvalovací pravidla.

Nativní identita má Bundle ID `app.turquoise6409.green2444` a Team ID `3QRXV8G5Y3`. Majitel potvrdil iPhone 15 Pro s iOS 26.6.1 a jeho UDID v dodaném profilu. Vývojový profil expiruje 25. 12. 2026; Ad Hoc profil pro stejný tým, Bundle ID a jedno zařízení expiruje 24. 12. 2026. CI kontroluje shodu identity, platnost a certifikát, exportuje IPA a ověřuje její podpis a embedded profil. Při chybě importu `.p12` zkusí OpenSSL čtení a kompatibilní přebalení. Publikační job kontroluje SHA-256, Bundle ID a metadata IPA, vytvoří manifest a z externího runneru znovu stáhne stejné soubory přes HTTPS. [Ad Hoc běh 36636991415](https://github.com/Blato58/DildoVander/actions/runs/36636991415) prošel všemi joby a majitel 30. 9. 2026 potvrdil úspěšnou instalaci a spuštění na iPhonu. Stabilní identita je důležitá pro aktualizaci a Keychain. E10.1–3 doplňují native TLS/Keychain, text a Bonjour a fyzický běh těchto funkcí ani aktualizace přes Safari zatím nejsou ověřené. GitHub Release, Pages, feed a ntfy zde nejsou zapnuté.
