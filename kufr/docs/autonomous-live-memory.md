# Autonomní GPT-Live a hybridní paměť (D143)

Implementace je opt-in. Běžnou řeč tvoří `gpt-live-1`; samostatný model
rozhoduje pouze o skutečných kandidátech. Od D148 lze pro tuto úzkou úlohu
zvolit rychlou `gpt-6-luna` s `reasoning=none`, 512 výstupními tokeny a
vlastní cenovou kontrolou. Původní `gpt-6.1-sol` zůstává výchozí i možnost
rollbacku. SQLite v `EveningMemoryStore` je zdroj pravdy,
OpenAI Vector Store je odvozený index. Výchozí konfigurace zachovává
`LegacyBackendScripted` a vypnutou hybridní/cloudovou/osobní paměť.
Nasazení, aktivace, placená zkouška, export skutečných hostů a fyzická
prezentace nejsou součástí lokálního ověření této změny.

## Zapojení a kompatibilita

`PersonalityCatalog` dál vlastní `party-original` a `ollivander-trunk`,
jejich názvy, popisy i zdrojové SHA-256. Volba v `server_settings.personality`,
admin modes a mobilní výběr zůstávají. Zdrojové embedded resources jsou
`Kufr.Backend.VoicePersona` → `ai-eval/voice-persona.txt`,
`Kufr.Backend.OllivanderPersona` → `ai-eval/ollivander-trunk-persona.txt`
a `Kufr.Backend.PartyPrompt` → `ai-eval/prompt.txt`. Nemají změněný obsah.

Nový `Kufr.Backend.OllivanderLivePersona` čte samostatný celý
[`ollivander-trunk-live-persona.txt`](../ai-eval/ollivander-trunk-live-persona.txt).
`LivePromptSnapshot` přidá jednu společnou důvěryhodnou provozní politiku,
verzi `autonomous-live-v3` (od D150) a SHA-256 sestavených instrukcí. Tento hash
nenahrazuje zdrojový `PromptSha256`. Při vytvoření rozhovoru se zmrazí
personalityId, prompt, verze, režim a výsledná politika. Administrace
stále odmítá změnu postavy při aktivním hovoru. Sol/text, Realtime/PTT,
fake a legacy Live dál používají původní zdroje a dosavadní větve.

Ollivanderův Live kufr obvykle potřebuje dvě odpovědi, výjimečně třetí;
může vybrat dřív a přímé „prostě mi nějakou vyber“ končí diagnostiku.
„Nevím“ je použitelná odpověď. Party má vlastní výchozí rytmus 4–7,
nikoli Ollivanderovu adaptaci. Výslovné `ConversationPolicy` nastavení
má přednost před výchozími hodnotami; prompt i rozhodování dostanou
stejný `LiveConversationPolicy`. Přehled otázkových kandidátů je
konzervativní aplikační projekce časovaných přepisů, **není přesným
sémantickým měřením**. Neodvozuje otázky od počtu delegací, paketů
ani samotných otazníků. Opakování neslyšeného a pokračování téže
odpovědi nejsou další diagnostická odpověď.

`WandDecisionService` používá nezávislou volbu `Backend:GptLive:DecisionModel`,
oficiální .NET SDK 2.14.0 a vlastní strukturovanou funkci `decide_wand`. Není spojena
s legacy `Reply`. Výsledek má stav, platnou přihrádku 1–6 nebo null,
reference a přesné citace odpovědí, krátké důvody, chybějící informaci,
revizi vstupu, snapshot dostupnosti a stav nepublikovaného návrhu.
Prázdný ReadyMask se vyhodnotí bez modelu. Citace musí existovat
v aktuálních podkladech; oprava konzervativně vyřadí starší konfliktní
kandidátní podklady. Humor není důkaz vlastnosti hosta ani katalogu.

Live samostatně přivítá, reaguje, roastuje, ptá se a oznámí výsledek.
Ověřený výběr se publikuje automaticky bez souhlasu hosta. Od D150
společná politika, Ollivanderova Live adaptace i bezprostřední pokyn k
verdiktu vyžadují pouze oznamovací věty, včetně závěrečného rýpnutí.
Verdikt končí mlčením, bez potvrzovací, diagnostické i řečnické otázky
nebo výzvy k hodnocení. Dobrovolná podstatná oprava hosta dál zneplatní
starý návrh; není povinnou podmínkou výběru.
Obyčejná replika nevolá `TextDialogRunner`, další přepisování výsledku
modelem ani STT → text → TTS. Delegace používá skutečné `delegation.id`,
`target=client`, `offset_ms`; událost neobsahuje function name/arguments
ani text zadání. Kontext vznikne z časované projekce a stavu aplikace.
Delegace bez odpovědi čeká omezeně a pak žádá krátké zopakování;
neprovede rozhodnutí nad prázdným nebo starým neodpovězeným kontextem.
Stejné ID je idempotentní, ekvivalentní běžící práce se neduplikuje.
Pozdní výsledek jiné revize se nepředá ani nepublikuje; jeho náklad
se nezapomene. Zrušený placený požadavek s nejasnou spotřebou ponechá
konzervativní rezervaci. „Přestaň mluvit“ nezruší fyzickou operaci.

## Aktivace, A/B a rollback

