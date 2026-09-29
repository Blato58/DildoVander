# Stav implementace AI kufru

Poslední aktualizace: **29. 9. 2026**. Zdroj stavu: aktuální kód, hostitelské testy a lokální firmware build E2.2, kontrola fixtures E2.1/E2.4b, hostitelský simulátor E2.3 a místní WSS/restart/HTTPS stream test E2.4c–E2.5. Ostatní etapy, hardware, iPhone a CI zůstávají bez runtime akceptace. Hotový návrh není hotová implementace.

[Implementační plán](implementacni-plan.md) určuje architekturu a akceptaci. [AGENTS.md](../../AGENTS.md) určuje pracovní postup AI. Tento soubor je jediný průběžný záznam postupu; README ho pouze odkazují.

## Předání další AI

**Aktivní implementační krok:** E2.6b – zbývající HTTP trasy a integrační hrany. E2.4a–d přidaly parser, handshake/lease kontrakty, TLS socket nad C++ modelem a souborový fake journal s restartovým ověřením (D6–D9). E2.5 přidává webovou časovou osu nad stejnými C++ událostmi (D10), ale její vykreslení čeká na prohlížeči důvěryhodný vývojový certifikát. E2.6a přidává první část backend OpenAPI (D11). E2 ještě není hotové; audio/cleanup HTTP trasy a zbývající integrační zprávy chybí. E1 je dokončené jako softwarové jádro bez desky. Fyzické nezávislé vypnutí při zamrzlé smyčce a další měření zůstávají kritérii E5.

**Další konkrétní krok: E2.6b – dokončení OpenAPI backendu.**

1. Doplnit do `contracts/backend-openapi-v1.json` přepis, syntézu, audio a cleanup podle kapitoly 6, včetně typů výsledků, limitů a chyb; ověřit schéma a pozitivní/chybné požadavky bez cloudového účtu. E2.6a již pokrývá token, sezení, textový tah a polling.
2. Doplnit zbývající zprávy/protokolové integrační hrany. Webová časová osa E2.5 používá samostatný pouze čtecí HTTPS proud se stejnými C++ událostmi: běžné browser WebSocket API neumí hlavičku `Authorization` při nativním handshake. Po získání důvěryhodného místního certifikátu ověřit její skutečné vykreslení, chybový stav a úzký viewport v prohlížeči; nevypínat TLS ověřování.
3. Nezaměnit fake log s NVS ani s fyzickým potvrzením západky. Samostatná HW akceptace zůstává v E5 po vstupu B1.

Předpokládané dotčené cesty: `kufr/contracts/`, následně backend a související README. Krok nevyžaduje fyzickou desku, cloudový klíč ani Apple certifikát. Toto je implementační předání, nikoli příkaz něco nahrát či publikovat.

Pokud uživatel omezí práci pouze na mobilní aplikaci, neoznačuj závislosti E2 za splněné. Lze připravit samostatnou UI část E3 proti výslovně označenému fake transportu, ale E3 zůstane částečné do integrace se simulátorem/protokolem E2. Rozsah a mezeru zapiš sem.

## Jak se eviduje práce

- Stavy etap: **nezahájeno**, **rozpracováno**, **blokováno**, **hotovo**, **odloženo**. Poslední stav používat pro volitelné etapy nebo výslovné odložení, nikdy jako náhradu nesplněného kritéria.
- U rozpracované etapy zapisuj konkrétní dílčí kroky `E1.1`, `E1.2` atd. Přidej dokončený rozsah i zbytek; procenta bez měřitelného podkladu nepoužívej. Jedna malá změna může dokončit jen část etapy.
- `Hotovo` vyžaduje všechna kritéria kapitoly 12 plánu a odkazy na důkazy níže. Software/fake, build, integrace, iPhone a hardware jsou různé úrovně ověření. Chybějící požadované měření ponechá etapu rozpracovanou nebo blokovanou.
- Blokace musí uvést dotčené kritérium, potřebný vstup, kdo ho dodá a nezávislou práci, která může pokračovat. Při částečné blokaci nezastavuj celý projekt. Vyřešení blokace zapiš s důkazem, ne jen změnou barvy/stavu.
- Na začátku práce porovnej záznam s aktuálními soubory. Při rozporu oprav stav s vysvětlením; nevracej cizí změny podle starého záznamu. Při souběhu uvede každý pracovník svůj krok a vlastněné soubory, jeden integrátor zapíše společný výsledek.
- Před koncem pracovního kroku aktualizuj tabulku, aktuální předání a deník. Zaznamenej i neúspěšný test, rozpracovaný kód nebo neprovedenou kontrolu. Další AI musí poznat přesný bod navázání bez historie chatu.
- Ověření zapisuj jako: datum; krok; soubory/revize; pracovní adresář a přesný příkaz; výsledek; důležité omezení. Pro CI přidej URL běhu a commit, pro zařízení revizi PCB/iOS, profil a kalibraci. Commit uváděj jen pokud skutečně existuje; jinak uveď místní necommitované změny. Logy uváděj stručně a bez tajemství.
- Pokud změna zneplatní dřívější test, zapiš „ověření zastaralo“ a zopakuj relevantní část. Historický úspěch nedokazuje stav nového kódu.
- Architektonické změny patří do plánu, nikoli jen sem. Do deníku patří důvod, odkaz na změněnou část plánu, dopad na kontrakty/etapy a potřebné přetestování. Číslování etap zachovej; nové kroky přidávej, nepřečísluj historii.

