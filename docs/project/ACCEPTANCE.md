# Pravidla projektové akceptace

Zdrojem aktuální práce jsou [work items](work-items/), důkazů [receipts](evidence/)
a posuzovaných sestav [targets](targets/). [STATUS.md](STATUS.md) je generovaná
projekce. Rozhodující implementace pravidel je
[project_status.py](../../scripts/project_status.py); jeho
[regresní testy](../../scripts/test_project_status.py) používají pouze izolované
syntetické fixtures, nikdy nevytvářejí produkční důkaz.

## Použití CLI

Z kořene repozitáře, Python 3.10 nebo novější, bez dalších balíčků:

```text
python scripts/project_status.py validate
python scripts/project_status.py validate --base HEAD^
python scripts/project_status.py render
python scripts/project_status.py render --check
python scripts/project_status.py next --initiative DV08 --scope scripts
python scripts/project_status.py blockers
python scripts/project_status.py verify-evidence
python scripts/project_status.py migration-check
python scripts/project_status.py validate --readiness --target TARGET_ID
python scripts/test_project_status.py
```

`--root` přijímá kořen jiného checkoutu před i za názvem příkazu. Výchozí kořen
se odvozuje z umístění skriptu. CLI samo práci nespouští, nevytváří receipts,
neprovádí commit, push, nasazení ani fyzické testy. Výstup je UTF-8 i na Windows.

| Exit code | Význam |
|---|---|
| 0 | Požadovaná kontrola prošla. Samotné `validate` neprohlašuje readiness. |
| 1 | Gate není připravena, není další povolená práce nebo snapshot nesouhlasí. |
| 2 | Neplatná data, reference, baseline či chyba čtení/zápisu. |
| 3 | Požadovaný externí/lokální podklad není dostupný; nejde o výsledek fail testu. |

`validate` kontroluje strukturu a reference včetně neúspěšných, ale pravdivých
gates. `validate --readiness` posoudí uvedený target, jinak `project.currentTarget`,
jinak všechny targety. Prázdná gate je `not_configured` a readiness neprojde.
Při kombinaci selhání a nedostupnosti má pro readiness přednost exit code 1.

## Datový kontrakt

Strojové dokumenty mají `schemaVersion: 1`. Soubory v `work-items`, `evidence`,
`targets` a `releases` mají jméno shodné se svým `id`, `evidenceId`, `targetId`
nebo `releaseId`. ID obsahuje písmena ASCII, číslice, tečku, pomlčku nebo
podtržítko a začíná písmenem nebo číslicí. JSON nesmí mít duplicitní klíče,
nekonečné hodnoty, cyklické závislosti ani placeholdery `TODO`, `TBD`,
`CHANGEME`, `PLACEHOLDER`, `REPLACE_ME`, značky `<...>` či example domény.

Work item vyžaduje `initiative`, `title`, `state`, `priority` (P0–P3), celočíselné
nezáporné `sequence`, neprázdné `scope`, `dependencies`, neprázdné `acceptance`
a `blockers`. `planned`, `active` a `blocked` mají konkrétní `nextAction`.
`blocked` vyžaduje alespoň jeden blocker. `deferred` a `cancelled` vyžadují
`decisionRef`. `owner`, `currentNote` a `relatedItems` jsou volitelné.

Kritérium má `id`, kladnou celočíselnou `revision`, `expected`, neprázdné
`requiredEvidenceKinds`, `appliesTo` a `evidenceRefs`. Pro více druhů je povinné
`rule: allOf | anyOf`; pro jediný druh je výchozí `allOf`. `appliesTo` je objekt
přesných relevantních identit, nikoli seznam cest. `evidenceRefs` volí ID receipts.
Neshoda revize způsobí `stale`, nikoli splnění nového kritéria.

Dependency má `itemId` a `requiredMilestone: implemented | accepted`.
`implemented` vyžaduje samostatný neprázdný `implementedSubject` s přesnou
identitou skutečně implementovaných bytes (artifactDigest, firmwareDigest nebo
commit s dirty); samotná revision či assemblyId nestačí a lifecycle stav
implementaci nedokazuje.
Známé odpovídající identity aktuálního targetu se musí shodovat.
`accepted` vyžaduje historické `closed/accepted` a současně `passed` ověření
aktuálních kritérií pro aktuální target, pokud je nastaven. Odložená tvrdá závislost
sama o sobě požadovaný milestone nesplňuje.

Blocker má `id`, `problem`, `severity: critical | high | medium | low`, neprázdné
`blocks` s ID kritérií nebo `continuation` a `unblockCondition`; `role` je volitelné.
Každý blocker na povinném kritériu či jeho pokračování ovlivňuje gate.
Výpis zkrácený limitem STATUS na tom nic nemění.

## Receipts a identity