Nejprve aktualizovat backend a nativní klienty, které rozumějí
`playback_checkpoint`. Starý klient s autonomní větví nedostane
neověřený návrh, ale nemusí dokončit stream. Samotné změny zdrojového
kódu produkci nepřepínají. Po samostatném oprávnění ke zkoušce nastavit
v existujícím serverovém konfigurátoru / prostředí:

```text
Backend__GptLive__ActorMode=AutonomousActor
Backend__Memory__Enabled=true
Backend__Memory__CloudIndexingEnabled=false
Backend__Memory__LongTermPersonalEnabled=false
Backend__Memory__AppNamespace=YOUR_INSTALLATION
Backend__Memory__Environment=Development
```

Stávající dialog/voice musí být dostupné pro AI a klient zvolí `gpt-live`;
persona se vybírá dosavadní administrací. `Enabled=true` bez cloudové
indexace poskytuje funkční lokální kontext. Teprve po povoleném
provisioningu zapnout `CloudIndexingEnabled=true` a nastavit obě store ID.
Dlouhodobou paměť zapnout zvlášť; ani její globální zapnutí samo neověří
hosta a neudělí souhlas s exportem.

Rollback: ukončit zkušební interakce a vrátit
`Backend__GptLive__ActorMode=LegacyBackendScripted`. Výběr personality
zůstává. Pro vypnutí nových zápisů nastavit současně
`Backend__Memory__Enabled=false`, `Backend__Memory__CloudIndexingEnabled=false`
a `Backend__Memory__LongTermPersonalEnabled=false` (validace vyžaduje
Enabled pro oba závislé přepínače);
za dostupného klíče a stores se dál zpracuje již povolené mazání/retence.
Nedestruktivní přidané tabulky není potřeba při rollbacku odstranit.
Vypnutí feature samo neodstraní už uložené cloudové soubory.
Oddělit databázové cesty, namespace a stores vývoje a produkce.

## Skutečné konfigurační klíče

V JSON patří klíče do `Backend:GptLive`; environment nahrazuje `:` za `__`.
Vzor [`appsettings.json`](../backend/appsettings.json) má bezpečné výchozí
hodnoty, prázdné store ID a žádný klíč.

| Klíč pod `Backend:GptLive` | Výchozí hodnota / validace |
| --- | --- |
| `ActorMode` | `LegacyBackendScripted` nebo `AutonomousActor`; přesná velikost písmen |
| `DecisionModel` | `gpt-6.1-sol` (výchozí) nebo `gpt-6-luna`; jen faktický autonomní výběr |
| `AdultLanguage` | `true`; false vypne vulgarity/sexuální narážky |
| `ConversationPolicy:UsualAnswers` | nezadané → Ollivander 2 / party 4; 1–7 |
| `ConversationPolicy:MaximumAnswers` | nezadané → Ollivander 3 / party 7; 1–8 a ≥ usual |
| `TranscriptCharacterLimit` | 128000; 16000–256000, archiv se omezí bez ukončení audia |
| `DecisionContextBytes` | 8000; 2000–8000; všechny důležité odpovědi v rozpočtu |
| `DelegationContextWaitMs` | 350; 0–2000, pouze debounce revize po delegaci |
| `InputQuietMs` | 1200; 500–3000, RMS ticho mikrofonu před backendovou řečí a publikováním |
| `DelegationTimeoutMs` | 8000; 2000–20000, zotavení chybějícího kontextu |
| `OutputPauseMs` | 900; 500–3000, odhad hranice PCM segmentu |
| `TerminalQuietMs` | 4000; ≥ OutputPauseMs a ≤ 10000, heuristika závěru |
| `TerminalTimeoutSeconds` | 35; 15–60, neověřený přednes končí chybou |
| `SpeechAmplitudeThreshold` | 256; 32–4096, PCM16 amplituda pro heuristiku |

| Klíč pod `Backend:Memory` | Výchozí hodnota / validace |
| --- | --- |
| `Enabled`, `CloudIndexingEnabled`, `LongTermPersonalEnabled` | všechny false; cloud/personal vyžadují Enabled |
| `AppNamespace` | `kufr`, 1–64 ASCII písmen/číslic/`-_.`; instalaci pojmenuje správce |
| `Environment` | `ASPNETCORE_ENVIRONMENT`, potom `DOTNET_ENVIRONMENT`, jinak `Production`; explicitní hodnota má přednost, stejné limity jako namespace |
| `PersonalStoreId`, `MotifStoreId` | prázdné; odlišné `vs_…`, ≤128; konfigurace má přednost před provisioned `server_settings` |
| `ContextTokens` | 500; 300–700, jen vybraná paměť, ne celé instructions |
| `SearchResults` | 5; 1–5, nula nalezených je platný výsledek |
| `TimeoutSeconds` | 15; 1–60 pro REST požadavek |
| `Concurrency` | 2; 1–4 workerů |
| `MaxAttempts` | 6; 1–20, pak dohledatelná failed úloha |
| `RetryBaseSeconds` | 2; 1–60, exponenciální backoff + jitter, respektuje Retry-After |
| `IndexPollSeconds` | 2; 1–30 |
| `IndexTimeoutSeconds` | 120; 10–600 |
| `ConversationRetentionHours` | 24; 1–720 |
| `MotifRetentionDays` | 1; 1–30 |
| `PersonalRetentionDays` | 30; 1–365 |
| `MaxWritesPerDay` | 2000; 1–2000 |
| `MaxSearchesPerDay` | 100; 1–1000 |
| `MaxUploadsPerDay` | 500; 1–2000 |
| `MaxStoredBytes` | 2000000; 2000–2000000, nové aktivní texty + časované přepisy |
| `MaxRecordBytes` | 2000; 200–2000 UTF-8 |
| `MaxPendingJobs` | 4000; 1–10000, včetně failed úloh |

