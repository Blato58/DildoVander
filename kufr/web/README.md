# Veřejná aplikace · Vapour Chrome

Hostovaná aplikace vzniká z `kufr/mobilni-aplikace`: Vue komponenty, router,
společný shell a stejné adaptéry pro Capacitor. Home, Kufr, Workshop, Archiv
a nastavení jsou její routy. `/ios/` a instalační manifesty zůstávají
samostatnou distribuční výjimkou. Připravený kód ani tato dokumentace
neznamenají provedené produkční nasazení.

[Show Pack](../docs/show-pack.md) rozšiřuje stejné Vue obrazovky o identitu,
sběratelskou kartu, soukromé proroctví, duel a kroniku. Veřejné cesty
`/archive/chronicle` a `/archive/duel` mají výslovné kompatibilní odkazy z
`/keepsake/chronicle.html` a `/keepsake/duel.html`; nejde o další HTML aplikace.
Oprávnění pro vlastníka památky, tah duelu a obsluhu zůstávají oddělená.
Proxy generuj z `deploy/keepsake_proxy.py`, bez obecného API catch-all.

`build-site.py --web-dist <dist> --destination <nový-adresář>` balí výhradně
hotovou aplikaci. Neimportuje CAD Python, negeneruje geometrii, neověřuje
výrobní ZIP a nekopíruje staré Companion, simulátor, guest ani viewer balíčky.
`site-manifest.json` schema 2 odděluje revizi aplikace, příznak rozpracovaného
zdroje, verzi API a hashe souborů od nezávislé revize ModelBundle.
Výstup obsahuje jeden HTML vstup a kompilované prostředky. Publikace odmítá
rozpracovaný zdroj, neplatný inventář a soukromé soubory.

ModelBundle se načítá samostatně přes `/models/active.json`. Chybějící model
neblokuje ostatní routy. Geometrii a výrobní výstupy stále vlastní CAD pipeline;
aktualizace aplikace je necertifikuje znovu. Explicitní offline distribuce skládá
udržovaný viewer s vybraným modelem. Syntetické fixtures jsou jen pro testy.

`/keepsake/` zachovává návštěvnické odkazy DV10; `/keepsake/evening/` vede přes
router na veřejný Archiv. Owner URL `/keepsake/?k=<locator>` sama neuděluje
přístup. Fragmenty `#c=`, `#r=` a `#s=` zachytí kořenový inline skript před
načtením modulů a odstraní je z adresy; výměna vyžaduje výslovnou akci hosta.
Soukromé guest API používá vlastní cookie, CSRF a idempotentní mutace.
Veřejné GET přehledy používají `credentials: omit`. Fotografie se odesílá až
po souhlasu; GET, polling ani příchod na routu nespouští generování.
Přihlášený účet nenahrazuje guest capability ani firmware credential.

Nginx používá přesný seznam SPA stránek. API chyby, websockety, auth callbacky,
manifesty, downloady a chybějící prostředky nepřepisuje na HTML. Claim odkazy
nepřesměrovává. HTML a privátní odpovědi mají `no-store`; privátní guest stránky
navíc `noindex` a `no-referrer`. Fingerprintované `/assets/` a neměnné modelové
balíčky mají oddělenou cache. Aktivační reference modelu zůstává `no-store`.

CI projekt `site` sestavuje aplikaci a testuje viewer fixtures bez CAD toolchainu.
Samostatná gate `cad` ověřuje skutečné výrobní vstupy; neznámý mechanický soubor
zůstává konzervativně výrobním vstupem. Ruční `project=site` nespouští CAD;
`project=all` vybere obě nezávislé gate. Přímý ruční build `main` bez PR vstupu
zachovává kompatibilní název artefaktu `arcane-site`. Public workflow neobsahuje
produkční credentials ani produkční publisher.

Lokální kontroly:

```powershell
python scripts/ci/ui_boundary.py
python -m unittest discover -s kufr/web -p 'test_*.py'
python -m unittest discover -s kufr/web/deploy -p 'test_*.py'
npm run test:browser --prefix kufr/mobilni-aplikace
```

Samostatné staré guest stránky a projektový homepage již nemají build vstup.
Také recovery aplikace používá stejný kompilovaný Vue výstup a příkaz výše;
privátní guest routy nadále dostávají HTTP `noindex`, `no-store` a `no-referrer`.
Schema-1 publisher/readback zůstává jen pro již vydané neměnné archivy, nikoli
pro sestavení druhého UI. Browser testy samy neprokazují fyzický iPhone,
kvalitu OpenAI ani výrobní přijetí.

[Postup přípravy release, persistence a rollback](deploy/APPLICATION-RELEASES.md)
popisuje oddělené aplikace/modely, atomickou lokální aktivaci a hranice důkazů.
[CI/CD](../../docs/ci-cd.md) popisuje oddělení veřejných kontrol a privátního
nasazování. Produkční nginx, oprávnění a health je nutné ověřit při samostatně
autorizovaném rolloutu.
