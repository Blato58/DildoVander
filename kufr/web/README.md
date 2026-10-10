# Veřejný Arcane Workshop

Kořen HTTPS webu je rozcestník. `/workshop/` obsahuje aktuální generovaný
CAD viewer a úplný výrobní ZIP; `/ios/` zachovává podepsanou Safari instalaci.
`/companion/` je webový vývojový frontend, `/simulator/` odpojený veřejný náhled
pozorovací konzole. Veřejný web neposkytuje simulátoru WSS facade ani token.

`build-site.py` přijímá pouze aktuálně ověřené exporty, kontroluje otisky
zdrojů, výstupů a ZIPu a sestaví výslovný inventář bez starých exportů.
`publish-site.py` ověří všechny soubory a atomicky přepne symlink `current`;
předchozí release zůstává pro návrat. SHA zdroje je v `site-manifest.json`.

CI projekt `site` regeneruje CAD přes `final/Regenerate.ps1`, sestaví webový
Companion a kontroluje balíček. Workflow `kufr-site-deploy.yml` se automaticky
spouští po změnách příslušných zdrojů v `main`; přímý ruční běh je také možný.
Před publikací i po stažení artefaktu odmítne zastaralý main. Nginx routy
instaluje správce jednou přes `deploy/install-site-host.sh github-runner`.
Installer zachová ostatní routy, privátně zazálohuje konfiguraci a při chybě
obnoví původní stav. Nemění backendovou službu, credentialy ani databáze.
HTTPS šablona obsahuje volitelné include pro Workshop a CloudVPSBridge,
aby opakovaný původní bootstrap neztratil již nainstalované rozšíření.

Po úspěšném ověření nového webu odstraňuje deploy výhradně dva historické
soubory `/var/www/kufr-ios/kufr-mechanika-prototyp.zip` a `viewer.html`.
Aktuální IPA, manifesty a iOS releases ponechá. Pro rollback lze nastavit
`current` na předchozí release a obnovit konfiguraci z root-only zálohy.
