# Stav implementace AI kufru

Poslední aktualizace: **29. 9. 2026**. Zdroj stavu: aktuální kód, hostitelské testy a lokální firmware build E1.1. Ostatní etapy, hardware, iPhone a CI zůstávají bez runtime akceptace. Hotový návrh není hotová implementace.

[Implementační plán](implementacni-plan.md) určuje architekturu a akceptaci. [AGENTS.md](../../AGENTS.md) určuje pracovní postup AI. Tento soubor je jediný průběžný záznam postupu; README ho pouze odkazují.

## Předání další AI

**Aktivní implementační krok:** žádný. E1.1 bylo 29. 9. 2026 převzato a implementováno; rozsah a kontroly jsou v deníku D1. E1 zůstává rozpracované kvůli neověřenému nezávislému vypnutí při zamrzlé smyčce a navazujícím softwarovým scénářům.

**Další konkrétní krok: E1.2 – timeout řízení a ztráta ručního řízení v hostitelském modelu.**

1. Navázat na `SafetyController`, `SelectionSequence` a fake port z E1.1. Podle kapitol 8 a 11 plánu zmodelovat ztrátu průchodu řídicí smyčkou, poruchu/Stop a ztrátu ručního lease jako nezávislé vstupy, bez síťové implementace a GPIO.
2. Hostitelsky ověřit přednost Stop a bezpečné ukončení aktivního motoru/serva při těchto vstupech; nepovažovat fake timeout za důkaz fyzického watchdogu. Rozhodnout podle všech softwarových kritérií kapitoly 12, zda lze E1 uzavřít a navázat E2.
3. Samostatná HW akceptace inhibit/watchdogu, resetu a fyzických limitů zůstává v E5 po vstupu B1.

Předpokládané dotčené cesty: `kufr/firmware/src/features/safety/`, `wand_selection/`, hostitelské testy a firmware README. Krok nevyžaduje fyzickou desku, cloudový klíč ani Apple certifikát. Toto je implementační předání, nikoli příkaz něco nahrát či publikovat.

Pokud uživatel omezí práci pouze na mobilní aplikaci, neoznačuj závislosti E2 za splněné. Lze připravit samostatnou UI část E3 proti výslovně označenému fake transportu, ale E3 zůstane částečné do integrace se simulátorem/protokolem E2. Rozsah a mezeru zapiš sem.

## Jak se eviduje práce

- Stavy etap: **nezahájeno**, **rozpracováno**, **blokováno**, **hotovo**, **odloženo**. Poslední stav používat pro volitelné etapy nebo výslovné odložení, nikdy jako náhradu nesplněného kritéria.
- U rozpracované etapy zapisuj konkrétní dílčí kroky `E1.1`, `E1.2` atd. Přidej dokončený rozsah i zbytek; procenta bez měřitelného podkladu nepoužívej. Jedna malá změna může dokončit jen část etapy.
- `Hotovo` vyžaduje všechna kritéria kapitoly 12 plánu a odkazy na důkazy níže. Software/fake, build, integrace, iPhone a hardware jsou různé úrovně ověření. Chybějící požadované měření ponechá etapu rozpracovanou nebo blokovanou.
- Blokace musí uvést dotčené kritérium, potřebný vstup, kdo ho dodá a nezávislou práci, která může pokračovat. Při částečné blokaci nezastavuj celý projekt. Vyřešení blokace zapiš s důkazem, ne jen změnou barvy/stavu.
- Na začátku práce porovnej záznam s aktuálními soubory. Při rozporu oprav stav s vysvětlením; nevracej cizí změny podle starého záznamu. Při souběhu uvede každý pracovník svůj krok a vlastněné soubory, jeden integrátor zapíše společný výsledek.
- Před koncem pracovního kroku aktualizuj tabulku, aktuální předání a deník. Zaznamenej i neúspěšný test, rozpracovaný kód nebo neprovedenou kontrolu. Další AI musí poznat přesný bod navázání bez historie chatu.
- Ověření zapisuj jako: datum; krok; soubory/revize; pracovní adresář a přesný příkaz; výsledek; důležité omezení. Pro CI přidej URL běhu a commit, pro zařízení revizi PCB/iOS, profil a kalibraci. Commit uváděj jen pokud skutečně existuje; jinak uveď místní necommitované změny. Logy uváděj stručně a bez tajemství.
- Pokud změna zneplatní dřívější test, zapiš „ověření zastaralo“ a zopakuj relevantní část. Historický úspěch nedokazuje stav nového kódu.
- Architektonické změny patří do plánu, nikoli jen sem. Do deníku patří důvod, odkaz na změněnou část plánu, dopad na kontrakty/etapy a potřebné přetestování. Číslování etap zachovej; nové kroky přidávej, nepřečísluj historii.

