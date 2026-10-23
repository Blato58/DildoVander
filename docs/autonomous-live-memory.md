# Rozhovor: GPT Live, Realtime 2.1 a kanonická paměť

Rozhodnutí majitele z19.10.2026 nahrazuje starou architekturu D128/D143.
Aktivní implementaci a skutečné důkazy vede
[privátní evidence](development-coordination.md).
Staré receipts jsou historické, nejsou návodem obnovit vyřazený engine.

## Zapojení a kompatibilita

Existují dva produkční enginy: GPT Live `gpt-live-1` (primární) a Realtime
`gpt-realtime-2.1` (výslovná alternativa). Výběr operátora se zachová;
nepřístupný model vrací chybu, nemění se na jiný. Oba používají stejného
českého DildoVandera, skutečný katalog a autoritativní dostupnost. Live tvoří
běžnou řeč, backendové delegace vracejí podklady a validovaný výběr.
Sol vision/strukturované rozhodování ani provider transcription nejsou třetí
rozhovor. Samostatný Responses dialog, STT→dialog→TTS, skriptovaný Live a
oddělený Realtime PTT runtime byly odstraněny. Deterministické fixture adaptéry
slouží testům a označenému simulátoru.

Backend vlastní aplikační stav a SQLite; poskytovatel vlastní svou relaci,
iOS nativní média. ESP32-S3 stále komunikuje s backendem samostatně a vlastní
bezpečnost i fyzickou sekvenci. Telefon neřídí cloud kufru. Katalogová revize,
boot, generace, příkaz a fyzický výsledek se nezaměňují.

`engines.schemaVersion=1` v diagnostice/server modes udává primární engine,
přesný model, konfigurační připravenost, hlasy, podporované tempo, audio vstup
a kamery. Připravená konfigurace není důkaz přístupu účtu; první provider
handshake musí uspět. Aktuální veřejný vstup je audio. Staré textové/voice
route `/turns`, `/opening`, `/transcriptions`, `/speech`, `/realtime-turns`
a staré `dialogMode`/`voiceMode` vracejí410 `conversation_engine_upgrade_required`.
Staré striktní native klienty je nutné aktualizovat instalací IPA; webový
bundle ani backend deployment ji nenahradí. Nový firmware textovou cestu
neinzeruje a odmítá ji explicitně. Offline ruční řízení je nezávislé na AI.

Každý nový host dostává nové conversationId a generaci. Reconnect provideru,
přepnutí enginu a nový host jsou různé události. Současná omezená obnova při
ztrátě streamu vyžaduje výslovný restart; nehraje staré mikrofonní buffery ani
neopakuje fyzické příkazy. Restart používá nový provider session a pouze
oprávněný kontext. Neznámý výsledek fyzické operace vyžaduje rekonciliaci stavu.

## Aktivace, A/B a rollback

Před zásahem do skutečných dat:

1. Ověř release, Git tree, systemd službu, soukromé datové cesty a volné místo.
2. Zastav službu během konzistentního snapshotu. Vytvoř soukromý adresář mimo
   release s mode0700 a kopie mode0600. Použij SQLite backup API pro
   `journal.sqlite`, `journal.sqlite.evening.sqlite` a další konfigurované DB;
   prosté kopírování otevřené WAL databáze není konzistentní záloha. Zahrň
   service environment, device identity, data protection klíče a oddělený DV10
   archiv existujícím privátním recovery postupem. Nevypisuj obsah ani tajemství.
   Pro databáze v obvyklém datovém adresáři lze po zastavení služby použít
   následující příkaz v root shellu VPS. Vytvoří nový soukromý adresář, použije
   SQLite backup API a zkontroluje kopie; žádný obsah databáze nevypisuje.

   ```bash
   python3 - <<'PY'
   import datetime, os, pathlib, shutil, sqlite3
   os.umask(0o077)
   stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
   destination = pathlib.Path('/var/backups/kufr-conversations') / stamp
   destination.mkdir(parents=True, mode=0o700)
   for source in pathlib.Path('/var/lib/kufr-backend').glob('*.sqlite'):
       with sqlite3.connect(source.as_uri() + '?mode=ro', uri=True) as original:
           with sqlite3.connect(destination / source.name) as copy:
               original.backup(copy)
               assert copy.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
   for name in ('backend.env', 'device-identities.env'):
       source = pathlib.Path('/etc/kufr-backend') / name
       if source.is_file():
           shutil.copyfile(source, destination / name)
           os.chmod(destination / name, 0o600)
   print('Private conversation snapshot:', destination)
   PY
   ```

   Samostatný DV10 archiv a klíče zachovej stávajícím privátním recovery postupem;
   tento omezený příkaz není jejich záloha. Snapshot před deployem nebyl samotným
   zdrojovým ověřením proveden; musí být doložen skutečnou provozní receipt.