Serverový `OPENAI_API_KEY` zůstává ve stávajícím bezpečném mechanismu.
Nepatří do souborů verzovaných v gitu, firmware, telefonu ani URL.
Chybějící klíč / store ID má diagnostický stav, nikoli automatické
vytváření resources či fake úspěch. Od 9. 10. 2026 peněžní rozpočet řídí
pouze OpenAI účet; model a časové limity zůstávají. Paměť má zvlášť trvalé denní
objemové kvóty `memory_usage`, objem obsahu a backlog. Výchozí worker
nevytváří shrnutí pomocí LLM; ukládá skutečný přepis a explicitní tvrzení
na významové hranici. Nevzniká modelové volání za každý fragment.

## Provisioning OpenAI stores

CLI pracuje se stávajícím `Backend:DatabasePath` a instance lockem.
Pro provedení nad servisní databází použít plánované údržbové okno,
nikoli souběžný zápis vedle běžícího backendu. Převzít konfiguraci
z jeho bezpečného prostředí. Store ID nejsou osobní údaje ani API klíče.
Níže jsou **připravené příkazy**, `--execute` se při implementaci nespouští.

Dry-run nic nevolá do OpenAI a nic neexportuje. `--create` bez `--execute`
jen ukáže požadovaný plán:

```sh
# Z kořene repozitáře; nakonfigurovat skutečnou servisní DB pro další použití.
Backend__DatabasePath=/tmp/kufr-memory-dry-run.sqlite \
ASPNETCORE_ENVIRONMENT=Development \
dotnet run --project kufr/backend/backend.csproj -c Release --no-launch-profile \
  -- --memory-provision --create
```

Připojení existujících stores: v bezpečné konfiguraci nastavit
`Backend__Memory__PersonalStoreId=vs_YOUR_PERSONAL_STORE` a
`Backend__Memory__MotifStoreId=vs_YOUR_MOTIF_STORE`. Poté teprve
s oprávněním ověřit přístup a uložit ID:

```sh
dotnet run --project kufr/backend/backend.csproj -c Release --no-launch-profile \
  -- --memory-provision --execute
```

Vytvoření chybějících stores vyžaduje oba výslovné přepínače:

```sh
dotnet run --project kufr/backend/backend.csproj -c Release --no-launch-profile \
  -- --memory-provision --create --execute
```

Vytvoří nejvýše dva chybějící stores a jejich ID uloží okamžitě,
takže přerušení druhého kroku znovu nevytvoří první. Metadata vytvořeného
store obsahují namespace, prostředí a scope. Pokud je existující store
má, provider kontroluje shodu. Explicitně připojený starší store bez
metadat zůstává použitelný; souborové filtry a lokální mapování dál
vynucují izolaci. Chybějící konfigurace nevytváří stores při startu.
Provisioning nevkládá vzpomínky. Cloudovou indexaci aktivuje zvláštní
konfigurace po ověření, nikoli CLI vytvoření resource.

### GitHub Actions na produkčním runneru

Workflow `kufr-memory-provision.yml` ověří úspěšný CI artifact a shodu
s aktuálním sloučeným `main`, poté spustí stejné backendové CLI. Nevyžaduje
širší sudo ani přístup do databáze běžící služby. Jeho trvalý journal
`${GITHUB_WORKSPACE}/../.kufr-memory-provisioning-production/journal.sqlite`
obsahuje pouze technická provisioning ID, nikoli paměť hostů. Adresář má
0700 a soubory 0600, leží mimo checkout i RUNNER_TEMP a musí se zálohovat
s konfigurací runneru. První vytvořené ID zůstává uložené i při chybě
druhého kroku. Přesun na jiný runner vyžaduje přenos tohoto journalu nebo
výslovné zadání existujících IDs; jeho ztráta není pokyn vytvořit nové stores.
Namespace je `kufr:Production`; jiná instalace nesmí sdílet tento journal.

```sh
# CI_RUN_ID nahradit úspěšným CI během pro aktuální sloučený main.
gh workflow run kufr-memory-provision.yml --ref main \
  -f build_run_id=CI_RUN_ID -f action=dry-run
gh workflow run kufr-memory-provision.yml --ref main \
  -f build_run_id=CI_RUN_ID -f action=create
# Opakované ověření existujících stores, bez vytváření:
gh workflow run kufr-memory-provision.yml --ref main \
  -f build_run_id=CI_RUN_ID -f action=verify
```

Existující stores lze poprvé zadat přes `personal_store_id` a
`motif_store_id`; alternativně environment variables GitHub Actions
`KUFR_MEMORY_PERSONAL_STORE_ID` a `KUFR_MEMORY_MOTIF_STORE_ID`.
Konflikt s trvale uloženými ID se odmítne, nepřesměruje existující index.
API klíč je pouze secret `OPENAI_API_KEY` environmentu `backend-production`;
dry-run jej CLI ani nepředá. Žádný režim provisioning workflow nenahrává
soubory hostů. Při nejasném výsledku prvního POST a chybějícím ID ověřit
OpenAI resources správcem; POST automaticky neopakovat naslepo.

