# CI/CD: ověření PR a nasazení z explicitního buildu

[CI](../.github/workflows/ci.yml) používá path filtr: změny samotné
dokumentace (`docs/`, `.md`, `.mdx`, `.rst`) build CI nespouštějí.
U PR s kódem [výběr projektů](../scripts/ci/select_projects.py) porovná
celý PR s jeho base a spustí jen dotčené kontroly. **CI required** vyžaduje
úspěch vybraných projektů a vynechání ostatních; selhání výběru nebo
neočekávané vynechání dotčeného projektu odmítne. Nový commit do stejného
PR zruší zastaralé CI; rozběhnuté nasazení se neruší. Merge do `main`
automaticky spouští pouze site deploy při změnách jeho zdrojů. Backend,
firmware a web pro podpis se obnovují explicitním main buildem podle postupu níže.

### Aktualizace PR před buildem

[Synchronizace větví](../.github/workflows/pr-sync.yml) při otevření,
aktualizaci nebo připravení PR a při push do `main` zkontroluje otevřená
PR vůči jejich aktuální cílové větvi. Pokud větev zaostává, GitHub API
nejprve přimíchá cíl do PR větve. Očekávaná head SHA chrání souběžný
commit autora; konflikt ukončí synchronizaci a musí se vyřešit ručně.
Draft a fork PR se automaticky nemění. Nejde o sloučení PR do cíle.

Teprve po dokončení aktualizace automatika explicitně spustí CI na nové
head SHA. Kontrola aktuálního head/base před výběrem projektů odmítne
zastaralý běh ještě před buildy. PR dispatch i běžné PR CI sdílejí
concurrency skupinu, takže nový běh ruší starý. Pozdější změna cíle
může stále vyžadovat nové CI; kontrola ji nepředstírá jako ověřenou.

Writer používá oddělený self-hosted `kufr-control` runner a kód pouze z výchozí
větve; PR kód nedostává jeho write token. Bez dalších secrets použije
`GITHUB_TOKEN` a explicitní dispatch, protože tokenem vyvolané PR
události na GitHubu vyžadují schválení. Takový čekající PR běh nemusíme
schvalovat: rozhodující je automaticky spuštěné CI na stejné head SHA.
Samotná dokumentace může vyvolat krátkou kontrolu synchronizace,
ale výběr projektů pro ni nespustí buildy. Běžné buildy zůstávají na VPS.

Při selhání po aktualizaci větve zopakuj synchronizační běh; opakovaný
pokus umí spustit CI i pro již aktuální PR. Ruční **Synchronize PR branches**
zkontroluje všechna otevřená PR do `main` a obnoví jejich CI.
Interní dispatch vstupy `pr_number`, `expected_head_sha`, `expected_base_sha`
se zadávají společně; běžný obnovovací build bez nich patří na `main`.
PR i synchronizační dispatch kontrolují celé balení s číslem PR a base/head SHA,
ale neukládají ho do GitHub artifact storage. Pro nasazení spusť explicitní
obnovovací build příslušného projektu na aktuálním `main` bez interních PR vstupů.

| Změněné soubory | Spuštěné projekty |
| --- | --- |
| `kufr/backend/`, `kufr/backend-tests/`, .NET konfigurace v `kufr/` | Backend |
| `kufr/firmware/` | Firmware a hostitelské testy |
| `kufr/mobilni-aplikace/` | Mobilní web |
| `kufr/ai-eval/` | Offline AI eval |
| `kufr/contracts/` | Backend, firmware, mobilní web, site |
| `kufr/simulator/` | Firmware a mobilní web |
| Build workflow konkrétního projektu | Daný projekt a kontroly repozitáře |
| `.github/`, `scripts/ci/` | Kontroly repozitáře; změna balení artefaktů také backend, firmware, web a site |
| Samotná dokumentace | Žádný build; pouze kontrola synchronizace PR |

Path filtr se vztahuje na celý PR. Když PR obsahuje i změny kódu,
následný dokumentační commit stále ověřuje jeho dotčené projekty.
Ruční `workflow_dispatch` nabízí `project`: `all`, `repository`,
`backend`, `firmware`, `web`, `ai-eval` nebo `site`. Výslovný ruční build lze
použít také po dokumentační změně, potřebujeme-li nový artefakt přesného stromu.

## Runnery

