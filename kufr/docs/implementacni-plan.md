# Implementační plán AI kufru

> **D160, 9. 10. 2026: uchování BLE PoP na žádost majitele.**
> Po úspěšném Security1 ověření ukládá iOS PoP nativně do odděleného
> Keychain pro konkrétní BLE název kufru, WhenUnlockedThisDeviceOnly,
> bez synchronizace. Wi-Fi hesla se v telefonu neukládají. Nové ověření
> nabídne použití uloženého PoP nebo zadání nového; uložený údaj se mění
> až po úspěšném ověření. Dosavadní uživatelé jej uloží prvním novým
> úspěšným spojením. BLE provisioning sítě nenahrazuje provisioning
> místní TLS identity a párování nutných pro LAN/WSS discovery.

> **D158, 9. 10. 2026: BLE síťové nastavení, hotspot a podepsané OTA.**
> Výslovné zadání majitele rozšiřuje mobilní aplikaci o nativní CoreBluetooth
> přes oficiální ESPProvision 3.2.0 a ESP-IDF protocomm Security1 s unikátním
> PoP. BLE slouží nastavení a diagnostice; místní WSS/TLS ruční ovládání
> i samostatný HTTPS backend klient zůstávají. Níže uvedená historická
> preference BLE nepřidávat a návrh servisního SoftAP nejsou aktuální cestou
> změny Wi-Fi: současný firmware používá omezené autentizované BLE okno.
> Síťová priorita je hotspot iPhonu → volitelná záložní Wi-Fi; kandidátní
> připojení se ověřuje před uložením pod společnou rezervací klidu s OTA a SD.
> Aktualizace používají původní dva 0x640000 app sloty, IDF rollback,
> HTTPS a ECDSA P-256 podepsaný manifest z existujícího backendu.
> NVS se nemaže; rozložení oddílů a piny se nemění. SD asset worker je volitelný, read-only,
> gated měřeným `sd_verified`; camera/audio kontrakty zůstávají.
> [Audit](../../docs/software-completion-audit.md),
> [BLE](../../docs/ios-ble-provisioning.md),
> [OTA](../../docs/firmware-ota.md) a
> [akceptační matice](../../docs/event-readiness.md) určují nový rozsah.
> Dokončení E11/E13 stále vyžaduje skutečný iPhone/ESP a fyzické měření.


> **D157, 9. 10. 2026: obrazové rozšíření na přání majitele.**
> Softwarová větev E12/E13 postupuje v pořadí backend → iOS → firmware.
> Jednorázový JPEG se samostatným souhlasem slouží opatrnému popisu
> viditelného kostýmu a možné fiktivní postavy, s ruční opravou.
> Rozpoznávání biometrické identity, věku a pohlaví se nepřidává.
> Fotografie jde do Responses přes oficiální SDK; do hlasového dialogu
> a rozhodování se předává omezený textový kontext. Obraz ani jeho
> výsledek nejsou fyzickým povelem. Software lze ověřit bez desky;
> úplná akceptace E12/E13 nadále vyžaduje původní provozní a HW kritéria.
> Před implementací firmwaru proběhl nový `git pull --ff-only origin main`:
> checkout převzal D156/PR138 (`3d0e73a`) a B-effects s volnými kamerovými piny.
> Majitel současně ruší všechny softwarové peněžní limity. Staré
> session/daily/monthly capy se nepoužívají ani jako podmínka dostupnosti
> AI; účetní odhady a idempotence zůstávají. Finanční nastavení vlastní
> OpenAI účet. Velikosti dat, čas, počet tahů a souběh zůstávají
> technickými limity. Toto rozhodnutí nahrazuje starší peněžní stropy níže.
> Majitel dále odstranil Arduino sériový profil kufru. Jediný podporovaný
> runtime a sestavovací profil je ESP-IDF; původní Arduino prototyp je historie.

> **D151, 7. 10. 2026: hranice Live událostí a diagnostika přerušení.**
> Dva skutečné hovory skončily `ai_unavailable`, jiné dva výběr dokončily.
> Dosavadní log nezachoval výjimku; konkrétní příčinu těchto pádů nelze
> zpětně určit. Časová metadata přijímají konečné JSON číslo v mezích
> 0–200000 ms včetně desetinného zápisu a uchovávají přesnost i v SQLite.
> Strukturovaný rozhodovací požadavek zadává stejné délky citací/faktů
> a počet důvodů jako dosavadní validace. Nejasný výsledek dál nepustí.
> Technický `live_failure_detail` rozlišuje pevné kategorie bez provider
> zpráv, přepisů či credentialů; log zachytí také začátek a chybu delegace.
> Bez změny modelu, paměťových oprávnění, kontraktu a fyzické brány.

> **D150, 6. 10. 2026: automatický verdikt bez potvrzovací otázky.**
> Reálný iPhone Debug test D149 už zveřejnil návrh přihrádky 4; závěr
> přesto obsahoval otázku na hodnocení. Live politika v3 a pokyn k
> oznámení vyžadují celý verdikt pouze oznamovacími větami, také roast
> nebo callback. Po závěru mlčí. Výběr nečeká na souhlas ani odpověď;
> stávající validace dostupnosti, dobrovolné opravy, přednes a drain
> se zachovávají. Bez změny kontraktu, klientů či fyzické brány.
>
> **D149, 6. 10. 2026: dokončení Live výběru a prostor pro řeč hosta.**
> Reálný test D148 potvrdil rychlejší rozhovor, ale tři validní návrhy
> skončily bez playback ACK a bez publikování. Přepis nezaručuje spolehlivou
> závěrečnou interpunkci ani přesně nominativní katalogový název. D149
> identifikuje jedinečný titul ze stejného katalogu i v českém skloňování;
> výslovně nedokončený fragment a nejednoznačné jméno nepustí. Jedna krátká
> oprava oznámení vyžaduje nový skutečný přednes a lokální drain.
> Samostatná PCM RMS aktivita a `InputQuietMs=1200` chrání rozvíjenou
> odpověď, vrácený backendový výsledek i publikování. Stabilní přijetí či
> poděkování nemění výběrové podklady; oprava/alternativa jej zneplatní.
> Aktualizovaný neměnný prompt `autonomous-live-v2` výslovně předává hostovi
> celý tah. Model, index, session rozpočet a fyzická ESP brána zůstávají.
> Jde o heuristiky nad skutečným audiem/přepisem; poslech a HW akceptace
> vyžadují reálné ověření. `live_terminal_state` ukáže místo případné blokace.

> **D148, 6. 10. 2026: aktivace Vector Stores a kratší čekání na Live výběr.**
> Majitel výslovně požaduje aktivaci vektorové paměti a dovoluje jiný model
> nebo architekturu kvůli prodlevě výběru. Live vede rozhovor i závěr sám
> v `AutonomousActor`; úzká faktická delegace má samostatný
> `Backend:GptLive:DecisionModel` (`gpt-6-luna` nebo původní `gpt-6.1-sol`).
> Luna řeší pouze strukturovaný výběr ze skutečného katalogu, nikoli humor.
> Live client delegation nemá function arguments, takže pouhá vyslovená
> hůlka nemůže nahradit validovaný návrh pro zařízení. Zachovávají se přesné
> citace, revize, rozpočet, dostupnost a místní playback checkpoint.
> Aktivace používá existující stores, cloud indexing a aktualizované nativní
> klienty D146. Osobní paměť dál vyžaduje samostatné povolení/identitu.
> Nejde o migraci SDK, DB, firmware ani zdrojových personalit. Skutečný
> výsledek nasazení a ověření patří do stavu implementace D148.

