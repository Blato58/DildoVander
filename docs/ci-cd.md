# CI/CD a oddeleni produkce

Zdrojovy [Blato58/DildoVander](https://github.com/Blato58/DildoVander) obsahuje
hostovane buildy. Produkcni runner, administrace, diagnostika a placene AI
workflow patri do privatniho
[Blato58/DildoVander-deploy](https://github.com/Blato58/DildoVander-deploy).
Priprava hranice sama nemeni viditelnost zdrojoveho repozitare.

## Zdrojove buildy

Zdrojova workflow pouzivaji GitHub-hosted runners. PR CI nema
produkcni prostredi, SSH ani provider credentials. `pr-sync.yml` pouziva pouze
API a duveryhodny kod vychozi vetve; jeho private guard po skutecnem
zverejneni vypne controller. Fork PR se nemeni.

| Workflow | Ucel |
| --- | --- |
| `ci.yml` | PR kontroly, secret scan historie checkoutu, rucni main build |
| `kufr-backend.yml` | Backend/testy, backend-publish a backend-diagnostics |
| `kufr-firmware.yml` | ESP-IDF host testy a firmware build |
| `kufr-web.yml` | Web a offline testy |
| `kufr-ios.yml` | Nativni Swift testy a nepodepsany iOS Simulator build na macOS |
| `kufr-site.yml` | Rychle PR kontroly webu/vieweru a Companion; rucni main build celeho arcane-site |
| `kufr-cad.yml` | Samostatna CAD gate a naplneni sdilene cache po zmene CAD vstupu na main |
| `kufr-ai-eval.yml` | Pouze offline synteticky corpus a parser |
| `reuse-build.yml` | Overeni aktualniho main artefaktu pred podpisem |
| `kufr-ios-sign.yml` | Rucni podepsane Ad Hoc IPA na macOS |
| `pr-sync.yml` | API-only synchronizace vlastnich PR za private podminky |

PR zmeny mobilu, backendu, kontraktu a verejneho webu nespousteji CAD regeneraci.
Samostatny job `cad` je povinny pro zmeny `kufr/mechanika/final/`, sdileneho
design-systemu, pripnuteho sliceru, Node verze a dvou dokumentu pouzitych ve
zdrojovem snapshotu (rozmery soucastek a potvrzene zapojeni). To zahrnuje i
Markdown uvnitr vyrobniho balicku. Job `site` v PR testuje sestaveni Companionu,
viewer a sestaveni skutecnych statickych zdroju builderem s izolovanymi
syntetickymi CAD fixtures; kompletni vyrobni ZIP nevytvari. Po sestaveni
Companionu pouziva i jeho skutecne `dist`, nikoli pouze HTML fixture.
Rucni recovery `project=site` nebo `all` stale regeneruje a overuje cely CAD
balicek pred sestavenim publikovatelneho artefaktu. `project=cad` spusti pouze
CAD gate. Kontrola `CI required` vyzaduje uspech vsech vybranych jobs.

CAD workflow po relevantnim push na `main` naplni cache vychozi vetve;
nic nepublikuje ani nenasazuje. GitHub cache vytvorena pro `refs/pull/N/merge`
je dostupna jen temuz PR, ne jinym PR ani main. Cache main mohou cist dalsi PR
([pravidla GitHub cache](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching#restrictions-for-accessing-a-cache)).
Proto samotne opakovane ukladani PR cache nezajisti sdileni napric PR.

Sdilena composite action `verified-cad` pouziva CI wrapper `scripts/ci/cad_cache.py`.
Klic v2 zachovava presne zdroje, Python, vsechny distribuce, architekturu,
Ubuntu 24, glibc ABI a slicer vcetne native/Flatpak hash. Vynechava pouze cislo
kernelu a datum image rollout: GitHub pridelil stejnym vstupum soubezne image
`20261004.327.1` a `20261012.334.1`, coz drive vytvorilo ruzne klice i pri
identickych nastrojich. Skutecne host metadata zustavaji v diagnostice CI.
Fallback na v1 se smi adoptovat jen pri shode teto identity a po puvodni
uplne kontrole zdroju, sliceru, SHA-256 vsech vystupu a kazdeho clena ZIPu.
Wrapper zmeni pouze cache receipt; vyrobni vystupy ani jejich overeni neupravuje.
Zmena zdroju, knihoven, sliceru nebo ABI stale vyzaduje celou regeneraci.
Lokalni `Regenerate.ps1` zachovava puvodni prisnou host identitu.
Python testy sdili memoizovanou geometrii v jednom procesu;
generovani, overeni a baleni zachovavaji oddelene ciste CAD procesy.
Lokalne lze vynutit vsechny kroky parametrem `-Force`.

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

1. Po kontrole a slouceni spust zdrojove **CI** na `main` s projektem
   `backend`, `web`, `ios`, `firmware`, `site` nebo `all`. Interni `pr_number`,
   `expected_head_sha` a `expected_base_sha` nech prazdne.
2. Pro IPA spust **Build signed Kufr IPA** na `main` s ID uspesneho CI,
   ktery obsahuje `web-dist`.
3. Aktivaci spusti explicitne v privatnim deployment repozitari na `main`.
   IPA publisher dostane ID podepsaneho buildu, ostatni produkty ID CI.

PR artefakt ani artefakt ze slouceneho PR neni promotovatelny. Po prepisu
historie je nutny novy primy rucni build aktualniho `main`. Verifier kontroluje
kanonicky repozitar i head repozitar, workflow, celkove uspesny
`workflow_dispatch` na `main`, ID/pokus behu, manifest a presny aktualni
commit i cely Git tree. Stejny tree s jinym commitem nestaci. API vybere
jediny neexpirovany artefakt a preda nemenne ID a GitHub SHA-256 digest.
Payload ma dalsi SHA-256 v `build-manifest.json`; bezpecne rozbalovani odmita
traversal, odkazy, specialni a duplicitni soubory.

Retence CI balicku je jeden den, `signed-ipa` tri dny. `backend-diagnostics`
obsahuje zkompilovany synteticky testovaci harness bez provider klice,
produkcnich dat nebo konfigurace. Samotny build nevola placenou AI.

Podpisovy workflow funguje pred i po zverejneni. Vyhradne rucni kanonicky
`main` pouziva prostredi `ios-signing`. Po overeni webu provede Capacitor
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

Cross-repository `workflow_run` se nepouziva. Kazda publikace, diagnostika,
provisioning a placeny test vyzaduje explicitni maintainer dispatch. Privatni
repo nema automaticke PR ani cron spousteni produkcnich jobs. Prostredi
`backend-production` seskupuje secrets; neznamena aktivni required reviewers.
Pro privatni repo na Free/Pro/Team tito reviewers nejsou k dispozici.
Operator gate tvori rucni dispatch a minimalni write pristup k privatnimu
repozitaru. Ochrany pri podporovanem tarifu musi spravce skutecne overit.

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
zustavaji odlisne operace. Toto rozdeleni workflow je automaticky nespousti.