Deployment čte tyto dvě nastavení do `appsettings.Production.json`
konkrétního release. Parametry `live_actor_mode` (výchozí
`LegacyBackendScripted`), `hybrid_memory` a `cloud_indexing` (oba false)
mapuje na `Backend:GptLive:ActorMode`, `Backend:Memory:Enabled` a
`Backend:Memory:CloudIndexingEnabled`. `LongTermPersonalEnabled` zůstává
false. Zapnutí hybridní paměti vyžaduje obě provisionovaná IDs; cloud
navíc zapnutou paměť. Režimy/modely se nemění automaticky; softwarové
peněžní capy byly na přání majitele 9. 10. 2026 odstraněné.

Od D148 `live_decision_model` volí nezávisle `gpt-6.1-sol` (výchozí)
nebo `gpt-6-luna`. `live_terminal_quiet_ms` má výchozí 4000 a v tomto
workflow rozsah 900–10000 (výchozí OutputPauseMs=900); 1500 zkracuje
čekání po kompletní závěrečné řeči o 2,5 s. Konec stále vyžaduje celý
název hůlky, ukončenou větu, klid audia i přepisu a místní drain/ACK.
Nové věcné sdělení hosta před close závěr zneplatní.
Vzor aktivace již provisionovaných stores s aktualizovanou IPA D146:

```sh
gh workflow run kufr-backend-deploy.yml --ref main \
  -f build_run_id=CI_RUN_ID -f dialog_mode=responses \
  -f dialog_model=gpt-6.1-sol -f voice_mode=openai \
  -f live_actor_mode=AutonomousActor -f live_decision_model=gpt-6-luna \
  -f live_terminal_quiet_ms=1500 -f hybrid_memory=true -f cloud_indexing=true
```

Textový a legacy model zůstává Sol. Volba v existující aplikaci má
historický název **GPT-Live + Sol**; autonomní serverový režim použije
nastavený rozhodovací model. `GET /v1/diagnostics` nyní vrací
`liveDecisionModel`, `liveTerminalQuietMs`, `memoryEnabled`,
`cloudIndexingEnabled` a `longTermPersonalEnabled`. Post-deploy kontrola
ověří skutečné hodnoty i stores, bez generování rozhovoru.
Před aktivací workflow ověří přístup vybraného modelu přes OpenAI Models
GET, bez placené inference; chybějící přístup zastaví nasazení ještě
před restartem služby. Metadata sama nepotvrzují kvalitu výběru.
Log `live_decision_completed` měří v `durationMs` lokální uložení kontextu,
frontu a rozhodovací API, s operationId/traceId a stavem; neobsahuje text
hosta, audio ani prompt. Od D151 zachytí i stav `error` při výjimce a
`live_terminal_state/decision_running` začátek práce. Pro skutečnou latenci hlasu a správnost výběru
je stále potřebný poslech při použití.

```sh
gh workflow run kufr-backend-deploy.yml --ref main \
  -f build_run_id=CI_RUN_ID -f dialog_mode=responses \
  -f dialog_model=gpt-6.1-sol -f voice_mode=openai \
  -f live_actor_mode=AutonomousActor -f hybrid_memory=true -f cloud_indexing=false
```

Autonomní režim vyžaduje klienty s `playback_checkpoint`; IPA publikovaná
v D146 jej má, starší IPA/firmware tuto podmínku nesplňují. Před jejich aktualizací nasadit
`live_actor_mode=LegacyBackendScripted`. Rollback chování stejným aktuálním
artifactem: `LegacyBackendScripted`, `hybrid_memory=false`,
`cloud_indexing=false`. ID stores zůstávají; rollback prostředky nemaže.
Root-owned helper při neúspěšném health checku vrací předchozí release.
Po aktivaci se ověří veřejné HTTPS, admin/device oddělení, skutečný actor
a paměťový provider; pokud jsou stores nakonfigurované, dvěma GET ověří
jejich přístup a scope. Nejde o placený hlasový či poslechový test.

## Paměť, povolení a administrace

Provozní záznam 6. 10. 2026: oba stores jsou vytvořené/ověřené a backend
nasazený s `LegacyBackendScripted`, lokální pamětí true, cloud indexací
a dlouhodobou osobní pamětí false. Konkrétní resource IDs, release,
příkazy a výsledky jsou ve [stavu D145](stav-implementace.md). Provisioning
neexportoval žádné vzpomínky; nativní klientské a poslechové ověření
autonomní varianty stále čeká na provedení.

Schema 2 rozšiřuje existující `.evening.sqlite`, původní večery a výměny
zachová. Staré výměny mají jednorázovou projekci `LegacyUnverified`, bez
povoleného použití a exportu. Nepřidělují se lidem podle jmen. Nové
tabulky: `memory_records`, `memory_guests`, `memory_indexes`,
`memory_outbox`, `memory_meta`, `memory_usage`, `live_transcripts`.
Před servisní migrací zachovat obvyklou bezpečnou zálohu SQLite.