Receipt vyžaduje `observedAtUtc` s UTC zónou, `kind`, `result`, neprázdné `claims`,
neprázdný `subject`, `method`, `environment`, pole `limitations` a alespoň
`artifactRefs` nebo stručnou samostatně vyhodnotitelnou `observation`.
Každé tvrzení má `workItemId`, `criterionId`, `criterionRevision` a
`result: pass | fail | inconclusive`. Souhrnný result je fail při jakémkoli fail,
jinak inconclusive při jakémkoli inconclusive, jinak pass.

Povolené druhy: `source_review`, `unit`, `integration`, `browser`, `native_ios`,
`esp_boot`, `esp_runtime`, `physical_inspection`, `electrical_measurement`,
`long_running_soak`, `production_deploy`. Místo běhu není druhem důkazu.
Unit či browser test nemůže nahradit požadovaný fyzický nebo nativní test.

Softwarový subject obsahuje `commit` a boolean `dirty`, nebo přesný
`artifactDigest` či klíč končící `ArtifactDigest` (například `iosArtifactDigest`).
`configDigest` samotný bytes software neidentifikuje.
Commit je hexadecimální Git revize. Identita s `dirty: true`
vyžaduje takový artifact digest; pouhé HEAD nestačí. Digestové identity obsahují
64 malých hexadecimálních znaků SHA-256. Relevantní `appliesTo` musí zahrnovat
přesné bytes přes artifact digest nebo commit spolu s dirty. Samotný název sestavy
či aplikace software neidentifikuje. Identický artifactDigest umožňuje ověřit
stejné bytes i po čistě dokumentační změně; nepřenosný výsledek se nepřenáší odhadem.

Fyzické druhy `esp_boot`, `esp_runtime`, `physical_inspection`,
`electrical_measurement` a `long_running_soak` vyžadují `assemblyId`,
`firmwareDigest`, `wiringRevision`, `mechanicsRevision`, `configDigest` a
stabilní alias `operator`. Pro platné fyzické splnění musí být všechny identity
mimo operator známy také v posuzovaném targetu, nebo v appliesTo při posouzení bez
targetu. Samotné assemblyId nesmí přijmout výsledek po změně zapojení.
Elektrické měření navíc vyžaduje `instrument` a neprázdné `measurements`.
Záznam přesných podmínek a naměřených hodnot je odpovědností autora.

`artifactRefs` jsou cesty k souborům od kořene repozitáře s `/`, nebo HTTP(S) URL
bez credentialů. Absolutní cesty, `..`, zakódovaný traversal, Windows disky,
nepodporovaná URL schémata a symlinky mimo checkout jsou odmítnuty.
Výchozí kontrola nepoužívá síť. Chybějící místní soubor a vzdálený podklad bez
samostatné observation znamenají unavailable. `verify-evidence --online` explicitně
ověří vzdálené odkazy metodou HEAD; síťová nedostupnost není fail testu.
Samostatná observation umožňuje offline vyhodnocení, dostupnost dalších doplňkových
odkazů pak není podmínkou. Kontrola URL dostupnosti sama neprokazuje obsah testu.

Oprava receipt vytvoří nové ID a `supersedes` jako pole původních ID. Řetězce
nesmějí být cyklické ani časově obrácené. Změna subject nebo množiny tvrzení
vyžaduje `supersedesReason`. Pass nahrazující fail stejného tvrzení vyžaduje
`resolution` popisující opravu či opakovaný test. Resolving pass musí být vybraný,
dostupný a stejného požadovaného druhu jako nahrazované selhání.
V řetězci supersedes přenáší resolution pouze skutečný pass stejného druhu,
jehož podklady jsou pro aktuální posouzení dostupné. Text resolution u
inconclusive, nesprávného druhu či nedostupného mezilehlého pass selhání neodstraní.
Inconclusive nevymaže fail.
Nahrazení pro jiný subject neukryje selhání původního subjectu.
Relevantní nevyřešené fail se hledají ve všech receipts, i mimo vybrané evidenceRefs.
Novější timestamp sám nerozhoduje. Nový platný pass musí být výslovně vybrán.

## Uzavření, verification a gates

Lifecycle `planned | active | blocked | closed | deferred | cancelled` je ruční.
Verification je odvozené `unverified | partial | passed | failed | stale | conflict`.
Rozpor pass a nevyřešeného fail je conflict. Částečně splněné allOf je partial.
Existující vybraný důkaz pro jiné identity nebo revizi je stale.

`closed` má `closure.outcome: accepted | superseded` a `reason`.
Superseded uvádí existující jiné `replacementItemId`. Accepted uvádí přesný
`subject`, `evidenceRefs` a `acceptedCriteria`, snapshot úplných tehdy přijatých
kritérií. Souhrn refs musí souhlasit s refs ve snapshotu. Snapshot se vyhodnocuje
pro historický subject, nezávisle na pozdější revizi či aktuálním targetu.
Současná nedostupnost externího podkladu historický záznam nepřepisuje.