3. Na izolované kopii spusť kandidáta bez provider credentialů a `integrity_check`,
   porovnej počty a identity archivů/povolení/obrázků; pak ověř restart.
4. Aktivuj ověřený release přes existující pipeline; zkontroluj health,
   capabilities, místní paměť, oprávnění a počty dat. Teprve samostatně
   autorizovaný provider/device test dokazuje dostupnost a reálné médium.
5. Při rollbacku zastav kandidáta a uchovej novější data samostatně. Použij
   předchozí kompatibilní release a konfiguraci na ověřené kopii; starý release
   s destruktivní retencí nesmí otevřít jediný aktuální archiv. Obnova snapshotu
   je výslovná administrativní operace, nikdy automatické přepsání novějších dat.

Migrace jsou aditivní: generace paměti, revize, indexy a archiv obrázků; žádný
reset DB. `LegacyBackendScripted` je pouze stará konfigurační hodnota mapovaná
na autonomní Live, ne dostupný engine. Staré false přepínače lokální/osobní
paměti jsou migrovány na kanonickou místní službu; konkrétní osobní obsah vždy
vyžaduje výslovné ověřené guest binding a longTermAllowed. Cloud zůstává opt-in.
Historické retention klíče se mohou načíst kvůli kompatibilitě, obsah nemažou.

## Skutečné konfigurační klíče

- `OPENAI_API_KEY`: backend credential; bez něj jsou oba enginy unavailable.
- `Backend:DatabasePath`: soukromá SQLite mimo release/checkout; paměť používá
  příponu `.evening.sqlite`. Namespace tvoří AppNamespace+Environment.
- `Backend:GptLive`: omezené rozhodování, pauzy a politika delegací. Produkční
  actor je autonomní. Model Live je pevně gpt-live-1.
- `Backend:Vision:Model`: samostatná image-understanding služba, standardně Sol.
- `Backend:Memory:CloudIndexingEnabled`: volitelný odvozený index. Store IDs
  pro Personal/Motif musí být oddělené, stejně jako Development/Production.
- Limity payloadů, kontextu, času, souběhu a úložiště zůstávají technické.
  Měnové limity aplikace se neobnovují; náklady řídí OpenAI účet.

### Kapacita místního archivu

`Backend:Memory:MaxStoredBytes` (proměnná prostředí
`Backend__Memory__MaxStoredBytes`) má výchozí hodnotu **67 108 864 B (64 MiB)**.
Povoleny jsou celé hodnoty od 2 000 do 1 073 741 824 B (1 GiB), včetně.
Neplatná hodnota zastaví načtení konfigurace s názvem klíče. Dosavadní explicitní
nastavení 2 000 000 B zůstává platné; zvýšení výchozí hodnoty ho nepřepíše.
Změna konfigurace vyžaduje restart backendu.

Limit počítá UTF-8 obsah všech revizí `memory_records` a textů `live_transcripts`
ve společné databázi, napříč rozhovory, zařízeními i namespaces. Nejde o velikost
SQLite souboru ani limit disku: indexy, metadata, tombstones, fronty, další tabulky
a journal potřebují další místo. Deterministická fixture příkazu
`dotnet run --project backend-tests/BackendTests.csproj -- --memory-capacity`
obsahuje 100 rozhovorů, 10 000 českých segmentů po 120 znacích a 500 faktů.
Naměřila 1 325 450 B UTF-8 obsahu (1 290 000 B přepisů a 35 450 B faktů)
a SQLite soubor 4 775 936 až 4 816 896 B ve dvou lokálních bězích (velikost
indexů se může lišit podle rozložení identifikátorů). Výchozích 64 MiB poskytuje přibližně 50násobek
tohoto obsahového vzorku; nejde o slib produkční délky večera ani propustnosti.
Horní mez 1 GiB zachovává konečný technický limit; před zvýšením zkontrolujte
volný disk a zálohy, včetně větší skutečné databáze. Automatické mazání nepřidává.