Aktuální rozhovor má vlastní časovanou projekci obou mluvčích a lokální
`Conversation` fakta, která nejsou cloudová. Fragmenty se zachovají
včetně `start_ms`/`end_ms`, eventId, reference a session generation.
Starší důležité odpovědi se netrimují na dvě poslední výměny. Nový host
vzniká explicitním create/start; osobní kontext předchozího se nepřenáší.
Rozmanitost večera tvoří `EveningMotif` z uzavřené slovní zásoby
abstraktních témat; osobní citace a příběhy se do ní nepřepisují.
`Personal` vyžaduje samostatně ověřeného guestId, platnou verzi povolení
a zapnutou dlouhodobou paměť. Žádné rozpoznání podle hlasu či jména.

S existujícím **server admin credential**, nikoli device či log tokenem:

| Endpoint | Význam |
| --- | --- |
| `GET /v1/admin/memory` | provider, konfigurace, degraded, backlog indexace/mazání a failed počet; bez osobního textu |
| `POST /v1/admin/memory/verify` | skutečné čtení obou stores; missing config vrátí 503 |
| `PUT /v1/admin/memory/guest` | vazba před první řečí; verified guest UUID, identitySource, longTermAllowed, CloudExportAllowed a ExpectedVersion; revokace může okamžitě ukončit aktivní kontext |
| `PUT /v1/admin/memory/records/{MemoryId}` | revizovaný explicitní záznam; ExpectedRevision (0 pro nový), volitelný ExpectedEpoch, scope/kind, zdroj a text |
| `DELETE /v1/admin/memory/records/{MemoryId}` | JSON DeviceId + ExpectedRevision; okamžitý tombstone, cache invalidace a ukončení běžícího kontextu |

JSON názvy jsou camelCase; scope/kind ve vstupu používají řetězce
`Conversation|EveningMotif|Personal` a
`UserReportedFact|ConfirmedSelection|TopicUsed|FictionalJoke|LegacyUnverified`.
Přesná těla a bezpečnost jsou v
[`backend-openapi-v1.json`](../contracts/backend-openapi-v1.json).
`identitySource` popisuje externě doložené přiřazení; samotné nastavení
`identityVerified=true` správcem není automatická biometrická kontrola.
Správce odpovídá za skutečné ověření a samostatné povolení cloudového
exportu. Nová konverzace vracejícího hosta se přiřazuje znovu explicitně.

Výslovné hlasové „zapamatuj si…“ jde po delegaci do téhož store jako
`UserReportedFact`, pouze s ověřenou identitou a povolením. Opakovaný
ekvivalentní zápis je idempotentní. Příliš dlouhý text se uloží v označené
zkrácené podobě. Neúspěšný zápis se neohlásí jako uložený.
„Zapomeň … vzpomínky / paměť / všechno“ smaže jen povolenou osobní
paměť aktuálního hosta, nikoli ostatní lidi. U této změny končí Live
interakce bez návrhu a další session se sestaví z platných dat. Admin
oprava/smazání aktivní kontext zruší také; nepředstírá vymazání toho,
co už zaznělo. `ConfirmedSelection` je samostatný autoritativní záznam
s doloženým zdrojem; návrh/ACK/closed se na něj automaticky nepřevádí.
Stávající backend nedostává potvrzení fyzického výsledku od pohonů,
takže tato změna žádné takové dokončení nevymýšlí.

## Retrieval, outbox a retence

Provider provádí skutečné REST Files upload (`purpose=assistants`), attach
s attributes, polling stavu, direct search a obě DELETE operace.
Pinned .NET SDK nemá potřebné direct search / file attributes; tento
izolovaný provider používá ověřený REST, Responses zůstává SDK.
Žádné lokální embeddings, další databázový server či store na fragment.
Jedna vzpomínka/revize je krátký UTF-8 `.txt` s neprůhledným `km-UUID.txt`.
Osoby se nemíchají do jednoho souboru. ≤16 primitivních atributů,
žádné null, klíče ≤64 a řetězce ≤512. Nový text vytvoří novou revizi/soubor.

`POST /v1/vector_stores/{id}/search` má v těle **`filters`**. Server
vynucuje namespace/zařízení/scope/aktivitu/expiraci, osobní guestId a
případně conversationId; motivy večer a personalityId. Query rewriting
je vypnuté. Výsledek se přes FileId mapuje zpět do SQLite, znovu ověří
revize, identita a povolení; chunk text z OpenAI se hostovi nepředává.
Cizí/neznámý FileId, smazaný či expirovaný záznam se zahodí.
Cache zahrnuje celý auth scope, epoch/revize a permission version,
má TTL 10 s a nejvýše 64 položek. Důležité aktuální opravy jsou mimo
sémantické dohledání. Každá session eviduje pouze ID/revize skutečně
vloženého kontextu a stejnou paměť znovu nepřidává; novější opravená revize
je znovu způsobilá. Sledování se nesdílí mezi hosty či sessions.
Start přidá jen relevantní lokální text jako
historický developer context, ne jako novou repliku přítomného hosta.
Od D148 startup připojí i platné lokální anonymní motivy daného večera,
aby čerstvý motiv nezmizel z kontextu během asynchronní indexace. Stejné
ID/revize se deduplikují; původní filtr scope/večera a tokenový limit platí.