> **D145–D146, 6. 10. 2026: skutečné nasazení a provisioning.**
> D143 backend je v produkci, dvě OpenAI stores byly vytvořené a ověřené;
> žádná data hostů nebyla exportovaná. Tehdejší Ad Hoc IPA 1.0/1000.0.16
> je podepsaná a publikovaná po macOS nativních testech a HTTPS kontrole.
> Server zůstává LegacyBackendScripted, hybrid memory=true, cloud indexing
> a long-term personal=false. Instalace této IPA, poslech a hardware nejsou
> tímto akceptované; [D145–D146](stav-implementace.md#předání-další-ai)
> uvádí skutečné běhy, konfiguraci a zbývající ověření. Architektura níže
> se samotnou publikací nemění. Jde o historický doklad D145–D146;
> aktuální publikace D158 je 1.0/1000.0.19 podle stavu implementace.

> **D143, 6. 10. 2026: opt-in autonomní GPT-Live a hybridní paměť.**
> `Backend:GptLive:ActorMode=AutonomousActor` odděluje hlasového herce od
> faktického výběru stávajícím Solem. Pozdrav, roast, otázka a běžná reakce
> nepotřebují backendový dialogový model. Výchozí `LegacyBackendScripted`,
> Sol/text a Realtime zůstávají porovnávací a kompatibilní větve.
> `ollivander-trunk` má dedikovanou Live adaptaci 2/3 odpovědi, party vlastní
> politiku 4–7; původní zdroje/hashy, katalog a uložená volba zůstávají.
> SQLite v EveningMemoryStore je autorita, skutečný OpenAI Vector Store
> odvozený index se dvěma scopes, trvalým outboxem a revalidací každého hitu.
> Cloudová a osobní paměť jsou výchozí vypnuté. Playback checkpoint vyžaduje
> skutečný místní drain, nikoli cloudový ACK; mechaniku dál vlastní ESP32.
> Toto upřesnění platí pro novou větev, starší popisy níže zachycují své
> původní režimy. [Konfigurace, provisioning, retence, rollback a A/B](autonomous-live-memory.md).
> Produkční aktivace, cloudové prostředky, placený poslech a HW jsou
> samostatná ověření; implementace je bez nich nezávisle testovatelná.

> **D128, 2. 10. 2026: volitelný GPT-Live + Sol a 3 USD/session.**
> Na výslovné zadání majitele přibývá přepínač `gpt-live` ve firmware/local
> control a mobilním Debug. Samostatné `/gpt-live` session používá `gpt-live-1`,
> klientské delegace do stávajícího Solu a výběr validovaný backendem i ESP.
> .NET OpenAI 2.14.0 dosud Live API neobsahuje; izolovaný WebSocket adaptér
> je zdůvodněná výjimka z SDK pravidla, podle oficiálního wire schématu.
> Telefon používá Apple voice processing; ESP bez ověřeného AEC posílá během
> přehrávání ticho. Nový režim je testovací, kvalita ani HW akceptace nejsou
> potvrzené. Limit všech sessions je 3 USD; denní/měsíční aplikované stropy
> se ruší, nadřazený rozpočet spravuje OpenAI. Protokolové, časové a fyzické
> bezpečnostní meze zůstávají. Podrobnosti a testovací postup jsou v
> [rešerši](hlas-a-humor-research.md#d128-testovaci-gpt-live-rezim).

> **D127, 2. 10. 2026: kvalita úvodu a humoru.** Opening je nový rozhovor
> bez odeslané paměti/historie a bez povinného dotazu na jméno. Backend
> od D141 neposkytuje pevné náměty ani ukázky; model tvoří vlastní otevřenou otázku. Další tahy znovu používají
> archiv. Sdílený prompt adaptuje CARLIN (konkrétní podklad, více úhlů, jedna
> zkrácená pointa). [Rešerše a akceptace](hlas-a-humor-research.md) doporučují
> porovnat opravený Realtime s GPT‑Live + Sol; migrace na GPT‑Live není
> součástí tohoto slice. Dosavadní transport, poloduplex a limity zůstávají.

> **Změna zadání 1. 10. 2026 (D116): hlas bez opakovaného mačkání.**
> Po instalaci D114 majitel žádá automatické střídání poslechu a řeči;
> druhá odpověď v PTT navíc zůstává bez výsledku. Výchozí iOS Debug drží
> jeden provider Realtime WebSocket po celý rozhovor. Telefon odesílá
> průběžné PCM16/24kHz bloky přes autentizované HTTPS a přijímá jeden
> dlouhý NDJSON stream. Server VAD ukončí odpověď po 650 ms ticha;
> backend před každým poslechem rezervuje náklad a sám spustí odpověď.
> Mikrofon se během řeči kufru zavře a po potvrzeném dohrání znovu otevře.
> Tento slice nepřidává přerušování řeči. PTT a řetězený tok jsou zálohy.
> Stop, pozadí, zamčení a odpojení ruší celé sezení včetně čekání na dohrání.
> Prompt zůstává podrobný; rezervace může zkrátit odpověď/context bez mazání
> paměti nebo zvýšení stropů. Chyby mají viditelný technický důvod.
> Majitel již schválil backend, novou IPA a živou zkoušku do 1 USD.
> Průběžný hlas je diagnostický tok telefonu, bezpečnost ESP32/B1 se nemění.

> **Změna zadání 1. 10. 2026 (D114): Realtime hlas.** Majitel doložil
> dialog 9,8 s + TTS 2,3 s a výslovně schválil GPT-live/Realtime místo
> sekvenční hlasové cesty. První software slice používá `gpt-realtime-1.5`
> přes oficiální OpenAI .NET SDK 2.14.0 na backendu a průběžný HTTPS NDJSON
> výstup do nativního iOS Debugu. PTT/poloduplex zůstává, VAD je vypnuté.
> Model přímo slyší audio; paralelní přepis ukládá backendová paměť večera.
> Prompt je záměrně podrobnější, se specifickými pravidly humoru a řeči.
> Textový Sol a oddělená STT/Sol/TTS cesta zůstávají kompatibilní.
> Realtime návrh 1–6 vyžaduje validovanou funkci a stávající korelaci,
> masku/expiraci; Debug nesmí spustit pohon. Backend stream nepotvrzuje
> ESP32 audio, HW ani iPhone akceptaci. Ty dál vyžadují E9/E10 a B1.
> Nové streamové rozhraní a rezervace jsou v OpenAPI/backend README;
> migrace nasazení vyžaduje ověření skutečných capů a novou IPA.

> **Změna zadání 30. 9. 2026 (D75): autonomní prezentace pevné rekvizity.** Majitel výslovně nahradil západku zdvihacím servem. Hůlka zůstává v přihrádce. AI sama vybere dostupnou přihrádku; běžný rozhovor nepotřebuje potvrzení obsluhou ani fyzickým tlačítkem. ESP32 ověří korelaci, expiraci, snapshot a místní bezpečný stav, provede omezenou sekvenci vysunout–předvést–zasunout a po úspěšném návratu obnoví dostupnost. Stop, porucha, reset či neznámá poloha dostupnost neobnovují. Vyžadují místní zotavení. Následující starší popisy západky, ručního reloadu a tlačítkového potvrzení popisují původní implementaci/manuální režim; nejsou podmínkou nové autonomní cesty. Ta nesmí předstírat ruční lease. Fyzické GPIO a kalibrace zůstávají podmíněné ověřením desky a bezpečnostní větve v E5.

Backend používá nativní Responses function calling: `continue_conversation`, `propose_wand`, `end_conversation`, strict schémata, jediná funkce na tah. Název `propose_wand` zachovává existující drátový kontrakt návrhu, který nová autonomní brána přijímá bez obsluhy. Model nikdy nedostává GPIO, úhly, výkon či libovolné časování. Framework typu Semantic Kernel by tuto hranici neposílil; tři úzké funkce mapujeme přímo do stávajícího backendu. Ukončovací replika musí zůstat dostupná pro poll; firmware až po převzetí ukončí sezení DELETE a obnoví klíč dalšího startu. Totéž provede při opuštění rozhovoru nebo timeoutu. Paměť večera se koncem sezení nemaže.


Stav návrhu: **29. 9. 2026**. Dokument navrhuje budoucí implementaci; žádnou z ní neprohlašuje za hotovou. Pinová přidělení jsou **předběžná**, nikoli změřené zapojení konkrétní desky. Před zapojením a nahráním musí projít kontrolou revize podle kapitoly 10.

Tento plán určuje cílovou architekturu a akceptační kritéria. Aktuální dokončení, rozpracovanost, ověření a konkrétní další krok vede [stav implementace](stav-implementace.md); postup další AI určuje [AGENTS.md](../../AGENTS.md). Plán nahrazuje dosavadní nezávazné softwarové představy, nikoli existující elektrická bezpečnostní omezení. Obludárium není součástí návrhu. Původní příprava plánu i navazující sjednocení dokumentace byly pouze dokumentační práce; nejsou dokončením implementačních etap.

## 1. Výchozí stav a cílový zážitek

Následující inventura zachycuje výchozí stav při návrhu 29. 9. 2026. Aktuální implementaci ověřuj podle stavového souboru a živého kódu; tento historický popis není překážkou zahájení dalších etap.

Přečtené místní podklady:

- Kořenový `AGENTS.md`, pokyny přiložené k zadání, kořenový `README.md` a `kufr/README.md`.
- `kufr/docs/efekty-a-napajeni.md`, `kamera.md`, `pin-mapa-esp32-s3.md` a samotný obrázek `pin-map-esp32-s3.png`.
- `kufr/firmware/README.md`, `platformio.ini`, `src/main.cpp`, všechny čtyři soubory `wand_selection` a `tests/selection_sequence_test.cpp`.
- `kufr/mobilni-aplikace/README.md`, `package.json`, relevantní uzamčené závislosti v `package-lock.json`, `tsconfig.json`, `index.html`, `src/main.ts` a `src/style.css`.
- `docs/elektronika.md` kvůli konkrétním periferiím a rozpočtu; `.gitignore` kvůli hranicím zdrojových a generovaných souborů.

Firmware je PlatformIO `espressif32@7.1.3`, Arduino, předběžný profil `esp32-s3-devkitc-1`, QIO flash / OPI PSRAM, 16 MB / 8 MB. Profil není důkazem skutečné paměti. `SelectionSequence` je čisté C++: hledání 6 × 100 ms, zvýraznění 400 ms, tři pulzy motoru 150 ms s mezerami 150 ms, uvolnění 400 ms, celkem přibližně 2,3 s. Odmítá souběh a čísla mimo 1–6, zvládá přetečení času a při pozdním volání provede nejvýše jeden krok. **Tento historický sériový prototyp nemá skutečné výstupy.** Sériový adaptér jen tiskne, zpracuje nejvýše 32 bajtů za průchod, přijímá `select 1..6`, `status`, `help`.

Mobilní aplikace je jedna statická obrazovka ve vanilla TypeScriptu. Lockfile obsahuje TypeScript 5.9.3 a Vite 7.3.6; Vite deklaruje Node `^20.19.0 || >=22.12.0`. Neexistuje backend, síťový protokol, párování, PWA offline cache, audio ani AI integrace. Pro tento dokument nebyly spouštěny buildy, instalace ani hardware.

Cílový provoz: obsluha připraví šest fyzických přihrádek a kufr připojí k Wi-Fi s internetem. **ESP32-S3 samo vede celé spojení s backendem: nahraje hlas, odešle nahrávku, převezme návrh a přehraje odpověď. Telefon není potřeba.** Po 4–7 otázkách AI vybere dostupnou hůlku, kufr oznámí volbu a autonomně provede omezenou prezentaci vysunout–předvést–zasunout. Volitelná aplikace nabízí ruční výběr, opravu návrhu, text, diagnostiku a záložní mikrofon. Hůlka zůstává jako rekvizita; úspěšný normální návrat obnoví připravenost pro dalšího návštěvníka. Po přerušení či poruše je nutná kontrola a zotavení.

První provozní verze tedy pracuje samostatně **z hlediska telefonu**, nikoli bez internetu pro AI. ESP32 neprovádí lokální rozpoznávání řeči ani LLM inference. Volný nepřerušovaný poslech, automatické otevření na slovo, biometrická identifikace a trénování vlastního modelu nejsou součástí první verze. V samostatném režimu je bezpečnostní hranicí místní validace návrhu, kalibrace, omezené časování a nezávislé odpojení pohonů. V explicitně převzatém ručním režimu je alternativou vědomé potvrzení ve spárované aplikaci; další stisk na kufru potom není nutný. Ani v jednom režimu nestačí samotný výsledek AI.

## 2. Rozhodnutí a porovnání variant

| Oblast | Doporučená varianta a důvod | Nevýhoda a kdy rozhodnutí změnit |
| --- | --- | --- |
| Mobil | Zachovat TypeScript/Vite, doplnit **Capacitor 8; první podporovaný klient je iOS/iPhone**. Zdrojové soubory vyvíjet na Windows, nativní testy, sestavení a podpis provádět vzdáleně v GitHub Actions na macOS/Xcode. UI se balí do aplikace, funguje bez načtení webu; Swift adaptér řeší místní TLS, discovery, mikrofon a Keychain. | Pro CI build není nutný vlastní Mac. Ladění nativních chyb přes CI je pomalejší a instalační/podpisové podmínky musí odpovídat konkrétnímu iPhonu. Android je až volitelné další rozšíření, nikoli podmínka první verze. |
| Čistá PWA | Zachovat webový režim pro simulátor a vývoj. Pro produkční řízení ji nevolit. | HTTPS web → lokální zařízení, důvěra lokálnímu certifikátu, oprávnění a uspávání jsou komplikace. Web Bluetooth má omezenou podporu. PWA přehodnotit, pokud se ovládání přesune na důvěryhodnou místní HTTPS bránu nebo bude přesně omezený a ověřený prohlížeč. |
| Plně nativní aplikace / MAUI / Flutter | Nyní nepřepisovat existující webový základ. | Zvážit při požadavku na dlouhodobý provoz na pozadí, hlubší integraci OS nebo pokud údržba nativních mostů převáží přínos společného UI. |
| Lokální spojení | **Wi-Fi + WSS**, telefon iniciuje spojení do ESP32. Jeden řídicí a jeden binární mediální socket. | Více konfigurace než BLE povely. BLE šetří síťovou infrastrukturu pro několik bajtů, ale přenos hlasu/obrázků a souběh rádií jsou další komplikace. BLE přidat pouze pokud se ukáže nutná záloha samotných ručních povelů. |
| Síť na akci | Vyhrazený malý router/AP, 2,4 GHz pro ESP32, telefon ve stejné LAN, bez izolace klientů. WAN z Ethernetu nebo LTE. | Další zařízení, energie a případný nákup mimo současný košík. Hotspot řídicího telefonu lze použít po testu konkrétního telefonu; není výchozí provozní předpoklad. |
| Backend | **ASP.NET Core 10 LTS**, jeden proces, Minimal APIs a vertical slices, SQLite pro přístupy, limity a technický audit. Linux kontejner na jedné malé VM v EU. | Provoz VM a aktualizace. TypeScript backend by sdílel jazyk klienta, ale zde převažuje jednoduchý .NET provoz a testování. Serverless přidává cold start a limity běhu/úložiště. Při více instancích přejít na PostgreSQL a sdílenou idempotenci/kvóty. |
| Vlastník řízení | **ESP32 iniciuje HTTPS spojení s backendem a orchestruje rozhovor.** Backend vrací data, nemá endpoint ani push kanál pro otevírání. Autonomní přijetí a sekvenci vlastní firmware. Mobil je místní záloha. | Vyžaduje device identitu, TLS, správu paměti a nový conversation slice ve firmware. Pro S3 N16R8 je omezený PTT přenos realistický; nutné potvrdit heap/DMA/PSRAM zátěžovým testem. Telefonní most je záložní architektonická varianta jen při nevyhovujících měřeních. |
| AI | Responses Sol pro text; Realtime 2.1 pro přímý hlas od D135, reasoning **medium** od D140 na přání majitele. Validované funkce určují návrh. Bez agentního frameworku, RAG nebo nástrojů řídících zařízení. | D135 vychází z primárních zdrojů: 2.1 zlepšuje hluk, ticho a přerušení; skutečná česká kvalita/latence vyžaduje poslech. D140 mění pouze Realtime effort; medium může zvýšit latenci a usage. SDK a wire kontrakt klientů zůstávají, JSON patch přidává reasoning. Konzervativní sazba výstupu 64 USD/1M pokrývá i textové reasoning tokeny 24 USD/1M; peněžní stropy od 9. 10. 2026 určuje pouze OpenAI účet. |
| Hlas | iOS Debug: **průběžný Realtime poslech se server VAD**, mikrofon se zavírá během přehrávání. PTT a STT → Sol → TTS jsou zálohy. | D116 drží jedno provider spojení a automaticky pokračuje po dohrání. Přerušování řeči a samostatný ESP32 hlas vyžadují další práci E9. Latenci/kvalitu změřit po nasazení; místní bezpečnost výběru zůstává v ESP32. |
| Firmware runtime | Pouze **ESP-IDF 6.1 přes PlatformIO 7.1.3**, čisté C++ jádro a vertical slices, vstup `espidf_main.cpp`. | Konfigurovatelný oficiální WSS server, I2S, NVS a kamera. Původní Arduino sériový profil i jeho adaptéry jsou na pokyn majitele D157 odstraněné z konfigurace, zdrojů a CI; nejsou podporovaný fallback. |
| Kamera | Nejprve žádná; první obrazový experiment kamerou telefonu. Kufrovou OV5640 + MicroSD dodat až jako samostatný profil. | GPIO a výkon jsou těsné. Obraz je jen volitelný popis kostýmu, nikdy identita člověka či povel aktuátoru. |

Capacitor 8 podporuje iOS 15+ a vyžaduje Xcode 26.0+; jeho CLI vyžaduje Node 22+ [O1]. Pro tento projekt připnout Node alespoň 22.12 kvůli existujícímu Vite a konkrétní otestované verze Capacitor balíčků. iOS přístup do LAN vyžaduje vlastní popis oprávnění a deklaraci používané Bonjour služby; odmítnutí musí mít vlastní stav UI [O2]. .NET 10 má uvedenou podporu do 14. 11. 2028 [O3]. Podpora WSS je doložena příkladem Espressif, nikoli dosavadním sestavením tohoto projektu [O4]. Přesné package verze a konfiguraci zvolených runtime uzamknout v příslušné implementační etapě; plán žádné balíčky neinstaluje.

## 3. Komponenty, nasazení a vertical slices

```text
Návštěvník -- PTT / hlas --> ESP32-S3 -- HTTPS /v1 --> backend --> STT / Responses / TTS
Návštěvník <-- reproduktor - ESP32-S3 <-- odpověď / návrh / audio -- backend
Validovaný autonomní výběr --> bezpečný příkaz 1..6 --> RGB / PCA / servo zdvihu
Volitelný telefon <-- místní WSS --> ESP32-S3
                    ruční volba / Stop / text / diagnostika / záložní média
```

### Vývoj

Notebook spouští backend na `https://localhost:7043`, Vite na `localhost:5173` a simulátor zařízení na `wss://localhost:8443`. Výchozí AI adaptér je deterministický fake; vývoj bez klíče a účtování je plnohodnotný. Pro skutečný telefon použít LAN adresu notebooku a zvláštní důvěryhodný vývojový certifikát, nikoli vypnutí TLS ověřování. Nativní build obsahuje vlastní UI; Vite dev server se nesmí stát podmínkou provozu. Serial simulator a hostitelské testy C++ běží bez desky.

### iOS na Windows a vzdálené sestavení

**Zvolený postup (změna majitele 2. 10. 2026):** Windows → PR s webovými kontrolami na Linuxu → merge → ručně vyžádané nativní kontroly a Ad Hoc podpis na macOS/Xcode → IPA artifact → ruční publikace. Majitel dodal workflow z jiné MAUI aplikace a výslovně povolil vhodnější řešení. Jeho YAML nelze pro Capacitor beze změny použít. Webové PR kontroly zajišťuje `.github/workflows/kufr-web.yml`; jediný nativní iOS build je ruční `.github/workflows/kufr-ios-sign.yml` nad `main` pro podepsanou aplikaci určenou k nasazení. PR ani obnovovací CI nespouští unsigned simulator build. Pro registrovaný iPhone 15 Pro s iOS 26.6.1 majitel zvolil Ad Hoc IPA z tohoto CI a instalační HTML stránku, manifest a IPA na vlastním serveru `https://2.28.202.220/ios/`; publikace smí nastat až po úspěšném podpisu a ověření artifactu. Běh CI a instalaci na iPhone eviduje stav implementace.

Na Windows se upravuje TypeScript, Swift pluginy, konfigurace a testy. Webové UI a simulátor zařízení se ověřují lokálně; Xcode a nativní testy běží na macOS runneru pouze při ručním podepsaném buildu. iOS Simulator lze použít lokálně na Macu. Zdrojový nativní projekt `kufr/mobilni-aplikace/ios/` včetně vlastní Swift části a sdíleného Xcode scheme bude verzovaný, nebude se znovu generovat při každém sestavení. Projekt používá Swift Package Manager; `Package.resolved` vznikne na macOS při prvním úspěšném resolve a musí se vrátit do repozitáře před uzavřením E3a [O1]. Build po `npm ci`, `npm run build` a `cap sync ios` nesmí přepsat vlastní nativní kód. Závislosti stahovat podle lockfile, nikoli neomezeně aktualizovat.

Nativní iOS implementace:

- `URLSessionWebSocketTask` pro control i media WSS a `NWBrowser` pro Bonjour `_kufr._tcp`; pevně omezený cíl a TLS pin podle kapitoly 4. Je to nativní transport, ne spoléhání na JavaScriptový WebSocket uvnitř WKWebView [O16].
- `Info.plist`: `NSLocalNetworkUsageDescription`, `NSBonjourServices` s `_kufr._tcp`, `NSMicrophoneUsageDescription` pro záložní hlas a `NSCameraUsageDescription` pro QR/snímek. Oprávnění žádat v okamžiku použití. Při odmítnutí nabídnout vysvětlení a cestu do Nastavení; bez mikrofonu funguje ruční/textový režim. Použít systémový Bonjour, ne vlastní broadcast vyžadující další multicast entitlement [O2].
- Certifikát desky ověřovat ve vyhrazeném URLSession trust adaptéru podle spárované identity. Otestovat self-signed TLS a přístup přes IP i Bonjour na fyzickém iOS. Nezapínat plošné `NSAllowsArbitraryLoads`; případnou konkrétní lokální ATS konfiguraci zdokumentovat a otestovat, nikoli zaměnit síťové oprávnění za důvěru certifikátu.
- Tokeny a piny do iOS Keychain s dostupností jen na odemčeném zařízení, bez iCloud synchronizace. Zachovat stabilní Bundle ID/Team ID pro aktualizace. Změna podpisové identity může znepřístupnit Keychain a vyžadovat nové párování.
- Audio přes AVFoundation; přerušení hovorem, změna audio route, uzamčení a odchod do pozadí uvolní manual lease a zastaví vlastní média. Samostatný ESP32 tím není ovlivněn. První verze nevyžaduje background audio ani background trvalé řízení.

#### Posloupnost GitHub Actions

1. **Kontroly bez podpisových tajemství:** PR a ruční obnovovací CI spustí TypeScript kontrolu, mobilní unit testy a webový build na Linuxu. Nespouští Xcode ani iOS Simulator. Nativní transport/debug testy se provádějí až v ručním podepsaném buildu po synchronizaci Capacitoru; ani ty nejsou důkaz komunikace s fyzickým kufrem.
2. **Připnutý toolchain:** pro první integraci doporučuji `macos-15` a explicitně Xcode 26.3 (`DEVELOPER_DIR=/Applications/Xcode_26.3.app/Contents/Developer`), který otevřený seznam image uvádí [O15]. Při převzetí již funkční pipeline zachovat její kompatibilní pin. Na začátku kontrolovat `xcodebuild -version`, dostupný SDK/simulator runtime a uzamčené závislosti. Změna image nebo chybějící Xcode znamená srozumitelnou chybu, ne tichý fallback na jinou verzi. Hosted image se aktualizuje i pod stejným label; verzi image zaznamenat u výsledku.
3. **Podpis až po zelených kontrolách:** signing job pouze při ručním `workflow_dispatch` nad `main`, s převzetím webového artefaktu úspěšného CI a ověřením shody stromu s aktuálním `main`; PR z forku ani nedůvěryhodný kód nesmí získat secrets. Použít GitHub Environment pro podpis, minimální oprávnění jobů a oddělit build od veřejné publikace. Zkoušky pro simulátor nepotřebují distribuční `.p12`.
4. **Secrets:** ruční Ad Hoc workflow očekává `IOS_DISTRIBUTION_CERTIFICATE_BASE64`, `IOS_DISTRIBUTION_PROFILE_BASE64`, `IOS_P12_PASSWORD` a `IOS_KEYCHAIN_PASSWORD` v environmentu `ios-signing`. Vývojovou identitu tento workflow nepoužívá. Base64 není šifrování. `.p12` musí obsahovat odpovídající soukromý klíč. V `RUNNER_TEMP` dekódovat jen vybranou dvojici souborů, vytvořit dočasný odemčený keychain, importovat identitu a zpřístupnit ji codesign; provisioning profil nainstalovat do umístění očekávaného vybraným Xcode. Tuto cestu popisuje přímo GitHub [O17].
5. **Předpodpisová kontrola:** profil, certifikát, Team ID, App ID/Bundle ID a entitlements musí souhlasit; ověřit expiraci a pro Ad Hoc členství cílového UDID v profilu. `IOS_TEAM_ID`, `IOS_BUNDLE_ID`, Xcode scheme a exportní profil jsou explicitní konfigurace projektu. Skutečný identifikátor přebrat od vlastníka, nevymýšlet tým ani podpisovou identitu.
6. **Archive a export:** `xcodebuild archive` pro fyzické zařízení (`generic/platform=iOS`), potom `xcodebuild -exportArchive` s jednorázovým `ExportOptions.plist`. Workflow používá pouze Ad Hoc metodu `release-testing` a dodaný distribuční profil/certifikát pro Safari instalaci. Dostupnost vybrané metody ověří `xcodebuild -help` připnutého Xcode. Build používá ruční podpis dodaným profilem, bez automatického zakládání identit. Zkontrolovat výsledný podpis, embedded profil, entitlements, verzi/build a architekturu zařízení.
7. **Artifact a web:** E3a uloží IPA a SHA-256 jako GitHub Actions artifact s retencí 14 dní; commit SHA a běh jsou dohledatelné v Actions. Po Ad Hoc podpisu self-hosted runner ověří IPA a atomicky zveřejní verzovaný soubor, manifest a instalační stránku pod `/ios/`; samostatný job na témže serverovém runneru ověří přes veřejné HTTPS manifest, MIME typy, IPA a checksum. Na zadání majitele 1. 10. běží všechny Linux joby na vlastním serveru; ověření z jiné sítě proto vyžaduje samostatnou klientskou zkoušku. S E11 doplnit verzovaný build report a skutečnou instalaci/aktualizaci na iPhonu. `.p12`, hesla, samotné secret soubory a keychain nikdy neuploadovat. IPA ze své podstaty obsahuje podpis a embedded provisioning profil; ani veřejnou IPA nepovažovat za místo pro tajné údaje.
8. **Vlastní server:** Ad Hoc publish job nad již ověřenou IPA, nikoli nový rozdílný build. Nejdříve uložit verzovanou IPA a manifest na vlastní HTTPS server, ověřit hash a metadata a teprve pak atomicky aktualizovat instalační stránku. Ponechat starší ověřenou verzi pro návrat. Publikace je explicitní parametr ručního běhu, ne vedlejší efekt každého PR.
9. **Volitelné ntfy:** pokud je zapnuté, po dokončení požadovaného build/publish kroku poslat verzi, výsledek a odkaz. NTFY URL/topic/token držet v konfiguraci/secrets dle citlivosti, neposílat certifikáty či UDID. Selhání oznámení nesmí znehodnotit úspěšnou IPA [O21]. Zapnutí skutečného odesílání musí být součástí autorizované konfigurace distribuce E11.
10. **Úklid vždy:** `if: always()` po úspěchu i chybě odstraní dočasný podpisový keychain, dekódovaný `.p12` a nainstalovaný provisioning profil a obnoví případně změněný seznam keychainů. Mazat jen konkrétní cesty vytvořené jobem. U GitHub-hosted runneru navíc zaniká celý stroj [O17]; ani tak nespoléhat na budoucí použití self-hosted runneru bez explicitního úklidu.

Actions připnout na prověřené revize; build job má běžně jen `contents: read`, Release job samostatně `contents: write`, Pages job jen potřebná Pages oprávnění. Nedávat podpisové secrets do cache ani do jobu spouštějícího kód z cizího PR přes `pull_request_target`. Privátní podpisový job nepřebírá neověřený artifact z nedůvěryhodného workflow. Náklady macOS runneru, retenci a dostupný Actions limit ověřit v konkrétním účtu; nezahrnovat je automaticky do rozpočtu AI volání.

#### IPA, instalační stránka a způsoby distribuce

Majitel dodal vývojový provisioning profil pro `3QRXV8G5Y3.app.turquoise6409.green2444` s jedním registrovaným zařízením a expirací 25. 12. 2026 a odpovídající Ad Hoc profil se stejným Team/Bundle ID, jedním zařízením a expirací 24. 12. 2026. E3a používá stejné Bundle ID jako původní aplikace, kterou majitel z telefonu odstraní. Majitel potvrdil UDID svého iPhonu 15 Pro s iOS 26.6.1; Ad Hoc podpis prošel v CI a majitel potvrdil instalaci i spuštění přímo na telefonu. Simulator build ani vývojově podepsaná IPA nejsou náhradou této Ad Hoc zkoušky [O18].

| Výstup / klient | Účel a hranice |
| --- | --- |
| GitHub Actions artifact | Archiv pro vývojáře a ruční stažení; má expiraci a standardně vyžaduje GitHub přihlášení/oprávnění. Jeho UI URL ani dočasný download redirect nepatří do veřejného instalačního feedu [O19]. |
| GitHub Release IPA | Doporučené verzované download URL pro feed/OTA, pokud je vydání veřejně přístupné. U privátního repozitáře neposílat GitHub PAT do `apps.json`; použít vědomě veřejný distribuční repozitář nebo jiný HTTPS hosting kompatibilní s instalátorem. Samotný podpis nechrání IPA před stažením. |
| Vlastní IP HTTPS server | Statická stránka `/ios/`, verzovaná IPA a manifest na stejném serveru jako backend, ale mimo API a bez backendových tajemství. Publikuje se až po Ad Hoc podpisu; aplikace při běhu instalační stránku nepotřebuje. |
| `apps.json` | Feed s názvem zdroje a aplikace, Bundle ID, verzí/buildem, datem, velikostí, minimálním OS a `downloadURL` přesně pro publikovanou IPA; doplnit permissions podle schématu zvoleného klienta. Generovat z ověřených build metadat a otestovat import ve Feather i AltStore Classic. Shodný formát automaticky nezaručuje shodný způsob instalace [O20]. |
| Feather | Importuje IPA/feed a může aplikaci znovu podepsat vlastním certifikátem/profilem. Klient i jeho platná identita už musí být na zařízení připravené. Neřeší přístup na nepovolený iPhone ani platnost certifikátu; změna podpisu může změnit Keychain přístup [O20]. |
| AltStore Classic | Samostatná alternativní cesta sideloadingu/přepodpisu s vlastními podmínkami účtu a obnovy. Nepředpokládat, že zachová CI podpis. **AltStore PAL je jiný distribuční režim** s požadavkem na notarizovanou aplikaci a příslušná metadata; tento plán jeho nasazení nezavádí [O20]. |
| OTA `manifest.plist` | Doplňkový manifest pro podporovanou Ad Hoc instalaci, URL přes HTTPS a odpovídající Bundle ID/verze/IPA. Odkaz `itms-services` není oprávnění instalovat; rozhoduje platný podpis/profil a konkrétní iOS. Oficiální Xcode dokumentace popisuje OTA manifest při self-distribution, výsledek je nutné otestovat na registrovaném iPhonu [O18]. |

Platnost certifikátu/profilu kontrolovat před akcí; vytvoření nového ZIP/IPA neprodlouží jejich expiraci. Změna UDID nebo entitlementů vyžaduje nový profil/build. CI build potvrzuje sestavitelnost a testy na runneru; dokončení distribuce vyžaduje stažení, instalaci, spuštění a aktualizaci na skutečném iPhonu. Následně ověřit přístup do LAN, správné odmítnutí cizího TLS pinu, Keychain po aktualizaci a zastavení při uzamčení. Pro tento workflow není potřeba vlastnit Mac, ale bez něj není dostupné běžné lokální interaktivní Xcode ladění; použít testy, `.xcresult` a bezpečný export diagnostiky z aplikace.

### Provoz

- Telefon: volitelná iOS aplikace instalovaná z podepsané IPA vytvořené macOS CI, s UI, offline katalogem a přístupem k lokálnímu zařízení. V ručním režimu drží řízení; při odchodu do pozadí je uvolní a zastaví vlastní média. Pozorovatelské odpojení telefonu nemá vliv na samostatný kufr.
- ESP32: v LAN routeru, bez přesměrovaných portů z internetu. Uchovává kalibraci, lokální párování, vlastní device credential a stav bezpečnosti. V RAM drží právě jednu omezenou nahrávku/odpověď a identifikátory sezení; historii drží backend. AI provider klíč zde není. MicroSD není podmínkou bootu.
- Backend: na majitelem dodané Ubuntu VM nasazovat self-contained ASP.NET Core 10 jako oddělenou systemd službu. Na výslovné zadání majitele 1. 10. 2026 vlastní runner nyní také sestavuje a testuje Linux projekty, ověřuje artefakty/HTTPS a provádí diagnostiku; jen macOS joby zůstávají GitHub-hosted. Build běží jako účet runneru bez root a produkčních environment secrets. Aktivace hotového artefaktu nadále používá předem nainstalovaný root-owned helper. PR CI sdílí dlouhodobý serverový účet a musí pocházet z důvěryhodných PR; detaily a předpoklady jsou v [CI/CD návodu](../../docs/ci-cd.md). Verze jsou v `/opt/kufr-backend/releases`, SQLite v `/var/lib/kufr-backend`, secrets v omezeném systemd environment souboru. Po přepnutí se ověřuje `/healthz`, při neúspěchu se vrací k předchozímu release a konfiguraci. Toto nahrazuje původní návrh kontejneru, protože runner již běží přímo na cílovém Ubuntu a současný backend nepotřebuje další běhovou vrstvu. Migrace SQLite zůstávají dopředné; rollback binárky musí být s novým schématem kompatibilní. Budoucí readiness má ověřit dostupnost DB/konfigurace bez placeného AI dotazu.
- Backend zatím poslouchá jen `127.0.0.1`; před spojením ESP32 je potřeba důvěryhodný certifikát pro DNS jméno nebo IP SAN, HTTPS reverzní proxy a firewall. Samotná veřejná IP nezaručuje TLS identitu. OpenAI volá pouze backend. Zálohy se týkají jen konfigurace, přístupů a čítačů, nikoli nahrávek; před akceptací potvrdit region, retenci a limity.

Výpadek WAN při funkční LAN nesmí zablokovat ruční výběr. Při výpadku routeru lze fyzickým servisním postupem spustit chráněný SoftAP ESP32 pro **lokální ruční režim**. Připojení k tomuto AP samo nezajišťuje internet; automatické přepínání Wi-Fi/mobilních dat neslibovat.

### Konkrétní vlastnictví funkcí

Tabulka je návrh cílových adresářů; vytvářet je postupně s příslušnými etapami, nikoli jako prázdnou kostru celého systému. Každá slice drží kontrakt, validaci, rozhodování, adaptéry a své testy. Sdílet jen skutečně společný protokol a technické primitivy; nezavádět univerzální repository, mediátor ani povinnou abstrakci každé třídy.

| Komponenta a slice | Co vlastní |
| --- | --- |
| `mobilni-aplikace/src/features/device_connection/` | Párování, discovery, WSS adaptér, oprávnění LAN, lease, snapshot a reconnect. Nativní Swift adaptér pod odpovídajícím pluginem v `ios/`; TypeScript deklaruje úzké rozhraní a webový fake. |
| `.../conversation/` | Volitelný pohled na sezení firmware, textový vstup a oprava přepisu přes ESP32, zrušení, zobrazení AI chyby. Nemá vlastní paralelní cloudové sezení. |
| `.../voice_turn/` | Záložní mikrofon/přehrávání telefonu přes ESP32, stav médií. Nativní most neposílá každý PCM rámec přes DOM. |
| `.../wand_selection/` | Návrh AI, dostupnost, ruční změna, potvrzení, příkaz a sledování výsledku. Žádná druhá implementace časování motorů. |
| `.../manual_control/` | Offline výběr 1–6, Stop, přehled chyb a ruční potvrzení založení hůlky. |
| Nativní `KufrDebugPlugin` + `DebugBackendClient`, `features/debug/` | Souhlas s jedním snímkem, náhled, smazání a ruční oprava popisu pro testovací dialog; `features/voice_control/` předává souhlas samostatnému kufru. |
| `backend/Features/Access/` | Registrace zařízení, vydání krátkého access tokenu za device credential, odvolání a izolace každého kufru. |
| `backend/Features/Conversations/` | Start/ukončení sezení, textové tahy, sestavení promptu, Responses adaptér a omezená historie. |
| `backend/Features/VoiceTurns/` | Validace WAV, STT adaptér, převzetí přepisu; neotevírá hůlky. |
| `backend/Features/Realtime/` | Oficiální OpenAI SDK, průběžný hlas se server VAD, PTT záloha, PCM/NDJSON transport a validace funkcí; backend drží provider klíč a jedno WebSocket spojení po celý rozhovor. |
| `backend/Features/SelectionProposals/` | Katalog, pravidla dostupnosti, interpretace strukturovaného návrhu a jeho expirace. |
| `backend/Features/Speech/` | TTS pouze pro již schválenou odpověď, audio metadata a krátkodobý obsah. |
| `backend/Features/Usage/` | Transakční rezervace nákladů, kvóty, technické metriky a idempotence placených operací. |
| `backend/Features/CostumeDescription/` | Jednorázový obrázek → opatrný popis kostýmu; bez rozpoznávání tváří. |
| `firmware/src/features/wand_selection/` | Stávající sekvence, abort, hardwarový port RGB/PCA/motorů, kalibrace přihrádek. |
| `firmware/src/features/device_control/` | Ověření klienta, příkazy, idempotence, revision, snapshot, výhradní vlastník řízení. |
| `firmware/src/features/conversation/` | Samostatný orchestrátor: fyzické PTT, HTTPS klient backendu, session/turn/generation, návrh, autonomní přijetí a ukončení opuštěného sezení. Text a média telefonu jsou alternativní vstupy do stejného sezení. |
| `firmware/src/features/safety/` | Jediný vlastník povolení pohonů, stop latch, softwarové timeouty, reakce na reset a chyby sběrnice. |
| `firmware/src/features/voice_turn/` | I2S, DMA, PTT, zesilovač, omezené buffery, lokální nouzové audio. |
| `firmware/src/features/provisioning/` | Fyzické párovací okno, Wi-Fi/NVS, identita desky a revize konfigurace. |
| `firmware/src/features/costume_photo/` | OV5640 a jeden JPEG před hlasem, servisní ověření kamery a souhlas pro jednotlivý start; samostatná volitelná capability. |
| `firmware/src/features/local_assets/` | Kontrolované čtení nouzových hlášek z flash/SD; ztráta SD neblokuje řízení. |
| `simulator/`, `contracts/`, `tests/` | Hostitelský firmware model, sdílené JSON Schema/OpenAPI, pozitivní i chybné protokolové fixtures a integrační scénáře. |

`main.cpp` zůstane skládáním. V IDF plánovat smyčku řízení s cílem průchodu do 10 ms; síť, TLS, audio a SD mají oddělené úlohy a omezené fronty. Do sekvence vstupuje jen jedna řídicí úloha; callback socketu ani HTTP odpověď nesmí přímo přepnout GPIO. `esp_http_client_perform` může blokovat volající task, proto patří do síťové úlohy [O13]. Sdílené I2C obsluhuje jeden koordinovaný adaptér s timeoutem; bezpečnostní vypnutí se sběrnicí nekomunikuje.

D99 skládá boot-lifetime runtime v `espidf_main.cpp`. `EspIdfStationNetwork`
načte nastavení jednou, vlastní STA/netif a v samostatné úloze opakuje připojení.
Backend na tuto síť pouze odkazuje; porucha identity/času/tokenu/WAN ji nezničí.
Server se aktivuje po IP a zdravých provisionovaných identitách, zůstává během
reconnectu a přijímá pouze modelové příkazy. Smyčka volá server.Pump, owner.Tick
a owner.PumpReply; přímý druhý runtime Tick je odstraněn. Lease ID vzniká přes
STA-gated RNG, boot a místní conversation UUID před startem RF. Katalog je
`ollivander-private-party-v1`. Reset gate je false, kalibrace 0 a bezpečný boot
neobchází recovery; chybí fyzické zotavení, kalibrace, autonomní fyzický port,
opening/PTT a audio. Další akceptace E4/E5 vyžaduje B1 a živé zařízení. Hostitelské
stub testy a build neprokazují reconnect/IP změny/TLS/aktuátory na desce.

## 4. Připojení, párování a identita

### První nastavení

**D161 – rozhodnutí majitele 9. 10. 2026:** po ověřeném BLE spojení má
iPhone spárovat místní Wi-Fi ovládání jedním tlačítkem bez opisování adresy,
otisku nebo dalšího tajemství. Jedinečný servisní PoP a Security1 zůstávají
autoritou BLE; samotné nalezení zařízení nestačí. U zdravé, dosud prázdné
lokální TLS identity firmware při startu vygeneruje samostatný EC klíč a
self-signed certifikát a atomicky je uloží. Existující i poškozené záznamy
se automaticky nepřepisují. Rotace a obnova zůstávají fyzickým servisem.
TLS server a Bonjour mohou běžet před prvním párováním; řízení dál vyžaduje token.

Šifrovaný `kufr-control` přijímá `{"v":1,"command":"pair-local"}`.
Úspěšná odpověď do 512 B je přesně
`{v:1,ok:true,id,host,spkiSha256,token}`; pin a token jsou 64 malých hex znaků.
Firmware povolí pouze první vydání při chybějícím pairing záznamu,
uloží jen hash tokenu a odmítne existující QR/párování. Nativní klient
ověří ID proti ověřenému BLE stavu a vloží profil do nesynchronizovaného
Keychain `WhenUnlockedThisDeviceOnly`, bez přepsání dřívějšího profilu.
Do JavaScriptu vrátí jen ID/adresu. Stejný pin ověřuje následné HTTPS/WSS.
Nejistý příkaz se neopakuje; ztráta odpovědi nebo Keychain zápisu po vydání
tokenu vyžaduje servisní obnovu. Níže uvedený QR postup zůstává servisní alternativou.

1. Při servisním uvedení do provozu vygenerovat pro každou desku samostatný TLS klíč/certifikát, `deviceId` a náhodné párovací tajemství alespoň 128 bitů. Generování/provisioning je budoucí lokální servisní krok, ne obsah firmware image. QR uvnitř kufru obsahuje `deviceId`, SHA-256 otisk veřejného klíče, jednorázové tajemství a údaje servisního AP. Privátní klíč neopouští servisní úložiště desky.
2. Párovací AP povolit jen držením tlačítka během startu, nejvýše 120 s, s unikátním WPA2 heslem. Výstupy jsou během nastavení blokované. Nepoužívat běžné BOOT tlačítko jako návštěvnické PTT.
3. Telefon přečte QR a přes nativní TLS adaptér ověří přesně očekávaný veřejný klíč. Self-signed identita není globální výjimka ověřování. Při odlišném klíči spojení odmítnout; mDNS/IP nejsou důvěryhodná identita.
4. Přes připnuté TLS předat Wi-Fi SSID/heslo a spárovat obsluhu. Firmware ověří tajemství, vydá unikátní náhodný 256bitový lokální bearer token, uloží jen jeho hash a původní tajemství zneplatní. iPhone uloží token do iOS Keychain. Při ztrátě telefonu je obnova pouze fyzickým servisním resetem párování; nový QR/tajemství, žádné vzdálené obejití.

5. Deska přejde do STA režimu routeru, AP zavře. Změna sítě nikdy sama nevyvolá pohyb.

Pro `Authorization: Bearer` místního WSS se 32bajtový token předává jako přesně 64 malých hexadecimálních znaků. Je to textové kódování stejného tokenu, ne další tajemství; hlavička nesmí obsahovat další znaky ani token v URL. Dekodér po ověření vymaže dočasný binární buffer. Nativní klient bude ukládat původní token do Keychain a při handshake jej takto zakóduje.

D98 konkretizuje místní HTTPS `POST /v1/pair` pro již servisně provisionovanou
TLS identitu a jednorázový párovací záznam. Klient připne SPKI z QR a pošle
`Content-Type: application/json` a objekt `{"protocolVersion":1,"pairingSecret":"lowercase hex"}`
(secret 16–64 bajtů, tělo do 192 bajtů, žádný query token/Transfer-Encoding).
Úspěch vrací `{"protocolVersion":1,"token":"64 lowercase hex znaků"}` s no-store.
WSS používá token v Authorization Bearer; zařízení uloží jen hash. 400 znamená
formát, 401 nesprávné tajemství nebo nedostupné RNG, 409 již spotřebované párování,
413/415 velikost/typ, 503 poškozené úložiště. Ztráta úspěšné odpovědi vyžaduje
fyzický servisní reset. Server nepotřebuje backend token pro párování, vyžaduje
však aktivní STA pro RNG. Servisní AP, čtení QR kamerou a skutečný USB
zápis po fyzické bráně zůstávají navazující části; žádná identita není v image.

**Podporovaná cesta zcela bez telefonu:** autorizovaný lokální servisní nástroj na notebooku přes USB nastaví Wi-Fi, backend host, čas, device credential a počáteční katalog/kalibraci. Vyžaduje fyzický servisní režim při startu a odpojenou větev pohonů; pracuje interaktivně bez tajemství v argumentech příkazové řádky, zdrojích či logu. Tento postup platí pro vývoj i produkční uvedení do provozu. Notebook se po nastavení odpojí a na akci není potřebný. Lokální certifikát/QR lze připravit předem pro případ pozdějšího použití záložní aplikace; její spárování není podmínkou cloudu. Pozdější změna Wi-Fi je opět servisní USB nebo spárovaná aplikace.

TLS adaptér musí validovat identitu vůči uloženému klíči a ověřit TLS handshake, nikoli použít `trust all` nebo plošně vypnout hostname/certifikáty. Pro tuto lokální identitu je autoritou QR pin v odděleném klientovi; cloud používá běžné veřejné PKI a správný hostname. Životnost/obnova lokálního certifikátu se provádí v servisním režimu. Bez důvěryhodného času ESP32 se nelze spoléhat na jeho ověření absolutní expirace uživatelského certifikátu; proto lokální klientská autorizace používá token uvnitř připnutého TLS a monotónní lease.

### Samostatné ESP32 → backend spojení

Po bootu: bezpečné výstupy → připojení Wi-Fi → kontrola času → HTTPS autentizace → připravenost rozhovoru. `esp_http_client` s mbedTLS a CA bundle ověřuje hostname i certifikát backendu; žádné `insecure`, HTTP downgrade nebo libovolný redirect. Backend URL je servisní konfigurace s allowlistem hostu, ne text přijatý od AI [O13]. Čas pro TLS získat SNTP, porovnat s datem buildu a posledním důvěryhodným časem; zásadní rozpor hlásit. Při nedostupném/nevěrohodném času zůstat offline, oprava servisně přes spárovaný telefon/USB, nikdy vypnutím ověřování certifikátu [O14]. Řízení pohonů nadále používá monotónní čas.

Každý kufr má unikátní náhodný 256bitový device credential, provisionovaný servisně do NVS; backend uchovává hash a device scope. HTTPS `POST /v1/device-access/token` jej v Authorization hlavičce vymění za access token na 15 min, uložený pouze v RAM. Obnova po 12 min, při 401 jednou znovu autentizovat; neplatný/odvolaný credential přepne AI offline. Provider API key zůstává pouze na backendu. Rotace/revokace se provádí pro jednotlivé zařízení, ne změnou klíče v každém firmware image.

ESP32 volá níže uvedené HTTP API, nejvýše jeden aktivní tah, drží keep-alive spojení pro malé požadavky. WAV upload/download jde v blocích 4 KiB bez base64 a bez kopie celé nahrávky do TLS bufferu. Backend odpoví 202 + operationId; ESP32 polluje stav. Výsledek musí echoovat `{deviceId, bootId, conversationId, clientTurnId, generation, deviceRevision}`. Nesoulad nebo již zrušená generation se zahodí. Zrušení/ruční převzetí zvýší generation a nečeká na konec cloudu.

Pro E4.3d sdílí vlastník řízení a síťová úloha jeden uzamčený snapshot `{generation,deviceRevision,readyMask,catalogVersion,version}`. Po potvrzeném zápisu journalu a změně řízení jej vlastník publikuje jako celek; při poruše jej zneplatní. Čekající text nese verzi snapshotu a síťová úloha před zahájením i po přijetí výsledku ověří shodu. Změna verze zahodí starý tah a při dalším tahu založí nové backend sezení. Tento mechanismus sám nevytváří autorizovaný místní textový vstup: jeho producent musí projít WSS autentizací a platným ručním lease podle kapitoly 5.

Wi-Fi reconnect 1/2/4/8/16/30 s s jitterem; návrat připojení sám nezahájí rozhovor ani nepřehraje staré potvrzení. Při výpadku zahodit nepotvrzený návrh, ukončit nahrávání a nabídnout místní nouzovou hlášku. Opakovat lze stejný idempotentní API požadavek nebo polling dosud platné operace, nikoli vytvořit nový turn se starou nahrávkou. Po restartu zařízení začíná nové sezení. Pro běžný provoz není nutný MQTT broker, cloudový WebSocket ani mobilní relay.

### Volitelné místní spojení telefonu

- Discovery `_kufr._tcp.local`, TCP port 443; TXT přesně `deviceId`, `protocol=1` a `capabilities` (CSV názvů ze snapshotu, bez tajemství, tokenu a TLS pinu). Po 5 s bez nálezu nabídnout zadání IP nebo DHCP rezervaci routeru, také pro již spárovanou identitu. Výběr adresy sám nepřipojuje; uložený pin/token se nemění a adresní hint se trvale uloží až po úspěšném připnutém autentizovaném WSS. Cloud nikdy neskáče do nalezené LAN adresy. ESP-IDF 6.1 používá pro tuto plánovanou funkci oficiální komponentu `espressif/mdns` 1.13.1 přes manifest/lock; inzerce běží jen s IP a spuštěným provisionovaným TLS serverem, mimo safety/cloud úlohu. [Dokumentace komponenty](https://docs.espressif.com/projects/esp-protocols/mdns/docs/latest/en/index.html).
- `wss://<adresa>/v1/control`: token v `Authorization` hlavičce nativního handshake, nikdy v URL. Limit jeden telefon; další dostane `controller_busy`. Připojení samo je pozorovatelské, nezastavuje samostatný provoz. `control.acquire` při klidu přepne explicitně na ruční režim a zneplatní čekající AI návrh; během pohybu nejprve Stop. Automatický takeover není dovolen.
- Po TLS/auth do 10 s proběhne `hello` → `welcome` s verzí protokolu, `bootId`, stavem, `deviceUptimeMs`, kalibrační revizí, `readyMask`, `revision` a capabilities. `controlLeaseId` je null, dokud telefon explicitně nepřevezme ruční řízení. Nekompatibilní major verze = pouze obrazovka chyby, žádné povely.
- Pro verzi 1 nese `hello` `{protocolVersion, type, messageId, deviceId}`; `welcome` vrací `{protocolVersion, type, requestMessageId, snapshot}`. Vnořený `state.snapshot` je úplný atomický stav včetně `bootId`, uptime, revision, kalibrace, masky a capabilities. `state.get` a `control.acquire` nesou `messageId`, `deviceId` a `bootId`; `control.release` a `heartbeat` přidávají `controlLeaseId`. `heartbeat.ack` vrací `requestMessageId`, `bootId`, uptime, revision a lease. Přesné typy/rozsahy jsou ve sdíleném JSON Schema. Bearer token zůstává pouze v TLS handshake hlavičce, ne v JSON těle.
- **Jen ruční vlastník** získá lease na 6 s obnovované heartbeat každé 2 s. Odpojení jeho socketu zruší ruční řízení ihned; tichý výpadek nejpozději po vypršení lease. UI zastará po 3 s bez zprávy a zablokuje nová potvrzení. Samostatný režim nemá telefonní lease a odpojení pouhého pozorovatele ho nepřeruší. Přechod z přerušeného ručního režimu zpět na samostatný vyžaduje fyzické potvrzení, nikdy se sám nepřepne do rozběhnutého AI návrhu.
- Druhý socket `/v1/media` je pouze pro záložní média telefonu, váže se na tentýž token, boot a případný ruční lease. Nezískává právo řídit zařízení. Jeho zaplnění nesmí zastavit heartbeat nebo Stop na control socketu.
- Opakované spojení: prodlevy 0,5 / 1 / 2 / 4 / 8 s, dále 8 s, jitter ±20 %. Po návratu vždy nový handshake a úplný snapshot. Nepřehrávat frontu povelů ani audio. Změněný `bootId` znamená restart a nutnost ručního zotavení.

### D119 – samostatný Realtime hlas ESP a nastavení v Ovládání

Majitel 1. 10. 2026 výslovně rozšiřuje D114/D116 na firmware ESP a žádá
všechna OpenAI volání přes SDK. ESP používá existující autentizovaný
`realtime-live` backendový kontrakt; telefon posílá pouze lokální nastavení
nebo explicitní start/stop a není audio/cloud prostředníkem. Textové a
kompatibilní STT/TTS adaptéry používají oficiální .NET SDK 2.14.0;
syntetický eval oficiální Python SDK 3.22.1. Automatické placené retry je 0;
rezervace, časové/velikostní limity, validace funkcí a paměť večera zůstávají.

Nové lokální zprávy `voice.configure` (`mode=off|realtime`,
`voice=cedar|marin|coral`), `voice.start` (`expectedRevision`) a `voice.stop`
mají v1 device/boot/messageId obálku a vlastní `voice.ack`. Jsou explicitní
výjimkou z manual lease pro spárovaného pozorovatele: konfigurace je jen
nastavení audia, start vyžaduje bezpečný samostatný snapshot bez manual
vlastníka, stop vypíná audio. Nenahrazují fyzický příkaz ani zotavení.
Nastavení platí v RAM do restartu, výchozí režim je off. Snapshot přidává
volitelný `voice` objekt, takže stávající simulátor bez audia je kompatibilní.
Start je jednorázový, přijetí není důkaz spojení s AI. Odpojení pozorovatele
samostatný rozhovor neruší; safety.stop a změna snapshotu jej ruší.

I2S adaptér je pouze pro servisně ověřený profil A (kamera/SD odpojené):
BCLK4/WS5/DIN6/DOUT7/mute16, levý mikrofonní slot, stereo 32bit/32 kHz.
NVS `wand_audio/profile_a_ok=1` je explicitní potvrzení zapojení; bez něj
se GPIO audia neinicializují. Průběžný FIR mění 32↔24 kHz se zachovanou
fází; mikrofon používá 400ms omezenou frontu, pořadové bloky a 30s limit.
Plná fronta/chyba přeruší rozhovor. Zesilovač začíná na 25 % amplitudy,
během poslechu je mute a po skutečném DMA drain se odešle `played`.
Síť/capture/upload mají vlastní úlohy; řídicí smyčka dál zpracovává Stop.
Validovaný návrh přebírá existující PendingProposal s maskou, korelací a TTL,
přímé spínání pohonů z provider streamu neexistuje.

Profil B/expander, fyzický vstup PTT/start bez telefonu, nouzové audio,
100 tahů na HW bez úniku RAM a fyzická akceptace B1/E9 zůstávají otevřené.
D119 je implementovaný hlasový adaptér a lokální ovládání, nikoli uzavření E9.

## 5. Datové kontrakty a spolehlivost povelů

### Společná pravidla

JSON UTF-8, názvy camelCase, explicitní `protocolVersion: 1`; JSON control zpráva nejvýše 2048 bajtů, bez komprese, bez neomezeného vnořování. Neznámý typ, pole navíc v příkazu, duplicitní JSON klíče, necelé číslo, řetězec místo čísla, `null` místo povinné hodnoty a hodnoty mimo rozsah se odmítají. UUID jako řetězce; `deviceUptimeMs` jako celé nezáporné číslo v rozsahu bezpečného integeru JavaScriptu, ve firmware `uint64_t`. `readyMask` je 6bitové číslo 0–63; bit 0 patří přihrádce 1. `revision` se mění při změně fyzického stavu/dostupnosti/konfigurace, nikoli heartbeat nebo každé fázi rozhovoru. Pro rozhovor se používá samostatná `generation`.

Pro přijatou autonomní prezentaci pevné hůlky snapshot používá phase `presenting` po celý aktivní cyklus vysunutí/prezentace/návratu. Nevydává ho za ready ani za starou jednorázovou latch sekvenci. Je to rozšíření enumu v1; strict klient před reálným připojením musí převzít aktualizované schéma. Nynější mobilní simulátor má string phase a každou ne-ready provozní fázi mapuje na running. `calibrationRevision: 0` vyžaduje ne-ready stav, neexistující mikrofon `unavailable`; výsledek softwarové sekvence stále nese `physicalVerification: unavailable`.

Výjimka velikosti pro `conversation.text` a `conversation.result`: nejvýše 8192 bajtů, vstup stále nejvýše 1000 znaků a reply nejvýše 500 znaků; validovat limit bajtů i znaků kvůli češtině v UTF-8. Dekorativní zvýraznění kandidáta během dialogu samo nemění fyzickou revision; změna dostupnosti, kalibrace, režimu řízení nebo skutečná sekvence ano. Každá běžná mutace z telefonu vyžaduje explicitní manual lease; výjimky jsou autorizované safety.stop a samotná žádost control.acquire při bezpečném klidu. Pozorovatel jinak smí jen číst. Obsluha přepne na manual také před textovým nebo telefonním hlasovým vstupem.

Ovladač uchovává odhad monotónního času desky z handshake/heartbeat a RTT. Fyzický povel nesmí odeslat při vzorku starším než 3 s nebo RTT nad 500 ms. `issuedAtDeviceMs` musí při přijetí ležet nejvýše 500 ms v budoucnosti a s `maxAgeMs` nejvýše 2000 ms nesmí být prošlý. TTL nezačíná až přijetím opožděného paketu. UTC je jen pro backend/audit; firmware fyzické povely nepodmiňuje NTP.

Ukázka fyzického příkazu po výslovném potvrzení obsluhou:

```json
{
  "protocolVersion": 1,
  "type": "selection.start",
  "commandId": "523ef590-ce4d-4901-91b3-79de1bfe7e35",
  "deviceId": "kufr-001",
  "bootId": "c85743d5-1b72-41b8-8500-ae3c102b27d6",
  "controlLeaseId": "a8287a95-b6a1-4501-bdaa-38721ec83191",
  "sequence": 12,
  "expectedRevision": 37,
  "issuedAtDeviceMs": 153200,
  "maxAgeMs": 2000,
  "payload": { "compartment": 4, "selectionSource": "operatorConfirmedAi" }
}
```

`selectionSource` je auditní enum `manual | operatorConfirmedAi | operatorCorrectedAi | buttonConfirmedAi`; není autorizace. Síťový fyzický povel vyžaduje lokální token/ruční lease. Tento enum a lease patří staré ruční cestě. Samostatný režim má oddělenou autonomní bránu pro jednorázové proposalId, bootId, generation a revision, bez vytvoření falešného ručního lease. **Adaptér pohonů** nepřijímá prompt, kostým, text odpovědi, libovolné GPIO, úhel serva, jas nad limitem ani délku motorového pulzu. Conversation slice samozřejmě text/backend data přijímá, ale může předat jen validovaný číselný návrh místní autonomní bráně. Číslo 1–6 bezpečnostní řadič zkontroluje nezávisle na backendu i aplikaci.

Autonomní prezentace před zdvihem trvale zapíše `proposalId` a přihrádku do journalu a zneplatní publikovaný snapshot. Po potvrzeném návratu do klidové polohy uzavře záznam atomicky se zachovanou maskou dostupnosti; ruční výběr naopak při dokončení příslušný bit masky maže. Po Stop, poruše nebo restartu s otevřeným záznamem se poloha považuje za neznámou, vyžaduje fyzickou kontrolu a návratovou cestu nelze použít automaticky. Vlastník řídicí smyčky volá Stop portu prezentace okamžitě a nedovolí publikovat dostupnost, dokud jeho stav není znovu klidový.

Přijetí a dokončení jsou různé zprávy:

```json
{
  "protocolVersion": 1,
  "type": "command.ack",
  "commandId": "523ef590-ce4d-4901-91b3-79de1bfe7e35",
  "bootId": "c85743d5-1b72-41b8-8500-ae3c102b27d6",
  "eventSequence": 81,
  "status": "accepted",
  "revision": 38,
  "errorCode": null
}
```

```json
{
  "protocolVersion": 1,
  "type": "selection.result",
  "commandId": "523ef590-ce4d-4901-91b3-79de1bfe7e35",
  "bootId": "c85743d5-1b72-41b8-8500-ae3c102b27d6",
  "eventSequence": 89,
  "status": "completed",
  "revision": 45,
  "compartment": 4,
  "physicalVerification": "unavailable",
  "requiresReload": true
}
```

`completed` potvrzuje dokončení naprogramovaných výstupů, **nikoli změřenou polohu přihrádky či přítomnost hůlky**. V návrhu nejsou koncové snímače. UI zobrazí „Sekvence dokončena, zkontrolujte přihrádku“, nikoli „Otevření ověřeno“.

### Příkazy a události

| Typ | Význam a omezení |
| --- | --- |
| `state.get` / `state.snapshot` | Úplný stav: boot, revision, fáze, activeCommandId, readyMask, chyby, stav mikrofonu, capabilities, calibrationRevision, poslední výsledek. Bez pohybu. |
| `control.acquire/release` | Explicitní přepnutí mezi samostatným a ručním vlastníkem, pouze v bezpečném klidu. Snapshot obsahuje `controlMode: standalone/manual/recovery` a ruční lease nebo null. |
| `conversation.text/cancel` | Textový alternativní vstup nebo zrušení do conversation slice ESP32; text nikdy neobchází potvrzení. |
| `selection.start` | Jedna přihrádka, jen `Ready`, platná kalibrace a dostupná hůlka. `expectedRevision` chrání před potvrzením starého stavu. |
| `safety.stop` | Vysoká priorita, z každého stavu, platný klient. Nepodléhá `expectedRevision` ani běžné frontě. Opakování bezpečné. Zablokuje pohony do samostatného zotavení. |
| `safety.reset` | Jen po odstranění chyby a kontrole obsluhou, všechny výstupy vypnuté. Žádný automatický servo pohyb při resetu. |
| `inventory.confirmReload` | Potvrzení mechanického založení konkrétní přihrádky, jen v klidu; mění masku/revision, nehýbe servem. |
| `voice.capture.start/stop/cancel` | Jedno streamId, 30 s limit, jen při aktivním sezení firmware. Zdroj kufr nebo telefon; žádný příkaz západce. PTT události tlačítka patří sem. |
| `audio.play/stop` | Konkrétní ověřené audioId, omezený formát/délka, bez URL od AI. |
| `heartbeat` / `heartbeat.ack` | Obnova lease, monotónní čas a revision. |
| `selection.progress`, `fault.raised`, `button.changed`, `audio.ended` | Události s bootId a rostoucím eventSequence; mezera v pořadí vyvolá snapshot. |

Servisní kalibrace bude samostatná fyzicky odemčená funkce s lokálními omezeními; produkční protokol nemá univerzální `setServoAngle`. Firmwareový serial `select` v produkčním profilu vypnout nebo vést přes stejný autorizovaný servisní vstup, aby neobcházel bezpečnostní pravidla.

`inventory.confirmReload` používá stejnou autorizovanou obálku jako `selection.start` (`commandId`, boot, manual lease, sequence, revision a deadline); payload obsahuje právě `compartment` 1–6 a `inspectionConfirmed: true`. Sdílí čítač a 64 výsledků, takže stejné ID s jiným typem nebo obsahem je konflikt. Po kontrole v bezpečném klidu trvale nastaví bit dostupnosti před `command.ack` a pošle snapshot, bez spuštění fyzické sekvence. Jeho journal aktualizace masky je samostatná operace, ne falešné uzavření transakce výběru. Hostitelský souborový journal je pouze fake; skutečná NVS atomicita čeká na E4.

`conversation.text` má odlišnou textovou obálku: `messageId`, `deviceId`, `bootId`, manual `controlLeaseId`, `conversationId`, jednorázové `clientTurnId`, `generation`, `expectedRevision` a text 1–1000 znaků. Zpráva je výjimkou z 2048bajtového control limitu do 8192 bajtů UTF-8. `conversation.cancel` nese stejný device/boot/lease/session kontext a očekávanou `generation`; vlastní conversation slice při přijetí zvýší generation a zahodí starší výsledek. Textová zpráva nikdy neobsahuje číslo aktuátoru nebo povel k výběru.

Snapshot lokálního protokolu v1 nyní vyžaduje `conversation:{conversationId,generation,activeTurnId}`; identitu založí kufr, telefon ji čte ze snapshotu a nepřiděluje si vlastní. E2.8b2 přidává `conversation.ack` s `requestMessageId`, stavem přijetí a chybou a `conversation.result` s identitou tahu, generací a odpovědí. Opakování stejného cancel `messageId` a obsahu v aktivním manual lease vrátí duplicitní přijetí bez dalšího zvýšení generation; stejný ID se změněným obsahem je konflikt. Dosavadní přednasazovací fixtures snapshotu se doplnily o tato pole; budoucí klient musí číst snapshot před textovým vstupem. Hostitelský simulátor vrací výslovně `simulated:true` a pevnou odpověď bez backendu/AI; tato větev nevytváří kandidáta k výběru. Skutečný výsledek backendu bude před přijetím potřebovat úplné echo identity a revision podle kapitoly 4, a místní autonomní bránu dle D75.

### ACK, opakování a neznámý výsledek

- Cíl ACK do 200 ms na LAN, čekání 500 ms. Při ztrátě ACK nejvýše dvakrát opakovat **stejný příkaz, commandId, sequence i obsah**, po 500 a 1000 ms od prvního odeslání; stále musí platit lease a deadline. Nové ID při retry je chyba.
- Firmware zpracuje příkaz atomicky v jednom vlastníkovi stavu: ověřit auth/boot/lease → rozpoznat duplicitu → validovat nový příkaz → zaznamenat přijetí → ACK → výstupy. Stejné ID s jiným obsahem = `id_conflict`. Duplicita vrací uložený stav, nespustí sekvenci znovu.
- Za lease vede firmware rostoucí `sequence` a high-water mark; opakování starého čísla se nikdy nestane novým povelem ani po vytěsnění cache. Uchovat posledních 64 výsledků v RAM. Starý příkaz mimo cache vrátí `already_seen_result_unavailable`; nový příkaz s vynechaným pořadím se odmítne `sequence_gap`, klient načte snapshot. Čítač se přiděluje v jediném odesílacím místě.
- Před prvním fyzickým výstupem zapsat malý NVS journal `{commandId, compartment, inProgress}`; při úspěchu jej uzavřít. Selhání zápisu = žádný pohyb. Při bootu s neuzavřeným záznamem `RecoveryRequired`. Neukládat heartbeat ani každý pulz do flash.
- Přijetí není dokončení. Celkový limit fyzické prezentace 15 s (historický sériový prototyp 5 s); při překročení řídicí úloha zastaví výstupy. Telefon po 16 s bez terminální události žádá `state.get`, automaticky neposílá nový výběr.
- Po přerušení spojení nelze z timeoutu dovodit, zda se západka uvolnila. UI drží `OutcomeUnknown`, vyžádá snapshot a nabídne kontrolu obsluhou. **Žádný retry fyzického příkazu přes nový lease nebo boot.** Ruční nový pokus až po kontrole a explicitním zotavení, s novým ID.
- Fronta výběrů má kapacitu nula při běžící sekvenci: druhý požadavek = `busy`, nikoli odložené otevření. Stop předbíhá média i běžné příkazy; při přeplnění control fronty bezpečně zastavit a zavřít session.

Pravidla retry/lease výše platí pro síťové ruční povely. Místní tlačítkové potvrzení má stejnou bránu validace, NVS journal a jedinou sekvenci; nepotřebuje síťové ACK ani cloudový souhlas k dokončení. Platná autentizovaná obálka s očekávaným sequence spotřebuje pořadové číslo i při obchodním odmítnutí (například busy), výsledek se zapamatuje. Neautorizovaná/rozbitá obálka číslo nespotřebuje. Read-only state.get a safety.stop mají vlastní messageId bez navazování na číselnou řadu výběrů, aby mezera nemohla zablokovat zotavení nebo Stop.

Chybové kódy minimálně: `invalid_message`, `unsupported_version`, `unauthorized`, `controller_busy`, `wrong_boot`, `lease_expired`, `expired`, `stale_revision`, `sequence_gap`, `id_conflict`, `busy`, `compartment_unavailable`, `not_calibrated`, `safety_latched`, `i2c_timeout`, `journal_failed`, `outcome_unknown`.

D97 rozšiřuje `conversation.result` v1 na `simulated:boolean` a reply 1–500 Unicode znaků / nejvýše 2000 UTF-8 bajtů, v souladu s validovaným backendem. Simulátor zachovává true, skutečný backendový výstup má false; false označuje původ odpovědi, nikoli důkaz spuštění placené AI. Textové result zprávy mají stejně jako vstup limit 8192 bajtů kvůli JSON escapování, ostatní control zprávy 2048. Strict klient musí převzít rozšířené schéma/limity. Řídicí vlastník před výstupem kontroluje aktivní sezení/tah, generation a snapshot; dokončení až po přijetí do transportní fronty. Už odeslaný text nelze vzít zpět; cancel/Stop jej odliší novou generation. Předání textu telefonu není dokončení oznamovacího audio portu ani oprávnění k pohybu.

## 6. Backend, AI a bezpečný převod na hůlku

### Modely a dodavatel

Pro vývojový textový dialog zvolit OpenAI GPT-6.1 Sol s reasoning effort low jako kvalitní výchozí bod; po živém testu Luny majitel výslovně zvolil pouze Sol: Luna navazovala na obsah, ale humor zůstával popisný [O22]. Volba pro akci bude vycházet z české kvality, bezpečných odmítnutí, latence a skutečných tokenů, ne pouze z názvu nebo ceny. Vývojový test v ../ai-eval/ běží na syntetických textech bez device identity a bez fyzických výstupů; nepředstavuje dokončený E8 adaptér ani akceptaci. U modelů zaznamenat skutečně vrácený identifikátor a před změnou aliasu zopakovat evaluaci. Responses API používá přísné Structured Outputs a store:false, bez background mode a bez serverových Conversations/Agents objektů.

Pro hlasovou větev ponechat gpt-transcribe pro STT a gpt-4o-mini-tts-2025-12-15 s hlasem marin pro TTS [O7, O8, O19]. Textový model Sol nejsou náhradou samostatného audio adaptéru. Před reálnými návštěvnickými daty potvrdit region a retenci. Samotný projektový API klíč patří pouze do backendového secret store, nikdy do firmware/mobilu.

Alternativy: Azure-hostované modely zvážit při požadavku na konkrétní firemní identitu/region a již existující správu Azure; přidávají konfiguraci deploymentů a jejich dostupnost je nutné ověřit zvlášť. Jiný cloudový dodavatel může vyhrát českým hlasem/cenou, ale znamená další datové podmínky a adaptér. Vlastní lokální STT/LLM/TTS na notebooku dává plný offline režim, přidává však hardware a výkonové testy. Nyní zvolit jednoho dodavatele, tři úzké adaptéry, žádnou automatickou síť náhradních poskytovatelů.

### Katalog a prompt

**D143 — autonomní Live varianta.** Katalog z D142 zůstává jediným zdrojem
ID/jmen/zdrojových SHA. Samostatný `ollivander-trunk-live-persona.txt` je
adaptace pouze pro tuto personalitu; party používá vlastní identitu bez
starého povinného serverového psaní replik. Při create se zmrazí také
`LivePromptSnapshot` s verzí a odvozeným hashem; provozní změna jej nevymění.
Výslovné `Backend:GptLive:ConversationPolicy` má jednotnou přednost před
výchozí politikou v promptu i rozhodování. Otázkové kandidáty jsou časovaná
aplikační projekce, nikoli přesný sémantický důkaz či počet delegací.
`WandDecisionService` přijímá skutečné validní kandidáty, opravy a reference
odpovědí. Vrací faktický návrh/needs_information/no_available, revize a
důvody bez `Reply`; Live jej formulací sdělí samo. Prázdná dostupnost není
modelové volání. Potvrzení přehrání, znovu ověřený snapshot a platná revize
jsou podmínkou návrhu pro zařízení; fyzická akceptace zůstává místní.

Hybridní paměť rozšiřuje EveningMemoryStore nedestruktivním schema 2.
Rozsahy Conversation, EveningMotif a Personal se nesměšují. Legacy data
nemají ověřenou identitu/původ a neexportují se automaticky. Osobní paměť
vyžaduje serverem ověřenou vazbu a dlouhodobé povolení, export samostatné
povolení. Revizovaný zápis + outbox jsou jedna transakce; oprava/mazání
vytvoří okamžitý místní tombstone, cloudový úklid odstraní asociaci i vlastní
Files soubor. Přímé REST search používá serverové `filters`, pak SQLite
revalidaci FileId/revize/oprávnění. Index nemění dostupnost či mechaniku.
Provisioning je explicitní CLI dry-run / execute, nikdy vytváření stores
při startu. D144 používá toto CLI také v ručním production workflow nad
trvalým provisioning journalem runneru; neobchází soukromou paměťovou DB
služby ani nevkládá hosty. Deployment převezme ověřená ID do release
konfigurace bez rozšíření sudo. Context budget je výchozí 500 konzervativních tokenů vybrané
paměti; nejsou zvýšené dosavadní konverzační limity ani změněný model.
Podrobnosti a připravené akceptace jsou v [D143 provozním návodu](autonomous-live-memory.md).

**D142, 5. 10. 2026 — inventář personalit na výslovné zadání majitele.**
Dosavadní `voice-persona.txt` zůstává výchozí `party-original`, beze změn.
Druhý zdroj `ollivander-trunk-persona.txt` je dodaný text bez úprav.
[Inventář](../ai-eval/personalities.md) určuje ID a debug postup. Správce
přepíná v iOS Debug → Server přes rozšířené `/v1/admin/modes`; metadata
katalogu se načítají ze serveru. Volba platí globálně pro nový rozhovor
ESP i telefonu, při zakládání se zmrazí a přežije restart v SQLite
`server_settings` (schema 7). Existující klient bez `personalityId` volbu
zachová; starší server nová aplikace rozpozná a přepínání zablokuje.

Původní politika čtyř povinných otázek a otevřeného úvodu platí jen pro
původní personalitu. Nová používá přesný dodaný prompt: obvykle dvě
zodpovězené otázky, dříve při dostatku podkladů/žádosti; server vynucuje
úvodní otázku a dovolí výběr od první odpovědi. Sol, Realtime a GPT-Live
přebírají stejné zvolené znění, bez přimíchání první personality. Pravidla
funkcí a reálného katalogu zůstávají oddělená. Delší prompt zvyšuje pouze
explicitní přírůstek limitu requestu a předběžný účetní odhad. Peněžní
limit určuje OpenAI účet; fyzická bezpečnost se nemění. Změna mezi rozhovory vyžaduje admin
credential a aktuální revizi, nedovolí změnu během provider volání a sama
AI nevolá. Debug používá existující izolovaný test: fake pro průchod/politiku
otázek, AI pro poslech charakteru. Nasazení backendu a nová IPA jsou
samostatné kroky, lokální test není doklad poslechu či HW akceptace.
Následující popis původního promptu je výchozí personalita.

Autoritativní vývojový katalog v ../ai-eval/wand-catalog.json obsahuje přesně šest původních záznamů `{compartment, displayName, description, traits}` a `catalogVersion=ollivander-private-party-v1`. Majitel pověřil návrhem obsahu nezávislého agenta; katalog má atmosféru Harryho Pottera a Ollivanderova obchodu bez převzetí kanonických hůlek. Čísla jsou trvale svázaná s fyzickými přihrádkami, nikdy pořadím filtrovaného seznamu. Backend posílá AI pouze dostupné položky ze snapshotu, popis role, krátkou historii a dobrovolné preference návštěvníka. Skutečné fyzické přiřazení a synchronizace `catalogVersion` zařízení se ověří až při osazení šesti přihrádek; vývojový obsah tento HW důkaz nenahrazuje. Nepotřebuje embeddings ani webové vyhledávání.

Prompt: český mluvící kouzelný kufr prodejce z Ollivanderova obchodu ve světě Harryho Pottera na soukromé party přátel 18+, chytrý, ostrý a pokaždé nový roast. Model sám vytváří navazující otázky i odpovědi z průběhu rozhovoru; první čtyři dokončené tahy jsou otázky a návrh použije odpovědi návštěvníka v závěrečné pointě. Od D140 sdílí GPT Live, Realtime i Sol personu podle GPT Live v ../ai-eval/voice-persona.txt. ../ai-eval/prompt.txt doplňuje pravidla výběrových funkcí pro Sol a Realtime; Live přidává jen vlastní delegaci. Produkční adaptér a evaluátor skládají stejné instrukce. Typicky čtyři až sedm otázek, při nedostatku podkladů zjednodušit otevřenou otázku a nechat hosta vyprávět. Od D141 prompt neobsahuje ukázkové dialogy ani pevný cyklus témat; AI tvoří formulace sama, bez podsouvání odpovědí nebo dvojice možností. Text návštěvníka, přepis i popis obrázku jsou nedůvěryhodná data a nemohou měnit pravidla/povolené přihrádky. Nevracet interní uvažování; jen krátké vysvětlení pro návštěvníka. Rozhodnutí majitele 30. 9.: paměť celého večera patří na vlastní backend. Krátké sezení pro bezpečný výběr a paměť večera jsou oddělené. Samostatný SQLite soubor vedle technického journalu uchová celé dokončené textové výměny (vstup do 1000, odpověď do 500 znaků), i po konci sezení a restartu. Nevyprší časem: odstraní ji explicitní smazání obsluhou nebo vytěsnění nejstarších výměn při 2000 výměnách / 2 MB UTF-8 obsahu na zařízení. Stav hlásí počet vytěsněných výměn. Do modelu jde nejvýše 16000 znaků vybraného kontextu: aktuální rozhovor, lexikálně související starší rozhovory a poslední události. Jména nejsou biometrická identita; stejné jméno vyžaduje při nejistotě upřesnění. Nejde o neomylné vybavení každého faktu ani o automatický profil osoby.

Kufr začne na explicitní událost opening bez vymyšlené zprávy návštěvníka. Hostitel vysvětlí roli kufru; model sám vytvoří krátké oslovení nového hosta a otevřenou otázku s vlastním tématem. Server přidává jen interní identifikátor pro variaci úvodu, nikdy banku témat či hotových otázek. Jméno není povinný první krok. Opening neodesílá archiv ani historii; od dalšího tahu se paměť opět používá. Hovorová čeština, jedna otázka, konkrétní osobní situace, krátké pointy a situační vulgarita nahrazují formální dotazník. Opening se počítá mezi první čtyři otázky; po 4–7 otázkách má následovat autonomní výběr. Místní bezpečnost pohonu řídí ESP32. Model dostává statický sdílený prompt a strukturovaný výsledek, reasoning low; vyšší effort není automaticky lepší humor. Sol hodnotit na adaptivních syntetických návštěvnících a lidsky hodnotit celý přepis. Celkový schválený rozpočet tohoto srovnání je 1 USD při zachování 10 USD/měsíc.

Základní JSON požadavek má limit 14 kB; paměť přidá pouze vlastní JSON-encoded počet bajtů. Rezervace Sol 0,10 USD se před odesláním zvýší o konzervativní cenu přidaných bajtů (nejvýše jeden token na bajt při sazbě cache-write Fast). Požadavek výslovně volí `service_tier: default`; jiný nebo chybějící tarif odpovědi se odmítne a drží celou rezervaci. Dvojnásobná pojistka kryje neočekávanou sazbu Fast, potvrzený Standard se přepočítá podle tokenů. Od 9. 10. 2026 se peněžní session/daily/monthly stropy nevynucují; součty slouží pouze auditu. Dvě nedávné výměny v RAM doplňují výběr z trvalé paměti. Samostatný obsahový soubor není součástí technických záloh. Po smazání se změní eveningId, zruší se aktivní sezení a pozdní odpověď starého večera nesmí paměť obnovit. Smazání konkrétního starého eveningId je idempotentní a nikdy nesmaže nový večer.

### Strukturovaný výsledek

Backend: Responses function calling, `tool_choice: required`, `parallel_tool_calls: false`, `strict: true`; samostatný obsahový eval zatím používá `text.format` typu `json_schema`. schéma všech objektů `additionalProperties: false`. Všechny klíče jsou required, nepoužitý návrh je explicitní `null` [O9]. Normalizovaný vnitřní výsledek funkcí (nikoli přímý povel pohonu):

```json
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "reply": { "type": "string" },
    "decision": { "type": "string", "enum": ["ask", "propose", "no_selection"] },
    "compartment": { "type": ["integer", "null"], "enum": [1, 2, 3, 4, 5, 6, null] },
    "reason": { "type": "string" }
  },
  "required": ["reply", "decision", "compartment", "reason"]
}
```

Backend dodatečně vynucuje: `reply` 1–500 znaků, `reason` do 200 znaků, `propose` vyžaduje dostupné číslo 1–6, jiné decision vyžaduje `null`. Schéma samo není obchodní validace ani důkaz správného výběru. Refusal, nekompletní/truncated výstup a nevalidní JSON znamenají `AiUnavailable`, žádný návrh a žádné aktuátory. Není zde parsování čísla z volné věty, regex nad odpovědí ani opravování neplatného čísla na nejbližší platné.

Backend vystaví ověřený návrh s vlastním `proposalId`, `conversationId`, `turnId`, `catalogVersion`, `deviceRevision`, `compartment`, `reason`, `bootId`, `generation` a `expiresAt` (60 s). Firmware ověří shodu s posledním požadavkem, sezením a fyzickým snapshotem; navíc zavede monotónní lokální deadline nejvýše 60 s a nepřijme výsledek operace starší než 45 s. Návrh oznámí hlasem a zvýrazní LED. Místní autonomní brána přijme nejvýše jeden aktuální návrh a zahájí omezenou prezentaci. Rozpad spojení před přijetím kandidáta zneplatní; přijatou místní sekvenci neřídí WAN.

Volitelný telefon zobrazí velké číslo/název a akce „Změnit hůlku“ a „Spustit výběr“. Ruční převzetí ruší původní návrh; opravený výběr se znovu potvrzuje jako explicitní ruční příkaz. Před odesláním aplikace znovu porovná dostupnost a revision. Žádná změna v UI ani další AI replika se nepovažuje za potvrzení pohybu.

AI při návrhu mluví „Navrhuji hůlku číslo 4“, nikoli „Právě jsem otevřel čtyřku“. Číslo v oznámení musí být deterministicky svázané s validovaným polem compartment: backend vytvoří samostatnou oznamovací větu, neodvozuje ji z volného reply. Firmware má lokální hlášky pro čísla 1–6 a potvrzení. Finální slavnostní repliku přehraje z **validované** volby, aby při ruční opravě nezaznělo původní číslo. Chyba TTS nesmí změnit vybranou přihrádku ani obejít místní bezpečnostní bránu.

### HTTP API v1

Každý endpoint ověřuje device access token a vlastnictví sezení; ID samo neopravňuje ke čtení. Jeden probíhající tah na zařízení/sezení. Pro mutace je `Idempotency-Key` UUID, spojený s deviceId, routou a hashem těla. Opakování vrací stejnou operaci; jiné tělo pod stejným klíčem = 409. Telefon toto API běžně nevolá: i text/záložní hlas pošle lokálně do ESP32, které používá stejné device sezení.

| Endpoint | Request → response |
| --- | --- |
| `POST /v1/device-access/token` | Unikátní device credential v Authorization → access token 15 min s device scope. Revokace/registrace pouze servisní správou, bez veřejného endpointu pro volné přidávání zařízení. |
| `POST /v1/conversations` | `{deviceId, bootId, generation, catalogVersion, locale:"cs-CZ", consent:{audio,image}}` → `{conversationId, expiresAt, limits}`. Souhlas není odvozen z párování. |
| `POST /v1/conversations/{id}/transcriptions` | Multipart WAV + clientTurnId + bootId/generation + deviceRevision → 202 `{operationId}`; po dokončení transcript a source. Revize je nutná pro požadované echo výsledku. ESP32 pokračuje do textového tahu, v režimu asistovaného přepisu čeká na opravu telefonu. |
| `POST /v1/conversations/{id}/opening` | Stejná korelace/snapshot jako turns, bez text; vytvoří první modelovou otázku jako pollovatelnou turn operaci. Opakování je idempotentní, nový opening po přijatém tahu = 409. Firmware musí tuto událost vyvolat až při vědomém zahájení, nikoli automaticky po reconnectu. |
| `GET /v1/evening-memory` | Device-scoped eveningId a počty/limity, nikdy celý obsah v diagnostice. |
| `DELETE /v1/evening-memory/{id}` | Prázdné tělo, device token a Idempotency-Key; smaže jen očekávaný večer daného zařízení a ukončí aktivní sezení. Stará/cizí ID jsou neškodné 204 bez změny aktuálního večera. |
| `POST /v1/conversations/{id}/turns` | `{clientTurnId, bootId, generation, text, readyMask, deviceRevision, catalogVersion}` → 202 operace; výsledek obsahuje validovaný reply a případný proposal. |
| `GET /v1/operations/{id}` | Stav `pending/running/completed/failed/cancelled/unknown` a typovaný výsledek/chyba. Polling po 500 ms, po 3 s každou 1 s; max. jeden dotaz současně. |
| `DELETE /v1/operations/{id}` | Zruší zájem klienta a cancellation token; neslibuje, že již odeslané AI volání nebude účtováno. Pozdní výsledek se nepublikuje. |
| `POST /v1/conversations/{id}/speech` | `{turnId, outputTarget}`; text se bere z validované odpovědi, ne z libovolného klientského řetězce → operace s audioId. |
| `GET /v1/audio/{audioId}` | Autorizovaný WAV/PCM obsah s metadaty, TTL 120 s, bez veřejného URL a bez dlouhodobé cache. |
| `DELETE /v1/conversations/{id}` | Zruší probíhající operace a odstraní přepis/média z aplikační paměti. |

OpenAPI kontrakt E2.6 v `contracts/backend-openapi-v1.json` konkretizuje všechny základní HTTP trasy v tabulce. Token endpoint používá samostatné bearer schéma pro servisně provisionovaný device credential; ostatní trasy krátkodobý device access token. Odpověď tokenu obsahuje `accessToken`, `tokenType: "Bearer"`, `expiresInSeconds: 900` a `deviceId`. Backend při založení sezení ověří shodu `deviceId` v těle se scope tokenu a u všech následujících ID vlastnictví sezení/operace/audio. Odpověď sezení vrací `limits` s počtem tahů, délkou textu a TTL. Stav operace obsahuje echo identit, generace a revize, typ `transcription | turn | speech`, stav, příslušný typovaný výsledek nebo chybu; `propose` má povinný návrh a ostatní rozhodnutí jej nemají. Transcription multipart pole `wav` je PCM16 mono 32 kHz nejvýše 30 s; `source` rozlišuje `deviceMicrophone | phoneMicrophone`. Speech `outputTarget` je právě `device | phone`, nikdy obojí, a přijímá ID předchozího tahu bez libovolného textu. Audio odpověď je `audio/wav` PCM16 mono 32 kHz s `no-store`; důvěryhodný vývojový certifikát, kontrola WAV bajtů a runtime autorizace zůstávají povinností implementace. Tento HTTP kontrakt nemění lokální WSS kontrakt.

D157 konkretizuje původní návrh snapshotu: `POST /v1/conversations/{id}/image`
přijímá jeden metadata-free JPEG do 256 KiB, nejvýše 640 × 640, se
samostatným `consent.image: true`, před úvodní replikou. Klient překreslí
telefonní snímek před uploadem; firmware snímá 640 × 480. Backend odmítne
EXIF/GPS, komentáře a náhledy. Vyžaduje UUID `Idempotency-Key`, `Client-Turn-Id`,
`Boot-Id`, nezáporné `Generation`/`Device-Revision` a `Catalog-Version`.
Vrací 202 a operaci typu `image`; běžný polling vydá stručný popis,
možnou fiktivní postavu, nejistotu, zdroj a dobu platnosti. `PUT .../costume-context`
vrací synchronní obálku operace `costume` pro ruční popis do 240 znaků,
s předností před obrazovým výsledkem; prázdný popis kontext vymaže.
Osm korekcí je technická mez RAM, ne peněžní budget. Obraz a lokální
vizuální kontext jsou krátkodobé (120 s), ne archivační paměť; běžný
textový přepis zůstává zachovaný. Již předaný Live kontext může přetrvat
do ukončení provider spojení. Rozšíření nezískává oprávnění pro aktuátory.
REST chyby jsou ProblemDetails se stabilním `code`, `traceId`, bez promptu
nebo klíče: 400 formát, 401 přihlášení, 403 scope, 409 souběh/verze,
413 velikost, 429 technický limit, 502/503/504 poskytovatel.

Lokální v1 kontrakt rozšiřuje `voice.start` o volitelné `imageConsent`
(výchozí false), `voice.status` o `cameraAvailable` a `photoWarning`
(null/`photo_unavailable`/`photo_failed`) a přípravné fáze
`photo_capture`/`photo_upload`/`photo_wait`. Staré zprávy zůstávají platné.
Příznak `wand_hw/camera_verified` z existující servisní konfigurace
je výchozí false a vyžaduje skutečnou kontrolu modulu/pinů/souběhu.
Bez kamery či při chybě fotografie hlas pokračuje s varováním.

Operace je před placeným voláním transakčně zapsána do SQLite včetně rezervace nákladů a idempotence; dočasný citlivý obsah zůstává v RAM, dokončené textové výměny se ukládají do samostatné paměti večera podle §6. Po restartu backendu neznámé rozběhnuté operace označit `unknown`, bez automatického znovuúčtovaného volání. Záznam idempotence bez obsahu držet 24 h. Ztracený citlivý výsledek vrací `result_expired`, nikoli tichou regeneraci. Pro jednu instanci stačí omezená in-process fronta; přetížení odmítnout, neakumulovat rozhovory.

## 7. Text, hlas, média a offline režimy

**Upřesnění pokračování 1. 10. 2026 (D106):** majitel požaduje připravit AI hlas
ještě bez fyzické desky. E9 se proto implementuje po nezávislých softwarových
částech: backendové STT/TTS adaptéry a jejich omezené asynchronní operace lze
ověřit proti syntetickému HTTP provideru po E7/E8. Příprava nevyžaduje E5.
Následují firmwareové upload/download a poloduplexní model s fake audio porty.
Skutečný I2S/PTT/reproduktor, fyzická prezentace a akceptace celé E9 nadále
vyžadují E4/E5/B1. Původní závislost celé etapy není zákazem přípravy jejího
nezávislého softwaru; kritéria dokončení E9 se nesnižují.

### Průběh samostatného hlasového tahu

**Večery a uložený přístup D112 (zadání majitele 1. 10.):** iOS Debug nabízí
pojmenovaný nový večer a přepnutí aktivního večera pro vlastní debug identitu.
Nový večer je prázdný, předchozí zůstává uložený. Existující SQLite ID, text
a počty migrace zachová transakčně. Kontext i limity 2000 výměn / 2 MB platí
pro konkrétní večer; nejvýše 100 večerů na zařízení, název 1–80 Unicode znaků
bez řídicích znaků. GET `/v1/evenings` vrací pouze metadata a aktivní ID.
POST `/v1/evenings` a PUT `/v1/evenings/{id}/active` vyžadují device token,
UUID Idempotency-Key a očekávané aktivní ID; CAS/busy konflikt vrací 409.
Živé sezení nebo doznívající provider daného zařízení změnu blokuje.
Trvalá idempotence platí 24 h, nejvýše 1024 neexpirovaných změn na zařízení;
replay nesmí znovu aktivovat starý ukazatel. Native po mutaci čte nový stav.
Legacy DELETE maže jen aktuální večer, ostatní archivy zachová.
Credential a poslední URL/ID vlastní samostatný nesynchronizovaný Keychain
`WhenUnlockedThisDeviceOnly`, oddělený od ESP párování; stabilní Bundle ID/tým
zachová přístup aktualizace. Pozadí maže RAM a ruší test, odemčení profil obnoví.
Výchozí URL `https://2.28.202.220/`, ID `debug-server-admin`. Secure pole zůstává
prázdné; ponechat credential lze jen pro stejný host/port a ID. Změna cíle
vyžaduje nový credential. Explicitní zapomenutí maže jen Debug profil.
Starší RAM-only verze nemá co migrovat: první uložení musí udělat obsluha.
Uložení mezi procesy a macOS CI nejsou důkazem aktualizace fyzického iPhonu.

**Interaktivní diagnostika D110 (zadání majitele 1. 10.):** výjimka D108/D109
zahrnuje celý virtuální rozhovor s textovými a mikrofonními odpověďmi telefonu.
Obsluha před začátkem nastaví simulovanou masku 0–63, výchozí 63; během sezení
se nemění. Nejde o fyzický snapshot. Native klient ověřuje korelaci tahu i návrhu,
platnost a dostupnost přihrádky, zobrazuje návrh a nikdy neposílá pohybový povel.
Pouze fake dialog identity `debug-server-admin` provede čtyři otázky a simulovaný
výběr; ostatní fake identity zachovávají dosavadní chování. Sol zůstává skutečným
dialogovým modelem. Fake STT vrací označený syntetický přepis.
Mikrofon po povolení iOS pořizuje nejvýše 30 s PCM16 mono 32 kHz do RAM;
odeslání je výslovná akce. Stop, pozadí a audio interruption nahrávku zahodí,
uklidí vlastní backendové sezení a zneplatní pozdní výsledky. Přepis se zobrazí
jen přechodně, log neobsahuje návštěvnický text. Backendová paměť večera se
ukončením sezení nemaže. Volby speech `voice=cedar|marin|onyx|coral` a konečná
`speed=0.85–1.25` jsou volitelné; starší klient má marin/1.0. Debug nabízí
cedar/1.1. Provider model i nákladové rezervace zůstávají pevné.
Native HTTP přijímá WAV omezeně po blocích mimo hlavní vlákno a přehrávání
začíná před úklidem audio operace. Měření zvlášť uvádí dialog, TTS a stažení;
nejde o streamování přehrávání před dokončením provideru. Skutečná česká
kvalita/latence, mikrofon iPhone a fyzické I2S zůstávají samostatnou akceptací.

**Diagnostická výjimka D108 (zadání majitele 1. 10.):** iOS Debug může nezávisle
testovat backendový opening → speech → WAV → reproduktor telefonu. Nejde o běžný
cloudový prostředník ESP: používá samostatnou provisionovanou identitu `debug-*`,
masku dostupnosti 0 a žádné místní řídicí povely/lease. Credential zadá obsluha
v zabezpečeném nativním dialogu; není v JS, IPA ani logu. D112 nahrazuje původní
RAM-only profil uložením do odděleného Keychain; pozadí stále ruší práci.
HTTPS zachovává systémové PKI a odmítá redirecty. Nové autentizované
`GET /v1/diagnostics` pouze čte provider režimy/katalog/audio formát, nic negeneruje.
Test výslovně odlišuje systémový hlas, fake tiché WAV a skutečné OpenAI TTS,
používá stávající limity/operace a uklízí jen vlastní sezení. Ztracená create
odpověď a DELETE se opakují nejvýše jednou se stejným klíčem a tělem; nevyřešená
nejistota vyžaduje expiraci testovacího sezení. Paměť testovacího večera se
automaticky nemaže. Bez nasazení backendu s D106/D108 a testovací identity není
živý AI test dostupný. Tato cesta nedokazuje audio kufru, GPIO ani fyzickou E10
akceptaci; mikrofon a STT test nejsou součástí D108.

**Provozní diagnostika D118 (zadání majitele 1. 10.):** server ukládá omezený
technický log do samostatné SQLite vedle journalu, výchozí historie 24 h/5000
událostí. Je to doplněk technického auditu podle §9, nemění jeho retenční
požadavky ani paměť večera. Zapisuje jen čas, pevný event/code, route template,
HTTP status/duration a serverové UUID požadavku/operace/rozhovoru; žádná těla,
query, credential, text/audio, prompty ani exception message. Úspěšný polling
GET/PCM se nezapisuje, Realtime fáze a chyby ano i uvnitř HTTP 200 streamu.
`GET /v1/admin/logs` nabízí tail, vzestupné kurzorové stránky, filtr úrovně a
UUID; `GET /v1/admin/logs/status` režimy/počty bez AI. Obě cesty vyžadují
admin credential nebo samostatný čtecí credential. Ten nezískává device token,
správu režimů ani rozhovor. Native Debug čte logy přes stejný Keychain/PKI
profil nezávisle na hlasovém toku; JS dostává jen typovaná metadata. Codex
používá stdio MCP s environment tokenem nebo GitHub read workflow se serverovým
secretem a jednodenním diagnostickým artefaktem. Žádná z těchto cest nevolá AI.
Změna logové DB invaliduje namespace kurzoru, restart historie zachová.
Nasazení/backend a podpis/publikace nové IPA zůstávají samostatné kroky.

**Servisní správa D109 (zadání majitele 1. 10.):** iOS Debug smí přepnout globální
režim dialogu fake/Responses Sol a hlasu fake/OpenAI STT-TTS přes nové
`GET/PUT /v1/admin/modes`. Jde o správu backendu, nikoli aktuátorů. Oprávnění
určuje samostatný credential správce s hash konfigurací na serveru; credential
ESP, místní párování ani běžný access token nestačí. Tento credential zároveň
provisionuje jen oddělenou `debug-server-admin` testovací identitu. Native profil
zachovává hranice D108 (PKI, bez redirectu/tajemství v JS a IPA); uložení profilu
do Keychain mění výslovné zadání D112.
Změna vyžaduje aktuální revizi a žádný živý rozhovor/doznívající provider;
create a přepnutí sdílejí zámek. Platí do restartu, pak rozhoduje explicitní
konfigurace nasazení. Přepnutí samo nic negeneruje. Fake hlasový test musí ověřit
fake dialog i hlas po získání sezení, aby souběžná změna nikdy nezpůsobila
placené volání. Zadání zahrnuje nové serverové nasazení a Ad Hoc publikaci
aktuální IPA na stávající instalační stránku; neprokazuje instalaci na iPhone
ani fyzický hlas/pohyb kufru.

1. Po fyzické kontrole a aktivaci samostatného režimu kufr přehraje informaci o cloudovém zpracování a nabídne PTT. Teprve vědomý stisk začne nahrávat; firmware vytvoří sezení backendu a drží jeho identifikátory. Žádný telefonní lease se nevyžaduje.
2. Nahrává právě jeden zdroj: mikrofon kufru **nebo** telefonu. PTT se uvolněním ukončí; automatický strop 30 s, minimálně 300 ms. Nahrávání má zřetelnou indikaci na kufru i telefonu, samotné podsvícení tlačítka napájené napevno ji nenahrazuje.
3. ESP32 sestaví WAV v PSRAM a odešle jej přímo backendu. V samostatném režimu automaticky po přepisu pokračuje textovým tahem; špatnému porozumění lze oponovat další replikou. V asistovaném režimu telefon přepis ukáže a pošle opravený text přes ESP32. Přepis nikdy není příkaz fyzického ovládání.
4. Po úplné validované odpovědi ESP32 vyžádá TTS, stáhne omezený PCM obsah a přehraje jej. Telefon, je-li připojen, zrcadlí text. TTS vzniká nad validovanou odpovědí, ne nad částečným JSON streamem. Výchozí výstup je reproduktor kufru; explicitní záloha telefon, nikdy oba současně.
5. Během přehrávání se audio mikrofonu neposílá. PTT může přehrávání zrušit; následuje vyprázdnění TX bufferu a alespoň 150 ms ticha před capture. Bez potlačení ozvěny nepřidávat full duplex.

### Jedno tlačítko bez telefonu

Chování je stavové, aby stejný stisk současně nenahrával a nespouštěl motor. `ListeningReady`: držení alespoň 300 ms nahrává, puštění ukončí; krátký dotek se ignoruje. Ve standalone režimu validovaný návrh přechází do autonomní prezentace bez dvojkliku. PTT nesmí být současně událostí startu pohonu; během prezentace má přednost Stop.

Během `Executing` libovolný debouncovaný stisk = okamžitý softwarový Stop, nikoli čekání na dlouhý stisk. V `RecoveryRequired` držení 3 s po kontrole obsluhou odemkne samostatný režim; neobnovuje starý návrh ani automaticky nepotvrzuje vrácení všech hůlek. V offline režimu krátký stisk cykluje připravené přihrádky se světlem a lokálním vyslovením čísla, držení 2 s potvrdí právě indikovanou volbu. Po startu offline menu vyžadovat nejprve puštění tlačítka. Nouzový elektrický vypínač je vždy samostatný.

**Servis po poruše / starý ruční reload (nikoli běžný cyklus pevné rekvizity):** obsluha vypne vnitřní PHYSICAL_ENABLE pohonů a v klidu podrží PTT 6 s, čímž vstoupí do `MaintenanceReload`. Krátký stisk vybírá jednu z přihrádek 1–6, lokální hláška a RGB ukazují číslo; po mechanickém vrácení hůlky a uzavření západky držení 2 s potvrdí právě tuto přihrádku. Firmware provede totéž jako inventory.confirmReload, bez jakéhokoli servo pohybu. Menu nepřijme vstup do režimu, pokud pohony nejsou fyzicky zakázané; PHYSICAL_ENABLE proto musí poskytovat stav i do firmware (A GPIO18, B MCP GPA3). Přepnutí enable zpět ukončí servisní menu a vyžádá samostatné 3 s potvrzení návratu. Alternativou je explicitní reload v aplikaci/USB servisním nástroji. Nikdy nepotvrzovat všech šest automaticky po restartu nebo jen otevřením menu.

I2S společné BCLK/WS pro ICS43434 a MAX98357A, oddělené DIN/DOUT, 32 kHz, dva 32bitové sloty, mikrofon čte vybraný kanál. RX převést ze znaménkových 24 bitů na PCM16 mono; síťový záznam 32 kHz = 64 000 B/s, nejvýše 1,92 MB za 30 s. Audio výstup OpenAI PCM má 24 kHz / 16 bit LE [O10]; backend jej deterministicky převzorkuje na 32 kHz před přenosem do kufru. Neprohlásit odlišné rychlosti za shodné a nepoužít prosté zahazování vzorků. Pro telefonní mikrofon nativní adaptér také normalizuje do WAV PCM16 mono 32 kHz. Webový vývoj může posílat fixture WAV; nevyžadovat stejný kodek MediaRecorderu v každém prohlížeči.

### Lokální binární protokol

Control `media.open` domluví `{streamId, direction, kind, format, sampleRateHz, channels, maxBytes, totalBytes?}`. `streamId` je uint32 přidělený firmware v rámci bootu. Datový WSS rámec má 16bajtovou hlavičku: ASCII `KF`, uint8 verze 1, uint8 druh (`1` PCM capture, `2` PCM playback, `3` JPEG), uint32 streamId, uint32 chunkSequence, uint32 payloadLength; vícebajtová pole little-endian. Maximální payload 4096 B; PCM blok běžně 20 ms = 1280 B. `media.end` obsahuje celkový počet bajtů a SHA-256 dokončeného obsahu. TLS zajišťuje integritu přenosu, hash ověřuje úplnost sestavení assetu. Neznámý stream, délka nebo mezera v pořadí = ukončení média, ne „nějak přehrát“.

E2.9b konkretizuje první směr pro místní `/v1/media`: po převzetí ručního řízení device pošle jen vlastnickému control socketu `media.authorize` s náhodným media ticketem; pozorovací snapshot ho nesmí obsahovat. Klient po samostatném WSS bearer handshake pošle `media.hello` s ticketem, bootem, platným manual lease a identitou/generací sezení ze snapshotu. Device vrátí `media.open` s vlastním `streamId`, `phoneToDevice`, PCM16 mono 32 kHz, stropem 1 920 000 B a počátečním creditem 16 KiB. `media.credit` uděluje další bajty až po přijetí bloků. Klient ukončí stream `media.end` s přesnou délkou a SHA-256, nebo `media.cancel`; výsledný `media.result` v hostitelském simulátoru nese `simulated:true` a žádný přepis ani návrh. Minimálně 300 ms znamená aspoň 19 200 B PCM; lichý počet bajtů je neplatný vzorek. Media socket běží odděleně od `/v1/control`, po Stop, odpojení či ztrátě lease nebo změně generation se zavře. Tento omezený fake příjem nemění samostatné HTTPS spojení ESP32 s backendem a neimplikuje hotové TTS/JPEG směry.

Žádný base64 PCM v control JSON. Pro první verzi nejprve přijmout celé TTS do max. 2 MiB v ověřené PSRAM, ověřit délku/hash a až potom povolit přehrávání. Capture používá interní DMA/ring buffer přibližně 200 ms a zapisuje do předalokovaného PSRAM bufferu do 1,92 MB, následně jej po blocích pošle HTTPS přímo backendu. Capture a playback sdílejí jeden časově oddělený buffer; neponechávat současně kopii WAV, PCM i base64. Při nedostatku RAM přehrát lokální chybu a zvolit text/telefonní fallback, nikoli restartovat během pohybu.

Výše uvedený mediální WSS kontrakt slouží pouze telefonu jako alternativní vstup/výstup. Má credit/flow control po blocích 16 KiB; klient neposílá bez volného místa. Při neúplném přenosu či 1 s bez pokroku médium zahodit. Kamera pořizuje jeden snímek pouze mimo capture/playback a motorovou sekvenci; kufrový snímek do cloudu opět odešle ESP32 přes HTTPS. Telefonní snímek nejprve projde lokálním uploadem do ESP32.

Výkonnostní brána: ověřená funkční PSRAM 8 MB, jeden audio buffer 2 MiB, jeden omezený JPEG buffer do 512 KiB až ve variantě B, TLS ve vlastní úloze a jen nezbytné sockety. Před každou alokací kontrolovat volné a největší souvislé bloky; DMA ponechat v paměti podporované konkrétním driverem. Po 100 hlasových tazích nesmí klesat volná paměť ani vznikat watchdog reset. Pokud selže souběh TLS serveru/klienta/kamery, nejdříve omezit kameru a mediální spojení, zachovat samostatný základní hlas.

MicroSD ukládá pouze předem připravené nouzové hlášky a technické assety s manifestem/hash, nikoli hovory nebo fotografie. Základní krátké hlášky držet v interní flash. Vložení vadné karty, plná karta nebo její vyjmutí znamená ztrátu volitelné funkce, ne restart pohonů. Čtení SD je omezené a s timeoutem.

### Výpadky a časové rozpočty

| Situace | Reakce |
| --- | --- |
| Internet/backend/AI chybí | Kufr přehraje lokální offline hlášku; tlačítkové offline menu nebo aplikace nabízí šest dostupných voleb. Žádná fronta hlasů k pozdějšímu uploadu. |
| STT chybí nebo je přepis chybný | Nahrávku smazat po dokončení/chybě, lokálně nabídnout nový pokus; telefon navíc nabízí opravu nebo text. |
| Dialog timeout/refusal/schema failure | Žádný proposal; možnost ruční volby a nové otázky. Nikdy náhodně neotevřít jako fallback. |
| TTS selže | Bez telefonu přehrát lokální chybovou hlášku. Platný návrh lze nabídnout pouze s úspěšně přehratelným lokálním oznámením jeho čísla; pokud ani to nefunguje, autonomní prezentaci zablokovat. Telefon může zobrazit text/číslo a umožnit explicitní ruční výběr. |
| Telefon chybí / pozorovatel se odpojí | Samostatný kufr pokračuje beze změny, pokud má Wi-Fi/backend. Odpojení telefonu není porucha autonomního řízení. |
| Telefon skončí během ručního ovládání | Po stop/disconnect nebo vypršení ručního lease vypnout výstupy a vyžádat kontrolu. Po návratu snapshot, neobnovovat výběr; návrat do samostatného režimu pouze explicitně. |
| Wi-Fi/LAN chybí | Rozpracovaný nepotvrzený AI návrh zrušit, přejít na lokální offline menu. Již místně přijatou samostatnou sekvenci dokončit pod místním řízením a časovými limity; není závislá na cloudu. |
| Router chybí | Fyzicky aktivovaný servisní SoftAP a pouze místní ruční režim; jinak mechanické ruční ovládání podle konstrukce. |

Výchozí síťové limity: TLS/connect 10 s; STT 15 s; dialog 12 s; TTS 15 s; celý tah po puštění PTT max. 45 s. UI ukazuje fázi a po 8 s nabídne text/ruční režim. Cíl měření na akci: medián odpovědi do 5 s a p95 do 10 s po konci řeči; nejde o garantované SLA služby. Backend opakuje nejvýše jednou explicitní 429/503 s `Retry-After`, pokud se vejde do společného deadline. Nejasný síťový timeout po odeslání placeného POST automaticky neopakuje; může již být účtovaný. Klient opakuje pouze svou idempotentní operaci/polling. Po třech selháních poskytovatele otevřít circuit breaker na 60 s, potom jeden zkušební požadavek.

## 8. Stavy a bezpečnost provozu

### Stavový automat fyzického řízení

| Stav | Povolený přechod / akce |
| --- | --- |
| `BootSafe` | OE HIGH ještě před sítí, signály motorů/serv vypnuté, žádné automatické hledání polohy. Kontrola NVS, kalibrace, sběrnice. |
| `RecoveryRequired` | Po každém zapnutí kontrola mechaniky obsluhou; neuzavřený journal zvýrazní neznámý předchozí výsledek. Samostatný reset + potvrzení připravenosti. |
| `Ready` | Self-test, kalibrace, alespoň jedna dostupná přihrádka a explicitní režim. V manual platný lease, ve standalone místní povolení a validovaný aktuální AI návrh. Lze přijmout právě jeden výběr. |
| Autonomní zdvih → prezentace → návrat | Přechody řídí čistá časovaná sekvence. Pohyby jsou omezené kalibrací; současně běží nejvýše jeden pohon. Starší `SelectionSequence` zůstává ruční vývojovou cestou. |
| `Completed` | Po úspěšném normálním návratu odpojit výstupy a obnovit dostupnost pevné rekvizity; emitovat výsledek. Bez snímačů jde o dokončený povel, nikoli změřenou polohu. |
| `Stopped` | Stop, ztráta řízení nebo timeout. Pohony ihned deaktivovat, výsledek `aborted`/`outcome_unknown`. Vyžaduje kontrolu obsluhou. |
| `Fault` | I2C, časové limity, kalibrace/journal, brownout. Bez automatického resetování chyby a bez automatického pokračování. |

V ručním režimu výpadek řídicího telefonu sekvenci zruší. V samostatném režimu výpadek WAN neovlivní již místně přijatou, lokálně běžící sekvenci; nepotvrzený návrh se zruší. Stop a porucha hardwaru zastavují oba režimy. Při přerušení během pohybu vypnout elektrický pohon, ale **nepokoušet se automaticky vrátit servo**; bez snímače je mechanický stav neznámý. Zvolená přihrádka zůstane nepřipravená. Odpojení proudu nedokazuje návrat přihrádky do spodní polohy. Mechanika musí být stabilní bez napájení a nesmí vyžadovat trvalé držení servem; to je podmínka prototypu.

**Mechanická revize D154 (8. 10. 2026):** fotografie majitele uvádí hůlku
19–20 cm, powerbanka je UGREEN PB720 / 25188 a osazují se dva reproduktory.
Pro snadnější tisk/montáž nahrazuje CAD původní souběžný zdvih na dvou
tyčích vyklápěným lůžkem na zadním M4 čepu, s původní servo pákou a jedním
tištěným táhlem. Přední konec se geometricky nadzvedne asi 31 mm; rekvizita
zůstává zajištěná v lůžku a normální návrat je řízený stejným servem.
Fyzický kontrakt vysunutí/prezentace/návratu se nemění, firmware nepřebírá
nové pulzy bez kalibrace. Pasivní zadržení se navrhuje třecím čepem, ale
není ověřené; výpadek v jakékoli poloze a tečení plastu zůstávají součástí
E5/B1. Společný zadní držák kamery a ESP zachovává krátký originální FPC,
mikrofon je vpředu mimo reproduktory. Pro rozvržení platí majitelem
doplněný [společný přehled rozměrů](../mechanika/rozmery-nakoupenych-soucastek.md).
Usazení rekvizity a kamerového FPC se ověří na prototypu. Původní CAD/export D126
zůstává historickou evidencí; montážní návod a nový zdroj jsou
v [mechanice](../mechanika/README.md). Žádné kritérium HW akceptace se tím
neuzavírá.

**Doručený kufr – rozhodnutí majitele 9. 10. 2026:** potvrzené vnitřní
rozměry jsou 17″ na šířku, 10,5″ na délku a 4,5″ od dna po horní okraj
spodní části, tedy 431,8 × 266,7 × 114,3 mm. Pro návrh používat přibližně
432 × 267 × 114 mm (šířka × délka × výška). Kufr se prezentuje otevřený;
veškerá mechanika a elektronika patří do spodní části, víko zůstává
prázdné a jeho prostor se do zástavby nepočítá. Příčná výztuha na dně
má podle majitele **5 mm**; další údaje o ní nejsou podmínkou rozvržení. Toto potvrzení
nahrazuje původní předpoklad prostoru kufru; CAD s obálkou
310 × 450 × 200 mm je nutné přizpůsobit a znovu ověřit před tiskem.

**Doplnění rozměrů majitelem 9. 10. 2026:** PTT 14×14×35 mm,
PCA9685 25×70×15 mm a U059 podle výrobce 32×24×8 mm. Servo používá
oboustrannou páku délky 35 mm s upevněním a osou uprostřed nebo
jednostrannou délky 20 mm s upevněním a osou na konci; nejde o rozměry
montážních uší těla serva ani rozteč otvorů. Powerbanka má potvrzené
rozměry **139×80×26,5 mm** a musí jít jednoduše vyjmout po uvolnění pásků
a odpojení kabelu, bez demontáže okolních dílů. Svorky jsou pořízené,
čekají na doručení a neblokují rozvržení. Další výšky mikrofonu/zesilovačů,
otvory reproduktorů a podrobnosti kolébek, LED či kabelů nejsou podmínkou
rozvržení. Údaje jsou pohromadě ve společném přehledu; fyzické usazení,
kalibrace a elektrická akceptace E5/B1 zůstávají samostatným ověřením.

Firmware musí mít samostatně testovaný `Abort` a bezpečný výstup, nikoli čekat na další běžný krok stávající sekvence. Motorový pulz má provozní limit 150 ms a tvrdý softwarový strop 250 ms, servo okno dle kalibrace 20–3000 ms + 250ms softwarová rezerva, fyzická prezentace do 15 s (sériový prototyp 5 s). Pozdní smyčka při aktivním motoru znamená vypnutí, nikoli další prodloužení pulzu. Maximálně jeden motor NEBO jedno servo, nikdy současně; nejvýše jeden 20cm LED úsek může svítit i během pohybu. Celkové limity nesmí měnit AI nebo běžný klient.

### Elektrické výchozí stavy

**Rozhodnutí majitele 9. 10. 2026:** sestava používá pouze koupené díly.
GPIO2 řídí přímo PCA OE; zelená kolébka ručně odpojuje 5V napájení serv
a U059. Stav kolébky se nesnímá, MCP GPA3 je volný.
PCA OE má pull-up kolem 10 kΩ (ze zásoby 2×22 kΩ paralelně) na 3,3 V a `OUTNE=00`: HIGH znamená výstupy LOW.
Případný osazený pull-down OE upravit a změřit resetový stav GPIO2/LED.
Před změnou kanálů nebo při Stop/I2C chybě nejdřív nastavit OE HIGH,
potom FULL_OFF všech kanálů. Jedno servo nebo vibrace, jeden LED úsek.
OE nezajišťuje odpojení 5V napájení; to provede servisní kolébka.
Softwarový limit smyčky se uplatní při návratu do řídicí úlohy.

Serva nemají návratový kanál. Dokončení zdvihu/návratu je konec individuálně
kalibrovaného časového okna, ne měřená poloha. Po přerušení žádný automatický
návrat: nejdřív ruční kontrola mechanismu, potom místní zotavení tlačítkem.
[Zapojení](zapojeni-potvrzeneho-hardwaru.md) a
[napájení](efekty-a-napajeni.md) určují detaily; fyzické proudy, teplota,
resetové stavy a pulzy se stále musí změřit. Výsledky mají
`physicalVerification: unavailable`, dokud neexistuje fyzický důkaz.

### Stavy rozhovoru a UI

`Idle → Listening → Transcribing → [TranscriptReview] → Thinking → Speaking/ProposalReady → AwaitingConfirmation → Executing → Finished`. TranscriptReview je jen asistovaná možnost telefonu; samostatný režim ho vynechá. V každém čekání je dostupné zrušení, při pohybu Stop. Textový tah vynechá Listening/Transcribing. Samostatný/ruční vlastník, AI stav, místní spojení a stav mechaniky jsou nezávislé osy: „AI offline“ není „kufr odpojen“. UI nikdy neukazuje úspěch jen podle HTTP 200 nebo ACK.

## 9. Bezpečnost přístupů, soukromí, náklady a limity

### Přístup a provozní ochrany

- Cloudové API klíče jen v secret store hostingu; vývoj v .NET user-secrets nebo lokální necommitované konfiguraci. Žádný `VITE_...` AI klíč, žádné tajemství v JS balíčku, logu, QR pro veřejnost nebo git historii.
- Cloudová device identita je oddělená od lokálního párování. Backend ukládá hash device credential a technické vazby. Lokální ruční řízení a tlačítkové offline menu fungují i po expiraci cloudového tokenu. Ztracený telefon vyžaduje fyzické lokální přepárování; odcizený kufr odvolání device credential v backendu. Mobil žádný cloudový token nepotřebuje.
- iOS Keychain; při případném pozdějším Android portu KeyStore-backed encrypted storage. Capacitor Preferences/localStorage nepatří k úložištím tajemství. QR tajemství patří dovnitř kufru a nesmí se zobrazovat v diagnostických snímcích. Apple podpisové secrets patří výhradně do odděleného CI prostředí, nikoli do aplikace nebo instalační stránky.
- Privátní device klíč, Wi-Fi a tokeny v NVS, ne v univerzálním image. Pro prototyp nepředstírat odolnost proti fyzickému čtení flash. Šifrování flash, secure boot a eFuse navrhnout při finalizaci servisního režimu; jejich nevratné zapnutí vyžaduje samostatné schválení a zálohu servisních postupů.
- Cloud validuje velikosti, concurrency a tenant/device scope, CORS povoluje jen konkrétní vývojové originy. Lokální server omezí počet klientů, handshake a rámců; mDNS je pouze discovery. Logovat stabilní kódy chyb a korelační ID, ne Authorization hlavičky.
- Obraz od AI, text i názvy vykreslovat jako text, ne přes neověřené `innerHTML`. Nedovolit vzdálené URL pro TTS/obrázky ani backendové fetch z adres dodaných klientem; omezit SSRF a nechtěné výdaje.
- OTA v první verzi vypnuté. Později pouze podepsaný image, servisní režim, vypnuté výstupy pohonů a rollback; existence dvou slotů v tabulce oddílů není hotové bezpečné OTA.

### Soukromí

**D143, pouze autonomní Live/hybridní větev:** textová paměť se rozšiřuje
o explicitně oddělené scopes a časovanou historii v témže EveningMemoryStore.
Osobní dlouhodobá paměť a export do OpenAI jsou výchozí vypnuté a vyžadují
ověřenou serverovou identitu a samostatná povolení. Anonymní večerní motivy
neobsahují osobní příběhy. Nové záznamy/přepisy mají scope retenci a revizované
mazání přes trvalý outbox včetně Files souboru. SQLite tombstone brání použití
okamžitě, vzdálený úklid má vlastní potvrzení/backlog. Běžící kontaminovaná
session po admin opravě/smazání končí; nová začne jen z platných dat.
Přesné limity a datové podmínky jsou v [D143 návodu](autonomous-live-memory.md).
Dosavadní archiv večerů a jeho explicitní mazání zůstávají kompatibilní;
následující původní retence jej popisuje, nikoli novou cloudovou osobní paměť.

Před rozhovorem zřetelně sdělit, že hlas/text zpracovává cloud a hlas odpovědi je syntetický. Textový a ruční režim bez nahrávání jsou rovnocenné možnosti. Kamera má oddělený souhlas pro jednotlivý snímek a náhled; žádné trvalé sledování, identifikace osoby, věk/pohlaví z obličeje nebo skrytý archiv.

Nahrávky a fotografie držet pouze v RAM/PSRAM ESP32 a backendu, při záložním vstupu také v RAM telefonu; po zpracování či chybě ihned smazat, tvrdý TTL 120 s. Aktivní přepis/sezení v RAM má TTL 15 min nečinnosti, nejvýše 30 min; konec odstraní dočasný stav, nikoli paměť večera schválenou majitelem 30. 9. Textová paměť má samostatný životní cyklus, limity a explicitní smazání podle §6. ESP32 drží jen aktuální tah a identifikátory. Nekládat obsah do analytiky, crash reportů, technického SQLite journalu, SD ani záloh. Výjimkou je výslovně schválený samostatný SQLite soubor textové paměti večera; žádné audio ani fotografie. HTTP/média `Cache-Control: no-store`; vypnout body logging, obsahové coredumpy a odložené uploady po reconnectu. Technický audit bez obsahu držet 7 dní, agregované denní náklady 90 dní. Nastavit cleanup timer a test odstranění i při timeoutu.

`store:false` není příslib nulové retence poskytovatele. Oficiální datové podmínky uvádějí, že API data se standardně nepoužívají k trénování bez opt-in; abuse monitoring může data uchovávat až 30 dní s výjimkami. ZDR a regionální zpracování mají podmínky a schválení [O11]. EU VM sama nezaručuje, že všechna AI data zůstávají v EU. Pokud pořadatel vyžaduje výhradně lokální/EU zpracování, nejprve potvrdit podmínky pro konkrétní modely a účet, jinak cloudový hlas/obraz nepoužít.

### Evidence nákladů a technické limity

Veřejné standardní sazby textu: GPT-6.1 Sol 2,00 USD / 1M vstupních a 10,00 USD / 1M výstupních tokenů; GPT-6 Luna 0,10/0,50. Cache write může stát více než běžný vstup [O22]. Pro hlas zůstává orientačně gpt-transcribe 0,0045 USD/min a mini-TTS 0,60 USD / 1M textových a 12 USD / 1M audio výstupních tokenů [O12, O18]. Před aktivací ověřit účet, modelovou dostupnost a skutečné usage včetně reasoning tokenů. Audio tokeny nelze převést na minuty pevnou konstantou.

Příklad sezení se Sol: 15 000 vstupních a 1 500 výstupních dialogových tokenů = 0,045 USD; 2 min STT přibližně 0,009 USD; 1 000 TTS textových a předpokládaných 2 500 audio výstupních tokenů = 0,0306 USD. Celkem přibližně 0,0846 USD, tedy 8,46 USD / 100 takových sezení. Je to modelový výpočet bez případné ceny cache writes, dalších reasoning tokenů, kamery, opakování, daní, hostingu a připojení; není to garantovaná cena návštěvníka.

Výchozí aplikační stropy: jeden návštěvník a jeden placený request na sezení současně; 10 tahů / sezení; vstupní text 1000 znaků; textový kontext a nákladová rezervace podle §6; provider výstup nejvýše 1200 tokenů včetně reasoning; nejvýše 30 s nahrávky a 30 s přehrávání; 10 nových tahů/min/zařízení; fronta backendu nejvýše 4 operace, dvě současná provider volání celkem. Kontext vybírat z trvalé paměti podle §6; žádné skryté sumarizační volání. Limit kontextu nevymaže archiv večera.

Majitel 9. 10. 2026 zrušil všechny softwarové peněžní stropy v backendu,
AI evaluaci i diagnostice. Staré proměnné session/daily/monthly cap se
ignorují a jejich chybění či hodnota nebrání startu AI. Finanční kontrola
patří nastavení OpenAI účtu; [Spend limits](https://developers.openai.com/api/docs/guides/spend-limits)
rozlišují upozornění a vynucený limit. V aplikaci se nadále transakčně
zapisuje odhad před voláním a spotřeba po odpovědi pro audit/idempotenci,
bez peněžního zamítnutí nebo zkracování dialogu podle zbývající částky.
Nejasný provider request se po restartu neopakuje. Účetní odhad není
vyúčtování OpenAI; skutečnou cenu uvádět pouze po ověření billing zdroje.
Ruční režim je nadále nezávislý na dostupnosti AI a účtu.

Pro jednu malou VM/doménu počítat plánovací rezervu přibližně 5–15 EUR/měsíc; není to ověřená nabídka konkrétního hostingu. Router/LTE a nový bezpečnostní obvod/PCA úpravy/expander patří do doplnění nákupního rozpočtu, nikoli do původního součtu elektroniky. Před nákupem potvrdit, zda jsou již k dispozici.

## 10. Předběžné piny, periferie a napájení

**D156 – 9. 10. 2026: implementovaný B-effects pouze s koupenými díly.**
PCA/RMT/MCP, audio B a návrat serv jsou v kódu. GPIO2 přímo ovládá OE,
MCP GPA0 tlačítko, GPA1–7 a GPB0–7 jsou volné. Zesilovače SD/GAIN
zůstávají ve výchozím mono zapojení, mute/hlasitost řídí PCM/I2S. GPIO38–40 zůstávají pro SD, kamerové piny se nepoužívají
pro efekty. NVS vyžaduje `profile_b_ok` a individuální kalibrace;
po bootu místní ruční kontrolu a zotavení. Doba pohybu potvrzuje pouze
programově zadanou polohu, není snímačem polohy.
[Konkrétní zapojení a servis](zapojeni-potvrzeneho-hardwaru.md).

Autonomní i nové fyzické ruční řízení drží journal až do dokončeného časovaného
návratu a potom obnoví dostupnost. Pětisekundová ukázka probíhá bez
impulzů; klidový příkon serv se musí změřit; celá ruční prezentace má strop 15 s, každé servo
okno je individuální 20–3000 ms s 250ms softwarovou rezervou a motorový pulz 150 ms (tvrdě 250 ms).
Historické 5s/400ms limity níže zůstávají v kompatibilním sériovém
prototypu západky, nikoli v implementované prezentaci. Změna brání tomu,
aby se nový návrat ukončil před pětisekundovou ukázkou; žádná mez se nedá
měnit AI ani běžným síťovým příkazem. E5/E6/B1 vyžadují fyzické měření přímého OE, napájení, kalibrace a mechaniky podle nového zadání.

### Co skutečně plyne z podkladů

Zdrojem barevných vazeb je [dodaná pinová mapa](pin-map-esp32-s3.png) a její [přepis](pin-mapa-esp32-s3.md). **Ani jedno nenahrazuje schéma a měření doručeného kusu.** Označení N16R8, kapacita pamětí a význam „LED ON“ nejsou zde elektricky ověřené.

| Funkce na obrázku / rezervace | Piny a zacházení |
| --- | --- |
| Kamera OV5640 | GPIO4 SIOD, 5 SIOC, 6 VSYNC, 7 HREF, 8 Y4, 9 Y3, 10 Y5, 11 Y2, 12 Y6, 13 PCLK, 15 XCLK, 16 Y9, 17 Y8, 18 Y7. Při plné kameře se neberou jako volné GPIO. |
| MicroSD | GPIO38 CMD, 39 CLK, 40 DATA. Předběžně SDMMC 1bit; nikoli automatický předpoklad SPI nebo 4bit. Skutečné propojení socketu a pull-upy ověřit. |
| Paměti | Obrázek značí PSRAM35/36/37, dokumentace S3 upozorňuje i na piny paměťové sběrnice 26–32 a podle varianty 33–37. Žádný z nich nově nepřidělovat [H1]. |
| USB | Rezervovat GPIO19/20. **Obrázek uvádí GPIO20 jako D− a GPIO19 jako D+, což je opačně než dokumentace Espressif (19 D−, 20 D+) i místní textový přepis.** Neopravovat fyzické propojení podle obrázku; identifikovat konektory na skutečné desce [H2]. |
| UART0 | GPIO43 TX, 44 RX, obrázek u nich značí TX/RX LED. Ponechat pro konzoli/servis a případný osazený USB-UART převodník. |
| Boot/strapping | GPIO0 BOOT, 3 JTAG_EN, 45, 46 LOG. Nepoužít pro motory, tlačítko ani OE; RST/EN není běžný GPIO. |
| JTAG | GPIO39–42. V základu lze zachovat externí JTAG; s SD39/40 a audiem41/42 už ne. V rozšíření ladit přes nativní USB Serial/JTAG, UART jako konzoli. USB-OTG audio/storage funkce se současně neslibuje. |
| LED na desce | GPIO2 „LED ON“ a GPIO48 WS2812. V základu ponechat desce. V rozšíření lze vědomě sdílet jen za podmínek níže; nejsou to neobsazené piny. |
| Napájení | 3V3 pro logiku, 5V/GND podle revize. 5V pin desky nenapájí serva/RGB rozvod. |

### Varianta A: bez kamery a bez MicroSD

Přímé řízení všech šesti motorů je možné, pokud je konektor kamery prázdný a skutečně nepřidává konflikt/zátěž. Doporučuji ale už v základu motory spínat přes šest volných kanálů PCA9685: omezí kabeláž a připraví rozšíření. Jde o digitální vstupy **hotových U059 modulů**, ne o napájení motoru z PCA.

| Externí funkce | Doporučené přidělení A | Poznámka |
| --- | --- | --- |
| I2C SDA / SCL | GPIO1 / GPIO14 | PCA9685 adresa 0x40, logika/pull-upy 3,3 V. Začít 100 kHz. |
| RGB data | GPIO21 | Přes Pixel Shifter 3,3 → 5 V a 330 Ω do šesti úseků, celkem asi 36 LED. |
| PCA OE | GPIO47 | Historický návrh A: přímé OE, HIGH vypíná signály; pull-up kolem 10 kΩ na 3,3 V. Fyzické efekty jsou implementované v B. |
| I2S BCLK / WS | GPIO4 / GPIO5 | Společné pro mikrofon a zesilovač; blokuje kameru. |
| Mikrofon SD → ESP DIN | GPIO6 | ICS43434 napájen 3,3 V; kanál L/R pevně nastavit. |
| ESP DOUT → zesilovač DIN | GPIO7 | MAX98357A, reproduktor mezi jeho výstupy, ne na GND. |
| PTT kontakt | GPIO8 | Proti GND, externí pull-up 3,3 V, debounce 30 ms. LED tlačítka řešit podle napětí skutečného modulu. |
| Mute/SD zesilovače | GPIO16 | Pull-down = mute při bootu; nastavení kanálu/SD podle skutečného breakout modulu. |
| Snímání PHYSICAL_ENABLE | GPIO18 | Napětí za servisní kolébkou za zelenou kolébkou přes dělič 15 kΩ / 22 kΩ do 3,3V logiky; kolébka přímo odpojuje napájení pohonů. |
| Serva přihrádek 1–6 | PCA kanály 0–5 | Přihrádka n → kanál n−1. 50 Hz; individuálně kalibrované mikrosekundy pulzu, žádný univerzální úhel. |
| Motory přihrádek 1–6 | PCA kanály 6–11 | Přihrádka n → kanál n+5; jen FULL_ON/FULL_OFF, nejvýše jeden zapnutý. |
| Rezerva | PCA12–15, GPIO9–13/15 a 38–42 | Konektor kamery/SD musí zůstat neosazený nebo elektricky prověřený. Nezavádět jejich použití potají. |

**Přímá alternativa A:** U059 DIN1–DIN6 = GPIO9,10,11,12,13,15, každá větev s pull-down a zelenou kolébkou pro napájení pohonů. PCA pak řídí pouze serva. Funguje bez kamery, ale vyžaduje šest GPIO a změnu kabeláže při rozšíření. Volit jen pokud PCA výstupy/moduly neprojdou ověřením logických úrovní nebo bude potřeba nezávislé rychlé PWM vibrací.

### Varianta B: kamera OV5640 + MicroSD + úplné audio

Po zachování kamery, SD, pamětí, UART, USB a strapping pinů zbývá na obrázku jen **GPIO1,14,21,41,42,47** bez další označené vazby. Audio potřebuje čtyři piny i při poloduplexu, I2C dva; nezbývá RGB, tlačítko ani přímý OE výstup. Plné přímé zapojení všech požadavků tedy nevychází. Poloduplex dovoluje časové střídání audia, ne spojení mikrofonního SD s DIN zesilovače do jediné datové linky.

Pro tuto rozšířenou variantu doporučuji PCA motory + **MCP23017 pro pomalé pomocné I/O**, vědomé sdílení RGB48 a ověřené použití LED pinu GPIO2 pro OE výstup. Toto je podmíněná, ale konkrétní jednodesková varianta; nejdřív ověřit GPIO2/48 a skutečnou kabeláž; strapping/PSRAM piny nepřidělovat.

| Externí funkce | Přidělení B | Kolize / podmínka |
| --- | --- | --- |
| Kamera | GPIO4–13,15–18 podle tabulky výše | Zachovat SIOD/SIOC samostatně pro kameru; PWDN/RESET neuvádět jako dostupné GPIO bez schématu. |
| SDMMC 1bit | CMD38, CLK39, D0 40 | Ověřit socket a pull-upy; externí JTAG39/40 nelze současně. |
| I2C SDA / SCL | GPIO1 / GPIO14 | PCA0x40 + MCP23017 0x20 (A0–A2 GND), jedna koordinovaná sběrnice, pull-upy 3,3 V. |
| I2S BCLK / WS | GPIO21 / GPIO41 | Společné audio hodiny, GPIO41 nelze současně externí JTAG. |
| Mikrofon SD → DIN | GPIO42 | Konflikt s externím JTAG řešen USB laděním. |
| DOUT → MAX98357A DIN | GPIO47 | Rozšíření přesouvá OE výstup na GPIO2. |
| RGB data | GPIO48 | Rozbočit **GPIO před onboard WS2812** do Pixel Shifteru; onboard LED zrcadlí první pixel. Nejde o její DOUT a neposouvá to indexy externích 36 LED. Ověřit elektrickou zátěž/topologii. |
| PCA OE | GPIO2 | Již označený „LED ON“: změřit LED zapojení, polaritu a proud/pull rezistory. HIGH vypíná PCA signály, pull-up kolem 10 kΩ na 3,3 V; LED může odrážet stav OE; není nezávislou stavovou LED. Bez prokázaného bezpečného boot stavu tuto variantu nepoužít. |
| PTT | MCP GPA0 | Kontakt na GND, externí 15 kΩ na 3,3 V; polling 10 ms, debounce 30 ms. INT pin není nutný. |
| Volné MCP | GPA1–GPA7 / GPB0–GPB7 | Nezapojené vstupy; žádné snímání kolébky ani hardwarový mute. |
| Serva / motory | PCA0–5 / PCA6–11 | Stejná přihrádková vazba jako A. PCA12–15 a zbytek MCP rezerva. |
| USB / UART / paměti / straps | Beze změny podle rezervací | Dva fyzické USB-C konektory nejsou dva libovolné páry USB GPIO. |

Pro A a B budou dva explicitní board profily s compile-time kontrolou duplicit a povolených sdílení. Fyzická přestavba A → B přesouvá audio/RGB/OE/tlačítko; **není to jen připojení kamery**. Před zapnutím porovnat profil, kabelové štítky a kalibraci. Chybný profil se musí zjistit servisní kontrolou; software sám neumí spolehlivě rozpoznat veškerou kabeláž.

### PCA, audio a napájení

PCA9685 má společnou frekvenci PWM; pro serva 50 Hz proto není vhodné používat jiné duty PWM na motorech. Bity FULL_ON/FULL_OFF poskytují prosté zapnuto/vypnuto [H4], což pro tři vibrační pulzy stačí. OFF má přednost; při přepnutí explicitně odstranit protichůdný bit. U059 obsahuje vlastní MOSFET a digitální DIN [H5]. Napájet ho z pohonové větve, ověřit přijetí 3,3V DIN a skutečný špičkový odběr. Holý motor vyžaduje samostatný tranzistor/driver a ochranu indukční zátěže; na PCA/GPIO nepatří.

I2S zvolit standard Philips, 32kHz/32bit sloty, bez externího MCLK. Sdílené hodiny při full-duplex konfiguraci periférie vyžadují shodný formát RX/TX [H3], i když aplikace střídá poslech a mluvení. ICS43434 používá 24bitová data v rámci 64 hodinových taktů; při 32 kHz je BCLK 2,048 MHz. MAX98357A podporuje 32kHz, ale ne 24 kHz, proto je převzorkování důležité. Ve zvoleném B-effects zůstávají SD původní, ticho vytváří PCM a auto-clear DMA; při bootu/restartu není garantované. Historický ověřený A používá GPIO16 mute [H7]. Kamera na S3 není automaticky stejný I2S kamerový režim jako na původním ESP32; používat driver pro S3 a ověřit DMA/PSRAM souběh [H6]. Kamera jeden JPEG, max. 640 × 480, jeden framebuffer, mimo audio/efekty; SD bez ukládání snímků. Je-li nedostatek DMA paměti, kamera se odmítne, nevytěsní bezpečnostní úlohu.

Napájení zachovat podle [návrhu napájení](efekty-a-napajeni.md): PD12V z UGREEN C1 jen do vstupu Joy-IT SBC-Buck02, před připojením MCU změřit 5 V. Návrhový trvalý rozpočet 4 A pro deklarovaný 5A měnič, startovní jas WS2812 nejvýše přibližně 25 %, asi 36 LED. Neodvozovat dostupný proud pro pohony pouze z procent jasu. Změřit souběh desky/Wi-Fi/kamery/audia a přechodové špičky pohonů; při nedostatečné rezervě snížit jas/hlasitost nebo změnit napájení. Napájení serv +5V oddělit od 3,3V logiky PCA, všechny země propojit, rozvod hvězdicově, 2×470 µF u LED a další 2×470 µF u pohonů podle původního návrhu. Ověřit pojistku, vodiče, teplotu měniče a konektory; software není náhrada proudové ochrany.

Při současném USB ladění a externím napájení ověřit ochranu proti zpětnému napájení konkrétní desky. Nepřipojovat dva 5V zdroje proti sobě bez doloženého power-path řešení. Powerbanka může při nízkém odběru vypnout; řešit měřením a volbou zdroje, ne umělým trvalým zapnutím motoru.

### Povinná kontrola před prvním zapojením

Zaznamenat fotografie obou stran, přesné označení modulu/PCB, dostupné schéma, zapojení obou USB, paměti, kontinuitu kamery/SD/LED2/LED48, OE rezistory PCA, napětí logiky modulů a resetové úrovně. Bez připojených pohonů ověřit měřením všechny bezpečné výstupy při bootu, resetu, brownoutu a odpojení I2C. Teprve poté jeden RGB úsek, jeden motor a jedno odpojené servo na omezeném zdroji; až nakonec mechanika a šest přihrádek. Tato kontrola je hardwarová podmínka plánu, nikoli již provedené měření.

## 11. Vývoj bez fyzické desky a ověřování

### Simulátor

Hostitelský C++ proces znovu používá `SelectionSequence` a budoucí parser/řadič/safety/conversation logiku. Simuluje PTT a potvrzení, jako device HTTPS klient volá lokální backend bez telefonu. Výstupní port vytváří časovou osu LED, motorů, servo PWM a OE; čas lze řídit deterministicky bez čekání. WSS facade napodobí stejný handshake a kontrakty jako ESP32, obsahuje testovací identitu a oddělený certifikát. UI ukazuje šest přihrádek a viditelné „SIMULACE“; nesmí vypadat jako připojený fyzický kufr.

Hostitelský simulátor může pro vývojové webové UI vystavit samostatný pouze čtecí HTTPS proud nad stejnými událostmi C++ modelu. Prohlížeč v běžném WebSocket API neumí přidat `Authorization` hlavičku nativního WSS handshake; token proto webové UI posílá v HTTPS hlavičce, nikdy v URL. Tento proud není kontrakt telefonu ani firmware a nemění jediné řídicí WSS spojení.

Fault injection: ztracený ACK, zpožděný příkaz, zavřený socket v každé fázi, restart s novým bootId, přetečení času, obsazená přihrádka, neplatná kalibrace, zamrzlá I2C, plný buffer, přerušení audia a neuzavřený journal. Backend fake vrací ask/propose/refusal/invalid/timeout/429, STT testovací přepis a TTS fixture WAV. Simulátor neověřuje GPIO, proud, DMA timing ani bezpečnost mechaniky; Wokwi/QEMU jsou volitelné doplňky, ne důkaz kompatibility této konkrétní desky.

### Testovací vrstvy a konkrétní akceptace

| Vrstva | Minimální scénáře a důkaz |
| --- | --- |
| C++ unit | Stávající tři testy zachovat; přidat abort v každé fázi, maximální on-time, výpadek lease, časový overflow, pozdní loop a prioritu Stop. Invariant: nejvýše jeden motor, motor=0 při servo výstupu, safe state vypíná všechny pohony. |
| Protokol | Stejné fixtures C++/TypeScript/.NET. Čísla 0/7/1.5/"4", záporné délky, neznámý boot, stará revision, duplicitní klíče, oversize, změněný payload pod stejným ID, ztráta ACK a vyprázdněná cache nesmějí způsobit druhé spuštění. |
| Backend integrace | Skutečná lokální SQLite a HTTP fake provider; souběžné idempotentní requesty spustí právě jednu provider operaci, náklady se evidují bez peněžního stropu, cizí session=403, restart=unknown bez automatického replay, cleanup odstraňuje obsah. |
| Mobil + simulátor E2E | Text → návrh → změna na jinou hůlku → potvrzení → odpovídající commandId a přihrádka. Ruční cesta má explicitní potvrzení; autonomní cesta má samostatnou validaci dle D75. Ruční režim při odpojené WAN, Stop, zamítnuté oprávnění a návrat z pozadí. |
| Samostatný kufr E2E | Žádný spuštěný telefon: PTT fixture → device HTTPS → fake/AI → hlasový výběr → autonomní vysunutí/předvedení/zasunutí. Odpojení pozorovatele nic nezmění; ztráta manual lease zastaví. Starý návrh po takeover/reconnect/reboot se nespustí. Test PTT/Stop v každém stavu, USB provisioning, opuštěný rozhovor a opakované normální cykly bez ručního reloadu. |
| Audio fixtures | Správná délka a sample rate, int24 znaménko, resampling 24→32 kHz, clipping, prázdná řeč, maximum 30 s, přerušený přenos, starý streamId a zrušení při playback. |
| AI eval | Alespoň 60 českých scénářů: běžné odpovědi, hluk/nářečí, nesrozumitelnost, prompt injection, volba nedostupné hůlky, všech šest možností a odmítnutí. 100 % výstupů musí buď projít validací, nebo skončit bezpečně; kvalitu stylu obsluha schválí alespoň v 90 % scénářů. |
| Hardware | Osciloskop/logický analyzátor a měření proudu, žádný impulz motoru/serva při bootu/resetu, cut při I2C/freeze a fyzickém stop. USB/JTAG, audio bez praskání, SD/camera profil, desetiminutový tepelný test a alespoň 100 cyklů každé přihrádky bez chyb. |

Při implementaci: firmware `pio run` v `kufr/firmware`, hostitelský C++ test podle aktuálního README (výstupy do dočasné/build složky); frontend `npm run build`, cílené unit/E2E skripty po jejich zavedení; backend cílené `dotnet test` a build po vytvoření projektu. Nové testovací závislosti zavádět odůvodněně jako součást příslušné etapy, s připnutými verzemi a ověřenými příkazy. Browser E2E a iOS Simulator v GitHub Actions **nenahrazují** fyzický iPhone test instalace podepsané IPA, native TLS, LAN oprávnění, mikrofonu a uspání. Dokumentační práce sama o sobě nevyžaduje spuštění CI; skutečně provedené kontroly vždy zapsat do stavu implementace.

Metriky: LAN RTT a ACK p95, reconnect time, počet neznámých výsledků, provider latence/usage, buffer overruns, minimální heap/DMA/PSRAM, watchdog/reset reason a délky pulzů. Žádné texty či nahrávky v metrikách. Pro hardware evidovat revizi PCB, firmware hash, profil a kalibraci, aby byl výsledek reprodukovatelný.

## 12. Postup v malých ověřitelných etapách

Každá etapa je samostatně reviewovatelná změna a lze ji dělit na menší ověřitelné kroky. Implementace softwaru a simulace mohou předběhnout dodání desky; povolení skutečných pohonů nikoli. Následující tabulka určuje akceptaci; skutečné dokončení se vede pouze ve stavu implementace.

| Etapa | Výstup | Závislost | Dokončeno, když |
| --- | --- | --- | --- |
| E0 – provozní předpoklady | Potvrdit model/verzi iPhonu, vlastníka cloudu, katalog, router a rozpočet. Získat zdroj existujícího iOS workflow, Team/Bundle ID a distribuční režim; secrets zadá vlastník mimo repozitář. Změřit revizi desky a mechanický prototyp, zapsat A/B profil. | Dokument | SW může začít s iOS/default katalogem; HW nesmí pokračovat bez měření a schématu bezpečnostní větve. Podepsaná distribuce čeká na platný profil/identitu, neblokuje simulátor. |
| E1 – bezpečné jádro | Abort/Stop, safety port, invarianty, časové limity a hostitelské testy nad stávající sekvencí. | Dokument | Každá fáze jde bezpečně přerušit; opožděná smyčka neudrží motor nad limit; nic není závislé na síti. |
| E2 – smlouvy a simulátor | JSON Schema/OpenAPI, fake clock, UI časová osa, journal a idempotence model. | E1 | Ztracený ACK/duplicitní/pozdní příkaz nezpůsobí druhý výběr; restart vyžaduje zotavení. |
| E3 – ruční mobil bez HW | Šest přihrádek, potvrzení, Stop, aktuální snapshot, offline UI; bez AI. | E2 | Celá ruční sekvence proti simulátoru včetně všech chyb spojení. |
| E3a – iOS CI základ | Capacitor iOS projekt, rozhraní pro pozdější Swift bridge E10, webové PR CI a ruční Windows→GitHub→macOS workflow s nativními testy a podepsanou Ad Hoc IPA. | E0 identita, E3 | CI běží na připnutém Xcode, tajemství se načítají až pro podpis, keychain se uklidí i při selhání. IPA se nainstaluje/spustí na registrovaném iPhonu; Release/Pages nejsou nutné. |
| E4 – runtime a přímá síť | ESP-IDF 6.1/PlatformIO profil, certifikát/NVS, Wi-Fi, HTTPS klient backendu a lokální WSS hello/manual lease. | E2; pro nahrání E0 | Device volá fake backend bez telefonu, TLS/čas/reconnect funguje; později E3 připojí native adaptér. Verze knihoven uzamčené. |
| E5 – jedna bezpečná přihrádka | RGB, PCA servo, jeden U059, přímé OE/servisní kolébka, kalibrace. | E0,E1,E4 | Změřený proud, reset bez pohybu, Stop/I2C timeout vypne signály, kolébka odpojí napájení; mechanika stabilní bez napájení. |
| E6 – šest přihrádek | Šest kanálů, maska dostupnosti, automatický návrat pevné rekvizity, servisní zotavení, limity jasu. | E5 | 100 cyklů/přihrádka, žádný souběh motor/servo, ověřená spotřeba/teplota a ruční ovládání bez WAN. |
| E7 – backend bez skutečné AI | ASP.NET, device tokeny, SQLite limity/idempotence, fake conversation, cleanup. | E2 | Integrační testy přístupů, souběhu, restartu a retence zelené; logy neobsahují citlivé údaje. |
| E8 – skutečný textový dialog | Responses adaptér, firmware conversation slice, prompt/katalog, autonomní brána a simulovaná prezentace; telefonní text/oprava jako druhý vstup. | E2,E7; na desce E4; klíč správce | Eval splní kritéria, neplatný nebo zastaralý návrh nikdy nevolá výstupy, rozpočet se měří/vynucuje. Funguje v device simulátoru bez aplikace. |
| E9 – samostatný hlas kufru | I2S 32 kHz, PTT, přímý WAV upload/STT/TTS download, playback/mute, offline menu, nouzové hlášky. | E4,E5,E8 | Kompletní rozhovor a autonomní prezentace s návratem bez telefonu. Buffer/TLS chyby neblokují safety, 100 tahů bez úniku RAM, česká kvalita/latence ověřena. |
| E10 – dokončení mobilní zálohy | Swift native TLS/discovery, ruční takeover, text a oprava přepisu; volitelný mikrofon/hlas iPhonu přes device session. | E3,E3a,E4,E8; audio po E9 | Na fyzickém iPhonu funguje ruční ovládání bez WAN, ztráta manual lease, pozorovatel bez vlivu na samostatný režim a oprávnění iOS. Telefon není podmínkou E9. |
| E11 – provozní balení | Samostatný kufr, CI podepsaná iOS záloha, artifact a vlastní HTTPS instalační web s OTA manifestem, cloud instance, runbook, backup. | E6,E8,E9; záložní aplikace E10 | Celý scénář bez notebooku a bez aktivního telefonu; navíc funkční instalace/aktualizace ruční zálohy. Publikovaný IPA/manifest má ověřený download a platný podpis; klíče ani profily se samostatně nezveřejní. |
| E12 – obraz telefonu | Souhlas, jednorázový obrázek, popis kostýmu a ruční oprava. | E8,E11 | Obrázek se neukládá, chybný popis nic fyzicky nespouští; přínos vyhodnocen. |
| E13 – OV5640 + SD profil B | Přestavba pinů, MCP, sdílené LED piny nebo schválený druhý řadič; přímý HTTPS upload snímku, camera/SD stress test. | E0 kontrola B,E9,E12 | USB/debug a všechny efekty fungují, měřený DMA/heap/proud, selhání SD/kamery neovlivní safe state. |

Hardwarová větev je E0→E1/E2→E4→E5→E6. Konverzační větev E2→E7→E8 může běžet bez desky. Spojí se v E9 pro samostatný hlas; E3/E3a/E10 dodají iOS zálohu a E11 celek ověří na akci. Skutečný placený provider test vyžaduje vědomě nakonfigurovaný účet. E12/E13 nejsou podmínkou první použitelné rekvizity.

## 13. Otevřené otázky a výchozí předpoklady

| Otázka | Výchozí rozhodnutí, dopad a termín ověření |
| --- | --- |
| Jaký konkrétní iPhone a verze iOS? | Majitel potvrdil iPhone 15 Pro s iOS 26.6.1 a 30. 9. 2026 úspěšnou Ad Hoc instalaci/spuštění; Windows je vývojové prostředí, Xcode běží vzdáleně. |
| Kde je zdroj již fungujícího workflow a jaký profil používá? | Majitel dodal MAUI workflow z jiné aplikace, vývojový i Ad Hoc profil a odpovídající `.p12`; schválil stejné Bundle ID. E3a má vlastní Capacitor workflow. Oba profily mají Team ID `3QRXV8G5Y3`, Bundle ID `app.turquoise6409.green2444` a jedno zařízení; Ad Hoc podpis prošel CI a instalaci potvrdil majitel. |
| Jak bude dostupná IPA a používá se Feather, AltStore Classic nebo OTA? | Artifact vždy, publikace volitelná. Feed vyžaduje trvalý dostupný HTTPS download, ne přihlášením chráněný artifact. Zvolený klient a případný přepodpis ověřit fyzickou instalací; PAL není součást návrhu. |
| Je nutné potvrzení AI výběru? | Rozhodnuto majitelem 30. 9.: ne. Autonomní validovaná prezentace pevné rekvizity; PTT a Stop zůstávají oddělené od volného textu. |
| Router/internet na místě, hotspot a izolace klientů? | Vyhrazená LAN s dostupným AP, WAN volitelná. Otestovat konkrétní místo; telefon jako hotspot pouze po zkoušce. |
| Jaká je doručená deska a zapojení LED2/48/USB/SD? | Neznámé; schéma a měření jsou blokátor elektrického zapojení, zejména varianty B. |
| Jsou 3,3V výstupy PCA pro U059 skutečně dostatečné? | Předpoklad podle digitálního modulu, potvrdit na kusu. Při nevyhovění vhodný buffer/driver, nikoli 5V do ESP32. |
| Servo model, pulzy, zdvih a senzory? | Zvolen zdvih a automatický návrat; konkrétní mechanika a kalibrace jsou otevřené. Bez snímačů jen povelově ověřený stav. Po Stop/chybě žádný automatický návrat. |
| Názvy/osobnosti šesti hůlek a cílové publikum? | Party katalog šesti rekvizit; majitel v D135 výslovně žádá ostrý, urážlivý humor a drzého prodavače z Ollivanderova obchodu pro dospělé. Otázky patří k výběru hůlky, respektují odmítnutí tématu a ukončení hry. Kvalita vyžaduje lidský poslech. |
| Dostupnost/vracení hůlek? | Rozhodnuto majitelem 30. 9.: hůlka zůstává. Úspěšný normální návrat obnoví dostupnost, porucha/Stop vyžadují zotavení. |
| Potřebujeme kameru i SD zároveň? | Ne pro základ. SD jen assety, kamera jednotlivé snímky; potvrdit užitek před E13 a teprve pak měnit kabeláž. |
| Hlasitost a srozumitelnost v reálném hluku? | Dva reproduktory 8 Ω, každý vlastní MAX98357A, společný hlas a PTT; český eval a měření v prostředí před E10/E11. |
| Kdo platí a spravuje cloud, jaká retence/region je přípustný? | Jeden správce, explicitní limity a informování návštěvníků. Přesné účetní/datové podmínky a dostupnost modelů před placeným testem. |
| Rozměry kufru, kabely, teplota a výdrž powerbanky? | Vnitřek kufru 431,8×266,7×114,3 mm, výztuha 5 mm; součástky ve společném přehledu. Powerbanka 139×80×26,5 mm musí jít snadno vyjmout. Další kabelové rozměry neblokují rozvržení; teplotu a výdrž ověřit zátěžovým měřením, ne pouze z nominální kapacity. |

## 14. Práce s plánem a průběžná dokumentace

Pokyn „začni/pokračuj implementovat aplikaci podle implementačního plánu“ spouští postup v AGENTS.md: ověřit skutečný stav, převzít nejbližší proveditelný krok, implementovat, ověřit a předat. Při prvním zahájení je to E1, nikoli požadavek napřed dokončit všechna externí měření E0. Hardware/podpis/placené služby blokují jen své závislé části.

- **Plán:** přijatá rozhodnutí, kontrakty, bezpečnost, etapové závislosti a kritéria. Měnit při novém architektonickém důkazu; důvod, dopady a potřebnou migraci zaznamenat spolu se změnou. Nesnižovat kritéria, aby šla etapa označit za hotovou.
- **[Stav implementace](stav-implementace.md):** jediný seznam stavů E0–E13/E3a, dílčí kroky, chybějící vstupy, důkazy kontrol a konkrétní předání. Aktualizovat při převzetí i dokončení/přerušení kroku. Fake, build, integrační test a fyzické ověření rozlišovat; procenta bez podkladu nepoužívat.
- **README a AGENTS:** krátké vstupní body a postup práce. Komponentové README popisují skutečně dostupné příkazy/konfiguraci a zvlášť budoucí směr. Nové příkazy a závislosti doplnit zároveň s jejich implementací; nepředstírat existující backend/CI jen proto, že ho plán navrhuje.
- **Firmware:** dokumentovat skutečný ESP-IDF runtime, kontrakty, jediný profil, servisní provisioning bez tajemství a ověřené příkazy. Historický Arduino sériový prototyp se nesestavuje ani neobnovuje.
- **Mobil po E3/E3a/E4:** doplnit skutečné Windows/CI příkazy, Swift adaptér, workflow cestu, názvy secrets, podpisový režim a důkaz instalace. Vite build ani CI archive neznamenají otestovaný iPhone.
- **Hardware:** podkladový obrázek zachovat; měření doplňovat s revizí PCB, firmware, profilem a kalibrací. Profily A/B zůstávají předběžné do ověření. Nákupní ceny jsou historický podklad, bezpečnostní obvod/MCP/router vyžadují doplnění rozpočtu před nákupem.

Při předání uvést přesně co prošlo, co nebylo ověřeno, co je rozpracované a který malý krok následuje. Nespoléhat na paměť chatu. Tento postup neopravňuje sám o sobě k externí publikaci, nákupu nebo nasazení; hranice aktuálního zadání a dříve udělená konkrétní oprávnění zůstávají platná.

## 15. Ověřené externí zdroje

Oficiální dokumentace byla otevřena při přípravě plánu 29. 9. 2026. Odkazy `stable/latest` se mění; při implementaci zachytit skutečně použitou verzi a znovu ověřit účetní dostupnost modelů. Dokumentace součástky potvrzuje její vlastnosti, ne zapojení prodejcem osazené desky.

### Platformy a API

- **O1:** [Capacitor plugins, dokumentace v8](https://capacitorjs.com/docs/plugins), [iOS](https://capacitorjs.com/docs/ios) a [požadavky Capacitor8](https://capacitorjs.com/docs/updating/8-0) – Swift adaptéry, Node/Xcode/minimální iOS a SPM výchozí šablona.
- **O2:** [Apple TN3179 – Local network privacy](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy) – LAN oprávnění a Bonjour deklarace. [Web Bluetooth](https://developer.mozilla.org/en-US/docs/Web/API/Web_Bluetooth_API) – omezená podpora, nepovažovat za univerzální mobilní transport.
- **O3:** [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) – .NET10 LTS.
- **O4:** [ESP-IDF HTTPS/WSS server](https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/api-reference/protocols/esp_https_server.html), [PlatformIO framework/sdkconfig](https://docs.platformio.org/en/latest/platforms/espressif32.html), [přesný manifest platformy 7.1.3](https://raw.githubusercontent.com/platformio/platform-espressif32/v7.1.3/platform.json) – rozdíl Arduino a ESP-IDF balíčků.
- **O5:** [GPT-4.1 mini](https://developers.openai.com/api/docs/models/gpt-4.1-mini) – snapshot a podporované funkce.
- **O6:** [GPT-4o mini Transcribe](https://developers.openai.com/api/docs/models/gpt-4o-mini-transcribe) – připnutelný snapshot STT.
- **O7:** [GPT-4o mini TTS](https://developers.openai.com/api/docs/models/gpt-4o-mini-tts) – snapshot TTS.
- **O8:** [Speech to text](https://developers.openai.com/api/docs/guides/speech-to-text), [GPT-Transcribe](https://developers.openai.com/api/docs/models/gpt-transcribe) – současné doporučení a upload audio API.
- **O9:** [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs) – Responses `text.format`, strict schema a refusal.
- **O10:** [Text to speech](https://developers.openai.com/api/docs/guides/text-to-speech) – PCM formát, jazyky a syntetický hlas; [Realtime](https://developers.openai.com/api/docs/guides/realtime) – přímý průběžný hlas D114; [oficiální .NET SDK](https://github.com/openai/openai-dotnet) – použitý klient.
- **O11:** [OpenAI data controls](https://developers.openai.com/api/docs/guides/your-data) – aplikační stav, monitoring, ZDR a regionální podmínky.
- **O12:** [OpenAI API pricing](https://developers.openai.com/api/docs/pricing) – sazby použité v příkladu, nikoli garantovaný účet.
- **O13:** [ESP-IDF HTTP/S client](https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/api-reference/protocols/esp_http_client.html) – přímá komunikace ESP32, ověření CA/hostname, streaming a keep-alive.
- **O14:** [ESP-IDF System time / SNTP](https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/api-reference/system/system_time.html) – systémový čas oddělený od monotónního časování efektů.
- **O15:** [GitHub macOS15 runner image](https://github.com/actions/runner-images/blob/main/images/macos/macos-15-Readme.md) – dostupný Xcode a SDK; image se průběžně mění.
- **O16:** [Apple URLSessionWebSocketTask](https://developer.apple.com/documentation/foundation/urlsessionwebsockettask) – nativní textové/binární WSS spojení.
- **O17:** [GitHub – Apple signing certificate on macOS runners](https://docs.github.com/en/actions/how-tos/deploy/deploy-to-third-party-platforms/sign-xcode-applications) – secrets, keychain a úklid.
- **O18:** [GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna), [GPT-Transcribe](https://developers.openai.com/api/docs/models/gpt-transcribe) – možnosti, sazby a současné aliasy.
- **O19:** [OpenAI deprecations](https://developers.openai.com/api/docs/deprecations) – ukončení staré rodiny mini-transcribe.
- **O18:** [Apple Ad Hoc provisioning](https://developer.apple.com/help/account/provisioning-profiles/create-an-ad-hoc-provisioning-profile/) a [Xcode distribution](https://developer.apple.com/documentation/xcode/distributing-your-app-for-beta-testing-and-releases) – registrovaná zařízení, export a distribuční režimy.
- **O19:** [GitHub – downloading workflow artifacts](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts) – přístup a expirace artifactů.
- **O20:** [AltStore source format](https://faq.altstore.io/developers/make-a-source) a [oficiální Feather repozitář](https://github.com/claration/feather) – feed a odlišné podmínky podpisu/instalace klientů.
- **O21:** [ntfy publishing](https://docs.ntfy.sh/publish/) – volitelná oznámení výsledku buildu.
- **O22:** [GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol), [GPT-6 Astra](https://developers.openai.com/api/docs/models/gpt-6-astra), [GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna) a [Spend limits](https://developers.openai.com/api/docs/guides/spend-limits) – volba modelu, ceny a projektový hard limit.

### Hardware

- **H1:** [ESP32-S3 GPIO](https://docs.espressif.com/projects/esp-idf/en/latest/esp32s3/api-reference/peripherals/gpio.html) a [strapping/hardware checklist](https://docs.espressif.com/projects/esp-hardware-design-guidelines/en/latest/esp32s3/schematic-checklist.html#strapping-pins).
- **H2:** [ESP32-S3 vestavěný USB JTAG](https://docs.espressif.com/projects/esp-idf/en/latest/esp32s3/api-guides/jtag-debugging/configure-builtin-jtag.html) – GPIO19 D−, GPIO20 D+.
- **H3:** [ESP32-S3 I2S](https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/api-reference/peripherals/i2s.html) – RX/TX, sdílené hodiny a sloty.
- **H4:** [NXP PCA9685 datasheet](https://www.nxp.com/docs/en/data-sheet/PCA9685.pdf) – OE, reset, full-on/full-off a společná PWM frekvence.
- **H5:** [M5Stack U059 Vibrator](https://docs.m5stack.com/en/unit/vibrator) – hotový modul s MOSFET a digitálním řízením.
- **H6:** [Oficiální esp32-camera driver](https://github.com/espressif/esp32-camera) – podpora S3/OV5640 a omezení framebufferu/PSRAM.
- **H7:** [TDK ICS43434 datasheet](https://cdn-shop.adafruit.com/product-files/6049/6049_DS-000069-ICS-43434-v1.2.pdf) a [Analog Devices MAX98357A/B datasheet](https://www.analog.com/media/en/technical-documentation/data-sheets/MAX98357A-MAX98357B.pdf) – kompatibilní 32kHz rámec a shutdown zesilovače.
- **H8:** [Microchip MCP23017 datasheet](https://ww1.microchip.com/downloads/en/DeviceDoc/20001952C.pdf) – expander, resetové vstupy a registr výstupů; ověřit konkrétní breakout a jeho pull rezistory.

Místní obrázek, produktové označení v nákupním seznamu a předběžné tabulky tohoto plánu zůstávají návrhovými podklady do ověření skutečného kusu.