Majitel 1. 10. 2026 požádal přesun všech Linux jobů na svůj server.
Build/test a provozní joby používají `[self-hosted, Linux, X64, kufr-backend]`, tedy stávající
pool `dildovander-server` a `dildovander-server-2`. Synchronizace PR používá
`[self-hosted, Linux, X64, kufr-control]` na `dildovander-control`.
Pouze ruční podepsaný Ad Hoc IPA build
a jeho nativní testy běží na GitHub-hosted `macos-15` s Xcode 26.3.
Linux runner obsluhuje i PR kontroly, ověření artefaktů, veřejné HTTPS
kontroly, diagnostiku a ruční AI testy. PR ani obnovovací CI nespouští
Xcode nebo iOS Simulator.
Jeden registrovaný serverový runner provádí vždy jen jeden job; ostatní
čekají ve frontě. Závislosti mezi joby zůstávají zachované.

Server potřebuje Git, curl, tar, OpenSSL, C++ toolchain a Python s venv.
Správce jednorázově zajistí systémové závislosti:

```bash
sudo apt-get update
sudo apt-get install -y --no-install-recommends git curl ca-certificates tar openssl build-essential python3 python3-venv espeak-ng xvfb xauth libgl1 libegl1 libglib2.0-0
```

`espeak-ng` potřebuje jen explicitní živá Realtime zkouška. Workflow
balíčky neinstaluje přes sudo. Node 24.21.0 instalují připnuté setup-node
actions podle `.nvmrc`; .NET SDK určuje `kufr/global.json` a setup-dotnet
jej ukládá do zapisovatelného `${{ runner.tool_cache }}/dotnet`.
CAD dále potřebuje PowerShell (na VPS ověřena oficiální verze 7.6.6).
Python 3.11 dodává setup-python; CAD závislosti se instalují do čerstvého
`RUNNER_TEMP/cad-venv`, jehož Python používají všechny následující kroky.
PR CI (běžný PR i synchronizační dispatch s `pr_number`) provede celé
ověření, build a balení bez uploadu do GitHub storage. Explicitní main recovery
CI ukládá vybraný balíček na jeden den; chybu uploadu neskrývá.
Site deployment si sestaví a ověří vlastní balíček a publikuje jej ve stejném
jobu. Nepotřebuje upload/download artefaktu ani sdílený adresář mezi runnery.
Přesun runneru neodstraňuje samostatnou GitHub artifact storage kvótu.
PlatformIO a OpenAI SDK ručního eval se instalují do vlastních venv
v `RUNNER_TEMP`, bez změny systémového Pythonu. Rozšířený ruční
**Kufr backend host probe** zkontroluje dostupnost nástrojů a Python venv.

PR CI nyní sdílí server s nasazením. GitHub environment secrets stále
dostávají jen explicitní main-only joby, ale dlouhodobý runner neposkytuje
izolaci jednorázového GitHub-hosted stroje. Spouštěj zde pouze důvěryhodná PR;
účet runneru má mít pouze existující omezené sudo pro aktivační helper.

### Izolace synchronizačního runneru

`dildovander-control` používá samostatný účet `kufr-control`, soukromý
`/home/kufr-control` a `/opt/actions-runner-control` (žádné sdílené actions,
workspace ani tool cache). Účet nemá sudo ani Docker. Binárky a konfigurace
runneru vlastní root; registrace používá `--disableupdate`. Správce musí
runner aktualizovat ručně z ověřeného oficiálního archivu před koncem
GitHub podpory verze. Oba build runnery zůstávají samostatné.

Label sám není bezpečnostní hranice. Root instaluje
[job hook](../scripts/ci/control-runner-hook.sh) jako
`/usr/local/sbin/kufr-control-job-started.sh` s právy 0755 a nastaví
`ACTIONS_RUNNER_HOOK_JOB_STARTED` v rootem vlastněném `.env` runneru.
Hook povoluje pouze `pr-sync.yml@refs/heads/main`, události
`pull_request_target`, `workflow_dispatch` a push do `main`. Při odmítnutí
ukončí proces `Runner.Worker` ještě před prvním workflow krokem; samotný
exit hooku není dostatečný proti kroku s `if: always()`.