Zápis + outbox jsou jedna SQLite transakce. Worker má trvalé lease,
bounded concurrency, deadline, CancellationToken, jitter/backoff,
Retry-After a konečný počet pokusů. Před uploadem i po indexaci ověří
aktuálnost. In-progress/failed/cancelled index není použitelný hit.
Po nejasném uploadu dohledává vlastní náhodný filename a nový POST
neopakuje naslepo. Neověřený výsledek zůstává failed/dohledatelný,
nikoli „úspěšně smazáno“. Technické jobId/chybové kódy jsou v logu
a v `memory_outbox`; text/klíče se nelogují. Opravu failed indexu lze
provést autorizovanou novou revizí; trvalou chybu mazání je nutné
servisně vyřešit, ne ignorovat počet failed v diagnostice.

Oprava či smazání nejdřív atomicky deaktivují místní text, zvýší epoch
a vytvoří mazací job. Stará summarizační/zápisová úloha nemůže obnovit
tombstonovaný ID ani překonat ExpectedEpoch. RetentionWorker maže
expirovaný obsah, časované přepisy a cache; outbox odstraňuje asociaci
ze store **i vlastní podkladový Files soubor**. Kontroluje vlastnictví
a lokální reference. Neznámé/cizí/sdílené soubory nemaže. Žádný store
se nemaže automaticky. Stav rozlišuje místní invalidaci, odeslané
mazání a potvrzené vzdálené dokončení. Dokončené joby/mapping a denní
metriky se čistí po 7 dnech; minimální tombstone ID/revize přežijí,
aby pozdní úlohy nemohly obnovit smazaný obsah. Instance lock chrání
jediného vlastníka DB a trvalé lease chrání souběžné workery.

Výpadek provideru vrátí `memory_unavailable` a povolený lokální kontext;
nula ověřených hitů vrátí `memory_not_found`. Hlas tím nezávisí na
povinném hledání před každou reakcí. Osobní cloudový obsah se neexportuje
bez odděleného povolení. `store:false` u Live/Responses **nevypíná Files
ani Vector Store persistence**. Tato konfigurace neslibuje ZDR ani
nulovou retenci. Zpracování smazání má vlastní backlog a OpenAI
podmínky; odvolání již přednesené věty není možné.

## Audio, playback a fyzická prezentace

Transport je serverový WSS `wss://api.openai.com/v1/live/sessions`,
model `gpt-live-1`, client delegation, store=false, stávající hlas,
PCM16 signed LE mono/24 kHz. `session.start` čeká na korelované started.
Append obsah je čitelný text s konzervativním UTF-8 tokenovým horním
odhadem ≤500 a povinným delegation_id známým nebo null. Thinking je
kontext, commentary je faktický výsledek pro vlastní řeč a instructions
jsou pouze aplikační pokyny. Žádná tajemství. Audio append nemá ACK.
Žádné `response.create` ani vymyšlené output-audio-done.

`pause` je heuristika segmentu; callback jen zapíše událost a server
dál čte Live. Nečeká na přehrávač v readeru. Terminální heuristika
vyžaduje aktuální výsledek, odpovídající skutečný výstupní přepis,
přijetí výsledku, PCM a konfigurované ticho. D149 nevyžaduje poslední
tečku ani celý nominativní název: jedinečný titul z katalogu rozpozná i
v českém skloňování a normalizuje mezery/diakritiku. Nedokončené zakončení
(pomlčka, čárka, dvojtečka či výpustka), jiná nebo dvě hůlky nepustí.
Při dokončeném, ale nejasném oznámení po ≥2,5 s výstupního klidu a
ověřeném vstupním klidu jednou vyžádá krátké zopakování platného názvu. Oprava sama nic
nepublikuje; nový skutečný přednes a jeho drain jsou znovu povinné. Potom
odešle `playback_checkpoint {playbackId,pcmBytes,decisionRevision}`.
Nativní přehrávač potvrdí až skutečnou lokální drain hranici přes
`POST .../gpt-live/{streamId}/playback {playbackId,pcmBytes,drained:true}`.
Duplicitní stejné potvrzení je neškodné. Čekání neblokuje reader;
pokračující řeč/oprava nový checkpoint zneplatní. Teprve platná
kombinace dovolí stop input → session.close → session.closed a následné
publikování validovaného návrhu. Cloudový ACK, ticho, poslední packet
či samotný closed nepředstavují hotový přednes nebo fyzickou akci.
Heuristika není měřený konec celé budoucí řeči; A/B test musí zahrnout
dramatické pauzy a ověřit vhodné nastavení pro konkrétní hlas.

Mikrofonní PCM se posuzuje zvlášť od přepisových paketů: RMS aktivita
blokuje spuštění práce, dodání hotového výsledku do řeči i uzavření
checkpointu do `InputQuietMs` klidu. Během dlouhého příběhu neběží
zotavení „chybí přepis“ jen proto, že vypršel delegation timeout;
pokračování hosta pozastaví také terminální timeout. Nový input fragment
pozastaví publikování okamžitě; podstatné změny se vyhodnotí až nad
stabilním přepisem a klidem mikrofonu. Čisté „dobře, ukaž ji“, opakování či
poděkování zachovají ověřený návrh a novou revizi. Nové preference,
alternativa a zrušení jej zneplatní. Poděkování obsahující změnu není
potvrzení. Samotné přijetí není další diagnostická odpověď.