## Výchozí stav podle zdrojů

| Část | Co skutečně existuje | Co zatím chybí |
| --- | --- | --- |
| Firmware | Čistá neblokující `SelectionSequence`/`SafetyController` z E1 a čistý, zatím nezapojený `DeviceControl` s fake clock vstupy, 64 výsledky a `CommandJournal` portem E2.2. Sériové `select 1..6`, `stop`, `reset`, `status`, `help` zůstává vývojovým vstupem. Hostitelské testy a lokální Arduino/PlatformIO build prošly. | Nezávislý fyzický watchdog/inhibit, skutečný NVS journal/GPIO/síťový detektor lease, provisioning, I2S, dialog a ovládání. |
| Mobil | Statická obrazovka TypeScript/Vite, build script a lockfile. | Interaktivní ruční řízení, transport, simulátor, Capacitor, iOS/Swift projekt a testy. |
| Backend/kontrakty/simulátor | Lokální protokol v `contracts/control-message-v1.schema.json`, 15 pozitivních a 27 negativních kontrol, připnutý Node/npm test; spustitelný C++ simulátor a šest deterministických scénářů E2.3; místní TLS WSS facade nad C++ modelem s auth/lease/retry/Stop a restartovým fake journalem E2.4c/d; webová časová osa a HTTPS pozorovací proud E2.5; OpenAPI token/sezení/text/polling E2.6a. | Backend, zbývající OpenAPI, browser vizuální ověření E2.5, fake/AI adaptéry, zbývající zprávy a integrační hrany. |
| iOS CI | Majitelem popsaný existující postup Windows → macOS runner → podepsaná IPA. | Skutečný workflow v tomto checkoutu, ověřená identita/profil a běh/instalace. |
| Hardware | Dodaný obrázek pinů, návrh napájení a předběžné profily A/B. | Revize konkrétní desky, schéma bezpečnostní větve, měření a fyzická akceptace. |

Zdroje: [firmware](../firmware/README.md), [jeho konfigurace](../firmware/platformio.ini), [sekvence](../firmware/src/features/wand_selection/SelectionSequence.cpp), [hostitelský test](../firmware/tests/selection_sequence_test.cpp), [mobilní vstup](../mobilni-aplikace/src/main.ts), [package.json](../mobilni-aplikace/package.json). Tento přehled aktualizuj, až se skutečný stav změní.

## Etapy

Přesné závislosti a kritéria dokončení jsou v kapitole 12 plánu. Následující tabulka je stav, nikoli jejich zjednodušená náhrada.