Administrátorský stav (`/v1/admin/memory`, `memoryStatus` v inspekci rozhovoru
a `memoryProvider` v autentizovaném `/v1/diagnostics`) uvádí `storedBytes`, `capacityBytes`,
`rejectedWrites`, `storageStatus`, `storageIncomplete`, `lastStorageErrorCode`
a `lastStorageErrorAt`. `storageStatus` je aktuální: `available` pod 80 %,
`warning` od 80 % a `full` od 100 %. Odmítnout lze i větší zápis před dosažením
100 %, pokud se už nevejde celý. `rejectedWrites` počítá odmítnuté záznamy či
segmenty, nikoli rozhovory; opakovaný neúspěšný pokus jej může zvýšit znovu.
`storageIncomplete` a poslední kód `archive_capacity` s UTC časem jsou trvalá
historie odmítnutí, nikoli tvrzení, že úložiště právě selhává. Zvýšení kapacity
ani ruční smazání je nenuluje a neobnoví chybějící obsah. Diagnostika neobsahuje
text hosta. Panel Paměť hosta zobrazuje snímek stavu při načtení rozhovoru;
pro nový stav ho načtěte znovu.

Při nedostatku kapacity zůstává starší obsah zachován, včetně revizí. Přepisová
dávka může uložit jen segmenty, které se vejdou, a vrací neúplný výsledek.
Snížení limitu pod obsazené místo nic nemaže, pouze odmítá nové zápisy.
Správce může po ověření kapacitu zvýšit, nebo použít existující výslovně
potvrzené ruční odstranění příslušné paměti. Nezavádí se pruning, expirace
ani automatický úklid obsahu; odstranění textu může uvolnit obsahový limit,
aniž by zmenšilo SQLite soubor na disku.

## Provisioning OpenAI stores

Lokální paměť nepotřebuje cloud ani provisioning. Volitelný index se nastavuje
existujícím `--memory-provision` dry-run/verify/create postupem, s předchozí
samostatnou autorizací externích zápisů. Neukládej skutečná guest data do
vývojového indexu. Nový lokální fakt je použitelný hned, indexing nikdy
neblokuje zahájení hovoru. Vrácené cloudové texty nejsou autorita; reference se
znovu načtou z kanonického store a prověří oprávnění i revize.

### GitHub Actions na produkčním runneru

Používej existující CI/build artefakt a deploy workflow v soukromém
`Blato58/DildoVander-deploy`; ověř shodu Git tree. Release defaults jsou nový
runtime, nikoli starý dialog. Staré workflow ověřující `/opening`/textové
repliky není akceptací enginů a vyžaduje aktualizaci dříve než jej použiješ.
Soukromá konfigurace/credentialy nejsou součástí source ani artefaktu.

## Paměť, povolení a administrace

SQLite archiv odděluje finalizované události, odvozené fakty a kompaktní kontext:

- Současný host: vlastní finalizované výroky, opravy, průběh a vizuální podklady.
- Večer: jen anonymní ověřené motivy pro variabilitu; žádné cizí syrové rozhovory.
- Návrat hosta: pouze výslovně ověřená stabilní identita s longTermAllowed.
  Jméno, hlas, podobný příběh ani fotografie nejsou identifikační mechanismus.

Finalizované události se zapisují idempotentně během relace, včetně dříve než
vznikne výběr. Assistant vtip/lore a vizuální odhad nejsou osobní fakta. Oprava
superseduje odvozený kontext; historický text zůstává v archivu. Oddělené
konverzační deletion/fact generace brání pozdnímu zápisu, oprava neukončí hlas.
Potvrzený výsledek pochází z autoritativního zařízení, návrh ani vyslovení
hůlky nedokládá její předání. Časovaná prezentace není měřená poloha.

Admin credential používá stávající nativní Keychain profil, nikdy provider key:

- `PUT /v1/admin/memory/guest`: deviceId,conversationId,guestId,identitySource,
  identityVerified,longTermAllowed,cloudExportAllowed,expectedVersion.
  Binding před první řečí; revokace zneplatní běžící kontext.
- `GET /v1/admin/memory/context?deviceId=...&conversationId=...&personalityId=...`:
  identity,epoch,deletionEpoch,records,totalRecords,hasMore,memoryStatus.
  Nejvýše50 záznamů po kontrole scope/oprávnění, enumy jsou pojmenované řetězce.