`live_terminal_state` zapisuje pouze korelaci a technické `code`, například
`result_input_active`, `input_pending`, `awaiting_announcement`,
`announcement_repair`, `awaiting_playback` a `closing`. Přepis ani audio
do technického logu nepatří. Při dalším selhání porovnat tento stav,
`live_decision_completed` a HTTP `/playback`/finální completed podle traceId.

Od D151 `live_failure_detail` přidává pevnou technickou příčinu bez
zprávy výjimky nebo provider obsahu; [Codex diagnostika](../backend/codex/README.md)
popisuje kategorie. Časy `start_ms`/`end_ms`/`offset_ms` přijímají konečné
JSON číslo v rozsahu 0–200000 ms, také desetinný zápis; obrácené časy
jsou neplatné. Přesnost se zachová v časované projekci a stávajícím SQLite
archivu bez migrace. Strict decision schema používá podporované string
`pattern` a array `maxItems`: citace 1–100, fakt 1–160, chybějící informace
0–200 znaků a nejvýše dva důvody. Dosavadní kontrola přesných podkladů
platí i po schema omezení. Reference: [Live API](https://developers.openai.com/api/reference/resources/live)
a [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs).
Tyto opravy neprokazují konkrétní příčinu historických chyb, které log
uložil pouze jako `ai_unavailable`; neprovádějí placené opakování requestu.

`/availability` přijímá device token a přesný bootId/generation, mask
a monotónní deviceRevision. Nový snapshot zneplatní výsledek;
změněná fyzická revize vyžaduje nový korelovaný start, žádný návrh
se nepřepíše na starou revizi. Existující ESP32 `PendingProposal`,
OperationJournal a místní brána dál ověřují 1–6, snapshot, kalibraci,
expiry a bezpečnost. Návrh není příkaz modelového textu pro servo.
Přijetí zařízením, prezentace a skutečný návrat hůlky zůstávají
lokálními stavy. Normální návrat obnoví dostupnost; Stop/chyba vyžadují
zotavení. Reconnect nikdy nereplayuje placený stream ani přijatou operaci.

ESP32 bez změřeného AEC zachovává bezpečný poloduplex: capture/24 kHz
časová osa při speaker playback obsahuje nulové bloky, nikoli hlas/echo.
`EspIdfVoiceAudio.Play` má původní digitální gate a `Drain` dokončí DMA
ocas před návratem poslechu. `pause` ani ztracený HTTP ACK samy neuzamknou
mikrofon. Prompt nezajistí hlasové přerušení, které gate nezachytil.
Telefon má původní současný capture/playback a Apple voice processing.
To není nově změřený důkaz AEC. Firmware capture má 20 ms bloky, omezenou
frontu a antialias resampling 32↔24 kHz; output má dosavadní 25% úroveň,
sticky Stop a kontrolu I2S zápisu. Host testy ověřují řazení, DMA/drain,
Stop a ACK; skutečný clipping, echo, hladiny, buffer/latence a speaker
vyžadují měření na desce/telefonu.

## Akceptace a poslechové A/B scénáře

Offline:

```sh
dotnet restore kufr/backend-tests/BackendTests.csproj --locked-mode --runtime linux-x64
dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release --no-restore
dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release --no-restore -- --autonomous-memory
KUFR_BACKEND_DLL=bin/Release/net10.0/backend.dll node kufr/backend/tests/smoke.mjs
npm ci --prefix kufr/contracts
npm test --prefix kufr/contracts
npm ci --prefix kufr/mobilni-aplikace
npm test --prefix kufr/mobilni-aplikace
npm run build --prefix kufr/mobilni-aplikace
```

Veškerá nová API ověření mají fixture HTTP/WebSocket transporty a temp DB,
ani přítomný klíč nespustí integraci. Stávající placený `--gpt-live-smoke`
je výhradně explicitní opt-in; není důkaz poslechu nové actor větve.
CI nevolá provisioning `--execute`. Přesné skutečně spuštěné příkazy
a výsledky jsou v [D143 stavu implementace](stav-implementace.md).

Hostitelské C++ příkazy spuštěné v `kufr/firmware` (výstupy pouze `/tmp`):

```sh
common=(src/features/voice_turn/RealtimeEventCodec.cpp
  src/features/conversation/TurnOperationCodec.cpp
  src/features/conversation/TurnPollGuard.cpp
  src/features/conversation/ConversationSession.cpp
  src/features/conversation/ConversationStartCodec.cpp)
g++ -std=c++14 -Wall -Wextra -Werror "${common[@]}" \
  src/features/local_control/LocalControlCodec.cpp tests/realtime_voice_test.cpp \
  -o /tmp/kufr-autonomous-realtime-voice-test
/tmp/kufr-autonomous-realtime-voice-test
g++ -std=c++14 -Wall -Wextra -Werror -pthread -I tests/realtime_stubs \
  "${common[@]}" src/features/voice_turn/EspIdfRealtimeTransport.cpp \
  src/features/backend_access/NetworkSettings.cpp tests/espidf_realtime_transport_test.cpp \
  -o /tmp/kufr-autonomous-realtime-transport-test
/tmp/kufr-autonomous-realtime-transport-test
g++ -std=c++14 -Wall -Wextra -Werror -pthread -I tests/audio_stubs \
  src/features/voice_turn/EspIdfVoiceAudio.cpp tests/espidf_voice_audio_test.cpp \
  -o /tmp/kufr-autonomous-voice-audio-test
/tmp/kufr-autonomous-voice-audio-test
g++ -std=c++14 -Wall -Wextra -Werror -pthread \
  src/features/safety/SafetyController.cpp src/features/wand_selection/SelectionSequence.cpp \
  src/features/device_control/DeviceControl.cpp src/features/device_control/DeviceControlRuntime.cpp \
  src/features/conversation/ConversationRuntimeState.cpp src/features/conversation/PendingProposal.cpp \
  src/features/wand_presentation/WandPresentation.cpp tests/autonomous_presentation_lifecycle_test.cpp \
  -o /tmp/kufr-autonomous-presentation-test
/tmp/kufr-autonomous-presentation-test
```

V téže složce prošel `pio run` se stávajícími připnutými verzemi. Tyto
kontroly nic nenahrávají do desky. Nativní Swift fixture spouští na macOS
`bash scripts/test-native-debug.sh` v `kufr/mobilni-aplikace`; tento běh
na Linuxu nebyl proveden.

Po oprávnění spustit každý scénář na stejné personality v legacy a
actor režimu, alespoň tři průchody; měnit pouze režim, ne zároveň model,
hlas a mikrofon. Konkrétní formulace vtipu není testovací oracle.

| Scénář | Co sledovat |
| --- | --- |
| Hovorný / stručný host | prostor pro řeč, konkrétní návaznost, počet diagnostických odpovědí |
| Dlouhé zaváhání / příběh | nepřerušit nádech či pauzu, žádný pád po 1000 znacích |
| Oprava během výběru | starý výsledek se nepředá/publikuje; nový stojí na opravě |
| „Nevím“ / „prostě vyber“ | bez nekonečného doptávání a povinného souhlasu |
| Odmítnutí roastu | okamžitě ubrat, bez výsměchu za hranici |
| Host vrací otázky kufru | krátká vlastní reakce, návrat k tématu bez povinného Sol volání |
| Odmítnutý návrh | nejvýše jedna ověřená alternativa; žádný replay již přijaté operace |
| Změna dostupnosti / nula | aktuální snapshot, bez vymyšleného kusu nebo servo příkazu |
| Hluk / echo | oddělit telefon full duplex a ESP gate; měřit hladiny/clipping/ozvěnu |
| Vracející identifikovaný host | potvrzené přiřazení, oddělený souhlas, 3–5 relevantních hitů |
| Zapamatování / zapomenutí | souhlas, lokální autorita, cloud backlog, další čistá session |
| Dramatická pauza / přerušení závěru | pokračování řeči a skutečný lokální drain, bez falešného úspěchu |

Zapisovat zvlášť českou srozumitelnost, návaznost na skutečnou odpověď,
konkrétnost/opakování humoru, skákání do řeči a čas **od skutečného konce
řeči hosta k prvnímu slyšitelnému zvuku**. Dále počet rozhodovacích a
paměťových volání, vrácené tokeny/voice seconds, rezervace vs spotřebu,
objem indexu/backlog, správnost návrhu a nezávisle playback/physical
stav včetně návratu hůlky. Zelený unit test a přepis neprokazují přirozený
hlas. Poslech, skutečný OpenAI integration test, IPA/iPhone a deska
nebyly tímto lokálním slice ověřené.

## Ověřené oficiální kontrakty (5. 10. 2026)

- [Live](https://developers.openai.com/api/docs/guides/live),
  [prompting](https://developers.openai.com/api/docs/guides/live-prompting),
  [delegation](https://developers.openai.com/api/docs/guides/live-delegation),
  [WebSockets](https://developers.openai.com/api/docs/guides/voice-websockets?api=live),
  [migration](https://developers.openai.com/api/docs/guides/live-migration).
- [Managing sessions](https://developers.openai.com/api/docs/guides/live-conversations)
  a generované oficiální Python `types/live/session_config_param.py` a
  `initial_item_param.py`: nejvýše 128 input messages / 8192 input tokens,
  instructions 16384 tokens; typ message, role developer/user/assistant,
  text parts podle role. Aplikace posílá historii jako developer/input_text.
- [Retrieval](https://developers.openai.com/api/docs/guides/retrieval),
  [REST search s filters](https://developers.openai.com/api/reference/resources/vector_stores/methods/search),
  [attach](https://developers.openai.com/api/reference/resources/vector_stores/subresources/files/methods/create),
  [delete association](https://developers.openai.com/api/reference/resources/vector_stores/subresources/files/methods/delete),
  [Files upload](https://developers.openai.com/api/reference/resources/files/methods/create),
  [Files delete](https://developers.openai.com/api/reference/resources/files/methods/delete).
- [Your data](https://developers.openai.com/api/docs/guides/your-data),
  [pricing](https://developers.openai.com/api/docs/pricing): stávající
  Live $0.05/min po sekundách a Sol konzervativní cache-write $2.50 / output
  $10 za 1M tokenů byly znovu ověřené. Storage je samostatně účtované;
  cena file_search tool call není automaticky cenou přímého REST search.
  Aplikace paměť vykazuje v objemových metrikách, nevymýšlí její USD cenu.
