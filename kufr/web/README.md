# Veřejný web · Vapour Chrome

[Show Pack](../docs/show-pack.md) doplňuje identitu a sběratelskou kartu do
stávající památky, `/keepsake/chronicle.html` a `/keepsake/duel.html`.
`build-site.py --keepsake-only` balí všechny tři návštěvnické cesty bez CAD.
Kronika a výsledek duelu používají allowlist `/guest/v1`; soukromé proroctví,
portrét, operátor a tahy duelu mají nadále oddělená oprávnění. Proxy příklady
generuj z `deploy/keepsake_proxy.py`, neupravuj je ručně ani nepovoluj catch-all.

Kořen HTTPS webu vede návštěvníka nejprve k památce z večera; dílna a její
nástroje tvoří samostatný přehled níže. `/workshop/` obsahuje aktuální generovaný
CAD viewer a úplný výrobní ZIP; `/ios/` zachovává podepsanou Safari instalaci.
`/companion/` je webový vývojový frontend, `/simulator/` odpojený veřejný náhled
pozorovací konzole. Veřejný web neposkytuje simulátoru WSS facade ani token.

`/keepsake/` je kanonický návštěvnický portál DV10. `build-site.py` kopíruje
jeho runtime soubory včetně `theme.js`, veřejný přehled večera a společný
lokální design systém do téhož inventáře. Vzhled Vapour Chrome používá
historický vstup `arcane-workshop.css` a sémantické `--aw-*` tokeny.
Soukromý portál i veřejný přehled používají nativní komponenty z
`/design-system/ui.js` pro hlášení, stav, výběr hůlky a akce. Builder ověřuje
přítomnost společného runtime a generovaných tokenů i v samostatném balíčku
`--keepsake-only`; chybějící framework odmítne před publikací.
Volba vzhledu respektuje systémové nastavení (bez preference je tmavý)
a umožňuje světlý či tmavý režim pomocí `data-theme`. Změna platí jen pro
aktuální stránku; preference se neukládá do úložiště prohlížeče.
Owner URL je `/keepsake/?k=<locator>`; `&view=share` vždy vynucuje omezený
sdílený pohled. Locator sám neuděluje přístup. Fragmenty `#c=`, `#r=` a `#s=`
se odstraní před načtením stylů; výměna klíče vyžaduje klepnutí návštěvníka.
Osobní data používají pouze same-origin `/guest/v1/`, serverovou cookie,
CSRF a idempotentní mutace. Fotografie se nejdřív zobrazí lokálně a odešle
až po výslovném souhlasu. GET ani polling nespouští generování.

Syntetické browser kontroly: `node kufr/web/keepsake/test-browser.cjs`
(Playwright z dostupného runtime, případně `NODE_PATH`). Kontrola zdrojů
a izolovaného balení: `python -m unittest discover -s kufr/web/keepsake -p 'test_*.py'`.
Fake odpovědi jsou pouze v testu; nejsou součástí publikovaného balíčku.
Browser výsledek neprokazuje přijetí na fyzickém iPhonu ani kvalitu OpenAI.
Veřejný přehled se ověřuje samostatně přes
`node kufr/web/keepsake/test-evening-browser.cjs`. Obě sady používají pouze
syntetické odpovědi; ověřují také úzké displeje, 200% zoom, klávesnici,
omezený pohyb, oddělení veřejných a soukromých dat a částečné výsledky.

`build-site.py` přijímá pouze aktuálně ověřené exporty, kontroluje otisky
zdrojů, výstupů a ZIPu a sestaví výslovný inventář bez starých exportů.
`publish-site.py` ověří všechny soubory a atomicky přepne symlink `current`;
předchozí release zůstává pro návrat. SHA zdroje je v `site-manifest.json`.
Neúspěšný HTTPS readback vrátí předchozí release; při úplně první publikaci
odstraní pouze její vlastní aktivní symlink a obnoví původní stav bez webu.
Rollback odmítne změnit jiný mezitím aktivovaný release.
Po úspěšném veřejném ověření deploy ponechá tři ověřené site releases,
vždy včetně aktivní a předchozí verze. Maže pouze starší přímé adresáře
s úplným platným inventářem tohoto publisheru; neznámé soubory, adresáře
a symlinky zachová. iOS releases do této retence nespadají.
Všechny CLI mutace sdílejí trvalý Linux flock `.publication.lock`;
retence navíc odmítne změněnou aktivaci od posledního veřejného ověření.

CI projekt `site` v PR sestaví Companion a ověří skutečné statické zdroje
builderem s izolovanými syntetickými CAD fixtures; ty se nepublikují a neprokazují
výrobní ověření. Samostatná povinná gate `cad` regeneruje výrobní výstupy pouze
při změně CAD vstupů. Ruční main build projektu `site` nebo `all` dál regeneruje
CAD přes `final/Regenerate.ps1` a sestaví celý balíček na hosted `ubuntu-24.04`.
Primy rucni CI build aktualniho `main` bez PR vstupu ulozi `arcane-site`
na jeden den. Publikace `kufr-site-deploy.yml` patri do privatniho
`Blato58/DildoVander-deploy`; maintainer ji spusti rucne na `main` s ID CI.
Hosted verifier kontroluje workflow/run, aktualni source commit/tree a
nemenne artifact ID. Privatni publisher pouziva vlastni vetted helpers,
overi SHA-256 ZIPu i payloadu, bezpecne rozbali balicek a znovu zkontroluje
source `main` tesne pred atomickou aktivaci. HTTPS readback, rollback i
retence zustavaji zachovane. Zdrojovy repozitar nema produkcni workflow;
skutecna migrace registraci a pristupu se doklada oddelene.
Podrobnosti jsou v [CI/CD](../../docs/ci-cd.md).
Nginx routy
instaluje správce jednou přes `deploy/install-site-host.sh github-runner`.
Installer zachová ostatní routy, privátně zazálohuje konfiguraci a při chybě
obnoví původní stav. Nemění backendovou službu, credentialy ani databáze.
HTTPS šablona obsahuje volitelné include pro Workshop a CloudVPSBridge,
aby opakovaný původní bootstrap neztratil již nainstalované rozšíření.

Po úspěšném ověření nového webu odstraňuje deploy výhradně dva historické
soubory `/var/www/kufr-ios/kufr-mechanika-prototyp.zip` a `viewer.html`.
Aktuální IPA, manifesty a iOS releases ponechá. Ruční rollback provádějte
stejným CLI `python3 publish-site.py --site /var/www/dildovander --rollback ID`;
nepřepínejte symlink mimo tento zámek. Konfiguraci lze obnovit z root-only zálohy.