## Výchozí stav podle zdrojů

| Část | Co skutečně existuje | Co zatím chybí |
| --- | --- | --- |
| Firmware | Čistá neblokující `SelectionSequence`, `SafetyController` s portem pro požadavky na bezpečný výstup, Stop/zotavení a sériové `select 1..6`, `stop`, `reset`, `status`, `help`. Hostitelské testy a lokální Arduino/PlatformIO build E1.1. | Nezávislý fyzický watchdog/inhibit, GPIO, síť, provisioning, I2S, dialog a skutečné ovládání. |
| Mobil | Statická obrazovka TypeScript/Vite, build script a lockfile. | Interaktivní ruční řízení, transport, simulátor, Capacitor, iOS/Swift projekt a testy. |
| Backend/kontrakty/simulátor | Návrh v plánu. | Implementační adresáře, autentizace, fake/AI adaptéry, schémata a integrační testy. |
| iOS CI | Majitelem popsaný existující postup Windows → macOS runner → podepsaná IPA. | Skutečný workflow v tomto checkoutu, ověřená identita/profil a běh/instalace. |
| Hardware | Dodaný obrázek pinů, návrh napájení a předběžné profily A/B. | Revize konkrétní desky, schéma bezpečnostní větve, měření a fyzická akceptace. |

Zdroje: [firmware](../firmware/README.md), [jeho konfigurace](../firmware/platformio.ini), [sekvence](../firmware/src/features/wand_selection/SelectionSequence.cpp), [hostitelský test](../firmware/tests/selection_sequence_test.cpp), [mobilní vstup](../mobilni-aplikace/src/main.ts), [package.json](../mobilni-aplikace/package.json). Tento přehled aktualizuj, až se skutečný stav změní.

## Etapy

Přesné závislosti a kritéria dokončení jsou v kapitole 12 plánu. Následující tabulka je stav, nikoli jejich zjednodušená náhrada.

| Etapa | Stav | Dokončený rozsah / důkaz | Zbytek nebo podmínka dalšího kroku |
| --- | --- | --- | --- |
| E0 – provozní předpoklady | rozpracováno | Architektonické výchozí předpoklady v plánu; žádná měření. | Vstupy B1–B5 níže; software E1 může začít. |
| E1 – bezpečné jádro | rozpracováno | E1.1: Stop v každé fázi, safety port, 250/500/5000ms limity, explicitní zotavení; hostitelské testy a firmware build prošly (D1). | E1.2: model timeoutu řízení a výpadku lease/Stop; fyzická reakce watchdogu patří E5 a nebyla měřena. |
| E2 – smlouvy a simulátor | nezahájeno | Pouze návrh kontraktů. | Navázat na E1; schémata, simulátor a negativní scénáře. |
| E3 – ruční mobil bez HW | nezahájeno | Statická stránka není ruční řízení. | E2; UI, potvrzení/Stop a integrace. |
| E3a – iOS CI základ | nezahájeno | Jen majitelem popsaný postup. | E3; B2/B3 pro podpis a skutečnou instalaci. |
| E4 – runtime a přímá síť | nezahájeno | Arduino prototyp. | E2; pro nahrání B1. |
| E5 – jedna bezpečná přihrádka | nezahájeno | Bez ovládání a měření. | E0/E1/E4; B1, fyzická akceptace. |
| E6 – šest přihrádek | nezahájeno | Bez fyzického ověření. | E5; cykly, proudy, dostupnost/reload. |
| E7 – backend bez skutečné AI | nezahájeno | Bez projektu. | E2; lokální fake bez placeného účtu. |
| E8 – skutečný textový dialog | nezahájeno | Bez adaptéru a evaluace. | E2/E7, na desce E4; B4/B5 před placenou akceptací. |
| E9 – samostatný hlas kufru | nezahájeno | Bez audio cesty. | E4/E5/E8; hardware a hlasová akceptace. |
| E10 – dokončení mobilní zálohy | nezahájeno | Bez native transportu. | E3/E3a/E4/E8; audio po E9; skutečný iPhone. |
| E11 – provozní balení | nezahájeno | Bez provozního nasazení a balíčku. | E6/E8/E9, záloha E10; instalační a provozní zkouška. |
| E12 – obraz telefonu | odloženo | Volitelné rozšíření po základní verzi. | E8/E11 a potvrzený přínos. |
| E13 – OV5640 + SD profil B | odloženo | Jen předběžný návrh B. | E0 pro B/E9/E12; nové měření pinů a zátěže. |