| Etapa | Stav | Dokončený rozsah / důkaz | Zbytek nebo podmínka dalšího kroku |
| --- | --- | --- | --- |
| E0 – provozní předpoklady | rozpracováno | Architektonické výchozí předpoklady v plánu; žádná měření. | Vstupy B1–B5 níže; software E1 může začít. |
| E1 – bezpečné jádro | hotovo | E1.1: Stop v každé fázi, safety port, 250/500/5000ms limity, explicitní zotavení (D1). E1.2: hostitelský timeout průchodu smyčkou, porucha, výpadek ručního lease a přednost Stop; hostitelské testy a firmware build prošly (D2). | Fyzická reakce nezávislého watchdogu patří E5 a nebyla měřena; softwarový model vypíná až při dalším průchodu smyčkou. |
| E2 – smlouvy a simulátor | rozpracováno | E2.1: první JSON Schema lokálního protokolu, přísný parser a pozitivní/chybné fixtures (D3). E2.2: C++ model fake času, journalu, idempotence a hostitelské scénáře ztraceného ACK/restartu (D4). E2.3: spustitelná časová osa a šest hostitelských scénářů nad sdílenou fixture (D5). E2.4a–d: Node parser, handshake/lease schéma, místní WSS facade nad C++ modelem a souborový fake journal (D6–D9). E2.5: webová časová osa a test HTTPS proudu (D10). E2.6a: základ backend OpenAPI a validace schémat (D11). | Zbývající OpenAPI, protokolové hrany a browser vizuální ověření E2.5. Kritéria E2 ještě nejsou úplně doložena. |
| E3 – ruční mobil bez HW | nezahájeno | Statická stránka není ruční řízení. | E2; UI, potvrzení/Stop a integrace. |
| E3a – iOS CI základ | nezahájeno | Jen majitelem popsaný postup. | E3; B2/B3 pro podpis a skutečnou instalaci. |
| E4 – runtime a přímá síť | nezahájeno | Arduino prototyp. | E2; pro nahrání B1. |
| E5 – jedna bezpečná přihrádka | nezahájeno | Bez ovládání a měření. | E0/E1/E4; B1, fyzická akceptace. |
| E6 – šest přihrádek | nezahájeno | Bez fyzického ověření. | E5; cykly, proudy, dostupnost/reload. |
| E7 – backend bez skutečné AI | nezahájeno | Bez projektu. | E2; lokální fake bez placeného účtu. |
| E8 – skutečný textový dialog | nezahájeno | Bez adaptéru a evaluace. | E2/E7, na desce E4; B4/B5 před placenou akceptací. |
| E9 – samostatný hlas kufru | nezahájeno | Bez audio cesty. | E4/E5/E8; hardware a hlasová akceptace. |
| E10 – dokončení mobilní zálohy | nezahájeno | Bez native transportu. | E3/E3a/E4/E8; audio po E9; skutečný iPhone. |
| E11 – provozní balení | nezahájeno | Bez provozního nasazení a balíčku. | E6/E8/E9, záloha E10; instalační a provozní zkouška. |
| E12 – obraz telefonu | odloženo | Volitelné rozšíření po základní verzi. | E8/E11 a potvrzený přínos. |
| E13 – OV5640 + SD profil B | odloženo | Jen předběžný návrh B. | E0 pro B/E9/E12; nové měření pinů a zátěže. |

## Chybějící vstupy a blokace

Jde o budoucí podmínky konkrétních etap, nikoli důvod neprovést E1/E2. Další otázky a výchozí rozhodnutí zůstávají v kapitole 13 plánu.

| ID | Chybějící vstup a kdo ho dodá | Co blokuje | Co může pokračovat |
| --- | --- | --- | --- |
| B1 | Majitel/stavitel: revize PCB, měření USB/LED/paměti, mechanika a schéma/měření watchdogu a napájení. | Nahrání do neověřené desky, E5/E6 a HW akceptaci E9/E13. | Hostitelské testy, protokol, simulátor, SW build. |
| B2 | Majitel: skutečný soubor již používaného iOS workflow, Team/Bundle ID, distribuční režim a platná identita/profil do secrets. | Převzetí a ověření podpisové části E3a, vydání E11. | Webový vývoj, příprava nativního projektu a kontrol bez podpisových secrets. |
| B3 | Majitel: model/verze iOS, registrovaný iPhone pro Ad Hoc a vybraný instalační klient/hosting IPA. | Fyzickou instalaci E3a, LAN/Keychain akceptaci E10 a distribuci E11. | UI/protokolové testy; CI simulator test není náhrada iPhonu. |
| B4 | Správce/majitel: účet, AI klíč mimo repo, hosting, region/retence a schválené provozní limity. | Skutečná placená volání a provozní nasazení. | E7 a fake integrační/eval scénáře; připravit konfiguraci bez hodnot tajemství. |
| B5 | Majitel: finální katalog a publikum, rozměry, router a prostředí akce. | Obsahovou/provozní akceptaci E8/E11 a finální mechaniku. | Neutrální testovací katalog a laboratorní/simulované scénáře. |

## Deník změn a ověření

### D11 – 29. 9. 2026 – E2.6a základ backend OpenAPI

- Rozsah: `contracts/backend-openapi-v1.json` popisuje device credential výměnu, založení sezení, textový tah a polling jeho operace; oddělená bearer schémata, UUID idempotency hlavičku, ProblemDetails, šestibitovou masku, limit 1000 znaků, stav operace, echo identit a typovaný validovaný návrh. `verify-backend-openapi.mjs` kontroluje reference, požadované trasy/security a pozitivní/chybné hodnoty. `contracts/README.md` a kapitola 6 plánu vymezují tento dílčí kontrakt.
- Ověření z `kufr/contracts` pod Node 24.21.0: `npm test` — 15 platných a 27 chybných control zpráv a test E2.6a prošly. Backend ještě neexistuje, takže scope, skutečná idempotence, kvóty a retence nebyly ověřeny v HTTP runtime.
- Omezení: dokument má zatím čtyři trasy; WAV přepis, syntéza, binární audio a rušení/úklid chybí. Otevřené detaily těchto tras se neprohlašují za hotové. Pokračuje E2.6b.
- Revize a doručení: E2.6a je v commitu `49ef3f9e283ac36738406a1280383e7d99dc4a80`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D10 – 29. 9. 2026 – E2.5 webová časová osa simulátoru