`validate --base REV` je povinná kontrola změn používaná v CI: čte lokální Git
bez sítě, odmítá změnu/smazání existujících receipts a změnu významu použitého
kritéria bez zvýšení revision. Při novém accepted uzavření musí snapshot přesně
odpovídat všem současným kritériím a nesmí zbýt jejich blocker. Pro nový přechod
se všechna kritéria znovu vyhodnotí vůči closure.subject s právě dostupnými
podklady nebo samostatnou observation; historická výjimka z dostupnosti se při
novém uzavření nepoužije. Existující
historické closure se nesmějí přepisovat. Samotná strukturální kontrola bez
baseline nemůže doložit neměnnost vůči historii; reviewer musí ověřit skutečné
provedení testu. Receipt není kryptografická atestace.

Target vyžaduje `purpose`, `components` a `requiredCriteria` jako pole dvojic
`workItemId`/`criterionId`. Components mohou obsahovat pojmenované vnořené
komponenty; listové identity se porovnávají podle názvů klíčů. Tentýž název klíče
nesmí obsahovat různé hodnoty. Více komponent používej s jednoznačně pojmenovanými
digesty: například `components.firmware.firmwareDigest`,
`components.ios.iosArtifactDigest` a `components.backend.backendArtifactDigest`.
Nativní kritérium pak uvádí `appliesTo.iosArtifactDigest` a receipt stejné pole
v subject; backendové kritérium používá backendArtifactDigest. Změna backendu
tak nezneplatní identické iOS bytes. Fyzické kritérium stále potřebuje všechny
povinné fyzické identity. Odlišné obecné commit/artifactDigest pod několika
komponentami jsou nejednoznačné a validátor je odmítne; není zde automatický
výběr komponenty podle druhu testu. Explicitní target přepisuje relevantní identity
appliesTo; chybějící požadovaná identita nemůže splnit kritérium.

Odvozené gates jsou `ready`, `ready_with_exceptions`, `failed`, `stale`,
`unknown` nebo `not_configured`. Nesplněné tvrdé závislosti včetně tranzitivních
blokují gate; milestones se posuzují pro target této gate, nikoli jiný fokus.
Výjimka uvádí `targetId`, `workItemId`,
`criterionId`, `reason`, `decisionRef` a případně `expiresAtUtc`. Nezmění
původní výsledek kritéria na passed. Expirace se vyhodnocuje pouze vůči explicitnímu
`--as-of` UTC času; bez něj se časově omezená výjimka nepoužije.
Výjimku smí vytvořit pouze explicitně autorizované rozhodnutí majitele.

Release uvádí `releaseId`, `publishedAtUtc`, `sourceRevision`, `subject`,
neprázdné `artifactRefs` a případně `targetId`. Publikace neprokazuje deployment,
instalaci ani fyzickou přejímku. Release nemění gate.

## Snapshot a migrace

Volitelné `project.json` obsahuje `focusInitiative`, `currentTarget` a
`statusLimits` pro active/planned/blockers (0–12). Next filtruje uzavřené,
odložené a zrušené položky; řadí priority, sequence, ID a respektuje initiative
i scope. Zablokovaný fokus nevymění za jinou iniciativu. STATUS ukáže všechny
gates, celkové počty i závažnosti blockerů a počty skrytých položek.

Render je offline a deterministický, seřazený podle relativních cest, s UTF-8/LF.
Input digest rámuje původní bytes autoritativních JSON, ADR, ACCEPTANCE,
migration a implementace CLI. Nezahrnuje STATUS ani archivy. Neobsahuje čas
renderu ani vlastní commit. Atomické přejmenování používá dočasný soubor ve stejném
adresáři. `render --check` nic nezapisuje. Limit je 140 řádků; cílový tokenový
rozpočet se musí ověřit při migraci, počty tokenů bez tokenizeru jsou odhady.

`migration.json` má `sources` (path/sha256/archivePath), `mappings`
(source/section/lineStart/lineEnd/classification/destinations/reason),
`openBlockers` (source/section/workItemId/blockerId), `links` (source/href)
a konkrétní `rollback`. Klasifikace je current/historical/unresolved.
Mapování musí pokrýt každý neprázdný řádek archivovaného zdroje; current/unresolved
vyžadují existující cílové umístění. Fragment JSON může označit ID kritéria či
blockeru. Místní Markdown odkazy se kontrolují včetně Unicode heading slugů,
duplicitních nadpisů a explicitních HTML anchorů. Hash se ověřuje proti původním
bytes, nikoli normalizovanému textu. `migration-check` je pouze read-only.
Ruční inventura stále musí doložit úplnost seznamu tvrzení a otevřených blockerů;
skript neumí sémanticky přečíst nezdokumentovaný původní problém.
