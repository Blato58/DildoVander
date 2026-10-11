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
Companion a kontroluje balíček. Workflow `kufr-site-deploy.yml` se automaticky
spouští po změnách příslušných zdrojů v `main`; přímý ruční běh je také možný.
Reusable workflow `kufr-site.yml` při `publish: true` sestaví, zabalí,
ověřeně rozbalí a publikuje web v jednom jobu ve vlastním dočasném adresáři.
Automatický ani ruční deploy nepotřebuje GitHub artifact storage; dvě kontroly
přesného SHA `main` proběhnou po sestavení a těsně před aktivací. Publikace
odmítne jinou větev, PR i jinou událost než push nebo přímý ruční běh.
Běžné CI pouze ověřuje; výslovný recovery build CI na `main` bez `pr_number`
uloží `arcane-site` na jeden den pro ruční obnovu. Lokální staging se vždy uklidí.
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