## Chybějící vstupy a blokace

Jde o budoucí podmínky konkrétních etap, nikoli důvod neprovést E1/E2. Další otázky a výchozí rozhodnutí zůstávají v kapitole 13 plánu.

| ID | Chybějící vstup a kdo ho dodá | Co blokuje | Co může pokračovat |
| --- | --- | --- | --- |
| B1 | Majitel/stavitel: revize PCB, měření USB/LED/paměti, mechanika a schéma/měření watchdogu a napájení. | Nahrání do neověřené desky, E5/E6 a HW akceptaci E9/E13. | Hostitelské testy, protokol, simulátor, SW build. |
| B2 | Majitel: skutečný soubor již používaného iOS workflow, Team/Bundle ID, distribuční režim a platná identita/profil do secrets. | Převzetí a ověření podpisové části E3a, vydání E11. | Webový vývoj, příprava nativního projektu a kontrol bez podpisových secrets. |
| B3 | Majitel: model/verze iOS, registrovaný iPhone pro Ad Hoc a vybraný instalační klient/hosting IPA. | Fyzickou instalaci E3a, LAN/Keychain akceptaci E10 a distribuci E11. | UI/protokolové testy; CI simulator test není náhrada iPhonu. |
| B4 | Správce/majitel: účet, AI klíč mimo repo, hosting, region/retence a schválené provozní limity. | Skutečná placená volání a provozní nasazení. | E7 a fake integrační/eval scénáře; připravit konfiguraci bez hodnot tajemství. |
| B5 | Majitel: finální katalog a publikum, rozměry, router a prostředí akce. | Obsahovou/provozní akceptaci E8/E11 a finální mechaniku. | Neutrální testovací katalog a laboratorní/simulované scénáře. |

## Deník změn a ověření

### D1 – 29. 9. 2026 – E1.1 Stop a bezpečnostní požadavky bez desky