- Rozsah: `simulator/server.mjs` vystavuje na stejném loopback TLS portu stránku `/simulator` a pouze čtecí `GET /v1/observe` s bearer autorizací, nejvýše čtyřmi pozorovateli a NDJSON snapshot/událostmi ze stejného C++ modelu. `dashboard.html`/`dashboard.js` viditelně označují `SIMULACE`, zobrazují šest přihrádek, fázi, režim, journal, revizi a časovou osu. Stránka neposílá řídicí povely; token použije v HTTPS hlavičce a vymaže vstupní pole. `verify-socket.mjs` kontroluje stránku, script, odmítnutí bez tokenu, úvodní snapshot a právě jednu událost uvolnění po opakovaném příkazu. Plán v kapitole 11 zaznamenává vývojový pozorovací proud odděleně od nativního WSS kontraktu.
- Ověření z `kufr/simulator`: Node `--check` pro `server.mjs`, `dashboard.js`, `verify-socket.mjs` a Node 24.21.0 `npm test` s MSVC bridge a OpenSSL v dočasné složce — úspěšně. In-app Browser pokus o `https://127.0.0.1:8443/simulator` skončil `ERR_CERT_AUTHORITY_INVALID` na dočasném self-signed certifikátu; žádná výjimka z TLS nebyla udělena. Vykreslení, interakce a mobilní viewport proto nejsou potvrzeny.
- Omezení: `GET /v1/observe` je pouze vývojové rozhraní hostitelského procesu; neprokazuje TLS důvěru telefonu, ESP32 síť ani skutečnou mechaniku. K vizuálnímu browser ověření je třeba certifikát důvěryhodný pro testovací prohlížeč. E2 pokračuje E2.6.
- Revize a doručení: E2.5 je v commitu `11c358017a9ca1ca2e7a26d75fb5379a81df3840`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D9 – 29. 9. 2026 – E2.4d fake journal a restart WSS

- Rozsah: `simulator/bridge.cpp` ukládá append-only fake záznamy Begin/Resolve do cesty předané z `KUFR_SIMULATOR_JOURNAL`, při startu rekonstruuje pending a masku; neúplný, chybný nebo po předchozí inicializaci ztracený log selže uzavřeně. Soubor `.lock` vynucuje jediného zapisovatele; Node čeká na skončení bridge před restartem. Po neuzavřeném záznamu nenastává automatické zotavení. Místní proces `run-server.mjs` nabízí servisní simulaci kontroly `inspected`, která přes stejný C++ model označí nejistou přihrádku jako nedostupnou. Node server vystaví fázi/fault z journalu a předá stejnou cestu přes restart. Opravena i falešná terminální událost při vypršení neaktivního lease.
- Ověření: z `%TEMP%\dildovander-e2-simulator` MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` nad `SafetyController.cpp`, `SelectionSequence.cpp`, `DeviceControl.cpp` a `simulator/bridge.cpp` s `/Fe:bridge.exe` — úspěšně. Z `kufr/simulator` pod Node 24.21.0 s `KUFR_SIMULATOR_BRIDGE` a `OPENSSL`: `npm test` — úspěšně. Testy nyní pokrývají restart s otevřeným journalem, odmítnutí takeover, servisní zotavení s maskou 39, další restart s perzistovanou maskou, souběžného zapisovatele, poškozený i ztracený log (mask 0/fault), pozdní heartbeat i odpojení během acquire. Před opravou expirace neaktivního lease WSS test odhalil chybné `selection.result` s prázdným ID; po opravě znovu prošel.
- Omezení: fake log používá flush, neověřuje fyzickou atomicitu zápisu ani výpadek napájení/NVS. Současná kontrola `.lock` byla sestavena a testována na Windows; POSIX větev ještě ne. Simulovaný `inspected` je výslovná akce obsluhy, nikoli důkaz mechanické kontroly. Následuje E2.5.
- Revize a doručení: E2.4d je v commitu `e1ecd3b0fe4ae3b0a9943126726c0b2d401eedba`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D8 – 29. 9. 2026 – E2.4c místní WSS facade

- Rozsah: `simulator/bridge.cpp` je hostitelský proces nad stejným `DeviceControl`, `SelectionSequence` a fake portem/journalem. `server.mjs` přijímá na loopback TLS/WSS jeden socket, vyžaduje bearer v handshake, 10s hello a přísný E2 parser, odmítá kompresi a control payload nad 2048 B. Převzetí, heartbeat, výběr, ACK/retry, Stop, reset a snapshot volají C++ model. Stop předbíhá běžnou frontu, zneplatní před ním přijatý reset a počet čekajících zpráv je omezený. Odpojení zruší lease i po souběžném acquire. `run-server.mjs` čte certifikát, klíč, testovací token a cestu bridge z prostředí; nic z toho není ve zdroji. `verify-bridge.mjs` a `verify-socket.mjs` testují fake čas, pozdní lease, TLS/autorizaci, handshake, jediné uvolnění po retry, Stop se zatíženou frontou včetně staršího resetu, snapshot a chybný JSON. `simulator/package.json`/lockfile připíná `ws@8.21.3` a Node 24.21.0.
- Ověření z `%TEMP%\dildovander-e2-simulator`: MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` nad `SafetyController.cpp`, `SelectionSequence.cpp`, `DeviceControl.cpp` a `simulator/bridge.cpp` s `/Fe:bridge.exe` — úspěšně. Z `kufr/simulator` po nastavení `KUFR_SIMULATOR_BRIDGE` na tento soubor, `OPENSSL` na místní OpenSSL CLI a PATH na Node 24.21.0: `npm ci`, `npm test` — obojí úspěšně; test vytváří krátkodobý certifikát mimo repo a ověřuje jej klientem. Z `kufr/contracts` pod stejným Node proběhlo `npm ci`, `npm test` — 15 platných a 27 chybných zpráv prošlo. Před opravami test odhalil duplicitně čtenou ACK ve verifieru a nesprávný exit po zavření socketu; oba opraveny, opakovaný běh prošel. `npm ci` hlásilo 0 známých zranitelností pro `ws` v tomto lokálním auditu.
- Omezení: `BridgeJournal` je dosud pouze v RAM; restart procesu nedrží pending/masku. Socket test zatím neprokazuje close/acquire závod ani přerušení během motoru, chybí OpenAPI, UI a fyzický hardware. `server.mjs` je vývojový loopback server s dočasnou identitou; nahrání do ESP32 nebo produkční provisioning nebyly provedeny. Další E2.4d výslovně řeší tuto mezeru.
- Revize a doručení: E2.4c je v commitu `8351223d6dbea2d3fc9f75033c3a5114db0f3aa0`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D7 – 29. 9. 2026 – E2.4b zprávy spojení a lease