- `PUT /v1/admin/memory/records/{id}` a `DELETE .../records/{id}` kontrolují
  očekávanou revizi. `DELETE /v1/admin/memory/scopes/{conversation|guest|evening}/{id}`
  přijímá deviceId,expectedEpoch; lokální obsah je smazán/tombstonován hned,
  odvozený vzdálený cleanup má explicitní pending/complete stav.
- `DELETE /v1/admin/conversations/{id}/media?deviceId=...` smaže archiv obrázků
  a ukončí kontext, který ho mohl již spotřebovat. Scope deletion zahrnuje média.

## Retrieval, outbox a retence

Eligibility podle namespace, device, guest/scope, oprávnění a revize předchází
rankingu/limitům. Úspěšná nula z cloudu, čekající index, timeout ani vypnutý
cloud nezakrývají lokální fakta. Startup kontext vzniká lokálně bez síťového čekání.
Index má trvalý idempotentní outbox, omezené retry a tombstones proti vzkříšení.
Přepnutí hosta/enginu/večera ani změna oprávnění archiv nemaže.

Obsah se zachovává do explicitního ručního smazání. Staré24h/30d retence a
automatické ořezávání obsahu neplatí. Při kapacitě se odmítne nový zápis
s diagnostikou, starší obsah zůstává. PCM buffery, provider relace, tokeny,
technické logy a idempotency cache mají vlastní krátkou životnost.

## Audio, playback a fyzická prezentace

Pro telefon byl zvolen trvalý autentizovaný backendový WebSocket. Zachovává
stávající backendové delegace/device topology a odstraňuje sériové200ms HTTP
úzké místo bez přesunu provider credentialu do telefonu. Každý rámec má
inputId/sequence; ACK ledger omezuje nevyřízené bajty a dobu, chybu nereplayuje.
Realtime má skutečný poloduplex; Live telefon full duplex s nativním voice
processing. ESP bez AEC zůstává poloduplex, neaktivuje mikrofon proti reproduktoru.

Swift vlastní jediný microphone/audio engine a AVAudioSession, převádí skutečný
vstupní formát do24kHz mono. Generace oplocují pozdní callbacky. Interruption,
route změna, background a modal kamery jsou rozlišené; po neobnovitelné změně
je dostupný výslovný restart. Kamera permission/capture/retake/analýza jsou
volitelné a nesmí ukončit zdravý hlas. Staré audio se po cancel neobnoví.
Odebrání fotografie ruší probíhající odpověď a posílá oběma enginům společný
`audio_cancel` s rostoucí `responseGeneration` a příslušnou `visualRevision`.
Obyčejné přerušení řeči bez visualRevision nesmí předčasně uvolnit čekání
na odebrání fotografie. Klient vyprázdní playback a
odmítne starší audio/started/completed; `/played` potvrzuje stejnou generaci.
Backend obnoví pouze provider spojení bez odvolané fotografie a odvozených
odpovědí; app relace hosta a vstupní transport zůstávají. Samotná změna
instrukcí není zárukou odstranění již připravené řeči. Opakování uploadu
stejného schváleného konceptu používá nový immutable image ID a vyšší revizi,
přičemž zachová původní capture time a soukromý archiv předchozího pokusu.
Při vynuceném resetu nemusí staré provider spojení doručit závěrečné usage.
Audit v takovém případě ponechá konzervativní rezervaci za přerušené spojení;
`costMicroUsd` není doklad skutečně vyúčtované částky. Skutečnou spotřebu
určuje OpenAI účet. Tento odhad nezavádí místní peněžní limit.
Playback ACK popisuje klientské dohrání. Přesné zarovnání každého textového
slova se skutečně slyšeným PCM není prokázané; generovaný přepis není doklad
celého poslechu ani fyzické akce.

## Akceptace a poslechové A/B scénáře

Automatizace pokrývá dvě readiness cesty, skutečné provider contract fixtures,
více než dvě výměny, DV01 opravu, identity/late-task fencing,240 cizích
záznamů, zero-hit/timeout/disabled index, retention/restart/tombstones,
WS pořadí a zpožděné ACK, a syntetický desktop/mobilní UI. Test-double není
provider. macOS CI musí zkompilovat Swift/native harnessy a Simulator app.

Samostatně zbývá ověřit skutečný účet obou modelů, iPhone permission→camera→
retake→orientation→analysis→využití správné fotky, speaker/headphones/Bluetooth,
background/lock/network a dlouhé opakované cykly. Kufr má vlastní nezávislý
audio/camera/presentation test; simulovaná dostupnost není fyzické měření.
Přesné příkazy, výsledek běhů, release a mezery jsou v aktuálním tracking receipt.

