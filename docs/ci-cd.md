# CI/CD a oddeleni produkce

Zdrojovy [Duloc-Dungeon/DildoVander](https://github.com/Duloc-Dungeon/DildoVander) obsahuje
hostovane buildy. Produkcni runner, administrace, diagnostika a placene AI
workflow patri do privatniho
[Duloc-Dungeon/DildoVander-deploy](https://github.com/Duloc-Dungeon/DildoVander-deploy).
Priprava hranice sama nemeni viditelnost zdrojoveho repozitare.

## Zdrojove buildy

Zdrojova workflow pouzivaji GitHub-hosted runners. PR CI nema
produkcni prostredi, SSH ani provider credentials. `pr-sync.yml` pouziva pouze
API a duveryhodny kod vychozi vetve; jeho private guard po skutecnem
zverejneni vypne controller. Fork PR se nemeni.

Zmeny pouze nazvu nebo popisu PR preskoci build jobs a pouzivaji oddelenou
concurrency skupinu, aby nezrusily probihajici validaci kodu. Zmena cilove vetve
znovu validuje uplny diff. Agregat `CI required` se nikdy nepreskoci: pro
metadata-only udalost read-only verifier vyzaduje dokonceny uspesny produktovy
run stejneho workflow, PR, presneho head/base a GitHub Actions checku. Tyto SHA
jsou ulozeny v nazvu runu od jeho vzniku, nikoli prevzaty z editovatelneho PR
popisu. Metadata run sam neni dukazem pro dalsi reuse. Na probihajici produktovy
run ceka nejvyse 20 minut v hostovanem gate jobu; chybejici, neuspesny,
zruseny, nejednoznacny nebo stary dukaz selze. Po vyprseni cekani lze gate
opakovat po skutecnem dokonceni produktu; lokalni agent nemusi zustat aktivni.
Pri soubeznem vyberu `web` a `site` provede web job
sdilene unit testy a build jednou; site prevezme `shared-web-dist` ze stejneho
runu a zachova browser testy, sestaveni webu a baleni. Samostatny site build
provede i sdilene testy a build. Tento interni artefakt neni zdrojem pro podpis
ani produkcni promotion; stavajici `web-dist` a `arcane-site` zustavaji
vystupy explicitniho buildu aktualniho `main`.

| Workflow | Ucel |
| --- | --- |
| `ci.yml` | PR kontroly, secret scan historie checkoutu, rucni main build |
| `main-release.yml` | Po kazdem push na main overene CI all a oba podpisy; rucni recovery |
| `kufr-backend.yml` | Backend/testy, backend-publish a backend-diagnostics |
| `kufr-firmware.yml` | ESP-IDF host testy a firmware build |
| `kufr-web.yml` | Web a offline testy |
| `kufr-ios.yml` | Nativni Swift testy a nepodepsany iOS Simulator build na macOS |
| `kufr-site.yml` | Jedna Vue aplikace a viewer fixtures bez CAD; kompatibilni nazev artefaktu arcane-site |
| `kufr-ai-eval.yml` | Pouze offline synteticky corpus a parser |
| `reuse-build.yml` | Overeni aktualniho main artefaktu pred podpisem |
| `kufr-ios-sign.yml` | Podepsane Ad Hoc IPA na macOS, main controller nebo rucni recovery |
| `kufr-ios-app-store.yml` | Podepsane App Store IPA na macOS, main controller nebo rucni recovery |
| `pr-sync.yml` | API-only synchronizace vlastnich PR za private podminky |
| `dependabot-generate.yml` | Read-only regenerace NuGet lockfiles a nativnich Capacitor pinu |
| `dependabot-publish.yml` | Overeny API-only commit generovanych souboru do Dependabot vetve |

### Generovane zavislosti v Dependabot PR

NuGet konfigurace zahrnuje backend i jeho testovaci projekt ve stejne skupine.
Po otevreni nebo aktualizaci vlastniho Dependabot PR generator obnovi oba
`packages.lock.json` prikazem `dotnet restore` nad testovacim projektem a overi
`--locked-mode`. Zachovava oba deklarovane Linux/Windows runtime grafy.
Zmena presne verze Capacitor core/CLI/iOS spusti na macOS skutecny webovy build,
`cap sync ios` a `xcodebuild -resolvePackageDependencies`; vysledkem jsou
`Package.swift` a `Package.resolved`. Samotna zmena Vite macOS generator nespousti.

Generator ma pouze read-only token, nepouziva cache ani produkcni secrets
a checkoutuje nemenny overeny PR head bez ulozenych credentials.
Oddeleny `workflow_run` publisher spousti jen duveryhodny kod `main`, znovu
overi puvodniho Dependabot autora, vlastni repozitar, povolene zmeny manifestu,
identity artefaktu a aktualni head/base. Z artefaktu prijima pouze povolene
generovane soubory; jejich obsah nespousti. Zapis do vetve je nonforce commit
nad overenym headem. Pri soubeznem posunu se stary vysledek nepouzije.

Po zmene publisher vyvola `ci.yml` s presnym novym head/base SHA, protoze commit
pres `GITHUB_TOKEN` sam dalsi workflow nespousti. Opakovani po selhani dispatch
umi dokoncit CI bez dalsiho commitu. Shodne vystupy jinak zadny commit netvori.
Bezne PR CI nadale provadi locked restore, nativni testy a Simulator build;
generator tato overeni nenahrazuje. Workflow nemeni fork PR ani lidske PR.

Read-only zkouska je rucni dispatch generatoru na `main` s `pr_number`;
volitelna `revision` musi byt skutecnym commitem daneho Dependabot PR.
Lze tak reprodukovat i puvodni chybu uz slouceneho PR. Rucni beh nikdy nepise
do vetve ani nespousti publisher. Selhani je videt v prislusnem Actions behu;
po oprave generator opakuj. Zastaveni automatizace znamena vypnout obe tato
workflow; existujici locked-mode a nativni PR kontroly zustavaji aktivni.

Navrh vychazi z [pravidel GitHub tokenu](https://docs.github.com/en/actions/concepts/security/github_token)
a [bezpecnostnich hranic Actions](https://docs.github.com/en/actions/reference/security/secure-use).
Nepotrebuje `pull_request_target` ani vyjimku z jeho vychozi event policy.

UI, backend a kontrakty nespousteji CAD regeneraci. Job `site` sestavi jednu
Vue aplikaci z `mobilni-aplikace` na korenove ceste a baleni
`web/build-site.py` spotrebuje pouze jeji `dist`. Neimportuje CAD Python,
neoveruje vyrobni ZIP a nekopiruje stare Companion, simulator, guest ani viewer
HTML/JS balicky. Viewer fixture testy nepotrebuji CAD toolchain. Guard
`scripts/ci/ui_boundary.py` kontroluje i volane lokalni actions, web/iOS workflow
a build entry points a odmita vyrobni generovani v UI workflow.

CAD je od rozhodnuti majitele 20. 10. 2026 z CI/CD odstranen. PR, push na
`main` ani rucni recovery build nespousteji geometrii, rendery, slicer nebo
vyrobni overeni a nepublikuji CAD cache. `project=all` zahrnuje pouze softwarove
jobs; `project=cad` a `force_cad` nejsou podporovany. `CI required` nadale
vyzaduje uspech vsech vybranych softwarovych kontrol vcetne macOS pro iOS.
Lokalni CAD nastroje, kontroly zdroju a provenience zustavaji zachovany;
mechanickou akceptaci je nutne provest lokalne podle
[CAD README](../mechanika/final/README.md#inkrementální-cad-a-ci).
Zelene softwarove CI neprokazuje vyrobni overeni.

Aplikace, ModelBundle a offline distribuce maji oddelenou provenienci a
aktivaci. Pripraveny postup, cache pravidla, persistence identity a rollback
jsou v [application releases](../web/deploy/APPLICATION-RELEASES.md).

Secret scan pouziva pripnuty Gitleaks 8.30.1 a kontroluje celou historii `HEAD`:
u PR oba rodice merge checkoutu, u rucniho buildu historii vybrane vetve.
Zachyti i credential odstraneny v pozdejsim commitu. Nesouvisejici stazene
vetve a tagy neblokuji cizi PR. [Konfigurace](../.gitleaks.toml) zachovava
vsechny vychozi detektory; pouze `generic-api-key` ma vyjimky kombinujici
presnou cestu a podobu zname necredential hodnoty. SHA-256 inventare souboru
v projektovych JSON nejsou API klice; nove commity ani squash pro ne
nepotrebuji dalsi fingerprint v `.gitleaksignore`. Historicke fingerprinty
zustavaji pro drive posouzene nalezy. Cele soubory ani detektory se nevypinaji.
Kazdy scan nejprve spusti [regresni testy](../scripts/ci/test_secret_scan.py)
se skutecnym binary: nove/squash inventare projdou, synteticke credentialy ve
stejnych souborech a v odstranene historii selzou. Lokalni prikaz:
`GITLEAKS_BINARY=/cesta/gitleaks python -m unittest discover -s scripts/ci -p 'test_secret_scan.py'`.

Od rozhodnuti majitele 14. 10. 2026 se zmeny mobilu, contracts, simulatoru,
design-systemu a iOS workflow overuji i na macOS s Xcode 26.3. `kufr-ios.yml`
spousti existujici nativni Swift harnessy a nepodepsany Simulator build po
Capacitor sync; aplikaci v Simulatoru nespousti. Vysledek musi projit v
`CI required`. Podpisove prostredi ani secrets se do PR CI neprenaseji.

Deployment jobs nejsou ve zdrojovem repozitari. Jeho self-hosted registrace
musi byt odstraneny pri migraci; odstraneni YAML nebo labelu registraci
nenahrazuje. Provoz privatniho repozitare popisuje jeho `README.md`.

## Priprava artefaktu a podpis IPA

Od rozhodnuti majitele 24. 10. 2026 kazdy push (vcetne merge) na kanonicky
`main` spousti **Build current main release**. Hostovany controller pouziva
jen verejny `GITHUB_TOKEN` s `contents: read` a `actions: write`. Vyvola
stare `ci.yml` pres `workflow_dispatch` s `project=all`, po jeho uspechu
postupne `kufr-ios-sign.yml` a `kufr-ios-app-store.yml` se stejnym CI run ID.
Apple credentials zustavaji jen ve stavajicich podpisovych prostredich;
controller nema produkcni prostredi, cross-repository token ani SSH.

Controller kontroluje vlastni nemenny SHA a aktualni `main` pred dispatchi,
behem cekani a po dokonceni. Sdilena concurrency skupina nerusi aktivni
behy; zastaraly controller skonci s vysledkem `superseded` bez ruseni deti.
GitHub muze nahradit cekajici concurrency run novejsim; promuje se pouze
aktualni `main`. Cely controller ceka nejvyse 150 minut, korelace noveho
behu nejvyse tri minuty. Chybejici, nejednoznacny nebo neuspesny beh zablokuje
dalsi kroky. Stabilni interni `release_request` UUID a presny run-name
umoznuji opakovani stejneho controlleru bez duplicitnich deti. Selhane dite
se automaticky neopakuje; nove rucni spusteni controlleru je novy recovery
pokus. Po nejiste odpovedi dispatch API controller nezavola dispatch znovu.

Artefakt `main-release-receipt` obsahuje `main-release.json` se schematem 1:
`repository`, `source_sha`, `controller_run_id`, `controller_run_attempt`,
`ci_run_id`, `adhoc_run_id`, `app_store_run_id` a `outcome`. Jeho retence je
14 dni; produktove balicky maji vlastni kratsi retenci. Pouze uspesny
controller presneho aktualniho main a receipt s `outcome=success` je kandidat
pro oddelenou privatni automatickou publikaci vcetne TestFlight. Privatni
orchestrace znovu overuje behy a artefakty; receipt samotny neprokazuje
produkci. `release_request` je pouze korelace, nikoli opravnujici credential.

Pro rucni zotaveni lze spustit cely controller nebo jednotlive existujici kroky:

1. Po kontrole a slouceni spust zdrojove **CI** na `main` s projektem
   `backend`, `web`, `ios`, `firmware`, `site` nebo `all`. Interni `pr_number`,
   `expected_head_sha` a `expected_base_sha` nech prazdne.
2. Pro IPA spust **Build signed Kufr IPA** na `main` s ID uspesneho CI,
   ktery obsahuje `web-dist`.
3. Pripadnou samostatnou recovery aktivaci spust v privatnim deployment repozitari na `main`.
   IPA publisher dostane ID podepsaneho buildu, ostatni produkty ID CI.

PR artefakt ani artefakt ze slouceneho PR neni promotovatelny. Po prepisu
historie je nutny novy primy dispatch build aktualniho `main`. Verifier kontroluje
kanonicky repozitar i head repozitar, workflow, celkove uspesny
`workflow_dispatch` na `main`, ID/pokus behu, manifest a presny aktualni
commit i cely Git tree. Stejny tree s jinym commitem nestaci. API vybere
jediny neexpirovany artefakt a preda nemenne ID a GitHub SHA-256 digest.
Payload ma dalsi SHA-256 v `build-manifest.json`; bezpecne rozbalovani odmita
traversal, odkazy, specialni a duplicitni soubory.

Retence CI balicku je jeden den, `signed-ipa` tri dny. `backend-diagnostics`
obsahuje zkompilovany synteticky testovaci harness bez provider klice,
produkcnich dat nebo konfigurace. Samotny build nevola placenou AI.

Podpisovy workflow funguje pred i po zverejneni. Pouze kanonicky dispatch na
`main` pouziva prostredi `ios-signing` nebo `ios-app-store`; controller zadne
podpisove secrets necte. Po overeni webu provede Capacitor
sync, vytvori puvodni ikonu pomoci `scripts/generate-native-assets.mjs`,
overi pripnuty Xcode a nativni testy. Tesne pred podpisem znovu overi `main`.
Podpisova tajemstvi patri vyhradne do tohoto prostredi:

- `IOS_DISTRIBUTION_CERTIFICATE_BASE64`
- `IOS_DISTRIBUTION_PROFILE_BASE64`
- `IOS_P12_PASSWORD`
- `IOS_KEYCHAIN_PASSWORD`

Privatni klice ani samostatne podpisove soubory se neuploaduji. IPA obsahuje
profil nutny pro instalaci. Skutecna iPhone akceptace zustava samostatna.

## Privatni publikace a operator gate

Privatni workflow pouzivaji vlastni zkontrolovane kopie deployment nastroju,
nikdy checkout verejneho PR/head kodu na produkcnim hostu. Privatni verifier
ma jen `PUBLIC_SOURCE_READ_TOKEN` omezeny na **Actions: read** a
**Contents: read** zdrojoveho repozitare. Token je nutny i dokud zdroj zustava
privatni: vychozi token jineho repozitare nema automaticky cross-repo pristup.
Hodnotu zadava operator primo v GitHub nastaveni, nikdy do chatu nebo zdroju.

Publisher explicitne overi GitHub SHA-256 ZIPu i payload checksum a znovu
kontroluje source `main` tesne pred aktivaci. Backend zachovava root-owned
activation helper; web atomickou aktivaci, HTTPS readback a rollback pri
selhani; OTA root-owned podpisovou autoritu. IPA ani web publisher nebuildi.

Cross-repository `workflow_run` se nepouziva. Publikaci uspesneho aktualniho
main po kazdem merge autorizuje vyse popsana automaticka cesta. Diagnostika,
provisioning a placeny test nadale vyzaduji explicitni maintainer dispatch.
Detaily soukromeho controlleru a produkcnich jobs vede privatni README. Prostredi
`backend-production` seskupuje secrets; neznamena aktivni required reviewers.
Pro privatni repo na Free/Pro/Team tito reviewers nejsou k dispozici.
Operator recovery gate tvori rucni dispatch a minimalni write pristup k privatnimu
repozitaru. Automaticka cesta musi overit presny soucasny main a duveryhodne
zdrojove buildy. Ochrany pri podporovanem tarifu musi spravce skutecne overit.

Migrace registraci, credentials a root-owned OTA verifieru se doklada
skutecnymi API/runner vysledky; novy kod sam neprokazuje presun ani deployment.
Privatni README vede presne secret/variable metadata a puvod vetted kopii.

Migrace 12. 10. 2026 byla provedena: zdroj nema zadny VPS runner ani
prostredi `backend-production`; dva backend runnery jsou pouze v privatnim
deployment repozitari. Root-owned job hook povoluje jen rucni workflow na
privatnim main a presny zkontrolovany commit. Odmitnuty probe nespustil zadny
krok, vcetne `always()`. Produkcni release, proces aplikace a health zustaly
stejne. Pet aktivnich produkcnich secrets bylo preneseno sifrovane; ADMIN
credential byl overen ctenim diagnostiky bez volani AI. Apple podpisove
secrets zustaly ve zdrojovem `ios-signing`, omezenem na main.

Standardni GitHub-hosted buildy vcetne macOS jsou po zverejneni zdroje zdarma.
Zdroj byl se souhlasem majitele zverejnen 12. 10. 2026; private deployment
repo zustava soukromy. Main quality gate je aktivni, vychozi workflow token
read-only, externi PR workflow vyzaduji schvaleni a Actions SHA pinning je
povinny. Private vulnerability reporting je zapnuty.
Ucet drive dosahl kvoty artifact storage; tato kvota blokovala upload
diagnostickeho artifactu i po uspesnem cteni. Vsech 18 zbyvajicich artifactu
uctu bylo se souhlasem majitele odstraneno; prepocet kvoty a novy uspesny
upload maji samostatnou akceptaci.

## Kontroly a nastaveni zdroje

Actions jsou pripnute na commit SHA, checkout nema persistovane credentials.
CI overuje workflow checksumem overenym actionlint, provenance, integrity,
vybrane projekty a redigovany secret scan. Secret scan je povinny i pro
PR pouze s dokumentaci; komponentni build se vybira podle skutecneho diffu.

Pozadovana pravidla jsou v
[repository-settings.json](../.github/repository-settings.json) a
[main-ruleset.json](../.github/main-ruleset.json). Jejich skutecne zapnuti musi
spravce overit GitHub API; lokalni soubory nejsou dukaz aktivace.
`bash scripts/ci/apply-repository-settings.sh` zobrazi konfiguraci;
`--apply` vyzaduje spravcovska opravneni a podporu tarifu.

Build, zverejneni zdroje, release, deployment, placeny AI test a flash desky
zustavaji odlisne operace. Automaticke main buildy, podpis a nasledne nasazeni
autorizuje vyse popsana main release cesta. Placenou AI ani flash desky nespousti.