- Rozsah: plán v kapitole 4 upřesňuje tvar `hello`/`welcome`, `state.get`, `control.acquire/release` a heartbeat, vždy s oddělením tokenu do TLS handshake. `contracts/control-message-v1.schema.json` přidává jejich přísné varianty; sedm platných fixtures a čtyři negativní mutace hlídají verzi, pole navíc, vložený snapshot a lease. Contracts README nyní uvádí rozsah schématu.
- Ověření z `kufr/contracts`: `& 'C:\Users\vojtech.petrik\AppData\Roaming\nvm\v24.21.0\npm.cmd' test` — 15 platných a 27 chybných zpráv správně rozlišeno. Schéma neověřuje identitu, pořadí handshake ani životnost lease; bez WSS testu je E2.4 stále rozpracované.
- Revize a doručení: E2.4b je v commitu `fd3d211e7f0c0a5f91f6a99339cc9827a8aebeef`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D6 – 29. 9. 2026 – E2.4a přenosný vstupní parser

- Převzetí: po doručení E2.3 v `e03672f2fe974a790b374cc1ad5ca5ea4a15451a` začala E2.4. Před WSS vstupem bylo nutné použít skutečná E2.1 pravidla z runtime modulu, nikoli je zkopírovat do síťové vrstvy.
- Rozsah: `contracts/control-validator.mjs` vyčlenil přísné UTF-8/JSON/schema ověření do exportu `validateControlMessage(bytes)`, `verify-fixtures.mjs` ho nyní používá, contracts README popisuje hranici. Žádná změna schématu ani wire kontraktu.
- Ověření z `kufr/contracts`: `& 'C:\Users\vojtech.petrik\AppData\Roaming\nvm\v24.21.0\npm.cmd' test` — 8 platných a 23 chybných zpráv správně rozlišeno. WSS adaptér ještě neexistuje, takže toto není síťová integrace; pokračovat v E2.4.
- Revize a doručení: E2.4a je v commitu `5a55716dfd661355d456cb69b58120ed39b89764`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D5 – 29. 9. 2026 – E2.3 hostitelský simulátor

