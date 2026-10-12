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

CI projekt `site` regeneruje CAD přes `final/Regenerate.ps1`, sestaví webový
Companion a kontroluje balíček na GitHub-hosted `ubuntu-24.04`.
Workflow `kufr-site-deploy.yml` je pouze ruční, pro původní soukromý repozitář
a `main`; push jej automaticky nespouští. Reusable `kufr-site.yml` při
`publish: true` sestaví/zabalí web na hosted runneru a uloží `arcane-site`
na jeden den. Oddělený publish job s prostředím `backend-production` stáhne
přesné neměnné artifact ID stejného runu, ověří provenance a bezpečně rozbalí
balíček ve vlastním dočasném adresáři. Kontroly aktuálního SHA `main` chrání
proti zastaralé aktivaci. Publikace odmítne veřejný repozitář, jinou větev,
PR i jinou událost než přímý ruční běh.
Běžné CI pouze ověřuje; výslovný recovery build CI na `main` bez `pr_number`
uloží `arcane-site` na jeden den pro ruční obnovu. Lokální staging se vždy uklidí.
Před zveřejněním repozitáře je nutné přesunout privátní publish job a jeho
production-connected runner do oddělené důvěryhodné infrastruktury;
podmínka `private` sama nezajišťuje izolaci registrovaného runneru. Ochrany
GitHub environment musí správce nastavit a ověřit, viz [CI/CD](../../docs/ci-cd.md).
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