## Dokončení externí přejímky

Následující postup čeká na samostatně autorizované nasazení, podepsání/instalaci
na iPhone a placené provider relace. Metadata modelu ani zelené CI tyto kroky
nenahrazují. Zapiš Git tree backendu, verzi/build nainstalované aplikace,
model/hlas, iOS/model telefonu, audio route a u kufru firmware digest/revizi
zapojení. Použij výhradně syntetického hosta a zaznamenej UTC a technické ID
relace; credentialy, audio ani fotografie nepatří do diagnostického reportu.

1. V iOS **Debug** obnov stav serveru a zvol **GPT Live**. Zahaj nový rozhovor,
   proveď nejméně čtyři výměny, sděl syntetický fakt, oprav ho a ověř pozdější
   odpověď. Opakuj po výslovném přepnutí na **Realtime 2.1**; ověř efektivní
   model, žádný automatický fallback a srozumitelnou chybu při nedostupnosti.
   V obou případech ověř skutečný katalog a zamítnutí neplatné/nedostupné hůlky.
2. S výslovně ověřeným testovacím guest ID a souhlasem ukonči relaci a vrať
   stejného hosta; v inspektoru ověř opravený fakt. Nový anonymní host nesmí
   obdržet jeho osobní obsah. Ručně smaž testovací paměť/archiv a ověř po
   restartu backendu i po dokončení dříve čekající práce, že se obsah nevrátil.
3. V každém enginu pořiď fotku před i během řeči: první souhlas, zamítnutí a
   návrat ze Settings, přední/zadní kamera, portrét/krajina, retake a Cancel.
   Ověř postup uploading→analysing→available a použití právě této revize.
   Přeruš přenos, opakuj stejný koncept a odeber fotku při mluvení: stará řeč
   musí skončit, hlasová relace zůstat použitelná a pozdní výsledek nesmí
   přejít k novému hostovi. Soukromý archiv smaž samostatnou ruční operací.
4. Proveď přerušení vlastní řečí, delší pauzu, šum a zpoždění nástroje;
   opakuj se speakerem, sluchátky a Bluetooth. Během běžící relace vyvolej
   camera modal, background/foreground, lock/unlock, změnu route, ztrátu
   sítě a latenci nad200ms. Ověř bounded recovery nebo výslovný restart,
   žádné obnovení zrušeného audia, duplicitní tah ani záměnu hosta. Opakuj
   deset cyklů start/stop/nový host a porovnej počty otevřených relací,
   audio resources a velikost front; zaznamenej délku a pozorovaný výsledek.
5. Samostatně na identifikovaném ESP32 ověř přímý backend audio/camera tok,
   poloduplex bez AEC a výběr1–6 proti aktuálnímu snapshotu a kalibraci.
   Odpoj pozorovatelský telefon; kufr má pokračovat autonomně. Stop/chyba
   nesmí být vydávány za dokončenou prezentaci. Fyzický výsledek a časované,
   neměřené polohy zapisuj odděleně od simulátoru a provider přepisu.

Kroky1–2 pokrývají scénáře1–6 původního zadání, kroky3–4 scénáře7–11 a13,
krok5 scénář12. Každý neprovedený bod zůstává neověřený; výsledek musí
uvést konkrétní engine a zařízení. Přesné přiřazení slyšených slov k PCM se
netvrdí, pokud bylo zjištěno pouze vygenerování nebo zařazení do fronty.

## Ověřené oficiální kontrakty (19.10.2026)

[Live model](https://developers.openai.com/api/docs/models/gpt-live-1) uvádí
audio/text, proto obrázky zpracovává vyhrazená vision služba.
[Live WebSocket](https://developers.openai.com/api/reference/resources/live/primary-websocket)
a [delegace](https://developers.openai.com/api/docs/guides/live-delegation) mají
vlastní event vocabulary; delegace neobsahuje automaticky uživatelský text a
aplikace ruší vlastní backendové úlohy.
[Realtime model](https://developers.openai.com/api/docs/models/gpt-realtime-2.1)
a [Realtime conversations](https://developers.openai.com/api/docs/guides/realtime-conversations)
potvrzují image input a WebSocket PCM/interruption. Lokální pinned SDK a fixture
ověření podporují implementaci, přístup účtu a skutečná kvalita vyžadují živý test.