- Převzetí: E2.2 bylo doručeno v `7b0fbc82996651e5e2387e269a22bb498abe806a` a krok E2.3 zapsán jako aktivní. Čistý model výběru a journalu umožnil proces bez desky.
- Rozsah: `simulator/main.cpp` spouští `DeviceControl`/`SelectionSequence` s řízeným časem, fake portem a journalem; časová osa výslovně značí `SIMULACE`. `simulator/verify-scenarios.mjs` bere ID a přihrádku ze sdílené platné fixture a ověřuje běžné dokončení, dva retry po ztraceném ACK, Stop, ztrátu lease, restart při uvolňování a chybu zápisu. Kontroluje nejvýše jedno uvolnění, vypnutí před Stop, pending journal a masku dostupnosti. `simulator/README.md` uvádí build/běh a hranice důkazu, kořenové README ukazuje nový adresář. Kontrola navíc odmítá neplatné číslo přihrádky na CLI.
- Ověření z `%TEMP%\dildovander-e2-simulator`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /std:c++14 /W4 /WX /EHsc C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\src\features\device_control\DeviceControl.cpp C:\Development\DildoVander\kufr\simulator\main.cpp /Fe:simulator.exe'` — úspěšně bez warningů. Z kořene `& 'C:\Users\vojtech.petrik\AppData\Roaming\nvm\v24.21.0\node.exe' .\kufr\simulator\verify-scenarios.mjs (Join-Path $validationDirectory 'simulator.exe')` s `$validationDirectory = Join-Path $env:TEMP 'dildovander-e2-simulator'` — šest scénářů prošlo. Neplatné `4junk` na CLI vrací exit code 2. Firmware `pio run` se neopakoval, protože se zdroje firmwaru neměnily.
- Neprovedeno: syrová fixture neprochází C++ parserem; použily se jen `commandId` a přihrádka, ostatní pole jsou sestavena podle živého modelu. Neexistuje socket, UI časová osa, skutečný NVS/TLS ani fyzická časová osa. Revize, sekvence, lease a source fixture se proto tímto testem neověřují. Po dokončení doručení E2.3 navazuje E2.4.
- Revize a doručení: E2.3 je v commitu `e03672f2fe974a790b374cc1ad5ca5ea4a15451a`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D4 – 29. 9. 2026 – E2.2 model příkazu a journalu

- Převzetí: E2.1 bylo doručeno v `b2e9b8d9fc2ddb362b2ae31b0a98fad4a53bd12e`; krok E2.2 byl při zahájení zapsán jako aktivní. Schéma E2.1 validuje syrové zprávy, tento krok navazuje až na strukturovaný příkaz; propojení parseru s modelem zůstává na E2.3 a další E2 slices.
- Rozsah: nové `firmware/src/features/device_control/CommandJournal.h` a `DeviceControl.h/.cpp` modelují ruční lease 6 s, `bootId`/revision/pořadí, 64 posledních výsledků, konflikt ID, zamítnutí starého příkazu a zápis journalu před prvním pohybem. Přijetí a spuštění jsou samostatné operace. Při přerušení zůstává journal otevřený; po kontrole obsluhou se označí přihrádka jako nedostupná. Dokončení musí v jedné trvalé operaci uložit novou masku a uzavřít journal. Po selhání uzavření se nesmí emitovat dokončený výsledek. `firmware/tests/device_control_test.cpp` používá fake čas, port a journal; firmware README popisuje omezení.
- Ověření z `%TEMP%\dildovander-e2-validation`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /std:c++14 /W4 /WX /EHsc C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\src\features\device_control\DeviceControl.cpp C:\Development\DildoVander\kufr\firmware\tests\device_control_test.cpp /Fe:device_control_test.exe'`, pak `.\device_control_test.exe` — úspěšně. Scénáře pokrývají ztracený ACK, stejné/změněné ID, 64 výsledků a vytěsnění, neplatnou obálku, zpožděný příkaz, auth/lease, poruchu journalu, restart po přijetí i přerušeném uvolnění, perzistovanou nedostupnost po dokončení a vypnutí při ztrátě lease.
- Firmware build z `kufr/firmware`: `& 'C:\Users\vojtech.petrik\.platformio\penv\Scripts\pio.exe' run` — **SUCCESS**, `espressif32@7.1.3`/Arduino; 18 660 B RAM a 279 157 B flash. První pokus selhal na C++11 inicializaci `CommandReceipt`; po přidání konstruktoru prošel. `DeviceControl` se překládá, ale není zapojený do `main.cpp`, takže velikost výsledné binárky není měřením jeho provozní paměti.
- Revize a doručení: E2.2 je v commitu `7b0fbc82996651e5e2387e269a22bb498abe806a`, `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem. Neprovedeno: skutečná NVS atomicita, síť/parser/TLS, hardware a společný C++/TypeScript/.NET fixture test. Fake ověření nesmí nahradit tyto integrační a fyzické kontroly.
- Předání: E2 zůstává rozpracované. Následuje E2.3 podle horní části souboru.

### D3 – 29. 9. 2026 – E2.1 první sdílené kontrakty

- Převzetí: E1.2 bylo jako softwarové jádro pushnuto na `origin/main`; E2 podle kapitoly 12 smí začít bez desky. Před změnou byl krok E2.1 zapsán jako aktivní.
- Rozsah: nové `contracts/control-message-v1.schema.json` (JSON Schema 2020-12) pro `selection.start`, `command.ack`, `selection.result`, `state.snapshot`, `safety.stop` a `safety.reset`; `contracts/fixtures/` se sdílenými platnými/chybnými zprávami; `verify-fixtures.mjs`, připnuté `package.json`/lockfile/`.nvmrc` a README. Přísný vstup odmítá neplatné UTF-8, duplicitní klíče, desetinný/exponenciální zápis celočíselných polí a zprávy nad 2048 bajtů. Schéma odmítá nadbytečná pole, chybné typy/rozsahy a vynucuje oddělené kódy poruch, bezpečnostní povely a `eventSequence` watermark snapshotu. Kořenové `kufr/README.md` ukazuje existující adresář.
- Ověření z `kufr/contracts`: s Node `24.21.0` a npm `11.19.0` podle `.nvmrc` proběhlo `npm ci` a `npm test` — **8 platných a 23 chybných zpráv správně rozlišeno**, žádná chyba. Přísné schéma se zkompilovalo; `npm ci` nahlásilo 0 známých zranitelností v instalovaných osmi balíčcích. `git diff --check` bez chyb. Neprováděl se firmware, mobilní ani CI build, protože se změnily jen kontrakty a jejich vlastní verifier.
- Revize a doručení: E2.1 je v commitu `b2e9b8d9fc2ddb362b2ae31b0a98fad4a53bd12e`, `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem. Schéma neověřuje identitu/TLS, lease, boot, čas, revision, idempotenci ani journal; tyto stavové kontroly jsou následující E2 slices. Přijímače musí přísný parser a pravidla přenést do svých adaptérů; tento test je nespojuje se skutečnou sítí.
- Předání: E2 zůstává rozpracované. Následuje E2.2 podle horní části souboru; B1/B2/B4 nebrání hostitelskému modelu.