- Převzetí: po čtení kapitol 5, 8 a 12 plánu, firmware README a živého kódu byl převzat E1.1. Původní hostitelský test prošel přes Visual Studio Build Tools; `g++` a `pio` nebyly v PATH.
- Rozsah: nové `firmware/src/features/safety/SafetyPort.h` a `SafetyController.h/.cpp`; upravené `wand_selection/SelectionSequence.h/.cpp`, `SerialSelectionPort.h/.cpp`, `src/main.cpp`, `tests/selection_sequence_test.cpp` a firmware README. Sekvence po startu i Stop vyžaduje výslovné zotavení; Stop hned volá port pro vypnutí, nepokouší se znovu zamknout západku. Safety controller odmítá souběh motoru a západky; motor je provozně 150 ms, pozdní průchod od 250 ms, západka od 500 ms a sekvence od 5 s vyvolají Stop. Sériový port pouze vypisuje požadavky, žádné GPIO.
- Hostitelské ověření z `C:\Users\vojtech.petrik\AppData\Local\Temp\kufr-e1-validation`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /EHsc /W4 /WX /std:c++14 C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\tests\selection_sequence_test.cpp /Fe:selection_sequence_test.exe && selection_sequence_test.exe'` — průchod bez chyb. Testy pokrývají původní pořadí/validaci, Stop ve všech fázích, opakovaný i externí Stop, zotavení, pozdní průchod, meze 250/500/5000 ms a přetečení čítače včetně aktivního motoru/západky. Celý `src/main.cpp` a sériový adaptér navíc prošly kompilací s dočasnou hlavičkou Arduino mimo repo; tento stub není náhradou skutečného PlatformIO buildu.
- Firmware build z `kufr/firmware`: `& (Join-Path $env:TEMP 'kufr-pio-venv\Scripts\python.exe') -m platformio run` — **SUCCESS**, PlatformIO Core 6.2.0, platforma `espressif32@7.1.3`, Arduino; 18 660 B RAM a 270 217 B flash. CLI bylo nainstalováno do dočasného venv mimo repo; běžný příkaz `pio run` bez tohoto prostředí v PATH nefunguje. `.pio/` je ignorovaný výstup.
- Revize: žádný commit/push; checkout nemá HEAD a všechny původní soubory jsou untracked. Neproběhlo nahrání do desky, měření watchdogu/inhibit, ověření serva ani iPhone/CI. Softwarový Stop vydává požadavek; nepředstavuje důkaz fyzického vypnutí při zatuhlé smyčce.
- Dodatečné doručení 29. 9. 2026: po potvrzení prvního importu celého projektu byl E1.1 zahrnut do commitu `b21725a23be88b89f723fa01b59496058cb89abd` a pushnut na `origin/main` repozitáře `Blato58/DildoVander`. Pravidlo automatického commitu/push po každém dalším dokončeném slice, ověřovací skript a připnutí Node 24.21.0 byly pushnuty v commitu `80fecdebbad9e0b5d136d520e954da66caeedcee`. Trvalý PlatformIO Core 6.2.0 je v uživatelském prostředí; `pio run` znovu prošel, stejně jako `npm ci` a `npm run build` mobilního prototypu pod Node 24.21.0. Tyto buildy nedokazují HW ani iOS akceptaci.
- Předání: E1.2 podle horní části souboru. B1 blokuje jen fyzickou část E5, nikoli další hostitelské testy.

### D0 – 29. 9. 2026 – příprava dokumentace a předání

- Rozsah: sjednocení AGENTS/README a technických podkladů s plánem, založení tohoto stavového souboru. Žádná implementace E1–E13, žádná změna zdrojového kódu ani konfigurace.
- Evidence stavu: čtení firmware zdrojů/testu/PlatformIO, mobilního vstupu/package/lockfile a kontrola nepřítomnosti `.github/workflows/`. Nezávislý read-only průchod potvrdil absenci síťové, AI a nativní mobilní implementace.
- Dokumentační kontrola z kořene repozitáře: inventura `rg --files --hidden -g '!.git' -g '!**/node_modules/**' -g '!**/.pio/**' -g '!**/dist/**'`, čtení Markdown přes `Get-Content -Raw -Encoding UTF8`, následná kontrola odkazů/obsahu v paměti. Výsledek: 61 místních odkazů existuje, všech 15 ID etap se shoduje s plánem, 4 JSON ukázky se parsují, code fences jsou vyvážené. Porovnání `Get-FileHash -Algorithm SHA256` proti inventuře před úpravou: 11 původních Markdown změněno, 1 Markdown přidán, žádný kód/konfigurace smazán nebo změněn. `git diff --check` bez výstupu; protože jsou soubory untracked, rozhodující je kontrola obsahu a hashů, nikoli prázdný diff. Žádná z těchto kontrol není runtime akceptací.
- Nespouštěno: `pio run`, C++ test, `npm ci`, `npm run build`, CI, placená AI, instalace na iPhone a hardware. Důvod: pouze dokumentační zadání.
- Revize: místní necommitované soubory; nevznikl commit/push ani publikace.
- Předání: E1.1 podle horní části souboru. E0 zůstává částečné, hardware ani podpis nejsou podmínkou zahájení E1.

Pro další zápis použij nadpis s ID kroku, datem a konkrétním výsledkem. Pod něj napiš rozsah/soubory, přesné provedené kontroly a výsledky, zbývající akceptaci/blokace a další krok. Dlouhé raw logy, osobní údaje, certifikáty a klíče sem nepatří.
