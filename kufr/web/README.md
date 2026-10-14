# Veřejný Arcane Workshop

Kořen HTTPS webu je rozcestník. `/workshop/` obsahuje aktuální generovaný
CAD viewer a úplný výrobní ZIP; `/ios/` zachovává podepsanou Safari instalaci.
`/companion/` je webový vývojový frontend, `/simulator/` odpojený veřejný náhled
pozorovací konzole. Veřejný web neposkytuje simulátoru WSS facade ani token.

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