### D2 – 29. 9. 2026 – E1.2 hostitelský timeout a ztráta řízení

- Převzetí: čistý `main` na `f03a1a8`, E1.1 v kódu odpovídalo deníku D1. Krok E1.2 byl při zahájení zapsán jako aktivní.
- Rozsah: `firmware/src/features/wand_selection/SelectionSequence.h/.cpp` rozlišuje samostatný a ruční původ výběru, při ztrátě ručního lease zastaví pouze ruční výběr a při mezeře řídicí smyčky 250 ms vyžádá Stop před dalším sériovým povelem. `firmware/src/features/safety/SafetyController.h/.cpp` uchovává poruchu do výslovného zotavení i při souběžném Stop. `src/main.cpp` provádí kontrolu mezery na začátku průchodu; firmware README popisuje hranici hostitelského modelu. Všechny výstupy zůstávají jen požadavky přes port bez GPIO.
- Hostitelské ověření z `%TEMP%\dildovander-e1-validation-final`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /std:c++14 /W4 /WX /EHsc C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\tests\selection_sequence_test.cpp /Fe:selection_sequence_test.exe'`, pak `.\selection_sequence_test.exe` — obojí úspěšně. Testy navíc pokryly výpadek lease ve všech fázích, bez vlivu na samostatný výběr, poruchu při aktivním motoru/západce, přednost Stop, zachování příznaku poruchy a timeout smyčky včetně přetečení času.
- Firmware build z `kufr/firmware`: `& 'C:\Users\vojtech.petrik\.platformio\penv\Scripts\pio.exe' run` — **SUCCESS** na PlatformIO `espressif32@7.1.3`/Arduino; 18 660 B RAM a 270 493 B flash. `git diff --check` bez chyb. Pomocné `.obj` z prvního hostitelského buildu byly odstraněny, zdroje nebyly dotčeny.
- Revize a doručení: E1.2 je v commitu `d960210bc2352c3318690e3ec470b5f8df94edb6`, `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem. Nenahrávalo se do desky. Softwarová kontrola mezery vypne výstup až při návratu do smyčky; při úplném zamrznutí musí vypnout nezávislý fyzický watchdog/inhibit. To je neověřená podmínka E5/B1, nikoli důkaz E1.2.
- Předání: softwarová kritéria E1 kapitoly 12 jsou hostitelsky doložena; další je E2.1 podle horní části souboru.

### D1 – 29. 9. 2026 – E1.1 Stop a bezpečnostní požadavky bez desky

- Převzetí: po čtení kapitol 5, 8 a 12 plánu, firmware README a živého kódu byl převzat E1.1. Původní hostitelský test prošel přes Visual Studio Build Tools; `g++` a `pio` nebyly v PATH.
- Rozsah: nové `firmware/src/features/safety/SafetyPort.h` a `SafetyController.h/.cpp`; upravené `wand_selection/SelectionSequence.h/.cpp`, `SerialSelectionPort.h/.cpp`, `src/main.cpp`, `tests/selection_sequence_test.cpp` a firmware README. Sekvence po startu i Stop vyžaduje výslovné zotavení; Stop hned volá port pro vypnutí, nepokouší se znovu zamknout západku. Safety controller odmítá souběh motoru a západky; motor je provozně 150 ms, pozdní průchod od 250 ms, západka od 500 ms a sekvence od 5 s vyvolají Stop. Sériový port pouze vypisuje požadavky, žádné GPIO.
- Hostitelské ověření z `C:\Users\vojtech.petrik\AppData\Local\Temp\kufr-e1-validation`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /EHsc /W4 /WX /std:c++14 C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\tests\selection_sequence_test.cpp /Fe:selection_sequence_test.exe && selection_sequence_test.exe'` — průchod bez chyb. Testy pokrývají původní pořadí/validaci, Stop ve všech fázích, opakovaný i externí Stop, zotavení, pozdní průchod, meze 250/500/5000 ms a přetečení čítače včetně aktivního motoru/západky. Celý `src/main.cpp` a sériový adaptér navíc prošly kompilací s dočasnou hlavičkou Arduino mimo repo; tento stub není náhradou skutečného PlatformIO buildu.
- Firmware build z `kufr/firmware`: `& (Join-Path $env:TEMP 'kufr-pio-venv\Scripts\python.exe') -m platformio run` — **SUCCESS**, PlatformIO Core 6.2.0, platforma `espressif32@7.1.3`, Arduino; 18 660 B RAM a 270 217 B flash. CLI bylo nainstalováno do dočasného venv mimo repo; běžný příkaz `pio run` bez tohoto prostředí v PATH nefunguje. `.pio/` je ignorovaný výstup.
- Revize: žádný commit/push; checkout nemá HEAD a všechny původní soubory jsou untracked. Neproběhlo nahrání do desky, měření watchdogu/inhibit, ověření serva ani iPhone/CI. Softwarový Stop vydává požadavek; nepředstavuje důkaz fyzického vypnutí při zatuhlé smyčce.
- Dodatečné doručení 29. 9. 2026: po potvrzení prvního importu celého projektu byl E1.1 zahrnut do commitu `b21725a23be88b89f723fa01b59496058cb89abd` a pushnut na `origin/main` repozitáře `Blato58/DildoVander`. Pravidlo automatického commitu/push po každém dalším dokončeném slice, ověřovací skript a připnutí Node 24.21.0 byly pushnuty v commitu `80fecdebbad9e0b5d136d520e954da66caeedcee`. Trvalý PlatformIO Core 6.2.0 je v uživatelském prostředí; `pio run` znovu prošel, stejně jako `npm ci` a `npm run build` mobilního prototypu pod Node 24.21.0. Tyto buildy nedokazují HW ani iOS akceptaci.
- Předání: E1.2 podle horní části souboru. B1 blokuje jen fyzickou část E5, nikoli další hostitelské testy.