Pro jednorázovou migraci před merge lze do rootem vlastněného souboru
`/etc/kufr-control/approved-workflows` (0644, adresář root/0755) zapsat dvojici
`Blato58/DildoVander/.github/workflows/pr-sync.yml@refs/heads/BRANCH SHA`.
SHA musí být přesná přezkoumaná `GITHUB_WORKFLOW_SHA`; výjimka povoluje
jen ruční dispatch. Po migraci soubor vyprázdni. Hook ani jeho rodičovské
adresáře nesmí být zapisovatelné build účtem. Ověř odmítnutí jiného workflow,
podvržených job env a kroků `always()`/`failure()` a poté úspěšný controller.
První negativní běh [38098122361](https://github.com/Blato58/DildoVander/actions/runs/38098122361)
na runneru 2.338.0 selhal před kroky a oba zakázané testovací soubory chyběly.

| Workflow | Účel | Výstup / vstup |
| --- | --- | --- |
| `ci.yml` | PR build dotčených projektů bez uploadu; ruční obnovovací build na `main` | Obnovovací artefakty vybraných projektů: `backend-publish`, `web-dist`, `firmware`, `arcane-site` |
| `pr-sync.yml` | Aktualizace PR z cíle před buildy | API-only writer; explicitní CI dispatch |
| `kufr-backend-deploy.yml` | Aktivace backendu bez restore/build | `build_run_id` úspěšného CI |
| `kufr-memory-provision.yml` | Explicitní CLI dry-run / verify / create OpenAI stores, bez exportu hostů | CI `build_run_id`; trvalá provisioning ID runneru |
| `kufr-ios-sign.yml` | Samostatný podepsaný device build; převzetí webu z CI | CI `build_run_id`; výstup Ad Hoc `signed-ipa` |
| `kufr-ios-deploy.yml` | Publikace existující Ad Hoc IPA bez Xcode/npm | `build_run_id` podepsaného IPA buildu |
| `kufr-site-deploy.yml` | Build, ověření a atomická publikace webu ve stejném jobu | Automatický main push nebo ruční dispatch; bez GitHub artefaktu |

Artefakty patří konkrétnímu běhu a API vybírá jejich neměnné ID. Retry úspěšného
jobu nahradí jeho balíček; retry pouze neúspěšných jobů může použít výstup
dřívějšího úspěšného pokusu stejného běhu. Celý běh musí být úspěšný.
ID běhu je číslo za
`/actions/runs/` v URL Actions. Každý balíček má `payload.tar.gz` a
`build-manifest.json`: repozitář, ID/pokus běhu, source/head/base SHA, zdrojový
Git tree, PR a SHA-256 payloadu. Tar zachovává executable bit backendu a firmware
výstupy. Obnovovací CI artefakty se uchovávají **1 den**, podepsané IPA **3 dny**.
Verifier předává původní neměnné artifact ID a run ID; další job stáhne přesně
tento artefakt a znovu ověří checksum při bezpečném rozbalení. Ověřovací kopie
se neuploadují. Retence serverových releases a veřejná instalace tím nejsou dotčené.

## Běžné nasazení

1. Aktualizuj PR z `main`, u změn kódu vyčkej na zelené **CI required**
   a sluč PR. Dokumentační PR ověř lokálně; automatické CI neběží.
2. Spusť **CI** na `main` s `project=backend` (pro IPA `project=web`,
   pro firmware `project=firmware`, případně `all`) bez interních PR vstupů.
   Po úspěchu vyber **Deploy Kufr backend**, větev `main` a ID tohoto CI.
   Zvol `dialog_mode`, `dialog_model`, `voice_mode` stejně jako dříve.
   Volitelné `live_actor_mode`, `hybrid_memory`, `cloud_indexing` mají bezpečné
   výchozí hodnoty legacy/false/false. Pro hybridní paměť nejdříve spusť
   explicitní provisioning workflow; [provozní návod](../kufr/docs/autonomous-live-memory.md#github-actions-na-produkčním-runneru)
   popisuje původ stores a kompatibilitu nativních klientů.
3. Pro publikaci aplikace spusť ručně **Build signed Kufr IPA** na `main`
   s týmž CI ID; workflow používá výhradně Ad Hoc distribuční podpis.
   **Deploy Kufr IPA** následně dostane ID úspěšného **IPA buildu**;
   veřejná instalace vyžaduje `ad_hoc`.

Deployment nejdřív v samostatném jobu na serverovém runneru ověří přes API očekávaný workflow,
celkově úspěšný běh, platný pokus stejného běhu a neexpirovaný artefakt. PR musí být sloučené
do `main` ze stejného repozitáře. API Git commitů ověří původ build merge commitu
z konkrétního base/head a **shodu celého Git tree s aktuálním `main`**. Squash
nebo merge mění commit SHA, proto nestačí porovnat SHA commitu. Checksum a
rozbalení odmítnou poškození, traversal, symlinky, hardlinky a speciální soubory.
Teprve po ověření dostane produkční job původní neměnné artifact ID;
stáhne stejný balíček a kontroluje, zda se `main` během čekání nezměnil.
Prostředí a secrets zůstávají oddělené. Chybějící či smazaný artefakt se odmítne;
deployment jej nenahradí jiným artefaktem stejného jména.

Pokud `main` mezitím změnilo obsah nebo artefakt expiroval, deployment skončí
s vysvětlením. Spusť ručně **CI** na `main` a použij jeho nový run ID. Backend,
firmware ani iOS deployment nepřekládá a nevytváří náhradní build. Site deployment
sestavuje web v témže jobu. Přísná shoda celého stromu může
vyžadovat obnovovací CI i po změně dokumentace; chrání proti nasazení jiné verze.
Rollback již uloženého release je nadále správcovská operace podle backend README.

Nativní iOS build se spouští pouze ručně pro podepsanou aplikaci určenou
k nasazení. Podpisový workflow nejdřív ověří připnutý Xcode a nativní
transport/debug testy, potom sestaví device IPA jednou, použije již hotový
web z CI a uplatní Ad Hoc Apple profil. Publikace ani její
opakování už nebuildí. Soukromé klíče a samostatné podpisové soubory se neukládají do artefaktů;
IPA obsahuje profil nutný pro instalaci podepsané aplikace.

## Optimalizace a standardy

- .NET restore používá lockfiles a NuGet cache, backend a testy se sestaví
  společně v Release pro `linux-x64`; test/run/publish používají `--no-build`.
- Vite/TypeScript pro podpis se sestaví na Linuxu v `kufr-web.yml`. Ruční
  podpisový build převezme hotový web. npm cache zahrnuje všechny dotčené lockfiles.
- PlatformIO Core i platforma jsou připnuté; cache ukládá toolchainy a pip,
  nikoli znovupoužité firmware binárky. Explicitní main build má artefakt pro
  pozdější servisní flash bez dalšího buildu; CI samo do desky nenahrává.
- Každý job má timeout. Actions jsou připnuté na commit SHA, checkout neponechává
  credentials, běžné CI má jen `contents: read`; čtení cizího Actions běhu pouze
  promotion/sign/deploy. Promotion verifier i jeho callers potřebují
  `pull-requests: read` pro důkaz sloučeného PR; žádné PR write oprávnění.
  PR CI běží na serverovém runneru bez produkčních environment secrets;
  main-only podmínky nasazení, podpisu a placených testů zůstávají zachované.
- Workflow syntax ověřuje připnutý actionlint s checksumem; testy ověřují
  původ/promotion i integritu archivu. `CODEOWNERS`, PR šablona, `CONTRIBUTING`,
  `SECURITY`, EditorConfig a Git attributes sjednocují předání a formátování.
- Dependabot dělá týdenní seskupené Actions/npm/NuGet aktualizace. Placené AI
  eval, backend/OTA publikace a iOS podpis zůstávají explicitní ruční operace.
  Site deploy běží po odpovídajícím main push; úspěšný ruční iOS podpis
  spustí publikaci IPA přes `workflow_run`.

## Nastavení GitHubu a skutečné blokace

Požadovaná nastavení jsou v
[repository-settings.json](../.github/repository-settings.json) a
[main-ruleset.json](../.github/main-ruleset.json): squash merge, mazání větví,
PR před merge, vyřešené review komentáře, zákaz
mazání/force push `main`. Sólo vlastník nemusí získávat review sám od sebe;
CODEOWNERS review není povinné. Pro tým lze přidat jedno povinné schválení.

Kontrola 1. 10. 2026: GitHub rulesets API vrátilo `403 Upgrade to GitHub Pro or
make this repository public to enable this feature`. Změna běžných repo voleb
vrátila `403 Resource not accessible by integration`. Tato pravidla tedy **nejsou
aktivovaná**. Repozitář zůstává privátní; tarif ani přístupová práva se nemění.
Majitel s repo admin oprávněním a podporovaným tarifem může použít:

```bash
# Nejdřív zobrazí konkrétní konfiguraci bez změn:
bash scripts/ci/apply-repository-settings.sh
# Aplikuje běžné volby a vytvoří/aktualizuje pouze pojmenovaný main ruleset:
bash scripts/ci/apply-repository-settings.sh --apply
```

Skript je opakovatelný, nedotýká se jiných rulesets a při chybě API skončí.
Běžné volby může admin nastavit i bez Pro, pravidlo větve vyžaduje uvedený tarif.
CI gate lze do té doby používat jako kontrolu před merge, GitHub jej ale
nevynucuje. Podpisové prostředí `ios-signing` a `backend-production` mají mít
povoleny deploymenty pouze z `main`; přístup k jejich správě nebyl dostupný.

Path filtr znamená, že dokumentační PR nemá status **CI required**.
Neaktivovat univerzální povinný status pro všechna PR: GitHub by u
dokumentačního PR čekal na workflow, které se záměrně nespustí.
Kontrola úspěšného CI pro změny kódu zůstává součástí postupu před merge.