### D0 – 29. 9. 2026 – příprava dokumentace a předání

- Rozsah: sjednocení AGENTS/README a technických podkladů s plánem, založení tohoto stavového souboru. Žádná implementace E1–E13, žádná změna zdrojového kódu ani konfigurace.
- Evidence stavu: čtení firmware zdrojů/testu/PlatformIO, mobilního vstupu/package/lockfile a kontrola nepřítomnosti `.github/workflows/`. Nezávislý read-only průchod potvrdil absenci síťové, AI a nativní mobilní implementace.
- Dokumentační kontrola z kořene repozitáře: inventura `rg --files --hidden -g '!.git' -g '!**/node_modules/**' -g '!**/.pio/**' -g '!**/dist/**'`, čtení Markdown přes `Get-Content -Raw -Encoding UTF8`, následná kontrola odkazů/obsahu v paměti. Výsledek: 61 místních odkazů existuje, všech 15 ID etap se shoduje s plánem, 4 JSON ukázky se parsují, code fences jsou vyvážené. Porovnání `Get-FileHash -Algorithm SHA256` proti inventuře před úpravou: 11 původních Markdown změněno, 1 Markdown přidán, žádný kód/konfigurace smazán nebo změněn. `git diff --check` bez výstupu; protože jsou soubory untracked, rozhodující je kontrola obsahu a hashů, nikoli prázdný diff. Žádná z těchto kontrol není runtime akceptací.
- Nespouštěno: `pio run`, C++ test, `npm ci`, `npm run build`, CI, placená AI, instalace na iPhone a hardware. Důvod: pouze dokumentační zadání.
- Revize: místní necommitované soubory; nevznikl commit/push ani publikace.
- Předání: E1.1 podle horní části souboru. E0 zůstává částečné, hardware ani podpis nejsou podmínkou zahájení E1.

Pro další zápis použij nadpis s ID kroku, datem a konkrétním výsledkem. Pod něj napiš rozsah/soubory, přesné provedené kontroly a výsledky, zbývající akceptaci/blokace a další krok. Dlouhé raw logy, osobní údaje, certifikáty a klíče sem nepatří.
