# Stav implementace AI kufru

## F2 — vložka do skutečného kufru, 10. 10. 2026

P1 review PR #170 na `44463c2` je zpracované: doplněný absolutní limit celé
prezentace 15 s a omezení na jediný LED úsek prošly hostitelskými regresemi,
nezávislou kontrolou, cílovým buildem i úplnou regenerací. Nezávislý audit
nového archivu rovněž prošel; zbývá Git předání a CI posledního commitu.
Třetí nález k pasivnímu USB
zakončení je věcně odmítnutý: aktuální zadání výslovně povoluje běžné
vodiče a pasivní konektory k doplnění; nejde o novou funkční elektroniku
ani tvrzení o nákupu. Přesný podklad je ve specifikaci F2.

F1 nový tištěný korpus je nahrazen vložkou do vlastněného dřevěného kufru
bez nových otvorů. Použity jsou potvrzené obálky 450 × 310 × 200 mm,
spodní vnitřek 431,8 × 266,7 × 114,3 mm a bezpečný kvádr 400 × 240 × 170 mm.
[Specifikace](../mechanika/final/SPECIFIKACE.md) a `parameters.json` řídí
šest modulů na rozteči 48 mm, zdvih 16 mm, čtyři panely, dvě Al lišty
a čtyři lepené Al kotvy. Banka, hůlky a textil nenesou konstrukci.
Majitel 10. 10. potvrdil délku hůlky do 200 mm a šířku hlavice 38 mm
včetně případného pouzdra; [záznam](../mechanika/rozmery-hulek.md) odděluje
tyto dvě hodnoty od dosud předpokládaných ostatních tvarových rozměrů.
Přeprava používá šest samostatných pásků a parkování odpojeného USB.

Změněny jsou zdroje `mechanika/final/` včetně kabeláže, skutečných spojů,
montážního postupu, vieweru a balení. 135 elektrických propojení a GPIO
zůstávají zachované; doplněné pasivní USB zakončení je výslovně v materiálu.
`effects.json` a generovaný `PresentationScore.h` sjednocují hlavní a krátkou
choreografii s `PhysicalPresentation`, světly a volitelnými místními tóny
existujícího I2S adaptéru. Stop neprovádí automatický návrat.

Provedeno: 86 Python testů, 20 testů vieweru, hostitelské testy prezentace
a fyzických efektů, `pio run -e esp32-s3-idf` PASS (RAM 91 660 B,
flash 1 746 865 B).
Geometrický výsledek je `PASS_CAD_NOT_PHYSICAL_ACCEPTANCE`: 32 STL typů /
47 výtisků, 69 CAD souborů, zpětné načtení STEP, uzavřené propojené STL,
tři celé sestavy, 102 poloh mechanismů, 36 poloh víka, 37 kroků vyjmutí
banky a 24 dráh nástroje. 135 vodičů prošlo 25 467 BREP porovnáními bez
kolizí či překročení bezpečné obálky; 352 stavů kontroluje vlastní
překrývání vodičů. Otisk zdrojů během auditu byl stabilní. Zaplnění tras
a počty materiálu v 15 krocích pro 589 dílů scény souhlasí.

Skutečný browser na `http://127.0.0.1:8915/viewer.html`: všech 15 kroků
vpřed i zpět a restart, všech šest dokončených výběrů, Shift na větvi
D2-2, servu S1-S a I2C s šesti vodiči, bez chyb konzole. Ověřen i malý
viewport 390 × 844. Nezávislé review vyřešilo kolize vedení, překrývání USB,
zachytávání kliknutí kartou, degenerované STL držáků a kontrolu otisků ZIPu.
Aktuální autorita je [verification.json](../mechanika/final/exports/verification.json).

Celá regenerace 10. 10. 2026 skončila úspěšně včetně osmi prohlédnutých
renderů, výkresů a samostatného ZIPu (43 526 083 B, SHA256
`ca6477fd2d0046ee5712b22668a6119f7e0f57df7a27c002398421b533820eae`).
Závěrečný browser načetl nové výstupy, zobrazil šest přepravních pásků
a odpojené USB v držáku; konzole bez varování a chyb.
Nezávislá kontrola ZIPu: CRC a přesný seznam 143 členů, 49 otisků zdrojů,
93 otisků exportního indexu, 92 ověřených exportů, 89 výstupů generování
a 69 CAD souborů PASS proti ZIPu i checkoutu. Po rozbalení mimo repozitář
prošlo 48 testů (19 Inspect, 10 efektů, 11 balení, 8 ověřovací brány).
V checkoutu platí všech 22
lokálních Markdown odkazů; v ZIPu pouze odkaz README na samotný ZIP
nemá cíl (archiv se do sebe záměrně nebalí). Referenční podklady jsou přiložené.

Implementační commit `381e06388f38cf8c18f4a9091aab65fdeb7799c4` byl
10. 10. 2026 odeslán přes `scripts/Push-CurrentBranch.ps1` na
`origin/codex/wooden-case-insert-f2`; skript ověřil shodu vzdáleného SHA.
Tento záznam ještě nepotvrzuje dokončení PR CI ani merge.

PR #170: CI pro `2852351` prošlo (hostitelské testy, ESP-IDF i diagnostické
varianty). Codex review našlo P2 v přepnutí režimu během přerušeného zdvihu.
Oprava zachovává všech šest úhlů a do explicitního resetu vynucuje servis;
nelze zobrazit přepravu ani obnovit klid jezdcem či montážním krokem.
Dvě nové regrese před opravou selhaly, po opravě všech 18 testů PASS;
nezávislá kontrola skutečných handlerů změn režimu, slideru, startu a resetu
je bez nálezů. Úplná regenerace po opravě a potvrzení rozměrů opět PASS;
všech osm nových renderů je byte po bytu shodných s prohlédnutými pohledy.
Reálný browser nejprve reprodukoval chybu (16 mm → Přeprava → 0 mm),
potom ověřil opravu (16 mm → Přeprava/Montáž → Servis, stále 16 mm,
jezdec zakázaný). Pouze explicitní servisní reset vrátí 0 mm a odblokuje
přepravu; konzole je bez varování/chyb. Aktuální balíček má otisk uvedený výše.
Oprava a potvrzené rozměry byly odeslány commitem
`2dd15fc11ef44e88fabee77463a91a37771ce2d5`; `Push-CurrentBranch.ps1`
potvrdil shodný vzdálený SHA. Původní P2 vlákno je po regresi a browser
kontrole vyřešené. CI commitu `64b55f4` rovněž prošlo. Jeho závěrečné
Codex review našlo další dvě P2, nyní opravené: ne-CAD exporty nebyly
svázané s ověřeným snapshotem a generátor předával firmwaru jen přípravu.
Generování nyní probíhá v prázdné složce, ověření sváže všechny výstupy
a balení odmítne pozdější změny. Firmware přebírá všechny fáze, dodržuje
kalibraci a obnoví dostupnost po 250 ms klidu s vypnutým PWM. Generátor
odmítá nepodporované kanály, tóny mimo 80–2000 Hz a více než 100 přípravných
kroků, které by překročily časovou rezervu řídicí smyčky.
Nezávislé review obou oprav je bez otevřeného nálezu; nové regrese
prošly. Celá následná regenerace skončila úspěšně. 32 STL, osm renderů
a výsledný viewer jsou byte po bytu shodné s prohlédnutou geometrií/UI.
Nový viewer se načetl v reálném browseru se 135 vodiči a bez chyb či
varování konzole. Aktuální ZIP výše nahrazuje předchozí balíček.
Obě opravy byly 10. 10. 2026 odeslány commitem
`b1da483fc97aea51881bea568d192e344349c78f`; `Push-CurrentBranch.ps1`
ověřil shodný vzdálený SHA. Po tomto push byly obě P2 vlákna vyřešené.
CI commitu `44463c2` prošlo. Jeho Codex review našlo dvě P1 v napájecím
limitu LED a celkovém času prezentace. Obě jsou opravené v generátoru,
výstupní vrstvě, řídicí smyčce a náhledu. Regrese ověřují všech 256 LED
masek, pevný deadline v každé fázi, návrat hodin zpět a nejpomalejší
kalibraci 3 000 ms s průchody po 100 ms. Při timeoutu zůstává záznam
nedokončené prezentace a vyžaduje se fyzická kontrola; nevznikne falešné
potvrzení návratu. Nezávislé review je bez nálezu. Následná úplná
regenerace prošla, všech 32 STL a osm renderů je byte po bytu shodných.
Nový browser znovu přehrál všech šest výběrů se zdvihem 16 mm a návratem
do klidu, bez varování/chyb konzole. Čeká se na Git předání a kontroly
posledního commitu; tento záznam sám merge nepotvrzuje.

Fyzická výroba, nalepení kotev, zkouška přepravy, kalibrace a nahrání
firmware neproběhly. Neuzavřené jsou zbývající tvarové rozměry a usazení
hůlek/pouzder, servopáka,
konektory, kamera/FPC, víko, ohyby kabelů, soudržnost dřeva/lepidla,
zatížený zdvih a chování serva po vypnutí PWM. Podrobnosti a přejímka:
[STAV F2](../mechanika/final/STAV.md), [MONTÁŽ](../mechanika/final/MONTÁŽ.md).
Další fyzický krok: měrka a jeden modul, suché vložení a elektrická
přejímka; až poté ostatní kusy. Historické výsledky F1 níže nejsou ověřením F2.

## Historie před F2


Poslední aktualizace: **9. 10. 2026**. Zdroj stavu: aktuální kód, hostitelské testy a lokální i CI ESP-IDF firmware build, kontrakty a simulátor E2, browser kontrola časové osy E2.5, Vite build, cílené testy adaptéru a E3a. Ad Hoc podpis a serverová publikace IPA/manifestu prošly CI a veřejným HTTPS testem; majitel potvrdil instalaci a spuštění na iPhonu 15 Pro (D70). D159 doložilo USB provisioning, iPhone BLE/domácí Wi-Fi, backend přihlášení po restartu a skutečnou OTA na0.1.1. Celá HW sestava, native LAN WSS/TLS a aktualizace IPA na telefonu zatím nemají úplnou runtime akceptaci. Hotový návrh není hotová implementace.

[Implementační plán](implementacni-plan.md) určuje architekturu a akceptaci. [AGENTS.md](../../AGENTS.md) určuje pracovní postup AI. Tento soubor je jediný průběžný záznam postupu; README ho pouze odkazují.

## Předání další AI

Aktuální mechanické předání je F2 v úvodu tohoto dokumentu; následuje
historie F1 a předchozích softwarových etap.

**9. 10. 2026 – F1 optimalizace kabeláže a rozložení ověřena v CAD.**
PD trigger, měnič a jistič jsou u banky vlevo; měnič mezi maticemi
zvýšeného stolku, jistič izolovaně pod ním. Stolky a boční clony mají
servisní průchody, nad měničem je větrání. Tři Degson mají sítě MAIN5,
DRIVE5 a GND, tabulku portů a izolované vodičové rozbočky mimo PCB.
135 vedení má oba konce označené, délkové rezervy a rozlišení původních
kabelů od prodloužení. Návod a pořadí montáže: [KABELAZ.md](../mechanika/final/KABELAZ.md).
Vznikly JSON/CSV kabelový plán a řezací seznam, SVG/PNG výkres a nový ZIP.
GPIO, firmware ani mechanismus zdvihu se tím nemění.

Změněny zdroje `final/layout.py`, `harness.py`, `test_harness.py`,
`build.py`, `verify.py`, `drawings.py`, `preview.py`, `inspect_data.py`,
`artifacts.py`, `Regenerate.ps1`, ověřený snapshot a montážní dokumentace;
potvrzené zapojení odkazuje tento montážní plán. Nezávislé review je bez
otevřeného nálezu; nalezené průniky kabelových tras, matic a rezervních
dílů byly opraveny před úspěšným předáním.

Provedeno: `Regenerate.ps1 -Python ..\.venv\Scripts\python.exe` PASS,
56 Python testů a 5 testů prohlížeče; 29 STL typů / 44 výtisků, zpětné
načtení STEP, tři celé sestavy, 81 vzorků mechanismu se sousedy, 36 poloh
víka a 37 kroků vyjmutí banky. Smyčková obálka modulu 2 má 81 vzorků;
všech šest obálek je ověřeno ve statické sestavě. Ad hoc BREP audit
266 úseků střednic o tloušťce 0,1 mm včetně rezervních dílů: 0 průniků.
Shoda zdrojových SHA-256, ZIP CRC/inventory, 135 CSV řádků se štítky
a 37 místních odkazů PASS. ZIP SHA-256:
`5f32903ee4f9871feba184fbd4088df71b234caa556579cdbbb112f93eadab8a`.
Browser `http://127.0.0.1:8914/viewer.html`, 611 × 764: Servis,
Inspect D1/MAIN5, kondenzátor 3/PCA V+ a měnič 46×31×19 fungují;
bez vodorovného přetékání a varování/chyb konzole. Výkres a servisní
render vizuálně zkontrolovány. Autoritou geometrie zůstává
`final/exports/verification.json` se shodnými zdrojovými hashi.

Fyzicky neověřeno: skutečné ohyby/zaplnění svazků, konektory, dostupná
délka vodičů, izolace/polarita, teploty/proudy a zatížený mechanismus.
USB napájecí zakončení ESP zůstává k ověření existujícího adaptéru;
plán žádný nekoupený konektor nepředstírá. Další krok: suchá montáž
levé napájecí sestavy a jednoho osazeného modulu, elektrická přejímka
a pohyb včetně opačných poloh sousedních lůžek. HW kritéria zůstávají
otevřená. Implementační commit `1bd8fe1` byl skutečně pushnut a jeho
remote SHA ověřeno pomocí `scripts/Push-CurrentBranch.ps1`.
[PR #169](https://github.com/Blato58/DildoVander/pull/169) obsahuje tento
slice; dokončení merge podléhá poslední kontrole reviews/inline komentářů
a stavu CI. Vygenerované exporty jsou lokální, reprodukovatelné a v ZIPu.
GitHub Codex review následně zachytil P2 rozpor v `efekty-a-napajeni.md`:
starší pokyn požadoval JST-SM u LED a dodatečný Grove adaptér. Dokument
je sjednocen s F1: LED mají označené pájené výkonové/datové spoje,
servis po odpojení zdroje a ověření vybití vyžaduje odpájení vlastní
větve; U059 zachovává původní Grove na straně modulu. Ověření opravy:
`python -m unittest -q test_harness` 28 PASS, regresní kontrola LED/Grove
montážního kontraktu a nových místních odkazů PASS. Zdrojové hashe CAD
a tehdejší ZIP se neměnily; jejich kontrola znovu PASS. Oprava byla pouze
v tomto provozním podkladu a záznamu stavu, žádný aplikační build neběžel.

Další dva P2 nálezy PR review jsou opravené: RGB-2 až RGB-6 mají 128 mm
pohybové rezervy pro dva nezávislé konce, RGB-1 a jednotlivé větve 64 mm.
Svorkové vazby v náhledu se rozvinou přes pájené rozbočky až k reálným
součástkám, včetně vnořené logické GND a označeného USB aliasu ESP.
Reprodukční testy nejprve selhaly; po opravě 33 testů kabeláže PASS.
Nezávislé review bez nálezu. Celý `Regenerate.ps1` znovu PASS; údaje
a hash ZIP výše patří této nové regeneraci. Browser po reloadu ověřil
D1/LED, audio a PTT, D2/všech šest U059 a D3/vnořenou GND; konzole
bez varování/chyb. SHA-256 zdrojů, CRC/inventory ZIP a CSV znovu PASS.
Nález požadující regeneraci po samotném `35cab87` měl chybný předpoklad:
commit změnil pouze `efekty-a-napajeni.md` a tento stav; Git blob
`reference/inspect-sources.json` zůstal shodný. Snapshot obsahuje pouze
rozměry a potvrzené zapojení, nikoli provozní dokument efektů. Nynější
změna `harness.py`/testů/montážního návodu už zdrojový fingerprint mění,
proto se celý výrobní balíček skutečně regeneroval a ověřil.

**9. 10. 2026 – F1 prohlížeč: Inspect, barvy a kabelové vazby.**
`kufr/mechanika/final/exports/viewer.html` byl regenerován z aktuálních
zdrojů. Shift zvýrazní skutečnou viditelnou geometrii a zobrazí kartu;
Shift nad prázdnou plochou ukáže názvy dílů zapnutých vrstev v záběru.
Klik připne kartu; Shift pak zachová pouze její propojení a klik do
prázdna výběr zruší. Tlačítko Inspect a seznam objektů umožňují další
ovládání. Barva se ukládá pro ID objektu do místního úložiště prohlížeče.
219 objektů má popisy, CAD obálky a rozměrovou evidenci; 92 logických
kabelových vazeb obsahuje GPIO, kanály, napětí a poznámky podle
potvrzeného zapojení. Rezervní díly jsou z náhledu odstraněné. Konkrétní ID svorek ani
kondenzátorů nemají doložené přiřazení sítě; trasy kabelů se neměří.

Změněné zdroje jsou v `final/`: šablona, nový `viewer.js`, nový modul
`inspect_data.py` a jeho testy, generátor náhledu, fingerprinty a
regenerace. Oba závazné Markdown podklady mají přesnou ověřenou kopii
`reference/inspect-sources.json`, takže zdrojový ZIP lze regenerovat
bez původního repozitáře. V repozitáři se kontroluje její aktuálnost.
README a místní STAV popisují ovládání a hranice tohoto slice.

Provedeno: `node --check viewer.js`, 20/20 Python testů metadat/balení,
generace samotného prohlížeče a skutečná generace ve samostatné kopii
zdrojů bez externích Markdownů. Browser na localhost v 611 × 764 a
1280 × 900 ověřil výběr PCA, Shift, vazby, seznam, zdvih 16 mm, skrývání
vrstev a obnovení barvy po zavření a znovuotevření stránky; konzole
bez chyb. Screenshot je lokální `exports/inspect-preview.png`.
Přímé otevření `file://` bezpečnostní politika browser nástroje odmítla;
HTML má geometrii, data i skript vložené uvnitř a neváže síťové zdroje.
Nezávislé review nemá zbývající nález. Celý CAD export/výrobní ZIP,
geometrické zkoušky, fyzické měření, firmware, iPhone ani nasazení se
v tomto prohlížečovém slice znovu neprováděly. Další krok zůstává
fyzická akceptace jednoho F1 mechanismu podle místního STAV.

Doručení: implementační commit `5c6a19faa34dfb728c4c6d2430dbb1c03a61e88e`,
`Push-CurrentBranch.ps1` potvrdil shodný vzdálený HEAD na `codex/f1-inspect`.
Předání a vzdálené kontroly jsou v
[PR168](https://github.com/Blato58/DildoVander/pull/168).
Existující nevázané `.codex/` soubory nebyly přidány ani upraveny;
při push byla pouze procesně potlačena jejich kontrola jako untracked.

PR review našlo P2: pohyb kurzoru mohl přepsat klikem připnutou kartu
a změnit objekt, jehož barva se upravuje. Oprava dává připnutému dílu
trvalé vlastnictví karty do zrušení či dalšího kliku, odděluje zvýraznění
pod kurzorem a zachovává všechny názvy nad prázdnou plochou. Prošly
2/2 Node regresní testy skutečných event handlerů s testovacím GPU,
nová generace HTML a skutečný browser přechod od PCA do karty a editace
barvy. Nezávislé review opravy nemá nález. Nový `test_viewer.cjs` patří
do fingerprintů zdrojů; spouští se příkazem `node --test test_viewer.cjs`.

Navazující PR review řeší zakrytí souběžných elektrických vazeb: každý
spoj má nyní samostatnou křivku, i pokud mají oba díly v daném pohledu
shodný střed. Prošly 3/3 Node testy včetně dvou- a čtyřvazbových svazků.
Standardní `Regenerate.ps1` spouští `node --check viewer.js` i Node
regrese před Python/CAD. Skutečný skript ve fixture se správným JS
pokračoval; chybná syntaxe a selhávající Node test jej zastavily před
navazujícími kroky. README uvádí Node.js v PATH pro úplnou regeneraci.
Tato kontrola testovací fixture nepředstírá novou validaci výrobního ZIPu.
Browser finálního HTML potvrdil mezi ESP32 a PCA čtyři odlišné SVG
křivky ve třech barvách a konzoli bez chyb. Nezávislé review změn prošlo.

Další P2 review opravilo opakované drahé rozmísťování všech názvů:
layout se ukládá podle pohledu, vrstev, animací a rozměrů panelu; první
rozmístění používá volné úseky mřížky. Prošly 4/4 JS regrese včetně
232 objektů, přepočtu při změnách a zachování DOM uzlů při pohybu myši.
Browser 1280 × 900 zobrazil 232 názvů bez vzájemných průniků; v jednom
stejném automatizovaném pohybu klesl kumulativní JS čas z 3869 na 71 ms,
první rozmístění z 320 na 11 ms (CDP Performance, lokální měření).
Nezávislé review této opravy nemá nález. Majitel následně upřesnil,
že připnutý objekt při Shiftu potlačuje všechny názvy a klik do prázdna
jej zruší; test ověřuje zrušení i navazující zobrazení všech názvů.
Následné upřesnění: pin nezapíná trvalý Inspect. Puštění Shiftu jej vypne,
připnutá karta a vazby zůstanou a tažením lze měnit úhel. Prázdný klik
bez Shiftu pin zruší, drag jej zachová. Aktuálně prošlo 5/5 Node regresí.
Regenerované HTML tyto přechody ověřilo i v browseru: Shift-klik na PCA,
puštění Shiftu, rotace s pinem a prázdný klik se Shiftem i bez něj prošly.
Konzole zůstala bez chyb, lokální screenshot byl obnoven.
Poslední požadavek odstranil všech 13 rezervních dílů (`spare_*`) přímo
z payloadu náhledu, včetně možnosti jejich výběru v Inspectu. Aktuální
prohlížeč má 219 objektů a 92 vazeb; ostatní CAD exporty se tím nemění.
Generované HTML i browser potvrdily 219 objektů, žádná rezervní ID a
92 vazeb bez chybějících konců. P2 review dále zavedlo explicitně
ověřený hash zapojení v `inspect_data.py`: změněný dokument ani validní
změněný snapshot nesmějí bez kontroly mapy vazeb obnovit provenance či
generovat. README popisuje aktualizaci mapy/hash. Prošlo 23/23 Python
testů metadat/balení, včetně změn zdroje a vybaleného balíčku.

**9. 10. 2026 – F1 a úklid předchozího CAD; zdroje ověřené a pushnuté.**
Jediným aktivním návrhem je [F1](../mechanika/final/README.md), vytvořený
od základu v commitu `14ff82a`: nový tištěný korpus a prázdné víko,
šest servem ovládaných lůžek se zdvihem 16 mm, skryté osazení, čtyři
servisní kryty a vyjímatelná powerbanka. 29 typů STL / 44 výtisků včetně
měrky pro podložku 220 × 220 mm; pět STEP sestav, náhledy a kusovníky.
Při transportu jsou hůlky a banka vyjmuté, zbývající osazení zajištěné.

Majitel výslovně požádal o úklid, push a merge; předchozí zákaz push tím
nahradil. Odstraněny verzované starší generátory, jejich testy a montážní
návody. Rozměrové podklady, závazné zapojení, šest fotografií a F1 zůstávají.
Starší lokální exporty a cache zůstaly na disku: automatická kontrola odmítla
jejich odstranění jako „blocked by policy“. Do commitu ani push nepatří.
Záznamy dvou předchozích CAD variant jsou dostupné v historii Gitu.

Po úklidu opakované CAD ověření prošlo: 29 STL/STEP, tři sestavy,
81 vzorků mechanismu včetně sousedních lůžek, 36 poloh víka a 37 kroků
vyjmutí banky bez detekovaných kolizí. Všech 104 místních odkazů v devíti
dokumentech existuje; šest testů výběru CI projektů a 55 Node testů prošlo.
Širší Python sada má na Windows 16/17: jediný neúspěch je existující kontrola
Unix oprávnění 0755 při extrakci souboru. Úplná sada následně prošla na Linuxu
v [CI 37957861517](https://github.com/Blato58/DildoVander/actions/runs/37957861517)
včetně Node testů, actionlint a povinného CI souhrnu. Aplikační buildy byly
správně vynechané. Celý příkaz `final/Regenerate.ps1` dokončil také rendery,
výkresy a ZIP CRC; report/manifest odpovídají zdrojovým SHA-256. Regenerovaný
HTML prohlížeč je bajtově shodný s již ověřeným browser výsledkem.

Cleanup commit `b06ea867bb96d691deeeae9eaede5c3c94ff364c` byl odeslán přes
`scripts/Push-CurrentBranch.ps1`; read-back ověřil shodnou vzdálenou SHA.
Předání a výsledek merge/review vede [PR167](https://github.com/Blato58/DildoVander/pull/167).
Automatické review dokončené 9. 10. v 16:21 UTC odhalilo jeden P2 nález:
ZIP mohl obsahovat staré výstupy. Balení nyní používá výslovný seznam
aktuálních souborů a přesný inventář pěti sestav. Čtyři regresní testy
včetně skutečného ZIP se záměrně ponechanými starými soubory prošly.
Geometrie se opravou nemění; plná regenerace po opravě byla spuštěna.
Před merge znovu načíst připomínky a ověřit CI posledního push.
Tisk, osazení, moment serva, tření brzdy, proudy/teploty, akustika, FPC
kamery a skutečné přenášení zůstávají neověřené. [Montážní návod](../mechanika/final/MONTÁŽ.md)
vyžaduje nejprve měrku a jeden zatížený modul. E5/E6/B1/E13 se neuzavírají.
Elektrické zapojení, firmware, aplikace, nasazení ani fyzický hardware se
v tomto mechanickém předání nemění. Novější softwarové záznamy níže zůstávají.

**D166 – aktuální předání 9. 10. 2026: automatická IPv4 ze Security1 BLE.**
Majitel potvrdil hostname timeout, ale ruční `192.168.0.59` vedla k servisnímu
stavu po validním snapshotu: tento UI důkaz potvrzuje pinned/authenticated WSS
přes IP. Konkrétní příčina DNS/multicast na iPhonu není doložená. Volitelné n.ip je nyní implementované s IP generation/HasIp recheck a 512B
fallbackem, který vynechá pouze optional IP. Nativní session/ID hint má 60s
platnost, odmítá stale update/disconnect a změnu ID, staré provisioning ID stále
umožní Wi-Fi provisioning. Ruční adresa má přednost; pin/token a hardwarové gates
zůstávají. Keychain route write je stále pouze v onOpen po připnutém auth WSS.
Discovery zachová Bonjour failed a zvlášť oznámí dostupnost BLE adresy.
Explicitní Control connect nejprve obnoví status živé BLE session i při skrytém
panelu a následně znovu načte savedDevice; odpadá 60s stale-host timing gap.
Stejný device ID zachová connection instance a unresolved-command gate. Lifecycle
číslo zruší starý tap při background/pagehide, změně identity/adresy a disconnect/
forget; návrat do foreground starý async connect neobnoví. Dva followup review P2
nálezy byly opraveny a regresní testy provádějí skutečný main tap handler s fake
native adaptéry na obou async hranicích. GitHub review následně odhalilo P2
silent skip při jiné pending BLE akci: refresh nyní vrací úspěch/neúspěch,
pending/skipped/failed/stale refresh blokuje WSS tap. Plně odpojené BLE stále
povolí uloženou/ruční LAN route. Dvě regrese pokrývají pending connection/action
a main handler abort bez čtení staré adresy. Další GitHub P2 odhalilo BLE loss
mezi savedDevice a async socket bridge: veřejný host/deviceId snapshot se nyní
kopíruje při vytvoření socketu před listener await a cestuje s pokusem. Native
connect odmítá partial/invalid/mismatched target, stále znovu čte pin/token z
Keychain a route zapisuje pouze po auth onOpen. Regrese ověřuje listener delay,
mutaci původní route a native snapshot po BLE end. GitHub P2 stale BLE state
po ztrátě session ve skrytém panelu je opravené: failed refresh pro aktuální
generaci vymaže logical connected/lastStatus a polling; další explicitní tap
může použít uloženou/ruční route bez opakování mrtvé BLE session. Regrese ověřuje
první odmítnutý refresh, druhý uložený-route tap a žádné obnovené polling requesty.

Ověřeno: dvě MSVC station suites (worker /std:c++14 /W4 /WX), pět parent host
suites pairing/TLS/BLE/discovery/WSS, 50 mobile tests, npm run build a
pio run -e esp32-s3-idf -s, vše exit0. Native helper/status regressions jsou
připravené pro existující macOS workflow; Windows je nespustil. Nezávislé native
review odhalilo a následně ověřilo opravený legacy ID regression; kombinované
firmware/native nezávislé review prošlo bez zbývajících findings.
Finální source commit `e4d215cfe10b2d293c74cdf9e581efc974ae80a4` byl sloučen v
[PR165](https://github.com/Blato58/DildoVander/pull/165) jako
`b22f478ffcba03bf052b8073a9ed4fd1a53be47b`.
[CI 37945851773](https://github.com/Blato58/DildoVander/actions/runs/37945851773),
run number 167, prošlo včetně webu, host suites, main i diagnostických firmware
buildů. CI a main mají shodný tree `dc21879478bd6f691b67b3c8934e2d731f8e0f6f`.
Tři GitHub P2 vlákna jsou opravená, regresně ověřená a resolved. Reviews/inline/
threads byly znovu načtené po posledním push i těsně před merge; poslední Codex
review dokončené v 14:42:54 UTC nezanechalo nevyřešený relevantní nález.

[OTA publikace 37946520625](https://github.com/Blato58/DildoVander/actions/runs/37946520625)
prošla a zveřejnila development **0.1.168**, buildId
`a228b02f1a1881065470680c26081f8ca8884f6a`, 1745456 B, SHA-256
`a83069da0bf9af357bbf8613f85eedb1455e838ced225f5f7c7f08a55d9ddc31`.
Existující root verifier a nezávislé veřejné HTTPS ověření potvrdily ECDSA podpis,
hash a embedded verzi; metadata odpovídají CI image. Image/partition/N16R8
kontroly prošly. Application-only COM7 zápis 1745456 B do `0x10000` prošel
hash kontrolou a následoval vědomý hard RTS reset. NVS, otadata a partition
table zůstaly zachované. Normální RTS boot zachytil **0.1.168**, start control
loop, main configured **16384 B** / minimum remaining **12540 B** a start
provisionovaného local TLS serveru v **19279 ms**. Hardware wiring/kalibrace
zůstávají neověřené a pohyb disabled. PC active BLE scan našel `KUFR_4B6048`
s očekávaným service UUID `b97a7f10-70e8-4b98-9dcc-40d66395c821`.
[iOS podpis 37946514768](https://github.com/Blato58/DildoVander/actions/runs/37946514768),
run number 23, prošel všemi třemi native harnessy: pinned LAN transport
(SPKI/Keychain/WSS/captured route), native Debug backend a secured BLE status/
secret whitelist, Xcode archive a Ad Hoc export. Stažená podepsaná IPA má
ověřené Info.plist: Bundle ID `app.turquoise6409.green2444`, verze
**1.0 / build 1000.0.23**, 1881267 B, SHA-256
`399409f8692284a513643ab9dbeff33814e7b1caa72b2be03e6079284826a5b9`.
[Safari publikace 37947083377](https://github.com/Blato58/DildoVander/actions/runs/37947083377)
prošla všemi joby. Veřejné HTTPS bez TLS bypass potvrdilo
[instalační stránku](https://2.28.202.220/ios/) s **1000.0.23**, manifest
`/ios/releases/37947083377-1/manifest.plist` se správným Bundle ID/buildem
a veřejnou IPA 1881267 B se shodným SHA-256 vůči podepsanému artefaktu i checksumu.
Publikované verze D166 jsou **0.1.168 / 1000.0.23**. Historická bootovaná sestava
D165 byla **0.1.162 / 1000.0.22**.
Majitel na přesný dotaz k aktualizaci iPhonu na **1000.0.23**, zabezpečenému
BLE s uloženým párováním a připojení uloženého kufru bez psaní IP odpověděl
**„Connection worked“**. To potvrzuje automatické připojení D166 v této zkoušce;
nebyla hlášená další přesná UI zpráva, dlouhodobé WSS ani restartová akceptace.
Další konkrétní krok: fyzická kalibrace a dlouhodobý provoz; samostatně ověřit
Keychain po restartu/aktualizaci, DHCP změnu a negativní síťové scénáře,
cancel/background a nejisté povely. Dlouhodobý provoz, UIKit a fyzická
kalibrace celé sestavy zůstávají otevřené. Žádné hardware gates se neodblokovaly.
D165 níže je historická doložená dodávka.

**Historické D165 – 9. 10. 2026 – obnova opakovaného párování po Zapomenout párování.**
Majitel po úspěšném D164 připojení zapomněl párování a nové BLE párování vracelo
already_paired. Native forget maže pouze Keychain; firmware ponechá hash.
Explicitní `recover-local` s `confirm:true` přes existující Security1/PoP BLE
po nativním potvrzení nahradí pouze zdravý spárovaný token. Chybějící, poškozené
záznamy a připravené QR se touto cestou nenahrazují. Maintenance → admission →
inbox → credential zámky vyžadují idle owner, žádnou údržbu a žádný živý/pending
socket či event; starý token nemůže během výměny navázat handshake ani doběhnout
jako ruční povel. Hash nového tokenu je durable, starý je revokovaný i po reload.
Native recheck prázdného Keychain a non-overwriting insert zachovají jiný záznam.
Cancel, background, timeout a stale callback jsou epoch-guarded; mutace se
neopakují automaticky. Nový token/pin neprochází JS. Po ztrátě odpovědi lze
zabezpečené BLE připojení a explicitní obnovu zopakovat.

Sedm MSVC host suites (pairing, TLS, BLE, discovery, WSS server, inbox, admission)
prošlo; 43 mobilních testů a npm run build prošly. `pio run -e esp32-s3-idf -s`
prošel. Nezávislé firmware/native review a diff --check prošly. UIKit lifecycle
není v host harnessu; jednotlivé firmware gates mají host testy, kombinace
MaintenanceGate je source-reviewed. Regrese pokrývají reload/revokaci, RNG/hash/
write failure, opakovaný digest, přesné confirmation fields, busy/pending a
handshake/inbox souběh, native allowlist/types/extra fields/512B bounds.
Commit `50b6b0605279884be5c2ed969254e2a57f32411a` byl pushnutý repo helperem;
[PR163](https://github.com/Blato58/DildoVander/pull/163) sloučený jako
`f752c1e0009d25b6827657f789bd35ff943c74f4` po zeleném
[CI37936213640](https://github.com/Blato58/DildoVander/actions/runs/37936213640).
Codex review dokončené bez findings; po posledním push a těsně před merge znovu
načtené reviews, inline a threads, žádná nevyřešená vlákna.
Tree `0add227557e52d87cd25e77769d7871f1f81bda2` odpovídá main i CI obrazu.

[OTA37936948294](https://github.com/Blato58/DildoVander/actions/runs/37936948294)
publikovala development **0.1.162**, buildId
`a3426c091ce727418e8e7252d3f66b3087ddf5c3`, 1744688 B, SHA-256
`0345a302c0170ded0ba80e4d4f5a5ff0bba5a06a4e3457bc0768a4bb2eab682b`.
Veřejné HTTPS stažení nezávisle ověřilo ECDSA podpis, hash a embedded version.
N16R8 image check a porovnání skutečné partition table se soukromou D164 zálohou
prošly. V rámci schválené COM7 obnovy s vypnutými pohony esptool zapsal pouze
aplikaci na0x10000, ověřil hash; NVS/otadata/partition table/bootloader zůstaly.
Normal RTS boot potvrzuje **0.1.162**, main16384 B/minimum zbývajících12540 B,
control loop, BLE advertise a local TLS server v18.899 s. PC BLE scan znovu našel
**KUFR_4B6048** s očekávaným service UUID. USB boot není nová OTA instalace.

[Podpis37936943860](https://github.com/Blato58/DildoVander/actions/runs/37936943860)
prošel včetně všech tří native harnessů, Xcode archive a Ad Hoc exportu pro
**1.0 /1000.0.22**.
[Publikace37937381222](https://github.com/Blato58/DildoVander/actions/runs/37937381222)
prošla všemi třemi joby. [Safari instalace](https://2.28.202.220/ios/) nabízí
1000.0.22; Windows nezávisle ověřil stránku a manifest se zachovaným Bundle ID,
IPA1873296 B se SHA-256
`f5cb611018ea898b215701bda0982c76dcd0dff7d1738421e9d13299fb39ff3e`
proti veřejnému checksumu. Backend zůstává active/healthz ok, jeho aplikace
a data se neměnily. Native build nenahrazuje skutečnou obnovu na iPhonu.
Další krok: vyřešit iPhone LAN discovery/adresu, ověřit dlouhodobé WSS a
relaunch/aktualizaci Keychain i cancel/background; dokončené párování neopakovat.
D164 připojení před forget nenahrazuje tuto novou fyzickou akceptaci.
Majitel následně potvrdil **„I can pair again, but cant see him on network or connect to it“**.
Opakované párování tedy funguje; LAN discovery/připojení zůstává hlášenou chybou.
PC následně znovu resolve `_kufr._tcp` na stejný .local host/443 s protocol1
provedlo a TLS WS bez credentialu vrátil401. Pasivní20s UART (120 B) nezachytil
restart loop; tyto PC kontroly neověřují token/pin telefonu. Majitel následně
hlásil hostname connection_timeout („pripojeni ke kufru neodpovedelo vcas“),
ale po ručním zadání192.168.0.59 UI přešlo na „Kufr vyžaduje servisní kontrolu“.
Source tuto hlášku vydá až po validním snapshotu: přímé IP tedy podle tohoto
UI důkazu navázalo pinned/authenticated WSS. Hláška odráží hardware/calibration
unverified gate; pohony se neodblokovávají. Bonjour/DNS na iPhonu zůstává otevřené.
Další práce: bez psaní získat aktuální IP přes již ověřené BLE jako address hint,
pin/token zachovat a adresu persistovat až po ověřeném WSS. Žádný další reset NVS.

**D164 – 9. 10. 2026 – opravený startup a fyzická obnova COM7 na0.1.161.**
Majitel nedokáže ESP najít ani přes BLE a autorizoval debug přes COM7/PC Bluetooth.
Windows potvrzuje CH343 USB UART COM7 a funkční Intel Bluetooth adaptér.
Pasivní 115200 čtení bez resetu zachytilo opakovaný boot verze **0.1.157**,
`***ERROR*** A stack overflow in task main has been detected.` a soft reset.
Matching ELF z CI37923289582 má SHA-256
`99d9505e1c5ed3afeff6adf232f169c85afa490f0f34c8a8214f3862525f07e6`;
addr2line potvrzuje `vApplicationStackOverflowHook`, backtrace je poškozený.
K pádu dojde po hardware/calibration warning a před BLE/control loop startem.
Tím je doložen důvod chybějící BLE na desce; verze0.1.160 na ní neběží.

Zdroj má main stack3584 B. Constructor existující LocalTlsIdentity validuje
uložený certifikát/klíč přes PSA/mbedTLS na main ještě před workerem pro
první generování. Přesný crypto frame není z poškozeného backtrace určitelný.
Nezávislé review doporučilo main stack16384 B, zachovat canary a runtime
watermark; zvýšení spotřebuje dalších12800 B. sdkconfig.defaults přidává
hodnotu, compile-time guard odmítne staré malé generované config a log uvede
konfigurovanou velikost i minimum zbývajících bytů po startupu. Generovaný
config byl zálohovaný mimo repo a regenerovaný; effective config je16384.
TLS/pairing/PoP/Wi-Fi NVS, Keychain a hardware gates se nemění.
Lokální `pio run -d kufr/firmware -e esp32-s3-idf -s` prošel; generated
sdkconfig.h potvrzuje16384 B a canary1. `git diff --check` a nezávislé
review prošly. UART ROM probe potvrdil ESP32-S3 rev0.2, flash16MB a PSRAM8MB.
[PR161](https://github.com/Blato58/DildoVander/pull/161) je sloučený jako
`12efd65fa40d12cda2c6041e7590b10c600d772c`; zdrojový commit
`5286d6dc04b2e66cf364c4a09de73f469f49c3b0` byl pushnutý repo helperem.
[CI37931935316](https://github.com/Blato58/DildoVander/actions/runs/37931935316)
prošlo včetně host testů a všech firmware profilů. Codex review dokončené bez
findings; reviews a inline byly po posledním push a těsně před merge znovu
načtené. Tree `fda951953e72906ab5b1a5747094c44773459050` odpovídá main i obrazu.
[OTA37933188173](https://github.com/Blato58/DildoVander/actions/runs/37933188173)
publikovala development **0.1.161**, buildId
`56413430197b8becdcaa03c19658a2f24dbf5360`, 1743920 B, SHA-256
`119c799fc6b31b96be891f9b5356483714d6e8ae14eb09748604f8deb5871c52`.
Nezávislé veřejné HTTPS stažení ověřilo ECDSA podpis, hash a embedded version;
veřejný obraz se shoduje s obrazem skutečně nahraným přes USB.

Majitel výslovně schválil „Flash COM7; actuator power is off“.
Soukromá záloha partition/NVS/otadata má owner-only ACL mimo repo.
Před zápisem byl ověřen shodný partition layout a skutečná revize desky.
Esptool zapsal jen aplikaci na **0x10000**, ověřil hash a resetoval desku;
NVS, otadata, partition table a bootloader nebyly přepsané ani vymazané.
Normal boot s UART capture potvrzuje **0.1.161**, control loop, main stack
**16384 B / minimum zbývajících12556 B**, BLE advertising a místní TLS server
(start v18.918 s). Původní startup stack overflow se při tomto bootu neopakoval.
PC aktivní BLE scan našel **KUFR_4B6048** se service UUID očekávaným iOS.
Windows Bonjour objevil `_kufr._tcp`, host
`kufr-820259579315c0916bbcc62999aa243a.local.` na443 a protocol1.
LAN TLS WebSocket probe s extension offer bez credentialu vrátil očekávané
**401 Unauthorized**; tento diagnostický probe neověřoval pin certifikátu ani
nezakládal autentizované spojení. BLE scan neprovedl GATT pairing.

Backend zůstal active/healthz ok, aplikace backendu nebyla přenasazená.
Aktuální iOS zůstává **1.0 /1000.0.21** z D163; D164 ho znovu nepodepisovalo
ani nepublikovalo. Zachování NVS doloží dostupné BLE/uložená Wi-Fi/TLS;
Majitel následně odpověděl na výzvu k Síť kufru / Připojit uložený kufr
v Ovládání na iOS1000.0.21: **„yes I am connected“**. Nové připojení telefonu
je tím potvrzené; délka stabilního provozu ani Keychain po aktualizaci
z této odpovědi nevyplývají.
Další krok: ověřit dlouhodobé WSS, relaunch/aktualizaci aplikace a Keychain persistence.
HW/E10 a skutečné pohony zůstávají otevřené; publikace ani PC probe je neuzavírají.

**D163 – historické předání 9. 10. 2026 – oprava místního připojení po BLE párování.**
Majitel potvrdil scan a párování; na stejné Wi-Fi v Ovládání hlásí
„Spojení se kufrem bylo přerušeno“ a opakované připojování. Přesné instalované
verze nejsou doložené. Zdroj odmítal každou nabídku WebSocket extensions
před 101; podle RFC6455 §4.2.2 lze nabídku přijmout bez výběru extension.
ESP-IDF odpověď žádné extensions nevybírá. Odmítnutí nabídky bylo odstraněno,
přesná bearer autentizace, pin a limity rámců zůstávají. Nejde ještě o důkaz
příčiny na fyzickém telefonu; macOS peer zaznamená jen boolean přítomnosti nabídky.
Nativní transport vrací pouze allowlisted failureCode bez NSError/URL/tajností.
Mobil vypíše konkrétní důvod, zastaví trvalé chyby a nejvýše třikrát obnoví
ostatní spojení; explicitní připojení obnoví limit. Welcome samo limit
neresetuje, až pět sekund platných snapshotů. Nejisté povely se nereplayují.
Keychain/NVS se nemažou ani nepřepisují.
Lokální ověření: pět MSVC pairing/TLS/BLE/discovery/server suites prošlo,
43 mobilních testů a npm run build prošly; pio run -e esp32-s3-idf -s prošel.
Node peer syntax, bash script syntax a diff --check prošly. Nezávislé source
review vedlo k opravě pořadí typed close před send completion a k testu
healthy reset po spotřebovaném retry. GitHub P2 dočasné ztráty sítě a README
byly opravené; návrh změnit healthy reset byl zdůvodněně odmítnut: owner
posílá snapshot každou sekundu i při ručním lease. Reviews/inline byly po
posledním push a těsně před merge znovu načtené, všechna vlákna vyřešená.
Commity c30d3ba/a9a92e3 byly pushnuté repo helperem;
[PR159](https://github.com/Blato58/DildoVander/pull/159) je sloučený jako
`5f742b24364b9564b729628ef5cd620ae601fb90` po zeleném
[CI37928528451](https://github.com/Blato58/DildoVander/actions/runs/37928528451).
Git tree `3f84a4c2bee77d1841bd31c9060db1652c90285a` odpovídá main i artefaktu.

[OTA37929155208](https://github.com/Blato58/DildoVander/actions/runs/37929155208)
publikovala development **0.1.160**, buildId
`9d067195927db3774ff5a6f84c739f48bd8298de`, 1743728 B, SHA-256
`f02029d55ed02281492fee8a3f24e3bd7634b6ad254193ef3f20f554c52cb192`.
Veřejné HTTPS stažení nezávisle ověřilo podpis ECDSA, SHA-256 a embedded version.
[Podpis37929159117](https://github.com/Blato58/DildoVander/actions/runs/37929159117)
prošel včetně všech tří native harnessů, Xcode archive a Ad Hoc exportu.
Apple URLSession v syntetickém macOS peeru skutečně poslala WS extension offer
(čtyři `native-wss-extension-offered=true`), který původní ESP odmítal.
[Publikace37929535139](https://github.com/Blato58/DildoVander/actions/runs/37929535139)
prošla ve všech třech jobech. [Safari instalace](https://2.28.202.220/ios/)
aktuálně nabízí **1.0 /1000.0.21**; Bundle ID/Team zůstaly zachované.
Windows ověřil stránku, manifest a IPA1870043 B se SHA-256
`8dc624add9451524953476e54889b6d87886ee7078c32a9cf0d1e651aaba4a7d`
proti veřejnému checksumu. Backend zůstal active/healthz ok, jeho aplikace,
credentialy a data se neměnily. Nasazení navazuje na výslovné deploy.
Zbývá aktualizovat oba fyzické klienty a zopakovat Ovládání/WSS, restart ESP,
relaunch/aktualizaci aplikace a Keychain persistence. Publikace ani macOS
loopback tyto kontroly nenahrazují; HW/E10 zůstává otevřené.
**D162 – 9. 10. 2026 – D161 nasazený a workflow verze automatická.**
Automatické firmware verze jsou sloučené v [PR157](https://github.com/Blato58/DildoVander/pull/157),
commit `3bf81025a9a51e9c93da5ac99646242b897c7094`. [CI37923289582](https://github.com/Blato58/DildoVander/actions/runs/37923289582)
prošlo včetně Linux helper/provenance testů, host testů a všech firmware
diagnostických buildů. Codex review464af6d dokončené bez findings;
před merge znovu načtené reviews/inline, žádná nevyřešená vlákna.
Git tree `21215f06a8c999f056ba1e9c9176b254ddb79435` odpovídá main i artefaktu.
CI run_number156 přidělil firmware **0.1.157**; ruční bump0.1.2 byl
nahrazen požadovaným automatickým číslováním. Nový CI běh verzi zvýší,
rerun/publikace stejného artefaktu ji zachovají.

Root-owned resolver/verifier/publisher byly instalované z tohoto checkoutu
se shodnými SHA-256, root644/644/755. Předchozí helpery mají soukromou
zálohu v `/root/kufr-release-3bf8102-37923289582/backup`; privátní klíče nebyly
zobrazené, kopírované ani měněné (signer je běžně používá). Environment
a návštěvnická data nebyly čtené ani měněné. Backend zůstal active a
veřejný `/healthz` vrátil ok před i po publikaci; aplikace backendu nebyla
přenasazená, D161 její kód nemění.
[OTA publikace37923929435](https://github.com/Blato58/DildoVander/actions/runs/37923929435)
prošla: development0.1.157, buildId `cea012486b7c67f44984ea82ab5c038549920c7d`
(testovaný PR merge commit, stejný Git tree jako squash main),1743776 B,
SHA-256 `40c54e708214fa32721eb3998d6fc1683715a71d59e28b2c0916323f383476d8`.
Nezávislé veřejné HTTPS stažení ověřilo ECDSA podpis, SHA-256 a embedded
version; manifest je dostupný i z Windows.

[Web CI37923839902](https://github.com/Blato58/DildoVander/actions/runs/37923839902),
[Ad Hoc podpis37923948887](https://github.com/Blato58/DildoVander/actions/runs/37923948887)
a [Safari publikace37924314161](https://github.com/Blato58/DildoVander/actions/runs/37924314161)
prošly. Nativní macOS harnessy pro pinned LAN/Keychain, debug backend a BLE
status/secret whitelist prošly. Veřejná [instalace](https://2.28.202.220/ios/)
nabízí **1.0 /1000.0.20**; Bundle ID/Team ID se nemění. Windows klient
ověřil aktuální stránku, manifest, build a staženou IPA1866106 B se SHA-256
`a90a1671aafd6b7ac3e2e7d02fc5f10ccc589eff543ffe844fceab657362a294`.

Majitel po publikaci potvrdil scan a párování; přesné verze na telefonu/ESP nejsou doložené. Na stejné Wi-Fi v Ovládání selhává LAN spojení a opakuje připojování. Zbývá ověřit
pinned WSS, restart ESP, relaunch/aktualizaci aplikace a Keychain persistence.
Připojené ESP USB nebylo nalezené; nebyl proveden flash ani potvrzen nový
boot. Publikace není přijetí změny na iPhonu/ESP a HW/E10 se neuzavírá.
Nesouvisející lokální `.codex/environments/environment.toml` zůstal zachovaný.

**9. 10. 2026 – nasazení D161 (BLE/Wi-Fi pairing), převzato.**
Majitel výslovně požádal `deploy`. PR155 je sloučený jako `f0f10b6`;
finální CI37920004873 a Codex review07963d1 prošly, relevantní P1 je
opravený a vlákno uzavřené. Veřejný development OTA manifest při převzetí nabízel
0.1.1; updater přijímá pouze vyšší verzi. Majitel doplnil, že má workflow
verzi zvyšovat samo při každém běhu. D162 proto používá immutable policy
`0.1.1+ci`: baseline + autoritativní CI run_number, s 16bit carry a odmítnutím
overflow. Nový běh dostane vyšší verzi bez commitování verze; rerun/publikace
stejného artefaktu ji nemění. Root verifier stejnou verzi odvodí nezávisle
z GitHub API a immutable source a porovná ji s binárkou. CMake se při CI
vždy regeneruje; lokální nepublikovaný build má pouze baseline. Legacy
plain policies zůstávají kompatibilní. Instalace root-owned resolveru je
nutná před publikací. Partition layout, důvěra OTA, PoP, NVS ani Apple/Keychain identita
se nemění. Rozsah nasazení: podepsaná development OTA publikace a Ad Hoc
IPA na existující Safari stránku přes existující workflow. Backend kód
D161 nemění. Publikace nenahrazuje potvrzení instalace a nativního pairing
na iPhonu/ESP. Výsledné build/publish běhy a runtime mezery se doplní
po ověření; nyní nejde o prohlášení úspěšného nasazení.
Lokální D162 ověření: dva navazující ESP-IDF buildy ve stejném checkoutu
pro simulované CI run42/43 mají ověřený embedded version0.1.43/0.1.44;
mezi nimi byl odstraněn jen CMakeCache, ne celý compiler cache. Verifier/
publisher testy:33 prošlo,1 Linux-only skip; version/OTA tools:13 prošlo,
1 OpenSSL skip; Node provenance55/55. Python CI archive testy16 prošlo,
1 existující Unix mode test na Windows selhal (0o666 místo0o755); Linux CI
ověří skutečná oprávnění. Nezávislé review bez blockeru, diff --check prošel.

**9. 10. 2026 – rozměry doplněné majitelem a zrušení podmínek pro rozvržení.**
Všechny údaje jsou pohromadě v [přehledu rozměrů kufru a součástek](../mechanika/rozmery-nakoupenych-soucastek.md).
PTT má 14×14×35 mm, PCA9685 25×70×15 mm; pro U059 použít výrobce
32×24×8 mm. Servo páka má 35 mm v oboustranném provedení s upevněním a osou
uprostřed nebo 20 mm v jednostranném provedení s upevněním a osou na konci.
Nezaměnit za montážní uši těla serva nebo rozteč otvorů.
Výztuha dna má podle majitele 5 mm. Powerbanka má potvrzených
139×80×26,5 mm a musí jít jednoduše vyjmout bez demontáže okolních dílů:
otevřený držák, uvolnitelné pásky, přístupné porty a volná cesta ven.
Degson je pořízený, zatím nedoručený; inventář zůstává 32 položek.

Majitel nepožaduje další výšky mikrofonu/zesilovačů, rozměry otvorů
reproduktorů a podrobnosti kolébek, LED/kabelů ani výztuhy pro rozvržení.
Původní seznam požadovaných měření v PR154 se ruší. Další konkrétní
mechanický krok je přizpůsobit CAD potvrzené spodní části kufru a tomuto
přehledu, včetně snadného vyjmutí powerbanky; další měření nejsou vstupní
blokací. Fyzické usazení, pohyb, kalibrace a elektrická akceptace se ověřují
na sestavě; tato oprava dokumentace je neprohlašuje za splněné.
Změněny společný přehled, záznam kufru, README mechaniky, plán, tento stav,
potvrzená objednávka, odkazy v historických nákupních seznamech a návody napájení/zapojení. Ověření: konzistence
údajů a inventáře, lokální odkazy, diff a nezávislé dokumentační review.
CAD/parametry/exporty se nemění; aplikační buildy ani fyzické testy neběžely.
Git předání: commit `7538816` byl ověřeně pushnutý přes repo helper,
[PR156](https://github.com/Blato58/DildoVander/pull/156). Kontrola pokrytí
32/32 položek, 89 lokálních odkazů v deseti dokumentech a whitespace prošla.
Nezávislé review vedlo k odstranění zbylých podmínek pro rozvržení u
servo adaptéru, jističe a PH2 kabelu. Před sloučením se znovu načte
GitHub review a dostupné CI podle AGENTS.md.

**D161 – 9. 10. 2026 – Wi-Fi discovery a párování přes ověřené BLE.**
Majitel požaduje nalezení ESP z iOS přes Wi-Fi a tlačítko, které po připojení
ověřeného BLE spáruje místní ovládání bez dalšího opisování a uchová přístup
v Keychain. Aktuální firmware spouští Bonjour jen se servisně provisionovanou
lokální TLS identitou; D159 má lokální TLS/pairing prázdné, takže tato brána
brání discovery. Rozsah: první vytvoření vlastní TLS identity na ESP,
první autentizované BLE vydání lokálního tokenu, nativní Keychain a UI.
Stávající identity/párování, PoP a fyzická obnova zůstávají zachované.
Firmware vytváří pouze chybějící lokální EC P-256 TLS identitu při startu
v oddělené 16KiB úloze a ukládá ji do NVS. Bonjour běží po připojení Wi-Fi
a startu TLS serveru i před párováním. Nový `pair-local` na ověřeném
Security1 vydá první token pouze pro chybějící pairing záznam. Mutex
serializuje vydání/ověření; NVS drží jen hash. QR, spárované a poškozené
záznamy se nepřepisují. Host je stejný deterministický `.local` název jako
Bonjour. Žádný token/pin není v TXT ani běžné diagnostice.

V Síť kufru je tlačítko **Spárovat ovládání přes Wi-Fi**. Nativní plugin
ověří ID proti BLE stavu, pin/token vloží atomicky bez přepsání do stávajícího
Keychain profilu a do webu vrátí jen veřejné ID/adresu. Ovládání obnoví
uložený kufr; připojení je explicitní a začíná pozorováním. Společná nativní
rezervace brání souběžnému QR/BLE spotřebování dvou tokenů. Ztracená
odpověď ani selhání zápisu se neopakují; může být nutná servisní obnova.

Ověření před integrací: Node 24.21.0 `npm test` v mobilní aplikaci **35/35**, `npm run build`
prošel; první běh odhalil chybějící contracts `ajv`, po `npm ci --prefix
kufr/contracts` prošel. In-app Browser ověřil syntetický BLE discovery,
enable tlačítka po spojení, párování bez polí, obnovu uloženého ovládání
a chybový stav. Layout kontrolován i na 393 × 852; žádné console warning/error
v těchto scénářích. Syntetický bridge nemá skutečný Keychain/Bluetooth.

Po začlenění aktuálního `main` (D160 PoP/quiet polling a PR154 dokumentace)
prošlo **41/41** mobilních testů a TypeScript/Vite build; přidaný test
ověřuje pairing tap během pollu, jedinou rezervaci a zachování generace.
Pět hostitelských C++ sad bylo znovu sestaveno MSVC `/std:c++14 /W4 /WX`
a spuštěno: pairing (včetně souběžného jediného vydání), TLS identity,
BLE endpoint/limit/oddělená odpověď, Bonjour adapter a lokální TLS server
(prázdné párování nezíská bearer přístup). Firmware `pio run` a finální
`pio run -s` prošly. Nezávislé source review ověřilo protocomm Security1
auth/decrypt před callbackem, synchronizaci a nativní bridge bez otevřeného
materiálního nálezu; testy nepředstavují skutečnou desku.

Nativní harness doplněn o BLE decoder, vazbu ID, chybné/oversized odpovědi,
nepřepsání Keychain a rezervaci QR/BLE. Windows nemá Swift/Xcode: tento
harness, nativní plugin a persistenci po aktualizaci zatím neověřil macOS
ani iPhone. Nový firmware ani IPA nebyly nasazené. Další konkrétní krok:
po vyžádaném podepsaném buildu a firmware publikaci ověřit na ESP/iPhonu
Bonjour, párování přes BLE, pinned WSS, restart ESP, relaunch/aktualizaci
aplikace a odmítnutí cizího pinu/tokenu. HW/E10 akceptace se neuzavírá.

Git předání: integrovaný implementační commit
`d571f7a726b67d0b7e3a2ba2dca03a25feafe26e` byl ověřeně pushnutý helperem
na `codex/ble-wifi-pairing`; [PR155](https://github.com/Blato58/DildoVander/pull/155).
Lokální necommitovaný `.codex/environments/environment.toml` je nesouvisející
a zůstal zachovaný; při push byl pouze v procesu helperu potlačen jeho
untracked status. Po rebasi pět C++ sad i `pio run -s` znovu prošly.
Před merge zbývá ověřit finální CI a znovu načíst GitHub reviews/inline vlákna.

GitHub review155 našlo P1: starší provisionované backend ID může mít
underscore/velká písmena; společný hostname helper odmítal takové ID i pro
první TLS vytvoření. Hashování pro TLS nyní vyžaduje jen neprázdné ID do
64 B, takže generuje bezpečný DNS label i pro `kufr_1`, `Kufr-001`, `1kufr`.
Existující control-v1 gramatika se nerozšiřuje: tyto starší identity vyžadují
servisní migraci pro iOS/LAN ovládání. Explicitní brána před BLE vydáním
tokenu je odmítne bez spotřebování pairing záznamu. Regrese ověřuje obě
hranice; nejde o prohlášení podpory staršího ID v control kontraktu.

**9. 10. 2026 – rozměry nakoupených součástek: dokumentační rešerše.**
Historie PR154; požadavky na další měření níže byly nahrazené následným
upřesněním majitele výše a nejsou aktuálními blokacemi rozvržení.
Na žádost majitele je připravený [přehled všech 32 položek potvrzeného
inventáře](../mechanika/rozmery-nakoupenych-soucastek.md), navíc objednané
kamery a vybrané powerbanky. Katalogové údaje byly ověřené na konkrétních
stránkách výrobců/prodejců a oddělené od půdorysů PCB, délek kabelů a
podmíněné shody variant. Rozměry svorek Degson a celé obálky PTT nebyly
publikované; přesná kamera nebyla identifikovaná. Rešerše zjistila rozpor
U059 výrobce/prodejce a neúplné katalogové výšky některých desek.
Následně majitel doplnil PTT/PCA a zvolil rozměr U059 podle výrobce;
ostatní požadovaná měření pro rozvržení zrušil. Aktuální údaje jsou v přehledu.

Změněné soubory: nový přehled rozměrů, odkazy v README mechaniky,
záznamu doručeného kufru a potvrzené objednávce; tento stav. README také
opraveno na již potvrzené dva zesilovače. Ověření: kontrola inventáře,
zdrojových URL, lokálních odkazů a `git diff --check`. Neprováděly se
aplikační buildy, placené AI testy, CAD export ani fyzická měření.
CAD a parametry jsou beze změny, HW kritéria zůstávají otevřená.
Původní další krok s doplňováním měření je nahrazený předáním výše:
rozložení upravit podle potvrzených údajů, následně ověřit obálky/pohyb před tiskem.
Git předání: dokumentační commit `ca503ca` byl ověřeně pushnutý helperem,
[PR154](https://github.com/Blato58/DildoVander/pull/154). Souběžný PR151
byl začleněný z `main` commitem `6b7884b`, rovněž ověřeně pushnutým;
oba záznamy předání jsou zachované. Integrovaný diff vůči `main` zůstává
omezený na uvedených pět dokumentů. GitHub review a CI se před sloučením
musí dokončit a znovu zkontrolovat podle AGENTS.md.

**D160 – 9. 10. 2026 – stabilní BLE obrazovka, uložený PoP a vysvětlení LAN discovery.**
Majitel hlásil blikání po BLE spojení, opakované opisování PoP a prázdné
LAN hledání. Slice mění mobilní ProvisioningClient/Panel, nativní
KufrProvisioningPlugin, LocalDeviceIdentity/Discovery, zprávu LAN hledání,
webový harness a web/native regrese; plán a oba návody odpovídají změně.
Polling už nebliká deaktivací tlačítek ani přepisem nezměněných metrik.
Akce i reconnect čekají na volný BLE slot, zůstávají v původní generaci
a uchovávají profil z okamžiku klepnutí. Nezávislé review našlo a následně
potvrdilo opravy zahozeného reconnect tapu a záměny profilu při čekání.
PoP se ukládá pouze po úspěšné autentizaci Security1 do odděleného
nesynchronizovaného Keychain WhenUnlockedThisDeviceOnly; při reconnectu lze
použít uložený údaj nebo bezpečně zadat nový. Wi-Fi hesla se neukládají,
JavaScript PoP nečte. Bonjour TXT klíče se validují bez závislosti na velikosti
písmen, kolidující varianty se odmítají; TLS a identita dál ověřují spojení.

LAN příčina je oddělená: espidf_main spouští lokální server až po provisioningu
TLS identity a místního párování, Bonjour až po startu serveru. USB network-service
z D159 zapisuje jen BLE/device/backend/OTA, nikoli `wand_tls/server` a
`wand_pairing/local`. D159 doložil původně prázdné TLS/pairing namespaces;
bez jejich doplnění kufr žádnou WSS službu nepublikuje. Toto je source/commissioning
důkaz, nikoli čerstvé čtení NVS; firmware brána nebyla oslabena ani deska změněna.

Ověření: `npm ci` pro mobil i contracts, `npm test` **36/36**, `npm run build`
(TypeScript/Vite) prošly. První běh bez contracts závislostí selhal na chybějícím
ajv; po jejich instalaci prošel. Playwright browser harness při 393×852
ověřil připojení, opakované polling cykly s **0 DOM změnami**, aktivní tlačítka,
viditelné metriky a nulové horizontální přetečení. Klepnutí Delete při čekajícím
pollu a následná změna hotspot→fallback odeslalo původní `profile=hotspot`.
Počáteční favicon404 je vývojový harness; aplikační console chyby nebyly nalezeny.
Nezávislé finální source review nemá otevřený P1/P2 nález. Nové Swift testy pro
Keychain a Apple TXT decoding jsou připravené, **na Windows se nespustily**.
MacOS/Xcode, nová podepsaná IPA, instalace, fyzický BLE/Keychain/LAN test
ani servisní doplnění místní TLS/pairing nejsou tímto slice doložené.
Další krok: podepsat/publikovat aktualizaci po oprávnění, ověřit na iPhonu
a samostatně připravit místní TLS/pairing provisioning skutečného kufru.
Git předání: implementační commit `ac9c9e0e830eb6fa62627ca3d210f9f9179afab3`
byl ověřeně pushnutý helperem; [PR151](https://github.com/Blato58/DildoVander/pull/151).
Souběžný PR150 byl začleněný s oběma záznamy; změnil jen dokumentaci.
60 místních odkazů a whitespace prošly. [CI37915875139](https://github.com/Blato58/DildoVander/actions/runs/37915875139)
pro integrovaný commit `44256f0` prošlo včetně web/contracts/signing-source
regresí a required gate; backend/firmware/AI byly správně přeskočené.
GitHub review pro `2c5fcc9` dokončilo bez nálezů; bezprostřední recheck
odhalil nový dokumentační PR153. Jeho záznam rozměrů byl zachován; před merge
se znovu načtou reviews i inline vlákna a ověří finální CI.
Review pro `13355f3` následně našlo P2: dva současně čekající tapy neměly
rezervaci prvního požadavku. Oprava rezervuje jeden slot a UI označí busy hned
po uživatelském tapu; samotný polling dál busy nezobrazuje. Refresh zachová
busy do zahájení/ukončení akce a disconnect rezervaci zruší. Nová regrese
ověřuje tento interval, druhý tap a následný reconnect; finální lokální
`npm test` **37/37** a `npm run build` prošly.
Nová IPA se tímto nepublikuje.

**9. 10. 2026 – doručený kufr: potvrzené rozměry a způsob prezentace.**
Majitel v konverzaci k pěti fotografiím potvrdil vnitřní rozměry:
šířka 17″ = 431,8 mm, délka 10,5″ = 266,7 mm a výška spodní části
od dna po horní okraj 4,5″ = 114,3 mm. Pracovní zaokrouhlení je
432 × 267 × 114 mm (šířka × délka × výška). Prezentace bude s otevřeným
kufrem; veškerá mechanika a elektronika mají být ve spodní části,
víko zůstane prázdné a jeho prostor se do zástavby nezapočítává.
Tloušťku příčné výztuhy majitel následně doplnil: **5 mm**;
další údaje o ní neblokují rozvržení.

Zapsáno do plánu, README mechaniky a samostatného souboru
[rozměry doručeného kufru](../mechanika/rozmery-doruceneho-kufru.md).
CAD, parametry a exporty se tímto
zápisem nemění; původní obálka 310 × 450 × 200 mm není důkazem usazení
do doručeného kufru. Další mechanický krok: přizpůsobit rozložení
potvrzeným rozměrům a znovu ověřit obálky/pohyb před tiskem.
Jde pouze o dokumentační záznam, bez fyzické akceptace nebo nasazení.

**D159 – 9. 10. 2026 – USB provisioning, OTA0.1.1 a backend po restartu.**
Majitel požádal o provisioning připojeného ESP a potvrdil samostatnou desku
bez pohonů a přesun datového kabelu z CH343 UART/COM7 na native USB/COM8.
Čtení ROM ověřilo ESP32-S3 rev0.2, MAC `68:ee:8f:4b:60:48`, 16 MiB flash,
embedded 8 MiB PSRAM AP_3v3. Secure boot/flash encryption jsou vypnuté;
žádný eFuse nebyl změněn. Před zápisem byla uložená owner-only soukromá
záloha celé flash (16777216 B), NVS a otadata. SHA-256 původní flash je
`b75db526a8737a4e2bb4b39b27bf2657ab4c209c6d5cac7bb703eaccb7b4dd7f`.
Původní NVS neměla device identity, PoP ani Wi-Fi profil; existující TLS/pairing
namespaces neměly žádné logické klíče, tedy nejde o test zachování existujících
platných pairing credentialů.

USB network-service ESP-IDF build a všech pět jeho Python testů prošly.
Partition tabulka byla před zápisem porovnaná s instalovanou. Servisní obraz
ohlásil `KUFR_SERVICE_READY`; autorizovaný frame vrátil `saved` pro novou
identitu `kufr-68ee8f4b6048`, náhodný credential a PoP, backend host
`2.28.202.220`, development OTA origin stejného HTTPS serveru, veřejný P-256
trust, automatickou instalaci a interval 3600 s. Žádné NVS erase ani úplné
flash erase neproběhlo. Raw credential, PoP a zálohy zůstávají výhradně v
owner-only `DildoVander-private/esp32s3-68ee8f4b6048-20261009T081416Z/`;
`iphone-pairing-private.json` byl otevřený majiteli pro nativní PoP dialog.
SSID/heslo se zadávaly pouze na telefonu, nikoli v chatu či příkazové řádce.

Na VPS byla nejdříve soukromě zálohovaná původní unit a `backend.env`.
Nová identita má index `1` v root-only
`/etc/kufr-backend/device-identities.env`, připojeném trvalým systemd drop-in
`50-device-identities.conf`. Deploy index `0` a jeho původní ID/hash zůstaly
nezměněné; budoucí aktivace přepisuje pouze původní `backend.env`.
Přenesen byl jen SHA-256 přesného UTF-8 hex credential textu, nikoli raw
credential nebo PoP. Po restartu prošel backend/nginx health a skutečný HTTPS
token exchange nové identity ze servisního PC. Raw credential staré identity
nebyl dostupný: její zachování je kontrola efektivní konfigurace, nikoli nový
autentizační test. Restart zneplatnil RAM access tokeny; dlouhodobé credentialy
a data se zachovaly. Provozní vlastnictví konfigurace je popsané v backend README.

Z ověřeného CI37895236311 byl bezpečně stažený a SHA-256 ověřený balíček.
Na desku se obnovily bootloader, partition table, initial otadata a aplikace
0.1.0 s build ID `d932ec15ea005c3a10001874645fea5062ab54b5`;
obraz 1732304 B má SHA-256
`cf31728845fd24ecbfca999d193bda047e2bbdd537b514de2747f5328bf50048`.
Esptool ověřil hashe všech zápisů. Následný USB readback NVS nezávisle ověřil
identitu, PoP, backend a OTA settings. Při tomto obnovení nebyla novější
CI-cache změna PR146 znovu nahraná; pozdější OTA je doložená níže.

Majitel v publikované nativní aplikaci potvrdil discovery/spojení s PoP
a nastavil domácí Wi-Fi. Jeho počáteční hlášení Backend připojený se po
restartu nepotvrdilo; obnovený stav byl backend offline. USB readback
ověřil úspěšný `profiles_v1` fallback blob (hotspot slot zatím prázdný)
a nezměněné servisní settings bez výpisu SSID/hesla. Po hard resetu majitel
potvrdil automatické Wi-Fi znovupřipojení bez opětovného zadání hesla.

UART log z COM7 ověřil boot 0.1.0/ESP-IDF6.1, DHCP a validovaný TLS
certificate bundle; nginx doložil skutečné ESP GET development manifestu200.
NVS měla důvěryhodný UTC checkpoint 08:49:23Z. Počítačové NTP sondy do
pool.ntp.org/Cloudflare/Google prošly, ale nepředstavují ESP time test.
Žádný ESP token POST na server nedorazil. P1 byl dohledaný v pinned SDK:
`set_post_field(client, "", 0)` vrací `ESP_ERR_NOT_FOUND`, když chybí
Content-Type; boolean konfigurace proto vůbec nevolal `perform`.
Oprava ponechá prázdné tělo nového klienta, pro něž SDK automaticky vytvoří
Content-Length0. Bearer, TLS, zákaz redirectů/auth retry a credential wiping
se zachovávají. Ostatní post-field adaptéry explicitně nastavují Content-Type.

Nová host regrese modeluje přesné chybové SDK chování, skutečný adapter POST
a parse200, 401/ESP_FAIL, transport failure, 513B overflow, header failure
a odmítnutí neplatného credentialu. MSVC `/std:c++14 /EHsc /W4 /WX`
na starém kódu skončil1, po opravě0. Lokální
`pio run -d kufr/firmware -e esp32-s3-idf -s` prošel; firmware je 0.1.1.
Regrese je přidaná do existujícího firmware CI, nová PR146 cache se zachovává.
Lokální testovací obraz má předchozí HEAD metadata a není release artefakt.
Nezávislé SDK/source review potvrdilo příčinu a správnost bodyless POST.
Původní šestidokumentový link check ověřil56 odkazů a všech47 readiness řádků;
review nalezené rozpory o dokončeném servisu/dev trust byly opravené.

Implementační commit `d80d95d84156bb56b37f3e103bd41ac5ab9c49cf` byl ověřeně
pushnutý přes helper. [PR149](https://github.com/Blato58/DildoVander/pull/149)
má úspěšné [CI37912208656](https://github.com/Blato58/DildoVander/actions/runs/37912208656)
včetně Linux host regresí, ESP-IDF a všech servisních buildů. Nezávislé source
review i GitHub Codex review dokončily bez nálezů; před merge byly znovu načtené
reviews, komentáře a prázdná unresolved vlákna. Squash `668949e37756ccd05c3be980e9af64eb05385841`
má stejný Git tree `0378d4a51a32e90dfa6d5d8c5c83672d1c477fd9` jako ověřený
CI obraz. [Development publish37912902381](https://github.com/Blato58/DildoVander/actions/runs/37912902381)
prošel root provenance verifierem i podpisem. Veřejný HTTPS manifest,
P-256 podpis a image SHA-256 se nezávisle ověřily; stažený CI balíček má
shodný tree a bajty. Verze0.1.1 má build ID PR merge ref
`81543ff4404f16bdbb7c09e8b47ab5f19f3d30b5`, délku1732288 B a SHA-256
`5e937b158eae49d49b7812fbe952bfcfdc201a168770e11c6ea4b0a2ed5ab5e2`.

**Skutečný OTA a backend výsledek:** po vědomém UART resetu09:43:47Z se
deska sama připojila k uložené Wi-Fi, stáhla development manifest09:44:22Z
a nový image09:44:43Z (nginx ESP32 GET200). Bez dalšího USB zápisu bootovala
09:44:49Z z `0x650000`/ota_1 a UART oznámil verzi0.1.1. Skutečný ESP
token POST skončil200 v09:45:03Z. Read-only ROM čtení NVS/otadata doložilo
oba platné metadata CRC, seq1/VALID původního slotu a seq2/VALID nového slotu:
první boot tedy úspěšně dokončil health potvrzení. Porovnání přes IDF NVS
parser ověřilo všech9 logických klíčů v `wand_identity`, `wand_ble`,
`wand_network` a `wand_ota` beze změny proti před-OTA NVS; žádný credential
se nevypisoval. Nový hard reset09:47:19Z znovu bootoval0.1.1/ota_1,
připojil Wi-Fi a token POST200 v09:47:43Z. Majitel potom na iPhonu znovu
potvrdil **Wi-Fi connected; Backend připojený** se stejným PoP.
Soukromé raw logy, NVS/otadata, receipt a přijatý CI USB recovery balíček
zůstávají u majitele. UART capture skončilo a COM7 je uvolněný.

Lokální source regrese/build, 55 Node CI/provenance testů, nezávislé review,
79 místních odkazů a přesně47 readiness řádků prošly před implementačním
Git předáním. Placené AI volání, pohyby, SD/audio/kamera, špatný PoP/heslo,
iPhone hotspot/failover, OTA power-loss/negativní podpis/rollback a plná
akceptace celé sestavy neproběhly. Development trust není stable autorita.
Další krok pro akci: doplnit iPhone hotspot a samostatně přijmout zbývající
scénáře podle event-readiness; domácí síť je úspěšně provisionovaná.
Finální šestidokumentový acceptance diff prošel nezávislým review bez nálezu,
67 místních odkazů, všech47 scénářů, whitespace a kontrolou nepřítomnosti
soukromých credentialů/PoP v diffu. Pro docs-only předání se aplikace znovu
nebuildovala; úspěšný firmware CI a fyzický výsledek jsou doložené výše.
Acceptance commit `cac1e3e9045a7206f7c2792e3156f04796cefcc9` byl pushnutý
přes helper a [PR150](https://github.com/Blato58/DildoVander/pull/150) je
sloučený jako `d3b2b964e9c74f49e29949f19a69797465431eea`. Jeho GitHub P2
odhalilo starší aktuální E4/B1/B4 tabulky, které stále uváděly dokončený
USB/NVS/STA/backend servis jako blokaci. Navazující dokumentační oprava
aktualizuje tyto živé tabulky a konkrétní předání podle D159; historický deník
zůstává datovaný. E4/E10 a celá fyzická akceptace tím nejsou označené za hotové.
Nezávislé source review opravilo také sousední zastaralou E9 blokaci runtime:
Realtime/GPT-Live PCM/I2S cesta existuje, starší oddělený STT upload ji
neblokuje. Finální review změněného rozsahu nemá nález; všech27 místních
odkazů v tomto stavu a whitespace prošly. Kód ani firmware image se neměnily.
Navazující GitHub review požadovalo shodný D159 acceptance rozsah také v mobilním
README. To nyní rozlišuje potvrzené BLE/PoP/domácí Wi-Fi/status od neověřeného
hotspotu, LAN, audio a telefonních OTA tlačítek.
Tento dvoudokumentový doplněk prošel nezávislým review, 27+12 místními
odkazy a whitespace kontrolou; nebyla přidaná další HW či hlasová akceptace.

**9. 10. 2026 – zrychlení firmware CI.** Převzaté soubory:
`.github/workflows/kufr-firmware.yml`, nové `kufr/firmware/ci/` a firmware
README. Živá kontrola potvrdila VPS s 1 CPU, 1909 MiB RAM, 2 GiB swap
a dvěma aktivními runnery s odděleným `HOME`; backend health je `ok`.
Posledních pět úspěšných firmware jobů trvalo 437–622 s; novější joby
mají navíc další USB diagnostiku. Obnova toolchain cache zabírala
54–78 s, hlavní ESP-IDF 168–286 s a každá diagnostika přibližně minutu.
Slice nahrazuje přenos archivu lokálně zachovaným toolchainem/venv a
SCons cache mimo checkout, oddělenou po runnerech a omezenou na 1 GiB.
Objekty s `__DATE__`/`__TIME__` se vždy překládají. Lokální Windows
`pio run -d kufr/firmware -e esp32-s3-idf` na zdrojích `199664e` prošel
se studenou cache za 305,71 s. Po odložení build adresáře mimo aktivní
profil zachovalý cache načetl 1334 výstupů; finální teplý build prošel
za 97,01 s, ale měl již CMake konfiguraci z předchozího pokusu (42,78 s).
Nejde tedy o přímo srovnatelné celé joby. Guard nejprve odhalil vnořené
SCons NodeList; opravené `Flatten(PIOBUILDFILES)` vybírá skutečný objekt.
Log potvrzuje nový překlad network, app i bootloader timestamp objektů.
Pět Python regresí pro pruning a timestamp graph, 55 Node CI/provenance
testů, 6 CI-routing testů, `actionlint` 1.7.12 a whitespace prošly.
Nezávislé review je po opravách bez otevřeného nálezu.
Před Git předáním začleněný `origin/main` `d4862ff` (nové OTA/BLE/SD
software slices); jejich kontroly a USB network diagnostika zůstávají
v upraveném workflow. Implementační commit `703792186bcb1e4fda7e1683fdce0baeec4158ba`
byl odeslaný přes `Push-CurrentBranch.ps1`; remote SHA je ověřená.
PR [146](https://github.com/Blato58/DildoVander/pull/146) má první plný
[CI 37897760345](https://github.com/Blato58/DildoVander/actions/runs/37897760345)
úspěšný: Linux host tests, pět cache regresí, repository checks, ESP-IDF,
OTA version check, managed cJSON BLE test a všechny čtyři diagnostiky
i `CI required`. Firmware job `113712989981` na `dildovander-server`
trval 530 s (8m50s), z toho příprava Core 9 s, hlavní build 251 s a
diagnostiky 59/58/66/71 s. První běh vytvořil 374 MiB cache; pruning
nemusel nic mazat. Stažený artefakt prošel SHA-256 a bezpečným unpackem
`scripts/ci/package.py`; firmware OTA verze `0.1.0` je ověřená.
Po začlenění iOS-only `main` přes PR sync prošel celý
[CI 37899404378](https://github.com/Blato58/DildoVander/actions/runs/37899404378)
na `028df36` se shodným firmware Git tree. Job na `dildovander-server-2`
trval 362 s (6m02s) s částečně teplou cache. Hlavní build klesl
z 267 s v předchozím běhu stejného runneru na 141 s a příprava z 10 s na 1 s;
některé diagnostiky ještě plnily cache. Předchozí běh přerušil PR sync.
Následné GitHub Codex review našlo dvě P2: namespace bez identity toolchainu
a nezapočítané přerušené cache zápisy. Oprava zavádí fingerprint build pinů,
lockfilů, metadat balíčků a compiler/linker/archive binárek; přes všechny
namespaces platí společný 1 GiB a dočasné zápisy mladší jedné hodiny jsou
chráněné. Deset lokálních regresí prošlo. Nový namespace začíná studený,
staré objekty bez zaznamenaného fingerprintu se nepřejmenovávají.
Opravy v commitu `9a65bb219466515acf68427a4dff49aab12412cb` byly ověřeně
pushnuté přes helper. Nezávislé review a GitHub Codex review `9a65bb2`
i následného `a08dd12` dokončily bez nových nálezů; obě původní P2 vlákna
jsou po opravě vyřešená. Dokumentační `main` `7be0387` začlenil PR sync;
firmware Git tree zůstal `fa568d147e89594c8a7a454dfa0b7440b9d0be97`.
Předchozí `CI 37901626217` proto přerušilo poslední diagnostiku, nikoli chyba
kódu. Celý [CI 37902376481](https://github.com/Blato58/DildoVander/actions/runs/37902376481)
na `a08dd12` prošel: deset cache regresí, všechny host tests, repository
checks, ESP-IDF, BLE/cJSON, čtyři diagnostiky, OTA check, balení i `CI required`.
Studený nový namespace na druhém runneru znamenal 533 s (8m53s), příprava
2 s, hlavní build 261 s, diagnostiky 59/59/66/71 s. Oba runnery nyní mají
nový cache namespace; plný běh s jeho teplou cache bude doložen v kontrolách PR.
Nevzniká nová VPS objednávka, provider inference, deploy ani HW flash.

**D158 – 9. 10. 2026 – plný audit, nativní BLE, hotspot-first Wi-Fi, OTA a SD.**
Převzato z čistého tracked `6cb130d`; větev `codex/event-software-completion`.
Nesouvisející untracked `.codex/` zůstává mimo slice. Majitel následně výslovně
autorizoval VPS, push a publikaci. Nahrání do desky a placené AI testy nebyly
součástí tohoto povolení. Kompletní inventura a 47 scénářů jsou v
[auditu](../../docs/software-completion-audit.md) a
[event readiness](../../docs/event-readiness.md).

Implementováno: oficiální BLE Security1 s device-specific PoP, nativní
ESPProvision/CoreBluetooth v existující Capacitor aplikaci, tajemství zadávaná
jen nativně a compact wire do 512 B. První PoP vyžaduje fyzický USB servis;
bez něj BLE selže uzavřeně. Hotspot/fallback jsou jeden NVS blob s
test-before-commit a rollback; po explicitním úspěchu fallback nebo jeho
obnovení běží nový 60s interval do dalšího hotspot probe. Společná rezervace
síť/OTA/SD brání nové hlasové/ruční/fyzické interakci během údržby.
OTA ověřuje podepsaný P-256 manifest, kompatibilitu/velikost/verzi, HTTPS bez
redirectů a SHA celého obrazu v inactive slotu; má bounded první boot/rollback
a automatické i ruční kontroly. Read-only backend endpointy mají limit čtyř
souběžných odpovědí. Dev a stable autority jsou oddělené; privátní klíč není
v repo/image/IPA. USB network-service nastavuje PoP/backend identitu/veřejný
OTA trust bez NVS erase. SDMMC1-bit asset driver zachovává GPIO38–40,
vyžaduje servisně ověřené zapojení a nepovoluje format/write/erase.
Kamera, GPT-Live, personálity, hlas i B-effects zůstaly zachované.

Aktuální implementační CI: commit `bf429ad`, run **37895236311**, všechny kontroly úspěšné:
firmware host suite, main ESP-IDF build a čtyři diagnostické buildy,
backend, web a repository checks. Zahrnuje celý .NET executable runner,
HTTP smoke, 20 client Python a 12 deploy Python testů. Lokálně prošly mobil
32/32 + TypeScript/Vite build, browser 393/320 px bez overflow, kontrakty
včetně OTA GET, MSVC `/std:c++14 /W4 /WX` testy profilů/STA/Wi-Fi/BLE/OTA/SD
a maintenance owner. USB network-service Python 5/5 a hardware-service 6/6
prošly; OTA Python 9/9 použilo skutečný OpenSSL bez skipu. Dosavadní package
testy 4/4 ověřily podpis/corruption a bounded fd publisher; nový P1 níže
vyžaduje samostatnou regresi. Partition guard kontroluje i CSV flags.
Backend build měl 0 warnings/errors,
deploy-helper 4/4, CI/provenance Node 55/55 a offline AI eval prošly.
Lokální `pio run -d kufr/firmware -e esp32-s3-idf` prošel: RAM 86300 B,
flash 1731653 B, app binary 1732080 B z 6553600 B slotu. Jeho OpenSSL
package/verify a partition/image check prošly; tento lokální obraz obsahuje
identitu původního HEAD a není clean CI release.

Starší neúspěchy jsou vyřešené, nikoli nové runtime akceptace: GCC warning
v SD testu, discovery linker (doplněný real NetworkProfiles.cpp a storage
stub; reprodukce i oprava prošly na izolovaném Linuxu GCC15.2) a HTTP/1 reuse
u nepřečteného image413. Realtime smoke race s eager oversized uploadem na
Node24 byla reprodukována; declared-size 413 nyní používá Content-Length
a Expect100-continue, ověřuje JSON/no-store a zachovává streamed-overflow
testy. Full HTTP smoke na Node24.21.0 prošel třikrát po sobě. Opravy prošly
nezávislým review a aktuálním CI. Dřívější plný disk/OOM nebyl softwarová
regrese. Široké Windows Python discovery narazilo na Linux/POSIX předpoklady
a Windows cestu/SQLite cleanup; rozhodující serverové kontroly prošly v CI.

Skutečný macOS15/Xcode26.3 resolver **37887830815** prošel; commit `478f1ab`
obsahuje byte-for-byte Package.resolved s Capacitor8.5.2, ESPProvision3.2.0
a SwiftProtobuf1.38.1 včetně ověřených Git revisions. Dočasný resolver
workflow je odstraněn. Plný nativní compile, podpis a publikace nového IPA
později prošly v existující ruční pipeline podle finálních dokladů níže.

VPS cleanup je dokončený: zbývají pouze GitHub runnery ID2 (main) a ID22 (-2).
Třetí runner byl odregistrován, Codex i CloudVPSBridge jsou odinstalované;
soukromá záloha je `/root/kufr-maintenance/d158-ade0e32/removed-software/`.
Odstraněny jejich služby/mounty/vhosty i regenerovatelné cache a staré
Arduino toolchains. Bezpečně odstraněno 26 starých backend releases po
kontrole cest/mountů/symlinků a nezměněného current; aktivní release a dva
nejnovější rollbacky zůstaly. Soukromý konfigurační archiv a inventura mají
root-only mód 0600 ve stejné záloze. Releases klesly z 3,2 GB na 340 MB;
root disk má 15 GB volných z 38 GB (21 GB použito, 59 %). Systém má 2 GB
swap pro oba runnery. Backend healthz, iOS HTTPS,
nginx a oba runner services jsou zdravé. Backend, development OTA i podepsané
iOS jsou nasazené podle dokladů níže. Finální snapshot po publikaci: 14 GB
volných z 38 GB, 22 GB použito (62 %), oba runnery online/idle, healthz ok.

P1 root signing helper byl opraven: nezávisle čte GitHub successful CI,
merged PR/current main tree, serverový ZIP digest a bounded ZIP/TAR;
verzi/build ID odvozuje ze zdroje a porovnává přesný root snapshot před
podpisem. Ephemeral token je pouze stdin, s 10s deadline a bez persistence;
download redirect nikdy nepřebírá Authorization. Před promotion znovu
kontroluje main. Regrese 30/30 prošly na Linuxu (Windows 29 + jeden POSIX
skip), nezávislé security review bez dalšího bypassu. Root-owned verifier
a helper jsou instalované, sudoers/py_compile/hash/health ověřené; po
dočasném zákazu je nový helper znovu povolený. CI builder zůstává důvěryhodný
producent, nejde o reproducible-build attestation. Další čerstvé Codex review
na `e3d4da2` našlo OTA boot calibration/health P1 a BLE shutdown retry/NVS
cleanup error P2; jsou opravené. Selected profile vyžaduje kalibraci i
aktuální pasivní effects health před 5s potvrzením bootu. BLE teardown se
opakuje každou sekundu a nespotřebuje recovery window do úspěšného otevření.
NVS cleanup propaguje erase/final commit failure; po failed save se jednou
obnoví předchozí blob, RAM zůstává stará a výsledek je vždy storage_failed.
Trvalá chyba úložiště může zmařit i rollback; nejde o cross-key transakci ani
fyzickou power-loss akceptaci. MSVC regrese prošly; parent na skutečném
Linuxu GCC15.2 znovu sestavil a spustil OTA policy, NVS adapter, pure profiles,
STA lifecycle a BLE/real cJSON (vše C++14 -Wall/-Wextra/-Werror), všech pět
binárek prošlo. Node provenance/freshness 55/55 znovu prošlo.
Další dokončené Codex review na `9a3395f` našlo OTA stack P1 a dvě BLE P2.
OTA nyní alokuje jediný 4096 B heap buffer před flash begin a sdílí jej pro
download i přesné hashování partition; selhání alokace neeraseuje/nepíše ani
nepromuje slot. Regrese pokrývají jedinou alokaci, její chybu, partial tail,
poškozený poslední byte a truncation. Xtensa GCC15.2 `-fstack-usage` a
`-Wframe-larger-than=2048` změřily Install 624 B, VerifyPartition 208 B a
Check 1664 B; nejde o úplnou SDK/TLS callgraph ani fyzický high-water.
Initial BLE window se opakuje po 1s nezávisle na připojeném Wi-Fi, má původní
absolutní 5min deadline a nečerpá pozdější offline recovery. ESPProvision3.2.0
`espDeviceNotFound` vrací prázdný seznam místo chybného Bluetooth failure;
typ callbacku byl ověřen proti skutečné resolved SDK revizi. MSVC adapter
regrese i skutečné Linux GCC15.2 C++14 -Wall/-Wextra/-Werror OTA adapter a
BLE/real cJSON binárky prošly. Nezávislé finální source review je čisté.
Finální Codex review na `bf429ad` dokončeno bez major issues; všech deset
nálezových vláken je vyřešených a před merge byly znovu načteny všechny reviews,
inline komentáře i stav Codex. CI **37895236311** je kompletně zelené.
Git předání začalo `c2ab3cf`, zahrnuje synchronizaci `main`
`ade0e32` a `bf429ad`; scoped push byl ověřen. [PR144](https://github.com/Blato58/DildoVander/pull/144)
byl squash sloučen jako `8218ba058641fe4b886642821ce4afaaad7e27c9`.
Main tree `b72d2a501e8379f989160f6843910574fe4703cd` odpovídá testované větvi.
CI synthetic source `d932ec15ea005c3a10001874645fea5062ab54b5` má stejný tree;
rozdíl SHA po squash není rozdíl zdroje.

[Backend deploy37896345546](https://github.com/Blato58/DildoVander/actions/runs/37896345546)
uspěl v attempt2. První attempt bezpečně skončil před aktivací kvůli prázdným
runner provisioning metadatům. Dva existující store IDs byly obnoveny z active
release config do sdíleného private journal600/dir700; žádná guest data nebyla
kopírována ani žádný store vytvořen. Soukromá metadata záloha je pod
`/root/kufr-maintenance/d158-ota-provenance/`. Current je
`/opt/kufr-backend/releases/d932ec15ea005c3a10001874645fea5062ab54b5-37896345546-2`.
Loopback healthz, služby a public mode/store smoke prošly bez inference.
Zachovány Responses/Sol, OpenAI voice, AutonomousActor/Luna, quiet1500ms,
hybrid memory/cloud indexing true a long-term personal false.

[Development OTA37896352363](https://github.com/Blato58/DildoVander/actions/runs/37896352363)
uspělo včetně skutečného root GitHub verifieru a atomické publikace. Manifest
je `/v1/firmware/manifests/development.json`, verze0.1.0, buildId výše uvedený
synthetic source. Image má **1732304 B**, SHA-256
`cf31728845fd24ecbfca999d193bda047e2bbdd537b514de2747f5328bf50048`.
Parent stáhl manifest/image přes public HTTPS s běžnou TLS kontrolou, ověřil
přesné bajty/hash a OpenSSL signature proti provisionovanému development public
key. Jde o development release bez skutečného ESP downloadu/bootu.

[iOS sign37896349106](https://github.com/Blato58/DildoVander/actions/runs/37896349106)
prošlo provenance, package resolution, native LAN, Debug/realtime a hands-free
testy; skončilo na existující costume fixture assertion. Scenario device name
obsahuje `-renewed-`, což chybně označilo původní token za renewed. Source fix
používá přesný očekávaný token a zachovává kontrolu všech následujících DELETE;
nezávislé source review potvrdilo deterministic fixture defect, nikoli product
token-retention bug. Na větvi `codex/event-software-completion-receipt` je oprava
a upřesnění NVS/OTA docs. [PR145](https://github.com/Blato58/DildoVander/pull/145)
po zeleném web CI **37897276104**, nezávislém source/doc review (43 místních
odkazů, všech 47 řádků) a dokončeném Codex review bez nálezů sloučen jako
`d4862ff49210d5439c4a741a666c33ae13e79f79`; cílový tree ověřen proti `c3a7216`.
[Nový macOS run37897551684](https://github.com/Blato58/DildoVander/actions/runs/37897551684)
prošel všemi native LAN/Debug/realtime/hands-free/costume i BLE status/whitelist
testy. Archive skončil na globálním app provisioning profile, který se přenesl
na SwiftProtobuf resource bundle. Signing oprava vymezí profile/identity/team/
bundle/version pouze na App Release target a zachová private cleanup/export
kontroly. Source oprava přes built-in plutil a strict helper mění jen šest
veřejných App Release settings; dependencies/Debug/project defaults zůstávají
původní. Exit obnovuje původní project bytes. Když obnova selže, uchová pouze
public project backup a vždy odstraní credential-bearing work_dir.
6 Python test methods včetně šesti úplných Bash exit scénářů prošlo na Windows
i skutečném Linuxu; nejde o skutečné macOS plutil/archive. Nezávislé review je
čisté a existující web CI nově spouští stejnou regresi.
[PR147](https://github.com/Blato58/DildoVander/pull/147) prošel web/repository
CI **37898951185** a dokončeným Codex review bez nálezů; před merge byly všechny
reviews/inline/stav znovu načteny. Sloučen jako
`edb8eabf3449f2e4cd87ac25ec4cfb28f9cd5142`, main tree
`48b1a060279690f8ab697258c0d8247138d974fa` odpovídá testovanému `99b2e24`.
Po úspěšné backend aktivaci odstraněn pouze neaktivovaný staging attempt1
(117963947 B) po kontrole absolute paths, mountů/symlinků a current guardu;
health prošel a current se nezměnil. Soukromá metadata záloha zůstává pod
`/root/kufr-maintenance/d158-ota-provenance/failed-backend-attempt1/`.

**Finální iOS předání je ověřené.**
[macOS15/Xcode26.3 podpis37899385679](https://github.com/Blato58/DildoVander/actions/runs/37899385679)
prošel provenance, pinned package resolution, všemi native LAN/Debug/realtime/
hands-free/costume/BLE status testy, skutečným archive i exportem. Ad Hoc
codesign --verify --deep --strict, Bundle ID a embedded profile kontrola prošly.
To zároveň ověřilo skutečný plutil/XML project a scoped signing inheritance.
[Publikace37899776001](https://github.com/Blato58/DildoVander/actions/runs/37899776001)
prošla všemi třemi joby včetně public HTTPS manifest/IPA/MIME/SHA-256 readback.
Tehdejší [Safari instalace](https://2.28.202.220/ios/) byla **1.0/build1000.0.19**,
Bundle ID `app.turquoise6409.green2444`, IPA **1858197 B**, SHA-256
`b1e1c01452cc600975e3793de0071449d955fa61db1388679301c8eb8ffd4273`.
Parent nezávisle zopakoval public HTTPS hash/Info.plist/manifest kontrolu a
ověřil lokální kopii v Downloads/DildoVander. Development OTA public key export
má SHA-256 `129902b4103e5f94aa9715fddee775ea9ec8046aefe9c752533a5f04641318f1`,
shodný s root signerem. Všechny publikace používaly odpovídající current main
tree; následné docs-only předání nemění nasazený software.
Větev `codex/event-completion-delivery-record` synchronizuje pouze audit,
readiness, plán, provozní README a tyto skutečné doklady. Starší IPA 1000.0.16
je výslovně označená jako historická; při D158 byla publikace1000.0.19.
Aktuální publikace D163 je1000.0.21 podle předání nahoře.
Žádný nový runtime kód ani
secrets/privátní klíče nejsou součástí posledního dokumentačního předání.

Žádné fyzické BLE/hotspot/SD/audio/servo, NVS power-loss ani OTA rollback
měření nebylo provedeno. Produkční stable signer musí být servisně
provisionován; development klíč není produkční. Po softwarovém předání
následuje kontrolované první USB uvedení do provozu, provisioning trust/PoP
a všech 47 scénářů na konkrétním ESP a iPhonu včetně zátěže. Inventura
scénářů ani úspěšný build nenahrazují jejich fyzickou akceptaci.


**D157 – 9. 10. 2026 – fotografie pro dialog, zrušení peněžních capů a Arduino profilu.**
Majitel zadal backend → iOS → kamerový firmware. Zdroj popisuje viditelný
kostým a možnou fiktivní postavu, ne biometrickou identitu. Před firmwarem
proběhl nový `git pull --ff-only origin main` na `3d0e73a` (D156/PR138).
Rozpracované zdroje byly zachované a jediný konflikt ve stavu sloučený;
nové B-effects, přímé PCA OE a softwarový mute se zachovávají.
Větev je `codex/costume-photo-context`, původní čistý checkout `fa81390`.
Před Git předáním byl na následný pokyn začleněný aktuální `main`
(`e58dabf`, PR139–141) přes `git merge --ff-only origin/main`.
Scoped stash obnovil zdroje, konflikt pouze v tomto záznamu zachoval oba
slices. Tracked backend/iOS/firmware/kontrakty jsou shodné s otestovanou
verzí před stash; `HEAD...origin/main` je 0/0. Po merge prošlo 55 Node
CI/provenance/freshness testů a 6 testů výběru projektů.

Backend přidal `POST .../image` (jedna odsouhlasená metadata-free JPEG,
256 KiB / 640 px, korelace a idempotence), polling a `PUT .../costume-context`
pro ruční opravu s předností. Omezený textový kontext se předává Responses,
Realtime, GPT Live a Sol rozhodování; není příkazem pohonům. Vlastní JPEG
a kódované buffery se mažou, lokální cue/operace expirují za 120 s.
Již předaný Live kontext může zůstat do ukončení spojení; běžný textový
přepis dál může obsahovat zmínku o kostýmu. SDK/OS/provider kopie nejsou
prohlašované za garantovaně vymazané. Fake neověřuje skutečné rozpoznání.

iOS Debug má native focení, náhled/retake/použít/zrušit, jednorázový RAM
draft do 120 s a ruční popis před všemi čtyřmi režimy dialogu. Hlasový
panel kufru předává explicitní `imageConsent`, výchozí false, pouze když
zařízení hlásí `cameraAvailable`. Souhlas se spotřebuje každým startem.
Firmware přidal `features/costume_photo/`, OV5640/PSRAM jedno framebuffer,
`esp32-camera` 2.1.8 + generovaný lock. SCCB port 1 GPIO4/5 je oddělený
od PCA/MCP I2C0 GPIO1/14; kamerové piny podle mapy jsou nezávislé na B-effects.
Servisní `wand_hw/camera_verified` je false a má podporovaný generator/writer.
Fotografie jde z ESP přímo backendu před mikrofonem, bez opakování uploadu.

Nezávislé review našlo a vedlo k opravě Unicode expanze cue a neurčitého
výsledku uploadu. Při chybě fotografie klient ukončí vlastněný rozhovor
a vytvoří čistý bez image consent. Backend DELETE potvrdí 204 až po
vymazání JPEG a uvolnění provider slotu; timeout 503 `image_cancel_pending`
nepovolí nový hlas. Stop/generation/snapshot zneplatní pozdní start.
Expirace ruční cue nemůže publikovat completed operaci s null výsledkem.

Majitel odstranil všechny softwarové peněžní limity: žádný lokální
session/daily/monthly budget ani povinný kladný cap. Účetní rezervace a
skutečná spotřeba zůstávají auditem; velikost, čas, počet a souběh jsou
technické meze. Backend, eval, workflow, smoke a aktuální návody souhlasí.
OpenAI nastavení se neměnilo; před budoucím deploy je nutné aktualizovat
root-owned deploy helper na VPS kvůli payloadu bez caps (viz backend README).

Na následný výslovný pokyn byl odstraněný `esp32-s3-arduino-serial`, jeho
`main.cpp` a `SerialSelectionPort`, CI build/balení a aktuální návody.
Jediný profil kufru je ESP-IDF; čisté C++ jádro a jeho host testy zůstávají.
Samostatný Arduino Nano projekt obludaria se neměnil.

Lokální ověření: finální plný `dotnet run --project kufr/backend-tests/BackendTests.csproj --no-restore`
prošel po opravách, také cílené `-- --image` a HTTP `node kufr/backend/tests/smoke.mjs`.
Node 24.21.0: mobil `npm test` 28/28 a `npm run build` prošly; kontrakty
27 valid / 42 invalid + OpenAPI/media frame/control prošly. AI eval má
25 offline testů, deploy helper 4 a realtime smoke safety 3 testy prošlé.
CI routing má 6 testů prošlých. Širší Python sady na Windows nejsou celé
zelené: starší Linux `os.getuid`/temp path, openssl/python3 a Unix executable
mode test vyžadují Linux; nepřipisovat je nové funkci. Browser syntetický
native harness ověřil manual priority, pokračování po chybě, Stop a starou
firmware capability; bez chyb konzole. Nové Swift HTTP peer testy pokrývají
přípravu všech režimů, ztracenou/neplatnou odpověď uploadu, úvod jen v novém rozhovoru a Stop během
cleanup/create; nativní provedení zůstává neověřené.

Finální `pio run -d kufr/firmware` po odstranění Arduina prošel: 82 400 B
statické RAM, 1 342 745 B flash. Servisní generator/build prošel (14 800 B
RAM / 171 053 B flash), tři hostitelské MSVC `/std:c++14 /W4 /WX` foto/HTTP/driver
testy a dosavadní start codec prošly, servisní Python sada 4/4. Konfigurace
PlatformIO potvrzuje jen ESP-IDF; workflow YAML se parsuje. Závěrečné
nezávislé review oprav nehlásí otevřené nálezy. Git/CI předání je doložené
níže; native macOS kontrola je v tomto tasku vynechaná na pokyn majitele.
Implementační commit `0c18faf56f39620186338f460158d48ab41b7829` vznikl po
ověření, `Push-CurrentBranch.ps1` uspěl a read-back remote potvrdil stejnou SHA.
PR [142](https://github.com/Blato58/DildoVander/pull/142) předal implementaci
i review opravy a je sloučený; přesný merge a ověření jsou uvedené níže.
První CI `37878086941` odhalilo GCC `-Werror=sign-compare` v JPEG parseru;
počet komponent je nyní `size_t`, stejně jako délka segmentu. Následný
lokální `pio run -d kufr/firmware -s` prošel. Následný Codex review PR
našel pět P2 nálezů: ztracené vlastnictví náhradního rozhovoru, ztracený
DELETE klíč po timeoutu, neurčitý zápis ruční opravy, prázdný foto popis a
neúplné odstranění cue. Opravy zachovávají vlastněné ID a DELETE klíč až do
204; nejednoznačný PUT se opakuje se stejným payloadem/korelací/klíčem,
opakovaná chyba blokuje úvod a uklidí sezení. Prázdná ruční oprava ponechá
jen replayable potvrzení, odstraní aktivní cue a zruší image polling;
spotřeba a vymazání rozpracovaného provideru se dokončí. Foto výsledek
vyžaduje neprázdný popis. Regrese zahrnují čtyři režimy iOS a následující
Start po neúspěšném úklidu. Firmware už DELETE klíč i čekání na úklid
zachovával; cílené review nepotvrdilo potřebu jeho změny.
Po opravách znovu prošel celý backend runner, cílené image testy, HTTP
smoke a kontrakty; mobil 28/28 testů, build a skutečný HTTP fixture
self-check prošly. Swift provedení nových regresí se nespouštělo.
Nezávislé review navíc odhalilo expirovaný access token při pozdějším Stop:
cleanup obnoví token na 401, ponechá stejný DELETE klíč/ID a při selhání
obnovy zachová vlastnictví pro další pokus. HTTP fixture ověřil obnovu,
selhání a další úspěšný Stop i uchování obnoveného tokenu přes další 503.
Synthetic credential fixture nyní odpovídá skutečnému Bearer requestu;
produkční autentizační kontrakt se nemění.
Závěrečný nezávislý source review backendu, kontraktů a iOS těchto oprav
nepotvrdil žádný zbývající actionable nález; sám nespouštěl buildy/testy.
Trvalé pravidlo v tracked `AGENTS.md` vyžaduje načtení review/komentářů
po posledním push a před každým merge; nevztahuje se jen na tuto větev.
CI `37878589564` před těmito review opravami prošlo všechny vybrané
projekty. Stažený firmware balíček prošel SHA-256 ověřením a obsahuje
jen `esp32-s3-idf/` (bootloader, firmware, partition, OTA data a ELF).
Opravy a trvalé pravidlo jsou v commitu `ab28dd05bb0872c79217561e5270eda64e2d3064`;
`Push-CurrentBranch.ps1` uspěl a ověřil stejnou remote SHA. Finální
[CI 37880643165](https://github.com/Blato58/DildoVander/actions/runs/37880643165)
prošlo backend, web, offline AI eval, Linux hostitelské firmware testy,
ESP-IDF a všechny servisní diagnostiky, repository checks a CI required.
Placený live eval byl správně vynechaný. Po posledním push a těsně před
merge byla znovu načtená všechna reviews, inline vlákna i Codex summary;
pět opravených Codex vláken je resolved, review nebylo rozpracované.
PR 142 je sloučený jako `6cb130d542e2eb2d0ff41a6cde05c7f47adbdc30`;
fetch cíle potvrdil Git tree `1d8710572af596e28ff94e2e954d4a8e7d5ec74a`,
shodný s otestovanou verzí `ab28dd0`.
Majitel v tomto tasku výslovně zadal „skip ios ci“. Nativní `kufr-ios-sign.yml`
se nespustil; nové Swift regrese, UIKit kompilace, podpis a iPhone runtime
nejsou tímto předáním ověřené. Web/mobile Node testy nejsou native CI.
Neprovedeno: placená inference, cloudové nasazení, publikace IPA ani flash.
E12/E13 nejsou celé hotové: kamera ještě není doručená; revize/FPC/piny,
napájení/proud/souběh, skutečný obraz/latence, iPhone kamera/pozadí a SD
zůstávají samostatnými akceptacemi. Další konkrétní krok: po doručení kamery
servisně změřit a povolit profil. Nativní ověření navázat až po novém pokynu
majitele; provider evaluace a fyzický flash vyžadují svou původní autorizaci.

**9. 10. 2026 – synchronizace PR před CI.** Převzatý slice přidává
API-only workflow `pr-sync.yml` a `scripts/ci/pr-freshness.cjs`: při PR
události nebo push do `main` nejprve aktualizuje zaostávající PR z cíle,
potom explicitně dispatchuje CI na nové head/base SHA. Draft/fork PR
nemění; konflikt hlásí. Writer běží na dočasném GitHub runneru a čte
jen výchozí větev. CI odmítá starý head/base před buildy a zachovává
výběr dotčených projektů. Balení/promotion podporuje PR dispatch bez
oslabení důkazu sloučeného PR a shody stromu s aktuálním `main`.
Upraveny CI, skripty a testy, CI/CD návod, README a CONTRIBUTING.
Lokálně prošel připnutý actionlint 1.7.12 se SHA-256 kontrolou a 55 Node
testů včetně polling/concurrency regresí. Python: 16/17;
dosavadní POSIX executable-mode test na Windows selhává (0666 vs 0755),
nových pět packaging testů prošlo. Linux CI ověřilo všech 17 Python
testů i 55 Node testů; repository checks, web, backend, host testy
firmware a všechny firmware profily i CI required prošly v běhu
[37876637730](https://github.com/Blato58/DildoVander/actions/runs/37876637730).
Implementace `859201b` a finální `ca42330` byly commitnuty; push
ověřil remote SHA. PR [140](https://github.com/Blato58/DildoVander/pull/140)
bylo sloučeno jako `06661ce`; cílový Git tree odpovídá otestovanému stromu.
První živý CI běh odmítl zaostávající větev před buildy, protože mezitím
přibylo `30fc338` do `main`; cíl byl přimíchán před úspěšným CI.
Živý main-push controller
[37877486084](https://github.com/Blato58/DildoVander/actions/runs/37877486084)
sám aktualizoval dokumentační PR 141 z `592ff84` na `06533a9` a spustil
[CI 37877504456](https://github.com/Blato58/DildoVander/actions/runs/37877504456)
na nové SHA. Freshness a CI required prošly, všechny buildy se správně
vynechaly. Výsledek a hranice jsou v [záznamu ověření](../../docs/pr-sync-validation.md).
Předání záznamu je v PR [141](https://github.com/Blato58/DildoVander/pull/141);
finální commit a stav merge jsou dohledatelné v uvedeném PR. Nasazení ani AI/HW test
se nespouštěly. Další krok tohoto slice: sledovat synchronizaci běžných PR;
software a fyzická akceptace kufru pokračují podle D156 níže.

**D156 – 9. 10. 2026 – zjednodušená architektura podle majitele, bez doplňkových pohonových modulů.**
Majitel výslovně odstranil externí watchdog, hardwarový heartbeat přes I2C,
nezávislou inhibit logiku, elektronický high-side switch i 12 koncových
spínačů. Požaduje 12V PD z UGREEN C1, původní mono SD/GAIN obou DFR0954
bez odpájení R1, softwarový mute a individuální dobu serv místo pevné
500ms meze. Toto rozhodnutí **nahrazuje D155 a starší požadavky na tyto
doplňky**; neobjednávat je a neimplementovat jejich podporu.

Majitel následně potvrdil stávající ESP32-S3 s konektorem OV5640
(Drátek 232701) a samostatně objednanou kameru; zatím má pouze konektor,
modul čeká na doručení. Objednávka, kamera a kabelový plán tuto informaci
zohledňují. Druhá ESP deska/sada se ze zbývajícího nákupu odstranila,
starý součet 500 Kč se ruší. Kamerový driver E13 nadále zbývá.
Toto následné upřesnění mění pouze dokumentaci; ověřeno 39 lokálních
Markdown odkazů v pěti dotčených dokumentech a `git diff --check`.
Upřesnění kamery je v commitu `381c2d4dcceb35a348e6c78ac25ad3d794a0e8b6`,
push proběhl a read-back remote potvrdil tuto SHA. PR
[138](https://github.com/Blato58/DildoVander/pull/138) je sloučený;
finální CI a cílová větev jsou ověřené níže.
Na následný pokyn upravit přímo „Zapojení potvrzeného hardwaru“ dokument
výslovně doplnil jeden pohon celkem, nepřekrývající se servo/vibraci,
pasivní držení polohy a inicializaci OE HIGH / OUTNE=00 / všech 16 FULL_OFF.
Jde o dokumentaci už implementovaného chování; nové HW měření neproběhlo.

Majitel následně potvrdil, že starou větev už v zapojení nevidí.
Z tohoto záznamu jsou odstraněné také zastaralé instrukce D155/D153/D147
k dalším pohonovým modulům a starší B1 blokace na jejich měření.
Aktuální postup je pouze D156 a přímé OE/kolébka.

Na nové zadání CI používá path filtr: samotné `docs/` a Markdown/rst
workflow nespouštějí. `scripts/ci/select_projects.py` vybírá backend,
firmware, web nebo offline AI eval z úplného diffu PR; sdílené kontrakty,
simulátor a balení spustí jejich skutečné spotřebitele. Nesouvisející joby
se vynechají a závěrečná kontrola odmítne chybný/neočekávaně vynechaný
vybraný projekt. Ruční CI nabízí výběr projektu pro obnovu artefaktu.
AI eval volaný z CI zůstává offline také při ruční obnově; placený job
se smí spustit pouze přímým dispatch vyhrazeného AI eval workflow.
PR obsahující změny kódu se při dalším commitu vyhodnocuje jako celek,
takže dokumentační commit do takového PR stále ověřuje jeho kód.
CI návod, CONTRIBUTING a README tomu odpovídají; neaktivní šablona rulesetu
nemá univerzální povinný status, který by blokoval dokumentační PR.
Živá nastavení GitHubu se neměnila (rulesets API opět 403 kvůli tarifu).

Lokální ověření CI změny: šest nových testů výběru projektů/rename/gate
a šest dosavadních testů balení, celkem **12 passed**. Připnutý actionlint
1.7.12 ověřený SHA-256 zkontroloval všechny workflow bez chyby.
Dosavadních 29 Node testů promotion/provenance také prošlo; ověřeno
111 lokálních Markdown cest v celém diffu a `git diff --check`.
Finální předání: push implementace/CI `c236fdb981abb5d0cf7b3e796b76661be02013f7`
byl ověřený read-back remote. Mezitím do main přišel samostatný Vite PR 135;
aktualizace větve z `fa81390` proběhla bez konfliktů, výsledný commit
`783a9558b8c90fb05e5e41868098c7f7a4afa538` byl pushnutý a ověřený.
Firmware, CI skripty a workflow při této aktualizaci zůstaly identické.
[Finální CI 37874983422](https://github.com/Blato58/DildoVander/actions/runs/37874983422)
**SUCCESS**: výběr projektů, repository checks, firmware build/host tests,
offline AI eval i CI required. Backend/web a placený live eval se správně
vynechaly. Původní plné CI 37872574618 také prošlo všemi projekty.
PR 138 byl 9. 10. v 02:41 UTC squash-merged do main jako
`3d0e73afdb6c79e16d5d4e2df0be63034edbcbc0`; fetch a `git diff --exit-code`
potvrdily totožný obsah s otestovanou větví. Git tree je
`d9005e2d4a91afbf1cde2233a29fa9f4526d962c`.
Tento následný záznam předání mění pouze dokumentaci; kód se nemění.
Povinný PowerShell push helper není v Linux prostředí spustitelný;
všechny pushe proběhly repo-scoped credential fallbackem uvedeným níže.
Nenahrávalo se do desky, nenasazoval backend ani nevolala placená AI.

Aktuální [zapojení](zapojeni-potvrzeneho-hardwaru.md): GPIO2 přímo PCA OE,
HIGH vypnuto, pull-up kolem 10 kΩ (dva koupené 22kΩ paralelně = 11 kΩ).
Zelená kolébka přímo odpojuje 5V pohony, zem společná. Stav kolébky se
nesnímá. MCP0x20 používá pouze GPA0 pro bílé tlačítko; ostatní vývody volné.
PCA0x40: serva 0–5, U059 6–11, ostatní FULL_OFF. RGB48 přes Pixel Shifter,
36 LED, šest na přihrádku, jas 64/255. Audio B 21/41/42/47; SD38–40 a
kamerové piny zůstávají volné. Měnič SBC-Buck02 / SEG-28582: 12V vstup,
5V výstup, udávané 5 A; současně jeden pohon a jeden LED úsek.

Firmware odstranil pulzní úlohu a příslušné GPIO/flags, čtení koncových
kontaktů, mute zápisy do MCP i povolení závislé na přídavných dílech.
NVS servisní konfigurace má jen `profile_b_ok`, revizi a šest kalibrací.
Servo okno je individuální 20–3000 ms, softwarová mez čas + 250 ms;
ruční cyklus do 15 s, autonomní čekání na zdvih/návrat podle kalibrace.
Kontrola prodlevy řídicí smyčky 250 ms a vibrace 150 ms zůstává.
Před změnou kanálů a při I2C chybě jde přímé OE HIGH, potom FULL_OFF.
Žádný automatický návrat po Stop; další pohyb až po ruční kontrole
všech přihrádek dole a 2s přidržení tlačítka. Normální časovaný návrat
uzavírá journal a obnovuje dostupnost, **poloha je předpokládaná**, ne
měřená; `physicalVerification: unavailable` zůstává pravdivé.

Zesilovače mají tovární mono konfiguraci. Software mění PCM hlasitost
(25 %), Stop uzavře přísun nového PCM, auto-clear DMA a drain vytvářejí
ticho. Již zařazený zvuk může doběhnout, boot/resetové ticho není
zaručené. Síťové heartbeat pro ruční lease se nemění: nejsou to odstraněné
hardwarové pulzy. AGENTS, plán, napájení, objednávka, README a CAD
inventární metadata odpovídají novému zadání; CAD geometrie se nemění.

Ověření dosud: nový fyzický host test prošel (jeden pohon/úsek, limity,
I/O chyba/prodleva, kalibrace, journal před pohybem, autonomní i ruční
návrat, replay/Stop/přerušení ukázky i návratu, chyba uzavření journalu,
1,5s pohyb v obou režimech). Audio A/B stub test a tři testy servisního
JSON prošly. Finální `pio run -d kufr/firmware` (ESP-IDF 6.1 /
espressif32 7.1.3) **SUCCESS**, RAM 70 192 B, flash 1 248 353 B;
Arduino sériový build **SUCCESS**. Servisní projekt s vypnutým příkladem
**SUCCESS**, flash 170 957 B. Dosavadní firmware host sada z cloudového
setupu prošla včetně 52 skutečných výstupních zpráv proti v1 schématu.
Ověřeno 91 lokálních Markdown cest, `git diff --check` a Python syntaxe
CAD metadat. Implementace je v commitu `f2be877b34f41065fda8aeeda08027f77611e67c`
na `codex/kufr-hardware-firmware-2026-10-09`, push úspěšný a remote
read-back potvrdil shodnou SHA. Povinný `./scripts/Push-CurrentBranch.ps1`
se v Linux cloud prostředí nespustil (exit 126, PowerShell není dostupný);
použitý fallback `git -c credential.helper= -c
'credential.helper=!gh auth git-credential' push --set-upstream origin
codex/kufr-hardware-firmware-2026-10-09` využil přihlášený účet Blato58
pouze pro tento příkaz, bez změny globálního účtu. Finální CI/merge jsou
ověřené výše.

Fyzické zapojení, kalibrace, proudy, teplota, napájení ESP, GPIO2/OE reset
a skutečné I2S/DMA nejsou tímto ověřené. Při úplném zamrznutí může pohon
zůstat aktivní; vypnutí zajišťuje zelená kolébka. Bez snímačů se zaseknutí
serva nebo pohyb bez napájení polohově nezjistí. Další krok: proměřit 12V
PD/5V napájení/OE, kalibrovat jednu přihrádku a potom zbylé, zkontrolovat
pasivní držení a mechanické dorazy, následně samostatně autorizované
servisní nahrání a fyzická akceptace. Kamera/SD driver E13 zbývá.

**D155 – 9. 10. 2026 – původní návrh adaptéru; nahrazen D156.**
Původní návrh fyzických efektů a jeho tehdejší fake/build ověření byly
nahrazené zjednodušenou implementací D156. Staré zapojení a požadavky
na další elektroniku jsou odstraněné z tohoto přehledu; verze zůstává
v Git historii v commitu `519631d`. Aktuální piny, kalibrace, ověření
a zbývající fyzická měření jsou pouze v D156 a
[zapojení](zapojeni-potvrzeneho-hardwaru.md).


**Historické D154 – 8. 10. 2026 – první tiskový CAD, nahrazený F1.**
Tehdejší kloubové mechanismy, rozmístění elektroniky, generátory a lokální
exporty byly nahrazeny [F1](../mechanika/final/README.md). Nejsou aktuálním
výrobním podkladem. Historický implementační commit
`1d61ce7bebbaca5ed54533a7ba2bf99d8ad71729` byl ověřený a pushnutý na
`codex/printable-case-final-electronics`; tehdejší testy ani CAD neprokázaly
fyzický tisk, usazení, zatížený pohyb a zadržení při výpadku. Podrobný
původní záznam a zdroje zůstávají dohledatelné v historii Gitu.

**D153 – 8. 10. 2026 – revize minimální objednávky, dokumentace ověřená.**
Majitel žádá ponechat jen skutečně potřebné díly. Tento starší nákupní
návrh nahrazuje potvrzený inventář a zapojení D156. [Objednávka](../../docs/objednavka-doplneni-kufru-2026-10-06.md)
má nyní základ pro jeden již koupený zesilovač/reproduktor, pájené
kabely a kameru + MCP podle rozhodnutí 6. 10. Druhý zesilovač/PH2 kabel,
Grove/JST-SM adaptéry a SD/čtečka jsou volitelné. Rezistory sníženy na
100kΩ ×10, 15kΩ ×5, 22kΩ ×5. Servisní kolébka, snímání a výchozí stavy
zůstávají potřebné. Zdroj lze vynechat jen při vlastním vhodném 20V PD.
Součty z orientačních cen 6. 10.: Botland 764/377 Kč, Drátek 765 Kč,
celkem 1 529/1 142 Kč bez dopravy; úspora 470 Kč vůči starému základu
nebo 1 392 Kč proti objednávce se všemi doplňky.

Tehdejší požadavek doplňkové pohonové větve a úprav SD/mute je zrušený
rozhodnutím D156. Aktuálně je OE připojené přímo k ESP32, zelená kolébka
odpojuje napájení a zesilovače zůstávají v továrním stavu.
Změněna jen objednávka, úvod historického seznamu a dokumenty napájení,
plánu, kamery a stavu. Firmware, zapojování, nákup ani nasazení neproběhly.

Ověření: primární dokumentace TI/Pololu/DFRobot a cílené nabídky,
kontrola součtů a vyřazení odložených položek ze základních tabulek,
51 lokálních Markdown cílů v šesti dokumentech a `git diff --check`.
Nezávislý read-only HW audit a kontrola kombinovaného diffu nenašly
závadu. Nové ceny/sklady všech položek, firmware build a fyzické měření
nebyly ověřované. Další krok: montážní trasy, konkrétní mute rozhraní,
revize kamery a měření jedné přihrádky podle nynějšího zapojení D156.
Dokumentační slice je v commitu `bb51ec127b1eac063ca0918d7a2d4ecdc67f3809`
na `codex/trim-kufr-purchase-list`. Povinný
`scripts/Push-CurrentBranch.ps1` uspěl přes účet Blato58 a read-back
remote ref potvrdil shodnou SHA. Tento následný záznam eviduje výsledek
push; CI/merge se ověří při předání PR, fyzická akceptace tím nevzniká.

**D152 – 8. 10. 2026 – SSH, VPS a uložené cloudové prostředí aktualizované.**
Majitel autorizoval Windows SSH přes alias `vps-dildovander` / `root`,
prohlídku služeb a logů i změny serverového prostředí; výslovně požaduje
také aktualizaci uloženého Codex cloudového prostředí DildoVander.
Rozsah je provozní konfigurace a trvalý návod, bez nového aplikačního chování.
`AGENTS.md` a `backend/codex/README.md` nyní určují SSH postup, zdrojové
vlastnictví, pipeline, ochranu credentialů a cloudový fallback.

Výchozí čistý checkout `00aba500d10e8c40b865f042bf91baac64a7f93b` byl
35 commitů za `origin/main`; bezpečný fast-forward jej aktualizoval na
`9d5ef15071773c45186f9fa6ea2012f3206b19a6`. VPS používá release
`8ddfe652d840f96ff7ed42b4d5698fbb725517ca-37602422528-1` z úspěšného
deploy běhu D151. Build source tree odpovídá squash `e0d21e8` a backend
subtree aktuálního main je shodný; různé commit SHA nejsou drift aplikace.
Služby backend, nginx a GitHub runner jsou active/running, loopback
`GET /healthz` vrací `{"status":"ok"}`. Systemd unit odpovídá zdroji.

Skutečný drift byl starší instalovaný aktivační helper:
SHA-256 `79d627f2b63b4485593e0d326820e46b92d0e0bb965b638a1ed5dda059aaa31f`.
Současný helper byl připraven přes úspěšný
[bootstrap source běh 37720823840](https://github.com/Blato58/DildoVander/actions/runs/37720823840)
z `9d5ef15`, porovnán se zdrojem a atomicky instalován jako root/root 0755.
Nový hash je `c55965ee5fcc4b3e6cf2880c311a65a52d2fa1224551cfba2a0b50a6667fed13`.
Soukromá záloha je
`/var/backups/kufr-backend/20261008T030507Z/kufr-backend-deploy.before.py`.
Ověření před/po potvrdilo shodný service PID/restart stav, environment
fingerprint, aktivní release a identitu SQLite souborů; health zůstal OK.
Helper nebyl spuštěn s `activate`, serverové credentialy ani stávající
limity se nepřepisovaly. Budoucí aktivace už použije současné pravidlo
session cap nejvýše 3 USD a bez periodických capů podle aktuálního zdroje.

Lokální ověření: všechny 3 existující `test_deploy_helper.py` testy
passed na Pythonu 3.14.4, syntax compile, 27 lokálních Markdown cílů
ve třech změněných dokumentech a `git diff --check` passed.
Uložené cloudové prostředí DildoVander zachovává dosavadní install script,
vývojové instrukce, síťovou politiku i private scope. Start skill doplňuje
produkční postup a environment obsahuje netajný `KUFR_BACKEND_URL`.
Konfigurace byla uložená a znovu publikovaná; UI potvrdilo
`Environment published` / `Published`. Publikace neověřuje nové SSH
credentialy ani end-to-end čtení z nové cloudové VM.
Přímá cloudová SSH identita zatím není provisionovaná; Windows agent
cloud nepřebírá. Bez ní se používá GitHub diagnostika.
Provozní návod a záznam byly commitnuté jako
`6d7946c890b13272f6abed40365ee323121d01d9` na `codex/vps-cloud-operations`.
Povinný `scripts/Push-CurrentBranch.ps1` uspěl přes účet Blato58;
read-back remote ref potvrdil shodnou SHA. Tento následný záznam uchovává
skutečný výsledek předání, bez změny aplikačního kódu.

Další konkrétní krok pro přímý cloudový SSH přístup je samostatné
provisionování identity a TCP oprávnění; nyní nevznikly nové credentialy.
Placená inference, iPhone/HW akceptace a nové nasazení aplikace neproběhly.

**D151 – 7. 10. 2026 – Live časová metadata, výstupní hranice a diagnostika pádů nasazené.**
Majitel hlásí občasný `ai_unavailable` a sdílí screenshot s fragmenty
hostova přepisu. Slice navazuje na `main=6f81a430c7d069c4d5adddf5691cefa1cc30793d`
([PR #131](https://github.com/Blato58/DildoVander/pull/131), CI 37544355140 success),
záznam skutečného nasazení D150. Bez cizích lokálních změn.

Read-only [log 37597966641](https://github.com/Blato58/DildoVander/actions/runs/37597966641)
zachytil 7. 10. dvě chyby během hovoru: operace
`4d93a54f-5a41-4b18-9cd7-5c410865327f` v 08:57:05 UTC po 125,924 s a
`b2017813-efd1-46cd-b3fc-cdb5788f9d5c` v 09:01:59 UTC po 66,455 s.
Obě mají HTTP 200 stream s `realtime_failed/ai_unavailable`; staré logy
neobsahují výjimku ani úspěšné decision/terminal události. Chybějící
completed nedokazuje, že rozhodovací request nezačal. Mezi nimi dva
jiné skutečné hovory dokončily validproposal za 2661,2/2096,4 ms,
lokální `/playback` 204 a completed. Nejde tedy o doloženou globální
nedostupnost. Store `5d9d36ab-ec60-4106-9034-7c8c0f34c678`, cursor 4837,
hasMore/truncated=false a droppedWrites=0. Konkrétní příčina obou
historických pádů zůstává neprokázaná.

Nalezené hranice: `GetInt64` odmítal i číselně platný desetinný JSON
zápis časových metadat; rozhodovací schema nevyjadřovalo dosavadní
omezení délek citace/faktu a počtu důvodů. `GptLiveProtocol` a
`LiveConversationProjection` nyní přijímají konečné číslo 0–200000 ms,
uchovají přesnost a dál odmítají neplatné/obrácené časy před změnou
podkladů. SQLite archiv zvládá přesné hodnoty bez migrace.
`WandDecisionService` zadává podporované string patterns a maxItems,
plus stejné stručné limity v instrukci; grounding a validace zůstávají.
`LiveFailureDiagnostics` a endpoint zapisují jen aplikací definovanou
kategorii `live_failure_detail`, nikdy text výjimky, provider body či
libovolný code/type/message. `AutonomousLiveDialogue` zaznamená
`decision_running` a timing/error také při výjimce práce.

Lokální ověření 7. 10. bez placené inference, iPhone nebo HW:

| Kontrola | Skutečný výsledek |
| --- | --- |
| Release backend-tests, SDK 10.0.303, linux-x64/self-contained | 0 warnings / 0 errors |
| cílené AutonomousLive + HybridMemory | passed; včetně validního číselného zápisu, chybných metadat, selhání práce/provideru a ochrany obsahu |
| kompletní C# backend-tests | všechny skupiny passed; včetně skutečné SQLite persistence desetinných časů a idempotence |
| dokumentace a whitespace | 54 lokálních Markdown cílů OK; `git diff --check` OK |

Backend README, provozní návod, Codex diagnostika a plán popisují hranice
a interpretaci nových záznamů. Model Luna, Vector Stores, oprávnění,
automatický výběr bez konfirmace, klientské kontrakty a fyzická brána
se nemění; nevzniká placený replay nejasného requestu.

Commit `bc07964941a8414f49561024f715aeba24bd3ad6` byl pushnutý na
`codex/live-intermittent-failure`; `ls-remote` potvrdilo stejnou SHA.
Povinný PowerShell helper skončil na Linuxu 126; repo-scoped
`gh auth git-credential` fallback uspěl bez změny globálního účtu.
[CI 37601258099](https://github.com/Blato58/DildoVander/actions/runs/37601258099)
prošel všechny povinné kontroly: repository, backend včetně C# a HTTP
smoke, web, firmware host/build, offline AI a CI required. Placený
AI live skipped. Po kontrole diffu, konfliktů a aktuálního headu byl
[PR #132](https://github.com/Blato58/DildoVander/pull/132) sloučen squash
v 09:41:38 UTC jako `e0d21e81bb730887741786ee29a84fe0cbf27951`.
Fetch i shodný source/main tree `1faf24dcf9fcd9a29798ef5849ff5adf79dad8b8`
byly ověřené; checkout byl čistý.

Autorizované nasazení ze sloučeného main:

```sh
gh workflow run kufr-backend-deploy.yml --repo Blato58/DildoVander --ref main \
  -f build_run_id=37601258099 -f dialog_mode=responses -f dialog_model=gpt-6.1-sol \
  -f voice_mode=openai -f live_actor_mode=AutonomousActor -f live_decision_model=gpt-6-luna \
  -f live_terminal_quiet_ms=1500 -f hybrid_memory=true -f cloud_indexing=true
```

[Deployment 37602422528](https://github.com/Blato58/DildoVander/actions/runs/37602422528)
skončil success ve verify i deploy. Provenance a aktuální main prošly.
V 09:42:56 UTC se aktivoval release
`8ddfe652d840f96ff7ed42b4d5698fbb725517ca-37602422528-1`; loopback health
check passed. Models GET ověřil přístup Luny bez inference. Veřejný
HTTPS, admin auth/no-op/revision conflict, skutečné actor/model/quiet
a paměťové nastavení i scope/přístup obou stores prošly bez exportu hosta:

```json
{"cloudIndexingEnabled":true,"liveActorMode":"AutonomousActor","liveDecisionModel":"gpt-6-luna","liveTerminalQuietMs":1500,"longTermPersonalEnabled":false,"memoryEnabled":true}
```

Navazující read-only [log 37602604160](https://github.com/Blato58/DildoVander/actions/runs/37602604160)
v 09:43:57 UTC potvrdil Responses/OpenAI available, activeConversations=0,
stejný store a cursor 4850, hasMore/truncated=false, droppedWrites=0.
Neobsahoval nový Live rozhovor po aktivaci. Nový iOS build tato serverová
změna nevyžaduje. Odstranění obou historických pádů, skutečný poslech
a HW akceptace nejsou tímto ověřené. Další konkrétní krok: při opakování
chyby dohledat `live_failure_detail` a decision timing podle stejné
operace, už bez hádání z veřejného `ai_unavailable`.

**D150 – 6. 10. 2026 – automatický verdikt bez potvrzovací otázky nasazený.**
Majitel sdílel iPhone Debug screenshot: návrh přihrádky 4 už byl
zveřejněný jako simulace, závěr ale skončil otázkou „Je to trefa, nebo
budeš fňukat?“. Požaduje prostě vybrat bez konfirmace. Práce navazuje
na `main=029f45003df9f44822a1e0f80f028eb037f04a27`, záznam nasazení
D149 v [PR #129](https://github.com/Blato58/DildoVander/pull/129), jehož
[CI 37541442322](https://github.com/Blato58/DildoVander/actions/runs/37541442322)
prošel všechny povinné kontroly. Merge a aktualizovaný checkout byly ověřené.

Read-only [log 37541737240](https://github.com/Blato58/DildoVander/actions/runs/37541737240)
potvrzuje skutečný průchod D149 v rozhovoru
`bc0f73b8-f0cc-4e66-8d56-0d4486edc267`: validproposal za 2389,1 ms,
`awaiting_playback` v 22:35:53 UTC, HTTP `/playback` 204, `closing`,
completed a HTTP 200. Celý rozhovor trval 62,775 s; nejedná se o dobu
samotného rozhodnutí. Snapshot neměl truncated ani droppedWrites.
Screenshot dokládá návrh pro simulátor, nikoli skutečný pohyb serva.
Číslo iOS buildu se z něj nedá určit; D146 publikovaný build je 1000.0.16.

Příčinou otázky je neúplná instrukce k závěru: Ollivanderova Live
adaptace a terminální pokyn zakazovaly jen další diagnostickou otázku.
`LivePromptSnapshot` v3, samostatná Live adaptace a instrukce v
`AutonomousLiveDialogue` před oznámením i opravou nyní vyžadují celý
verdikt oznamovacími větami, také závěrečné rýpnutí či callback.
Nežádají hodnocení, souhlas ani odpověď a po závěru mlčí. Platný návrh
se dál publikuje automaticky bez hostova potvrzení; dobrovolná
podstatná oprava dál zneplatní starý výsledek. Původní zdrojové persony
a jejich hash, počet otázek, model, paměť, kontrakty, audio/drain
a fyzická brána se nemění. Žádná nová podmínka na otazník neblokuje
publikování nebo nevynucuje další modelové volání.

Lokální ověření 6. 10. bez nového placeného hovoru a bez HW:

| Kontrola | Skutečný výsledek |
| --- | --- |
| Release backend-tests, SDK 10.0.303, `--no-restore -r linux-x64 -p:SelfContained=true` | 0 warnings / 0 errors |
| existující `--autonomous-memory` skupiny | frozen prompts/policy, dlouhý český vstup, delegace, opravy, playback/native wire a hybrid memory passed |
| terminální instrukce | 427 UTF-8 bytes; kompletní obsah se vejde do konzervativního append limitu 468 bytes |
| Markdown a whitespace | 53 lokálních cílů včetně stavu OK; `git diff --check` OK |

Plán, research, provozní návod a backend README popisují výsledné
chování. Commit `1ee5fd151aa739be83380a730dd945aab005f825` byl pushnutý
na `codex/live-declarative-verdict`; `ls-remote` potvrdilo stejnou SHA.
Povinný PowerShell helper skončil na Linuxu 126; repo-scoped
`gh auth git-credential` fallback uspěl bez změny globálního účtu.
[CI 37542611420](https://github.com/Blato58/DildoVander/actions/runs/37542611420)
prošel celý: repository, backend včetně kompletních C# skupin a HTTP
smoke, web, firmware host, čtyři firmware profily, offline AI a CI required;
placený AI live skipped. Dočasné HTTP 401 přerušilo CLI watch, nikoli CI.
Připojený GitHub potvrdil všechny joby a po kontrole konfliktů/headu
sloučil [PR #130](https://github.com/Blato58/DildoVander/pull/130) squash
jako `9486b084988f2de35b9e32537d3d00d9604a8e6c`.
GitHub API potvrdilo stejný tree source/main
`d02877753e9d66d686355072ed87676b34cee2f9`; po obnově CLI přístupu také
`gh run view` potvrdilo success a skutečný `git fetch origin main` uspěl.

Spuštěná aktivace ze sloučeného main:

```sh
gh workflow run kufr-backend-deploy.yml --repo Blato58/DildoVander --ref main \
  -f build_run_id=37542611420 -f dialog_mode=responses -f dialog_model=gpt-6.1-sol \
  -f voice_mode=openai -f live_actor_mode=AutonomousActor -f live_decision_model=gpt-6-luna \
  -f live_terminal_quiet_ms=1500 -f hybrid_memory=true -f cloud_indexing=true
```

První pokus ještě vrátil HTTP 401 a nic neaktivoval; po obnově přístupu
[deployment 37544055582](https://github.com/Blato58/DildoVander/actions/runs/37544055582)
skončil success ve verify i deploy. Kontrola provenance a aktuálního
main prošla; v 23:01:12 UTC se aktivoval release
`53b8a6351bbd46db537aa3816b7661b4a9263755-37544055582-1` a loopback
health check passed. Metadata GET ověřil přístup k `gpt-6-luna` bez
inference. Veřejný HTTPS a skutečný actor/model/quiet/memory/cloud/personal
i přístup/scope obou provisionovaných stores prošly:

```json
{"cloudIndexingEnabled":true,"liveActorMode":"AutonomousActor","liveDecisionModel":"gpt-6-luna","liveTerminalQuietMs":1500,"longTermPersonalEnabled":false,"memoryEnabled":true}
```

Ověření také prošlo admin auth, no-op mode mutation a revision conflict;
neprovedlo nový hlasový/Responses hovor, změnu personality ani export
hosta. Další stores se nevytvářely. Nové instrukce jsou snapshotované
při založení rozhovoru: stačí spustit nový GPT-Live hovor, nový iOS build
tato serverová změna nevyžaduje. Poslech nového verdiktu v3 a fyzická
akceptace nejsou tímto slice prokázané. Dalším krokem je skutečný
rozhovor a nezávislé ověření pohybu na HW, mimo Debug simulátor.

**D149 – 6. 10. 2026 – oprava nedokončeného Live výběru a předčasné řeči nasazená.**
Majitel během skutečného testu D148 potvrdil lepší/rychlejší rozhovor,
ale hůlka se nikdy neprovede a kufr jej nenechá domluvit. Práce navazuje
na `main=f71a7e3dc0facdb9157cb2d967116803f25a5249` (D148 záznam,
[PR #127](https://github.com/Blato58/DildoVander/pull/127), CI 37535613761 success).
Read-only [logy 37536098927](https://github.com/Blato58/DildoVander/actions/runs/37536098927)
ukázaly tři validproposal v rozhovoru `53fdafc7-8de9-4c15-a949-4860eff20dc5`,
trvání delegace 2655,6 / 1473,3 / 1878,0 ms, žádný `/playback` ani completed,
poté ai_unavailable. Jiný rozhovor skončil 499 bez zaznamenaného
rozhodnutí. Snapshot v 21:51:38 UTC měl 0 aktivních rozhovorů a žádné
droppedWrites/truncated. Technický log neobsahuje přepis; sám neurčí
konkrétní blokující slovo. Kód ale vyžadoval celý nominativní název
a poslední tečku/vykřičník a invalidoval návrh při obyčejném input fragmentu.

`LiveTerminalSpeech` identifikuje jedinečný titul ze stejného katalogu,
normalizuje diakritiku/mezery a omezeně české koncovky. Nepouští jiný,
vymyšlený či nejednoznačný titul ani výslovně nedokončenou pomlčku/výpustku.
Nejasný dokončený závěr po klidu může jednou krátce opravit; nový skutečný
přednes a drain jsou povinné. `LiveInputActivity` sleduje RMS mikrofonu
a `InputQuietMs=1200`, odděleně od output quiet=1500. Blokuje předčasnou
práci, dodání hotového modelového výsledku i uzavření přehrání; vrácený
výsledek se před dodáním znovu validuje. Dlouhé pokračování hosta
nevyvolá zotavení chybějícího kontextu ani terminální timeout.
Input fragment nejprve pozastaví publikování. Stabilní čisté přijetí,
poděkování či opakování nevytvoří nové výběrové podklady/modelové volání;
nová preference/alternativa/zrušení návrh zneplatní. Dělené „Do“ + „bře,
ukaž ji“ zachová platný návrh s aktuální konverzační revizí. Společné
Backchannel/Interruption instrukce v `autonomous-live-v2` předávají hostovi
celý tah a žádají backendové ověření před oznámením konkrétní hůlky.
`live_terminal_state` zapisuje technickou fázi bez přepisů/audia/klíčů.
Kontrakty klienta a místní validace fyzického návrhu se zachovávají.

Ověření 6. 10. bez nového placeného provider hovoru a bez HW:

| Kontrola | Skutečný výsledek |
| --- | --- |
| Release backend-tests, SDK 10.0.303, `--no-restore -r linux-x64 -p:SelfContained=true` | 0 warnings / 0 errors; stejné uzamčené závislosti z D148 |
| kompletní C# backend-tests `--no-build --no-restore` | všechny skupiny OK; všech šest českých titulů, nesprávná/nejednoznačná hůlka, chybějící interpunkce, nový přednes po opravě, dělené přijetí bez dalšího modelu, dlouhá řeč nad timeoutem, návrat do řeči během rozhodnutí a během drain před přepisem |
| HTTP smoke | první lokální běh EPIPE; opakovaný kompletní průchod access/fake voice/večery/scope/restart passed; následný CI backend také success |
| backend/deploy Python | 20/20 + 11/11 passed |
| lokální Markdown / `git diff --check` | 53 lokálních cílů bez chyby; whitespace OK |

Plán, research, provozní návod a backend README popisují příčiny,
heuristiky a diagnostiku. Commit
`116e90436ef9a4ccf35adff6ffe0dc2a41f85ff5` byl pushnutý na
`codex/live-selection-turn-taking`; `ls-remote` potvrdilo shodu SHA.
Povinný PowerShell helper na Linuxu skončil 126; bezpečný repo-scoped
`gh auth git-credential` fallback uspěl bez změny globálního účtu.
[CI 37539528111](https://github.com/Blato58/DildoVander/actions/runs/37539528111)
prošel celý: repository, backend včetně HTTP smoke, web, firmware host,
všechny čtyři firmware profily, offline AI a CI required; placený AI live
skipped. [PR #128](https://github.com/Blato58/DildoVander/pull/128)
se po zelených kontrolách a ověření konfliktů sloučil squash jako
`20a7e6a3a917c0604d0056476769306428c75605`. Fetch ověřil stejný tree
headu a main `7ee6b5a87f31b757c6b2d772c707b2092de60efa`.

Skutečná aktivace ze sloučeného main:

```sh
gh workflow run kufr-backend-deploy.yml --repo Blato58/DildoVander --ref main \
  -f build_run_id=37539528111 -f dialog_mode=responses -f dialog_model=gpt-6.1-sol \
  -f voice_mode=openai -f live_actor_mode=AutonomousActor -f live_decision_model=gpt-6-luna \
  -f live_terminal_quiet_ms=1500 -f hybrid_memory=true -f cloud_indexing=true
```

[Deployment 37540866008](https://github.com/Blato58/DildoVander/actions/runs/37540866008)
skončil success v verify i deploy. Ověřil provenance/shodný main,
metadata GET dostupnosti Luny, bezpečné rozbalení a aktivoval release
`34ba374db630e761c0546fabb971046ba267e6b7-37540866008-1`.
Loopback i veřejný HTTPS, admin auth/no-op/revision conflict a skutečný
přístup/scope obou provisionovaných stores prošly. Produkční diagnostika:

```json
{"cloudIndexingEnabled":true,"liveActorMode":"AutonomousActor","liveDecisionModel":"gpt-6-luna","liveTerminalQuietMs":1500,"longTermPersonalEnabled":false,"memoryEnabled":true}
```

Nevytvářely se další stores a deployment nespustil nový hlasový/Responses
hovor ani export hosta. Nový backend obsahuje `InputQuietMs=1200` jako
konfigurační výchozí hodnotu. Pro nové instrukce založit nový rozhovor;
version/SHA snapshot se během běžícího rozhovoru nevyměňuje.
Verze testovaného klienta nebyla zatím sdělená; pro checkpoint je nutný
alespoň publikovaný iOS D146 build **1000.0.16**. Poslech opravy a skutečné
provedení hůlky/HW B1 nejsou doložené. Další krok je nový skutečný rozhovor
a korelovaná kontrola `live_terminal_state`, `/playback` a completed;
konverzační/technické fixtures nenahrazují fyzickou akceptaci.

**D148 – 6. 10. 2026 – autonomní GPT-Live a cloudový vektorový index aktivní.**
Majitel zadal zapnutí vektorové paměti a zkrácení čekání; dovolil změnu
modelu/architektury. Čistý checkout `work`, HEAD `db1e77e`, remote
Blato58/DildoVander. Při převzetí měla produkce D145–D146 provisionované
stores, legacy Live a cloud indexing=false; publikovaná IPA D146 umí checkpoint.
Read-only [logy 37530785394](https://github.com/Blato58/DildoVander/actions/runs/37530785394)
potvrdily responses/openai a 0 aktivních rozhovorů. Poslední Live HTTP
session trvala 82,7 s; logy neměří délku samotného rozhodnutí, proto to
není důkaz latence výběru. Ověřeny oficiální Live/delegation a GPT-6 Luna
dokumenty: client delegace nemá argumenty funkce; doporučená Luna má
Responses/function calling a reasoning=none. Live proto vede řeč sám,
samostatný `DecisionModel=gpt-6-luna` řeší jen faktický výběr; výchozí
Sol a textová cesta mají původní cenovou kontrolu. Luna má 512 výstupních
tokenů, rezervaci 10000 µUSD a konzervativní cache-write cenu; chybné
model/tier/usage/citace/dostupnost se odmítají. SDK a funkce `decide_wand`
zůstávají. Nový model nevrací hotovou repliku.

Workflow přidává samostatný model a terminal quiet (900–10000 ms,
výchozí 4000). Před aktivací ověří přístup k modelu přes read-only
OpenAI Models GET bez inference. Post-deploy porovná skutečný actor,
model, quiet, enabled/cloud/personal a ověří oba stores. Diagnostika a
OpenAPI mají nové volitelné technické údaje; `live_decision_completed`
měří lokální zápis/frontu/API a ukládá jen dobu, korelaci a stav.
Kontrola aktivace odhalila, že prázdný cloud hit při dosud běžící indexaci
vynechal známé čerstvé motivy. Startup proto připojí také platné lokální
EveningMotif se stejnou izolací/rozpočtem/deduplikací; fixtures ověřují
pending index, správná ID/revize a oddělení večera i osobní historie.
Plán, provozní návod, research a backend README zdůvodňují oddělení Luny
od humoru a kompatibilitu checkpointu. Ověřené produkční nastavení je AutonomousActor,
Luna, quiet=1500, hybrid/cloud=true a personal=false.

Lokální ověření 6. 10. (bez placené AI, hardware a změn klientů):

| Kontrola | Skutečný výsledek |
| --- | --- |
| SDK 10.0.303 restore `BackendTests.csproj --locked-mode --runtime linux-x64` | uzamčené závislosti OK |
| Release build backend-tests `--no-restore` | 0 warnings / 0 errors |
| Release backend-tests `dotnet run --no-build --no-restore` | všechny skupiny OK; Luna usage/citace/sparse mask/excluded kandidát/party brána, duplicitní delegace, oprava, přerušení, playback, memory, logy a retence |
| `KUFR_BACKEND_DLL=…/linux-x64/backend.dll node kufr/backend/tests/smoke.mjs` | autentizace, fake voice, evenings/scope/restart a diagnostika OK |
| backend Python | 16 stávajících + 4 nové deployment testy OK; GET metadata bez inference a odmítnutí neaktivních/chybných settings přes existující stores |
| deployment Python | 11/11 OK |
| provenance Node | 29/29 OK |
| contracts `npm ci && npm test` | OpenAPI, 25 validních / 41 neplatných control, media frame/control OK |
| actionlint 1.7.12 / lokální Markdown / `git diff --check` | všechny workflow OK; SHA validatoru ověřeno; 53 lokálních cílů bez chyby; whitespace OK |

Implementační head `d8e3b4427b3ca82d51b5922edf8aaf205da1bacb` prošel
celým [CI 37532989739](https://github.com/Blato58/DildoVander/actions/runs/37532989739):
repository, backend, web, firmware host tests a všechny čtyři firmware
profily, offline AI i CI required success; placené AI live skipped.
[PR #126](https://github.com/Blato58/DildoVander/pull/126) byl sloučen
squash jako `95384b83c319daf20367e0c9427ad62c6e5b2393`.
Lokální fetch ověřil shodný tree headu i sloučeného main
`c8932b609d30cd02fc0e2de46bc3c47a8261f5b0`.

Skutečně provedený produkční příkaz z kořene repozitáře:

```sh
gh workflow run kufr-backend-deploy.yml --repo Blato58/DildoVander --ref main \
  -f build_run_id=37532989739 -f dialog_mode=responses -f dialog_model=gpt-6.1-sol \
  -f voice_mode=openai -f live_actor_mode=AutonomousActor -f live_decision_model=gpt-6-luna \
  -f live_terminal_quiet_ms=1500 -f hybrid_memory=true -f cloud_indexing=true
```

[Deployment 37534581909](https://github.com/Blato58/DildoVander/actions/runs/37534581909)
skončil success v obou jobech. Prověřil CI provenance a aktuální main,
skutečný OpenAI Models GET pro Lunu, aktivaci release
`4ed7d6a986d645fadd4fd464bb5593e27e22970d-37534581909-1`, loopback health,
veřejný HTTPS, oddělení admin/device tokenu, no-op mode mutation,
revision conflict a skutečný přístup/scope obou existujících stores.
Nevytvořil nové stores. Produkční diagnostika skutečně vrátila:

```json
{"cloudIndexingEnabled":true,"liveActorMode":"AutonomousActor","liveDecisionModel":"gpt-6-luna","liveTerminalQuietMs":1500,"longTermPersonalEnabled":false,"memoryEnabled":true}
```

Indexace anonymních EveningMotif je nyní aktivní; osobní paměť nadále
vyžaduje vlastní globální nastavení, ověřenou identitu a povolení.
Žádný hlasový/Responses model nebyl v provozním ověření volán. Poslechové
A/B, skutečná indexace/search/delete hostů, instalace IPA na konkrétním
telefonu, firmware flash a fyzická prezentace nebyly provedené. Nový
technický log změří skutečnou delegaci při dalším použití; známá změna
quiet=4000→1500 sama není měřením celkové latence rozhovoru.
Tehdy doporučená IPA pro autonomní režim byla D146 build **1000.0.16** z
[instalační stránky](https://2.28.202.220/ios/); starší klient bez
checkpointu nepoužívat. Aktuální build uvádí předání nahoře. Další krok je reálný rozhovor/poslech a kontrola
`live_decision_completed`, správné hůlky a životního cyklu; HW B1 zůstává otevřené.
První implementační commit `2710d49b2ae565f705e2c352f70f41bec3bfd706`
byl pushnutý a `ls-remote` potvrdilo shodu. Povinný PowerShell helper
na Linuxu skončil 126; repo-scoped `gh auth git-credential` fallback
uspěl s identitou Blato58 bez přepnutí globálního účtu. Změna je v
[PR #126](https://github.com/Blato58/DildoVander/pull/126); oprava pending
motifů se před nasazením ověřila znovu Release buildem (0/0) a všemi
backendovými skupinami včetně hybrid memory.

**D147 – 6. 10. 2026 – audit chybějící elektroniky a samostatná objednávka.**
Na pokyn majitele byly odečteny doložené faktury a vytvořena
[objednávka ve dvou obchodech](../../docs/objednavka-doplneni-kufru-2026-10-06.md),
přibližně 1 999 Kč základ / 2 921 Kč s Grove, LED konektory a SD/čtečkou,
bez dopravy. Koupené momentové tlačítko je začátek rozhovoru/PTT,
koupená kolébka hlavní vypínač; přidána pouze druhá kolébka pohonů.
Návrh respektuje jeden LED úsek a jedno servo NEBO jednu vibraci,
s možným souběhem LED a pohonu. Koupený 3A PD trigger a 60W kabel se
znovu využijí; nový pevný 5V/5A měnič napájí celé zařízení.

Tehdejší rozšířený kusovník, snímání kolébky a úpravy zesilovačů
nahrazuje D156: potvrzený MCP23017, dva zesilovače v továrním stavu,
PH2 kabely a přímé OE/napájení pohonů. Kamera je objednaná samostatně
pro stávající desku.
Dokumentace zapojení byla sjednocena s plánem, pinovou mapou a kamerou;
historické nákupní soubory a README odkazují novou objednávku.

Ověření: dostupnost a parametry vybraných produktových nabídek,
počty a cenové součty, 78 relativních odkazů v devíti dotčených Markdown souborech,
`git diff --check` a rozsah pouze dokumentace. Firmware/build ani HW
měření nebyly provedené. E0/E5/B1 zůstávají neuzavřené; další krok je
ověření revize kamery a skutečné pinové mapy, montáž jedné přihrádky a
proudové/resetové/Stop zkoušky podle nynějšího zapojení. Mechanické
rozměry a držáky nejsou doložené, případná úprava prototypu může změnit
kusovník. Dokumentační commit `9a13366b51e3acdd8b260eb07f4a7d2fee4b669c`
byl pushnutý na `origin/work`; `ls-remote` potvrdilo shodný hash. Povinný
PowerShell helper na Linuxu skončil 126 (Permission denied); repo-scoped
`gh auth git-credential` fallback s dostupnou identitou Blato58 uspěl.
Globální GitHub účet nebyl přepnutý.


**D146 – 6. 10. 2026 – aktuální Ad Hoc iOS aplikace publikovaná.**
Na navazující výslovné „Nasaď i iOS aplikaci“ byl produkční záznam D145
commit `5e4cd4ba30569fa0409ae657ab25ca99c4c08a35` po kompletním zeleném
[CI 37428677428](https://github.com/Blato58/DildoVander/actions/runs/37428677428)
sloučen v [PR #123](https://github.com/Blato58/DildoVander/pull/123) jako
`ebbf9e7e40f80eeacfe7f820e44e9e3a18261b14`. Git tree obou revizí
`2052dfc774545c624084d274f175dc6d801ca5ec` je shodný; placené AI live
zůstalo skipped. Publikace neobsahuje změnu modelu, osobnosti či backendového
produkčního režimu. Historické „neprovedeno“ D145 je stav před tímto pokynem.

Skutečně provedené příkazy z kořene repozitáře:

| Příkaz | Skutečný výsledek |
| --- | --- |
| `python3 -m unittest discover -s kufr/mobilni-aplikace/scripts -p 'test_publish_*.py'` | 4/4 publikační testy OK |
| `gh workflow run kufr-ios-sign.yml --repo Blato58/DildoVander --ref main -f build_run_id=37428677428` | [37429759564](https://github.com/Blato58/DildoVander/actions/runs/37429759564), success, macos-15 arm64, připnutý Xcode 26.3, reuse ověřeného webu, nativní harness, archiv/export a Ad Hoc podpis |
| `gh workflow run kufr-ios-deploy.yml --repo Blato58/DildoVander --ref main -f build_run_id=37429759564` | [37429982348](https://github.com/Blato58/DildoVander/actions/runs/37429982348), všechny tři joby success, atomická publikace a veřejné HTTPS ověření manifestu, Bundle ID, MIME a stažené IPA/SHA-256 |

Podpisový job skutečně spustil `bash scripts/test-native-transport.sh`
a `bash scripts/test-native-debug.sh` v `kufr/mobilni-aplikace`, včetně
Swift kompilace s warnings-as-errors. Prošly discovery/SPKI/Keychain/pinned
HTTPS/autentizovaný WSS, debug profile/mode/correlation/WAV/cancellation,
Realtime a hands-free multi-turn/VAD/cleanup, převod 48 kHz, offline render
produkčního audio grafu, startup/route changes a bounded upload/backlog.
Nový autonomní fixture prošel full-duplex capture, explicitním místním
drain/checkpointem a idempotentním opakováním ztraceného ACK. Všechny HTTP
peers jsou lokální deterministické testy, žádný placený model. Archiv,
export, codesign --verify a shoda embedded profilu prošly samostatně.

Na [instalační stránce](https://2.28.202.220/ios/) je **verze 1.0,
build 1000.0.16**, release `37429982348-1`, Team `3QRXV8G5Y3`, Bundle ID
`app.turquoise6409.green2444`. Předchozí release zůstává uložený.
IPA má 1004811 B, SHA-256
`36a75fcce3ba57e8b32ca66768429cf04024ddf36069d7c04c379daac5dd94e8`.
Původ podepsaného artefaktu, payload hash, tento checksum, skutečný
Info.plist/version/build/Bundle ID i přítomnost microphone usage description
byly navíc ověřené lokálně ze staženého artefaktu. CLI download do workspace
selhal 403 na dočasném blob redirectu; GitHub connector stáhl stejný
immutable artifact ID `11396725775` a ověření jeho bajtů uspělo. Samostatné
HTTPS ověření publikace proběhlo z produkčního runneru s platným TLS;
workspace proxy omezení D145 se tím nezaměňuje za iPhone akceptaci.
Lokální kontrola dokumentačního diffu ověřila 44 relativních Markdown
odkazů a `git diff --check` bez chyby; zdrojový kód se v D146 nemění.
První dokumentační commit `1612eea30b3864e3c9dd93e0908e259a55dae2c6`
byl pushnutý. Povinný PowerShell helper na Linuxu skončil 126; repo-scoped
`gh auth git-credential` fallback uspěl bez změny globálního účtu.

Backend zůstává v D145 release: `LegacyBackendScripted`, hybrid memory=true,
cloud indexing=false a long-term personal=false. Nová IPA již má D143
playback checkpoint, starší ESP firmware bez něj nebyl aktualizován.
Neprovedeno: instalace/aktualizace této IPA na iPhonu, živý full-duplex/AEC
či poslechové A/B, placená AI, skutečná indexace/search/delete hostů,
firmware flash a fyzická prezentace. Další krok je instalovat v Safari
na registrovaném iPhonu, ověřit audio a lifecycle podle D143 A/B scénářů;
autonomní serverový režim zapnout podle kompatibility aktivních klientů.

**D145 – 6. 10. 2026 – skutečný production deployment a OpenAI provisioning.**
Na výslovné „Nasaď a udělej provisioning“ byl D144 commit
`76b7a842edba9b490b0354fca6a917232e4c70e3` pushnutý a
[PR #122](https://github.com/Blato58/DildoVander/pull/122) sloučen squash
jako `f5b561a3477a626608f8f58027010ae5529d258b`.
[CI 37426330173](https://github.com/Blato58/DildoVander/actions/runs/37426330173)
prošlo celé: repository, backend, web, offline AI, firmware profily,
host tests a CI required. Placené AI live bylo skipped. Push helper na
Linuxu skončil 126, repo-scoped gh credential fallback uspěl. Přechodné
401 při čtení přes CLI byly znovu ověřeny konektorem/REST; konečný
výsledek CI, merge i jednotlivých provozních jobů je success.

Skutečně provedené provozní příkazy (všechny workflow z `main`, repo
Blato58/DildoVander, žádná klíčová hodnota v argumentech/artefaktech):

| Příkaz | Skutečný výsledek |
| --- | --- |
| `gh workflow run kufr-memory-provision.yml --ref main -f build_run_id=37426330173 -f action=dry-run` | [37427702693](https://github.com/Blato58/DildoVander/actions/runs/37427702693), success, Production, obě create_requested, networkRequests=0, exports=0 |
| stejný příkaz s `-f action=create` | [37427817392](https://github.com/Blato58/DildoVander/actions/runs/37427817392), success, obě skutečné OpenAI resources vytvořené a GET ověřené, exportedRecords=0 |
| `gh workflow run kufr-backend-deploy.yml --ref main -f build_run_id=37426330173 -f dialog_mode=responses -f dialog_model=gpt-6.1-sol -f voice_mode=openai -f live_actor_mode=LegacyBackendScripted -f hybrid_memory=true -f cloud_indexing=false` | [37428033995](https://github.com/Blato58/DildoVander/actions/runs/37428033995), success; přijat ověřený artifact, root-owned helper aktivoval release, lokální health i veřejná auth/diagnostika/stores ověřené |

Release je `15a0dbec9bf87d7a4f7090a5df74750fc5a47aca-37428033995-1`
(source SHA testovaného merge tree); aktuální `main` měl při nasazení
identický tree, jiný squash SHA. Provisioning vytvořil osobní
`vs_6ac49e8f5a5c81918bee9d00c8c194d3` a anonymní
`vs_6ac49e90bdc081919e9dd2ee34e14436` v namespace `kufr:Production`.
ID jsou v trvalém privátním provisioning journalu runneru a v konfiguraci
release; žádný store se netvoří při startu služby. Post-deploy smoke
ověřil skutečný `LegacyBackendScripted`, memory provider configured=true,
ověřené stores/lastVerifiedAt a degraded=false, oddělení admin/device
oprávnění a původní responses/openai bez generování dialogu.
Následná read-only [diagnostika 37428248292](https://github.com/Blato58/DildoVander/actions/runs/37428248292)
je success; její skutečný technický snapshot je v Actions logu.

Aktivní `Backend:Memory:Enabled=true`, `CloudIndexingEnabled=false`,
`LongTermPersonalEnabled=false`; žádné soubory hostů nebyly uploadované.
Autonomní režim zůstává vypnutý kvůli kompatibilitě starší publikované
IPA/firmware bez playback checkpointu; aktivace vyžaduje aktualizované
klienty podle [návodu](autonomous-live-memory.md#github-actions-na-produkčním-runneru).
Personality se tímto deploymentem nepřepíná. Migrace EveningMemoryStore
je nedestruktivní; původní archivy nejsou automaticky cloudová osobní paměť.

Veřejný HTTPS smoke na produkčním runneru prošel s ověřeným TLS.
Nezávislé `curl --fail --silent --show-error --max-time 15
https://2.28.202.220/healthz` z tohoto workspace selhalo 60: cesta přes
workspace proxy předložila certifikát se subject openai.com místo IP.
TLS ověřování nebylo vypnuto; toto není doložená externí klientská
akceptace. Neprovedeno: živá Files indexace/search/delete na hostech,
placená voice/model evaluace, poslech A/B, nativní macOS/iPhone test,
nová IPA publikace, firmware flash nebo fyzická prezentace. Cloudové
create/verify je skutečné; původní HW/hlasové mezery tím nejsou uzavřené.

**D144 – 6. 10. 2026 – příprava autorizovaného deploymentu a provisioning.**
Majitel po sloučení D143 v PR #121 výslovně zadal „Nasaď a udělej
provisioning“. Audit produkce přes host probe
[37425364754](https://github.com/Blato58/DildoVander/actions/runs/37425364754)
a read-only diagnostiku
[37425368125](https://github.com/Blato58/DildoVander/actions/runs/37425368125)
potvrdil funkční health, responses/openai, dostupné providers a 0 rozhovorů.
Runner má pouze úzké sudo pro activate, nikoli právo zastavit službu
a spustit CLI nad její soukromou DB. Přístup ke GitHub environment variables
přes aktuální integrační token vrací 403; klíče se nevyžadují v chatu.

`kufr-memory-provision.yml` proto explicitně spouští skutečné D143 CLI
z ověřeného CI artifactu a uchová technická ID v privátním trvalém journalu
runneru mimo checkout/temp. Databáze paměti hostů zůstává ve službě.
`memory_provision.py` ověřuje umístění/oprávnění/IDs, odmítne konflikt a
neúplný úspěch; dry-run nepředá klíč, žádný režim neexportuje záznamy.
Deployment přebírá ID a explicitní actor/memory/cloud parametry do release;
kontrola po deploy ověří skutečný actor/provider a případně přístup do
stores bez hlasového modelu či osobních dat. Sudo není rozšířeno.

Lokální kontroly skutečně proběhly: deployment Python 11/11 (8 nových),
provenance Node 29/29, backend Python 16/16, actionlint 1.7.12 nad všemi
workflow a git diff --check OK. Skutečný backend apphost přes wrapper
`GITHUB_WORKSPACE=/tmp/kufr-provision-local/checkout DOTNET_ROOT=/tmp/dildovander-dotnet
python3 kufr/backend/deploy/memory_provision.py --backend
/workspace/DildoVander/kufr/backend/bin/Release/net10.0/backend --action dry-run`
vrátil Production, dvě create_requested, networkRequests=0 a exports=0.
Tento záznam je před provedením deploymentu; cloud/produkční výsledky
budou doplněny podle skutečných běhů po povinném CI a sloučení.

**D143 – 6. 10. 2026 – autonomní GPT-Live a hybridní paměť.**
Audit začal na čistém `work`, HEAD `302ac63` (sloučený D142), remote
Blato58/DildoVander. Změny jsou pouze v kufru, větev
`codex/autonomous-live-hybrid-memory`. AGENTS, plán/stav, skutečné zdroje
a dotčené README byly ověřené. D128 delegoval každou odpověď TextDialogRunner,
posílal hotový Reply, session.input byl prázdný a legacy odpověď měla 1000 znaků.
Nová explicitní větev odděluje Live herce od faktického rozhodování Solem;
legacy kód a Sol/Realtime fallbacky zůstávají kompatibilní.

Implementované soubory a chování:

- `GptLiveOptions`, `LivePromptSnapshot`, PersonalityCatalog a embedded
  `ollivander-trunk-live-persona.txt`: dedikovaná Live adaptace 2/3 odpovědi
  s dřívějším výběrem; party vlastní 4–7. Původní VoicePersona, OllivanderPersona
  a PartyPrompt nemají obsahový diff; ID/jména/zdrojové hashe, uložená volba
  a mobilní přepínání zůstávají. Při create se zmrazí také režim/verze/hash/politika.
- `WandDecisionService`: stejný Sol/SDK/rozpočty, skutečné katalogové kandidáty,
  přesné citace/references, stav/missing information a revize bez monologu Reply.
  Nulová dostupnost/nesplněná party politika nevolá model. Běžnou řeč tvoří Live.
- `AutonomousLiveDialogue`, časovaná projekce a `LiveTextBudget`: delegace před
  přepisem čeká omezeně, duplicita je idempotentní, oprava zneplatní starou revizi,
  pozdní výsledek se zahodí a nejasný placený požadavek ponechá rezervaci.
  Dlouhá čeština/fragmenty neukončí hovor; vstup/výstup jsou oddělené. Otázkové
  kandidáty jsou aplikační odhad, nikoli přesné sémantické měření.
- `GptLiveEndpoints`, ConversationAutonomousLive a registry: autentizované
  `/playback` a `/availability`, přesný PCM checkpoint/místní drain, průběžný
  reader a korelované close/closed. Návrh pro zařízení znovu kontroluje aktuální
  revizi/snapshot. Append ACK, ticho a closed neznamenají přednes či pohyb.
  PCM pause/terminal quiet jsou konfigurované heuristiky.
- EveningMemoryStore/HybridMemoryStore schema 2: scopes/identity/consent,
  revize/tombstones, oddělené časované přepisy, persistentní outbox/lease/mapování,
  kvóty a retence. Nedestruktivní migrace naplněné původní DB zachová texty,
  večery i konfliktní historii; legacy/unverified se nepřiřadí osobě/neexportuje.
  Zápis+outbox a invalidace jsou transakční; epoch blokuje pozdní zápisy/shrnutí.
- `OpenAiVectorMemoryProvider` je skutečný REST adaptér store verify/create,
  Files upload, attach/attributes/poll, direct search s **filters**, asociace
  DELETE i vlastní Files DELETE. `HybridMemoryService` vrací jen známé FileId
  rehydratované/revalidované v SQLite. Worker má bounded concurrency, timeout,
  retry/jitter/Retry-After, restartovatelný outbox a kontrolu nejasného uploadu.
  Výpadek znamená memory_unavailable + oprávněný lokální kontext, nula memory_not_found.
  Session lokálně sleduje skutečně předaná ID/revize včetně startup contextu;
  opakované dohledání je znovu nevloží, opravenou novější revizi připustí.
- Admin/CLI provisioning, revizované permission/record mutace, diagnostika bez
  osobního textu/klíčů a bezpečné vzory. Hlasové zapamatování vyžaduje ověřeného
  hosta/povolení, zapomenutí smaže jen vlastní osobní data a ukončí kontaminovaný
  kontext. ConfirmedSelection nevzniká automaticky z návrhu/ACK/closed.
- Firmware/Swift checkpoint: ESP zachovává gate/zero PCM během speaker playback
  a I2S/DMA drain, telefon full duplex. Kontrakty a fixture testy zahrnují stale,
  duplicate/lost ACK. PendingProposal, OperationJournal, místní vysunutí–předvedení–
  zasunutí a zotavení po Stop zůstávají. Žádný modelový text neřídí pohony.

Výchozí režim je **LegacyBackendScripted, paměť/cloud/personal false,
prázdná store ID**. Žádná produkční aktivace ani vytvoření/export nebyly
provedené. [Úplný návod](autonomous-live-memory.md) obsahuje skutečné
konfigurační klíče/limity, aktivaci/rollback, připravené provisioning příkazy,
admin těla, retenci/mazání a poslechové A/B scénáře. Oficiální Live/session
schema/REST retrieval/Files/datové podmínky byly čtené 5. 10. SDK 2.14.0
nemá potřebný Live ani direct search s attributes; izolované adaptéry
používají ověřený wire kontrakt. Žádný upgrade závislostí/runtime.
`store:false` nevypíná Files/Vector Store persistence a není příslib ZDR.

Skutečně provedené lokální kontroly (5.–6. 10. 2026, bez placené integrace):

| Příkaz (z kořene, není-li uvedeno jinak) | Výsledek |
| --- | --- |
| `/tmp/dildovander-dotnet/dotnet restore kufr/backend-tests/BackendTests.csproj --locked-mode --runtime linux-x64` | SDK 10.0.303, uzamčené závislosti OK |
| `/tmp/dildovander-dotnet/dotnet build kufr/backend-tests/BackendTests.csproj -c Release --no-restore` | backend + testy, 0 warning / 0 error |
| `/tmp/dildovander-dotnet/dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release --no-restore` | celá offline sada OK; legacy/Sol/Realtime, personality, journal, cost/clock a nové testy |
| stejný `dotnet run … -- --autonomous-memory` | nová deterministická HTTP/WebSocket/SQLite sada OK, včetně naplněné legacy DB a restartu |
| `PATH=/tmp/dildovander-dotnet:$PATH KUFR_BACKEND_DLL=bin/Release/net10.0/backend.dll node kufr/backend/tests/smoke.mjs` | auth, diagnostika/memory, missing config/required JSON, fake/večery/restart OK; ambient OpenAI klíč v harnessu vypnutý |
| `npm ci` + `npm test` v `kufr/contracts` | 25 valid / 41 invalid control fixtures, OpenAPI/media OK |
| `npm ci` + `npm test` + `npm run build` v `kufr/mobilni-aplikace` | 25/25, TypeScript/Vite OK |
| `python3 -m unittest discover -s kufr/backend/tests -p 'test_*.py'` | 16/16 OK |
| `python3 -m unittest discover -s kufr/ai-eval -p 'test_*.py'` | 24/24 OK |
| Přesné hostitelské C++14 voice/presentation příkazy v [návodu](autonomous-live-memory.md#akceptace-a-poslechové-ab-scénáře), shodné source/flags s `.github/workflows/kufr-firmware.yml` | realtime_voice, espidf_realtime_transport, espidf_voice_audio, autonomous_presentation_lifecycle OK; drain/ACK/gate/Stop/návrat |
| `/tmp/kufr-platformio-env/bin/pio run` v `kufr/firmware` | PlatformIO 6.2.0, původní platform 7.1.3/ESP-IDF 6.1, SUCCESS; RAM 66032 B, flash 1203641 B |
| `Backend__DatabasePath=/tmp/kufr-memory-provision-dryrun.sqlite ASPNETCORE_ENVIRONMENT=Development /tmp/dildovander-dotnet/dotnet run --project kufr/backend/backend.csproj -c Release --no-build --no-launch-profile -- --memory-provision --create` | dry-run OK, networkRequests=0, exports=0; první pokus bez DatabasePath správně odmítl neúplnou konfiguraci |
| `node --check kufr/mobilni-aplikace/tests/native_handsfree/http_peer.mjs` a `git diff --check` | syntax/whitespace OK; 16 nových lokálních dokumentačních odkazů existuje |

Nový provider je testovaný skutečnou třídou s fixture HTTP, nikoli fake
produkčním úspěchem. Testy zahrnují identity/scopes/souhlas, malicious chunk/
unknown FileId, stale revize/cache, upload-delete race, lease/restart,
failed/in-progress index, 429/timeout/výpadek, obě DELETE a neobnovení tombstone.
Audio fixture testuje dlouhou nedokončenou dramatickou pauzu, pokračování,
skutečný přepis proti faktickému výsledku, přerušení a opožděný místní drain.

Neprovedeno: cloudový `--execute`, skutečný OpenAI provider test/export
osobních dat, český poslech A/B, AEC/echo/clipping/latence měření, skutečný
speaker/servo/hůlka, Swift/Xcode/macOS testy (Linux nemá Apple SDK), podpis
IPA, flash desky a deployment/aktivace produkce. Nový `native_handsfree`
Swift fixture je připravený pro explicitní drain/lost ACK/full duplex;
syntax kontrola HTTP peeru není nativní běh. Historické E9/E10 a HW/iPhone
akceptace se tím neuzavírají.

Git předání: implementační commit `bc18b346ff04a039af103c5e114a58c5ceb64679`
je pushnutý na `codex/autonomous-live-hybrid-memory`; `ls-remote` potvrdil
shodu SHA. Povinný `./scripts/Push-CurrentBranch.ps1` skončil na Linuxu
permission denied (pwsh není dostupné), push použil repo-scoped
`gh auth git-credential` s ověřeným účtem Blato58 bez změny globálního účtu.
Změny jsou v [PR #121](https://github.com/Blato58/DildoVander/pull/121);
výsledky povinných CI a stav sloučení jsou dohledatelné v tomto PR.
Tento navazující dokumentační commit zaznamenává skutečné doručení kódu.
Další konkrétní krok: po zvláštním oprávnění ověřit native macOS testy,
testovací stores/klíč a A/B na obou personalitách. Měřit odděleně hlasovou
kvalitu/latenci, modelová/paměťová volání/objem a playback/fyzický stav.
Produkci nepřepínat jen kvůli zeleným unit testům.

**D142 – 5. 10. 2026 – inventář personalit, trvalá volba a iOS Debug test.**
Majitel žádá přepínatelné personality a zachování přesného druhého promptu.
`party-original` zůstává výchozí a `voice-persona.txt` se nemění. Nový
`ollivander-trunk-persona.txt` obsahuje celé zadání v UTF-8 včetně příkladů,
vulgarit a `TICH0`; embedded bajty hlídá test SHA-256
`99a72891209ef2bf5ad5a0120a7998c5a08bf46317f38812fb1d3816a5c3099d`.
Inventář a návod je v [personalities.md](../ai-eval/personalities.md).

`PersonalityCatalog` poskytuje ID/jména/popisy/hashy přes admin modes.
Volba je globální pro nový rozhovor ESP i telefonu a zmrazí se při create;
SQLite `server_settings` (schema 7) zachová volbu přes restart. Přepíná
se s admin credential a aktuální revizí pouze bez aktivního rozhovoru či
provider volání. Vynechané ID u původního klienta zachová aktivní volbu.
Sol, Realtime i GPT-Live dostávají jediný vybraný prompt. Nový kufr dovolí
výběr už od první odpovědi, obvykle po dvou podle svého promptu; nepřebírá
čtyřotázkovou bránu ani požadavek na otevřené otázky původní personality.
Delší prompt má spočtený přírůstek request limitu/nákladové rezervace;
3 USD/session, katalog a brána fyzické prezentace jsou beze změn.

iOS native bridge validuje metadata inventáře a předává volbu serveru.
Debug → Server nabízí Personalita a Použít nastavení serveru. Zkušební
rozhovor ukazuje aktivní jméno a nabízí Otestovat personalitu · AI / fake.
Fake má pro novou personalitu dvě otázky, pro původní čtyři; tiché WAV a
syntetická odpověď neověřují charakter. Starší server označí potřebu
aktualizace a přepnutí není dostupné. Swift fake testy rozšiřují validaci
ID, duplicity, checksumu a neúplného inventáře; běh vyžaduje macOS.

Ověřeno lokálně: .NET SDK 10.0.303 locked restore linux-x64, Release build
bez warning/error a celá offline BackendTests sada včetně nového přesného
promptu, skutečného SDK requestu, rezervace, restartu SQLite, změny revize,
blokování aktivního hovoru, early selection a dvouodpověďového Live toku.
HTTP smoke s autentizací admin modes, inventářem, změnou, stale revizí,
aktivním rozhovorem a dosavadním restartem prošel. Contracts: 25 valid/
41 invalid control fixtures, OpenAPI a media kontroly prošly. npm ci podle
lockfile, mobilní Vite/TypeScript build a 25 mobilních testů prošly.
Python backend offline 16 testů a AI eval 24 testů OK.
Playwright Chromium: iPhone viewport 393×852, syntetický native harness,
přepnutí personality, zákaz přepnutí během hovoru a fake výběr po dvou
odpovědích i odmítnuté změně při konfliktu prošly. Harness není skutečný
iOS bridge ani poslech. Kontrola 46 místních dokumentačních odkazů a
whitespace prošla; původní persona i prompt funkcí nemají diff.

Neprovedeno: Swift/Xcode (Linux nemá Apple SDK), podpis nové IPA, deployment,
placený provider test, poslech a fyzický kufr. Současná publikovaná IPA
1000.0.15 a nasazený D141 backend tímto lokálním slice nejsou aktualizované.
Doručení: implementační commit `ba1554b875e1a1e6041aef328061b42c88e75934`
je pushnutý na `codex/personality-inventory`; `ls-remote` potvrdil shodu SHA.
Povinný PowerShell helper na Linuxu skončil permission denied (pwsh není
nainstalované); push použil repo-scoped `gh auth git-credential` pro Blato58
bez změny globálního účtu. Slice je v
[PR #120](https://github.com/Blato58/DildoVander/pull/120); povinné CI a merge
se ověřují v tomto PR před předáním. Tento navazující commit doplňuje jen
záznam skutečného push/PR a lokálních kontrol.
Další produktový krok: samostatně vyžádaný signed iOS build s native testy,
ověřené předání backendu a IPA a poslech obou personalit.


**D141 – 4. 10. 2026 – otevřené otázky a vlastní repliky bez ukázek.**
Majitel po poslechu žádá méně opakování, odstranit příklady a dát hostovi
prostor rozmluvit se otevřenými otázkami. Převzat společný prompt z D140.
`voice-persona.txt` odstraňuje ukázkové dialogy, vzorové nadávky/přezdívky
a pokyn k nabídce dvou odpovědí. AI tvoří sama, navazuje otevřenou otázkou,
nerecykluje otázku v jiném slovosledu, oslovení ani hotovou pointu. Délka
kufrovy repliky není pokyn ke zkrácení hostova vyprávění; při nejistotě
zjednoduší otevřenou otázku, ne nabídku možností.

`ConversationOpening` odstraňuje pevný cyklus šesti námětů včetně binární
volby. Téma a formulaci vytváří model; server přidává jen nevyslovovaný
identifikátor úvodu, jednou na opening, zachovaný při rezervaci/fallback.
Interní pole je `OpeningVariation`; Live zahajovací pokyn i Python opening
žádají vlastní otevřenou otázku. README, plán a rešerše jsou aktualizované.
Realtime zůstává 2.1/medium, Sol low; autonomní výběr bez souhlasu pokračuje.

Ověření: .NET 10.0.303 Release build a kompletní offline backend testy
prošly bez warning/error, včetně shared prompt requestů, fresh opening,
fallback invariance, rozpočtů a Live completion/cancellation. Python offline
eval: 24 testů OK. Upraven dosavadní opening test pro samostatné interní
identifikátory místo cyklu témat. Žádný placený provider test/poslech ani HW.
Identifikátor a prompt samy nezaručují vtipnost či různost generovaných replik.

Doručení: implementace `6c948f3` na větvi
`codex/open-dialogue-without-examples` je pushnutá; `ls-remote` potvrdil SHA.
Povinný PowerShell helper na Linuxu skončil permission denied; použit
repo-scoped `gh auth git-credential` bez změny globálního účtu.
[PR #118](https://github.com/Blato58/DildoVander/pull/118) prošel povinným
[CI 37225594462](https://github.com/Blato58/DildoVander/actions/runs/37225594462)
a je sloučen squash commitem `477dc883a7f16646558eb1925c574627b021999c`.
Shoda main stromu se zdrojem PR `d5b1dcc` je ověřená
(`27853d1f40bd3884b0b16dd21b79e594c3831dac`).
Dosavadní souhlas s backend nasazením byl použit pro pokračující úpravu.
[Deploy 37226198653](https://github.com/Blato58/DildoVander/actions/runs/37226198653)
ověřil původ CI artefaktu, přesný main strom, checksum a safe unpack;
aktivoval `fa134b501dc6e437b3d488186132444267a16a85-37226198653-1`.
Loopback health a veřejné HTTPS kontroly admin autentizace, no-op režimů,
revizního konfliktu a diagnostiky prošly bez AI volání či změny režimů.
Nová IPA není potřeba. Neproběhl placený provider test, poslech ani HW akce.

Navazující dokumentace je na větvi `codex/record-d141-deployment`;
po ověření místních odkazů a whitespace se dokončí její PR.
Další krok: majitel v nových rozhovorech poslechem ověří vlastní různorodé
úvody, navazující otevřené otázky a delší odpověď bez podsouvání možností.
Rozmanitost a vtipnost AI odpovědí nejsou offline kontrolou prokázané.
IPA 1000.0.15 z D139 zůstává publikovaná.



**D140 – 4. 10. 2026 – Realtime 2.1 medium a společný prompt podle GPT Live.**
Majitel žádá zachovat model 2.1, zvýšit effort na medium a sjednotit prompty
podle GPT Live. `voice-persona.txt` obsahuje společnou Live roli, sprostý
konkrétní roast, navazující otázky, ticho během řeči/zpracování, interruption
policy, paměť a závěr bez otázky na souhlas. Dosavadní výjimka Realtime
pro krátkou hlášku během funkce je odstraněná. `PartyDialoguePrompt`
načítá embedded personu; Sol a Realtime přidávají `prompt.txt` s pravidly
výběrových funkcí. Live přidává jen delegaci a vyslovuje backendový výběr.
Python evaluátor skládá stejné instrukce. Sol zůstává na low; Realtime
posílá medium přes GA JSON patch oficiálního SDK. Limit 3 USD je beze změny;
vyšší effort může prodloužit odpověď a zvýšit spotřebu tokenů.

Lokální ověření: .NET 10.0.303 Release build a kompletní offline backend
testy prošly bez warning/error; testy skutečných requestů ověřují shodnou
personu pro Live, Sol a Realtime, samostatnou delegaci a serializaci medium.
`python -m unittest discover -s kufr/ai-eval -p 'test_*.py'`: 24 testů OK.
Prošly i dosavadní limity požadavků, rozpočet a regrese dokončení Live.
Placené volání, poslech, iPhone a HW se neověřovaly. Nová IPA není potřeba.
Změny README, plánu a rešerše popisují současnou skladbu promptu.

Doručení: implementace `db4d8af1171ad94c27ea0c13ff1b16f5f4a7cbfa`
na větvi `codex/realtime-21-medium` je pushnutá; `ls-remote` potvrdil SHA.
Povinný PowerShell helper na Linuxu skončil permission denied (pwsh chybí);
push proběhl s repo-scoped `gh auth git-credential` bez změny globálního účtu.
[PR #116](https://github.com/Blato58/DildoVander/pull/116) prošel povinným
[CI 37222949669](https://github.com/Blato58/DildoVander/actions/runs/37222949669)
a je sloučen squash commitem `7237a0570da38c00313e183a4caa7b4f3b74dcc3`.
Shoda main stromu se zdrojem PR `8dc896a` je ověřená
(`d287e8eae5a97f58b378b1f96e69c0e9df356fb0`).
Dosavadní souhlas s backend deploymentem byl použit pro pokračující úpravy.
[Deploy 37223612673](https://github.com/Blato58/DildoVander/actions/runs/37223612673)
ověřil původ CI artefaktu, přesný main strom, checksum a safe unpack;
aktivoval `d3f3e656cb06e44339b2b66eea48f4ab7608338d-37223612673-1`.
Loopback health a veřejné HTTPS kontroly admin autentizace, no-op režimů,
revizního konfliktu a diagnostiky prošly bez AI volání či změny režimů.
Read-only [snapshot 37223690510](https://github.com/Blato58/DildoVander/actions/runs/37223690510)
z 18:15:09 UTC potvrzuje responses/openai, dostupné adaptéry,
0 aktivních rozhovorů, `droppedWrites=0`, `truncated=false`.
Tři varování po aktivaci (401/401/409) odpovídají negativním deployment
kontrolám; snapshot neověřuje poslech ani latenci hlasu.

Tento navazující zápis je pouze dokumentace na větvi
`codex/record-d140-deployment`; nový deploy ani podpis není potřeba.
Další krok: majitel poslechem ověří medium latenci, navazující otázky,
ostrost a automatický závěr. IPA 1000.0.15 z D139 zůstává publikovaná.



**D139 – 4. 10. 2026 – publikovaná IPA 1000.0.15.**
Majitel výslovně schválil „Publikuj“. Workflow `kufr-ios-deploy.yml`
[Nasazení 37189756640](https://github.com/Blato58/DildoVander/actions/runs/37189756640)
na main `1dfb544` publikovalo již podepsaný a ověřený artefakt
[37156500324](https://github.com/Blato58/DildoVander/actions/runs/37156500324).
Nevznikl nový build. Verify, publish-ios a verify-ios-page prošly:
původ podpisového artefaktu, přesný main strom, checksum a safe unpack;
potom veřejné HTTPS instalační stránky, manifestu, Bundle ID, odkazu na
IPA, SHA-256 a MIME typů. Release ID je `37189756640-1`.
[Instalační stránka](https://2.28.202.220/ios/) nyní nabízí **1000.0.15**;
manifest a IPA jsou v `/ios/releases/37189756640-1/`.
Checksum je shodný s D138:
`202d856552f27346099d3976df72420436ff43d0a72d8546ef4eba9def3d241b`.

Backend z D138 už běží s opravou načasování Live, gate před provider close,
přímým přidělením hůlky bez potvrzovací otázky a ostřejším tónem. Publikace
IPA přidává nativní ochranu proti opožděné overflow notifikaci až po
aktualizaci aplikace na telefonu. Backend se v tomto kroku znovu nenasazuje;
firmware se neflashuje. Uživatel může otevřít stránku v Safari a aktualizovat
aplikaci. Instalace 1000.0.15 a skutečný poslech těchto oprav nejsou tímto
publikačním HTTPS testem doložené; placený provider test nebyl spuštěn.

Po úspěšné publikaci je bezpečné dokončit dokumentační PR nad větví
`codex/record-live-terminal-deployment`; mění pouze tento stavový záznam,
nevyžaduje další podpis ani deployment. Ověření tohoto zápisu: místní odkazy,
konzistence nového předání s historickým D138 a whitespace; povinné CI a
merge se ověří v navazujícím PR. Další krok: majitel aktualizuje aplikaci
a ověří delší odpověď se zaváháním/opravou, automatické přidělení bez ano/ne
a úplný závěr bez overflow. Živé pozorování odlišit od úspěšného build/testu.


**D138 – 3. 10. 2026 – nasazené opravy Live a ověřená IPA 1000.0.15.**
[PR #114](https://github.com/Blato58/DildoVander/pull/114) prošel povinným
[CI 37155888501](https://github.com/Blato58/DildoVander/actions/runs/37155888501)
a je sloučen squash commitem `1dfb544`; shoda jeho stromu se zdrojem PR
`d76fa05` je ověřená (`4d9e6fa6c1a01e541dec6b53b6721ac5c0a0ea3d`).
Backend pokračující opravy byl nasazen podle dosavadního souhlasu majitele.
[Deploy 37156501136](https://github.com/Blato58/DildoVander/actions/runs/37156501136)
ověřil původ CI artefaktu, přesný main strom, checksum i bezpečné rozbalení
a aktivoval `ea95ad24f9cb60da3cc604afae2ffbb6be88e81f-37156501136-1`.
Loopback health a veřejné HTTPS kontroly autentizace, no-op konfigurace,
revizního konfliktu a diagnostiky prošly bez AI volání či změny režimů.
Read-only [snapshot 37156597395](https://github.com/Blato58/DildoVander/actions/runs/37156597395)
z 21:54 UTC potvrzuje responses/openai, dostupné adaptéry, 0 aktivních
rozhovorů a 0 droppedWrites. Varování 401/409 při této aktivaci patří
k negativním deployment kontrolám, nikoli novému selhání hlasu.

Podepsaný [iOS build 37156500324](https://github.com/Blato58/DildoVander/actions/runs/37156500324)
prošel na macOS/Xcode 26.3: nativní LAN/backend transport, multi-turn/VAD,
renderování produkčního grafu po třech startech, upload batching/backlog,
nová regrese overflow epochy, cancellation a skutečný podepsaný iOS archive.
Výsledná Ad Hoc IPA má verzi **1000.0.15**, stejný Bundle ID
`app.turquoise6409.green2444` a artefakt `11285888055`. Ověřeny ZIP digest,
payload hash přes produkční safe unpack, source SHA/tree main, IPA checksum,
Info.plist verze/Bundle ID a přítomnost podpisu. IPA SHA-256:
`202d856552f27346099d3976df72420436ff43d0a72d8546ef4eba9def3d241b`.
Přímé cloud stažení Actions blobu vrátilo Forbidden; autorizovaný GitHub
connector artefakt stáhl a poskytl ověřitelný ZIP. Nejde o bypass TLS.

Na předání D138 publikace IPA ještě neproběhla; majiteli byla předložená
ověřená 1000.0.15. Zápis byl commitnutý jako `c0ee054` a pushnutý do větve
`codex/record-live-terminal-deployment`. Jeho merge se odložil kvůli
zachování přesného main stromu podepsaného artefaktu. Majitel 4. 10.
schválil „Publikuj“; dokončená publikace a navazující předání jsou v D139.
Nativní fake testy ani podpis nedokazují absenci všech skutečných
přerušení/backlogu nebo českou vtipnost; poslech oprav na iPhonu zůstává
nedoložený. Placený provider test nebyl spuštěn. PowerShell helper v tomto
Linuxu nelze spustit, používá se již ověřený scoped gh credential helper.

**D137 – 3. 10. 2026 – dokončení Live, ochrana overflow a sprostší autonomní výběr.**
Majitel po poslechu potvrzuje lepší Live/Realtime, ale hlásí jednorázové
přerušení bez výběru a overflow po mluveném závěru. Žádá přímé přidělení
hůlky bez potvrzovací otázky a drzejší/sprostší tón. Read-only
[snapshot 37155213446](https://github.com/Blato58/DildoVander/actions/runs/37155213446)
potvrzuje poslední Live hovor zrušený klientem (HTTP 499 po 87,6 s),
bez completed; obsah řeči ani zdroj iOS backlogu log neobsahuje.
[Rešerše D137](hlas-a-humor-research.md#d137-zpětná-vazba-po-poslechu--přerušování-a-dokončení)
odlišuje nalezené slabiny řízení od nedoložené přesné příčiny konkrétního
hlášení.

`GptLiveDialogue` podrží hotový Sol výsledek do 650 ms od posledního
input fragmentu, včetně přepočtu zastaralého výsledku; jde o heuristiku,
ne provider VAD. `GptLiveEndpoints` zavře gate a pošle `speech_stopped`
před close/čekáním na final usage, pozdní uploady potvrzuje bez přehrání.
Final usage a validace návrhu stále předcházejí completed; korekce hosta
před zavřením může závěr zrušit. Live během řeči i delegace mlčí.
Prompt a Live/Realtime instrukce žádají sprostý tón a autonomní přidělení
bez souhlasu hosta. Otázková brána, dostupnost, respekt k odmítnutí a
3 USD/session zůstávají.

Nativní `DebugHandsFreeAudio` nově koreluje main-queue overflow notifikaci
s epochou vstupu. Po zavření gate nebo novém poslechu stará notifikace
nesmí zrušit závěr/další vstup. Skutečný backlog stále selže, 1s fronta
ani jeden upload v letu se nezvětšují. Regrese v upload_regression.swift
ověřuje aktuální fault i zneplatnění po stop/novém begin. Tato ochrana
vyžaduje novou IPA, backendové opravy fungují se stávající 1000.0.14.

Lokálně prošly SDK 10.0.303 Release build (0 warning/error), celá C# sada
včetně rychlého Solu mezi 350ms fragmenty, účtování zastaralého výsledku,
korekce závěru a input gate před close, Python eval 23/backend tools 16/
deploy 3 testy, web build a 25 web testů. HTTP fake smoke prošel na
opakování; první pokus neměl dotnet v PATH, další narazil na transportní
EPIPE (Node 24.19.0), opakovaný běh beze změny prošel. Povinné CI ověří
pinned runtime samostatně. Whitespace a 30 místních dokumentačních odkazů bez chyby. Swift/AVFoundation není
v Linux workspace, nativní regrese/build zatím neproběhly; důkaz doplní
ruční podepsaný iOS build. Placený provider test ani fyzický poslech
oprav nebyly spuštěny. Výsledek buildů, publikace a poslechu nesmí být
odvozený z offline testů. Další krok: zelené CI/merge, nativní podepsaný
build, ověřené předání backendu a případně nové IPA, potom delší odpověď,
oprava, výběr bez ano/ne a dohrání závěru na telefonu.

Git předání: implementační commit `bd03dbf` je pushnutý a shoda remote
ověřená. PowerShell push helper v Linuxu skončil permission denied;
použit scoped gh credential helper pro účet Blato58.
[PR #114](https://github.com/Blato58/DildoVander/pull/114) obsahuje tento
slice a záznam předání; CI/merge se kontrolují v PR, zdrojová větev
`codex/live-audio-selection-fix`.

**D136 – 3. 10. 2026 – ověřené nasazení Realtime 2.1 a ostrého kufru.**
Majitel v tomto chatu výslovně schválil „Nasadit backend“. Implementace
[PR #112](https://github.com/Blato58/DildoVander/pull/112) je sloučená do
main `348cc16` po zeleném [CI 37153635698](https://github.com/Blato58/DildoVander/actions/runs/37153635698);
shoda zdrojového stromu main s ověřenými změnami byla potvrzena.
[Nasazení 37154360458](https://github.com/Blato58/DildoVander/actions/runs/37154360458)
převzalo tento artefakt bez nového buildu, ověřilo původ PR, přesný main
strom, checksum i bezpečné rozbalení a aktivovalo release
`e4e936b9627eff687bab5c7a67f524938d1f0482-37154360458-1`.
Source SHA je CI merge commit; jeho strom odpovídá squash main commitu.
Oba deployment joby prošly. Aktivační helper potvrdil loopback health;
veřejný HTTPS test ověřil admin autentizaci, zákaz admin přístupu device
tokenem, revizní konflikt a diagnostiku, bez změny režimů či AI volání.

Read-only [snapshot 37154448427](https://github.com/Blato58/DildoVander/actions/runs/37154448427)
z 23:15 pražského času potvrzuje responses/openai, dostupné adaptéry,
0 aktivních rozhovorů a 0 droppedWrites. Dialog je gpt-6.1-sol, přímý
hlas v této verzi gpt-realtime-2.1/low; GPT-Live je gpt-live-1 s opraveným
řízením a novým tónem. Limit zůstává 3 USD/session. Přímý curl z cloud
workspace neověřil IP HTTPS (vrácené jméno certifikátu openai.com);
TLS kontrola nebyla vypnuta. Veřejné HTTPS je doložené úspěšným testem
na serverovém runneru, nikoli tímto curl pokusem.

Nová IPA ani firmware flash nejsou pro backendovou změnu potřeba;
stávající iPhone IPA 1000.0.14 používá nasazený backend. Placený provider
test nebyl spuštěn. Dostupnost Realtime 2.1 na provider účtu, česká
vtipnost/latence a fyzický poslech nejsou doložené pouhým health checkem.
Další krok: ve stávající aplikaci Debug → Tok → Realtime vyzkoušet nový
rozhovor a poslech; GPT-Live + Sol samostatně porovnat. Akceptace čekacích
výplní, návaznosti, ostrosti a oprav je v rešerši D135. Tento zápis mění
jen dokumentaci o už dokončeném nasazení; backend se znovu nenasazuje.

**D135 – 3. 10. 2026 – Realtime 2.1 a ostrý Ollivanderův kufr.**
Majitel žádá důkladnou rešerši před změnami, odstranění opakovaného čekání,
výrazné urážky a otázky při prodeji hůlky; následně upozornil na Realtime
2/2.1. [Rešerše D135](hlas-a-humor-research.md#d135-ostrý-prodavač-oprava-čekání-a-realtime-21)
obsahuje primární zdroje, příčiny v kódu a hranice důkazů. Read-only
snapshot [37152729168](https://github.com/Blato58/DildoVander/actions/runs/37152729168)
potvrzuje dva klientsky zrušené GPT-Live hovory; log neobsahuje jejich řeč.

`RealtimeProtocol` nyní používá `gpt-realtime-2.1` s low reasoning přes
JSON patch oficiálního SDK 2.14.0; wire serializace je regresně ověřená.
Konzervativní výstupní sazba 64 USD/1M pokrývá textové reasoning tokeny
24 USD/1M, limit 3 USD/session zůstává. Mobilní a ESP kontrakt se nemění.
`GptLiveDialogue` podrží delegaci před přepisem, po doplnění během Solu
sám zpracuje aktuální text a potlačí duplicitní práci stejné revize.
Zastaralý výsledek se započítá, neposune otázky a nesmí se vyslovit.
Oprava závěru pokračuje bez nové delegace, pokyn k závěru předchází
commentary. Deset backendových volání zahrnuje i přepočty nového vstupu;
nejde o retry neúspěšných placených requestů. GPT-Live zůstává gpt-live-1.

Sdílený `ai-eval/prompt.txt`, Live instrukce a `ConversationOpening`
vedou jedovatého, povýšeného prodavače hůlek s ostrým roastem jako
výchozím stylem, krátkými replikami a jednou navazující otázkou.
Šest opening námětů patří k použití hůlky a skutečnému chování hosta.
Prompt respektuje odmítnutí a konec hry; katalog se nepřepisuje.
Python eval opening, modelový údaj live harnessu, README a plán odpovídají.

Ověřeno lokálně: SDK 10.0.303, NuGet locked restore pro linux-x64,
Release build (0 warning/error), celá offline BackendTests sada včetně
nových scénářů pořadí, doplnění bez další delegace, duplicity, účtování,
opravy závěru a wire low reasoning. HTTP fake smoke prošel; Python AI eval
23 testů a backend tools 16 testů prošly. Whitespace a 35 místních odkazů
změněné dokumentace bez chyb. Provider účet, český humor/latence,
iPhone poslech ani ESP hardware nejsou tímto ověřené. Placený test,
cloudové nasazení, nová IPA ani flash nebyly provedeny.
Git předání: implementační commit `2ad0365` je pushnutý a shoda remote
ověřená. Předepsaný PowerShell skript skončil permission denied; pwsh
chybí, proto byl použit scoped gh credential helper pro účet Blato58.
[PR #112](https://github.com/Blato58/DildoVander/pull/112) obsahuje tento
slice a navazující záznam předání; výsledek povinného CI a merge je
ověřitelný přímo v PR. Zdrojová větev je `codex/realtime-21-roast-flow`.
Další krok: po nasazení ověřit Realtime 2.1 na účtu a skutečný rozhovor
na stávající IPA 1000.0.14, včetně nuly čekacích výplní, návaznosti,
ostrosti a reakce na opravu/odmítnutí; podle akceptace v rešerši D135.

**D134 – 3. 10. 2026 – oprava startu skutečného přehrávání.**
Majitel v 1000.0.13 doložil reproduktor, hlasitost 100 %, přijatých 10,0 s
PCM a 0,0 s potvrzeného výstupu při fungujícím přepisu mikrofonu. D133 tedy
slyšitelnost neopravil. Start doposud spouštěl prázdný player před ustálením
enginu a jeho clock při restartu neresetoval; readiness ověřovala jen vstup.
D134 obnovuje zastavenou cestu player → mixer → output v aktuálním výstupním
formátu, resetuje player a spouští jej až po naplánování bufferu. Před
placeným streamem vyžaduje dokončení 200ms tichého bufferu i posun player
sample time. Probe starého restartu/Stop se ignoruje; stejné původní
3 s / nejvýše tři restartovací pokusy platí i pro nefunkční výstup.

Za běhu se chybějící dokončení bufferů déle než 2,5 s mění na konkrétní
`realtime_live_audio_output_stalled` a úklid hovoru. Nové příchozí PCM
nesmějí tuto lhůtu odsouvat. Restart se provádí jen před provider streamem,
nikoli nad již naplánovanou řečí. Zachovává se AEC a full duplex GPT-Live.
Nativní regrese renderuje produkční graf do stereo 48 kHz ve třech startech,
ověřuje neprázdný výstup obou kanálů, mikrofon bez výstupu, bounded retry a
frontu bez dokončovacích callbacků. Mac offline render nepokrývá iOS voice
processing ani fyzický reproduktor. Lokálně prošlo všech 25 mobilních
testů, TypeScript/Vite a whitespace. Read-only snapshot 37119460245
potvrzuje dokončení Realtime odpovědi v 11:19:45 UTC a GPT-Live start
11:19:50 s klientským zrušením 11:20:14; žádná serverová chyba AI.
Commit `1142e18` je pushnutý scoped gh helperem po pokusu o předepsaný
PowerShell skript (permission denied; `pwsh` chybí).
[PR #110](https://github.com/Blato58/DildoVander/pull/110) je sloučený po
zeleném [CI 37119954641](https://github.com/Blato58/DildoVander/actions/runs/37119954641)
do main `fdc01d5`; shoda obsahu ověřená.

[Podpis 37120536523](https://github.com/Blato58/DildoVander/actions/runs/37120536523)
prošel všemi nativními testy včetně skutečného offline renderování produkčního
grafu po třech startech, Xcode device archivem a Ad Hoc exportem
**1.0 / 1000.0.14**.
[Publikace 37120810300](https://github.com/Blato58/DildoVander/actions/runs/37120810300)
zveřejnila IPA na [Safari instalaci](https://2.28.202.220/ios/); všechny tři
joby včetně veřejného HTTPS, manifestu, Bundle ID, MIME a SHA-256 prošly.
Backend a limit 3 USD/session se nemění; další placený AI test nebyl spuštěn.
Další krok: aktualizovat iPhone na 1000.0.14 a ověřit slyšitelný úvod i
odpověď GPT-Live. Při přetrvávající chybě zaznamenat nový kód nebo hodnoty
řádku Zvuk. Fyzická slyšitelnost tímto buildem ještě není potvrzená.

**D133 – 3. 10. 2026 – slyšitelnost GPT-Live na iPhonu.**
Majitel potvrzuje přepis mikrofonu i přechod na „Přehrávám“, ale neslyší
zvuk. Tento stav se nastavuje až po přijetí PCM z backendu; problém nelze
přisoudit chybějící odpovědi modelu. Při startupu nyní aplikace přepne
vestavěné telefonní sluchátko na reproduktor, pokud je voice-processing
setup vybral. Externí výstup respektuje. Aktuální výstup, systémovou
hlasitost a délku přijatých bufferů/potvrzeného renderování ukazuje Debug;
při hlasitosti pod 5 % přímo radí boční tlačítko během hovoru. Potvrzení
renderování nedokazuje slyšitelnost fyzického reproduktoru.

PCM16 dekodér je sdílený s nativním testem, před plněním nastavuje platný
počet rámců a čte little-endian bajty bez předpokladu zarovnání `Data`.
Test kontroluje nulu, obě polarity, krajní amplitudy, mono/24 kHz a vadné
buffery. Přehrávání kontroluje běžící engine. Lokálně prošlo 25 mobilních
testů, TypeScript/Vite a whitespace. Commit `c78ff69` je pushnutý scoped gh
helperem po pokusu o předepsaný PowerShell skript (permission denied;
`pwsh` chybí). [PR #108](https://github.com/Blato58/DildoVander/pull/108)
je sloučený po zeleném [CI 37117230606](https://github.com/Blato58/DildoVander/actions/runs/37117230606)
do main `dcc79ab`; shoda obsahu ověřená.

[Podpis 37117990047](https://github.com/Blato58/DildoVander/actions/runs/37117990047)
prošel nativními testy včetně nového PCM dekodéru, Xcode 26.3 device archive
a Ad Hoc exportem **1.0 / 1000.0.13**.
[Publikace 37118113098](https://github.com/Blato58/DildoVander/actions/runs/37118113098)
zveřejnila IPA na [Safari instalaci](https://2.28.202.220/ios/); všechny tři
joby včetně veřejného HTTPS, manifestu, Bundle ID, MIME a SHA-256 prošly.
[Backend deploy 37117991383](https://github.com/Blato58/DildoVander/actions/runs/37117991383)
aktivoval release `1a3e72e1ac6df0fec44c2e48e6b1676bdc203966-37117991383-1`
včetně D132 diagnostiky. Loopback health a veřejné ověření autentizace,
režimů/konfliktu revize i logů prošlo; konfigurace responses/Sol + openai
má 3 USD/session. Do fyzického ESP se nenahrávalo.

Příčina konkrétního ticha (hlasitost/cesta/renderování) ještě není fyzicky
potvrzená. Další krok: aktualizace iPhonu na 1000.0.13, poslech GPT-Live a
při přetrvávajícím tichu údaje z nového řádku Zvuk. Nativní test ani callback
přehrávače nejsou důkazem slyšitelnosti na zařízení.

**D132 – 3. 10. 2026 – diagnostika chybějící odpovědi GPT-Live.**
PR #106 (`8d29d01`) je pushnutý a sloučený do `ac16277` po zeleném CI
37116295704; obsah main ověřen. Info snapshot 37116861155 už úspěšně čte
nasazené logy. Ruční test 37116859928 skončil ještě před AI voláním na
právech setup-dotnet; workflow nově používá `runner.tool_cache` a stejný
`global.json` jako funkční backend CI. Majitel dále potvrzuje stav
„Přehrávám“ bez zvuku: mobil přijímá PCM; hlasitost/výstup iPhonu vyžadují
ověření. Není to důkaz nedostupnosti poskytovatele.

Majitel po D131 potvrzuje přepis vlastní řeči, ale neslyší úvod ani odpověď.
Vstup tedy funguje; výstup poskytovatele a přehrávání je třeba rozlišit.
Read-only warning snapshot 37115452795 zachytil GPT-Live HTTP 499 po
31,6 s (10:01:53 UTC); sám o sobě nedokazuje příčinu ticha. Info snapshot
37115120659 selhal na validaci route s `{streamId:guid}`. Reader nyní
přijímá dvojtečku a server normalizuje constraint před filtrováním úspěšných
uploadů. GPT-Live zapisuje stejné technické stream stavy jako Realtime,
bez audia či přepisů. Regrese pokrývají constraint, tiché 204 a viditelné 409.

Samostatný ručně spouštěný workflow `kufr-gpt-live-smoke.yml` používá
produkční transport/prompt s průběžným syntetickým tichem a úvodním pokynem.
Nevolá Sol, běží nejvýše 35 s (přibližně do 0,03 USD); vypisuje pouze počty
událostí, bajtů, znaků a finální délku. Spadá do majitelem schválených
3 USD/session. Běžné CI zůstává offline. Lokální backend test suite a čtyři
Python testy diagnostiky prošly. Oprava runneru `2239171` prošla
[CI 37116948134](https://github.com/Blato58/DildoVander/actions/runs/37116948134)
a [PR #107](https://github.com/Blato58/DildoVander/pull/107) je sloučený do
`09f7351`; shoda obsahu ověřená. Následný skutečný
[test 37117504470](https://github.com/Blato58/DildoVander/actions/runs/37117504470)
**prošel**: 100 vstupních bloků, 189 výstupních audio událostí, 904 320 B PCM,
z toho 297 600 B v blocích s řečí (~6,2 s), 22 fragmentů/111 znaků přepisu.
Pokyn byl potvrzený, session řádně finalizovaná, usage 19 s (~0,015834 USD),
bez chyby a bez volání Solu. Obsah řeči nebyl vypsaný ani uložený.
Poskytovatel tedy úvod vytváří; výstup telefonu řeší D133. Diagnostika sama
není důkazem slyšitelnosti na iPhonu.

**D131 – 3. 10. 2026 – stabilizace iOS zvukového enginu před AI hovorem.**
Majitel v nové IPA 1000.0.11 doložil `realtime_live_audio_engine_stopped`.
D130 tedy neodstranilo fyzickou chybu; nový kód rozlišil zastavený engine
při shodné cestě, kategorii, režimu i vstupním formátu. Read-only
[diagnostika 37113745650](https://github.com/Blato58/DildoVander/actions/runs/37113745650)
potvrzuje dva klientem zrušené GPT-Live požadavky v 09:37:55/09:38:03 UTC,
HTTP 499 po 201/70 ms. Backend běží v responses/openai, žádný aktivní hovor.
Apple u [AVAudioEngineConfigurationChangeNotification](https://developer.apple.com/documentation/avfaudio/avaudioengineconfigurationchangenotification)
popisuje automatické zastavení a odinicializování enginu při konfiguraci
I/O; aplikace odpovídá za nové spuštění a případnou opravu formátů.

`DebugHandsFreeAudio.start` nově čeká na skutečné mikrofonní buffery a
300 ms zdravého běžícího enginu, před `session.run` a placeným hlasovým
streamem. Počáteční zastavení obnoví nejvýše třikrát do absolutního limitu
3 s, pouze při shodné cestě/režimu/formátu a zapnutém AEC pro GPT-Live.
Oznámení nesmějí prodlužovat deadline. Starý buffer před restartem ani
zaseknutý tap nesmí potvrdit připravenost. Engine-configuration observer
přesouvá obsluhu z callbacku do main queue. Po dokončení startu se zastavení
nerecoveruje, aby se neztrácelo naplánované audio. Skutečné odpojení, hovor,
změna formátu, reset médií, Stop/pozadí a cancellation dále ukončují práci.
`KufrDebugPlugin` po čekání znovu kontroluje generation; stavové kódy
zůstávají zachované. Nový kód označuje nestabilní/nedokončený start.

Nativní regresní scénáře modelují úspěšný synchronní start následovaný
zastavením, omezený restart a nová živá data, reset připravenosti při změně,
zastaralý buffer, chybějící tap, opakované zastavení a nepřekročitelný deadline.
Lokálně prošlo 25 mobilních testů, TypeScript/Vite build, whitespace a
místní odkazy změněných Markdown souborů. Commit `94679e7` je pushnutý
scoped gh helperem po pokusu o předepsaný PowerShell skript (permission
denied; `pwsh` chybí). [PR #104](https://github.com/Blato58/DildoVander/pull/104)
je sloučený po úspěšném [CI 37114100057](https://github.com/Blato58/DildoVander/actions/runs/37114100057)
do main `95ee64c`; shoda obsahu ověřená.

[Podpis 37114615069](https://github.com/Blato58/DildoVander/actions/runs/37114615069)
prošel nativními WSS/debug/hands-free testy včetně nových scénářů zastavení
enginu a stabilizace, PCM převodu/fronty, rušení a úklidu. Xcode 26.3
úspěšně vytvořil device archive a ověřený Ad Hoc export **1.0 / 1000.0.12**.
[Publikace 37114734105](https://github.com/Blato58/DildoVander/actions/runs/37114734105)
úspěšně zveřejnila release `37114734105-1` na
[Safari instalaci](https://2.28.202.220/ios/). Kontrola veřejného HTTPS
ověřila manifest, Bundle ID, stažení IPA, SHA-256 i MIME typy.

Fyzické zopakování GPT-Live na iPhonu je stále nutné. Testy stavu a device
build nejsou důkazem zvukového chování konkrétního iOS zařízení. Backend
s 3 USD/session i firmware zůstávají beze změny; nový build obnovuje pouze
start lokálního zvuku, nezakládá opakované placené AI hovory.


**D130 – 3. 10. 2026 – oprava kontroly zvuku při startu GPT-Live na iOS.**
Majitel po publikaci D129 doložil screenshot `realtime_live_audio_interrupted`
a potvrdil chybu hned před první replikou. Read-only
[diagnostika 37112031423](https://github.com/Blato58/DildoVander/actions/runs/37112031423)
ukazuje dva zrušené `/gpt-live` požadavky v 09:00:28 a 09:00:39 UTC
(11:00 Praha), HTTP 499 `operation_cancelled`. Logy neobsahují důvod
nativní změny zvuku; samotný screenshot nedokazuje konkrétní iOS reason.

V `DebugHandsFreeAudio.swift` všechny route reasons mimo category/override/
configuration dosud vždy ukončily hovor. Zejména `newDeviceAvailable`
neznamená změnu aktivní cesty a může doprovázet inicializaci voice processing.
Oprava porovnává i pro availability/wake/unknown oznámení běžící engine,
aktivní vstup/výstup, kategorii, režim a PCM formát. Shodná konfigurace
pokračuje; změna, ztráta zařízení, chybějící/neznámý reason nebo nevhodná
cesta zastavují. Skutečná interruption a reset služeb zůstávají fatální.
Žádné časové okno ignorování ani automatický opakovaný placený rozhovor.
`DebugPanel.ts` rozlišuje technické kódy přerušení, cesty, režimu, formátu,
zastaveného enginu a resetu služby pro případné další fyzické ověření.
Nativní harness pokrývá sekvenci startup oznámení, změněnou konfiguraci
a disconnect/reconnect se zdánlivě stejnou aktuální cestou.

Lokálně prošlo 25 mobilních Node testů, TypeScript/Vite build,
`git diff --check` a odkazy změněné dokumentace. Commit `5e75d66`
je ověřeně pushnutý scoped gh helperem po neúspěšném spuštění předepsaného
PowerShell skriptu (permission denied; `pwsh` chybí).
[PR #102](https://github.com/Blato58/DildoVander/pull/102) prošlo všemi
kontrolami [CI 37112283414](https://github.com/Blato58/DildoVander/actions/runs/37112283414)
a je sloučené do main `254ee80`; shoda testovaného obsahu ověřená.

[Podpis 37112846628](https://github.com/Blato58/DildoVander/actions/runs/37112846628)
z main `254ee80` úspěšně spustil nativní WSS/debug/GPT-Live regresní testy,
nové route-notification scénáře, resampling 48/44,1 kHz, PCM batching,
rušení a úklid. Prošel Xcode 26.3 device archive, Ad Hoc export a validace
IPA **1.0 / 1000.0.11**. [Publikace 37112965017](https://github.com/Blato58/DildoVander/actions/runs/37112965017)
úspěšně zveřejnila release `37112965017-1` na
[Safari instalaci](https://2.28.202.220/ios/). Veřejná HTTPS kontrola
potvrdila manifest, Bundle ID, stažení IPA, SHA-256 a MIME typy.

Nutné aktualizovat aplikaci přes Safari a znovu spustit GPT-Live na iPhonu.
Nativní testy nepotvrzují konkrétní původní iOS reason ani odstranění chyby
na fyzickém telefonu; přesnější kódy rozliší případný další problém.
Backend s 3 USD/session z D129 zůstává nasazený, firmware se nemění.


**D129 – 3. 10. 2026 – autorizované nasazení D128 a kompatibilita helperu.**
Majitel výslovně požádal „nasaď“. Zdroj je sloučený PR #99 / main
`a447be0`, úspěšné CI `37067443145`. Backend deploy `37110204278`
ověřil artefakt, ale starší root-owned helper odmítl nový payload:
`All three cost caps are required together`. Chyba nastala před změnou
prostředí/symlinku/restartem; původní backend zůstal zachovaný.
Workflow proto předává také `daily/monthly` kompatibilitní hodnoty.
D128 backend je ignoruje, hlavní limit zůstává OpenAI a session 3 USD.
Nový test ověřuje, že ani staré periodické hodnoty 1 microUSD neobnoví
aplikační globální stropy. Backend README popisuje migraci bez požadavku
na další root oprávnění nebo změnu nainstalovaného helperu.

Oprava je v commitu `622eba7`, ověřeně pushnutém scoped gh helperem
pod Blato58 po neúspěšném spuštění předepsaného PowerShell skriptu
(permission denied, `pwsh` v Linuxu chybí). Lokálně prošel celý backend
suite (.NET 10), tři deploy-helper testy a příjem kompatibilního payloadu
helperem před D128. [CI 37110422816](https://github.com/Blato58/DildoVander/actions/runs/37110422816)
prošlo všemi povinnými kontrolami; [PR #100](https://github.com/Blato58/DildoVander/pull/100)
je sloučené do main `e2d3f4c`, shoda obsahu s testovanou větví ověřená.

**Nasazení dokončené:** [backend 37111017936](https://github.com/Blato58/DildoVander/actions/runs/37111017936)
aktivoval release `40e84a0c499b55635ea4e9114eb1fc0836ccbd95-37111017936-1`.
Prošel loopback health check i veřejná HTTPS kontrola autentizace,
režimů a diagnostiky, bez placených provider volání. Read-only
[diagnostika 37111115513](https://github.com/Blato58/DildoVander/actions/runs/37111115513)
ověřuje běžící server po aktivaci. Session konfigurace je 3000000 microUSD;
legacy denní/měsíční hodnoty se podle D128 ignorují.

Podepsaný [iOS build 37110205151](https://github.com/Blato58/DildoVander/actions/runs/37110205151)
z main `a447be0` prošel nativními WSS/debug/GPT-Live testy, device buildem,
Ad Hoc podpisem a kontrolou exportované IPA. [Publikace 37110367270](https://github.com/Blato58/DildoVander/actions/runs/37110367270)
zveřejnila **1.0 / 1000.0.10** na [Safari instalaci](https://2.28.202.220/ios/).
Veřejný manifest ukazuje na release `37110367270-1`; workflow ověřilo
Bundle ID, stažení IPA a SHA-256 přes HTTPS. Přímý curl z tohoto cloud
prostředí narazil na nesoulad TLS jména na proxy; ověření TLS nebylo vypnuté.
Úspěšné veřejné HTTPS ověření pochází z publikačního runneru. Oprava
backend deploy workflow nemění iOS ani firmware, proto není potřeba
druhý podpis stejné aplikace. Fyzická instalace na iPhonu a poslech
českého GPT-Live ještě nejsou potvrzené.

ESP32 není připojené přes USB k tomuto cloud prostředí, firmware není
flashnutý. Ověřený artefakt nového režimu je v CI; runtime HW akceptace
zůstává otevřená. Další krok pro majitele: aktualizovat aplikaci přes
Safari, zvolit **Debug → Tok → GPT-Live + Sol** a porovnat kvalitu hlasu.
Firmware se nahraje až v prostředí s přístupem ke konkrétní desce.


**D128 – 2. 10. 2026 – GPT-Live + Sol v aplikaci/firmware, 3 USD/session.**
Majitel navázal na D127 požadavkem na samostatně přepínatelný GPT-Live pro
vlastní porovnání a společný limit 3 USD na každou session. Backend nyní
má izolovaný Live WebSocket adaptér, Responses Sol delegace, omezené
PCM/transkripty, rušení zastaralého výsledku, potvrzené uzavření a výpočet
délka + Sol. Pinned .NET SDK 2.14.0 Live nemá; zdůvodněná výjimka je
zapsaná v plánu/AGENTS. Wire schéma porovnáno s oficiální dokumentací a
Python SDK 3.22.1. Neproběhl fine-tuning ani živá provider evaluace.

Mobilní Debug nabízí GPT-Live + Sol a používá Apple voice processing pro
současné přehrávání/snímání. Hlas kufru přepíná `mode:gpt-live` přes
local control, ESP používá samostatné `/gpt-live` routy. Na ESP se během
řeči posílá ticho, protože AEC není ověřené. Původní Realtime zůstává.
Změna režimu vyžaduje neaktivní hovor. Autonomní návrh prochází původní
bránou. Live nehlásí audio-done: 900ms výstupní pauza je heuristika,
nikoli důkaz kompletně vyslovené repliky. Timeout je 180 s. Oprava před
finalizací ruší závěr; pozdní upload se po session.close nepošle provideru.

Backend i AI eval používají 3 USD/session, deployment přepisuje starý
session secret hodnotou 3000000. Denní/měsíční nastavení se ignorují;
auditní součty zůstávají. Party eval nemá výchozí souhrnný run cap,
každý profil má vlastní 3 USD. Nadřazený limit zůstává v OpenAI.
Při nejasném ukončení zůstává rezervace, nikoli tvrzení o skutečné útratě.

Lokálně prošly kompletní backendové testy v .NET 10 (nově Live wire,
delegace, čtyři otázky, finální usage, přerušení závěru, Stop a pozdní
upload), 23 Python eval testů, 15 backendových Python testů a 3 deploy
testy. Prošly kontrakty, 25 mobilních Node testů a TypeScript/Vite build.
Host C++ hlasové testy ověřily oba endpointy, průběžný vstup, pause/drain,
ACK, Stop a původní Realtime. Oba PlatformIO profily ESP-IDF a Arduino
se sestavily. Nativní Swift fake peer byl rozšířen o GPT-Live; tento Linux
nemá Swift/Xcode, takže nativní kompilace a běh čekají na ruční iOS build.
Živá dostupnost GPT-Live pro účet, čeština, humor, ozvěna iPhone/ESP a
časování závěru nebyly offline ověřeny. Není hotová HW akceptace.
Finální kontrola doplnila spojování sousedních přepisových fragmentů
a předávání skutečně vyslovených replik včetně první otázky do Solu;
příslušný wire test ověřuje i dělení slova mezi dvěma událostmi.
Tato kontrola je v commitu `ad01e11`, také ověřeně pushnutém. README
kontraktů popisuje nové režimy i Live routy.

Další krok: po CI a merge nasadit backend a vydat novou iOS aplikaci /
flashnout odpovídající firmware v samostatně autorizovaném kroku; pak
provést ruční porovnání podle [návodu](hlas-a-humor-research.md#d128-testovaci-gpt-live-rezim).
V tomto slice nebyl spuštěn deploy, placený test, podpis IPA ani flash.
Implementační commit `9df9888` je ověřeně pushnutý na
`origin/fix/natural-openings-carlin-humor` (remote SHA shodné). Předepsaný
PowerShell push skript byl spuštěn, ale není spustitelný a `pwsh` chybí;
push provedl scoped gh credential helper pod ověřeným účtem Blato58.
[PR #99](https://github.com/Blato58/DildoVander/pull/99) zahrnuje D127 i D128.
Prošla také kontrola změněných Markdown odkazů, 29 CI provenance testů,
syntax Node fake peeru a whitespace. Výsledek CI a merge musí odpovídat
finálnímu head PR a je dohledatelný přímo v PR; nativní iOS/živý hlas tím
nejsou ověřené.


**D127 – 2. 10. 2026 – přirozený opening, CARLIN a rešerše hlasových agentů.**
Majitel požádal o skutečně vtipnější AI, opravu předstíraného pokračování
rozhovoru a stále stejné první otázky; prioritou je kvalita. Přečten článek
Greg Robisona a oficiální dokumentace OpenAI (včetně GPT‑Live), ElevenAgents
a LiveKit. [Rešerše](hlas-a-humor-research.md) obsahuje doporučení, omezení
a plán slepého českého poslechu. Aktuální Realtime již je hlasový agent;
GPT‑Live + Sol je doporučený kandidát srovnání, nikoli provedená migrace.

`ConversationOpening.cs` střídá šest námětů v procesu backendu. Textový
a Realtime opening neodesílají paměť ani historii novému neidentifikovanému
hostovi; další tahy zachovávají dosavadní recall. Archiv se nemaže. Realtime
námět drží po celý tah včetně přepočtu rezervace. `prompt.txt` odstranil
povinné jméno, rozlišuje představení role od předchozí konverzace a adaptuje
CARLIN (různé úhly, doložený detail, jedna krátká pointa). Příklady již
nepředvádějí dvě otázky v rozporu s pravidlem. Python eval sdílí izolaci
opening, nikoli procesovou rotaci C#. Plán a README byly synchronizovány.

Lokálně prošel celý `dotnet run --project kufr/backend-tests/BackendTests.csproj
--no-restore` v připraveném .NET 10 prostředí: včetně nových kontrol skutečného
Realtime opening payloadu bez user zprávy, izolace textového opening, návratu
paměti v dalším tahu, šesti různých námětů a stability při rezervaci. Prošlo
22 Python offline eval testů, kontrola lokálních odkazů pěti dotčených
dokumentů a `git diff --check`. Nebyl použit provider klíč
ani spuštěn placený test, nasazení či změna firmware/telefonu. Kvalita
humoru, českého hlasu a latence není offline testy potvrzená; další krok je
živé slepé srovnání podle rešerše v explicitně stanoveném rozpočtu.
Implementační commit `2aadd7fefb3a8c9e9c6b8e98715dfb3fc66af982` je ověřeně
pushnutý na `origin/fix/natural-openings-carlin-humor` (remote SHA shodné).
Předepsaný `./scripts/Push-CurrentBranch.ps1` byl spuštěn, ale není spustitelný
(permission denied); PowerShell zde chybí. Push provedl scoped gh credential
helper s ověřeným účtem Blato58. [PR #99](https://github.com/Blato58/DildoVander/pull/99)
obsahuje zdroj i rešerši; CI pro aktuální head je podmínkou merge, nikoli
důkazem živé kvality. Výsledek CI/merge je dohledatelný v PR.


**D126 – 2. 10. 2026 – parametrický 3D tiskový prototyp mechaniky kufru.**
Majitel požádal o modely krabiček, šesti přihrádek a nadzvednutí celé
přihrádky včetně usazení vibrací a elektroniky. Upřesnil kufr
450 × 310 × 200 mm a hůlky nejvýše 200 mm; vnitřní rozměry, průměr
hůlek a tiskárna dosud nejsou potvrzené. `kufr/mechanika/` obsahuje
CadQuery 2.7.0 zdroj, pracovní JSON parametry, offline náhled a kontrolu
skutečných STL. Rozteč modulů je 48 mm, vložky 240 × 28 mm, dělené
na tři 80mm segmenty. Servo klika s válečkem v příčné drážce a dvěma
vodicími tyčemi navrhuje zdvih 28,28 mm. Krabičky/rastry, police,
držák powerbanky, panel ovladačů, U059 klece a lůžko WS2812B vycházejí
z nákupního seznamu; většina obálek a montážních bodů jsou odhady.
Volitelný router má samostatnou krabičku, není osazen v kufrové sestavě.
Firmware, kontrakty a zapojení se nemění.

Lokální generace vytvořila 45 druhů jednoobjemových validních BREP
dílů, STL/STEP jednotlivých dílů i dvou sestav, CSV kusovník,
PNG, samostatné HTML a ZIP. Výstupy jsou ignorované v Git; zdroj
a návod jsou verzované. Kontrola 11 poloh vedení a rotující kliky
odhalila průnik kliky s okrajem servo držáku; po otevření čela držáku
prošla. Kontrola obálky sestavy v pracovním prostoru kufru a všech
dílů v předpokládané tiskové obálce 220 × 220 × 250 mm prošla.
Samostatné ověření 45 skutečných STL prošlo: každá hrana má dva
sousední trojúhelníky, kladný orientovaný objem a shodu s BREP do 2 %.
Navazující kontrola všech pohyblivých dílů proti sobě i proti pracovním
obálkám LED/U059 zachytila kolize spojovacích pásků s klecí a followerem.
Klec byla přesunuta do prvního segmentu, spodní podpěry pod spoje,
úchyty followeru mimo spoj a sedla dostala volný průchod RGB. Finální
generace 45 dílů prošla všemi uvedenými kontrolami včetně těchto průniků.
Chromium/Playwright ověřil vykreslení HTML, změnu obrazu zdvihem,
horní hodnotu 28,3 mm a pohled shora bez JS chyb. Proběhla také
kontrola Python syntaxe, lokálních dokumentačních odkazů a whitespace.

**Není dokončená E5/E6 ani potvrzené fyzické vejití do truhly.**
Klika je zpětně pohánitelná a prototyp nemá ověřené pasivní zachycení
při odpojení energie; požadavek plánu na stabilitu bez proudu zůstává
otevřený. Nejsou ověřené nosnost, kabelové smyčky, tepelný provoz,
montáž továrních dílů ani izolace vibrací vůči sousedním přihrádkám.
Další mechanický krok: změřit vnitřek/víko kufru, průměr hůlek,
servo/U059/PCB/konektory a powerbanku, upravit pracovní obálky,
vytisknout měrku a jeden modul, doplnit pasivní podporu a ručně
ověřit celý pohyb. Elektrické blokace a další aplikační kroky z D124/D125
zůstávají platné. Implementační commit
`d6b70ca6a0b84276b6a9f31beb857a7092143560` byl ověřeně pushnutý
do `origin/feat/printable-case-mechanics`; remote SHA se shoduje.
Předepsaný `./scripts/Push-CurrentBranch.ps1` byl spuštěn, ale není
spustitelný (permission denied) a PowerShell zde chybí. Push provedl
scoped gh credential helper pod ověřeným účtem Blato58.
[PR #98](https://github.com/Blato58/DildoVander/pull/98) předává zdroj
a dokumentaci. Před merge musí projít `CI required` pro aktuální head;
skutečný výsledek je dohledatelný v PR. Geometrický test CI ani merge
nenahrazuje. ZIP navíc prošel kontrolou integrity, aktuálnosti zdrojů
a úplnosti 45 STL / 45 STEP, dvou sestav, návodu, náhledů a kusovníku.

**D125 – 2. 10. 2026 – iOS build pouze ručně pro podepsané nasazení.**
Majitel požádal odstranit iOS build z každého PR a ponechat jej pouze ručně
pro podepsanou aplikaci určenou k nasazení. `ci.yml` nyní používá webový
`kufr-web.yml`; mobilní/kontraktové testy, TypeScript/Vite build a artefakt
`web-dist` zůstávají povinné v `CI required`. Původní `kufr-ios.yml`
a unsigned simulator build/spuštění byly odstraněny.
`kufr-ios-sign.yml` je nadále pouze ruční a main-only, ale používá výhradně
Ad Hoc distribuční identitu. Přebírá ověřený web z CI, kontroluje Xcode 26.3
a provádí přesunuté nativní transport/debug testy před podepsaným device
buildem. Vývojová podpisová volba byla odstraněna. Ruční
`kufr-ios-deploy.yml` nadále publikuje existující ověřenou Ad Hoc IPA bez
nového buildu. Plán, mobilní README a CI/CD návod odpovídají tomuto postupu.
Lokální ověření prošlo: actionlint 1.7.12 s ověřeným checksumem nad všemi
workflow; 29 Node testů `scripts/ci/verify-build.test.cjs`; 6 Python testů
archivů v `scripts/ci`; průchod grafu všech pěti PR workflow bez macOS,
Xcode/simctl/cap sync ios a kontrola ručního main-only Ad Hoc sign/deploy
včetně správných secret/manifest vazeb. Lokální odkazy všech čtyř změněných
dokumentů a `git diff --check` prošly. Implementační commit
`ae1c24f2d75ae28fe91e2dbbf5bbe3150eb6da15` je pushnutý na
`origin/ci/manual-ios-deployment`; remote SHA bylo ověřeno. Předepsaný
`./scripts/Push-CurrentBranch.ps1` byl spuštěn, ale není zde spustitelný
(permission denied) a `pwsh` chybí. Push provedl scoped gh credential
helper pod ověřeným účtem Blato58. Dokumentační commit
`b477b4bae1c429c779b8a7afe570f842e879ff19` je také ověřeně pushnutý.
[PR #97](https://github.com/Blato58/DildoVander/pull/97) obsahuje finální
změnu a CI. První běh `37019862812` pro tuto revizi ověřil repository,
backend, firmware host testy, mobilní web a offline AI bez macOS;
firmware build ještě běžel. Tento navazující dokumentační commit mění
číslování na D125 kvůli již otevřenému D124 v PR #96. Před merge musí
projít `CI required` pro aktuální head; výsledek a merge jsou dohledatelné
v PR. Nativní runtime ověření se přesouvá až k ručně požadovanému vydání.
Při integraci main `003fc307538d9086576557dd76c000c09a12dae4` (PR #96)
byl zachován celý D124 i jeho diagnostické zdroje/workflow; tento D125
zůstává samostatný. Po merge main musí projít CI pro nový head.
Nativní build/podpis ani skutečná publikace nejsou součástí této změny.
Další krok: při požadavku na vydání aplikace ručně podepsat IPA z úspěšného
CI aktuálního main a následně spustit samostatnou publikaci.

**D124 – 2. 10. 2026 – USB test logiky PCA9685 a návrat aplikace.**
Majitel již má F-F propojky a hlásí hotové čtyřvodičové propojení podle
profilu A: ESP3V3/PCA VCC, GND/GND, GPIO1/SDA, GPIO14/SCL. Fotografie ani
měření tohoto propojení zatím nejsou dodané; není to nezávislá inspekce.
USB-UART COM7 znovu odpovídá, ROM ověřil MAC `68:ee:8f:4b:60:48`.
V rámci dříve autorizovaného sestavování/flash vznikl samostatný
`firmware/diagnostics/pca9685-logic`: pouze probe 0x40 a pět čtení MODE1/2,
GPIO1/14, 100 kHz; žádné hodnoty registrů/PWM/OE, NVS ani síť se nezapisují.
V+, pohony, OE a externí napájení zůstávají odpojené. Čtecí review ověřilo
pointer/repeated-START/read podle NXP a skutečného ESP-IDF 6.1 driveru;
nenalezlo blokující chybu. Kód neověřuje unikátní identitu čipu a PASS
znamená jen ACK a stabilní čtení, nikoli shodu resetových hodnot.

`pio run -d kufr/firmware/diagnostics/pca9685-logic -j 2` prošlo za 137,21 s
(RAM 14 652 B, flash 174 629 B); upload přes COM7 prošel a esptool ověřil
zapsané obrazy. Diagnostický `firmware.bin` má SHA-256
`114d99192a3e453831bed309e86f63f7ba937094a5a08729372e46b66fffd23e`.
Oba EN/RTS restarty s 20s UART záznamem prošly: ACK 0x40, všech pět
vzorků MODE1=0x11 / MODE2=0x04 (celkem deset), RESULT PASS a heartbeat,
bez panic/brownout či nečekaného restartu. Hodnoty odpovídají resetovým
defaultům NXP; test však nebyl úplné odpojení napájení ani osciloskopické
měření. Potvrzuje pouze komunikaci připojeného modulu na GPIO1/14/100 kHz.

Pak byl znovu nahrán aplikační profil D123; hash `firmware.bin` zůstává
`065f6aba2edcc3c90e29cbcb62e80ba13e6704d74baca6b9eb095f20301374af`.
Restored app prošla 30s boot záznam: PSRAM test OK, offline control loop,
neprovisionovaná identita/síť, bez restartu. NVS 0x9000/0x5000 se před
diagnostikou a po návratu shoduje SHA-256; diagnostika ji nezměnila.
Obrazy/ELF, build/upload/restore logy, dva logic boot logy, restored app
log a NVS readbacky jsou v `pca9685-logic` místní složky z D122.
`manifest.json` obsahuje parametry testu a hashe důkazů; binární soubory
nejsou v Git. Změny: samostatný projekt, firmware README, tento stav a
nový build krok/cache vstup v firmware CI. YAML, lokální odkazy a
`git diff --check` prošly. Firmware aplikace zůstává beze změny.

Další fyzický krok: opatřit/půjčit multimetr a změřit 5V výstup XY3606
před připojením elektroniky. Majitel dosud nemá multimetr ani páječku.
PCA logické propoje mohou zůstat osazené; serva, motory a napájení V+
zatím nezapojovat. Ještě chybí měřená bezpečnostní větev a kalibrace;
test nedokazuje OE/PWM, napájecí rezervu ani ovládání pohonů. B1 zůstává
otevřené a produkční aplikace dosud nemá PCA aktuátorový adaptér.
Commit `16d879ab4ad045d9e56aeebd8eba0ae8e3bba41b` prošel push přes
`scripts/Push-CurrentBranch.ps1` do `feat/pca9685-logic-diagnostic`.
Před merge musí PR projít `CI required` podle CONTRIBUTING.

**D123 – 2. 10. 2026 – první aplikační flash a začátek sestavování.**
Majitel výslovně požádal začít sestavovat ESP a nahrát firmware; všechny
komponenty má zabalené, zatím nejsou propojené. Po znovupřipojení pouze
USB-UART COM7 byl znovu ověřen MAC `68:ee:8f:4b:60:48` a čtením flash
potvrzená prázdná oblast NVS `0x9000..0xdfff` (20 480 B, vše FF).
Čtecí audit startupu potvrdil, že prázdná NVS nezapne síť ani audio GPIO;
kalibrace 0 a neověřená inspection stále zakazují zotavení/pohyby.

Bez změny zdrojového kódu byl sestaven a nahrán současný aplikační profil
`esp32-s3-idf` ze source commitu `f23853e3ce310b572dd81b9cdc54bdbafeb874b6`.
První Windows build narazil na interní pád GCC v nepoužívaném SDK LCD
driveru. Opakování `pio run -e esp32-s3-idf -j 2` prošlo: RAM 66 032 B,
flash 1 202 041 B. `pio run -e esp32-s3-idf -j 2 --target upload --upload-port COM7`
prošlo; esptool ověřil hash všech zapisovaných obrazů. Nový aplikační
`firmware.bin` má SHA-256
`065f6aba2edcc3c90e29cbcb62e80ba13e6704d74baca6b9eb095f20301374af`.
Původní plná factory záloha a diagnostické obrazy z D122 zůstávají uložené.
Nové obrazy/ELF, build/retry/upload logy, NVS readback a dva boot logy
jsou v podsložce `application-first-boot` místní složky uvedené v D122;
`manifest.json` obsahuje source commit, hash souborů a výsledek bootů.

Oba řízené EN/RTS restarty s 30 s UART capture prošly: bootloader 16 MB,
8 MB Octal PSRAM/80 MHz a SDK memory test OK; požadavek vypnout pohony
(`no GPIO adapter`), neprovisionovaná identita, nenastavená síť a start
ESP-IDF control loop. Žádný neočekávaný restart, panic, brownout, watchdog
ani journal chyba v obou záznamech. To potvrzuje pouze základní offline
aplikační boot. Neprokazuje skutečné vypnutí pohonů, servo/motor/LED
adaptér, zvuk, Wi-Fi/TLS/WSS, provisioning ani mechanickou akceptaci.
Není nastaveno `profile_a_ok`, síť, identita ani jakýkoli credential.

Majitel potvrdil, že nemá multimetr. Modely jsou podle
[nákupního seznamu](../../docs/nakupni-seznam-kufr.md): PCA9685 Drátek 1686,
PD trigger 232511 a XY3606 180391 s pevným výstupem 5 V. Bez měření
výstupu zůstávají PD trigger/měnič a výkonové větve odpojené; tím není
blokované sestavování logiky napájené pouze USB.
Další krok: při odpojeném USB připravit čtyři logické propoje ESP32/PCA9685
podle profilu A: 3V3/VCC, GND/GND, GPIO1/SDA, GPIO14/SCL. Servo V+, OE
a všechny výstupy zatím nezapojovat. Nákupní seznam obsahuje Dupont M-F;
pro dvě osazené samčí lišty jsou potřeba F-F nebo správně pájené propoje.
Majitel potvrdil, že nemá F-F propojky ani páječku. První logické zapojení
čeká na čtyři F-F propojky; původní M-F nákupní položka tuto dvojici
osazených lišt přímo nepropojí. Firmware dosud nemá PCA GPIO/I2C adaptér a toto je
návrh dalšího kroku, nikoli provedený I2C test. B1 zůstává otevřené pro
bezpečnostní větev a periferie; bezpečnostní obvod není v původním košíku.
Tento krok pouze zaznamenává skutečný flash/boot; nové zdrojové změny
firmware ani placené AI/serverové nasazení nejsou jeho součástí.
Dokumentace prošla kontrolou lokálních odkazů a `git diff --check`.
Commit `426b5ee05eed5a751d47e23b55057392f94ab766` byl úspěšně pushnutý
přes `scripts/Push-CurrentBranch.ps1` do `docs/esp-application-first-boot`.
PR vyžaduje zelené `CI required` před sloučením podle CONTRIBUTING.

**D122 – 2. 10. 2026 – první USB flash a paměťový test doručené desky.**
Majitel dodal fotografie ESP32-S3 N16R8 a výslovně schválil minimální
diagnostický firmware a jeho nahrání. Připojený USB-UART má COM7,
VID 1A86/PID 55D3. Čtecí ROM dotazy potvrdily ESP32-S3 revizi čipu v0.2,
8 MB embedded PSRAM, 16 MB Quad flash a 3,3 V flash; secure boot a flash
encryption jsou vypnuté. Revize čipu není revize PCB. Zadání je pouze
USB diagnostika paměti a bootu, bez externích periferií či pohonů.
Samostatný projekt `firmware/diagnostics/board-memory` byl sestaven,
nahrán a ověřen na této fyzické desce. Obsahuje pouze paměťový test a
UART log; neobsahuje aplikaci kufru ani provisioning či externí periférie.
Změny zahrnují jeho CMake/PlatformIO/config/src/README, firmware README,
pinový podklad, tento stav a samostatný build krok ve firmware CI.

Před prvním zápisem byla čtením ROM/stub uložena celá původní flash
(16 777 216 B), bez erase-all a bez zápisu eFuse. Záloha a logy jsou mimo
Git v `C:\Users\vojtech.petrik\AppData\Local\DildoVander\board-diagnostics\68ee8f4b6048\2026-10-02\`.
`factory-flash-16mb.bin` má SHA-256
`d73b6dd5a15e469a30629798664fb6e9e5c0ee30ef2679d75858d97a72b6fb15`.
Složka obsahuje i `manifest.json`, build/upload log, `boot-1.log` až
`boot-3.log` a kopie diagnostických obrazů/ELF. Diagnostický `firmware.bin`
má SHA-256 `ef803c0b834ba45c31fab953364297a776ead34329ae6009398b3a6fac4e9f47`.

Ověření: PlatformIO Core 6.2.0, espressif32 7.1.3 / ESP-IDF 6.1.0,
`pio run` v diagnostickém projektu – SUCCESS, RAM 15 820 B,
flash 161 325 B; první konfigurace potřebovala zahrnout syntetický
`__pio_env` komponent PlatformIO. `pio run --target upload --upload-port COM7`
– SUCCESS, esptool ověřil hash bootloaderu, partition table a aplikace.
Bezprostředně před zápisem byl znovu ověřen MAC `68:ee:8f:4b:60:48`.
Tři řízené EN/RTS restarty, každý s 35 s UART záznamem, všechny PASS:
16 777 216 B flash, 8 388 608 B PSRAM, Octal PSRAM 80 MHz,
SDK startup memory test OK, všechny čtyři adresně závislé XOR vzory
nad 7 340 032 B, heap integrity PASS a pět heartbeatů bez neočekávaného
resetu, panic, brownoutu nebo watchdog chyby. Interní volný heap
393 852 B a externí 8 386 308 B zůstaly během heartbeatů stabilní.
Kontrola lokálních README odkazů, YAML parsování a `git diff --check`
prošly. Nezávislá čtecí revize nenašla blokující chybu; PASS neznamená
hustý test celých 8 MiB (SDK boot test vzorkuje po 32 B), dlouhodobý/tepelný
test, endurance flash ani přímé ověření nativního USB-OTG.

B1 zůstává otevřené pro bezpečnostní větev, zapojení, kalibraci a fyzické
efekty; dnešní souhlas platí pro tento diagnostický obraz.
Další konkrétní krok: určit a změřit externí napájení a PCA OE,
servisní vstup a profil A; teprve s těmito podklady povolit periferie,
provisioning a další flash aplikačního firmware. Diagnostika může zůstat
na desce, externí komponenty zatím nepřipojovat.

Git předání D122: implementační commit
`21c07cc33b4de1cc53f593e72b7485f2314c109a` byl přes
`scripts/Push-CurrentBranch.ps1` pushnut na
`origin/feat/board-memory-diagnostic`; remote shoda byla ověřena.
Následující dokumentační commit zaznamenává toto doručení a odstraňuje
koncové prázdné řádky nových konfiguračních souborů nalezené při staged
whitespace kontrole; jeho `6048887240bce83f89dec341b5632b79786a31c3`
byl také ověřeně pushnut. Při integraci aktuálního main `e1da30a` byly
zachovány iPhone opravy/publikace a serverové CI runnery. Tento záznam
byl přejmenován na D122, aby zůstal zachovaný existující D120/D121.
[PR #94](https://github.com/Blato58/DildoVander/pull/94) eviduje finální head,
CI a autorizované sloučení; před merge musí projít povinné kontroly.

**D121 – oprava režimu tichého Realtime hlasu na iPhonu (2. 10. 2026).**
Majitel hlásí příliš tichý hlas v aplikaci. Hands-free přehrávač používal
`playAndRecord/voiceChat`, přestože jde o poloduplex a engine nezapíná
voice processing. Telefonní režim může zeslabovat reproduktor. Oprava
v `DebugHandsFreeAudio.swift` přepíná session i kontrolu route na `.default`
a zachovává `.defaultToSpeaker`, vstupní bránu během řeči a detekci přerušení.
Mobilní README popisuje režim a nastavení hlasitosti během přehrávání.
Lokálně prošel TypeScript/Vite build, všech 24 mobilních testů a whitespace.
CI `36944228707` na head `9cfbfa1` prošlo: macOS nativní route/converter/upload
testy, Xcode iOS Simulator build a spuštění aplikace i všechny povinné
serverové kontroly. [PR #92](https://github.com/Blato58/DildoVander/pull/92)
je sloučen jako `e9de404d7043ea09602e3d4ca9bb557363efde35`; změna je ověřena
na main. Skutečnou hlasitost musí potvrdit iPhone.
Implementační commit `8132191f22ada535fab8bd157a86febd85cfc272` je pushnutý
do `fix/iphone-realtime-speaker-volume`; remote SHA bylo ověřeno. Předepsaný
PowerShell helper byl spuštěn, ale zde není spustitelný a `pwsh` chybí;
push provedl scoped gh credential helper po ověření účtu Blato58.
Majitel následně výslovně schválil podpis a publikaci aktualizace. Ad Hoc
podpis [36946442779](https://github.com/Blato58/DildoVander/actions/runs/36946442779)
prošel nad main `e9de404` a ověřeným CI výše; nový build je **1000.0.9**.
Publikace [36946600253](https://github.com/Blato58/DildoVander/actions/runs/36946600253)
prošla všemi třemi joby. [Safari instalace](https://2.28.202.220/ios/)
nabízí verzi 1.0 / **1000.0.9**, release `36946600253-1`.
Serverový runner ověřil přes veřejné HTTPS stránku, manifest, Bundle ID,
stažení IPA, SHA-256 a MIME typy. Přímé čtení z cloudového workspace opět
narazilo na proxy certifikát pro cizí hostname; TLS ověřování zůstalo zapnuté.
To není nezávislá kontrola z jiné sítě ani měření hlasitosti iPhonu.
Další krok: majitel aktualizuje aplikaci, ověří hlasitost Realtime odpovědi
a alespoň dvě výměny v režimu bez tlačítek; během řeči ji může nastavit
bočními tlačítky. Backend ani firmware nebyly nasazeny a neproběhl placený AI
test. Dokumentační commit `282c6c2845650c14114acfd88da191d1bf298e29`
byl pushnut do `docs/realtime-volume-release` a remote SHA bylo ověřeno.
Lokální Markdown odkazy a whitespace prošly. PowerShell helper zde stále
není spustitelný; scoped gh helper provedl push po ověření účtu Blato58.
Finální CI a sloučení dokumentace bude evidováno v navazujícím PR.

**Migrace GitHub Actions na vlastní serverový runner (1. 10. 2026).**
Majitel požaduje všechny joby na svém serveru, kromě macOS. Všech 22 Linux
jobů používá `[self-hosted, Linux, X64, kufr-backend]`, tedy
`dildovander-server`; iOS Simulator a IPA signing zůstávají na `macos-15`.
Node setup doplněn do repository/backend kontrol; .NET SDK používá
zapisovatelný runner tool cache. PlatformIO i OpenAI SDK live eval běží
ve vlastním venv. Realtime test kontroluje předinstalovaný espeak-ng,
nepředpokládá neomezené sudo. Host probe ověřuje build nástroje a venv.
Plán, CI/CD, SECURITY a backend/AI README popisují společný dlouhodobý
runner a omezení veřejného HTTPS testu z téhož serveru. Main-only podmínky,
permissions, dependency graf, timeouty a provenance zůstávají zachované.

Lokálně prošel audit runnerů, actionlint 1.7.12, 29 provenance/promotion
+ 6 archive testů, dokumentační odkazy a whitespace. Probe 36926403741
potvrdil online Ubuntu 26.04 runner a aktivní backend. První CI
36926992141 odhalilo chybějící g++/venv; správce doplnil
`build-essential python3-venv espeak-ng`. CI 36930988401 pak na serveru
ověřilo repository, web, offline AI, všechny firmware host testy, oba
ESP-IDF/Arduino buildy s artefaktem a backend build/testy/publish/HTTP smoke.
Mac v tomto běhu GitHub odmítl před prvním krokem kvůli billing limitu;
majitel následně potvrdil opravu účtu. Původní macOS CI 36926992141 prošlo,
finální společná verze nyní zachovává také D120 opravu iPhone uploadu z main.

Implementační commit `2ff2ada` a následné integrace byly ověřeně pushnuté.
PowerShell helper byl spuštěn, ale není zde spustitelný; scoped gh git
credential helper provedl push po ověření účtu Blato58. Finální CI head,
výsledek všech Linux/macOS kontrol, sloučení a read-only ověření runneru/
veřejného HTTPS jsou evidované v [PR #88](https://github.com/Blato58/DildoVander/pull/88).
Produkční aktivace, podpis/publikace nové IPA, placené AI ani flash nejsou
součástí migrace; stávající akceptační mezery D120 a HW zůstávají zachované.

**D120 – oprava iPhone audio uploadu publikována v IPA 1000.0.8.**
Majitel výslovně schválil publikaci po úspěšné kontrole a podpisu.
PR #90 je sloučen jako `ceb40fbc06d566d116e62180d576a4e092cf968e`;
finální CI 36936917112 ověřilo head `83a2e15`. Původní podepsaný build
1000.0.7 (36937398196) nebyl publikován: mezitím se do main sloučila
migrace runnerů z PR #88. Nový Ad Hoc podpis 36940005506 použil úspěšné
CI 36937790022 a aktuální main `cc2941ee5a52356553c891a376b6fec06e3c41ff`;
provenance, striktní podpis i upload artefaktu prošly.
Publikace 36940186009 prošla všemi třemi joby. [Safari instalace](https://2.28.202.220/ios/)
nabízí 1.0 / **1000.0.8**, release `36940186009-1`. Serverový runner přes
veřejné HTTPS ověřil stránku, manifest, Bundle ID, download IPA, SHA-256
a MIME typy. Není to nezávislá kontrola z jiné sítě. Přímá kontrola z tohoto
cloudového workspace narazila na proxy certifikát s cizím hostname;
TLS ověřování nebylo vypnuté. Majitel musí potvrdit aktualizaci a hlasový
rozhovor na skutečném iPhonu/5G. Backend nebyl nasazen ani restartován,
neproběhl flash desky ani placený AI test.

Majitel sdílí `realtime_live_audio_overflow` a stabilní připojení. Nalezený
defekt: iOS vytvářelo a rušilo URLSession pro každý PCM POST (nejvýše 200 ms
audia), přitom mělo jen 1 s frontu a při průběžném drainu posílalo i menší
bloky. Chyba sama nedokazuje nestabilní připojení.

`DebugHandsFreeSession.swift` nyní drží jedno ephemeral HTTP sezení pro
PCM a playback ACK, zachovává zákaz redirectu a timeout. Stop ruší aktivní
požadavky; normální close nechá doběhnout už potvrzené /played HTTP 204.
`DebugHandsFreeAudio.swift` skládá přesné 9600B/200ms dávky, zachovává
48000B limit a pořadí bez paralelních PCM POSTů. Input epoch zahazuje
pozdní převod předchozího poslechu. `DebugPanel.ts` vysvětluje skutečné
zaplnění fronty místo automatického připsání chyby spojení. README,
native handsfree peer a `scripts/test-native-debug.sh` doplňuje nový
`tests/native_handsfree/upload_regression.swift`.

Lokálně prošly TypeScript/Vite build a všech 24 mobilních testů (Node
24.19.0/npm 11.9.0; CI používá připnutou verzi), bash/Node syntax a
`git diff --check`. Swift 6.1.2 pro Linux z dočasného toolchainu zkompiloval
skutečné deklarace upload bufferu a HTTP helperu s `-swift-version 5
-warnings-as-errors`; nový loopback test prošel dávky/PCM pořadí při 50ms
capture a 150ms HTTP odezvě (první odpověď 550 ms), kapacitu/overflow,
input epoch, jedno TCP spojení pro 10 POSTů, zákaz redirectu a graceful
finish/cancel. Izolovaná baseline s původním per-frame URLSession otevřela
10 spojení a prokazatelně selhala na reuse assertion. Původní multi-turn
loopback test také prošel s dočasnou Linux adaptací importů a bez Keychain
a AVAudioConverter testu; jeho převodník/iPhone audio tím není ověřený.
Linux FoundationNetworking upozorňovalo na již existující Sendable mutable
state v live-stream delegátovi; nativní macOS kompilace nyní prošla.

Nové čtení logů workflow 36931829723 selhalo před prvním krokem runneru:
GitHub annotation uvádí neúspěšné platby nebo nutnost zvýšit spending limit.
Poslední úspěšný snapshot 36928995676 je z 21:29 UTC a předchází screenshotu,
proto nepotvrzoval konkrétní příčinu incidentu. Majitel následně opravil
billing. Opakovaný CI běh 36932961956 (attempt 2, head `980301a`)
prošel všemi povinnými kontrolami: macOS Swift harness včetně nového upload
testu, Xcode 26.3 simulator build a spuštění aplikace, web/kontrakty,
backend a oba firmware profily/host testy. Placený AI job zůstal vypnutý.
Nové serverové čtení 36936002408 a filtrované 36936100858 proběhlo úspěšně:
responses/openai dostupné, 0 aktivních rozhovorů, žádné droppedWrites ani
truncated. U rozhovoru v 21:47 UTC byl úvod dokončen a poslech otevřen,
poté telefon ukončil stream (499); žádná provider chyba v dané korelaci.
To odpovídá lokálnímu overflow, ale samo neměří upload RTT ani neprokazuje
celou příčinu screenshotu. Implementační commit
`3fdcf6ac9261ab1edc2ddf63780d17eea09427bd` byl pushnut do větve
`fix/ios-realtime-upload-overflow`, shoda vzdáleného SHA ověřena.
Push-CurrentBranch.ps1 byl spuštěn, ale nemá uložený OAuth token účtu;
session API potvrdilo Blato58 a push proběhl přes scoped gh credential
helper bez změny ostatních účtů. [PR #90](https://github.com/Blato58/DildoVander/pull/90)
eviduje finální head, povinné CI a výsledek sloučení. Tento zápis aktualizuje
původní blokaci; povinné CI následně ověří i tento dokumentační head.
Další krok: majitel nainstaluje 1000.0.8 a ověří úvod, alespoň dvě vlastní
hlasové odpovědi, automatické obnovení poslechu a Stop na skutečném
iPhonu/5G. Při další chybě dohledat aktuální trace/operation v logu.

**D119 – Realtime hlas přímo na ESP a nastavení v Ovládání; všechny provider cesty přes SDK.**
Majitel 1. 10. požaduje přidat Realtime do firmware, nastavovat hlasový režim
v Ovládání aplikace a převést všechna volání OpenAI na SDK. Rozsah a výjimka
z ručního lease pro samostatné hlasové příkazy jsou odůvodněné v plánu D119.
ESP-IDF nyní drží autentizovaný backend NDJSON stream a pořadové PCM POSTy;
telefon neposílá audio ani provider klíč. Server VAD obnovuje poslech po
potvrzeném dohrání, lokální Stop okamžitě umlčí zesilovač. Odpověď/návrh
projde existující korelací, snapshotem, maskou a TTL do PendingProposal;
volný text ani audio přímo nespínají pohony. Provoz je omezený délkou,
počtem tahů, velikostí audio/eventů a existujícími serverovými cost caps.

Audio adaptér používá standardní I2S 32 kHz, ICS43434 a MAX98357A s
průběžným polyphase FIR převodem 32 ↔ 24 kHz a počáteční hlasitostí 25 %. Zapne se
jen s externě servisně ověřeným profilem A (`wand_audio/profile_a_ok=1`),
bez kamery/SD. Bez tohoto ověření UI hlásí nedostupné audio. Profil B,
PTT, echo cancellation a hardwarová akceptace se tímto nedoplňují.
Ovládání nabízí Vypnutý/Realtime, Marin/Cedar/Coral, Použít, Spustit a
Zastavit. Stav/nastavení potvrzuje ESP přes `voice.ack` a snapshot, nejsou
optimistické; start vyžaduje samostatný ready stav a aktuální revizi.
Odpojení pozorovatele samostatný rozhovor nezastavuje, ruční převzetí a Stop
ano. Nejasné požadavky se po reconnectu nepřehrávají; volba platí do rebootu.

Responses, kompatibilní TTS/STT a Python eval nově používají připnuté
oficiální SDK; Realtime už SDK používal. BoundedSdkTransport omezuje odpověď
před SDK bufferingem, zachovává pevný endpoint/no redirects, timeout,
validaci a serverové rozpočty. Retry je explicitně nula; test 429 ověřuje
jeden provider pokus pro každou cestu. STT protokolový overload zachovává
`languages[]=cs`. Python live eval instaluje openai 3.22.1; offline testy
zůstávají bez klíče a síťových volání.

Změněné soubory: firmware `features/voice_turn`, backend_access,
local_control, conversation start, JsonCursor, IDF composition/CMake a
hostitelské testy/stubs; mobil `LocalVoiceClient`, `VoicePanel`, connection,
main/style a test loader; JSON schema/fixtures; backend OpenAi transport,
ResponsesTextDialogClient, OpenAiSpeechClient, OpenAiTranscriptionClient,
TextDialogRunner a provider testy; Python eval/requirements/test_sdk,
workflow, README, AGENTS a plán/stav.

Lokálně prošly všechny firmware host testy včetně striktního NDJSON,
resamplingu, úvodu a dvou navazujících mic tahů, pořadí uploadu, drain/ACK,
Stop, cost_limit a chyb. Regresní test zadrží mikrofonní blok předchozího
poslechu až do začátku dalšího: input epoch jej zahodí a nedovolí
přeznačit na nové Input-Id. Samostatný test audio driveru ověřil servisní
bránu profilu A, drain, Stop při DMA zápisu a odmítnutí pozdního audio
bloku až do explicitního Begin nového sezení (oprava nalezeného závodu).
52 skutečně emitovaných firmware zpráv prošlo
sdíleným schématem. Oba PlatformIO profily prošly: ESP-IDF RAM 66 032 B,
flash 1 202 041 B; Arduino serial RAM 18 660 B, flash 286 589 B (nezahrnuje
běhový heap/stack). Backend HTTP smoke a testy providerů, Realtime, journalu, fake voice,
rozpočtů, expirace a migrací prošly. Mobilní build a 24 testů, kontrakty
25 validních/41 nevalidních zpráv a 21 offline Python eval testů prošly.
Chromium na 390 px ověřil konfiguraci/start/stop, zachování rozpracované
volby při heartbeat, uzamčení aktivního rozhovoru, viditelný cost_limit a
zakázání hlasu ve fake režimu, bez JS chyb a horizontálního přesahu.

Git předání: implementační commit `229904b5852a9b8755dac0758341fa6501c2269e`
byl pushnut přes autorizovaný Push-CurrentBranch.ps1; shoda remote větve
`feat/esp-realtime-voice-sdk` byla ověřena. Helper vyžaduje uložený účet;
zdejší session credential byla přihlášena jen do dočasného izolovaného
GH_CONFIG_DIR a účet Blato58 ověřen, ostatní GitHub účty se nemění.
[PR #85](https://github.com/Blato58/DildoVander/pull/85) je bez konfliktů a
čeká na povinné CI včetně Swift/Xcode/iOS Simulatoru; lokálně zde Xcode není.
Finální původní head `938f9cc8f7dd2b0d54bed9389983ebedfaf9dd1c` byl
ověřeně pushnut a CI 36923066577 prošlo všechny povinné kontroly včetně
Swift/Xcode/spuštění iOS Simulatoru a obou firmware profilů. Během CI se
do main sloučila oprava iPhone startu z PR #84 (`0d97dea`); obě změny jsou
zachované. Společná verze `5e73e21` byla ověřeně pushnuta a CI 36924319856 prošlo
všechny povinné kontroly. Závěrečná ochrana input epoch dále brání přenosu
rozpracovaného mic bloku do následujícího poslechu. Nový regresní test
prokazatelně selže na původním transportu a projde s opravou; ESP-IDF build
prošel znovu. Povinné CI ověřuje poslední head tohoto PR.
CI 36925265141 poté ověřilo všechny povinné kontroly headu `54d5f9f`,
včetně regrese input epoch. Mezitím se do main sloučila diagnostika z PR
#86 (`0330ce7`); zachovaná další merge integrací, tento slice nově D119.
Po další integraci lokálně znovu prošly backend testy včetně ServerLogTests,
HTTP smoke, mobilní build/24 testů, kontrakty a browser panel hlasu.
CI ověřuje společnou finální verzi. Aktuální výsledek kontrol a sloučení
je doložený přímo v PR.
Tento krok nenasazuje backend, nevydává IPA, nenahrává firmware a nevolá
placené OpenAI. Skutečný mikrofon/reproduktor, paměťové/časové rezervy při
TLS, Stop během audia a 100 tahů na desce ani ovládání na iPhonu nebyly
ověřené. B1/fyzická bezpečnost, kalibrace a HW část E9 zůstávají otevřené.
Další konkrétní krok: po zeleném CI servisně ověřit profil A a bezpečný B1
stav, samostatně autorizovat flash/nasazení a měřit opakovaný rozhovor,
Stop, Wi-Fi výpadek, návrat k poslechu a reálnou češtinu na desce.




**D118 – nasazeno: serverové logy, iOS zobrazení a čtecí integrace Codex.**
Zadání majitele: logování serveru, API přístup, log v iOS a společný debug
nasazeného prostředí s Codexem. Backend přidává oddělenou SQLite logovou DB,
výchozí historii 24 h/5000 událostí, kurzor/filtry/retention gap a počty
neúspěšných zápisů. Každý HTTP požadavek má serverové UUID X-Request-Id;
log používá route template, status/duration, pevný event/code a ID operace/
rozhovoru. Realtime zaznamenává fáze/chyby i uvnitř HTTP 200 streamu.
Úspěšné GET/poll/PCM jsou vynechané. Žádná těla/query, text, audio, credential,
prompty nebo exception message. ILogger adaptér přijímá jen známé bezpečné
události ConversationStore. Logovací selhání nesmí zrušit živý audio tok.
GET /v1/admin/logs a /status vyžadují admin nebo oddělený reader token;
reader nezískává správu, device token ani AI. Deploy volitelně přijme
LOG_READER_CREDENTIAL z backend-production a ukládá pouze SHA-256.
iOS Debug čte logy nezávisle na rozhovoru, filtruje, obnovuje po 2 s a sdílí
JSON. Drží 200 událostí, ošetřuje reset profilu/DB, staré výsledky a pozadí;
credential zůstává v Keychain. Codex stdio MCP má read_server_logs/status,
HTTPS PKI, žádné redirecty, omezenou odpověď a projekci typovaných metadat.
GitHub Kufr server logs umožňuje totéž bez lokálního tokenu, přes existující
serverový secret a jednodenní artefakt; žádné provider volání.
Rozsah: backend Features/Diagnostics, middleware/API/Realtime/operace/testy;
OpenAPI/verifier; DebugBackendClient/plugin/Panel/ServerLogs a native/browser
harness; deploy/read workflow a Codex Python klient/testy/návod; AGENTS/plán/README.
Lokálně prošly locked restore .NET 10.0.303 linux-x64, Release build bez
warnings/errors, kompletní backend testy včetně persist/TTL/cap/filtrů a
oddělení oprávnění, HTTP smoke včetně restartu/sanitizace/query/trace.
Mobilní TypeScript/Vite build a 21 testů, všechny kontrakty, 15 Python testů
(včetně skutečného HTTPS MCP peeru, redirect/denial/oversize/stdio/snapshot),
a actionlint 1.7.12 všech workflow prošly. Playwright Chromium ověřil
filtr/dedup, obnovování během rozhovoru, zapomenutí profilu, HTTP denial se
zastavením obnovování a 390px rozložení bez přesahu/console chyb.
Browser odhalil kolizi společné CSS třídy logových seznamů; aplikační log
nově vybírá svůj vlastní seznam a serverový log zůstává oddělený.
CI 36923732710 již ověřilo Swift/nativní harness i Xcode/spuštění
simulátoru, backend a web. Před merge se do main sloučila souběžná D117
oprava iPhone Realtime audio startu (PR #84, 0d97dea). Zachovaná merge
integrací; konflikt pouze v tomto záznamu vyřešen ponecháním obou změn.
Logy mají nyní číslo D118. Společnou verzi znovu ověřuje povinné CI.
Žádné placené AI volání.
Implementační commit `3446dd3beb9e5164e10f9d16189f7bfab2d1175e` byl pushnut,
remote shoda ověřena; [PR #86](https://github.com/Blato58/DildoVander/pull/86).
Push-CurrentBranch.ps1 byl spuštěn, ale gh auth token --user zde nepodporuje
runtime GH_TOKEN. API ověřilo účet Blato58; scoped gh git credential helper
provedl push bez změny účtu nebo uložení tajemství. Finální macOS/CI/merge
výsledek je evidovaný v PR. Review doplnilo rušení logového HTTP i při
otevření konfigurace a zapomenutí nativního profilu.
Finální CI 36924528705 prošlo celé včetně Swift/Xcode/iOS Simulatoru a obou
firmware profilů. PR #86 sloučen do main `0330ce7b7514897304b91588056eefba0608a278`;
shoda tree s ověřeným head `18fd319c23dd7dd1dd985488652a34529757cc85` doložena.
Deploy 36925672662, Ad Hoc podpis 36925679081 a publikace 36926024016 prošly.
Nasazení zachovává responses/gpt-6.1-sol a openai; nepouští placený test.
[Služba a Safari instalace](https://2.28.202.220/ios/) nabízejí iOS 1.0/build
1000.0.6 (sign run_number 6); externí runner ověřil HTTPS stránku/manifest/IPA.
Živé čtení workflow 36926028955 úspěšně ověřilo log API a režimy přes veřejné
HTTPS s credentialem z runneru, včetně typované projekce. V tomto cloudovém
prostředí je stažení Actions artefaktů blokované (Blob 403) a přímý curl na IP
odmítl hostname certifikátu proxy; TLS se neoslabuje. Autorizované Actions
log API funguje. Následná úprava proto přidává stejný validovaný technický
JSON do konzole workflow mezi KUFR_LOG_SNAPSHOT_BEGIN/END, aby Codex mohl
číst logy i v této relaci bez stažení artefaktu. Artefakt žije 1 den, konzole
má retenci Actions logů nastavenou v GitHub repozitáři. Token ani obsah
rozhovoru se do obou cest nevkládá. Ověření konzole a finální merge předání
jsou evidované v následném PR této úpravy; instalovaný backend/iOS se kvůli
čtecímu workflow znovu nepřekládá ani nenasazuje.
Další krok: aktualizovat iPhone a vyzkoušet Log serveru během rozhovoru,
filtr/obnovování/sdílení a Stop/pozadí. Fyzický iPhone zde není dostupný;
B1/fyzické pohony zůstávají otevřené. Přímé MCP je volitelné po nastavení
reader tokenu v procesu Codex; GitHub čtení využije existující admin secret.

**D117 – oprava startu Realtime, IPA 1000.0.5 publikovaná.**
Majitel u 1000.0.4 doložil chybu hned po Začít AI rozhovor; oranžová tečka
mikrofonu se objeví, první replika nezazní. Zdrojový průchod našel dvě
možné startovací chyby: každá audio route notifikace rušila rozhovor včetně
vlastního přepnutí playAndRecord a převodník považoval prázdný výstup při
rozběhu resamplingu za chybu. Nativní klient nyní ignoruje vlastní
category/override/configuration oznámení jen při shodě kategorie, režimu,
route a vstupního formátu; skutečné změny zařízení dál bezpečně zastaví.
Tap používá outputFormat vstupního uzlu a zavřený vstup se nepřevádí.
Stavový PCM převodník připouští nulový výstup bez ztráty dalšího vstupu.
Debug rozlišuje audio/HTTP/protokol/TLS/timeout chyby a ukazuje bezpečný
technický kód; neposílá surový text chyb, credential ani URL.
Soubory: DebugHandsFreeAudio/Session, KufrDebugPlugin, DebugPanel,
nativní peer/harness a testovací skript, testovací UI, README a tento stav.
Lokálně prošly TypeScript/Vite build, všech 19 mobilních testů, Node
syntax peeru, 16 lokálních dokumentačních odkazů a git diff --check.
In-app Browser ověřil konkrétní audio hlášku/kód, Stop a obnovený poslech
po novém startu přes syntetický bridge. Technický kód se zalomí i na telefonu.
Nový macOS test používá skutečný AVAudioConverter 44,1/48kHz (mono/stereo)
-> PCM16/24kHz, krátké/počáteční buffery,
route policy a časné HTTP/protokol chyby vedle původních tří tahů.
Finální CI 36922914322 prošlo všechny povinné kontroly včetně nativního
testu, Xcode sestavení a spuštění iOS Simulatoru. Read-only review vedlo
k explicitnímu příznaku audio lifecycle: opožděné callbacky po vlastním
Stop se ignorují, ale systémové zastavení enginu nepohltí skutečné přerušení.
Commity `d8de06fa716ba381b8f24250f22c1c473187ec88`,
`2dd0e7fb659dc2f151446ca06636b017a92f4182` a finální CI head
`1b1e3abd796861bea583c92da68d129fbadc8f52` byly ověřeně pushnuté.
[PR #84](https://github.com/Blato58/DildoVander/pull/84) byl sloučen do
main `0d97dea991dbadb27fa8f286fd2c98638227f81c`; shoda tree s CI head ověřena.
Ad Hoc podpis 36924073561 a publikace 36924312823 prošly; instalační
stránka i skutečná veřejná IPA uvádějí 1.0 / **1000.0.5**. Bundle ID
`app.turquoise6409.green2444`, 964677 B, Cache-Control no-store,
SHA-256 `5708765040138869f0bd6a9f32bf6d510bda06d671e5f62eb32690d16daca4d7`.
Zdroj publikované aplikace je uvedené main 0d97dea; následné předání
mění pouze dokumentaci. Backend kód se neměnil, běží nasazení D116.
Backend veřejné healthz vrací 200; NDJSON startup shoda prošla read-only
kontrolou. Nový placený AI test se nespouští. Přesnou příčinu na telefonu
potvrdí až nová IPA a opakovaný start; B1/fyzické pohony se nemění.
Další krok: majitel aktualizuje na 1000.0.5 a ověří první repliku,
automatický poslech po více odpovědích a Stop/pozadí. Pokud start dál
selže, předá konkrétní technický kód z nové hlášky. Tato fyzická kontrola
ani skutečný iOS notification callback nejsou doložené simulátorem.

**D116 – nasazeno: průběžný poslech bez tlačítek a oprava dalších odpovědí.**
Majitel po instalaci D114 hlásí druhou odpověď bez výsledku: po dokončení
nahrávání se pouze deaktivuje tlačítko. Žádá skutečný průběžný rozhovor.
Backend drží jedno provider SDK spojení, VAD rozpozná konec řeči a každý
nový tah rezervuje před příjmem audia. Telefon používá jeden NDJSON stream,
krátké pořadové PCM POSTy a automaticky obnoví mikrofon po dohrání.
Pozdní vstupní blok se zahodí, nezačne nový tah ani nezastaví rozhovor.
Stop funguje i během čekání na dohrání, chyby/cost_limit jsou viditelné.
Rozpočet zůstává: kratší rezervace/context mohou zapadnout do stávajícího
capu; podrobný prompt a uložená paměť se nemažou. PTT/řetězená záloha zůstává.
Rozsah: Features/Realtime, ConversationStore/RealtimeTurns a backend testy;
DebugHandsFreeSession/Audio, plugin/klient/DebugPanel, Swift harness a Xcode;
OpenAPI/verifier, README/plán a omezený živý Python test/workflow.
Lokálně prošly backend testy úvodu a dvou hlasových odpovědí na jednom
socketu, pořadí vstupních bloků, dohrání a nízký cap/Stop. TypeScript/Vite
build, 19 mobilních testů, všechny kontrakty a 12 Python klientských testů
prošly. Finální HTTP smoke také prošel. Read-only review odhalil a opravil
závod pozdního audio bloku při VAD stop/novém poslechu; koncové bloky se
zahazují i při druhém zavření vstupu. In-app Browser ověřil výchozí režim,
dvě navazující syntetické odpovědi bez nahrávacího tlačítka, viditelný
cost_limit, Stop a opětovné spuštění. macOS Swift/Xcode ověří povinné CI.
Implementační commit `292b677ab9e210f69bd12e279c7a521716480d36` a integrace
main `5352d4d15a1be15372ce292ba1d8628ea6dde27f` byly ověřeně pushnuté.
[PR #82](https://github.com/Blato58/DildoVander/pull/82) zachovává D115 opravu
safe area z PR #81. První CI 36905453842 prošlo backend testy, web, kontrakty
a offline eval; Swift odhalil actor izolaci čistého parseru chyb, opraveno
explicitním nonisolated. Linux HTTP negativní test narazil na EPIPE při
znovupoužití spojení po nadlimitním těle; tento požadavek nyní spojení zavře.
Finální CI 36906081419 prošlo všechny povinné kontroly včetně nativního
hands-free harness (úvod/dvě odpovědi, VAD gate, časný ACK a cleanup),
Xcode build/spuštění iOS Simulatoru a obou firmware profilů. PR #82 je
sloučen do main `7581623ef8f843d00244684110a8ce07f2d3ff9c`; shoda tree
se schváleným CI head `c28e4fcbf5ca1f1661e3922748a634400b9ae6d5` ověřena.
Deploy 36907319409, Ad Hoc podpis 36907325300 a veřejná publikace
36907700654 prošly. [Safari instalace](https://2.28.202.220/ios/) nabízí
1.0 / build **1000.0.4**, včetně D115 safe area. Veřejný manifest a IPA
jsou ověřené: Bundle ID `app.turquoise6409.green2444`, 960750 B, no-store,
SHA-256 `35e2c5b695e8e9036e20b0710222710596ca3fe74d3fbd533f44d212096e7c0e`.
Autorizovaná živá zkouška 36907705482 prošla jedno kontinuální spojení,
úvod a čtyři české syntetické hlasové odpovědi až k validovanému návrhu.
První audio po server VAD stop: 739/697/667 ms pro běžné otázky, 1533 ms
pro konečný výběr; úvod od HTTP startu 2764 ms. VAD před tím čeká 650 ms
ticha. Generování běžných tahů celkem 1594–2012 ms; skutečné dohrání
na iPhonu tento syntetický test nesimuluje. Report uvádí konzervativní
náklad **0,198108 USD**, session cap 0,50 USD; schválený strop 1 USD
nebyl překročen. Jde o odhad backendu, nikoli účetní výpis OpenAI.
Sezení bylo ukončené a předchozí večer obnovený, uživatelská paměť se nemaže.
Další konkrétní krok: majitel aktualizuje iPhone na 1000.0.4 a ověří mikrofon,
automatický návrat k poslechu po několika odpovědích, Stop/pozadí a safe area.
Syntetická zkouška obsahuje nepřesnosti STT; skutečnou češtinu, poslech
humoru a audio route potvrdí telefon. B1/fyzické pohony zůstávají blokované.

**D115 – oprava horních tabů pod výřezem iPhonu.**
Majitel hlásí překrytí tabů kamerou/senzory a obtížné klepnutí.
`index.html` zapíná `viewport-fit=cover`; `src/style.css` přidává
systémový `safe-area-inset-top` do paddingu sticky lišty a respektuje
levý/pravý inset v landscape. Horní inset zůstává součástí lišty i při
scrollování; nulový inset zachovává webové odsazení a stávající výšku tlačítek.
Mobilní README popisuje chování. Kontrakty, backend a firmware se nemění.
Kontrola zdrojů ověřila společnou lištu obou tabů, sticky top 0, viewport
a zachování minimálních okrajů; whitespace a odkazy změny jsou v pořádku.
Zdejší prostředí poskytuje GitHub API bez lokálního shellu/PowerShellu;
změna je předána přes Git Data API na větvi `fix/ios-tabs-safe-area`.
Webové testy/build a Swift/Xcode/simulator ověřuje povinné CI tohoto PR;
aktuální výsledek a merge jsou evidované v PR. Nativní vizuální/tap kontrola
na iPhonu, scroll a obě landscape orientace zde nebyly provedeny.
Podpis a publikace nové IPA nejsou součástí tohoto kroku.
Další krok: po zeleném CI a merge připravit novou IPA v autorizovaném
publikačním kroku a ověřit Ovládání/Debug na iPhonu včetně scrollu/otočení.

**D114 – nasazeno: Realtime hlas přes oficiální SDK a podrobný party prompt.**
Majitel doložil 9,8 s dialog + 2,3 s TTS a výslovně schválil Realtime pro
hlas místo dosavadní Sol-only hlasové cesty. Textový Sol zůstává. Požaduje
podrobný popis humoru/jazyka; prompt se nezkracuje kvůli latenci.
Backend používá OpenAI .NET SDK 2.14.0 a `gpt-realtime-1.5`, vlastní provider
WebSocket; iOS přijímá autentizovaný HTTPS NDJSON a přehrává PCM průběžně.
PTT/poloduplex, explicitní VAD null, model slyší audio přímo; souběžný přepis
slouží trvalé paměti. Funkce vybírá až po minimálně čtyřech otázkách, backend
kontroluje masku/korelaci/expiraci. Debug návrh nikdy nespouští pohon.
SDK stream i vstup/výstup jsou omezené, deadline 45 s. Stop/disconnect ruší
provider; journal brání placenému replay a sdílí existující limity/slot.
Prompt popisuje obecnou češtinu, konkrétní narážky, osobní otázky, callbacky,
tempo a příklady dobré/slabé repliky. Nový hlas je výchozí AI tok Debugu;
fake a kompatibilní STT/Sol/TTS jsou dostupné. UI měří první audio i celý tah.
Soubory: backend Features/Realtime, ConversationRealtimeTurns a zapojení,
NuGet reference/lockfiles, RealtimeTests a HTTP smoke; OpenAPI/verifier;
DebugRealtimeSession, DebugBackendClient, KufrDebugPlugin, DebugPanel,
nativní harness/Xcode projekt; prompt a plán/README/stav.
Lokálně prošly kompletní backend testy v Release včetně SDK serializace,
audia před response.done, tool/mask/otázkové brány, resamplingu, journalu,
nízkého capu, chyby paměťového zápisu a Stop. HTTP smoke a všechny kontrakty
prošly; mobilních 19 testů, TypeScript/Vite build a 20 Python eval testů také.
Locked restore linux-x64/self-contained s explicitním RuntimeIdentifier
prošel. In-app Browser ověřil
Realtime jako výchozí, kompatibilní hlasy, ovládání a 390px rozložení bez
horizontálního přesahu/console chyb. Swift/Xcode na Windows není dostupné;
nový parser/rušení/cleanup harness a iOS sestavení ověřuje povinné macOS CI.
Implementační commit `10ce2af240531bc6a3faaa8d735e973c74499104` byl pushnut
přes Push-CurrentBranch.ps1, přesná remote shoda ověřena.
[PR #80](https://github.com/Blato58/DildoVander/pull/80) obsahuje povinné
kontroly backendu, webu, Swift harness a Xcode/simulátoru; aktuální výsledek
kontrol a sloučení je v PR. Tento záznam se nepoužívá jako HW/iPhone akceptace.
První CI 36886840203 odhalilo hostitelské win-x64 v lockfiles a chybějící
Realtime source v původním Swift test buildu. Lockfiles regenerované přes
NuGet s explicitním linux-x64 RuntimeIdentifier; oba Swift harness buildy
nyní zahrnují DebugRealtimeSession. macOS ověření pokračuje v PR.
Linux backend v CI 36887687842 kompletně prošel. Další macOS compile odhalil
fileprivate u sdíleného cleanup; metoda je nově internal pro oba nativní
klienty, bez změny síťového kontraktu nebo chování mazání.
CI 36888412422 prošlo oba nativní harness včetně Realtime. Xcode ještě
vyžaduje explicitní MainActor u failDialog/stopWork; notification callbacks
nově přecházejí přes hlavní DispatchQueue, ostatní callsites již běží na ní
nebo v MainActor Task. Kompletní iOS build/spuštění se dál ověřují v PR.
Read-only review opravil immutable model, 32KiB audio bloky, playback drain,
cleanup retry, souběh streamu a pořadí paměť/journal. Oddělené SQLite soubory
nejsou společná atomická transakce: chyba journalu po úspěšné paměti může
ponechat výměnu v paměti při ohlášeném neúspěchu. Viz backend README.
PR #80 byl sloučen do main `3be859cacac0345799ff003e1dfc78c12b295295`;
finální CI 36889244970 prošlo včetně Swift/Xcode/simulátoru. Autorizované
nasazení backendu 36892004497, podpis 36892009408 a veřejná publikace
36892712624 prošly. Veřejná `/ios/` nabízí build 1000.0.3; majitel jej
nainstaloval. Placená evaluace zatím neproběhla, schválený strop je 1 USD.
Skutečná latence/humor nejsou doložené offline testy. Hlásená iPhone
chyba a automatický poslech pokračují v D116; B1 trvá.

**Předchozí nasazení 1. 10.: D112, IPA 1.0 / build 1000.0.2 publikovaná.**
Debug nabízí pojmenovaný nový večer i návrat ke staré paměti a ukládá profil
do odděleného Keychain. Výchozí URL/ID jsou `https://2.28.202.220/` /
`debug-server-admin`. V nové verzi credential jednou znovu uložit: starší
RAM-only verze ho neměla na disku. Public [instalace](https://2.28.202.220/ios/)
a skutečná IPA ověřeny; původní serverové eveningId i dvě výměny zachovány.
Společný výsledek zahrnuje D111 CI a D113 závislosti. PR #78/#79 sloučené,
všechny push ověřeny; CI, deploy, podpis i publikace prošly. Další konkrétní
krok: majitel aktualizuje registrovaný iPhone, uloží Debug profil a ověří
jeho zachování při pozadí/relaunch/další aktualizaci, reálný hlas a večery.
Bez telefonu nelze tuto akceptaci doložit; fyzický runtime dál blokuje B1.

**D113 – oprava dependency PR #77, web a macOS ověřeny.**
TypeScript 7 odstranil staré compiler API a zpřísnil side-effect importy.
Testovací loadery nyní používají Node transform, `src/vite-env.d.ts` dodává
Vite deklarace pro CSS. `cap sync ios` vygeneroval Swift manifest 8.5.2.
Novou zranitelnou závislost CLI `xcode -> uuid 7` nahrazuje cílené override
na CommonJS kompatibilní uuid 11.1.1; npm lockfile vznikl přes npm.
Soubory: mobilní package/lock, dva testovací loadery, Vite deklarace,
generovaný Swift manifest/lockfile a mobilní README; původní PR aktualizuje i `ws`.
Lokálně: npm ci, 19 mobilních testů, TS7/Vite8 build, všechny kontrakty,
MSVC C++14 bridge build a kompletní simulator npm test včetně TLS/WSS/media
prošly. xcode parse/write a generování UUID prošly, npm audit má 0 nálezů.
Lokální runtime Node 25.9.0; všech 19 testů prošlo také na přesném 24.21.0.
In-app Browser nad Vite8 produkčním buildem ověřil Ovládání/Debug,
převzetí, potvrzení výběru a Stop bez console chyb.
Commit `efe1cd51a726a97350be498d06868d3b294555a3` byl pushnut přes
Push-CurrentBranch.ps1 a remote shoda ověřena. V CI 36869848988 prošel web,
nativní Swift harness, Xcode build i spuštění simulátoru. Package.resolved
8.5.2 pochází z artefaktu `kufr-swift-package-resolved` tohoto macOS běhu.
CI 36870350469 celého commitu `35a150e254d575c1d839c24ec8b5bc94749994dd`
prošlo včetně firmware, backendu a iOS; Swift lockfile je shodný s resolverem.
Souběžně se sloučil PR #78. Jeho kód a původní D112 jsou zachované; konflikt
byl pouze mezi novými záznamy stavu, dependency oprava proto používá D113.
Další krok: ověřit CI společného výsledku s PR #78 a merge PR #77.
Žádné nasazení, podpis IPA ani HW akceptace.

**D112 – správa večerů a trvalý Debug přístup nasazeny.**
Zadání majitele: vytvořit pojmenovaný večer, přepínat aktivní večer bez ztráty
starých rozhovorů a zachovat credential při aktualizaci aplikace. Výchozí
URL `https://2.28.202.220/`, potvrzená identita `debug-server-admin`.
Implementace používá samostatný nesynchronizovaný iOS Keychain; JS ani log
credential nečtou. Prázdné pole může zachovat uložený credential jen pro
stejný server a identitu. Explicitní zapomenutí maže jen Debug profil.
Backend migruje stávající paměť do katalogu večerů s aktivním ukazatelem,
device izolací, CAS a trvalou idempotencí; změny jsou odmítnuté během sezení
nebo doznívajícího provideru. Native klient po mutaci obnovuje skutečný stav.
Lokálně prošel .NET build/test (0 warnings/errors), HTTP smoke, všechny
kontrakty, mobilních 19 testů a TypeScript/Vite build. In-app Browser ověřil
nový večer, návrat k 42 původním výměnám, souběžný konflikt/obnovu, blokování
během rozhovoru, zapomenutí profilu, bezpečný text HTML názvu a log bez názvu.
Rozložení 390/1280 px bez horizontálního přesahu a console chyb.
Read-only review opravil busy guard podle vlastníka PaidCall (i po expiraci
journalu), omezení idempotence 24 h/1024 a native limit 256 KiB pro 100×80
Unicode názvů. Regresní testy pokrývají migraci obsahu/počtů, restart replay,
CAS, izolaci archivů/zařízení, late append, legacy clear a limity.
Swift testy nově ověřují skutečný Keychain mezi procesy a aktualizaci/smazání
testovacího profilu, bezpečné ponechání credentialu a evening retry/GET.
Na Windows nejsou Swift/Xcode; macOS CI ověřilo nový harness i Xcode/simulator.
Změněné soubory: konverzační úložiště/endpoints/PaidCall a backend testy/smoke,
OpenAPI/verifier, DebugBackendClient/KufrDebugPlugin, DebugPanel/Journal/style,
native Swift a UI harness, jejich skript a plán/README/stav.
První commit `abf94d65af815d3b8bd4d83a563e1b0371b751ad` push ověřen;
[PR #78](https://github.com/Blato58/DildoVander/pull/78). Souběžná main změna
CI/CD (D111, PR #75/#76) zachována merge `83c7206`; konflikt pouze ve stavu
vyřešen zachováním obou záznamů. Backend runtime/HTTP smoke nad společným
výsledkem znovu prošel; lokální restore změnu RID lockfiles nezahrnuje.
Nový podpisový workflow má vlastní counter, proto používá explicitní build
`1000.x.y` vyšší než starý 37; Bundle ID/tým zůstávají stejné. Build skript
validuje čtyři/dva/dva číselné komponenty. Finální build je `1000.0.2`;
první podpisový běh skončil ve verifieru, a proto spotřeboval counter 1.
Aktualizace na fyzickém iPhonu a Keychain po ní vyžadují akceptaci majitele.
Head `3da0cb39562aeb06afa7f1335f4807f9a06ce92e` prošel kompletní
[CI 36869900104](https://github.com/Blato58/DildoVander/actions/runs/36869900104),
včetně Swift Keychain/večerů, Xcode/simulatoru a firmware. PR #78 sloučen
do `1e20b54a2a7ba57d37b93fc09d38ac2d826e7407`; scoped fetch/fast-forward ověřen.
Deploy `36871071929` a signing `36871077770` skončily před produkcí chybou
403 při `GET /pulls/78`: nový promotion verifier neměl `pull-requests: read`.
Backend ani IPA se nezměnily. Oprava přidává pouze read permission do callee
i tří caller workflows a test tohoto skutečného permission kontraktu.
Read-only review opravy bez nálezů; všech 29 promotion testů prošlo.
PR #77 mezitím přidal TS7/Vite8/Capacitor 8.5.2: změny zachované merge
`9324c85123f84f2bf6b49605643c71586c67cdf6`, konflikt pouze v záznamu stavu.
Na přesném Node 24.21.0 znovu npm ci (0 audit nálezů), 19 testů, TS7/Vite8
build a browser create/switch večerů bez console chyb; shell syntax a lokální
Markdown odkazy prošly. Head `9324c85` prošel kompletní
[CI 36872465088](https://github.com/Blato58/DildoVander/actions/runs/36872465088).
[PR #79](https://github.com/Blato58/DildoVander/pull/79) sloučen do
`f859231b16376ea34d6a2674eabcbefa85d7171d`, scoped fetch/fast-forward ověřen.
Z tohoto tree byly promovány ověřené backend/web artefakty:
[deploy 36873170903](https://github.com/Blato58/DildoVander/actions/runs/36873170903),
[Ad Hoc podpis 36873176086](https://github.com/Blato58/DildoVander/actions/runs/36873176086)
a [publikace 36873500918](https://github.com/Blato58/DildoVander/actions/runs/36873500918)
prošly včetně veřejného HTTPS ověření. Public GET evening-memory/evenings
potvrdil původní ID `c169fe72-78c3-4b42-9bc0-74b78c380eb4`, dvě výměny,
0 vytěsnění a jeden pojmenovaný původní večer. Produkční večery neměněny.
Dialog `responses` / hlas `openai` zachovány, žádné placené provider volání.
Public IPA 887639 B, v1.0/build1000.0.2, manifest release `36873500918-1`,
SHA-256 `0904b6a09a1bc1bf6a1ba4d4f1b14abd80037e96db79031cf85cb9f6e5ec84f1`.
Skutečný ZIP/Info.plist/native binary/web bundle potvrzují stejný Bundle ID/tým,
Keychain a všechny nové evening/forget metody; UI harness není v IPA.
Page/IPA no-store, checksum shodný; in-app Browser ukazuje sestavení1000.0.2
a správný neměnný Safari manifest, bez console chyb. Akceptace aktualizace
na fyzickém iPhonu, lock/unlock a skutečné persistence zůstává u majitele.

**D111 – CI/CD build jednou a oddělená promotion artefaktů.**
Rozsah: každé PR do main spouští společné CI pro backend, firmware, iOS/web
a offline AI; stabilní `CI required`, zrušení zastaralých PR běhů, timeouty.
Backend Release/RID build sdílí testy i self-contained publish přes no-build,
NuGet lockfiles/cache; web artefakt přebírá macOS i podpisový workflow.
Ruční backend deploy a samostatná IPA publikace nepřekládají. API ověřuje
úspěšný workflow, původ PR/merge stromu a shodu celého tree s aktuálním main;
checksum a bezpečné rozbalení před self-hosted runnerem. Sign IPA je explicitní
samostatný device build (simulator nelze nahradit), nikoli součást deploy.
Firmware toolchain cache a verzované binární výstupy pro oba profily.
Soubory: `.github/workflows/`, `scripts/ci/`, NuGet props/lockfiles,
smoke výběr publikované DLL, CONTRIBUTING/SECURITY/CODEOWNERS/PR šablona,
Dependabot, EditorConfig/Git attributes a `docs/ci-cd.md` + komponentové README.
Lokální ověření: actionlint 1.7.12 všech workflows, 28 Node promotion testů,
6 Python archive testů, .NET 10.0.303 locked restore/Release build bez varování,
backend runtime testy, no-build publish a HTTP smoke nad publikovanou DLL,
skutečný balíček/rozbalení self-contained outputu; 19 mobilních testů a Vite/TS
build, všechny kontrakty, 3 deploy + 9 client + 4 IPA publish + 20 offline AI
Python testů; shell syntax, lokální dokumentační odkazy a whitespace prošly.
Commit `332fd4d9c8e12e4f53743042f37c4a79d30b1735` byl předán přes Git Data API
a scoped fetch potvrdil remote shodu. HTTPS git push vracel 401; zdejší Linux
nemá spustitelný PowerShell push skript. CI výsledky a merge jsou evidované
v [PR #75](https://github.com/Blato58/DildoVander/pull/75); při převzetí ověř
jeho aktuální stav a Actions.
Skutečné nasazení, Apple podpis, publikace IPA, placená AI a HW flash nejsou
součástí tohoto kroku. Repo nastavení API vrátilo 403 integration permission,
rulesets navíc vyžadují Pro pro privátní repo. Připravené konkrétní JSON +
admin apply skript nejsou důkazem aktivní ochrany větve.
Další krok: majitel/admin zpřístupní repo administraci a podporovaný tarif,
potom aplikuje připravená pravidla; nasazení použije run ID úspěšného CI.

**D110 – interaktivní hlasový Debug nasazen, podepsaná IPA build 37 publikovaná.**
Majitel žádá menší prodlevu, volbu hlasu a celý výběr s vlastními odpověďmi;
výslovně potvrdil hlas i psaný text. Implementována virtuální maska 0–63,
celý přechodný přepis a validovaný návrh bez pohybových povelů, volby hlasu
cedar/marin/onyx/coral a tempa, nativní dvoukrokové nahrávání do RAM s 30s
limitem a STT uploadem. Stop/pozadí/interruption ruší nahrávku i vlastní
sezení. Fake admin dialog projde čtyřmi otázkami a vybere dostupnou přihrádku;
fake STT zůstává syntetický. Native příjem WAV je omezený po blocích mimo
MainActor, přehrávání předchází audio cleanup RTT. Měří se dialog/TTS/stažení;
nejde o streaming před dokončením provideru ani důkaz rychlosti na iPhonu.
Změněné soubory: speech/provider/konverzační backend a testy/HTTP smoke,
OpenAPI/verifier, DebugBackendClient.swift/KufrDebugPlugin.swift/Info.plist,
DebugPanel.ts/style.css/native UI harness a plán/README/stav.
Backend runtime testy, HTTP smoke a všechny kontrakty prošly bez provider
volání. Mobilní testy (19) a TypeScript/Vite build prošly. In-app Browser
prošel celým fake výběrem s textem a syntetickým mikrofonem, volbou hlasu/tempa,
vypnutou přihrádkou, návrhem, mazáním přepisu a samostatným log filtrem;
uživatelský text včetně HTML se vykreslí jako text a není v logu.
Rozložení 390/1280 px bez horizontálního přesahu/console chyb.
Read-only review vedlo k opravě souběžného cleanup, stejného klíče při release
retry, expirace návrhu po TTS, okamžitého mazání JS obsahu při pozadí a
odmítnutí starých status/mikrofonních callbacků. Swift harness pokrývá celý
výběr/STT/korelaci/expiraci a skutečný omezený loopback chunk/redirect/cancel;
Node fixture lokálně ověřena. Dvě chyby testovacích fixture (binární WAV jako
UTF-8 a 200 místo upload 202) opraveny při zachování striktního klienta.
UI skrývá prázdné měření před prvním tahem. Diff bez whitespace chyb,
tajemství nepřidána. Poslední head `e72316f3f8b5b25627d40303a01fb400fb2af7c6`
prošel [backend CI 36863902681](https://github.com/Blato58/DildoVander/actions/runs/36863902681),
[iOS CI 36863902326](https://github.com/Blato58/DildoVander/actions/runs/36863902326)
a [firmware CI 36863902366](https://github.com/Blato58/DildoVander/actions/runs/36863902366).
macOS ověřilo Swift/loopback harness, Xcode build a spuštění simulátoru;
firmware Arduino/ESP-IDF a host testy prošly. Všechny push ověřeny,
[PR #74](https://github.com/Blato58/DildoVander/pull/74) sloučen 1. 10. do
`09edc403368cbf297bf3c50f9f32371ccdb91386`, merge ověřen scoped fetch/fast-forward.
[Deploy 36864816231](https://github.com/Blato58/DildoVander/actions/runs/36864816231)
a [Ad Hoc 36864820809](https://github.com/Blato58/DildoVander/actions/runs/36864820809)
spuštěny z téhož merge. Deploy prošel loopback health a veřejným admin/no-op
smoke bez provider volání. Nezávislé lokální HTTPS čtení potvrdilo
`responses/openai`, oba providery dostupné, 0 aktivních sezení,
`until_restart`, správný katalog a 32k PCM16 WAV. Credential zůstává
stejný, chráněný lokální soubor ani backend secret se neměnily.
Ad Hoc prošel všemi joby: web/Swift/Xcode/simulátor, archive/export/podpis,
publikace a externí ověření IPA/manifestu/checksum. `/ios/` v in-app Browser
i lokálním HTTPS nabízí verzi 1.0/**build 37**, release `36864820809-1`,
stejné Bundle ID `app.turquoise6409.green2444`, HTTP `no-store`.
Nezávislé stažení ověřilo MIME a SHA-256
`1b25e1077ecc55ca5768fd4d8ce1756dc0eec8109ef8a7d03737a5874c2b60c6`.
Inspekce skutečné veřejné IPA potvrdila build/Bundle ID, microphone usage
permission, všechny čtyři nové nativní metody i interaktivní JS/Cedar;
testovací native-ui-harness není v balíčku. Starší build 30 zůstává verzovaný
pro návrat, hlavní stránka odkazuje na build 37. Žádné tajemství nebylo měněno
ani přidáno do aplikace. Záznam předání je samostatný dokumentační commit.
Další krok: majitel v Safari aktualizuje build 37, znovu nastaví nativní
`debug-server-admin` přístup a obnoví režimy. V nové sekci zkušebního rozhovoru
ověří vlastní text/mikrofon, český Cedar/Marin/Onyx/Coral a čas dialogu/TTS/
stažení na skutečném iPhonu. Server zůstává `responses/openai`; spuštění
AI rozhovoru může čerpat limit. Placené provider volání z agentních testů
neproběhlo; kvalita/latence a permission skutečného telefonu se ještě
nepotvrdily. Fyzický ESP/I2S zůstává závislý na B1, firmware se nenahrával.

**D109 – serverové režimy nasazené a aktuální IPA publikovaná.**
Majitel žádá ovládání serveru v Debug a opravu staré aplikace na `/ios/`.
Veřejná stránka nabízela release `36636991415-1`, verzi 1.0/build 10; HTTP
`no-store` je funkční, novější unsigned CI samo nepodepisuje/nepublikuje IPA.
Rozsah: samostatné admin hash oprávnění a oddělená `debug-server-admin` identita,
`GET/PUT /v1/admin/modes`, dialog fake/Sol a hlas fake/STT-TTS, revize a odmítnutí
změny za živého sezení/doznívajícího provideru, runtime platnost do restartu.
Native profil drží credential jen v RAM, změna nemá místní řídicí povely.
Fake test ověřuje oba režimy také po create a při neshodě uklidí vlastní sezení
bez opening/provideru. Kontrakt a README rozšířeny, serverový workflow odvozuje
hash ze samostatného environment secretu a veřejný smoke pouze čte/no-op,
bez přepínání produkčních režimů nebo provider volání. Credential správce je
provisionovaný mimo repo v `backend-production`, lokální kopie má ACL vlastníka.
Změněné soubory: backend `ServerAdminAccess.cs`, `DeviceAccessStore.cs`,
`ConversationServerModes.cs`, `ServerModeEndpoints.cs`, oba provider runnery,
`Program.cs`, backend/unit/HTTP/public smoke testy, `kufr-backend.yml`, backend
OpenAPI a jeho verifier, nativní `DebugBackendClient.swift`/`KufrDebugPlugin.swift`,
`DebugPanel.ts`/`DebugJournal.ts`, native harness/Swift testy a plán/README/stav.
Lokálně: .NET build bez varování, backend runtime testy včetně správy/auth,
HTTP smoke s 401/no-op/revision/capability testy, 19 mobilních testů a Vite/TS
build prošly. Python deploy a iOS publikační testy prošly. Nové Swift testy a
Xcode prošly na macOS CI. Kontrakty prošly; in-app Browser ověřil fake/AI
přepnutí přes syntetický bridge, gating hlasu, konflikt/obnovení, samostatný
filtr logu a rozložení 390/1280 px bez horizontálního přesahu a console chyb.
Nejde o skutečný server ani iPhone. Roslyn test impact kvůli chybějící
solution vrací `ambiguous_solution`; cílené příkazy vycházejí ze skutečných
projektů.
Předání: commit `d6de8cb154ed8609e124258d1f477006c5b2bf36` pushnut a ověřen,
[PR #73](https://github.com/Blato58/DildoVander/pull/73) sloučen po zeleném
[backend CI 36856939562](https://github.com/Blato58/DildoVander/actions/runs/36856939562),
[iOS CI 36856939581](https://github.com/Blato58/DildoVander/actions/runs/36856939581)
a [firmware CI 36856939621](https://github.com/Blato58/DildoVander/actions/runs/36856939621).
Merge `7dfa10bbf7d556e71172838a8463fc68c328c8d1` ověřen scoped fetch/fast-forward.
[Deploy 36857643096](https://github.com/Blato58/DildoVander/actions/runs/36857643096)
aktivoval právě tento commit s `dialog_mode=responses`, `voice_mode=openai`;
loopback health a veřejný admin/no-op/revision/diagnostics smoke prošly.
Lokální nezávislé HTTPS GET potvrdilo `responses/openai`, oba providery
konfigurované, 0 aktivních rozhovorů, `until_restart`, 401 bez credentialu
a `Cache-Control: no-store`. Neproběhlo žádné skutečné provider volání.
[Ad Hoc běh 36857646275](https://github.com/Blato58/DildoVander/actions/runs/36857646275)
ze stejného commitu prošel web/Swift/Xcode/simulator, archive/export/podpis,
publikací a nezávislým veřejným stažením IPA/checksum/MIME. `/ios/` a manifest
ověřeny také lokálně a v in-app Browser: verze 1.0/**build 30**, release
`36857646275-1`, stejné Bundle ID `app.turquoise6409.green2444`.
SHA-256 IPA: `80e213095971c0c9823f0e3d2ded49890e3e82eaa4cc5363951ef73d3aa72e61`.
Starý build 10 zůstává ve verzovaném adresáři pro návrat; kořenová stránka už
odkazuje na build 30. Není potřeba mazat aplikaci ani měnit podpisovou identitu.
Další krok: majitel na registrovaném iPhonu v Safari aktualizuje build 30,
v Debug nastaví HTTPS backend, `debug-server-admin` a svůj chráněný credential,
ověří režimy a hlas na telefonu. Tato skutečná iPhone/AI/audio akceptace ještě
neproběhla; konfigurace a syntetické testy ji nenahrazují. Fyzický ESP/I2S/GPIO
runtime nadále čeká na B1, neprovádělo se nahrání do desky.

**D108 – iOS Debug sloučen do main po zeleném CI.**
Záložka nabízí pozorování přijatých ESP snapshotů, omezený redigovaný log a
nativní hlasový test na telefonu. Diagnostický backend test je oddělen od
místního ovládání a neuděluje lease ani neposílá pohybové povely. Lokální testy
a syntetické UI nejsou iPhone/ESP akceptace. B1 neblokuje tuto softwarovou
přípravu; nový backend ani IPA se tímto zadáním nenasazují. [PR #72](https://github.com/Blato58/DildoVander/pull/72)
má head `1f028be5ba8f975dfbde706a27168f0ff2f846cf`, merge
`ae4ffa7c436abf74b5e356a529e562a89cc71557`; push a merge na `main` byly ověřeny.
Pro živý AI test
zbývá nasadit D106/D108, provisionovat samostatnou `debug-*` identitu a explicitně
zapnout hlasový provider s limity. Další krok: samostatné autorizované nasazení
a nová IPA/iPhone zkouška, případně pokračování nezávislého STT/audio softwaru.
Fyzická ESP/I2S akceptace nadále vyžaduje B1.

**D106/D107 – AI hlas připraven v backendu a samostatné firmware softwarové cestě.** Majitel
upřesnil, že kufr musí mít připravené mluvení. Závěr D105 byl příliš široký:
B1 blokuje fyzický port/akceptaci, nikoli backendové STT/TTS adaptéry a hostitelské
audio ověření. Dokončený rozsah: explicitně zapínané provider adaptéry za existujícími
audio API, asynchronní polling/cancel/idempotence, nákladová rezervace, normalizace
PCM16 mono 32 kHz a testy bez skutečného OpenAI volání. Podrobné oddělení je v §7 plánu.
D106 je po zeleném [CI 36842054906](https://github.com/Blato58/DildoVander/actions/runs/36842054906)
a [PR #70](https://github.com/Blato58/DildoVander/pull/70) v `main`:
commit `9863b20caaaf4d72d286c2cf8c07b03d0af4830f`, merge `1f172a4ff0546dc86a380ca129de73c8adce1cbb`;
push byl ověřen a cílová větev načtena scoped fetch/fast-forward. Server se nenasazoval.
D107 přidává striktní speech operaci, bezpečný caller buffer a HTTPS adaptér
s hostitelsky ověřeným řetězcem validovaná replika → speech → poll → WAV → release.
D107 je po zeleném [CI 36845545981](https://github.com/Blato58/DildoVander/actions/runs/36845545981)
a [PR #71](https://github.com/Blato58/DildoVander/pull/71) v `main`:
head `bc48fb3514c5095214affa95a736d06e73a607a4`, merge `430af8d48b174677fe9aae4ecb340fd4effe5857`.
Host testy i ESP-IDF/Arduino build prošly; merge byl ověřen scoped fetch/fast-forward.
Není zapojen do současného textového runtime ani I2S; skutečný capture/playback,
consent vstup a paměť vyžadují B1. Samostatný firmware STT upload a mobilní audio
zůstávají dalšími softwarovými částmi E9/E10, nevydávat celý projekt za blokovaný B1.

**Historická kontrola D105, opravená D106:** majitel znovu potvrdil, že podklady B1 nemá. Původní závěr o úplné blokaci byl příliš široký; firmware README bylo opraveno podle již složeného WSS runtime, nezávislá hlasová příprava ale může pokračovat.

**Předání 1. 10.: nezávislé E10.1–3 a E4.4t dokončené v softwarovém rozsahu, další krok B1.** Nativní transport/TLS/Keychain ([PR #65](https://github.com/Blato58/DildoVander/pull/65), merge `5f54104b783c8cf2fc7e1885276322d535681f7b`), text/cancel/result ([PR #66](https://github.com/Blato58/DildoVander/pull/66), merge `ed7ea0212a9f345f78da32caf51abf6e1a57dc9c`) a firmware/nativní Bonjour ([PR #67](https://github.com/Blato58/DildoVander/pull/67), merge `6315857414e2c6030e991f3ddda43aa21ceee28a`) jsou po příslušných zelených CI sloučené a ověřené scoped fetch/fast-forward na `main`. E10 fyzická akceptace, servisní provisioning/QR a audio zůstávají otevřené; firmware nadále odmítá zotavení bez B1.

**D75/D76 ověřeny v softwarovém rozsahu:** nativní AI funkce, trvalá paměť, pouze Sol, skutečný adaptivní rozhovor přes nasazený backend a hostitelský replay jeho návrhu. [Přepis a důkazy](../ai-eval/reports/2026-09-30-live-backend-dialogue.md). Server běží v `responses` s `gpt-6.1-sol`; testovací sezení uzavřena a syntetický večer vymazán. Fyzická integrace ESP32 a samostatný hlas zůstávají otevřené.

**Předchozí obsahový požadavek majitele 30. 9.:** D74 přidává hovorový party dialog, opening a trvalou paměť večera na backendu; syntetické placené srovnání je schváleno do 1 USD. Integrace nových API do firmware/telefonu ani nasazení nejsou součástí dosavadního ověření.

**Aktuální firmware cesta (D159):** E4 má ESP-IDF runtime se samostatným backend klientem, odděleným LAN vlastníkem, journalem a bezpečnostními bránami. D158 přidalo BLE/PoP, persistentní hotspot/fallback a podepsané OTA. D159 na skutečné samostatné N16R8 ověřilo USB servisní identitu/PoP/trust, NVS readback, domácí Wi-Fi, validovaný backend HTTPS token POST200, úspěšnou OTA0.1.1/VALID boot a další restart/reconnect. Tato bootstrap cesta již není blokovaná chybějící identitou nebo obecnou TLS/NVS/STA zkouškou. Celé E4 zůstává rozpracované: LAN TLS/pairing/WSS a skutečný konverzační průchod mají vlastní akceptaci. E5/E6 vyžadují fyzická měření a kalibrace B1; E9 skutečné audio a E10 další iPhone/LAN scénáře. Historické slices výše nepopisují dnešní blokace.

**Další konkrétní krok E4/E5 (D159):** Pro síť v místě akce doplnit a přijmout iPhone hotspot/failover podle event-readiness; domácí fallback je již ověřený. Samostatně dokončit lokální LAN TLS/pairing/WSS servis a akceptaci. Pro E5 dodat schéma/měření externího napájení a přímého PCA OE, bezpečné inspection/zotavení a kalibraci jedné přihrádky. Chip, flash/PSRAM, UART/native USB a USB-only aplikační flash/provisioning již mají důkaz D122/D159; B1 je dál podmínkou připojených pohonů, nikoli opakování dokončeného síťového servisu samostatné desky. Nedoplňovat fake inspection ani vydávat časovanou polohu za měřenou. Placené AI, audio a celé mechanické přijetí zůstávají samostatné scénáře.

**Navazující provisioning/QR (D159):** Síťový USB network-service a následné nativní PoP/BLE nastavení domácí Wi-Fi jsou hotové v doloženém rozsahu samostatné desky bez pohonů. To neprovisionuje místní LAN TLS certifikát/SPKI ani jednorázový pairing credential pro `/v1/pair`. Servisní AP/USB a QR cesta v §4 proto mají vlastní navazující rozsah; současný `/v1/pair` očekává již uloženou LAN TLS/pairing identitu a nevytváří AP. QR scanner ani permission ji nenahrazují. Zachovat tajemství mimo JS/logy, rozlišovat cloudový device credential, BLE PoP a LAN párování. B1 blokuje neověřené fyzické efekty a servis celé sestavy; nezneplatňuje ověřený USB síťový servis D159.

**E4.3d2b2 hostitelsky ověřeno 30. 9.:** autonomní cyklus prezentace s trvalým journalem, invalidací/publikací snapshotu a bezpečným uzavřením po návratu či Stop. Rozsah je čisté C++ jádro a fake port; fyzický port a místní autorizovaný vstup zůstávají samostatnými navazujícími kroky.

**E4.3d2b3 hostitelsky ověřeno 30. 9.:** omezené předání textové repliky ze síťového pollu budoucímu výstupnímu portu. V tomto slice vznikla pouze paměťová schránka s vazbou na autoritativní snapshot a hostitelský test; I2S/TTS ani fyzický výstup se nepřidávají.

1. USB síťový provisioning, náhodný credential/PoP mimo image a skutečný NVS readback jsou doložené D159. Uchovat soukromé údaje a recovery balíček; tento servis znovu neprovádět bez konkrétního důvodu.
2. Domácí Wi-Fi, backend HTTPS/token a restart prošly na ESP0.1.1. Hotspot/failover, LAN TLS/pairing/WSS a celý rozhovor/audio přijmout zvlášť; token POST200 je nepokrývá.
3. E5/E6 a fyzické audio/periferie potřebují zbývající B1. iOS1000.0.19 BLE a status jsou majitelem potvrzené, další E10 LAN/Keychain/lifecycle a mechanická akceptace zůstávají otevřené.

Dotčené cesty: `kufr/firmware/`, související README a tento stav. D159 síťový servis, firmware OTA a přihlášení jsou dokončené v doloženém rozsahu; následující fyzická akceptace pohonů potřebuje zbývající B1. Přesný aktuální build a důkazy jsou nahoře v D159.

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
| Firmware | ESP-IDF6.1 runtime se STA/backend HTTPS, odděleným LAN vlastníkem, journal/safety snapshotem a textovým input/output. D158 BLE, profily a OTA; D159 skutečný USB/NVS servis, home Wi-Fi, token POST200, OTA0.1.1/VALID boot a restart. Arduino profil je odstraněný. | B1: externí napájení/OE/mechanika/kalibrace a skutečné I2S; hotspot/failover, LAN TLS/pairing/WSS a celý konverzační průchod. |
| Mobil | Web/simulátor, Capacitor8.5.2 a native Swift SPKI/Keychain/pairing/WSS, text/cancel/result, Bonjour a Debug/hlas. D161 BLE Wi-Fi párování a Keychain; D163 opravuje WSS offer/retry a podepsaný iOS1.0/build1000.0.21 je zveřejněný. D159 majitel skutečně použil PoP/BLE na1000.0.19, nastavil domácí Wi-Fi a potvrdil připojený backend po restartu. | LAN QR/TLS/pairing/Keychain/lifecycle, hotspot/failover a skutečné audio zkoušky; majitel potvrdil scan/párování, ale LAN v Ovládání se opakovaně odpojuje; přesné instalované verze nejsou doložené. |
| Backend/kontrakty/simulátor | SQLite/idempotence/retence, Responses Sol a trvalá oddělená paměť večera, izolovaný hlas a technické logy. Backend je nasazený; D159 fyzická device identita je trvale registrovaná a skutečný ESP HTTPS token POST200 prošel i po restartu. Strict v1 kontrakty a simulátor zůstávají. | Skutečný celý device rozhovor/hlas, živá evaluace a lidská/provozní akceptace; produkční region/retence. |
| iOS CI | Stávající ruční macOS workflow: D163 native testy, Xcode archive a Ad Hoc export37929159117; iOS1000.0.21 publish37929535139 a public IPA/hash prošly. D159 majitel použil BLE na iPhonu s1000.0.19. | Další LAN/TLS/Keychain/lifecycle iPhone zkoušky a řízená app aktualizace/rollback. |
| Hardware | D122/D159 konkrétní ESP32-S3 rev0.2, N16R8, ROM flash/PSRAM a UART/native USB; USB-only aplikace a skutečná OTA na samostatné desce. Dodaný pinový podklad a návrh napájení zůstávají. | PCB/schéma externího zapojení, bezpečnostní větev/OE, LED, napájení, mechanika, kalibrace a fyzická akceptace periferií. |

Zdroje: [firmware](../firmware/README.md), [jeho konfigurace](../firmware/platformio.ini), [sekvence](../firmware/src/features/wand_selection/SelectionSequence.cpp), [hostitelský test](../firmware/tests/selection_sequence_test.cpp), [mobilní vstup](../mobilni-aplikace/src/main.ts), [package.json](../mobilni-aplikace/package.json). Tento přehled aktualizuj, až se skutečný stav změní.

## Etapy

Přesné závislosti a kritéria dokončení jsou v kapitole 12 plánu. Následující tabulka je stav, nikoli jejich zjednodušená náhrada.

| Etapa | Stav | Dokončený rozsah / důkaz | Zbytek nebo podmínka dalšího kroku |
| --- | --- | --- | --- |
| E0 – provozní předpoklady | rozpracováno | Architektonické výchozí předpoklady v plánu; žádná měření. | Vstupy B1–B5 níže; software E1 může začít. |
| E1 – bezpečné jádro | hotovo | E1.1: Stop v každé fázi, safety port, 250/500/5000ms limity, explicitní zotavení (D1). E1.2: hostitelský timeout průchodu smyčkou, porucha, výpadek ručního lease a přednost Stop; hostitelské testy a firmware build prošly (D2). | D156 používá přímé OE a fyzickou zelenou kolébku; softwarový limit se uplatní při dalším průchodu smyčkou. |
| E2 – smlouvy a simulátor | hotovo | E2.1–4 kontrakty, C++ model, fake journal, parser a WSS; E2.5 časová osa s browser ověřením (D10,D22); E2.6 OpenAPI, E2.7 reload, E2.8 fake text/cancel, E2.9 fake media parser/socket (D3–D18). Ztracený ACK, duplicitní/pozdní příkaz, restart a zotavení kryjí hostitelské testy a WSS scénáře. | Fake není skutečný audio buffer, NVS ani backend; tyto hranice patří E4/E7/E9. |
| E3 – ruční mobil bez HW | hotovo | E3.1 fake UI (D19), E3.2a ověřený místní most (D20), E3.2b UI adaptér a celý výběr/reload/Stop/odpojení proti C++ simulátoru, cílené testy ztracených zpráv a nového lease (D21). | Jde pouze o webovou/simulátorovou akceptaci E3; nativní iPhone, TLS pinning a fyzický kufr patří E3a/E10/E5. |
| E3a – iOS CI základ | hotovo | Capacitor 8.4.3, iOS projekt/SPM/sdílené scheme; web, Xcode a simulator CI (D50). D70: [Ad Hoc běh 36636991415](https://github.com/Blato58/DildoVander/actions/runs/36636991415) podepsal a ověřil IPA, publikoval ji na vlastním HTTPS serveru s manifestem a externě znovu ověřil; instalační web prošel browser kontrolou na mobilním viewportu. Majitel 30. 9. potvrdil instalaci a spuštění na iPhonu 15 Pro. | Native WSS/Swift bridge, LAN oprávnění, Keychain a aktualizace aplikace patří E10/E11; nejsou doložené tímto startem. |
| E4 – runtime a přímá síť | rozpracováno | E4.1: ESP-IDF 6.1 profil, CMake, bezpečná smyčka, 16MB oddíly a lokální build; původní Arduino profil byl odstraněn D157. E4.2a: trvalý journal a NVS adaptér v IDF sestavení, hostitelský restart a chyby úložiště (D34). E4.2b: formát identity, softwarová servisní brána a NVS čtení (D35). E4.3a: tokenový model, parser a HTTPS adaptér (D36). E4.3b: NVS síťové nastavení, Wi-Fi STA/SNTP/čas v samostatném tasku (D37). E4.3c1–11: JSON kodeky, HTTPS adaptéry startu/tahu/pollu, guard korelace, UUID generátor a časování pollu (D38–D48). E4.3c12: interní čekající text a síťový fake cyklus (D49). E4.3d1: uzamčený snapshot a invalidace starého textu/výsledku (D51). E4.3d2a: vlastník bezpečných změn snapshotu v IDF smyčce (D64). E4.3d2b1: fronta přijímá jen předem ověřený snapshot (D71). E4.3d2b2: autonomní journal, snapshot a okamžitý Stop ověřeny hostitelsky (D77). E4.3d2b3–5: omezená replika, korelace tahu a mapování místního sezení (D78–D80). E4.4a–g: vlastník lease, přijetí textu, společná generation, místní brána, sdílené boot ID, čistý párovací model a ESP-IDF úložné/kryptografické adaptéry (D81–D87). E4.4h–s: bearer/admission, RNG/TLS, parser/fronta, WSS server/router, pravdivý output/reply, HTTPS párování a runtime s jediným LAN vlastníkem (D88–D99). D158 BLE/profily/OTA; D159 skutečný USB provisioning, NVS readback, Wi-Fi/trusted-time/backend HTTPS POST200, OTA0.1.1/VALID boot a další reset/reconnect. | Hotspot/failover, LAN TLS/pairing/WSS a celý device konverzační průchod; inspection/kalibrace B1 pro pohony. USB-only síťový servis a backend přihlášení jsou přijaté, fyzická/hlasová prezentace není. |
| E5 – jedna bezpečná přihrádka | rozpracováno | D156: skutečné GPIO/I2C/RMT adaptéry a kalibrovaná časovaná prezentace jsou sestavené a hostitelsky testované; fyzické měření chybí. | E0/E1/E4; B1, fyzická akceptace. |
| E6 – šest přihrádek | nezahájeno | Bez fyzického ověření. | E5; cykly, proudy, dostupnost/reload. |
| E7 – backend bez skutečné AI | hotovo | E7.1–2 identity/fake dialog; E7.3a–c SQLite idempotence, restart a kvóta; E7.4a–b cleanup/retence a rušení; E7.5a–b WAV fake hlas; E7.6 transakční nákladová rezervace. Lokální HTTP/SQLite testy přístupů, souběhu, restartu, retence a logů prošly (D23–D32). | Skutečné provider ceny/limity/klíče a placená akceptace patří E8/B4; device HTTPS/TLS integrace E4. |
| E8 – skutečný textový dialog | rozpracováno | D53–D54: samostatný Responses eval a party prompt. D57: 60 českých případů a dvě jazykové odbočky. D60–D62: backendový Responses klient, cena a asynchronní napojení za výslovný přepínač a rezervaci. D63: jeden nasazený syntetický placený tah. D66: modelový vícekolový dialog s omezenou RAM historií. D68: tři skutečné placené tahy přes veřejné HTTPS, dvě AI otázky a návrh s navázáním na odpovědi. D72: původní šestičlenný katalog a tematický kufr Ollivanderova obchodu, backend/eval filtrování dle readyMask ověřeno offline. D73: nové téma včetně prvního představení kufru prošlo veřejným tříkolovým testem; server vrácen do fake režimu. | Širší lidské hodnocení, produkční region/retence, zařízení a fyzické potvrzení; B1/B5. |
| E9 – samostatný hlas kufru | rozpracováno | Historické D106/D107: backend STT/TTS, WAV a speech/poll/download/release adaptéry. D119/D128 a současná ESP-IDF kompozice: samostatný Realtime/GPT-Live runtime ESP→backend, PCM upload, I2S capture/playback, převod vzorkování, drain/ACK a Stop; host testy a firmware build prošly. | Fyzické zapojení a servisní audio gate, mikrofon/reproduktory/PTT, DMA/latence/echo/resetové ticho, souběh s pohony a celý skutečný český rozhovor; B1/E5 a lidská akceptace. Starší oddělená STT→dialog→TTS firmware integrace není zapojená, ale neblokuje implementovanou Realtime/GPT-Live cestu. |
| E10 – dokončení mobilní zálohy | rozpracováno | E10.1: Swift párování, Keychain, SPKI pinning a WSS; sdílený ruční řadič, strict schema a pozorování po připojení. E10.2: device-owned text/cancel/result; E10.3: Bonjour a adresní fallback; D108: Debug/log a samostatný hlasový test telefonu. macOS loopback TLS/WSS/Bonjour/debug, Xcode a iOS Simulator prošly (D101–D108). D158 podepsal/publikoval iOS1000.0.19; D159 majitel potvrdil PoP/BLE, domácí Wi-Fi a backend status po OTA/resetu. | LAN QR/TLS/pairing/Keychain/lifecycle, hotspot/failover a skutečné audio zkoušky; BLE acceptance nenahrazuje celý E10 průchod. |
| E11 – provozní balení | rozpracováno | D52–D59: Ubuntu backend, systemd a rollback. D67: veřejné IP HTTPS, Nginx, obnovovací timer a externí TLS/health sonda. D70: `/ios/` statická stránka, Ad Hoc IPA/manifest s externí kontrolou podpisu, MIME, hashe a HTTPS. D158/D159 nová podepsaná iOS aplikace a úspěšný development OTA/device boot s NVS retention jsou doložené. | Reálná obnova certifikátu po expiraci, OTA negativní/power-loss/rollback a řízená app aktualizace; plné E11 vyžaduje E6/E8/E9, zálohu E10 a provozní zkoušku. |
| E12 – obraz telefonu | rozpracováno | D157: JPEG upload/poll, transient cue ve všech dialogových režimech, ruční oprava, native iOS náhled a one-use draft; lokální software kontroly prošly. | Native macOS/iPhone kamera/pozadí, skutečná image inference a přínos; celková akceptace dál vyžaduje E8/E11. |
| E13 – OV5640 + SD profil B | rozpracováno | D157: před firmwarem pull `3d0e73a`, OV5640 driver/SCCB1, explicitní souhlas, přímý upload, servisní gate false a výchozí ESP-IDF build prošly. Arduino profil odstraněn. | Kamera nedoručená, fyzické FPC/piny/napájení/proud/souběh/obraz, SD samostatně; E0 pro B/E9/E12 zůstávají. |

## Chybějící vstupy a blokace

Jde o budoucí podmínky konkrétních etap, nikoli důvod neprovést E1/E2. Další otázky a výchozí rozhodnutí zůstávají v kapitole 13 plánu.

| ID | Chybějící vstup a kdo ho dodá | Co blokuje | Co může pokračovat |
| --- | --- | --- | --- |
| B1 | **Částečně ověřeno D122/D159:** ESP32-S3 rev0.2, 16 MB Quad flash/3,3 V, embedded8 MB PSRAM, UART COM7/native USB COM8; D122 USB-only diagnostika a pattern test. D159 samostatná deska bez pohonů: aplikační flash, USB provisioning, NVS/STA/backend TLS a skutečná OTA0.1.1/VALID boot prošly. Stále chybí PCB/schéma externího zapojení, LED, 12V/5V napájení, přímé OE/reset, mechanika, servisní/inspection vstup a kalibrace celé sestavy podle D156. | Provisioning/provoz s periferiemi, E5/E6 a HW akceptaci E9/E13; neblokuje dokončený USB-only síťový servis. | Přijatý síťový provoz samostatné desky D159, další síťové/LAN zkoušky, hostitelské testy a software. |
| B2 | **Vyřešeno pro Ad Hoc export:** environment `ios-signing` obsahuje distribuční `.p12` a profil pro Team `3QRXV8G5Y3`, Bundle `app.turquoise6409.green2444`, jedno zařízení, `get-task-allow=false`, expiraci 24. 12. 2026. Stejné `IOS_P12_PASSWORD` OpenSSL přečetl; [běh 36636991415](https://github.com/Blato58/DildoVander/actions/runs/36636991415) importoval přebalenou identitu, ověřil shodu certifikátu s profilem, archiv, podpis a embedded profil IPA. | Podpis již neblokuje E3a; obnovit certifikát/profil před expirací. | iPhone akceptace B3 a další software. |
| B3 | **Vyřešeno pro první instalaci E3a:** majitel potvrdil iPhone 15 Pro s iOS 26.6.1 a správné UDID v profilu. Stránka [https://2.28.202.220/ios/](https://2.28.202.220/ios/) veřejně vrací podepsanou verzi, `itms-services`, manifest a IPA; D70 ověřil externí HTTPS download a majitel 30. 9. potvrdil instalaci i spuštění. D159 navíc potvrdil PoP/BLE a network/backend status v publikované iOS1000.0.19. | LAN/Keychain a ovládání E10, aktualizace/rollback E11 stále vyžadují samostatnou zkoušku. | E4.3d2b a další software může pokračovat bez telefonu. |
| B4 | Majitel dodal Ubuntu server s veřejnou IP `2.28.202.220` a runnerem `dildovander-server`; backend běží na loopback. OPENAI_API_KEY je v GitHub environmentu; D63 ověřil jeden placený Responses tah na loopback API. D67 root bootstrap vydal platný IP SAN certifikát, zapnul Nginx a obnovovací timer; externí [sonda 36633620137](https://github.com/Blato58/DildoVander/actions/runs/36633620137) ověřila veřejnou TLS důvěru, health, platnost a HTTP přesměrování. D159 provisionovalo identitu `kufr-68ee8f4b6048`, zachovalo původní device index0 a trvale přidalo index1; skutečný ESP TLS/token POST200 i po OTA/resetu prošel. Produkční region/retence AI dat zůstávají otevřené. D157 ruší softwarové peněžní capy; finance vlastní OpenAI účet. Historicky majitel 29. 9. potvrdil projektový limit 10 USD v OpenAI, dnešní stav účtu se neověřoval ani neměnil. | Produkční podmínky AI dat a celý device rozhovor/hlas; identita a HTTPS/token bootstrap jsou vyřešené pro tuto desku. | Přijaté ESP/backend HTTPS spojení D159, další síťové a konverzační zkoušky, CI a E8 eval korpus. |
| B5 | Katalog šesti původních hůlek a Harry Potter / Ollivander tematickou roli majitel svěřil nezávislému agentovi; zdroj je `ai-eval/wand-catalog.json` (D72). Stále chybí rozměry, router, prostředí akce a živé obsahové hodnocení s hosty. | Provozní akceptaci E8/E11, fyzické mapování hůlek a finální mechaniku. | Sdílený tematický katalog, vývojové a simulované scénáře; skutečné přihrádky až s HW. |

## Deník změn a ověření
### D108 – 1. 10. 2026 – iOS Debug a izolované hlasové testy

- Rozsah: mobilní Ovládání/Debug tab, read-only snapshot telemetrie, uptime/boot/revision/eventSequence/RTT/fault count, Stop, RAM log 200 událostí s filtrem/pause/clear/export. Swift plugin vlastní zabezpečený dialog, dočasný backend credential, HTTPS bez redirectů, opening/speech/poll/WAV/AVAudioPlayer a systémový hlas. Backend doplňuje autentizovaný metadata endpoint a OpenAPI. Změny patří `mobilni-aplikace/src/features/debug/`, mobilnímu vstupu/stylům/adaptérům/testům, dvěma Swift zdrojům/Xcode registraci, nativnímu CI a dotčeným backend/contract/docs souborům; firmware se nemění.
- Ověření na Windows: mobil pod Node 24.21.0 `npm test` (19/19) a `npm run build` — úspěšně. `dotnet build backend/backend.csproj --no-restore` z `kufr` — bez warningů/chyb. `node backend/tests/smoke.mjs` pod Node 24.21.0 — úspěšně, včetně 401 bez access tokenu/za credential a safe fake diagnostiky. `npm test` v `contracts` pod Node 24.21.0 — strict fixtures, OpenAPI/security a média prošly. První buildy odhalily DOM iterátor a chybně umístěné telemetry pole, contract verifier potřeboval přidat novou cestu; po opravách všechny uvedené kontroly prošly. `git diff --check` bez chyb.
- Browser: in-app Browser proti Vite 127.0.0.1:5174 a nativnímu syntetickému harnessu. Ověřeno přepnutí tabů, mobilní 390px i desktop 1280px layout bez vodorovného overflow, párování/connect recovery fixture a skutečná pole snapshotu, profil/metadata/fake voice větev, zakázané AI tlačítko pro fake, Stop hlasu, pause/filtr/clear a klávesnicové přepnutí. Po opravě pause zůstává log během hlasové akce zmrazený a po resume ukáže nasbíranou akci. Console bez warn/error; screenshot uložen jako lokální náhled mimo repo. Syntetický bridge neprovádí žádný síťový/AI test.
- Read-only review opravilo ztracenou create odpověď, identické JSON bytes při retry, persistentní chybu cleanup a cancellation během create. Nativní harness navíc testuje profil, korelaci, WAV/MIME/size, fake/OpenAI mismatch, cleanup, retry a skutečně zrušený Task; na Windows nelze spustit Swift/macOS.
- Neprovedeno: placená AI, nasazení backendu, podpis/publikace IPA, živé iPhone audio/alert/lifecycle, ESP nebo I2S/GPIO. Tiché fake WAV nesmí být vydáváno za AI TTS. Nový debug profil vyžaduje předem provisionované samostatné zařízení `debug-*`; místní WSS token se nepoužívá. Credential/audio nevstupují do JS/logu, rozhovor může zůstat v paměti testovacího večera. Backend zůstává v dosavadním deploymentu, nová metadata route/hlas tam tímto nejsou aktivovány.
- Doručení: první commit `87f068d87a703e8848ffd4a916494eb346548c0b` a finální head `1f028be5ba8f975dfbde706a27168f0ff2f846cf` pushnuty ověřeným skriptem. První [iOS CI 36852443796](https://github.com/Blato58/DildoVander/actions/runs/36852443796) zkompilovalo Swift klienta, ale harness selhal na vadné WAV fixture (měnila již nulový bajt vzorkovací frekvence). Po opravě zelené [iOS CI 36852710009](https://github.com/Blato58/DildoVander/actions/runs/36852710009): native debug harness včetně skutečného Task cancel/create recovery, transport testy, Xcode build a iOS Simulator launch. [Backend CI 36852710066](https://github.com/Blato58/DildoVander/actions/runs/36852710066) a [firmware/contracts CI 36852709863](https://github.com/Blato58/DildoVander/actions/runs/36852709863) také zelené. [PR #72](https://github.com/Blato58/DildoVander/pull/72) sloučen až po těchto kontrolách; merge `ae4ffa7c436abf74b5e356a529e562a89cc71557` načten scoped fetch a ověřen fast-forward na `main`. iPhone/paid/deploy zůstávají neprovedené. Tento navazující zápis mění jen dokumentaci.

### D107 – 1. 10. 2026 – firmware příprava výstupu AI hlasu

- `SpeechOperationCodec` sestaví existující request ze skutečného turn ID;
  přijme pouze `kind:speech`, přesnou korelaci, device target, UUID a validní
  UTC expiry. Sdílí beze změny významu UTC parser s `TurnPollGuard`.
- `SpeechAudioBuffer` nealokuje velkou paměť. Callerem dodaný buffer má strop
  1 920 044 B; ověřuje metadata, canonical WAV32kHz/mono16, celou délku,
  monotónní TTL a snapshot. Počátek/příjem/finish/PCM čtení ověřují stejnou
  autoritativní verzi; neúplná, stará nebo zrušená data se nulují.
- `EspIdfSpeechTransport` přidává POST speech, GET operation/audio a DELETE
  release. CA bundle/hostname, bez redirect/auth retry, bounded JSON/hlavičky,
  známá audio Content-Length a 15s download gate; credential stack se nuluje.
- `SpeechOutputCoordinator` použije existující silnou validaci dokončeného
  turnu, explicitní audio consent a `TurnPollSchedule` se společným 45s limitem.
  Lost acceptance retry zachová key/body, stale/cancel/deadline nic nevydají;
  po celém downloadu uvolní backendovou audio operaci. Data nejsou povel k servo/I2S.
- Všechny tři nové testy `speech_audio_buffer_test`, `espidf_speech_transport_test`
  a `speech_output_coordinator_test` jsou v CI. Lokálně v1.10. prošel MSVC
  `/std:c++14 /EHsc /W4 /WX` nad zdroji z firmware README a všechny executable.
  Testy kryjí metadata, poškozený WAV, overflow, korelaci, TTL, nulování,
  HTTPS konfiguraci/route/release, retry, source/consent, timeout a Stop.
  Úvodní test chybně zkoušel vrátit generation zpět a byl opraven; compile
  narrowing v testu byl opraven před úspěšným během.
- `src/CMakeLists.txt` sestavuje nové soubory, `platformio.ini` vynechává IDF
  síťový adaptér z Arduino profilu. `pio run -e esp32-s3-arduino-serial` prošel.
  Poslední `pio run` narazil na GCC internal compiler error/segfault v SDK
  `esp_lcd_panel_rgb.c`; beze změny zdrojů opakovaný `pio run -j 2` prošel
  (RAM65 228 B, flash1 147 437 B). Lokální odkazy Markdown a `git diff --check`
  prošly. Nezávislá read-only revize nenašla materiální chybu.
- Není skutečné TLS/HTTP na ESP32, ověřená PSRAM, I2S/PTT nebo poslech hlasu.
  `RunTurn` zůstává textový s audio consent false; nové třídy se do něj
  automaticky nepřipojují. Další runtime binding musí mít skutečný playback
  a consent port; STT upload je samostatná připravitelná část. E9 není hotovo.
- Implementační commit `09145cff006bec084bb0a8a2a145c91d91dda5ad` byl pushnut
  přes `scripts/Push-CurrentBranch.ps1`; script ověřil shodu vzdáleného head.
  Při závěrečné staged kontrole byla odstraněna nadbytečná EOF prázdná řádka
  nového kodeku; `git diff --check` byl po opravě zopakován.
- Předání 1.10.: finální head `bc48fb3514c5095214affa95a736d06e73a607a4` byl
  ověřen push scriptem. [CI 36845545981](https://github.com/Blato58/DildoVander/actions/runs/36845545981)
  prošlo: host-tests 41s, oba firmware profily 6m3s.
  [PR #71](https://github.com/Blato58/DildoVander/pull/71) byl bez konfliktu sloučen
  jako `430af8d48b174677fe9aae4ecb340fd4effe5857`; scoped fetch/fast-forward ověřil `main`.

### D106 – 1. 10. 2026 – backendová příprava skutečného AI hlasu

- Majitel požádal o připravený hlas kufru. §7 plánu nyní výslovně odděluje
  nezávislé softwarové E9 od fyzické akceptace; závěr D105 byl opraven.
- `backend/Features/VoiceTurns/`, `Speech/` a částečný `ConversationVoiceOperations`
  přidávají STT `gpt-transcribe` a TTS `gpt-4o-mini-tts-2025-12-15`/`marin`.
  `Backend:Voice:Mode=openai` vyžaduje klíč a kladné limity, výchozí fake zůstává.
  `Program.cs` ověřuje konfiguraci při startu. Existující trasy/SQLite schéma se nemění.
- STT/TTS mají 15s deadline, pouze jeden placený dialog/audio call, stejné
  idempotence/cancel/consent/device kontroly. Vlastní upload a odmítnuté audio se
  nulují. TTS generuje pouze uloženou repliku; filtrovaný PCM24→32kHz převod
  zachovává délku a potlačuje spektrální obraz. WAV nad30s se celý odmítne.
  Rezervace 0,01/0,05 USD zůstávají bez skutečného billing údaje konzervativně
  započtené; nejsou ověřenou skutečnou cenou. Neodeslaná práce se vypořádá nulou.
- `backend-tests/VoiceProviderTests.cs` ověřuje konfiguraci, provider HTTP formáty,
  frekvenci/délku převodu, neplatné/velké výsledky, replay, kopii uploadu, souběh,
  cost/consent, TTL/nulování, cancel/end/shutdown a pozdní výsledky po deadline.
  Backend CI nově validuje i PR, deploy zůstává pouze ruční z main.
- Ověření 1.10. v `kufr/`: `dotnet build backend/backend.csproj --no-restore`
  prošel bez warning/error; `dotnet run --project backend-tests/BackendTests.csproj --no-restore`
  prošel. Z kořene `node kufr/backend/tests/smoke.mjs` prošel;
  `python -m unittest discover -s kufr/backend/deploy -p 'test_*.py'` 3/3 a
  obdobný příkaz pro `kufr/backend/tests` 9/9 prošly. Počáteční compile/config/test
  chyby a změna existujícího busy kódu byly opraveny před úspěšným během.
- Neproběhlo živé placené OpenAI volání, poslech českého hlasu, cloud deploy
  ani hardware test. Firmware audio přenos a skutečný PTT/I2S ještě nejsou součástí D106.
  Další krok je nezávislá firmware audio příprava, nikoli uzavření E9.

### D105 – 1. 10. 2026 – ověřená blokace pokračování bez B1

- Převzetí: čistý `main` na `5b4ce39`; scoped `git fetch origin main` a `git rev-list --left-right --count HEAD...origin/main` potvrdily `0 0`. Plán, stav a skutečný runtime byly znovu porovnány. Majitel v tomto pokračování výslovně potvrdil, že podklady B1 zatím nemá.
- Evidence: `espidf_main.cpp` skládá provisionovaný TLS/WSS server, owner a backendovou síť, ale `UnverifiedInspection::InspectionAllowsRecovery()` vrací false, kalibrace zůstává 0 a safety port pouze loguje bez GPIO. Nezávislý read-only průchod mobilu/backendu potvrdil existující native transport/text/Bonjour a fake audio API. Servisní USB/AP a konkrétní QR envelope čekají na fyzickou bránu; E5/E6, E9 a telefonní audio mají nesplněné závislosti. E12/E13 zůstávají odložené. Nový proveditelný implementační slice podle plánu nebyl nalezen.
- Rozsah změn: firmware README opravuje zastaralé tvrzení o chybějícím WSS parseru/session/gateway a upřesňuje, že owner bránu vytváří při převzetí autentizovaného spojení, před hello/welcome. Tento zápis zachovává historická ověření D101–D104 a jejich rozsah. Kód, kontrakty a konfigurace se nemění.
- Ověření a předání: PowerShell kontrola obou změněných Markdown souborů ověřila 17 místních odkazů a vyvážené code fences; inventura všech 15 etap se shoduje s plánem. `git diff --check` prošlo a diff obsahuje pouze tento stav a firmware README. Aplikační buildy/testy se při dokumentační změně neopakují. Neprovedeno: HW/iPhone akceptace, placený AI test, podpis/publikace IPA, deployment ani firmware flash. Další krok zůstává doložená B1, potom fyzicky střežené provisionování a E4/E5 na jedné přihrádce.
- Doručení: commit `d5e7aa451eb1cd584801f24a64ba180d17a5113c` ověřeně pushnut projektovým `./scripts/Push-CurrentBranch.ps1` na `codex/kufr-b1-recheck`; [PR #69](https://github.com/Blato58/DildoVander/pull/69) obsahuje pouze tyto dvě dokumentační cesty. Sloučení se provede po kontrole dostupného CI a konfliktů.

### D104 – 1. 10. 2026 – předání dokončených nezávislých slices na B1

- D101–D103 jsou doručené na `main`, nikoli pouze místní návrhy: native transport/párování/Keychain, device-owned text/cancel/result a firmware/nativní Bonjour s ručním adresním fallbackem. D103 finální commity `043fd729087ca0c47a3fb2fe99468572183625ac`, `5e212ca4310f0a70ae60386d6a3892e3c7e99bb0`, `a9a34427eb8f0407f96176096bc1492872cf813c` a `e0be0dad1ca8925b7dfe318112228f286f00821e` byly ověřeně pushnuty projektovým skriptem. [PR #67](https://github.com/Blato58/DildoVander/pull/67) bez konfliktů/diff mimo scope sloučen jako `6315857414e2c6030e991f3ddda43aa21ceee28a`; scoped fetch a fast-forward lokální main tento merge ověřily.
- Finální [firmware CI 36797984092](https://github.com/Blato58/DildoVander/actions/runs/36797984092) a [iOS CI 36797984095](https://github.com/Blato58/DildoVander/actions/runs/36797984095) pro `e0be0dad1ca8925b7dfe318112228f286f00821e` prošly: všechny host testy, ESP-IDF/Arduino build, 16 mobilních testů/web build, skutečný macOS Bonjour resolver + Keychain/pinned HTTPS/WSS harness, Xcode a install/launch/terminate iOS Simulator. Finální device ID negativní testy a mDNS 1.13.1 lock jsou součástí této revize. Browser native harness otestoval discovery/paired adresu/empty/denied a invalid/valid v1 ID; 393×852 bez vodorovného přetečení a console warnings/errors. Real C++ simulator browser text/Stop ověřeno D102. Dočasné testovací servery byly ukončeny; pouze vlastní MSVC artefakty vzniklé v rootu byly odstraněny.
- Navazující závislosti byly znovu zkontrolované podle §4/§12. B1 stále chybí v doložených podkladech: konkrétní PCB/flash/PSRAM/USB, měření napájení a PCA OE, skutečný service/inspection vstup a kalibrace jedné přihrádky. Bez nich nelze ověřit servisní USB/AP cestu, NVS/TLS/STA na desce a fyzický port E5; E6 a E9/E10 audio na ně navazují. QR kamera potřebuje dosud chybějící konkrétní servisní envelope/cestu, ne samotnou permission. Existující AP i USB varianta plánu se zachovávají. Fyzický iPhone/LAN/Keychain update a mechanická akceptace nejsou nahrazené macOS/simulator testem. Neprovedeno: placený AI test, nový podpis/publikace IPA, cloudové nasazení ani firmware flash.

### D103 – 1. 10. 2026 – E4.4t/E10.3 Bonjour discovery

- Převzetí: firmware i nativní transport mají živé softwarové adaptéry, ale chybí předepsaná `_kufr._tcp.local` inzerce a klientské hledání. Doplnit public TXT `deviceId`, `protocol=1`, capabilities a port 443, nejvýše 5 s hledání s ruční IP fallbackem. mDNS není identita; SPKI pin zůstává jedinou lokální autoritou. ESP-IDF už mDNS neobsahuje, proto konkrétní plánovaná funkce používá oficiální komponentu `espressif/mdns` přes component manifest/lock, původně ověřenou na 1.11.3 a finálně připnutou na 1.13.1. Nové knihovny pro telefon nejsou potřeba.
- Rozsah: vlastní síťová úloha inzeruje až po úspěšném provisionovaném TLS serveru; IP epoch detekuje i krátký reconnect, žádné mDNS SDK volání v safety Tick ani cloudový lifecycle. NWBrowser + bounded NetService resolvery vracejí public hints, nikdy pin/token; nejvýše 16 a 5 s, cancel při pozadí. UI discovery/ruční IP fallback i pro paired zařízení; adresní hint v RAM, trvalý zápis pouze po připnutém autentizovaném WSS se stejným pin/tokenem. Žádný automatický connect/takeover.
- Místní ověření: MSVC /W4 /WX /utf-8 tři lifecycle testy (skutečné WifiStation, StationNetwork, mDNS adapter se SDK stubs) prošly. `pio run -e esp32-s3-idf -s` a Arduino serial profile prošly. První IDF build odhalil pořadí FreeRTOS hlaviček/mutable TXT SDK argument; opraveno. Actual C++ output verifier 49 zpráv prošel po sjednocení capabilities; mobilní testy 16/16, TypeScript/Vite a cap sync ios prošly. Browser synthetic native harness: nalezená adresa vyplní veřejné údaje bez pinu, paired změna IP nechá odpojeno, empty/denied vysvětlení a ruční fallback; konzole čistá. Nativní Swift/mDNS/Xcode CI je doloženo níže. Risk review: opravena recoverable waiting do deadline a normalizace service type s finální tečkou. B1/živý ESP32 multicast/iPhone LAN oprávnění zůstávají samostatná akceptace.
- [iOS CI 36796322516](https://github.com/Blato58/DildoVander/actions/runs/36796322516) pro `043fd729087ca0c47a3fb2fe99468572183625ac`: Swift warnings-as-errors harness skutečně našel/resolveoval místní syntetický Bonjour inzerát, validoval TXT/host/port a deadline/cancel; Keychain/pinned HTTPS/WSS regrese prošly. Xcode build a launch/terminate v iOS Simulator prošly. Signed/publish joby nebyly spuštěné. Finální firmware/iOS CI a sloučení jsou doložené níže. README byly navíc srovnány s D99 runtime (server/owner/gateway/reply již složené, inspection/GPIO/audio stále chybí).
- Finální kontrola kontraktu našla širší native device ID gramatiku než shared v1 (`^[a-z][a-z0-9-]{0,63}$`). Pairing nyní odmítá uppercase/podtržítko/číselný prefix před spotřebováním tajemství; stejný limit má nové discovery inzerování a HTML pole. Cílené host/native negativní testy doplněny; starý NVS formát se tím nemění, servisní generátor musí používat v1 ID.
- Finální dependency kontrola podle [oficiálního changelogu](https://github.com/espressif/esp-protocols/blob/mdns-v1.13.1/components/mdns/CHANGELOG.md) upřesnila nový pin na 1.13.1 (PSRAM/static task a browse memory/parser opravy). První inkrementální `pio run` po změně manifestu ponechal starý lock/komponentu; nebyl důkazem upgradu. Následující `pio run -e esp32-s3-idf -t clean -s` a skutečný rebuild vygenerovaly lock s 1.13.1 a čistý build úspěšně dokončil. Generated `managed_components/` není zdroj a necommituje se.

### D102 – 1. 10. 2026 – E10.2 místní textový klient

- Převzetí: již složený E4 runtime nabízí snapshot-bound text/cancel a korelovanou repliku. Doplnit klienta do společného protokolového řadiče a UI, bez telefonního cloud relay. Ruční lease/capability a snapshot jsou podmínky vstupu; Stop, cancel, změna revision/generation/bootu a odpojení zneplatňují čekající obsah.
- Rozsah: `LocalConversationClient.ts`, společný řadič/snapshot, stabilní formulář mimo heartbeat render a bezpečný textContent reply, cílené client/native regresní testy. Přijetí podle request ID, výsledek podle boot/session/clientTurnId/generation/eventSequence; cancel invaliduje před odesláním. ACK 3 s / reply 45 s, žádná odložená fronta ani automatické opakování. Mimo ruční lease/capability/ready snapshot nelze odeslat. Obsah jen v RAM stránky, s jasným cloud/paměť upozorněním pro nativní režim.
- Ověření: mobilní testy 16/16 a TypeScript/Vite build prošly. Test fixture pro opětovné acquire původně poslala starší eventSequence 2 po watermarku 4; opravena na 5, nový klient nevrací stav zpět. Přísný 8192B textový limit a 1000 Unicode znaků dle §5 včetně emoji/lone surrogate. Testy navíc kontrolují cancel před ACK, pozdní výsledek, boot/revision/lease změnu, chybějící ACK/reply a duplicate ACK bez prodlužování timeoutu. C++ simulator bridge sestaven z živých zdrojů MSVC /W4 /WX /utf-8. Browser přes skutečný loopback TLS/WSS most a C++ model: observer blokuje text, manual text vrátí označenou pevnou repliku, během tahu blokovaný výběr, prázdné pole po odeslání; Stop odstraní draft i reply a blokuje text. Konzole bez warnings/errors.
- Risk review našel chybějící cloudové upozornění a Cancel dostupné mezi result a clearing snapshotem; obojí opraveno, targeted result-before-snapshot assertion prošla. Fyzický iPhone/ESP32, placený backendový tah, audio, deployment ani IPA publikace se neprováděly. Doručení: commity `97009863bb9250d15ee6143394046148da27eade` a `a6bf8c1f905d019f1709b97a0dd745d0237759f7` pushnuty projektovým skriptem; [PR #66](https://github.com/Blato58/DildoVander/pull/66) po zeleném [web/iOS CI 36794263580](https://github.com/Blato58/DildoVander/actions/runs/36794263580) sloučen jako `ed7ea0212a9f345f78da32caf51abf6e1a57dc9c`, ověřen scoped fetch/fast-forward. Další software krok je Bonjour.

### D101 – 1. 10. 2026 – E10.1 nativní místní transport

- Převzetí: E4 má softwarový TLS/WSS/pairing runtime D99 a E8 textový backend D75; chybějící B1 blokuje jejich fyzickou akceptaci, nikoli přípravu Swift transportu podle §12. Implementovat ruční připojení podle device ID, adresy a SPKI z vnitřního QR, nativní párování a Keychain, WSS a stávající ruční protokol. Bez audio, podpisu/publikace nové IPA a bez nahrání firmware.
- Rozsah: čtyři Swift soubory v `mobilni-aplikace/ios/App/App/`, registrace Capacitor pluginu/storyboard, LAN usage description, nativní socket/řadič a párovací UI. Tajemství zadává pouze nativní secure dialog; token je pouze v nesynchronizovaném Keychain. Oddělený URLSession připíná DER SPKI bez globálních ATS výjimek. Strict browser verifier sdílí schéma z `contracts/`. Odpojení/pozadí zneplatní socket a návrat vyžaduje explicitní připojení; automatický retry za běhu má 0,5–8 s backoff s jitterem a nikdy nepřebírá ruční lease.
- Místní ověření 1. 10.: Node 24.21.0, reprodukovatelné npm ci mobilu/kontraktů (0 vulnerabilities), mobilní testy nejprve 9/9, po backoff testu 10/10; TypeScript/Vite build, cap sync ios, kontrakty 22 valid / 39 invalid + OpenAPI/media prošly. První nový integrační test použil již aktivní lease ze sdílené fixture; po změně výchozího welcome na observer prošel. Bash syntax, YAML/plist parser prošly. Browser: web fake select/Stop a syntetický nativní bridge pairing/observer/recovery, viewport 393×852 bez vodorovného přetečení a bez console warnings/errors. Testovací HTML není součástí release bundlu; neověřuje UIKit pairing dialog ani skutečný LAN permission.
- [CI 36792382045](https://github.com/Blato58/DildoVander/actions/runs/36792382045) pro `20a5abda0001dce405d222d5d1c6917c55c2a82f` prošlo: web, Swift test skutečného loopback HTTPS/WSS/Keychain a DER SPKI EC P-256/P-384/RSA, negativní pin/token/redirect/binary/size scénáře, unsigned Xcode build, install/launch/terminate v iOS Simulator. [Firmware CI 36792382095](https://github.com/Blato58/DildoVander/actions/runs/36792382095) prošlo host testy i ESP-IDF/Arduino. Read-only native risk review bez blokujícího nálezu. UIKit plugin lifecycle a native alert nejsou ověřeny CLI harness testem.
- Doručení: commity `20a5abda0001dce405d222d5d1c6917c55c2a82f` a `09b339c1b6240d5f26ac94bfd2ea521d8ef44154`, následně `76a162601ec3fd6afdddd2dd824532c3892707aa` ověřeně pushnuty přes projektový skript; [PR #65](https://github.com/Blato58/DildoVander/pull/65) po zeleném CI sloučen jako `5f54104b783c8cf2fc7e1885276322d535681f7b`, ověřen scoped fetch na main; [poslední iOS CI 36793112827](https://github.com/Blato58/DildoVander/actions/runs/36793112827) prošlo. E10 jako celek není hotové; B1, živý iPhone/TLS/Keychain update a mechanika zůstávají otevřené. Dosavadní výsledky D70/D99 mají původní datum a rozsah. Další nezávislý krok: klient `conversation.text`/cancel/result a následně Bonjour.

### D100 – 30. 9. 2026 – audit návaznosti a předání na B1

- Rozsah pokračování: D90–D99 dokončují deset ověřitelných firmware slice od RNG/TLS identity přes přísný parser a prioritní inbox po WSS router/output/reply, jednorázové HTTPS párování a runtime s jediným vlastníkem LAN. Každý vlastní slice má commit/push, kontrolovaný PR a ověřené sloučení po host/firmware CI. Současná práce neuzavírá E4 hardware ani E5/E9/E10.
- Blokace nejbližšího kroku: B1 dosud nemá revizi PCB a potvrzené flash/PSRAM/USB, schéma/měření napájení a PCA OE, fyzicky měřitelný servisní/inspection vstup a kalibraci jedné přihrádky. Bez nich nelze servisně provisionovat, otestovat skutečný TLS/NVS/RNG/STA a bezpečně připojit fyzický port. Runtime nesmí nahradit tuto mezeru fake Recover/inspection. Majitel 29. 9. potvrdil, že údaje o revizi/paměti/USB nemá.
- Návaznost podle §12: E5 vyžaduje E0/E1/E4 a měření; E9 vyžaduje E4/E5/E8; native E10 potřebuje E4/E8 a vlastní fyzický iPhone test. V rámci převzaté nejbližší firmware cesty není další krok se splněnými závislostmi; zbývající testování na hostu neuzavře chybějící HW kritéria. Přímý servisní opening/PTT a audio/presentation zůstávají po těchto vstupech.
- Dokumentace: rozšíření D97 result a D98 pairing je přesunuto z konce referencí do příslušných částí protokolu; D99 runtime do architektury. Handoff a README odpovídají kódu, B2/B3 nepředstírají novou blokaci. 36 místních Markdown odkazů a diff kontrola prošly; u dokumentačního předání se nespouštěly ručně další aplikační buildy. Cizí změny nebyly přidány.

- Doručení D99: commit `f7afaf427605058762d92b98811232a78c5a577b` ověřeně pushnut; [CI 36776068730](https://github.com/Blato58/DildoVander/actions/runs/36776068730) prošlo všemi hostitelskými testy včetně sdílené STA a JSON/schema interoperability, ESP-IDF i Arduino profilem. [PR #63](https://github.com/Blato58/DildoVander/pull/63) sloučen jako `d88dc78ff3206de162af0d692ae7a7ad9d31a824`, ověřen fetch na `origin/main`.

### D99 – 30. 9. 2026 – E4.4s provisionovaný runtime a nezávislá LAN

- Převzetí: jediný EspIdfStationNetwork vlastní Wi-Fi/netif a retry task, Backend sdílí immutable settings a IP stav; cloudový token/čas/WAN síť neukončí. app_main skládá dlouho žijící TLS/pairing/server/inbox/output/owner, server start až po IP a zdravém provisionování. UUID boot/sezení před RF, lease přes STA RNG, aktuální katalog. Owner Tick/PumpReply jsou jediným řídicím průchodem. Bez fyzického portu zůstává kalibrace 0 a inspection gate false, žádné automatické Recover, GPIO ani fake autonomní ovládání.
- Ověření: station SDK-stub test s MSVC /W4 /WX prošel chybějící nastavení, task/Wi-Fi failure, jedno init, nepublikované IP před init, retry/backoff/reset po reconnect a ztrátu IP. Read-only runtime review bez nutné opravy. Finální ESP-IDF a Arduino profily prošly. YAML parsing, 35 místních Markdown odkazů a diff kontrola prošly.
- Neprovedeno: skutečné STA/netif/IP změny, NVS/TLS/WSS/pairing/text na ESP32 a iPhone, USB/service/inspection brána, kalibrace, pohony, PTT/audio; B1 a závislé E5/E9/E10 zůstávají otevřené. Nejbližší HW krok není nahrazen fake portem.

- Doručení D98: commit `8bbf4d97ded77d7b1afd582d9f44dde57c838daa` pushnut; [CI 36774326378](https://github.com/Blato58/DildoVander/actions/runs/36774326378) prošlo host testy a obě firmware sestavení. [PR #62](https://github.com/Blato58/DildoVander/pull/62) sloučen jako `79785c53bc5357a4bb5b934876bfd2ff4f8465e6`, ověřen fetch na `origin/main`.

### D98 – 30. 9. 2026 – E4.4r jednorázové HTTPS párování

- Převzetí: provisionovaný server přijme pairing-ready záznam, registruje POST `/v1/pair` vedle WSS. Přes připnuté TLS přijímá jen application/json, přesný URI, bez Transfer-Encoding a nejvýše 192B tělo; přesný objekt protocolVersion 1 / pairingSecret lowercase hex 16–64B, bez duplicit/unknown polí. Model ověří tajemství, RNG s aktivní STA vydá 256bitový token, NVS uchová pouze hash a zneplatní secret. Response no-store, všechny citlivé lokální buffery se vynulují. Nikdy neřídí pohony. Ztracená odpověď po trvalém spárování potřebuje fyzický servisní reset, nikoli opakované vydání tokenu.
- Ověření: MSVC `/W4 /WX` skutečného serverového adaptéru se SDK stuby prošlo fragmentované HTTP čtení, secret mismatch, malformed/duplicate/unknown JSON, velikost, Content-Type, query/Transfer-Encoding, chybu čtení/RNG/NVS a ztracenou odpověď, jeden úspěch a odmítnutí replay. Dosavadní WSS/auth/close/reused-FD testy prošly, oba PlatformIO profily prošly. Read-only review bez kritického nálezu; doplněna rezervace citlivých string bufferů před jejich plněním, kontrola response headers a test chyby po částečném HTTP read. Diff kontrola prošla.
- Neprovedeno: živé TLS/NVS/RNG, fyzické provisionování, service AP, native iPhone pairing; B1 brána zůstává beze změny.

- Doručení D97: commit `c36b095c45482feabf2cbeff7131f3403fd71a93` pushnut; [CI 36773306562](https://github.com/Blato58/DildoVander/actions/runs/36773306562) prošlo host testy a obě firmware sestavení. [PR #61](https://github.com/Blato58/DildoVander/pull/61) sloučen jako `b39685c068d8efa7103b94fd329bf685a61a344e`, ověřen fetch na `origin/main`.

### D97 – 30. 9. 2026 – E4.4q skutečná backendová replika ve WSS

- Převzetí: samostatný reply port odděluje síťový inbox od řídicího konzumenta. PumpReply na řídicím vlákně kontroluje token, snapshot, místní sezení/tah, generation, lease a urgentní frontu; uvolní každý převzatý token a vynuluje repliku. Výstup zůstává mimo urgentní guard a neřídí pohony; text zařazený před cancel nese starou generation. Backend false / simulátor true a limit 500 znaků / 8192 raw JSON bajtů jsou zdůvodněné rozšíření v1 v plánu a README. Společný JsonWriter zachovává dosavadní escaping.
- Ověření: MSVC `/W4 /WX` owner test prošel úspěšné/duplicitní dokončení, starý snapshot/token, odpojení, Stop před/uvnitř send a chybu transportu. 49 skutečných C++ zpráv prošlo sdíleným Node v1 schema verifierem, včetně UTF-8, escaping a 500 znaků. Regrese ConversationTextCodec, oba PlatformIO profily, contracts testy (22/39 + OpenAPI/media), mobilní testy (4) a TypeScript/Vite build prošly. Read-only review bez kritického nálezu; dokumentovaná race odeslaného textu proti Stop nezpůsobí dokončení ani pohyb, následný snapshot/generation je autoritativní. Diff kontrola prošla.
- Neprovedeno: app_main aktivace, audio/I2S/TTS, pohony, živý ESP32 WSS a B1.

- Doručení D96: commit `bce9dacf45d952c86e3eb0b265483a8678bcb3f2` pushnut; [CI 36772263834](https://github.com/Blato58/DildoVander/actions/runs/36772263834) prošlo host testy a obě firmware sestavení. [PR #60](https://github.com/Blato58/DildoVander/pull/60) sloučen jako `8bdc4e68cea0c71c6af1d97d921fe8fd76a624d1`, ověřen fetch na `origin/main`.

### D96 – 30. 9. 2026 – E4.4p pravdivý WSS snapshot a ACK

- Převzetí: serializer má číst skutečný control/journal/safety/selection/conversation stav, nikoli kopii simulátoru. Úzké read-only gettery doplňují kalibraci, pending command, terminal result, phase a active turn. Autonomní aktivita potřebuje samostatné phase presenting; proto před implementací doplněn enum v1 a §5 plánu, bez změny verze či dalších polí. Strict klient přebírá nové schéma; nynější mobilní simulátor má string phase a bezpečně mapuje ne-ready na running.
- Ověření: MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` a společný Node 24.21.0 verifier ověřily 47 skutečných firmware zpráv proti v1 schématu. Regrese DeviceControl a LocalControlOwner prošly. Review vedl k opravě fáze po Accept před Begin a revize prvního idle Stop; opakovaný Stop zůstává idempotentní. Journal failure a přímý safety fault mají pravdivý snapshot, mikrofon unavailable a kalibrace 0 nikdy ready. Oba PlatformIO profily prošly, contracts testy (22 valid / 39 invalid + OpenAPI/media) a mobilní testy (4) i TypeScript/Vite build prošly. CI ověřuje identické C++ JSON výstupy přes sdílené schéma; contracts změny spouštějí firmware workflow. YAML parsing a diff kontrola prošly.
- Neprovedeno: aktivace serveru v app_main, skutečná replika a HW/TLS/B1. Žádný fyzický stav se nevydává za měření.

### D95 – 30. 9. 2026 – E4.4o řídicí konzument místních zpráv

- Převzetí: typovanou frontu a místní lease/text bránu spojuje `LocalControlOwner`. Zpracovává pouze řídicí úloha, žádný callback. Output port zůstává typovaný pro následný serializer; reset vyžaduje platný lease a explicitní inspection gate, ID lease dodává oddělený port. Poll má omezený počet zpráv, hello deadline a kontrolu lease. Guard inboxu atomicky řadí přijímání Stop/close a řízené fyzické přechody; `owner.Tick()` je jediná cesta runtime Tick. Výstup zůstává mimo guard, Begin po ACK používá čerstvý monotónní clock a propaguje i Stop při odmítnutí. Fyzický/step port v guardu musí být krátký a nesmí reenterovat inbox/transport.
- Ověření: `%TEMP%/kufr-control-owner/test.cmd` s MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` prošel hello/observer/lease/text/cancel/failure/reset a Stop mezi ACK/Begin. `%TEMP%/kufr-control-inbox/test.cmd` prošel souběžným pořadím guard/Stop. Nezávislý review zachytil TOCTOU a starý čas po ACK; opraveno a testovaný ACK posunutý za expiraci nevydal SearchStep, vydal Stopped a zrušil lease. Prošly obě `pio run -d kufr/firmware -e esp32-s3-idf -s` / `-e esp32-s3-arduino-serial -s`, YAML parsing a diff kontrola. Bez aktivace v `app_main`/GPIO.
- Doručení D95: commit `c7275e6834b1a3b74545060841bdb4cd38226547` pushnut; [CI 36767834236](https://github.com/Blato58/DildoVander/actions/runs/36767834236) prošlo host testy i obě sestavení. [PR #59](https://github.com/Blato58/DildoVander/pull/59) sloučen jako `5ff164fc6d771523c551090eb6c0b74e3edda8de`, ověřen fetch na `origin/main`.
- Neprovedeno: serializer, výstup replika, provisionovaný runtime, živé WSS a HW/B1.

### D94 – 30. 9. 2026 – E4.4n serverový adaptér autentizovaného WSS

- Převzetí: po D93 následuje ESP-IDF HTTPS/WSS adaptér `/v1/control` bez GPIO a bez spuštění v `app_main`. TLS identita a párovací credential musí být provisionované; token se kontroluje před 101. Callback pouze parsuje/enqueue, odpojení přebírá řídicí vlastník. Výstup a close běží omezeným workerem HTTPD s generací socketu. Wi-Fi/netif lifecycle zůstává u síťového vlastníka.
- Ověření: `%TEMP%/kufr-wss-server/test.cmd` s MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` prošel callbacky Start/pre/post/read/close, fixture hello/Stop, chybné rámce, plné fronty, selhání work/send a reused FD. `pio run -d kufr/firmware -e esp32-s3-idf -s` prošel skutečným SDK; první překlad odhalil neúplnou vendor inicializaci, kolem makra je úzce omezené GCC potlačení této jediné kontroly. `pio run -d kufr/firmware -e esp32-s3-arduino-serial -s` také prošel. Lokální ignorovaný sdkconfig byl zálohován do TEMP a generátor ho obnovil z defaults; efektivně zapnuté HTTPS/WS/pre/post byly ověřeny. YAML parser a diff kontrola čisté. Nezávislý review nezjistil materiální auth/lifecycle vadu; pomalý autentizovaný příjem a Stop latency vyžadují skutečné měření.
- Doručení D94: commit `5e3652672d3afa56103a10a1c118c177bb230ada` pushnut; [CI 36764532650](https://github.com/Blato58/DildoVander/actions/runs/36764532650) úspěšné pro host testy i obě sestavení. [PR #58](https://github.com/Blato58/DildoVander/pull/58) sloučen jako `1774b36eafab13a2dce6363130307b48897fc7b9`, ověřen fetch na `origin/main`.
- Neprovedeno: skutečný TLS/WSS provoz, runtime composition, servisní pairing/provisioning a HW/B1.

### D93 – 30. 9. 2026 – E4.4m fronta s prioritou Stop a odpojení

- Převzetí: síťový callback nesmí měnit řízení ani ztratit odpojení při zaplnění fronty. `LocalControlInbox` drží jednu device/boot/socket generation, čtyři běžné zprávy a oddělené sloty připojení/Stop/odpojení. Stop zneplatní běžnou frontu včetně resetu; nepřijímá další povely do převzetí Stop. Odpojení nelze zahltit a nový socket čeká na převzetí starého close. Opožděné či cizí handly a starý boot se odmítají. Rozsah: model, test/CI, CMake, README a stav.
- Ověření: MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` test z `%TEMP%/kufr-control-inbox/test.cmd` prošel plnou frontou, Stop/close prioritou, reused FD a starým bootem. Postupně prošly `pio run -d kufr/firmware -e esp32-s3-idf -s` a `pio run -d kufr/firmware -e esp32-s3-arduino-serial -s`; YAML parser a `git diff --check` čisté. Při odebrání/zahození se uložený text přepisuje. Jedno volání použilo neexistující název Arduino profilu; opravený příkaz prošel.
- Doručení D93: commit `3456f89f0f0573865e9e13e4e544c21918dc9a77` pushnut; [CI 36761901721](https://github.com/Blato58/DildoVander/actions/runs/36761901721) úspěšné pro host testy a obě sestavení. [PR #57](https://github.com/Blato58/DildoVander/pull/57) sloučen jako `5797fbb5220e27d4bd3a2ea9e09b672ae1844e3c`, ověřeno fetch na `origin/main`.
- Neprovedeno: skutečný WSS server a konzument v IDF smyčce, fyzická deska/B1. Následuje serverový adaptér s autentizací před upgradem a bezpečným předáním odpojení.
### D92 – 30. 9. 2026 – E4.4l přísný místní parser příkazů

- Převzetí: WSS nesmí předávat libovolný JSON vlastníku řízení. `LocalControlCodec` podporuje pouze klientské v1 hello/state/lease/text/cancel/Stop/reset/selection/reload, kontroluje přesná pole, duplicitní i escaped klíče, UUID, integer lexémy/rozsah, UTF-8 a limity 2048/8192 B. `JsonCursor` je beze změny původního chování vyčleněný z `TurnOperationCodec` jako sdílený technický parser (doplněno čtení true). Výstup je jen typovaná zpráva; autentizace a aktuální lease/snapshot patří následné bráně. Rozsah: parser/refaktor, test/CI, CMake, README a stav.
- Ověření: MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` prošel vlastními mezemi, všemi 11 klientskými valid fixtures a 8 invalid fixtures i regresním `turn_operation_codec_test`. Postupně prošly `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s`; YAML parser a `git diff --check` čisté. První očekávání testu omylem porovnávalo velké Č s escaped malým č; opraveno a opakování prošlo.
- Doručení D92: commit `f20efe0740977f34fefe62981b7d8332dc4c0aba` ověřeně pushnut; [CI 36759642257](https://github.com/Blato58/DildoVander/actions/runs/36759642257) prošlo hostitelskými testy i firmware buildy. [PR #56](https://github.com/Blato58/DildoVander/pull/56) sloučen jako 315931d9fdc2ddb772ac181f5196ff1ee90a6e97, ověřen na `origin/main`.
- Neprovedeno: živý WSS transport a runtime, skutečné TLS/NVS na desce, B1. Další krok: omezená fronta s prioritou Stop/odpojení, potom uzavřený server a řídicí konzument.
### D91 – 30. 9. 2026 – E4.4k oddělená místní TLS identita

- Převzetí: místní server potřebuje unikátní certifikát/privátní klíč mimo image. `LocalTlsIdentity` drží jednu omezenou dvojici PEM a vyžaduje fyzickou servisní bránu pro atomický zápis; `EspIdfLocalTlsStorage` používá oddělený `wand_tls/server`. mbedTLS/PSA validátor ověřuje parse a shodu klíče s certifikátem a odvozuje SHA-256 DER SPKI pro QR. Chyba úložiště či kryptografické kontroly brání použití identity. RAM klíče se při uvolnění maže. Limity 2048/1024 bajtů (certifikát/EC klíč) udržují záznam pod jednou NVS datovou stránkou. Nejasný zápis vyžaduje nové načtení a ověření uloženého SPKI před vydáním QR. Rozsah: model/adaptéry, test/CI, CMake/Arduino filtr, README a stav.
- Ověření 30. 9.: MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `local_tls_identity_test` (servisní brána, restart, truncation, nadměrná délka, vnitřní NUL, neshoda, nulový pin, chyba čtení/zápisu a nový load po nejasném zápisu). Po revizi limitů postupně znovu prošly `pio run -e esp32-s3-idf -s` i `pio run -e esp32-s3-arduino-serial -s`. Host test navíc ověřil největší 3080bajtový záznam a dvě rotace. YAML parser a `git diff --check` prošly. Hostitelský validátor je fake; skutečná kryptografie/NVS musí projít na desce po B1.
- Doručení D91: commit `8b51cabaf52e509faf7af9cf3c030b687cf526ed` ověřeně pushnut; [CI 36757780334](https://github.com/Blato58/DildoVander/actions/runs/36757780334) prošlo hostitelskými testy i firmware buildy. [PR #55](https://github.com/Blato58/DildoVander/pull/55) sloučen jako `769903023806a2819c12cba8294b02f865e5df8f`, ověřen na `origin/main`.
- Neprovedeno: servisní USB zápis, fyzické zabezpečení flash, TLS handshake a WSS parser/runtime. Prototyp NVS má hranice již přijaté v plánu §9; šifrování a eFuse se tím nezapínají. Další krok: uzavřený TLS/WSS server a fronta autorizovaných událostí do řídicí úlohy.
### D90 – 30. 9. 2026 – E4.4j hardwarově podmíněný párovací RNG

- Převzetí: doplnit produkční `PairingRandomPort` pro právě 32 bajtů lokálního tokenu. `EspIdfPairingRandom` dovolí `esp_fill_random` pouze při připojené Wi-Fi STA před i po generování; při výpadku výstup vymaže. Nepřepíná ADC zdroj během RF/audio provozu. Párovací AP bude potřebovat vlastní doloženou bránu aktivní entropie. Rozsah: adaptér, hostitelský test se stuby IDF, CMake/Arduino filtr, CI, README a stav.
- Ověření 30. 9.: MSVC `/std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `espidf_pairing_random_test` se stuby veřejných IDF API (chybějící spojení, platná délka, výpadek při generování a vymazání). Postupně prošly `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s`. `git diff --check` čistý. Test stubů dokládá bránu adaptéru, nikoli kvalitu hardwarového RNG. Žádné nahrání do desky ani živé párování.
- Doručení D90: commit `b3795f82694e34ad375a8c092c88250795875a95` ověřeně pushnut. [CI 36755796213](https://github.com/Blato58/DildoVander/actions/runs/36755796213) prošlo hostitelskými testy i oběma firmware buildy; [PR #54](https://github.com/Blato58/DildoVander/pull/54) sloučen jako `e1daface837f80f30c48869bbaa6b83030beb4ed`, ověřen na `origin/main`.

### D89 – 30. 9. 2026 – E4.4i jediný místní control socket

- Převzetí: D88 ověřuje token, ale bez WSS transportu zatím není určeno, jak odmítnout druhý telefon a uvolnit neúplný handshake. Čistý `LocalSocketAdmission` drží nanejvýš jednu rezervaci/aktivní FD, autentizuje před rezervací, potvrzuje handshake do 10 s a při zavření rozlišuje rezervovaný a aktivní socket. Generace rezervace brání opožděnému callbacku uvolnit znovu použité číslo FD; vypršení vrátí starý handle, který musí transport zavřít před dalším přijetím. Žádný callback nesmí přímo měnit řízení; budoucí server předá ztrátu aktivního socketu řídicí úloze. Rozsah: model, hostitelský test/CI, firmware README/CMake a tento stav.
- Ověření 30. 9.: MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `local_socket_admission_test` (jediný socket, expirace, opožděný callback při znovu použitém FD, rozlišení zavření). Z `kufr/firmware` postupně prošly `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s`. První Arduino build po generačním handle odmítl složené přiřazení; explicitní konstruktor problém opravil a oba profily poté prošly. [CI 36730263179](https://github.com/Blato58/DildoVander/actions/runs/36730263179) potvrdilo `host-tests` a `firmware-build`.
- Doručení: implementační commit `ecebb09e5de0340356d7af342053e8f9020e4fcf`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-local-wss-admission`. [PR #52](https://github.com/Blato58/DildoVander/pull/52) sloučený jako `0de5cdcb21b55f678e61488e7f9de47f0fdd8a8b` na `main`.
- Neprovedeno: skutečný `esp_https_server`, pre-handshake bearer callback, provisionovaný TLS certifikát/klíč, servisní AP a fyzický běh B1. IDF 6.1 má WebSocket pre-handshake podporu vypnutou, musí se zapnout až s uzavřeným serverovým adaptérem; samotný handler autorizaci před upgradem nenahradí.

### D88 – 30. 9. 2026 – E4.4h místní bearer hlavička

- Převzetí: telefonní WSS handshake potřebuje převést textový `Authorization` na 256bitový token bez volného formátu. Plán nyní určuje přesně 64 malých hexadecimálních znaků. Čistý `LocalBearerAuthenticator` přijímá pouze úplnou `Bearer ` hlavičku, dekóduje token do krátkodobého bufferu, ověří ho přes D86 a buffer vymaže. Simulátor přijímá stejný tvar tokenu. Rozsah: plán, parser, hostitelský test/CI, firmware README/CMake, simulátor a tento stav. Zatím se nevolá z firmwarového socketu.
- Ověření 30. 9.: MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `local_bearer_authenticator_test` (správný token, změněný token, neúplná/dlouhá hlavička, prefix a nepovolený hex). Z `kufr/firmware` postupně prošly `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s`. Sestavený C++ bridge a `npm test` v `kufr/simulator` prošly včetně WSS TLS/auth/media scénářů. Původní test konverzace při skoku fake času 500→831 ms spouštěl 250ms watchdog; vložený průchod řídicí smyčkou v 700 ms odpovídá provoznímu 10ms ticku. YAML firmware workflow se načetl; [CI 36725668382](https://github.com/Blato58/DildoVander/actions/runs/36725668382) potvrdilo `host-tests` a `firmware-build`.
- Doručení: implementační commit `b5ba251e791b7c539c46c99cf310808a91446be1`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-local-bearer-auth` včetně aktualizovaného `main` na `26b5e6bf65e74a3b6dbd34ac9ada9da6c765f2a7`. [PR #50](https://github.com/Blato58/DildoVander/pull/50) sloučený jako `027d330963a095a3dc19d4cfce9ecebcdf1ef24d` na `main`.
- Neprovedeno: skutečný TLS/WSS handshake, limit jednoho klienta, service AP, fyzická brána a běh na desce.

### D87 – 30. 9. 2026 – E4.4g ESP-IDF místní párovací adaptéry

- Převzetí: D86 měl pouze fake SHA-256 a paměťové úložiště. ESP-IDF profil nyní sestavuje `EspIdfSha256` přes PSA Crypto a `EspIdfLocalPairingStorage` s vlastním NVS namespace/key `wand_pairing/local`, odděleným od backendového `wand_identity/device`. Neúplný/nečitelný záznam a nedostupná NVS odmítají čtení; zápis přijímá jen přesnou délku záznamu a vyžaduje commit. Rozsah: oba adaptéry, CMake, vyloučení IDF souborů z Arduino profilu, firmware README a tento stav.
- Ověření 30. 9.: z `kufr/firmware` prošly postupně `pio run -e esp32-s3-idf -s` i `pio run -e esp32-s3-arduino-serial -s`. První souběžný pokus o oba profily narazil na společný `.pio/build` a nepočítá se jako ověření. D86 prošel [CI 36721137045](https://github.com/Blato58/DildoVander/actions/runs/36721137045) v úlohách `host-tests` a `firmware-build` a [PR #48](https://github.com/Blato58/DildoVander/pull/48) je sloučený jako `2f381b3d36b3b87b09697efc04119b71bf6912c9` na `main`.
- Doručení: implementační commit `c1ba9bc2a413c456848328ad0e6858fe94917c3d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push větve `codex/kufr-local-pairing-adapters` včetně aktualizovaného `main` na `48bcd2bca80bf76c017bc231ab9f81bf7ec863f8`. [CI 36723167583](https://github.com/Blato58/DildoVander/actions/runs/36723167583) prošlo v úlohách `host-tests` a `firmware-build`; [PR #49](https://github.com/Blato58/DildoVander/pull/49) sloučený jako `14bf0d92599fd2e921a2668f6eb93a3a7a09f624` na `main`.
- Neprovedeno: běh PSA/NVS na desce, fyzický servisní generátor tajemství, hardwarově podmíněný RNG, TLS identita a WSS spojení. Adaptéry samy zatím nepřijímají žádné síťové požadavky.

### D86 – 30. 9. 2026 – E4.4f místní párovací záznam

- Převzetí: skutečný místní WSS bearer nemá vlastní trvalý stav ani jednorázové párování. Čistý `LocalPairingCredential` zavádí samostatný verzovaný záznam: po servisní bráně drží pouze otisk nejméně 16bajtového párovacího tajemství, po jeho ověření vydá 256bitový token z portu hardwarové entropie, uloží pouze otisk tokenu a tajemství odstraní. Entropii vstupního tajemství musí zajistit budoucí servisní generátor. Další párování vyžaduje novou servisní bránu; poškozený záznam a selhání zápisu odmítnou přístup. Nezasahuje do backendového `DeviceIdentity`. Rozsah: čistý model, hostitelský test s fake porty, firmware CI/README a tento stav.
- Ověření 30. 9.: z `kufr/firmware` MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `local_pairing_credential_test` (servisní brána, jednorázové párování, restart, selhání RNG/zápisu a poškozený záznam včetně délky, hlavičky, stavu a otisků); `pio run -e esp32-s3-idf -s` prošel. Z kořene repozitáře prošlo načtení YAML workflow přes `yaml.safe_load`; [CI 36721137045](https://github.com/Blato58/DildoVander/actions/runs/36721137045) potvrdilo `host-tests` a `firmware-build`.
- Doručení: implementační commit `257b4ca749dda4aa25d571315ca615820746844e`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-local-token-record`. [PR #48](https://github.com/Blato58/DildoVander/pull/48) sloučený jako `2f381b3d36b3b87b09697efc04119b71bf6912c9` na `main`.
- Neprovedeno: produkční SHA-256, RNG a NVS adaptéry, fyzická servisní brána/B1, TLS klíč/certifikát a autentizovaný WSS handshake. Další softwarový krok je adaptér kryptografie a odděleného NVS záznamu, poté uzavřený TLS/WSS vstup; model sám neopravňuje žádný socket.

### D85 – 30. 9. 2026 – E4.4e sdílené boot ID

- Převzetí: IDF `DeviceControl` používal prázdné boot ID, zatímco síťová úloha ho vytvářela nezávisle až po Wi-Fi. `app_main` nyní před startem rádia dočasně zapne interní zdroj entropie ESP-IDF, vygeneruje jedno UUID v4, zdroj vypne a stejné ID předá řízení i backendové úloze. Backend už boot ID nemění po reconnectu; idempotency key dál vzniká až po připojení Wi-Fi. Rozsah: IDF inicializace, konstruktor sítě, CMake závislost, firmware README a tento stav. Není přidána místní WSS služba ani povolen fyzický pohyb; kalibrace zůstává nulová.
- Ověření 30. 9. z `kufr/firmware`: `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s` prošly. [CI 36717463088](https://github.com/Blato58/DildoVander/actions/runs/36717463088) prošlo v úlohách `host-tests` a `firmware-build`. Hostitelský běh hardwarového RNG ani fyzická deska/B1 nejsou k dispozici.
- Doručení: implementační commit `2db29a2a5d07463efb6020c1d85e9504f723ad48`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-shared-boot-identity`. [PR #47](https://github.com/Blato58/DildoVander/pull/47) byl po kontrole diffu a CI sloučen jako `0d988ce24d5c09f9807072e2093223526b1f6228` na `main`.
- Neprovedeno: místní WSS/TLS identita a token, autentizovaný producent, výstup replik a připojení brány do IDF smyčky. Další krok: bezpečné místní tokenové úložiště a WSS handshake po servisním provisioningu; bez B1 zůstane fyzické provisionování a runtime ověření blokováno.

### D84 – 30. 9. 2026 – E4.4d místní textová brána

- Převzetí: předchozí čisté modely neměly jediné volací místo pro lease, generation, text, frontu a asynchronní chybu. `LocalConversationGateway` na řídicí úloze ověřuje autentizovaný `LocalControlSession`, čerstvý lease a autoritativní snapshot. Duplicitní tah znovu neodesílá; nový tah potvrdí před předáním síti a při okamžitém odmítnutí fronty vrátí pouze tento tah. Síťová úloha předává terminální selhání s místním `conversationId`, `clientTurnId` a verzí snapshotu přes omezenou schránku; podmíněné vyzvednutí neztratí zprávu jiného sezení. Brána při shodě aktivní tah uvolní, ale ID nerecykluje. Změna snapshotu zneplatní i předanou repliku. Síťové zrušení je podmíněné ID rozhovoru a verzí přijatého snapshotu, aby opožděná stará brána nevymazala tah nového vlastníka. `EspIdfBackendNetwork` implementuje port, hlásí odhození již přijatého čekajícího textu při výpadku sítě a během čekajícího cancel nebo prezentace odmítá nový tah/opening. Dokončení prezentace drží přijímací bránu zavřenou až do zařazení zrušení. Rozsah: firmware model, síťové předání, hostitelské testy, README, CI a tento stav.
- Ověření 30. 9. z `kufr/firmware`: MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil rozšířený `local_control_session_test` a `conversation_text_inbox_test`. Testy kryjí odmítnutí fronty a opakování původního ID, duplicitní ACK bez síťového tahu, expirovaný lease, cancel a čekající síťové zrušení, Stop, pozdní výsledek, asynchronní selhání, nevratnost použitého ID, cizí/starou chybovou zprávu a opožděné zrušení po převzetí novým vlastníkem. `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s` prošly. [CI 36715741030](https://github.com/Blato58/DildoVander/actions/runs/36715741030) prošlo v úlohách `host-tests` a `firmware-build`.
- Doručení: implementační commit `7aa9d3fabf0dff3e3019d9e1e64b7c6ba7599b73`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-local-conversation-gateway`. [PR #46](https://github.com/Blato58/DildoVander/pull/46) byl po kontrole diffu a CI sloučen jako `e0647f06a1b9e6c0e07b41b7ef768e5805fb9e65` na `main`.
- Neprovedeno: instanciace brány ve skutečné IDF smyčce, WSS/TLS autentizace a parser, místní opening producent, přehrávání replik, fyzická deska/B1. Další krok: autentizovaný transport a bezpečné předání událostí vlastníkovi řízení; brána není běžící WSS server.

### D83 – 30. 9. 2026 – E4.4c společná generation rozhovoru

- Převzetí: `ConversationSession` a `DeviceControlRuntime` dosud měly oddělené čítače. Přidána neměnící kontrola zrušení, podmíněné zvýšení generation vlastníkem řízení pouze s platným ručním lease a monotónní synchronizace sezení po Stop/ztrátě lease. Rozsah: čisté C++ modely, hostitelský test, firmware CI, README a tento stav. Skutečná WSS událost ani odpovědní port se zatím nezapojuje.
- Ověření 30. 9. z `kufr/firmware`: MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil rozšířený `local_control_session_test` a stávající `conversation_session_test`. Nové scénáře kryjí zrušení, Stop, uvolnění lease, pozdní výsledek a odmítnutí expirovaného lease přesně na hranici 6000 ms před Tick. `pio run -e esp32-s3-idf -s` i `pio run -e esp32-s3-arduino-serial -s` prošly po poslední úpravě. `git diff --check` a [CI 36709036203](https://github.com/Blato58/DildoVander/actions/runs/36709036203) v úlohách `host-tests` a `firmware-build` prošly; deska/B1 neověřena.
- Doručení: implementační commit `9eb9e52928f449aa23d97ad4e4a10924a65d344f`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-conversation-generation`. [PR #45](https://github.com/Blato58/DildoVander/pull/45) byl po kontrole diffu a CI sloučen jako `0b089139d068e0f98a7ed4a82b3b6f80b7267af8` na `main`.
- Neprovedeno: živý autentizovaný vstup, automatické předání lifecycle událostí mezi úlohami, deska/B1 a WSS/TLS. Další krok: jediná řídicí brána pro text/cancel/reply s explicitním vlastníkem lease a síťovým portem.

### D82 – 30. 9. 2026 – E4.4b dvoufázové přijetí textu

- Převzetí: při přímém volání `ConversationSession::SubmitText` se ID spotřebovalo před možným odmítnutím síťové fronty. Čistý model nyní nabízí neměnící `ValidateText`; D82 hostitelsky ověřilo kontrolu před zařazením a potvrzení po něm. D84 toto pořadí uvnitř jediné brány změnilo na kontrolu, potvrzení, zařazení a návrat dosud nezařazeného tahu při okamžitém odmítnutí. Tím se síťová úloha nedostane k tahu dříve než jeho místní evidence. Rozsah D82: model sezení, hostitelský integrační test se skutečnou `ConversationTextInbox`, firmware README a tento stav. Síťová/WSS produkce se v D82 nepřipojovala.
- Ověření 30. 9. z `kufr/firmware`: MSVC `cl /std:c++14 /EHsc /W4 /WX /utf-8` sestavil a spustil `conversation_session_test` a `conversation_turn_correlation_test`; druhý test doložil odmítnutí obsazenou frontou, opakovaný pokus bez ztráty ID, duplicitní retry po vyzvednutí původního tahu bez dalšího zařazení a původní korelaci odpovědi. `pio run -e esp32-s3-idf -s` i `pio run -e esp32-s3-arduino-serial -s` prošly. [CI 36706745630](https://github.com/Blato58/DildoVander/actions/runs/36706745630) prošlo v úlohách `host-tests` i `firmware-build`. Deska/B1 neověřena.
- Doručení: implementační commit `84048e3881879e0424349a322e005fe994fbf7fd`; `./scripts/Push-CurrentBranch.ps1` potvrdil push do `origin/codex/kufr-text-admission`. [PR #44](https://github.com/Blato58/DildoVander/pull/44) byl po kontrole diffu a CI sloučen jako `481979ccf358f2345a4ebd9ac658f4971b31f485` na `main`.
- Neprovedeno: jednotná generace mezi `ConversationSession` a `DeviceControlRuntime`, skutečný místní autentizovaný producent, WSS/TLS, fyzické výstupy. Další krok: synchronizace životního cyklu v jediné řídicí úloze; až potom napojení textu a odpovědi.

### D81 – 30. 9. 2026 – E4.4a místní řízení lease

- Převzetí: po D80 lze bez desky ověřit vlastnictví jednoho místního control spojení. Rozsah: čisté C++ pravidlo `hello`/manual lease/heartbeat/odpojení nad existujícím `DeviceControlRuntime`, hostitelský test, firmware CI, README a tento stav. Objekt smí vzniknout až po budoucí autentizaci TLS/WSS; žádný autentizační adaptér ani socket v tomto kroku nepřibývá.
- Ověření 30. 9. z `kufr/firmware`: MSVC `cl /std:c++14 /EHsc /W4 /WX` a spuštění nového `local_control_session_test` i stávajícího `device_control_runtime_test` prošly. První běh nového testu odhalil očekávanou nutnost fyzického zotavení po expiraci; test nyní ověřuje odmítnutí převzetí před zotavením. Další kontrola odhalila, že pozdní odpojení starého socketu mohlo zrušit lease nového vlastníka; podmíněné uvolnění podle přesného lease ID a dvousession test chybu opravily. `pio run` i `pio run -e esp32-s3-arduino-serial` prošly po opravě. PR #43 prošel `host-tests` i `firmware-build` a byl sloučen jako `0a09957ed122e27f6f1b05b4016a41fe51a48984` na `main`. Deska nebyla ověřena.
- Neprovedeno: skutečný WSS handshake a token, napojení na IDF control smyčku, textový producent, deska/B1. Další krok je transportní/autorizační adaptér a bezpečné předání jeho událostí vlastníku řízení.

### D80 – 30. 9. 2026 – E4.3d2b5 místní ID rozhovoru

- Převzetí: po D79 zůstávalo místní `conversationId` mimo síťovou cestu. Výsledek neměl vazbu na místní sezení a nový opening mohl následovat starý text z fronty. Rozsah: textová a odpovědní schránka, síťové opening/tah předání, hostitelské testy, firmware README a tento stav. JSON/NVS kontrakt se nemění; WSS a autorizovaný producent stále chybí.
- Ověření 30. 9. z `kufr/firmware`: MSVC `cl /std:c++14 /EHsc /W4 /WX` a spuštění testů textové/odpovědní schránky, runtime stavu a složené korelace prošly po opravě pořadí opening/text. `pio run` a `pio run -e esp32-s3-arduino-serial` prošly; ESP-IDF build byl opakován po přidání brány příjmu. Místní ID validuje UUID a zachová původní zápis pro odpověď. Nový opening zahodí čekající text jiného sezení; brána nepřijme cizí tah ani při neúspěšném backendovém startu. PR #42 prošel `host-tests` i `firmware-build` a byl sloučen jako `fb8fc77c6258625fa7631e3ce093e6931f9d91f8` na `main`. Deska nebyla ověřena.
- Neprovedeno: runtime autentizace WSS, manual lease a textový producent, fyzická deska/B1 a skutečný audio výstup. Další nezávislý krok: místní autorizační/lease brána a její napojení na text/opening; HW akceptace zůstává oddělená.

### D79 – 30. 9. 2026 – E4.3d2b4 korelace místního textového tahu

- Převzetí: po D78 je další nezávislý softwarový krok zachovat `clientTurnId` ze smlouvy `conversation.text` v čekajícím textu, backendovém požadavku a předávané replice. Dosavadní síťová úloha při každém tahu generovala jiné ID, takže budoucí místní klient by výsledek nemohl bezpečně přiřadit. Rozsah: firmware `conversation`/`backend_access`, hostitelské testy, firmware CI, README a tento stav. Nezavádí WSS server, audio port, GPIO ani skutečného místního producenta.
- Ověření 30. 9. z `kufr/firmware`: Visual Studio Build Tools `cl /std:c++14 /EHsc /W4 /WX` a spuštění `conversation_text_inbox_test`, `conversation_reply_inbox_test`, `conversation_runtime_state_test` a složeného `conversation_turn_correlation_test` prošly. `pio run` (ESP-IDF 6.1) a `pio run -e esp32-s3-arduino-serial` prošly po korekci velkých písmen/nulového UUID. Místní `clientTurnId` přijímá i velká písmena, do backendu jde kanonický malý zápis, pro místní výsledek zůstává původní podoba. Nulové UUID se odmítá; idempotency key zůstává oddělený. JSON/NVS kontrakt se nemění. PR #41 prošel `host-tests` i `firmware-build` a byl sloučen jako `b810936dacfa28a6fa1979ad52b1cb890b858d6f` na `main`.
- Neprovedeno: autorizované WSS přijetí `conversation.text`, vazba místního `conversationId` na backendové sezení, odeslání `conversation.result` klientovi, TTS/I2S, fyzický port, deska/B1 a runtime TLS. Další softwarový krok je vlastník autentizovaného místního sezení/lease a mapování jeho ID na backendové sezení; fyzický výstup čeká na B1.

### D78 – 30. 9. 2026 – E4.3d2b3 předání backendové repliky

- Převzetí: po sloučení D77 byla nejbližší nezávislá softwarová mezera zahozená textová replika v `EspIdfBackendNetwork::RunTurn`. Rozsah: `ConversationReplyInbox`, síťové předání, hostitelský test, firmware CI, README a tento stav. Schránka přijme jedinou repliku do 500 UTF-8 znaků/2000 bajtů proti přesnému snapshotu. `TakeReply` ji vydá s odvolatelným tokenem; další tah a prezentace čekají až na `FinishReply`, včetně vstupu zařazeného během předchozího pollu. Terminální návrh zahodí souběžně zařazený další vstup. Změna snapshotu či `QueueCancel` token zneplatní. Ukončení backendového sezení po `no_selection` ji nemaže, aby výstup mohl dohrát již validovanou repliku.
- Ověření 30. 9. z `kufr/firmware`: Visual Studio Build Tools `cl /std:c++14 /EHsc /W4 /WX` a spuštění `conversation_reply_inbox_test` prošly (jediná replika, běžící token, dokončení, starý snapshot, zrušení, UTF-8 a limity včetně 2000 bajtů). První `pio run` selhal při linkování, protože nový zdroj chyběl v explicitním `src/CMakeLists.txt`; seznam byl doplněn. Opravený `pio run`, jeho opakování po zpřísnění zrušení a `pio run -e esp32-s3-arduino-serial` prošly. YAML workflow byl načten a `git diff --check` prošel; vzdálený CI běh je doložen níže.
- Neprovedeno: skutečný konzument `TakeReply` v IDF smyčce, TTS/playback, autorizovaný místní opening/text vstup, kalibrované pohony, deska/B1, hardware a živý backendový test. Tato schránka neprokazuje slyšitelnou odpověď. Další krok je ověřený místní zdroj události a výstupní port; bez B1 nelze vybrat a ověřit fyzické piny ani zapojení.
- Implementační commit: `a7c2d08` na `codex/kufr-reply-handoff`, evidenční commit `c78b9a8`. `./scripts/Push-CurrentBranch.ps1` potvrdil 30. 9. push obou commitů do `origin/codex/kufr-reply-handoff`. [PR #39](https://github.com/Blato58/DildoVander/pull/39) prošel hostitelským i firmware CI [36690324250](https://github.com/Blato58/DildoVander/actions/runs/36690324250) a byl sloučen do `main` jako `9292243`; head PR je předkem ověřeného `origin/main`.

### D77 – 30. 9. 2026 – E4.3d2b2 autonomní journal a vlastník prezentace

- Převzetí: po D76 byl další proveditelný krok hostitelský životní cyklus bez GPIO. Rozsah: `DeviceControl`/`DeviceControlRuntime`, trvalý journal, `WandPresentation`, hostitelské testy, firmware CI, plán kapitola 5, firmware README a tento stav. Nový `ResolveRestored` zachovává masku pouze při potvrzeném návratu; původní `Resolve` dále maže bit po ručním výběru. Formát NVS záznamu se nemění, migrace není potřeba. Vlastník smyčky váže prezentaci před autonomním spuštěním a při Stop volá její vypnutí ihned; snapshot je nedostupný až do ukončení nebo fyzické kontroly.
- Ověření 30. 9. z `kufr/firmware`: Visual Studio Build Tools `cl /std:c++14 /EHsc /W4 /WX` a spuštění `autonomous_presentation_lifecycle_test`, `persistent_command_journal_test`, `device_control_runtime_test`, `wand_presentation_test` prošly; `turn_replay` se stejnými přepínači sestaven. Testy kryjí opakovaný normální návrat, atomickou dostupnost přes restart, starý/expirovaný návrh, chyby journalu, okamžitý Stop portu a zotavení před dalším Tick. `pio run` a `pio run -e esp32-s3-arduino-serial` prošly. `git diff --check` prošel. Firmware CI dostalo nový hostitelský test, vzdálený běh zatím neproběhl.
- Neprovedeno: fyzický servo port, potvrzení polohy, hardware a měření B1, nahrání do desky, místní autorizovaný opening/text producent, WSS, audio ani živé backendové volání. `espidf_main.cpp` zatím nemá skutečný port, proto autonomní transakci nespouští. Další je autorizovaný vstup a bezpečné předání replik/návrhu vlastníkovi smyčky; B1 blokuje až fyzické pohony.
- Implementační commit: `236411b` na `codex/kufr-presentation-lifecycle`; evidenční commit `a0eb5b9`. `./scripts/Push-CurrentBranch.ps1` potvrdil 30. 9. úspěšný push a shodu `origin/codex/kufr-presentation-lifecycle` s `a0eb5b9`.
- PR [#38](https://github.com/Blato58/DildoVander/pull/38) prošel hostitelským i firmware CI [36686604923](https://github.com/Blato58/DildoVander/actions/runs/36686604923) a byl sloučen do `main` jako `675cde8`; `d15a82f` z PR je předkem ověřeného `origin/main`.

### D76 – 30. 9. 2026 – pouze Sol a oprava opakujících se otázek

- Majitel zvolil pouze Sol po živém testu Luny. Backend i ruční workflow odmítají jiné dialogové modely; eval používá Sol. Prompt zakazuje mechanické potvrzování jména, znovu položené zodpovězené otázky a dva dotazy v jedné replice.
- D75: PR #35 sloučeno do `main` jako `c3d2c535ba00356f7710e432201745495b8d5832`; firmware CI a offline eval prošly. Nasazení 36651086166 prošlo. Pět skutečných Luna tahů přes veřejné API skončilo návrhem přihrádky 3; replay stejného opening vrátil tutéž operaci, DELETE sezení prošel (36652057592). Návštěvníkovy odpovědi vznikaly až po přečtení skutečné AI repliky.
- Hodnocení: Luna držela svatební historku, ale opakovala zodpovězený dotaz na výmluvu, pokládala dva dotazy najednou a místo pointy komentovala příběh. To není splněná akceptace přirozeného humoru.
- Skutečný backendový návrh prošel hostitelským parserem/guardem a simulovaným raise/lower/complete pro přihrádku 3. Expirovaný návrh a readyMask=0 byly odmítnuty bez simulovaného pohybu. Replay používá zachycený čas příjmu; není důkaz ESP32 sítě ani fyzického serva.
- Ověření změny: Python eval 20 testů a Release backend testovací program prošly; Luna a Astra jsou výslovně odmítnuty. Živý Sol a restart paměti jsou doložené níže. Nákladové rezervace/odhady nejsou skutečně vyúčtované částky OpenAI; původní limit testů 1 USD a měsíční 10 USD se nemění.

- Doručení: `0d7a26e` ověřeně pushnuto; [PR #36](https://github.com/Blato58/DildoVander/pull/36) po úspěšném offline CI sloučeno jako `78b563ef57cdc68d352d8aa808b7bd846d3d096f`, ověřeno na origin/main. Main backend CI 36652691648 prošlo. [Nasazení Sol 36652694673](https://github.com/Blato58/DildoVander/actions/runs/36652694673) úspěšné, server zůstává `responses` / `gpt-6.1-sol`.
- Živá akceptace softwarového toku: po restartu stejné eveningId a 5 výměn; na pouhé jméno Sol sám vybavil sekačku při prvním tanci a po potvrzení fotky zahrady. Pět Sol tahů skončilo správným Výborem pro špatné nápady / přihrádkou 3. Závěr „Za stovku už jim neposekáš zahradu, rovnou rozdělíš majetek“ navázal na skutečné předchozí odpovědi. Je to kvalitativní vzorek, nikoli záruka humoru na akci.
- Replay Sol návrhu produkčním C++ parserem/guardem a čistou prezentací prošel; expirace +120 s a readyMask=0 odmítnuty bez simulovaného pohybu. Luna měla navíc nesoulad názvu hůlky (katalog 6) a návrhu (3); číselný guard tento sémantický nesoulad volného textu nekontroluje. Sol v novém běhu název i číslo spojil správně.
- Cleanup: terminální Sol sezení DELETE prošlo; další opening a explicitní DELETE nedokončeného sezení také. Paměť stále 11 výměn / 1811 B. Smazání přesného syntetického eveningId a následný status 36653597988 potvrdily nové eveningId a 0 výměn. Automatický firmware timeout nebyl zkoušen na běžící desce.
- Rozpočet: 5 Luna + 6 Sol unikátních živých tahů v této sérii; včetně konzervativního historického základu kontrolní rezervace 0,884808 USD z povoleného 1 USD. Nejde o skutečnou fakturu, historický základ částečně vychází ze sdělení majitele. Měsíční limit 10 USD nezměněn. Přesný přepis, workflow ID, korelace a výpočet jsou v [živém reportu](../ai-eval/reports/2026-09-30-live-backend-dialogue.md).

### D75 – 30. 9. 2026 – autonomní pevná rekvizita, backendové funkce a živý dialog

- Rozhodnutí majitele: pouze Sol/Luna; žádná Astra v nových bězích. AI sama vybírá hůlku bez obsluhy, servo ji vysune a zase zasune. Hůlka zůstává v přihrádce. Plán aktualizuje provoz, fyzickou hranici a kritéria E6/E8/E9; starý ruční kontrakt zůstává kompatibilní.
- Backend: tři nativní Responses funkce s přísným schématem, výběr omezený readyMask, nejméně čtyři otázky před výběrem, přípustné ukončení kdykoli. Opravený hovorový prompt a více doplňujících otázek. Terminální replika zůstává pollovatelná, nové texty/přepisy se odmítají; následný DELETE uvolní sezení bez mazání paměti. Model je konfigurovatelný Sol/Luna, tarif Standard výslovný, konzervativní tokenový odhad není účetní údaj.
- Živý testovací nástroj: nový main-only workflow `kufr-backend-interactive-dialogue.yml`, jeden ručně řízený start/reply/finish/status/clear za běh. Tajemství zůstávají v environmentu runneru; artefakt obsahuje jen syntetický přepis a veřejnou korelaci. Další text se vybírá až po skutečné odpovědi. Starý pevný smoke nyní ověřuje pouze opening, ne humor.
- Lokální ověření v managed worktree `kufr-party-memory`: `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release` prošel včetně terminálního textu/přepisu, replay a Luna rezervace; `node kufr/backend/tests/smoke.mjs` prošel; Python eval 20 testů, interaktivní klient 9 testů, deploy konfigurace 3 testy prošly. JSON fixtures, YAML workflow a místní odkazy dotčených README/plánu/stavu se parsují a existují. `git diff --check` bez chyb.
- Firmware: bounded proposal parser + korelace/katalog/maska/UTC expirace, monotónní pending návrh, explicitní opening a DELETE s opakovatelným klíčem. Neúspěšný DELETE zachová sezení a blokuje další start; ztracená odpověď POST se jednou opakuje se stejnou obálkou, poté se sezení uzavírá. Lokální nečinnost 90 s, serverových 15 minut zůstává fallback. Odmítnutý či vypršelý návrh a dokončení prezentace mají uzavírací cestu. `WandPresentation` s portem řídí raise/show/lower; normální návrat obnoví dostupnost, Stop (i v klidu), timeout, změna bezpečnosti či zpětný čas vyvolají fault. Pohybové timeouty 3 s, prezentace 5 s jsou softwarové meze, nikoli změřená kalibrace.
- Firmware ověření pracovníkem: `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s` prošly. MSVC `/utf-8 /std:c++14 /W4 /WX /EHsc`: compile+execute `turn_operation_codec_test`, `conversation_text_codec_test`, `pending_proposal_test`, `wand_presentation_test` prošly. `turn_replay.cpp` se sestavil a syntetický JSON prošel skutečným parserem/guardem do simulovaného raise/lower/complete. CI obsahuje stejné čtyři testy a kompilaci replay nástroje. Nezávislá kontrola našla výše opravené retry/expiry chyby; úplný síťový lifecycle v běžícím ESP32 zatím testován nebyl.
- Mezera: `espidf_main` dosud nevolá opening/presentation API; chybí autorizovaný producent vstupu, playback, kalibrovaný fyzický port s journal/snapshot přechody a potvrzením polohy, Tick a propojení dokončení. Žádné GPIO, flash, snímače ani audio akceptace. Hostitelský model není živý ESP32. Backend `74d32b7` a firmware `bf43625` byly pushnuty a sloučeny v PR #35; CI/deploy a skutečný adaptivní test jsou doloženy v D76. Rozpracovaná fyzická integrace zůstává dalším krokem.

### D74 – 30. 9. 2026 – party dialog a trvalá paměť večera – backend a syntetické dialogy ověřeny

- Zadání: majitel chce přirozenou češtinu pro připité dospělé kamarády, otázky na tělo, kufr začínající bez představování, paměť jmen/odpovědí celého večera. Výslovně zvolil vlastní backend pro ukládání a schválil celkem 1 USD na placené syntetické srovnání při dosavadním limitu 10 USD/měsíc.
- Rozsah: sdílený prompt a přirozené opening/return/boundary fixtures; samostatný EveningMemoryStore SQLite, device scope, limit 2000 výměn / 2 MB, lexikální výběr kontextu 16000 znaků, trvalost přes restart a konec sezení. Nové opening/status/clear HTTP trasy v OpenAPI, dynamická rezervace přidaného kontextu, adaptivní eval Sol/Astra. Plán §6/§9 mění dosavadní RAM-only retenci pro text; audio se nearchivuje. Žádná migrace technického journalu.
- Dosavadní ověření: Release a Debug backend build bez varování; původní a nové backend testy včetně restartu, jména mimo nedávné okno, scope, row/byte cap, opening retry a clear při pozdní odpovědi prošly. HTTP smoke ověřil i uppercase UUID při smazání, cizí scope, retry a opening bez textu. Offline Python eval 17 testů; npm ci/test v contracts prošly. Nezávislá revize odhalila zápis paměti až po publikaci a normalizaci UUID; obojí opraveno, finální opakování prošlo. Cílený cs_test_impact neposkytl příkaz (ambiguous_solution při 0 objevených solutions); použit skutečný konzolový test runner z README.
- Doručení: commit `8977c5c` ověřeně pushnut; [PR #31](https://github.com/Blato58/DildoVander/pull/31) po zeleném offline CI sloučen jako `3693379`. První [main backend CI 36645715352](https://github.com/Blato58/DildoVander/actions/runs/36645715352) odhalilo závod stávajícího testu mezi terminálním stavem a uvolněním provider slotu. Testy nyní čekají i na dokončení finally; v této opravě se cena/limity nemění. Oprava `20f120a` a oddělení odhadu od billingu `4323ba1` byly pushnuty a sloučeny v [PR #32](https://github.com/Blato58/DildoVander/pull/32) jako `120fed5`; [main backend CI 36646632571](https://github.com/Blato58/DildoVander/actions/runs/36646632571) prošlo včetně HTTP smoke a publish. Schválený [placený eval 36645718827](https://github.com/Blato58/DildoVander/actions/runs/36645718827) dokončil všech 6 dialogů / 30 volání; původní odhad podle Standard ceníku je 0,325139 USD (tier tehdy nebyl uchován), skutečně účtovaná cena není ověřená (majitel v OpenAI vidí méně než cent); oba modely začaly samy a ověřily návrat Petra podle historie. Přepisy jsou v [eval reportu](../ai-eval/reports/2026-09-30-party-dialogue.md). Lenka neaktivovala odmítnutí tématu a simulátor odpověděl mimo otázku; toto není důkaz této větve. Další iterace promptu omezuje formální závěry a persona záměrně odmítne první osobní otázku. Druhý [běh pouze Sol 36646636262](https://github.com/Blato58/DildoVander/actions/runs/36646636262) s nastaveným stropem 674861 mikro USD dokončil další 3 dialogy / 19 volání: ověření Petra, doplňující otázka a změna tématu po odmítnutí u Lenky. Žádné další živé volání se nespouští.
- Oprava rozpočtu podle druhého živého důkazu: všechny odpovědi vrátily `fast`, dosavadní odhad omylem používal Standard. Report zachovává původní JSON a opravuje ceníkový odhad druhého běhu podle cache/tier na 0,088480 USD; toto stále není ověřené vyúčtování. První běh bez tieru nelze přesně zpětně ocenit. Backend i eval nyní výslovně žádají `default`, odmítnou chybějící/jiný tarif a drží předběžnou rezervu i pro 2× Fast; měsíční limit 10 USD se nemění. Lokální backend testy a 20 Python testů prošly; oprava `fc6a543` byla ověřeně pushnuta a sloučena v [PR #33](https://github.com/Blato58/DildoVander/pull/33) jako `501729f`. [Závěrečné main backend CI 36647378736](https://github.com/Blato58/DildoVander/actions/runs/36647378736) prošlo buildem, backend testy, HTTP smoke, konfigurací nasazení a self-contained publishem; deploy job se nespustil. Markdown odkazy a finální diff prošly kontrolou. Změna explicitního Standard tarifu je ověřená offline, nový placený test po této opravě neproběhl.
- Zbývá: lidské posouzení humoru a integrační navázání na skutečný provoz. Není nasazeno. Firmware/telefon zatím nevolají nový opening ani mazání; skutečný autonomní hlas a pohodlná obsluha zůstávají integrační kroky E4/E9/E10. Lexikální vzpomínky nejsou spolehlivá identifikace dvou lidí stejného jména. Uložená vygenerovaná replika není důkazem jejího přehrání; obsah a technický journal mají oddělené transakce.

### D73 – 30. 9. 2026 – zřetelná role kufru v prvních tazích

- Po merge [PR #28](https://github.com/Blato58/DildoVander/pull/28) nasazení [36641504963](https://github.com/Blato58/DildoVander/actions/runs/36641504963) prošlo buildem, backendovými testy, HTTP smoke a aktivací režimu `responses`. Veřejný placený tříkolový test [36641729532](https://github.com/Blato58/DildoVander/actions/runs/36641729532) přes HTTPS prošel: AI položila dvě navazující otázky, použila odpovědi v pointě a vybrala dostupnou Jasanovou hůlku Poslední slovo z přihrádky 1. První dvě repliky však neukázaly zřetelně roli Ollivanderova mluvícího kufru.
- Proto `ai-eval/prompt.txt` výslovně žádá krátké představení kufru v první odpovědi a přirozené kouzelnické detaily v dalších. Po změně znovu prošly `dotnet run --project kufr/backend-tests/BackendTests.csproj` a `python -m unittest discover -s kufr/ai-eval -p 'test_*.py'` (11 testů); testy kontrolují sdílené instrukce. Dřívější živý výsledek nedokazuje účinek nového promptu.
- Po merge [PR #29](https://github.com/Blato58/DildoVander/pull/29) [nasazení 36642018331](https://github.com/Blato58/DildoVander/actions/runs/36642018331) v režimu `responses` prošlo. [Veřejný test 36642220511](https://github.com/Blato58/DildoVander/actions/runs/36642220511) dokončil tři tahy: první replika se představila jako Ollivanderův mluvící kufr, druhá použila kouzelnické ministerstvo i jasan, finále vybralo přihrádku 1 a pointou spojilo obě odpovědi o pondělcích a rozpočtu. Ověřena korelace, idempotence, poll a cleanup bez fyzického výstupu. Každý z obou tříkolových testů rezervoval nejvýše 0,15 USD na své sezení; skutečný účet z provideru nebyl odečten. [Nasazení 36642296679](https://github.com/Blato58/DildoVander/actions/runs/36642296679) vrátilo backend do `fake` režimu; [loopback smoke 36642427583](https://github.com/Blato58/DildoVander/actions/runs/36642427583) prošel a veřejné `https://2.28.202.220/healthz` vrátilo `{"status":"ok"}`. Další je lidské hodnocení na akci a fyzické mapování po B1; E8 není produkčně akceptováno.

### D72 – 30. 9. 2026 – nezávisle navržený tematický katalog B5

- Rozsah: nezávislý agent navrhl šest původních českých hůlek pro přihrádky 1–6. `ai-eval/wand-catalog.json` je sdílený zdroj pro backendový embedded katalog a vývojový eval. Obě cesty předávají AI jen položky z `readyMask`; prompt výslovně staví roli mluvícího kouzelného kufru prodejce z Ollivanderova obchodu v tématu Harryho Pottera. Plán §6 a README aktualizují obsahové rozhodnutí. Fyzické sloty, přijetí `catalogVersion` zařízením a skutečný inventář stále čekají na B1/E6.
- Místní ověření v worktree `kufr-ip-https`: `dotnet run --project kufr/backend-tests/BackendTests.csproj` prošel; `python -m unittest discover -s kufr/ai-eval -p 'test_*.py'` — 11 testů prošlo. Testy ověřují tematické instrukce a filtrování dostupné hůlky v backendu i eval. Placený živý test aktualizovaného promptu a nasazení se ještě musí provést; starší D68 ověřovalo předchozí prompt.
- Předání: ověřit po merge hlavní CI, nasazení backendu a placený syntetický tříkolový rozhovor přes veřejné HTTPS. Teprve potom hodnotit, zda Ollivanderova role a závěrečná pointa skutečně zní správně. Obsahovou akceptaci s hosty ani fyzickou vazbu to nenahrazuje.

### D71 – 30. 9. 2026 – E4.3d2b1 text navázaný na ověřený snapshot

- Rozsah: `ConversationTextInbox::Submit` nyní přijímá celý očekávaný `ConversationRuntimeSnapshot` a při vložení jej porovná s aktuálním stavem. `EspIdfBackendNetwork::QueueTextTurn` vyžaduje stejný snapshot od budoucího autorizovaného volajícího; už si nesmí potichu načíst novější verzi po ověření příkazu. Síťový konzument dál zahazuje tah, pokud se snapshot změní až po vložení. Testy pokrývají zastaralou verzi, invalidaci, prázdný/chybný text a plnou frontu; firmware README a hostitelské CI přidávají tento test.
- Ověření v worktree `kufr-ip-https`: MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc /utf-8` nad `ConversationSession.cpp`, `ConversationRuntimeState.cpp`, `ConversationTextInbox.cpp` a zvlášť `conversation_text_inbox_test.cpp` i `conversation_runtime_state_test.cpp` v `%TEMP%/kufr-e4-text-inbox` — oba testy prošly. Z `kufr/firmware` přes místní PlatformIO Core 6.2.0 `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s` — oba buildy exit 0. [PR CI 36639168333](https://github.com/Blato58/DildoVander/actions/runs/36639168333) prošlo hostitelským testem a čistými buildy obou profilů; read-only kontrola nenašla chybu v pořadí zámků ani vazbě snapshotu. Commit `ebd1e8d` byl ověřeně pushnut, [PR #26](https://github.com/Blato58/DildoVander/pull/26) sloučen jako `e1c939c`.
- Omezení/předání: tento díl řeší jen hranici snapshotu. Autorizovaný WSS producent, ověření aktivního manual lease, vazba `ConversationSession` na životní cyklus Stop/lease loss, ACK/výsledek a skutečné TLS spojení na desce chybí. Další díl E4.3d2b musí řešit jejich vstupní kontrakt a korelaci; B1 stále blokuje fyzické ověření.

### D70 – 29. 9. 2026 – Ad Hoc podpis a vlastní Safari instalační web

- Rozsah: `.github/workflows/kufr-ios.yml` a `build-ios-ipa.sh` volí vývojový nebo Ad Hoc profil/certifikát se stejným secret heslem, ověřují shodu Team/Bundle ID, typ a platnost profilu, exportní podpis a embedded profil. Opravena Bash chyba prázdného pole při OpenSSL přebalení, kterou odhalil běh 36635404052. `publish-ios-install.py` ověří hash a metadata IPA, připraví nezměnitelnou verzi, manifest a instalační stránku a atomicky přepne hlavní stránku. Nginx template, `install-ios-host.sh` a IP HTTPS workflow přidaly statickou cestu `/ios/` bez změny backendového API. Plán a obě README popisují publikaci i hranice důkazu.
- Lokální ověření v worktree `kufr-ip-https`: `python -m unittest discover -s kufr/mobilni-aplikace/scripts -p 'test_publish_*.py'` — 4 testy prošly (platný IPA/manifest, chybný, prázdný hash a duplicitní release). Python `yaml.BaseLoader` načetl oba změněné workflow a `git diff --check` byl čistý. CI a veřejný serverový výsledek jsou doložené níže.
- Předání: E3a dokončeno v instalačním rozsahu. V E10 ověřit native WSS/TLS, LAN oprávnění, Keychain a skutečné ruční ovládání; v E11 aktualizaci a rollback IPA na iPhonu. B1 stále blokuje desku a fyzický kufr.
- Doručení a první serverový pokus: commit `27633a8` byl ověřeně pushnut, [PR #23](https://github.com/Blato58/DildoVander/pull/23) sloučen po průchodu [lint CI 36636588093](https://github.com/Blato58/DildoVander/actions/runs/36636588093) a [web/iOS Simulator CI 36636588207](https://github.com/Blato58/DildoVander/actions/runs/36636588207). [Stage 36636903406](https://github.com/Blato58/DildoVander/actions/runs/36636903406) připravil root skript; první spuštění prošlo `nginx -t`, ale okamžitá `/ios/` sonda dostala 404, takže skript vrátil předchozí Nginx konfiguraci. Následující oprava čeká na aktivaci nové konfigurace opakovanou sondou; případná trvalá 404 se bude dál diagnostikovat, neprohlásí se za hotovo.
- Výsledek: [PR #24](https://github.com/Blato58/DildoVander/pull/24) s retry sondou po reloadu prošel [lint CI 36637208230](https://github.com/Blato58/DildoVander/actions/runs/36637208230), byl sloučen jako `7382b4d` a [stage 36637316584](https://github.com/Blato58/DildoVander/actions/runs/36637316584) připravil opravený root skript. Majitel jej spustil; `nginx -t` i lokální HTTPS `/ios/` sonda prošly. Windows `curl.exe -I https://2.28.202.220/ios/` vrátil 200, `text/html`, `no-store` a `nosniff`; původní placeholder byl poté nahrazen IPA stránkou.
- [Ad Hoc běh 36636991415](https://github.com/Blato58/DildoVander/actions/runs/36636991415) na merge `bf2496e` prošel webem, iOS Simulatorem, podpisem, publikací a externí kontrolou stránky, manifestu, IPA a SHA-256. OpenSSL přebalená distribuční identita se importovala do Keychain, podpis i embedded profil IPA se ověřily. Externí 200 odpovědi mají pro `manifest.plist` `text/xml` a pro `kufr.ipa` `application/octet-stream`. Skutečný Brave/Chromium browser zobrazil stránku na 390×844 bez horizontálního přetečení (šířka obsahu 375 px), s instalačním odkazem na verzovaný manifest a bez chyb v konzoli. Majitel 30. 9. potvrdil, že na iPhonu 15 Pro aplikaci nainstaloval a spustil. E3a je tím akceptované; aktualizace, native transport a funkční ovládání kufru zůstávají E10/E11.

### D69 – 29. 9. 2026 – diagnostika PKCS#12 pro iOS podpis

- Rozsah: `build-ios-ipa.sh` při odmítnutí původního `.p12` v Keychain zkusí PKCS#12 přečíst pomocí OpenSSL se stejným secret heslem, poté jej v soukromém dočasném adresáři přebalí do kompatibilní podoby a znovu importuje. Soubor s nešifrovaným privátním klíčem se smaže hned po přebalení; EXIT cleanup maže celý adresář a do logu nevypisuje klíč ani heslo. Následné ověření identity proti profilu zůstává povinné.
- Ověření: třetí [CI běh 36634024967](https://github.com/Blato58/DildoVander/actions/runs/36634024967) před touto úpravou prošel webem a simulátorem, ale podpis opět selhal na `MAC verification failed`; aktuální fallback se musí ověřit dalším podepsaným macOS během. `certutil -encodehex -f ... 0x40000001` byl na neškodném vzorku lokálně ověřen jako čistý Base64 výstup.
- Předání: spustit podepsaný CI běh, rozlišit selhání OpenSSL čtení od úspěšného přebalení. Bez úspěšného podpisu a ověřené instalace na iPhonu B2/B3 zůstávají otevřené.

### D68 – 29. 9. 2026 – veřejný vícekolový smoke test

- Rozsah: `deployed_smoke.py` při explicitním režimu `responses` provede tři navazující syntetické tahy přes stejné sezení: dvě modelové otázky a návrh dostupné Hůlky 1. Kontroluje korelaci návrhu, idempotenci prvního tahu a vypisuje pouze veřejné syntetické repliky pro lidské posouzení. Dosavadní výchozí `fake` smoke zůstává jediný tah. Nový ruční workflow `Kufr backend public dialogue` běží na nezávislém GitHub runneru přes veřejné HTTPS s běžným ověřením IP certifikátu a vývojovou identitou z environmentu. Tři rezervace po 0,05 USD nepřekročí limit 0,50 USD/sezení.
- Ověření: lokální `python -m py_compile kufr/backend/tests/deployed_smoke.py`, YAML parser nového workflow a `git diff --check` prošly. [Main CI 36633868823](https://github.com/Blato58/DildoVander/actions/runs/36633868823) prošlo backend buildem, testy, HTTP smoke a publishem. [Nasazení 36634057947](https://github.com/Blato58/DildoVander/actions/runs/36634057947) aktivovalo aktuální `responses` na serveru. [Veřejný placený test 36634270957](https://github.com/Blato58/DildoVander/actions/runs/36634270957) prošel třemi tahy přes veřejné HTTPS: AI se zeptala na účel, pak na reakci na „zrušení pondělků“, a nakonec vybrala Hůlku 1 s pointou o rozpočtu jako konfetách a státním bankrotu. Návrh měl správnou přihrádku, turn ID, revision a žádný fyzický výstup. Přesná skutečná útrata nebyla z API/dashboardu odečtena; horní rezervace byla 0,15 USD.
- Předání: vrátit nasazení do výchozího `fake` režimu a ověřit fake smoke. Delší obsahovou akceptaci s lidmi a skutečným kufrem teprve provést; jeden syntetický tříkolový scénář ji nenahrazuje.

### D67 – 29. 9. 2026 – příprava veřejného IP HTTPS

- Rozsah: workflow `Kufr backend IP HTTPS` připraví root bootstrap z `main` na serveru a ověřuje veřejný endpoint z nezávislého GitHub runneru. Root skript instaluje Nginx a Certbot 5.4+, vydává veřejný krátkodobý certifikát s IP SAN přes webroot, přesměruje port 80 na HTTPS, proxyuje port 443 do loopback backendu a nastaví dvojnásobnou denní kontrolu obnovy s Nginx reload hookem. Dřívější root konfiguraci odmítá přepsat a při chybě nového Nginx configu vrátí původní site.
- Ověření: host probe [36631088123](https://github.com/Blato58/DildoVander/actions/runs/36631088123) potvrdil Ubuntu 26.04, aktivní backend na `127.0.0.1:5080` a volné porty 80/443. PR lint [36633171471](https://github.com/Blato58/DildoVander/actions/runs/36633171471) ověřil Bash syntaxi a zdrojové soubory. Po merge `7a3817d` nastavená repository variable `KUFR_BACKEND_PUBLIC_IPV4=2.28.202.220` a [stage 36633241241](https://github.com/Blato58/DildoVander/actions/runs/36633241241) připravil skript na serveru. Majitel jej spustil jako root; výstup potvrdil vydání certifikátu, aktivní HTTPS, timer a úspěšnou zkušební obnovu. [Externí verify 36633620137](https://github.com/Blato58/DildoVander/actions/runs/36633620137) z GitHub runneru prošel důvěrou certifikátu pro IP, platností nejméně 48 h, `/healthz` a HTTP 308. Lokální Windows `curl.exe --fail https://2.28.202.220/healthz` vrátil `{"status":"ok"}`.
- Předání: reálná obnova certifikátu po uplynutí jeho životnosti zatím neproběhla; denní externí sonda má odhalit problém. IP HTTPS je použitelné pro syntetický vývojový test, skutečné ESP32 a produkční provoz potřebují další akceptaci.

### D66 – 29. 9. 2026 – AI vytváří navazující otázky

- Rozsah: `ConversationStore`, `TextDialogRunner` a `ResponsesTextDialogClient` předávají poslední dvě dokončené výměny do dalšího placeného tahu. V prvních dvou tazích dynamické JSON schéma vyžaduje modelovou otázku; předčasný návrh se odmítne bez náhradní pevně napsané repliky. Prompt podle zadání majitele dovoluje ostřejší improvizovaný roast a neobsahuje seznam zakázaných témat. Odpovědi návštěvníka se použijí v závěrečné pointě. Historie má stropy 2 výměny po 500/300 znacích a mizí s koncem či expirací sezení. Odesílaný Responses požadavek má strop 14 kB; konzervativní mez při ceně Sol 2,50/10 USD za milion tokenů je 14 000 vstupních + 1 200 výstupních tokenů = 0,047 USD, pod rezervací 0,05 USD. Syntetický eval má tříkolové scénáře jen jako testovací fixtures; nejsou to odpovědi produkčního dialogu.
- Ověření v worktree `kufr-ai-eval`: `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release` prošel včetně hranice prvních otázek, historie, expirace a dostupnosti; `python -m unittest discover -s kufr/ai-eval -p 'test_*.py'` prošel 10 testy; `git diff --check` bez chyb. Před opravou test fixture byl jeden běh červený, protože očekával původní pevnou náhradní otázku; po úpravě na modelovou otázku prošel. Skutečná delší konverzace přes OpenAI ani nasazený backend v tomto slice zatím ověřené nejsou.
- Předání: provést řízenou placenou vícekolovou zkoušku v limitu 0,50 USD/sezení a lidsky zhodnotit otázky a pointu. Produkční `fake` režim zůstává samostatným deterministickým testovacím adaptérem.

### D65 – 29. 9. 2026 – firmware CI bez fyzické desky

- Rozsah: `.github/workflows/kufr-firmware.yml` přidává PR/push a ruční spuštění pro oba připnuté PlatformIO profily. Na Ubuntu sestaví ESP-IDF i Arduino serial a spustí hostitelský `DeviceControl` i nový snapshot runtime test; joby nemají přístup k podpisovým ani backendovým secrets a nic nenahrávají do desky.
- Ověření: Python `yaml.BaseLoader` úspěšně načetl workflow a oba joby; `git diff --check` bez chyb. [PR CI 36627873339](https://github.com/Blato58/DildoVander/actions/runs/36627873339) na commitu `23d6891` prošlo hostitelskými testy a čistými ESP-IDF/Arduino buildy. [Push CI 36628590170](https://github.com/Blato58/DildoVander/actions/runs/36628590170) na merge commitu `b35a82a` prošlo stejné dva joby na `main`.
- Doručení: commit `23d6891` ověřeně pushnut přes `./scripts/Push-CurrentBranch.ps1`; [PR #16](https://github.com/Blato58/DildoVander/pull/16) sloučen jako `b35a82a`.
- Předání: CI chrání build a modelové testy bez hardwaru; B1 a fyzickou akceptaci E4/E5 nenahrazuje. Následuje E4.3d2b jen s autorizovaným místním vstupem a B1 pro skutečné WSS/TLS.

### D64 – 29. 9. 2026 – vlastník snapshotu v ESP-IDF smyčce

- Rozsah: `DeviceControlRuntime` je jediný volající `DeviceControl` v IDF řídicí smyčce a publikuje `ConversationRuntimeState` po bezpečném zotavení, změně lease, uzavření journalu a reloadu. Při přijatém výběru, probíhající sekvenci, Stop nebo chybě journalu stav invaliduje; generaci zvýší při Stop a ztrátě ručního lease. `DeviceControl::Tick` nově hlídá mezeru smyčky i mezi přijetím a spuštěním výběru. IDF sestavení obsahuje zdroje v CMake a velkou command cache mimo zásobník `app_main`; prázdný boot ID a nulová kalibrace zatím blokují skutečné povely.
- Ověření z worktree `kufr/firmware`: MSVC hostitelské testy `device_control_runtime_test.cpp` a původní `device_control_test.cpp` s `/std:c++14 /utf-8 /W4 /WX` prošly; nový test byl po doplnění mezery smyčky před spuštěním opakován. První `pio run -e esp32-s3-idf -s` selhal při linkování, protože CMake neobsahoval `DeviceControl.cpp` a `DeviceControlRuntime.cpp`; po doplnění a pak znovu po přesunu command cache mimo stack prošel. `pio run -e esp32-s3-arduino-serial -s` prošel. `git diff --check` bez chyb. Na PR větvi se žádný CI job nespustil; tato etapa nemá firmware workflow.
- Doručení: commit `d0d99ef` byl ověřeně pushnut přes `./scripts/Push-CurrentBranch.ps1`; [PR #15](https://github.com/Blato58/DildoVander/pull/15) byl sloučen do `main` jako `60cee7b`.
- Omezení/předání: žádný místní autorizovaný producent textu, WSS, servisní USB brána ani fyzická akceptace; síťový task smí jen číst snapshot a nemá přístup k pohonům. D64 je díl E4.3d2a, další E4.3d2b řeší místní vstup a vazbu na lease. B1 stále blokuje nahrání a ověření na desce.

### D63 – 29. 9. 2026 – nasazený placený Responses tah a návrat na fake

- Rozsah: [PR #12](https://github.com/Blato58/DildoVander/pull/12) přidal ocenění GPT-6.1 Sol (commit `4c42550`, merge `dc872e4`) a [PR #13](https://github.com/Blato58/DildoVander/pull/13) asynchronní runtime (commit `acc95d8`, merge `3ca4ded`); oba byly ověřeně pushnuty a sloučeny. Výchozí `fake` se mění pouze výslovným vstupem ručního nasazení.
- Ověření: [main CI 36624471003](https://github.com/Blato58/DildoVander/actions/runs/36624471003) prošlo Linux buildem, backend testy, HTTP smoke, konfiguračními testy a self-contained publishem. [Nasazení 36624607061](https://github.com/Blato58/DildoVander/actions/runs/36624607061) s `dialog_mode=responses` prošlo včetně aktivace a health. [Placený loopback smoke 36624804144](https://github.com/Blato58/DildoVander/actions/runs/36624804144) vrátil `deployed_responses_dialogue=ok`: token, idempotentní opakování, asynchronní poll výsledku a cleanup na skutečné serverové instanci s vývojovým credential. [Nasazení 36624885475](https://github.com/Blato58/DildoVander/actions/runs/36624885475) vrátilo `dialog_mode=fake` a [smoke 36625085110](https://github.com/Blato58/DildoVander/actions/runs/36625085110) prošel běžným fake scénářem. Tah před voláním rezervoval 0,05 USD; přesný produkční účet z běhu nebyl čten z root-owned SQLite ani dashboardu.
- Omezení/předání: jde jen o syntetický text a lokální HTTP na runneru, nikoli veřejné HTTPS, fyzické ESP32, iPhone nebo lidskou obsahovou akceptaci. Provoz zůstává ve fake režimu. Následuje E4.3d2 a širší E8 hodnocení před akcí.

### D62 – 29. 9. 2026 – asynchronní backendový textový tah

- Rozsah: `ConversationStore` rezervuje cenu a identitu tahu pod jedním zámkem před odesláním; dva požadavky se stejným `clientTurnId` nesmí vyvolat druhé placené volání. `TextDialogRunner` drží nejvýše jedno souběžné volání mimo zámek a podporuje pouze explicitní `Backend:Dialog:Mode=responses`; chybějící klíč/limity odmítnou start. Operace přechází `pending` → `running` → `completed` či `failed`; DELETE operace nebo sezení zruší její token, předstartovní zrušení vyúčtuje nulu, nejasné výpadky si rezervaci ponechají a restart je neopakuje. Model vrací jen validovanou volbu; backend vytvoří korelovaný 60s návrh bez přístupu k aktuátorům. Ruční deploy a deployed smoke dostaly výslovný vstup `dialog_mode`, výchozí hodnota zůstává `fake`.
- Ověření: lokální `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release` prošel včetně úspěchu, idempotence, odmítnutí druhého tahu, skutečného účtování, rychlého přerušení zrušeného požadavku, pozdní odpovědi po zrušení, selhání poskytovatele, restartu běžící placené operace a limitu nákladu. `dotnet build kufr/backend/backend.csproj` a následný `node kufr/backend/tests/smoke.mjs` prošly ve výchozím fake režimu. První samostatný HTTP smoke bez předchozího Debug buildu neprošel kvůli chybějící `bin/Debug`; po sestavení prošel. Python syntax a YAML parsing obou workflow prošly. Lokální spuštění upraveného Python deployed smoke na dočasné službě automatická kontrola příkazu zamítla; bez obcházení nebylo provedeno. Živá placená odpověď přes backend ani nová aktivace zatím neproběhly.
- Předání: po sloučení provést explicitní `responses` deploy na vývojový loopback, jeden syntetický placený smoke a vrátit `fake`; zaznamenat skutečný výsledek. Před veřejným provozem doplnit HTTPS, device identitu, obsahovou evaluaci a fyzické potvrzení. Firmware E4.3d2 stále čeká.

### D61 – 29. 9. 2026 – rozpočet backendového Responses tahu

- Rozsah: `ResponsesTextDialogBudget` váže budoucí placený tah na model `gpt-6.1-sol`, rezervuje 50 000 mikro USD před voláním a po odpovědi počítá konzervativní skutečný náklad ze všech vstupních tokenů sazbou cache-write a všech výstupních tokenů včetně reasoning. Rezervace pokryje 1 200 výstupních a 15 200 vstupních tokenů při standardním zpracování; neznámý vrácený model odmítne. [Oficiální cena modelu](https://developers.openai.com/api/docs/models/gpt-6.1-sol) dne 29. 9. uvádí 2,50 USD/1M cache-write vstupu a 10 USD/1M výstupu. Majitel potvrdil projektový hard spend limit 10 USD. Tato třída zatím není napojená na endpoint ani journal a backend zůstává fake.
- Ověření: lokální `dotnet build kufr/backend/backend.csproj -c Release` prošel bez varování, následný samostatný `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release` prošel včetně sazby, horní rezervace a odmítnutí neznámého modelu. Souběžné první spuštění obou příkazů vyvolalo sdílení build výstupu a jediné varování MSB3026; sériové opakování testu prošlo bez varování. Žádný placený backendový tah ani nové nasazení v tomto kroku neproběhly.
- Předání: rezervaci v journalu vložit před provider volání, připojit ohraničené asynchronní provedení a konečný výsledek/cancel bez opakovaného volání při restartu. Pro jiný model, režim Fast nebo regionální zpracování je nutná nová cenová konfigurace a ověření.

### D60 – 29. 9. 2026 – izolovaný backendový Responses klient

- Rozsah: backend sestavuje party prompt z `ai-eval/prompt.txt` jako embedded resource. `ResponsesTextDialogClient` vytvoří jeden `store:false` požadavek s nízkým reasoning effort a přísným JSON schématem; celé volání včetně čtení těla má 45s deadline. Odpověď omezuje na 64 KiB a validuje stav, právě jeden text, rozhodnutí, přihrádku proti `readyMask` a reportované tokeny včetně celého `output_tokens`. Vrací pouze data pro budoucí orchestraci, nemá přístup k aktuátorům. Backendové endpointy jej zatím nevytvářejí ani nevolají, takže nasazený fake režim neúčtuje OpenAI. Workflow sleduje i změnu sdíleného promptu.
- Ověření: lokální `dotnet build kufr/backend/backend.csproj -c Release` prošel bez varování a `dotnet run --project kufr/backend-tests/BackendTests.csproj -c Release` prošel včetně mock Responses odpovědí, nedostupné přihrádky, prázdného inventáře, explicitního či smíšeného refusal, neúplného/malformed výsledku, chybného usage, nadměrného těla, zastaveného streamu a HTTP chyby bez úniku textu odpovědi. Offline Python eval prošel 6 testů. Skutečné provider volání přes tento klient ještě neproběhlo.
- Předání: připojit explicitní fake/Responses mód až po atomické rezervaci nákladu a turn identity, ohraničené asynchronní frontě mimo `ConversationStore.gate`, bezpečném dokončení či zrušení bez opakování po timeoutu. D61 později ověřil oficiální cenu `gpt-6.1-sol` a přidal samostatný výpočet nákladu.

### D59 – 29. 9. 2026 – autentizovaný smoke nasazeného fake backendu

- Rozsah: náhodný vývojový credential a jeho SHA-256 byly uloženy pouze jako oddělené environment secrets `DEV_DEVICE_CREDENTIAL` a `DEVICE_CREDENTIAL_SHA256` spolu s `DEVICE_ID=dev-simulator-1`; skutečný kufr tuto identitu nemá. Ruční workflow `kufr-backend-live-smoke.yml` volá jen loopback backendu na serverovém runneru a ověří token, fake sezení, idempotentní tah, poll a cleanup. Python klient netiskne tajemství ani text návštěvníka; OpenAI nevolá.
- Ověření: Python syntax a YAML parsing lokálně prošly; `git diff --check` bez chyb. Po merge `2223b33` ruční [deploy 36620277288](https://github.com/Blato58/DildoVander/actions/runs/36620277288) prošel buildem/testy/publishem/aktivací s vývojovým credential. [Deployed smoke 36620516849](https://github.com/Blato58/DildoVander/actions/runs/36620516849) vrátil `deployed_fake_dialogue=ok`: health, odmítnutí přístupu bez credential, vydání tokenu, sezení, idempotentní tah, poll výsledku `ask` bez návrhu a ukončení sezení.
- Omezení/předání: test je přes lokální HTTP na runneru, ne přes veřejné HTTPS; backend stále odpovídá fake textem a OpenAI klíč nepoužívá. Reálné zařízení, fyzické potvrzení, produkční E8 adaptér a nastavení hard spend limitu zůstávají samostatnou prací.

### D58 – 29. 9. 2026 – první úspěšná aktivace Ubuntu backendu

- Ověření: správce spustil jednorázový bootstrap z opravného balíčku D56 a obdržel `Bootstrap complete`. Ruční [Actions běh 36619302825](https://github.com/Blato58/DildoVander/actions/runs/36619302825) na `main` b8d6f6f prošel Linux buildem, backend testy, HTTP smoke, konfiguračními testy, self-contained publishem a nasazením. Nezávislá [sonda 36619461108](https://github.com/Blato58/DildoVander/actions/runs/36619461108) potvrdila `deploy_helper=present`, `releases_directory=present`, `service=active`, `loopback_health=ok` na `127.0.0.1:5080`.
- Omezení/předání: služba stále používá fake dialog a nemá veřejné HTTPS. V tomto běhu nebyla konfigurována device identita ani proveden autentizovaný `/v1` tah; připravit syntetickou vývojovou identitu a bezpečný loopback smoke. Fyzický ESP32 a iPhone zůstávají neověřené.

### D57 – 29. 9. 2026 – český korpus pro E8

- Rozsah: `cases.json` obsahuje 60 českých syntetických situací a dva jazykové vstupy. Nové případy pokrývají omezenou dostupnost, nejasné preference, prompt injection, bezpečné škádlení, fyzické potvrzení a různou podobu textu. Workflow bere case ID jako text a `evaluate.py` jej porovnává s přesným ID v korpusu; není nutné udržovat dlouhý seznam voleb v YAML.
- Ověření: místní `python -m unittest discover -s kufr/ai-eval -p 'test_*.py'` prošlo 5 testů, včetně unikátních ID a horní cenové meze pro každý případ a tři modely; YAML se parsuje, `git diff --check` bez chyb.
- Živý vzorek: [běh 36619893965](https://github.com/Blato58/DildoVander/actions/runs/36619893965) na `main` 478ef2e použil `gpt-6.1-sol` a `protected_trait_bait`. Model odmítl vtip o zdravotním omezení, lehce popíchl absurdní volbu a navrhl dostupnou Hůlku 3 bez tvrzení o pohybu; 621/102 tokenů, 4430 ms, konzervativní odhad 2573 mikro USD. Strojová validace prošla.
- Omezení/předání: u většiny 62 položek neproběhlo živé volání ani nezávislé lidské hodnocení češtiny a tónu. Dodělat backendový adaptér a širší měřený průchod před E8 akceptací.

### D55 – 29. 9. 2026 – první pokus o živé nasazení backendu

- Ověření: ruční [Actions běh 36617424363](https://github.com/Blato58/DildoVander/actions/runs/36617424363) na main eaa5ff2 prošel Linux buildem, C# testy, HTTP smoke, 3 konfiguračními Python testy, self-contained publishem a uploadem artefaktu. Deploy job na runneru dildovander-server skončil před kopírováním na kontrole test -d /opt/kufr-backend/releases s exit code 1. Nebyla aktivována žádná služba; health na serveru nebyl ověřen.
- Rozsah: nový ruční host-probe workflow vypisuje pouze účet runneru, dostupnost neinteraktivního sudo, helperu, release adresáře, systemd služby a loopback health. Nepoužívá secrets ani nic nemění na serveru.
- Ověření sondy: [běh 36617885612](https://github.com/Blato58/DildoVander/actions/runs/36617885612) vrátil `runner_user=github-runner`, `deploy_helper=missing`, `releases_directory=missing`, `service=inactive_or_missing`, `loopback_health=unavailable` a bez neomezeného passwordless sudo. Pokus správce z `/root` skončil `No such file or directory`, protože tam není checkout soukromého repozitáře; skript neběžel.
- Blokace/předání: připravit aktuální zdroj bootstrapu na runneru, správce musí ověřit jeho hash a spustit jej s root oprávněním. Potom zopakovat ruční deploy a ověřit systemd/health. Veřejné HTTPS a device identita stále chybí; zatím se netestovalo spojení ESP32.

### D56 – 29. 9. 2026 – předání zdroje bootstrapu na soukromý runner

- Rozsah: ruční workflow `kufr-backend-bootstrap-source.yml` stáhne aktuální `main` přímo na serverovém runneru a uloží `bootstrap-ubuntu.sh` spolu se závislými `deploy-helper.py` a `kufr-backend.service` do jednoho soukromého adresáře jeho účtu. Vypíše absolutní cestu, commit a SHA-256 pro ověření správcem. První běh 36618631962 přenesl jen skript; správce ověřil hash, ale bootstrap skončil na chybějícím `deploy-helper.py` po vytvoření adresářů a servisního účtu. Opravený workflow přenese všechny tři soubory. Bez sudo, secrets, aktivace služby a automatického spuštění skriptu.
- Předání: po průchodu workflow správce porovná SHA-256 v root shellu a spustí přesně tento soubor s argumentem `github-runner`. Pak znovu pustit host probe a deployment; do té doby serverová akceptace chybí.


### D54 – 29. 9. 2026 – party hlas vývojového AI promptu

- Rozsah: prompt.txt odděluje roli, humor a přátelský roast pro soukromou party 18+ od pravidel dostupnosti a fyzického potvrzení. Eval načítá tento prompt ze souboru; tři nové syntetické scénáře pokrývají vyžádané škádlení a prázdný inventář. Workflow nabízí všech 15 případů. Plán v kapitole 6 a eval README popisují tón a hranice.
- Ověření: python -m unittest discover -s kufr/ai-eval -p test_*.py prošlo 5 testů; JSON korpus a YAML workflow se parsují a všech 15 case ID souhlasí s volbami workflow. git diff --check bez chyb. Po merge eaa5ff2 prošel [živý běh 36617303150](https://github.com/Blato58/DildoVander/actions/runs/36617303150): GPT-6.1 Sol, roast_invited, readyMask=16, propose pouze přihrádku 5 s přátelským rýpnutím do nerozhodnosti; 618/92 tokenů, 4663 ms, konzervativní odhad 2465 mikro USD.
- Předání: jednorázový roast byl vtipný, stručný a držel dostupnost; rozšířit měřený český korpus a lidské hodnocení. Pořád nejde o produkční backend, STT/TTS ani HW akceptaci.

### D53 – 29. 9. 2026 – E8 vývojový textový eval bez zařízení

- Rozsah: samostatný kufr/ai-eval s 12 syntetickými českými případy, jedním placeným textovým tahem na ruční běh, přísným JSON schématem a kontrolou dostupné přihrádky; nový GitHub workflow má offline test bez klíče a ruční live job pouze na main v environmentu backend-production. Výchozí GPT-6.1 Sol lze porovnat s Astra/Luna. Není potřeba Device ID, credential, běžící backend, server ani hardware. Plán upravuje volbu dialogového modelu a vývojové limity. GitHub environment secrets SESSION_CAP_MICRO_USD=500000, DAILY_CAP_MICRO_USD=10000000 a MONTHLY_CAP_MICRO_USD=10000000 byly nastaveny a názvy ověřeny; OPENAI_API_KEY doplnil majitel po prvním commitu.
- Ověření: v izolovaném worktree python -m unittest discover -s kufr/ai-eval -p test_*.py prošlo 5 testů; stejné offline testy prošly v PR CI. Po schváleném merge b444f28 prošel [živý Actions běh 36615044071](https://github.com/Blato58/DildoVander/actions/runs/36615044071) s GPT-6.1 Sol a syntetickým případem unavailable_request: readyMask=3, požadavek na nedostupnou 6, odpověď ask s compartment=null a nabídkou 1/2; 322 vstupních, 81 výstupních tokenů, 4751 ms a konzervativní odhad 1615 mikro USD. Jde o jeden úspěšný případ, nikoli měření p95 nebo 90% české kvality.
- Blokace: OPENAI_API_KEY je v GitHub environmentu; majitel má potvrdit projektový hard monthly cap 10 USD v OpenAI Platform. První placený textový dotaz proběhl až po dodání klíče a výslovném schválení merge a testu. Na produkční E8 je dále třeba integrovat adaptér do backendového nákladového journalu, rozšířit eval nejméně na 60 scénářů a ověřit fyzické potvrzení; chybí B1/B5.
- Předání: doplnit širší český eval korpus a lidské hodnocení, potom pokračovat v backendovém adaptéru. Nezaměnit jeden syntetický běh s E8 akceptací.

### D52 – 29. 9. 2026 – příprava Ubuntu nasazení a úspornějších AI modelů

- Převzetí: majitel dodal Ubuntu server s veřejnou IP a již online GitHub runnerem `dildovander-server`, chce workflow a názvy secrets k vlastnímu vyplnění. Současně žádá přehodnocení GPT-4.1 mini vůči GPT-6 Luna. Přes GitHub API byl ověřen online Linux x64 runner, přidán štítek `kufr-backend` a vytvořen environment `backend-production` omezený na `main`; hodnoty secrets se nečetly ani nezapisovaly.
- Rozsah: `.github/workflows/kufr-backend.yml` staví/testuje na hosted Ubuntu při změně backendu v `main`; pouze ruční běh z `main` pokračuje k self-hosted aktivaci. `deploy/bootstrap-ubuntu.sh`, `deploy-helper.py` a systemd unit oddělují službu od runneru, zapisují jen omezenou konfiguraci, ukládají SQLite mimo release a vracejí binárku/konfiguraci při neúspěšném startu či health. Backend přidal `/healthz`; smoke a helper testy ověřují odpověď, záměrně prázdné secrets a odmítnutí neúplných/injektovaných hodnot. README popisuje bootstrap a přesné názvy secrets. Plán v kapitole 3 a 6 mění původní návrh kontejneru na dostupné Ubuntu/systemd a doporučuje `gpt-6-luna` s `reasoning.effort:none`, `gpt-transcribe` kvůli oznámené deprecaci mini STT a dosavadní mini TTS; kontrakt API se nemění.
- Ověření: v `C:\Development\DildoVander` prošel `dotnet build kufr/backend/backend.csproj -c Release` (0 warnings), `dotnet run --project kufr/backend-tests/BackendTests.csproj`, `node kufr/backend/tests/smoke.mjs`, `dotnet publish kufr/backend/backend.csproj -c Release -r linux-x64 --self-contained true`, `python -m unittest discover -s kufr/backend/deploy -p 'test_*.py'` (3 testy), Git Bash `bash -n` bootstrapu, parsování YAML a `git diff --check`. Pokus o Linux runtime kontrolu ve WSL skončil před startem na poškozeném připojení WSL virtuálního disku (`ERROR_FILE_NOT_FOUND`); nejde o selhání backendu. První [CI běh 36610140296](https://github.com/Blato58/DildoVander/actions/runs/36610140296) postavil backend a prošel C# testy, ale HTTP smoke selhal na Linuxu `EPIPE`: server odmítl 2MB upload dřív, než Node dokončil jeho odeslání. Test nyní posílá jen nadlimitní `Content-Length` a ověřuje HTTP 413 bez závodu v těle; lokální smoke znovu prošel. Opakovaný [CI běh 36610369322](https://github.com/Blato58/DildoVander/actions/runs/36610369322) na commitu `d5b9fe5bd824c802b4fc69e0c4e2b5cbcbfab142` prošel: Linux build, C# testy, HTTP smoke, 3 Python testy, self-contained publish a upload artefaktu. Deploy job se správně přeskočil při push. Skutečný systemd/TLS provoz zbývá.
- Omezení/předání: bez serverového bootstrapu a secrets workflow zatím nepouštět jako dokončený deployment. První instalace zůstane pouze na `127.0.0.1:5080`; veřejná IP bez TLS jména/certifikátu nestačí pro firmware. OpenAI klíč nemá spotřebitele do E8 a žádné placené volání neproběhlo. Doplnit host bootstrap, uživatelem schválené limity a secrets, ověřit ruční nasazení a až potom HTTPS proxy. Binární rollback nepokrývá nekompatibilní migraci SQLite. Zdrojové commity D52 jsou `c4234f637c6e5b1e8c70832fa1a53b6666b5253c` a `d5b9fe5bd824c802b4fc69e0c4e2b5cbcbfab142`; oba byly ověřeně pushnuty na `origin/main`.

### D51 – 29. 9. 2026 – E4.3d1 atomický snapshot fake konverzace

- Rozsah: `ConversationRuntimeState` drží pod mutexem jeden snapshot `generation`, `deviceRevision`, `readyMask`, `catalogVersion` a monotonicky měněnou verzi. Odmítá chybnou masku/katalog a zpětný posun generace/revize; po zneplatnění odmítá starý snapshot. ESP-IDF při bootu vytvoří dostupný snapshot pouze ze zdravého journalu bez pending výběru; revize/generace jsou 0, protože zde zatím žádné místní řízení neběží. `EspIdfBackendNetwork` čte stav jako celek místo samostatných konstant a čtení journalu, zahodí text se starou verzí, při změně snapshotu založí nové backend sezení a po změně během startu/pollu nepoužije výsledek. `ConversationTextInbox` nese s textem verzi. Plán a firmware README popisují vlastnictví stavu a zbývající WSS hranici.
- Ověření: v `C:\Development\DildoVander` prošly MSVC `/std:c++14 /utf-8 /W4 /WX` hostitelské testy `conversation_runtime_state_test.cpp` (včetně staré čekající události, změny masky/katalogu a invalidace) a `conversation_text_inbox_test.cpp`; výstupy byly mimo repozitář nebo odstraněny. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 020 577 B flash), po poslední změně `pio run -e esp32-s3-idf -s` a `pio run -e esp32-s3-arduino-serial -s`.
- Omezení/předání: `Publish` zatím nemá volajícího z živého `DeviceControl`/`ConversationSession`, takže změny po bootu nevznikají. `QueueTextTurn` nemá autorizovaného producenta; lokální WSS, manual lease, skutečný backend provoz, HW a GPIO nejsou ověřené. Další E4.3d2 musí připojit vlastníka řízení k publikování stavu po journalu/revizi/generaci, bez současného čtení nezamčeného journalu ze síťové úlohy. B1 dál blokuje servisní bránu a fyzické testy.
- Revize a doručení: E4.3d1 je v commitu `15e0f6533a5689603264825c9bd7e1f384026462`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D50 – 29. 9. 2026 – E3a zdrojový iOS projekt a oddělené CI

- Převzetí: majitel dodal jiné MAUI workflow a výslovně dovolil vlastní vhodnější postup. Dodal cestu k Apple `.p12` a profilu a schválil použití původního Bundle ID; profil má Team ID `3QRXV8G5Y3`, Bundle ID `app.turquoise6409.green2444`, vývojové oprávnění, jedno registrované zařízení a expiraci 25. 12. 2026. Četl se pouze obsah profilu potřebný k těmto metadatům, nikoli soukromý klíč nebo heslo.
- Rozsah: `package.json`/lockfile připínají Capacitor 8.4.3; `capacitor.config.ts` a verzovaný `ios/App` se sdíleným scheme balí dosavadní webové UI. `.github/workflows/kufr-ios.yml` odděluje webové kontroly, macOS unsigned simulator build/spuštění a ručně vyžádaný podpis pouze z `main` v environmentu `ios-signing`. `scripts/build-ios-ipa.sh` kontroluje profil/certifikát/Team/Bundle, vytváří dočasný keychain, exportuje development IPA, ověřuje podpis a profil, ukládá SHA-256 a uklidí dočasné podpisové soubory. Plán a mobilní README popisují skutečný režim.
- Místní ověření: v `kufr/mobilni-aplikace` pod Node 24.21.0 `npm ci` (0 audit vulnerabilities), `npm test` (4/4), `npm run build` a `npx cap sync ios` prošly. Z kořene repozitáře `C:\Program Files\Git_2_52\bin\bash.exe -n kufr/mobilni-aplikace/scripts/build-ios-ipa.sh`, parsování YAML a Xcode scheme/Info.plist XML a `git diff --check` prošly. Native Xcode build, simulator smoke a podpis nemohly běžet na Windows.
- CI ověření: [běh 36604229108](https://github.com/Blato58/DildoVander/actions/runs/36604229108) pro commit `60d8a974c6f048f931160a8d5d7b2639bbe0a359` — `web` zelený (14 s), `ios-simulator` zelený (2 min 3 s); Xcode 26.3 ověřen, SPM resolved, unsigned iOS Simulator build a `simctl launch` pro `app.turquoise6409.green2444` prošly. Artifact `kufr-swift-package-resolved` dodal `ios/App/App.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved` s Capacitor SPM revizí 8.4.3; soubor je vrácen do projektu. Podpisový job byl podle podmínky správně přeskočen. Runner hlásil pouze oznámení o budoucí změně Node runtime pro dvě Actions a o budoucím Ubuntu image; běh nebyl chybový.
- Omezení/předání: `ios-signing` zatím nemá potvrzené secrets; cílový iPhone, jeho UDID v profilu, podepsaná IPA a spuštění na skutečném zařízení nejsou ověřené. Základní nativní kontejner zatím zobrazuje fake UI, bez Swift WSS/TLS/Keychain adaptéru. E3a není hotová. Další nezávislý firmware krok je E4.3d; B1 blokuje HW/provisionování.
- Revize a doručení: zdrojový projekt/CI je v commitu `60d8a974c6f048f931160a8d5d7b2639bbe0a359`; CI evidence a SPM lock v `b2a3aa96e4cc10ec344a09147e07474f702bc439`. `./scripts/Push-CurrentBranch.ps1` potvrdil obě revize na `origin/main`.

### D49 – 29. 9. 2026 – E4.3c12 interní fake textový cyklus

- Rozsah: `ConversationTextInbox` přijímá jednu explicitní validovanou UTF-8 událost a při offline stavu ji zahazuje. Síťová úloha po tokenu skládá start sezení, jeden textový POST a serializovaný poll, používá stejný startovací idempotency key a sezení i pro další událost, při 401 jednou obnoví token a po 404 zahájí nové sezení až pro další explicitní událost. Odpověď `ask`/`no_selection` zůstává jen read-only diagnostikou; žádné GPIO ani `SelectionSequence` nevolá. Vstup zatím nemá autorizovaného producenta.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /utf-8 /W4 /WX` hostitelský `conversation_text_inbox_test.cpp` a stávající `conversation_start_coordinator_test.cpp` (včetně opakovaného startu a ztracené odpovědi). V `kufr/firmware` prošel `pio run` pro ESP-IDF a `pio run -e esp32-s3-arduino-serial -s`. Nový síťový cyklus se nespustil na desce ani proti živému backendu, protože událost nemá producenta a B1 chybí.
- Omezení: IDF profil nemá místní řadič stavu. Pro tento read-only fake cyklus je `generation = 0`, `deviceRevision = 0`, katalog `neutral-1` a `readyMask` z NVS journalu. Před místním WSS musí vzniknout atomický snapshot skutečného řízení, revize a katalogu. Již běžící blokující HTTPS nelze přerušit 45s deadlinem; vstupní fronta nepřenáší text přes reconnect. Opakované tahy po dosažení limitu backendu potřebují explicitní ukončení sezení.
- Předání: zdroj autorizované události a atomický snapshot, potom hostitelský integrační test celého fake cyklu a místní WSS. B1 stále blokuje provisionování lokální TLS identity, nahrání a fyzickou akceptaci.
- Revize a doručení: E4.3c12 je v commitu `392c62961a898eb07f04225722fc638e7362f1ad`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D48 – 29. 9. 2026 – E4.3c11 plánování pollu

- Rozsah: `TurnPollSchedule` odděluje monotónní začátek tahu od přijetí operace, povolí první poll po 500 ms, po 3 s přejde na 1s interval a nikdy nepovolí dva souběžné dotazy. Po 45 s od začátku tahu nový poll odmítne; dokončení, chyba a zrušení ukončí plánování.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /utf-8 /W4 /WX` test `turn_poll_schedule_test.cpp` pro hranice intervalů, souběh, deadline a vadné pořadí časů. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 489 B flash).
- Omezení: časování zatím nevolá síťová úloha. Deadline nepřeruší již běžící blokující HTTPS požadavek; skutečný čas tahu na desce není změřen.
- Předání: připojit explicitní událost textového tahu a platný snapshot stavu, potom jeden runtime start/tah/poll cyklus bez fyzických výstupů.
- Revize a doručení: E4.3c11 je v commitu `379cd143b2273f87c9053d6c9b814387982c4b44`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D47 – 29. 9. 2026 – E4.3c10 UUID pro boot a požadavky

- Rozsah: čistý `ConversationUuid` formátuje 16 bajtů jako kanonické UUID v4 včetně version/variant bitů; ESP-IDF adaptér používá `esp_fill_random` až po startu Wi-Fi, kdy má hardwarový RNG externí entropii. Tato část připravuje nové boot, idempotency a turn ID bez zakódovaných konstant.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /utf-8 /W4 /WX` test `conversation_uuid_test.cpp` pro nulový i nenulový vstup a kompatibilitu s existujícím validátorem. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 489 B flash).
- Omezení: kvalita skutečné HW entropy ani runtime použití na desce B1 nebyly ověřeny.
- Předání: zapojit jednorázový bootId a explicitní fake událost do síťové úlohy; bez automatického startu po reconnectu.
- Revize a doručení: E4.3c10 je v commitu `a1e2205fe1ebd29b8ff5d8133b992d9cd8296c15`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D46 – 29. 9. 2026 – E4.3c9 HTTPS poll výsledku

- Rozsah: `EspIdfTurnPollTransport` čte jen UUID cestu GET `/v1/operations/{id}` s bearer tokenem, CA bundle a kontrolou hostname, bez redirectu a automatických auth pokusů. Odpověď 200 omezuje na 4096 B, parsuje `TurnOperationCodec` a předá `TurnPollGuard` očekávané ID, všech šest korelačních polí i aktuální generation/revision. Ven vrací pouze potvrzenou fake odpověď bez návrhu; jiné stavy jsou Wait/Terminal/Discard. Na 401 vrací status pro pozdější obnovu tokenu.
- Ověření: v `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 433 B flash). Hostitelské testy parseru a guardu z D44–D45 zůstávají platné; skutečná HTTPS komunikace a runtime připojení nejsou ověřeny.
- Omezení: `esp_http_client_perform` při překročení limitu těla může dál číst odpověď v rámci svého timeoutu; paměťový limit se nepřekročí. Adaptér zatím nevolá síťová úloha a na desce B1 nebyl testován.
- Předání: generátor UUID a bezpečná fake událost textového tahu s jedním aktivním poll cyklem; potom lokální WSS.
- Revize a doručení: E4.3c9 je v commitu `f8dd6d7de275582c916786127027a9d5a65576cc`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D45 – 29. 9. 2026 – E4.3c8 parser výsledku operace

- Rozsah: `TurnOperationCodec` přijímá omezenou JSON odpověď GET operace (4096 B) jen pro `kind=turn` a všech šest korelačních polí. Rozlišuje pending/running/completed/failed/cancelled/unknown, kontroluje povinný tvar result/error a odmítá duplicitní/neznámá pole. Dekóduje JSON Unicode escape v textu, včetně surrogate páru, a odmítá vadné UTF-8. Podporuje zatím jen fake `ask`/`no_selection` bez návrhu; `propose` fail-closed. Zpracovaná data má následně posoudit `TurnPollGuard`, nikoli pohony.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /utf-8 /W4 /WX` test `tests/turn_operation_codec_test.cpp` pro dokončení s českým Unicode escape, pending/failed, nekonzistentní status/result, návrh, duplicitu, cizí data, zápornou revision a velikost. Po cílené revizi byla přidána hranice 500 znaků pro odpověď a 200 znaků pro důvod včetně kontroly UTF-8; znovu prošly testy parseru, `conversation_session_test.cpp` a `conversation_text_codec_test.cpp`. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 433 B flash).
- Omezení: parser ještě není připojený k HTTPS GET; nepřebírá návrhy výběru ani fyzické výstupy. Runtime TLS a deska bez B1 neověřeny.
- Předání: přidat GET poll transport s omezeným tělem a hostitelským guardem, potom fake událost textového tahu.

### D44 – 29. 9. 2026 – E4.3c7 korelace pollu

- Rozsah: `TurnPollGuard` porovná očekávaný operation ID, `deviceId`, `bootId`, `conversationId`, `clientTurnId`, `generation` a `deviceRevision` s odpovědí i aktuální generací/revision. Pozdní/cizí odpověď zahodí; pending/running pouze čeká, failed/cancelled/unknown ukončí. Pro fake text přijme jen dokončené `ask`/`no_selection` bez návrhu a s omezenou neprázdnou odpovědí. `propose` zůstává odmítnutý, dokud není ověřena expirace a fyzická potvrzovací cesta. Guard nemá přístup k pohonům.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /W4 /WX` test `tests/turn_poll_guard_test.cpp` pro každé korelační pole, změnu generace/revision, čekání, chybu a odmítnutí návrhu. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 345 B flash); PlatformIO bylo spuštěno plnou cestou k `pio.exe`.
- Omezení: není JSON parser výsledku ani HTTPS poll; guard pracuje nad již parsovanými daty. Neproběhlo síťové ani HW ověření.
- Revize a doručení: E4.3c7 je v commitu `44e2f878a455bf29c48914808b8774cf499854a6`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: přidat omezený parser operace a GET poll nad HTTPS, potom napojit na fake událost textového tahu při zachování všech kontrol.

### D43 – 29. 9. 2026 – E4.3c6 HTTPS textový tah

- Rozsah: `EspIdfConversationTextTransport` sestaví pouze pevnou HTTPS cestu nad validovaným backend hostem a kanonickým UUID sezení, odešle tělo `ConversationTextCodec` s Authorization/Idempotency-Key a přijme nejvýše 128 B odpovědi 202 přes `OperationAcceptedCodec`. CA bundle a hostname kontrola zůstávají povinné; redirect ani automatický auth retry nejsou povolené. Parsovanou 401 vrací i při `ESP_FAIL` připnutého ESP-IDF.
- Ověření: v `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 345 B flash); PlatformIO bylo spuštěno plnou cestou k `pio.exe`. Parser D42 prošel hostitelsky, síťový adaptér nemá živý TLS test.
- Omezení: transport není zapojený do síťové úlohy ani koordinačního modelu, protože chybí explicitní vstup textového tahu; bez B1 není možné ověřit běh na desce. Nadměrná pomalu streamovaná odpověď může zdržet síťovou úlohu obdobně jako u start adaptéru D39. Nenahrávalo se.
- Revize a doručení: E4.3c5–6 jsou v commitu `86a33fe6333f89c2356b8cdec4606536e130b60d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: navázat hostitelsky testovaným 401 retry se stejným tělem/UUID a doplnit korelovaný poll; před runtime připojením ověřit HTTPS stavové kódy.

### D42 – 29. 9. 2026 – E4.3c5 potvrzení operace

- Rozsah: `OperationAcceptedCodec` čte nejvýše 128 B odpovědi `202` textového tahu a přijímá pouze objekt s jediným `operationId` v kanonickém UUID formátu. Odmítá další/duplicitní pole, escaped hodnoty a trailing data; při chybě vyprázdní výstup.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /W4 /WX` test `tests/operation_accepted_codec_test.cpp` pro platný objekt, duplicitu, neznámé pole, vadné UUID, escape a velikost. Oba firmware buildy prošly společně s D43.
- Omezení: parser ještě nepřijímá reálnou HTTPS odpověď; není poll ani korelace výsledku. B1 údaje o revizi desky zatím nejsou dostupné, proto neproběhlo nahrání ani runtime TLS ověření.
- Předání: připojit omezený POST textového tahu k HTTPS adaptéru a následně poll s porovnáním všech šesti korelačních polí.

### D41 – 29. 9. 2026 – E4.3c4 JSON textového tahu

- Rozsah: `ConversationTextCodec` staví přesné JSON tělo `POST /v1/conversations/{id}/turns`, escapuje uvozovky, zpětná lomítka a řídicí znaky, odmítá neplatné UTF-8, více než 1000 znaků, prázdný text, chybná UUID, masku mimo 0–63 a tělo nad 4096 B. Sdílí validaci UTF-8 s existujícím `ConversationSession` a UUID se start kodekem; chování modelu se nemění. Idempotency-Key zůstává samostatný validovaný vstup pro pozdější HTTP hlavičku.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /utf-8 /W4 /WX` test `tests/conversation_text_codec_test.cpp` pro přesné tělo, české UTF-8, escaping, délku, vadné UTF-8, masku a ID. Původní `tests/conversation_session_test.cpp` prošel po vytažení validace. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 261 B flash); PlatformIO bylo spuštěno plnou cestou k `pio.exe`.
- Omezení: žádné HTTP odeslání tahu, parse `operationId` ani poll výsledku. Kodek sám žádný backend ani pohon nevolá. Nenahrávalo se.
- Revize a doručení: E4.3c4 je v commitu `9ed073cc2144df1805d788c8fda2388466f0058d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: přidat omezený parser 202 `operationId` a HTTPS textový tah; pak korelované pollování.

### D40 – 29. 9. 2026 – E4.3c3 jedno obnovení tokenu

- Rozsah: `ConversationStartCoordinator` připojuje device scope k tokenovému modelu a portu založení sezení. Při 401 vyžádá přes existující `DeviceTokenSession` právě jedno nové přihlášení a opakuje stejný `ConversationStartRequest` včetně Idempotency-Key. Neplatné ID/scope, serverové 409/429 a vadný úspěch odmítá bez vytvoření lokálního sezení. Hostitelský fake port umožňuje ověřit pořadí bez sítě.
- Ověření: v `C:\Development\DildoVander` prošel MSVC `/std:c++14 /W4 /WX` test `tests/conversation_start_coordinator_test.cpp` pro 401→nový token, stejné UUID při retry, scope, 409, vadný 201 a opakování po výpadku. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 813 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 141 B flash); oba příkazy byly spuštěny plnou cestou k `pio.exe`.
- Omezení: koordinátor se zatím nevytváří v síťové úloze a negeneruje UUID; volající musí uchovat idempotency key přes retry. Bez události startu rozhovoru ani PTT zůstává provozní spojení pouze tokenové. Nenahrávalo se.
- Revize a doručení: E4.3c3 je v commitu `701483d2d8eccf612d6bf83b347645f473b839fd`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: připojit start k explicitní události bez telefonu a připravit textový tah a poll; HTTP adaptér před použitím ověřit integračním TLS testem/na desce.

### D39 – 29. 9. 2026 – E4.3c2 HTTPS adaptér startu

- Rozsah: `EspIdfConversationStartTransport` připraví pevnou HTTPS cestu `/v1/conversations` z validovaného DNS hostu, předá RAM access token v Authorization a stabilní UUID v `Idempotency-Key`, odešle JSON kodeku a přijme pouze omezené tělo odpovědi 201. Používá CA bundle a hostname kontrolu, vypíná redirect a automatické auth retry; tokenovou hlavičku po požadavku smaže. U odpovědí mimo 201 nenačítá potenciálně velké tělo, aby se zachoval stav 401 pro budoucí jediné obnovení tokenu. Připnuté ESP-IDF vrací při vypnutém auth retry pro platnou 401 chybu `ESP_FAIL`; oba HTTPS adaptéry proto čtou parsovaný HTTP status i v tomto případě a úspěšné tělo dál vyžadují jen při `ESP_OK`.
- Ověření: `pio run` pro ESP-IDF po opravě 401 prošel (40 588 B RAM, 1 003 813 B flash). `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 280 081 B flash). Hostitelský kodek D38 prošel; cílená kontrola zdrojů našla a po opravě znovu ověřila zvláštní chování 401 v připnutém ESP-IDF 6.1. Síťový adaptér nemá hostitelský fake TLS test. Příkazy PlatformIO byly spuštěny plnou cestou k `pio.exe`.
- Omezení: adaptér je sestavený, ale dosud jej nevolá síťová úloha. Není zdroj zahájení rozhovoru/UUID, textový tah ani poll; skutečné spojení/CA/hostname není ověřené na desce. 512B buffer brání růstu paměti na vadné odpovědi, ale `esp_http_client_perform` může pomalu streamované nadměrné 201 tělo dočítat déle než 5s timeout jednoho čtení; řešit celkovou lhůtou při integraci. Nenahrávalo se.
- Revize a doručení: E4.3c2 je v commitu `4cd6e290f7f56aee4e3a2f7beb95579115e01aea`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: připojit start po explicitní události se stabilními ID a poté textový tah/poll; na 401 použít `DeviceTokenSession::HandleUnauthorized` bez změny idempotency key a těla.

### D38 – 29. 9. 2026 – E4.3c1 kodek založení konverzace

- Rozsah: `ConversationStartCodec` sestaví pevný JSON požadavek `/v1/conversations` s validovanými identifikátory, `cs-CZ` a vypnutým audio/image souhlasem; UUID idempotency key předává jako oddělený vstup pro budoucí HTTPS hlavičku. Omezený parser přijímá jen `conversationId`, `expiresAt` a přesné limity backendu, odmítá duplicity, neznámá pole, escaped hodnoty a odpověď nad 512 B. Kodek se nedotýká fyzických výstupů.
- Ověření: v `C:\Development\DildoVander` prošel MSVC hostitelský test `tests/conversation_start_codec_test.cpp` s `/std:c++14 /W4 /WX`; kontroluje deterministické tělo, opakovaný požadavek, vadná ID, platnou odpověď i vadný JSON. V `kufr/firmware` prošel `pio run` pro ESP-IDF (40 588 B RAM, 1 003 777 B flash) a `pio run -e esp32-s3-arduino-serial` (18 660 B RAM, 280 081 B flash); oba příkazy byly spuštěny plnou cestou k `pio.exe`.
- Omezení: ještě není HTTPS volání, runtime generování UUID, navázání na token, textový tah ani poll. Kodek je testovaný software, nikoli E4 E2E akceptace. Nenahrávalo se.
- Revize a doručení: E4.3c1 je v commitu `f232f9b636897c33b854c5763dfc2d293f804358`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: navázat blokujícím HTTPS adaptérem na samostatné síťové úloze a poté obsluhou textového tahu s přesnou korelací odpovědi; WSS zůstává další krok.

### D37 – 29. 9. 2026 – E4.3b Wi-Fi a časová brána

- Rozsah: `NetworkSettings` validuje servisně uložené SSID, WPA2 heslo a jediné DNS jméno backendu; `EspIdfNetworkSettings` čte `wand_network` NVS namespace a bez kompletní konfigurace síť nespouští. `EspIdfWifiStation` používá STA a ukládá konfiguraci Wi-Fi driveru jen v RAM; přijatou IP událost při opakovaném pokusu nemaže, aby se neztratila při souběhu s připojením. `ReconnectPolicy` dává 1/2/4/8/16/30 s s jitterem. `EspIdfBackendNetwork` běží ve vlastní FreeRTOS úloze mimo 10ms řídicí smyčku; po IP čeká na SNTP a `TrustedTime` srovnává čas s datem sestavení a poslední důvěryhodnou hodnotou v NVS. Teprve pak provede HTTPS token exchange. Chyby konfigurace, času, Wi-Fi i credential ponechávají backend offline; žádný callback nesahá na pohony. Bez identity nebo konfigurace se task nezaloží.
- Ověření: v `kufr/firmware` `tests/network_settings_test.cpp` s MSVC `/std:c++14 /W4 /WX` prošel pro validaci hostu/hesla, reset konfigurace, reconnect s jitterem, datum buildu a čas přes restart včetně selhání NVS zápisu. `pio run` pro ESP-IDF 6.1 prošel (40 588 B RAM, 1 003 777 B flash z 6 553 600 B slotu); `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 279 929 B flash). Příkazy PlatformIO byly spuštěny plnou cestou k `pio.exe`.
- Omezení: chybí fyzicky střežený servisní zápis NVS hodnot, takže bez něj na prázdné desce síť nezačne. Wi-Fi, SNTP, TLS hostname/CA ani restart nebyly ověřeny na desce; build a hostitelské fake testy nejsou fyzická akceptace. SNTP samo není autentizovaný zdroj času; konzervativní datum buildu a NVS high-water brání zjevnému návratu v čase, nikoli libovolnému posunu dopředu. Konverzační HTTPS a lokální WSS zbývají. Nenahrávalo se.
- Revize a doručení: E4.3b je v commitu `e626e2349ccd4cd5bdf07e2629dd960e4e99d29d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: pokračovat E4.3c HTTPS fake backend konverzací a E4.4 lokálním WSS; hardware/USB provisioning a runtime TLS zkontrolovat po B1.

### D36 – 29. 9. 2026 – E4.3a backend tokenový klient

- Rozsah: `DeviceTokenSession` drží access token pouze v RAM, obnovuje jej po 12 minutách, odmítá expiraci, po 401 zkusí jednu novou autentizaci a při odmítnutém device credential přechází do offline stavu. Porovnává `deviceId`, typ Bearer a dobu nejvýše 900 s. `TokenResponseParser` přijímá jen čtyři přesně typovaná pole do 512 B, odmítá duplicity, neznámá pole a escaped hodnoty. ESP-IDF adaptér sestavuje pouze HTTPS URL z validovaného hostu a pevné tokenové cesty, používá CA bundle/ověření hostname, blokuje redirect i automatické auth retry, omezuje tělo a neloguje credential ani token. Připnuté ESP-IDF 6.1 nemá komponentu `json`; po neúspěšném build pokusu ji nahradil testovaný omezený parser bez nového balíčku.
- Ověření: v `kufr/firmware` `tests/device_token_session_test.cpp` s hostitelským MSVC `/std:c++14 /W4 /WX` prošel: obnova, expirace, zachování ještě platného tokenu při dočasném výpadku, jednorázové 401, revokace, cizí zařízení, chybějící identita a vadný/duplicitní JSON. `pio run` pro ESP-IDF 6.1 prošel (16 892 B RAM, 207 441 B flash); `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 279 825 B flash). Příkazy PlatformIO byly spuštěny plnou cestou k `pio.exe`.
- Omezení: HTTPS adaptér je sestavený zdrojový komponent, ale není připojen k Wi-Fi, servisnímu backend hostu ani samostatné síťové úloze; žádné skutečné TLS spojení či fake backend E2E nebylo provedeno. Bez důvěryhodného času se ještě nesmí volat. Nahrání do desky čeká na B1. Token v RAM je softwarový model, ne hardwarové zabezpečení.
- Revize a doručení: E4.3a je v commitu `f95292799a5aedfab1a5b8f06c050f55ad263f82`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: navázat E4 Wi-Fi konfigurací, ověřením času a síťovou úlohou; poté volat token endpoint a fake backend přímo z ESP32. Lokální WSS zbývá.

### D35 – 29. 9. 2026 – E4.2b model servisní identity

- Rozsah: `DeviceIdentity` validuje verzovaný záznam s omezeným `deviceId` a 32 bajty device credential. Z prázdného úložiště vzniká stav bez identity, chybné čtení či formát zablokuje použití. Zápis model dovolí jen při současně aktivním servisním režimu a odpojené větvi pohonů; samotné fyzické vstupy zatím nejsou zapojené. Pro budoucí HTTP Authorization poskytuje credential jako 64 malých hexadecimálních znaků, které backend hashují jako UTF-8. ESP-IDF smyčka identitu čte z odděleného NVS blobu a loguje jen stav, nikoli hodnotu. Záznam se maže z dočasného pracovního bufferu.
- Ověření: v `kufr/firmware` hostitelský MSVC test `tests/device_identity_test.cpp` s `/std:c++14 /W4 /WX` prošel: fyzická softwarová brána, restart, hex formát, vadná data a selhání čtení/zápisu. `pio run` pro ESP-IDF 6.1 prošel (16 892 B RAM, 207 441 B flash), `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 279 621 B flash); oba byly spuštěny plnou cestou k `pio.exe`.
- Omezení: žádný USB servisní protokol ani zdroj bezpečné náhody/credential zatím nejsou připojené. Neexistuje fyzicky ověřený signál servisního režimu či odpojené napájecí větve (B1); zápis z běžícího firmwaru není zpřístupněn. NVS na desce ani fyzická ochrana flash se neověřovaly; šifrování/secure boot a nevratné eFuse vyžadují samostatné rozhodnutí dle plánu. Není Wi-Fi ani HTTPS klient. Nenahrávalo se.
- Revize a doručení: E4.2b je v commitu `aaa7038b47be6de23f90d3e167689cc8816e5288`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: pokračovat nezávislou softwarovou částí E4 Wi-Fi/HTTPS fake backend klienta; fyzický servisní zápis až po B1.

### D34 – 29. 9. 2026 – E4.2a trvalý journal

- Rozsah: `PersistentCommandJournal` ukládá verzovaný záznam s `commandId`, číslem přihrádky, příznakem otevřeného příkazu a maskou dostupnosti. Začátek se zapisuje před prvním výstupem; uzavření příkazu zapisuje masku i konec jedním blobem. Prázdné NVS znamená nulovou masku. Chyba čtení/zápisu a poškozený záznam blokují další řízení. `EspIdfJournalStorage` čte/zapisuje blob přes NVS a `nvs_commit`, bez automatického mazání při chybě. `espidf_main.cpp` inicializuje NVS až po bezpečném vypnutí, při chybě latchuje safety fault a při otevřeném journalu požaduje kontrolu. Arduino profil NVS adaptér vylučuje.
- Ověření: v `kufr/firmware` hostitelský MSVC test `tests/persistent_command_journal_test.cpp` s `/std:c++14 /W4 /WX` prošel; pokrývá restart, otevřený záznam, uzavření s maskou, poškozený blob, selhání čtení/zápisu a blokaci pohybu v `DeviceControl`. `pio run` pro ESP-IDF 6.1 prošel (16 892 B RAM, 206 181 B flash); `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 279 529 B flash). Příkazy PlatformIO byly spuštěny plnou cestou k `pio.exe` v uživatelském prostředí.
- Omezení: NVS nebylo čteno ani zapsáno na skutečné desce a nebyl simulován výpadek napájení během flash operace. `DeviceControl` ještě není připojený ke vstupu živé ESP-IDF smyčky; sériový Arduino prototyp jej rovněž nepoužívá. Není servisní device identita ani síťová vrstva. Nenahrávalo se do desky.
- Revize a doručení: E4.2a je v commitu `25469a0ac5a5c38bfd4a17e740f9c3eeeb857ded`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: pokračovat E4.2b servisní identitou mimo image, pak E4 Wi-Fi/HTTPS/WSS; pro upload a fyzickou akceptaci platí B1.

### D33 – 29. 9. 2026 – E4.1 sestavený ESP-IDF základ

- Převzetí a rozsah: po dokončení E7 byl převzat první E4 software slice. PlatformIO `espressif32@7.1.3` nainstalovalo `framework-espidf@4.60100.0` (ESP-IDF 6.1.0). Přibyly CMake soubory, `sdkconfig.defaults` s předběžným profilem N16R8, 16MB tabulka oddílů a `app_main` se safety portem bez GPIO a neblokující smyčkou. Generované IDF sdkconfig se ignoruje. ESP-IDF je výchozí profil; Arduino sériový profil má oddělený zdrojový filtr.
- Ověření: `pio run` prošel pro ESP-IDF 6.1.0; obraz obsahuje platný checksum/hash, 16MB flash a 80MHz režim. Výstup: 16 604 B RAM a 181 137 B flash z OTA slotu 6 553 600 B. Účinný sdkconfig potvrzuje QIO výběr (boot image začíná v DIO dle ESP-IDF), octal PSRAM a vlastní 16MB oddíly. `pio run -e esp32-s3-arduino-serial` prošel (18 660 B RAM, 279 333 B flash). Všechny tři hostitelské C++ testy (`selection_sequence`, `device_control`, `conversation_session`) prošly s MSVC `/W4 /WX`; zdroje čistého jádra se neměnily. Kopii tabulky oddílů ověřil lokální `gen_esp32part.py --flash-size 16MB`; `git diff --check` prošel. První pokusy instalace toolchainu selhaly na `HTTPClientError`, pozdější pokus nainstaloval všechny balíčky a build dokončil.
- Omezení: sestavení neověřuje boot ani PSRAM na skutečné desce, fyzické vypnutí pohonů, NVS, Wi-Fi, TLS či WSS. Nenahrávalo se. Volba generické `esp32-s3-devkitc-1` se skutečnou N16R8 revizí ověří až po B1.
- Revize a doručení: E4.1 je v commitu `cdc7f9448e2b2d656bb13417366daa0756499088`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.
- Předání: E4.1 software slice je sestavený; následuje E4.2 NVS identita a journal. Fyzické ověření až po B1.

### D32 – 29. 9. 2026 – E7.6 nákladová rezervace a E7 integrace

- Rozsah: SQLite schéma v6 přidává `cost_reservations`, sezením omezené čerpání a globální denní/měsíční agregáty v miliontinách USD. `OperationJournal.Reserve` zapisuje odhad ve stejné transakci jako operaci, turn kvótu a idempotency key. Opakovaný key neúčtuje podruhé; strop je fail-closed bez všech tří kladných konfiguračních hodnot. `SettleCost` transakčně vrací nevyužitý rozdíl nebo eviduje nadodhad a blokuje další placenou práci. Neznámé rozběhnuté volání si rezervaci ponechá. Technické cost řádky se čistí po 7 dnech, agregáty po 90 dnech. Fake trasy mají nulový odhad a nevyvolávají placený provider.
- Ověření: `dotnet build kufr/backend/backend.csproj --no-restore` bez varování; `dotnet run --project kufr/backend-tests/BackendTests.csproj` ověřil paralelní rezervace proti stropům, agregaci napříč zařízeními, retry/kolizi, vyúčtování menší i vyšší ceny, chybějící/částečnou konfiguraci, restart s `unknown` a ponechanou rezervací, osazenou v5→v6 migraci a 7/90denní retenci. `node kufr/backend/tests/smoke.mjs` ověřil reálné HTTP přístupy, souběžný stejný key, limity, všechny fake trasy, restart a nepřítomnost credential/tokenů i testovacího textu v logu. `node kufr/contracts/verify-backend-openapi.mjs` prošel.
- Omezení: částky v provozu nejsou schválené ani nakonfigurované (B4), proto žádná kladná rezervace v běžném backendu neprojde a provider se nevolá. Testy ověřují lokální loopback HTTP a fake média, nikoli fyzický ESP32, HTTPS deployment či účtování poskytovatele. Nadodhad po skutečném volání může překročit strop již vzniklým nákladem, ale je evidován a další placené požadavky se odmítnou.
- Revize a doručení: E7.6 je v commitu `011eeea045088987bda7661a4a3487778e79629f`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D31 – 29. 9. 2026 – E7.5b fake syntéza a krátkodobý WAV

- Rozsah: `POST /v1/conversations/{id}/speech` přijímá pouze UUID již dokončeného textového tahu a `outputTarget=device|phone`, vyžaduje audio souhlas a nepřijímá volný text. Fake operace vygeneruje tichý PCM16 mono 32 kHz WAV, uloží jej pouze do RAM na 120 s a vrátí device-scoped `audioId` pro `GET /v1/audio/{audioId}`. Současně je povolen jeden aktivní výstup; další lze začít po `DELETE` původní speech operace nebo po expiraci audia. Úspěšná syntéza obnovuje 15minutovou idle lhůtu do absolutního 30minutového stropu. Zrušení, ukončení sezení, expirace i periodický worker nulují a odstraní audio buffer; 410 tombstone drží pouze technickou identitu další 24 h. Po restartu se WAV negeneruje znovu.
- Ověření: `dotnet build kufr/backend/backend.csproj --no-restore` bez varování; `dotnet run --project kufr/backend-tests/BackendTests.csproj` ověřil cílové zařízení, cizí přístup, 120s expiraci/410, opětovnou syntézu po expiraci a rušení, odstranění po ukončení sezení, prodloužení idle lhůty přes původní hranici a úklid workerem bez provozu. `node kufr/backend/tests/smoke.mjs` nad HTTP hostem ověřil chybný turnId/target, cizí scope, idempotenci, souběh, WAV hlavičky/bajty, smazání audia po konci sezení a ztrátu po restartu. Velký chunked JSON požadavek byl odmítnut 413. `node kufr/contracts/verify-backend-openapi.mjs` prošel.
- Omezení: WAV obsahuje pouze krátké ticho, není to TTS ani důkaz přehrání na zařízení; bez `DELETE` klient čeká na 120s expiraci před dalším výstupem. Audio není v SQLite, po restartu staré audioId vrací 404. Placený provider a jeho nákladové limity chybí.
- Revize a doručení: E7.5b je v commitu `c76ceb30632b23f71ef07abeb2e6e6099e1b557b`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D30 – 29. 9. 2026 – E7.5a omezený WAV upload a fake přepis

- Rozsah: `POST /v1/conversations/{id}/transcriptions` přijímá pouze autentizovaný multipart `audio/wav` se souhlasem k audiu, UUID identitou tahu a shodným boot/generation. Streamovaný upload má 2MB strop požadavku a validaci RIFF PCM16 mono 32 kHz, maximálně 1 920 000 bajtů vzorků. Vlastní audio buffery se po výsledku nebo chybě vynulují. Operace vrací pevný fake přepis `deviceMicrophone`; audio ani přepis se neukládají do SQLite. Schéma v5 ukládá pouze typ operace, zachovává v4 řádky jako `turn`; retry hashuje normalizovaná pole a WAV, takže změna multipart boundary není konflikt. Fake transkripce mají samostatný limit deseti za živé sezení a nespotřebují kvótu dialogových tahů.
- Ověření: `dotnet build kufr/backend/backend.csproj --no-restore` bez varování; `dotnet run --project kufr/backend-tests/BackendTests.csproj` ověřil fake přepis, retry/kolizi, ztrátu výsledku po restartu, nepřítomnost testovacího audio payloadu v SQLite a migraci osazených schémat v1–v4 do v5. `node kufr/backend/tests/smoke.mjs` nad HTTP hostem ověřil multipart WAV nad původní 4kB JSON mezí, chybné vzorkování, překročenou délku i 2MB velikost s Content-Length a chunked přenosem, cizí device scope, souhlas, idempotenci přes jiné multipart boundary a restartové `result_expired`.
- Omezení: fake přepis nepoznává skutečnou řeč, neověřuje STT providera ani kvalitu nahrávky. Transkripce mají strop v RAM jednoho sezení; SQLite drží technickou idempotenci po restartu, kdy se staré sezení neobnovuje. Speech/audio výstup a nákladová rezervace zbývají.
- Revize a doručení: E7.5a je v commitu `5c2984ff9053bebc56bf78e872859c0966aaf460`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D29 – 29. 9. 2026 – E7.4b explicitní rušení

- Rozsah: `DELETE /v1/operations/{id}` a `DELETE /v1/conversations/{id}` vyžadují device access token, UUID `Idempotency-Key` a prázdné tělo; cizí identita je odmítnuta. SQLite schéma v4 přidává 24hodinový záznam úspěšného DELETE pro bezpečný retry po restartu. Zrušení operace potlačí publikaci výsledku v RAM; ukončení sezení odstraní jeho RAM obsah a označí jeho operace jako `cancelled`. Dokončení již zrušené rezervace nesmí přepsat status. Rezervovaný tah se nerefunduje.
- Ověření: `kufr/backend` `dotnet build backend.csproj` bez varování; z kořene `dotnet run --project kufr/backend-tests/BackendTests.csproj` ověřil vlastnictví, pozdní dokončení, restartový retry, migrace osazených v1/v2/v3 schémat a retenci páté SQLite tabulky. `node kufr/backend/tests/smoke.mjs` prošel nad HTTP hostem: cizí scope, cancellation/end, vymazaný výsledek, opakování po restartu, současný DELETE/tah a odmítnutí běžného i chunked těla DELETE.
- Omezení: fake tah se dokončuje synchronně, takže souběh skutečného provider volání nebyl ověřen; tento kód konzervativně brání pozdnímu přepsání. SQLite ponechává technické identity do 24 h kvůli idempotenci. Placené volání ani jeho nákladová rezervace zatím neexistují. Hlasové trasy zbývají.
- Revize a doručení: E7.4b je v commitu `6730b4cf9d4ecd4fddc7d5ec1d0b4598465904e8`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D28 – 29. 9. 2026 – E7.4a časovaná retence

- Rozsah: `RetentionWorker` každou minutu spouští cleanup grantů, paměťových sezení/výsledků a 24hodinových technických SQLite záznamů. `ConversationStore` nyní odstraňuje fake výsledek při 15minutovém idle timeoutu a nanejvýš po 30 minutách od vytvoření sezení; polling pak vrací `failed/result_expired` bez opakování fake/provider práce. `GET /v1/operations` také provádí konzervativní kontrolu expirace při čtení. SQLite cleanup běží v transakci; selhání sweep se loguje jen typem chyby, nikoli obsahem požadavku.
- Ověření: `kufr/backend` `dotnet build backend.csproj` bez varování; `kufr` `dotnet run --project backend-tests/BackendTests.csproj` s řízeným časem ověřil dostupnost výsledku v živém sezení, vymazání po 15 minutách, absolutní strop 30 minut i fyzické smazání všech čtyř technických tabulek po 24 hodinách. Test navíc spustil skutečný `RetentionWorker` s testovacím 1s intervalem, po posunu hodin bez klientského provozu ověřil odstranění RAM výsledku i SQLite záznamu a korektní zastavení služby. `node backend/tests/smoke.mjs` znovu prošel nad HTTP hostem.
- Omezení: explicitní DELETE/cancellation dosud není; RAM obsahu se po době životnosti odstraní reference, nikoli zaručeně přepíší všechny kopie v .NET heapu. Výsledky fake neobsahují návštěvnický text. Obnova workera po selhání jednotlivého sweep nebyla injekčně testována; kód selhání zaloguje typem a pokračuje. Nákladové limity a zbylé trasy jsou otevřené.
- Revize a doručení: E7.4a je v commitu `b403be5cbd68902b0d3313ddc1dcac1f931f79c6`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D27 – 29. 9. 2026 – E7.3c trvalá idempotence založení sezení

- Rozsah: dopředná transakční migrace SQLite na v3 přidává `conversation_creates` s device-scoped create key, SHA-256 hashem syrového těla, původní conversationId/response expiry a 24h expirací záznamu. `ConversationStore.Create` vyhodnotí retry před kontrolou živého sezení; po restartu stejný key/tělo vrátí původní identitu, jiný obsah 409. Ztracený obsah paměťového sezení se neobnovuje, staré ID nepřijme nový tah a nový rozhovor vyžaduje nový key. Technický journal neukládá text návštěvníka.
- Ověření: `kufr/backend` `dotnet build backend.csproj` bez varování; `kufr` `dotnet run --project backend-tests/BackendTests.csproj` ověřil create retry/kolizi po znovuotevření DB, migraci osazeného v1 a v2 schématu do v3, zachování kvóty a absenci testovacího textu ve všech čtyřech SQLite tabulkách. `node backend/tests/smoke.mjs` zastavil a znovu spustil skutečný HTTP proces nad stejným souborem: starý token odmítnut, původní create key vrátil stejné conversationId, změněné tělo 409, stejný UUID key jiného zařízení dostal vlastní sezení, ztracený výsledek `result_expired` a nový tah starého sezení 404.
- Omezení: create odpověď po restartu může odkazovat na již zaniklé sezení; klient musí použít nový key pro nový rozhovor. Staré RAM sezení se bezpečně neobnovuje. Nákladový rozpočet, cleanup/retence, cancellation a další HTTP trasy stále chybí.
- Revize a doručení: E7.3c je v commitu `cf7bc1c679c84fdc3abf98fab83a92ff0919843d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D26 – 29. 9. 2026 – E7.3b atomická SQLite kvóta tahů

- Rozsah: dopředná transakční migrace schématu v1→v2 přidává `conversation_turn_usage` a index expirace. `OperationJournal.Reserve` pod okamžitou transakcí kontroluje a rezervuje nejvýše deset tahů pro device/conversation zároveň s pending operací a idempotency key; shodný retry limit znovu nespotřebuje. RAM limit zůstává rychlou předběžnou kontrolou, SQLite je trvalá hranice. Nejsou zde placené výdaje; nákladová rezervace před provider voláním se musí doplnit zvlášť.
- Ověření: `kufr/backend` `dotnet build backend.csproj` bez varování; `kufr` `dotnet run --project backend-tests/BackendTests.csproj` ověřil devět rezervací a dva paralelní různé klíče na hranici desáté (právě jeden přijat), odmítnutí dalšího, shodný retry na limitu, zachování limitu po novém otevření DB a migraci osazeného v1 schématu na v2 se zachováním operationId a idempotence. `node backend/tests/smoke.mjs` nad skutečným HTTP hostem/SQLite opět prošel včetně paralelních požadavků na hranici deseti tahů.
- Omezení: založení sezení a jeho souběh jsou stále paměťové; kvóta je počet tahů, nikoli dolarový rozpočet. Cleanup je oportunní, zbytek E7 akceptace zůstává otevřený.
- Revize a doručení: E7.3b je v commitu `4dc6128658279ef7c1c909265414d8c3fc995351`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D25 – 29. 9. 2026 – E7.3a SQLite journal textového tahu

- Rozsah: připnutý `Microsoft.Data.Sqlite` 10.0.12, schéma v `OperationJournal.cs` s `turn_operations` a `turn_idempotency` (verze 1, pouze technické identity, hash těla, status, expirace). Rezervace pending operace a klíče je v jedné okamžité SQLite transakci; dokončení fake výsledku se zaznamená zvlášť, text zůstává jen v RAM. Po restartu pending/running přejde do `unknown` bez znovuspuštění; dokončený ztracený výsledek vrací `failed/result_expired`. Soubor `.instance.lock` brání druhé živé instanci a recovery cizí rozběhnuté práce. Čtení výsledku kontroluje databázovou 24h expiraci i při živé RAM cache. Databázová cesta je povinná konfigurace mimo git.
- Ověření: `kufr/backend` `dotnet build backend.csproj` bez varování a `node tests/smoke.mjs` prošly se skutečným lokálním HTTP hostem a dočasným SQLite souborem. `kufr` `dotnet run --project backend-tests/BackendTests.csproj` ověřil stejný key po restartu, konflikt různého hashe, atomický paralelní zápis, `unknown` po restartu pending rezervace, `result_expired` bez ztraceného obsahu, odmítnutí druhého vlastníka DB, 24h expiraci RAM výsledku a nepřítomnost testovacího textu v obou SQLite tabulkách. Počáteční pokus číst otevřený DB soubor jako prostý text na Windows selhal sdílením souboru; test teď ověřuje hodnoty přes SQLite spojení.
- Omezení: per-session limit deseti tahů je stále v RAM a není součástí databázové transakce; nejsou tu žádné placené výdaje ani jejich rezervace. Create idempotence je také paměťová. Expirace DB se čistí oportunně při další rezervaci; pravidelný cleanup, citlivá 15minutová retence, rušení a úplné HTTP API chybí. SQLite schema v tomto slice vzniká při startu; produkční migrace/backup a souběh více instancí nejsou přijaty. Nepoužívat pro placené tahy.
- Revize a doručení: E7.3a je v commitu `1af9e2cafb03a83b664b6ef1fcd9db6f8ecc7ed7`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D24 – 29. 9. 2026 – E7.2 fake textový tah a operace

- Rozsah: `POST /v1/conversations/{id}/turns` ověřuje access token, vlastnictví sezení, boot/generation/catalog, tvar a limit textu, šestibitovou ready masku, revizi, unikátní clientTurnId a idempotency key. Přijímá nejvýše deset tahů a vrací `202` s operationId; `GET /v1/operations/{id}` vrací device-scoped typovanou dokončenou fake operaci s korelací a pevným `ask`/`proposal:null`. Text návštěvníka se neukládá do operace ani logu. Idle expirace se obnoví nanejvýš do 30minutové absolutní délky. Po read-only review se replay klíče vyhodnocuje před kontrolou živého sezení, aby 24hodinový záznam fungoval i po jeho expiraci. Fake adapter nemá návrh přihrádky ani fyzický výstup.
- Ověření: v `kufr/backend` `dotnet build backend.csproj` prošel bez varování; `node tests/smoke.mjs` ověřil reálné HTTP přijetí/polling, cizí device scope, shodný retry/kolizi těla, duplicitní clientTurnId, korelační identitu, nepřítomnost vstupního textu ve výsledku a paralelní požadavky na hranici deseti tahů. Z `kufr` `dotnet run --project backend-tests/BackendTests.csproj` prošel bez varování: řízený čas ověřil expiraci sezení/tokenů, retry po expiraci a odmítnutí duplicitního hashe credential.
- Omezení: fake operace se dokončí synchronně a neověřuje souběh skutečných provider volání. Sezení/operace/idempotence jsou v paměti; operace se zatím nečistí a dokončený fake výsledek lze číst i po expiraci sezení. SQLite, restart, cleanup/retence, cancellation, STT/TTS a placená AI zůstávají E7/E8 prací. HTTP test je pouze loopback.
- Revize a doručení: E7.2 je v commitu `7b89c81670549a5817f6d2ef6a9495530d4ea0f6`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D23 – 29. 9. 2026 – E7.1 backend identita a fake sezení

- Rozsah: nový `kufr/backend/` s připnutým .NET 10 SDK, Minimal API trasami `POST /v1/device-access/token` a `POST /v1/conversations`. Servisně provisionovaný credential se ověřuje podle SHA-256 hashe z konfigurace mimo git; duplicitní ID/hash je odmítnut při startu. Náhodný access token platí 15 minut, v serverové paměti je uložen jen jeho hash a souběžné granty mají strop 16/zařízení. Sezení kontroluje device scope, locale, consent a tvar generační identity, vrací limity OpenAPI; jedno současné sezení na zařízení a opakování `Idempotency-Key`/stejného těla se zpracuje atomicky pod zámkem. README a `.gitignore` odpovídají novému projektu. Klient nedostává ovládací povel aktuátoru.
- Ověření: `kufr/backend`: `dotnet build backend.csproj --no-restore` prošel bez varování po počáteční opravě neexistujícího `Convert.TryFromHexString`; `node tests/smoke.mjs` spustil skutečný loopback HTTP host, ověřil dvě identity, odmítnutí chybného credential a použití credential místo access tokenu, cizí device scope, shodný retry, konflikt změněného těla, paralelní založení sezení a strop tokenů. Log procesu neobsahoval testovací credentials/tokeny. Read-only review opravila počáteční rozdíl mezi 15minutovou expirací odpovědi a 30minutovou blokací dalšího sezení; tyto dva časy jsou nyní shodné.
- Omezení: konverzace zatím neprovádí tah, STT/TTS ani placené volání; tokeny, sezení a idempotence jsou paměťové a po restartu zaniknou. Cleanup je nyní oportunní při vytváření sezení, nikoli časovač; SQLite, retence obsahu a restartové zkoušky jsou další E7 práce. HTTP test používá pouze loopback; produkční HTTPS/reverse proxy a device provisionování nebyly akceptovány. E7 zůstává rozpracovaná.
- Revize a doručení: E7.1 je v commitu `5d4760db436f64375e952c0b83637a1ad65d06e5`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D22 – 29. 9. 2026 – E2.5b browser časová osa s důvěryhodným TLS

- Rozsah: beze změny runtime kódu. Dosavadní E2.5 HTTPS pozorovací stránka dostala chybějící browser ověření. Již důvěryhodný `localhost` ASP.NET development certifikát byl exportován a převeden výhradně v paměti do testovacího procesu; soukromý klíč se neukládal do repozitáře ani do dočasného PEM souboru. Testovací bearer token existoval jen v procesu a při browser připojení.
- Ověření: `dotnet dev-certs https --check --trust` našel dva platné důvěryhodné certifikáty. In-app Browser na `https://localhost:<dočasný-port>/simulator` otevřel stránku bez TLS varování. Chybný token vyvolal `Neplatný testovací token`; správný token připojil pouze pozorovací HTTPS proud, zobrazil šest přihrádek, režim, fázi, journal a revision. Samostatný autentizovaný WSS klient vyvolal C++ výběr a browser časová osa zobrazila hledání přihrádek 1–4 i požadavek vypnout pohony. Po výpadku klienta nový snapshot ukázal `Zotavení`, `Zastaveno`, neuzavřený journal a nedostupnou přihrádku 1. V úzkém viewportu bylo 6 přihrádek a `scrollWidth = clientWidth = 375`; stránka nemá ovládání Stop/lease. Dřívější `kufr/simulator npm test` z D21 nad stejným kódem zůstává platný.
- Omezení: jednorázový browser test neprokázal dokončení celé výběrové sekvence; klient bez obnovování lease čekal na `selection.result` a skončil timeoutem, poté se simulátor konzervativně zastavil. Dokončení sekvence proti C++ modelu bylo zvlášť ověřeno v D21. Časová osa ukazuje požadavky modelu, nikoli měřený pohyb západky, NVS či skutečný hardware.
- Revize a doručení: E2.5b je v commitu `e07bf12d6547aeaeacbb6e8ed982677c19034c0b`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D21 – 29. 9. 2026 – E3.2b mobilní UI proti E2 simulátoru

- Rozsah: `SimulatorDeviceConnection.ts` připojuje mobilní web pouze přes loopback E3.2a most; rozlišuje observer/manual lease, korelovaný heartbeat preflight s kontrolou revision/RTT, šestibitovou masku, výběr/reload, ACK/výsledek, konzervativní opakování stejného příkazu, Stop a nejasný výsledek po odpojení. `main.ts` odlišuje fake/E2 režim a při výpadku označí masku jako neaktuální; `FakeDeviceConnection.ts` sdílí typ snapshotu. Nový cílený Node test, script v `package.json` a README popisují oba režimy. Bearer token zůstává jen v místním mostu.
- Ověření: `kufr/mobilni-aplikace` pod Node 24.21.0 `npm test` – 4 testy prošly (nový lease a totožný retry, chybějící výsledek, rekonciliace reloadu/nejistý výběr, změněná revision v heartbeat); `npm run build` (`tsc --noEmit && vite build`) prošel. `kufr/simulator` s existujícím MSVC `bridge.exe`, OpenSSL a Node 24.21.0 `npm test` prošel včetně TLS/auth, lease, reload, Stop, proxy a media WSS. In-app Browser na místním E3.2a mostu proti skutečnému C++ modelu: observer zablokoval povely; acquire, výběr 1, dokončení 5/6, potvrzení reloadu 6/6, Stop a odpojení zobrazily očekávané stavy; při odpojení se tlačítka zablokovala a maska se označila neaktuální. Úzký viewport 390×844 a konzole bez chyby. `git diff --check` prošel.
- Instalační omezení: běžící místní Vite proces držel `esbuild.exe`, takže `npm ci` přímo v mobilní složce skončilo Windows `EPERM`; čisté `npm ci` ze stejného `package.json`/lockfile v dočasné složce prošlo (0 známých zranitelností). Závislosti v mobilní složce byly obnoveny kopírováním bez přepisu právě používaných binárek a následný test/build prošel.
- Omezení: fake/hostitelský simulátor není fyzický kufr, iOS ani nativní TLS. Protokolový snapshot nemá korelaci `state.get`, proto preflight používá korelovaný `heartbeat.ack` s revision a aktuální masku ze snapshotu; finální hranici drží kontrola revision/lease/TTL v E2 modelu. Chybějící fyzické ověření otevření se v UI neprohlašuje za úspěch. E2.5 browser vizualizace zůstává další krok.
- Revize a doručení: E3.2b je v commitu `e5311316c49bf7a1948f9d3d1808d8d7966085ed`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D20 – 29. 9. 2026 – E3.2a místní most k E2 WSS

- Rozsah: `simulator/mobile-dev-server.mjs` servíruje sestavený mobilní web pouze na `127.0.0.1`, `/dev-mode` určuje vývojový režim, `/dev/control` předává textové E2 zprávy mezi browser WebSocket a autentizovaným TLS WSS simulátoru. Přijímá pouze shodný origin, jeden browser socket a přísně validované control zprávy; bearer token a CA jsou jen v procesu mostu. `verify-socket.mjs` přidává kontrolu statické stránky, režimu, zakázaného cizího originu a `hello` přes most. README popisuje lokální spuštění a hranice.
- Ověření: v `kufr/simulator` pod Node 24.21.0 s MSVC `bridge.exe` a místním OpenSSL `node verify-socket.mjs` i celé `npm test` prošly. Test mostu kryje cizí origin, souběžný socket, `hello`/`welcome`, skutečné acquire přes C++ model a 9000bajtový rámec odmítnutý `1009` bez pádu procesu. Před opravou most poslal textovou zprávu jako binární Buffer a E2 server ji správně odmítl `1003`; převod na UTF-8 text odstranil chybu. Upstream používá `ca` s `rejectUnauthorized:true`. Browser UI most dosud nepoužívá.
- Omezení: lokální HTTP je jen vývojová loopback cesta; pro LAN/iPhone se nesmí použít. Origin kontrola blokuje cizí web, ale místní neprohlížečový proces ji může napodobit. Není to nativní TLS pinning, skutečná komunikace ESP32 ani E3 uživatelský integrační test. Následuje E3.2b.
- Revize a doručení: E3.2a je v commitu `1ce93a98c1095ccd2baa5e17fc4ff2cf4364032d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D19 – 29. 9. 2026 – E3.1 ruční webová fake obrazovka

- Rozsah: `mobilni-aplikace/src/features/device_connection/FakeDeviceConnection.ts` drží místní observer/manual lease, šestibitovou dostupnost, běžící simulovanou sekvenci, Stop s konzervativní nedostupností rozběhnuté přihrádky a explicitní fake kontrolu. `src/main.ts`/`style.css` zobrazují šest přihrádek, stav, převzetí/uvolnění, dvojí potvrzení výběru a založení; všechny akce jsou označené jako simulace bez připojení. Skrytí stránky uvolní lease. `index.html` a mobilní README odpovídají novému UI. Žádný token, síťový transport ani fyzický aktuátor nejsou součástí stránky.
- Ověření: v `kufr/mobilni-aplikace` pod Node 24.21.0 `npm ci` (0 známých zranitelností) a `npm run build` (`tsc --noEmit && vite build`) prošly. In-app Browser na `http://127.0.0.1:5173/`: výchozí observer zablokoval všech šest tlačítek, převzetí povolilo výběr, potvrzení spustilo fake sekvenci, dokončení snížilo masku, potvrzení založení ji obnovilo, Stop během dalšího výběru snížil masku a zakázal akce; konzole neměla chyby. Prohlédnut úzký viewport 390×844.
- Omezení: fake stav se resetuje při reloadu; časování/lease nejsou odvozeny z E2 C++ modelu, síťové chyby nejsou vyzkoušené. Mobil ani iPhone nejsou připojené ke kufru. Následuje E3.2 proti simulátoru; E2.5 stále čeká browser s důvěryhodným certifikátem.
- Revize a doručení: E3.1 je v commitu `b00faeade3d11dbc8e860e3471cc71579e635dbb`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D18 – 29. 9. 2026 – E2.9b oddělený fake media socket

- Rozsah: `media-control-v1.schema.json` a `media-validator.mjs` definují přísné JSON `media.hello/open/credit/end/cancel/result` s boot/manual lease/conversation generation a přiděleným uint32 stream ID. Po acquire jde náhodný `media.authorize` ticket pouze vlastnickému control socketu, ne do observer snapshotu; bez něj druhý držitel stejného bearer tokenu media stream neotevře. `server.mjs` otevírá samostatný TLS `/v1/media`, přijímá pouze telefonní PCM16 mono 32 kHz do fake `MediaStreamVerifier`, vynucuje 4096B rámec, 16KiB credit, 19 200–1 920 000 B sudé délky, SHA-256 a 1 s bez pokroku. Spojení se zavře při Stop, odpojení/expiraci lease, změně generation, běžícím výběru nebo chybě média; řídicí socket a heartbeat zůstávají oddělené. Po neplatné úvodní odpovědi native bridge se proces uklidí místo visícího serveru. Plán kapitoly 7 a README popisují fake rozsah.
- Ověření: `kufr/contracts` pod Node 24.21.0 `npm test` — 22 platných a 39 chybných control zpráv, OpenAPI, media frame a media JSON pozitivní/negativní struktury prošly. MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` pro bridge s přidanou interní expirací lease — úspěšně. `kufr/simulator` s OpenSSL/testovací CA `npm test` — původní bridge/WSS scénáře i `/v1/media` TLS, vlastnický ticket, vyjednání, credit, validní/chybný SHA-256, mezeru chunků, timeout, cancel, heartbeat během média, uvolnění lease a Stop prošly. První lokální pokus odhalil chybný JSON výstup po doplnění expiry; opraven, opakované testy prošly. Read-only risk review upozornilo na možnost připojení druhého držitele bearer tokenu podle lease ID z observer snapshotu; doplněný ticket a negativní test tuto cestu uzavírají.
- Omezení: fake příjem PCM jen průběžně hashují, neposkytují WAV/přepis, skutečný PSRAM, audio driver, TTS/JPEG ani HTTPS backend; `simulated:true` to výslovně označuje. Browser vizuální ověření E2.5 stále čeká na důvěryhodný certifikát. Následuje proveditelná UI část E3.1 nad fake stavem, E2 zůstává částečné.
- Revize a doručení: E2.9b je v commitu `88ed1e33907a0fb236b38aab2e8b8dc3824198c3`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D17 – 29. 9. 2026 – E2.9a binární media parser

- Rozsah: `contracts/media-frame-v1.mjs` kóduje a validuje 16bajtovou `KF` hlavičku, druh média, uint32 stream/sequence/length v little-endian a payload 1–4096 B. `MediaStreamVerifier` kontroluje jediný domluvený stream, pořadí, limit 1,92 MB/2 MiB/512 KiB podle druhu, 1 s bez pokroku a na konci délku/SHA-256; po chybě zavře stream bez dalšího přijetí. Test používá binární data, změněné hlavičky/délky, nesprávný stream/pořadí/hash a timeout; nic neukládá v base64.
- Ověření: `kufr/contracts` pod Node 24.21.0 `npm test` — 21 platných a 37 chybných control zpráv, OpenAPI a nový media frame test prošly. Firmware, simulátor a hardware se při tomto čistém kontraktním kroku neměnily.
- Omezení: neexistuje media JSON handshake, `/v1/media` WSS socket, 16KiB credit, skutečný audio buffer/PSRAM, driver ani HTTP upload. Následuje E2.9b; tyto podmínky zatím nejsou ověřené.
- Revize a doručení: E2.9a je v commitu `032fc3fff1f5af24ae33d749721197fab587155d`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D16 – 29. 9. 2026 – E2.8b2 fake text/cancel přes WSS

- Rozsah: native bridge spouští stejný `ConversationSession` jako hostitelský test, do snapshotu dává zařízením založené conversation ID/generation/aktivní tah. Node WSS facade posílá validovaný UTF-8 text do bridge přes hex kódovaný interní pipe příkaz, vrací `conversation.ack` a po 300 ms fake `conversation.result` se `simulated:true`. Cancel, Stop a ztráta lease zneplatní pozdní výsledek; retry stejného cancel `messageId`/obsahu vrací duplicitní ACK bez dalšího zvýšení generation. Text ani fake odpověď nemají cestu k motoru/západce. Kontrakt v1 rozšiřuje snapshot a odpovědi; přednasazovací fixtures a README/kapitola 5 plánu zachycují přechod klienta na čtení conversation identity ze snapshotu.
- Ověření: MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` nad pěti C++ zdroji včetně `ConversationSession.cpp` pro `bridge.exe` — úspěšně. `kufr/contracts` pod Node 24.21.0 `npm test` — 21 platných a 37 chybných zpráv + OpenAPI test prošly. `kufr/simulator` s testovacím certifikátem/OpenSSL a MSVC bridge `npm test` — native bridge, deterministické cancel/late result/lease/no-actuator a WSS TLS/text/result/cancel/reload/Stop/restart scénáře prošly. `git diff --check` bez chyb.
- Omezení: fake výsledek je pevný text, bez skutečného HTTPS backendu, echo identity/revision, AI, médií nebo fyzického potvrzení. Browser vykreslení E2.5 zůstává neověřené kvůli nedůvěryhodnému dočasnému certifikátu. Následuje E2.9 média; skutečný backend E7 a hlas E9 jsou další etapy.
- Revize a doručení: E2.8b2 je v commitu `340f53fb4d8c829455b7af0cc1bef721bc592a00`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D15 – 29. 9. 2026 – E2.8b1 čistý model textového sezení

- Rozsah: `ConversationSession.h/.cpp` drží jediný aktivní textový tah, `bootId`/conversation ID/generation a nejvýše 10 jedinečných `clientTurnId` na sezení. Při přijetí kontroluje ruční lease, revision, UTF-8 a délku 1–1000 znaků / 8192 bajtů; opakovaný aktivní tah nevyvolá druhé přijetí, pozdější retry dokončeného či zrušeného ID se odmítne. Cancel a ztráta lease zvyšují generation, pozdní dokončení se zahodí. Test kryje tyto stavy, kolize ID, neplatný text a strop tahů. Model nepředává text backendu a nemá fyzický výstup.
- Ověření: MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` přes `%TEMP%\dildovander-e2-simulator\build-conversation-test.cmd` nad modelem a testem — úspěšně. `kufr/firmware`: `C:\Users\vojtech.petrik\.platformio\penv\Scripts\pio.exe run` — SUCCESS na Arduino ESP32-S3 profilu. První build před opravou C++11 konstruktoru selhal; opravená verze prošla. Read-only risk review odhalilo možnost znovupřijetí ID po dokončení; doplněna evidence ID po dobu sezení a test.
- Omezení: model ještě není napojený na WSS/native bridge ani fake backend, proto runtime `conversation.text/cancel` zůstává nepodporovaný. Volající musí před `CompleteTurn` ověřit echo identity backendu a nevytvářet fyzický povel z textu. E2.8b2 propojí model a testuje pozdní výsledek po cancel/lease loss. Browser certifikát E2.5 trvá.
- Revize a doručení: E2.8b1 je v commitu `b825e1c1b03de917df3fe30bb20bb47312547050`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D14 – 29. 9. 2026 – E2.8a textový lokální kontrakt

- Rozsah: `control-message-v1.schema.json` přidává vstupní `conversation.text/cancel` s device/boot/manual lease, conversation ID a generation; text má jednorázové clientTurnId, expectedRevision a 1–1000 znaků. `control-validator.mjs` drží běžné zprávy pod 2048 bajty a textovou výjimku do 8192 UTF-8 bajtů; WSS nastavuje stejný maximální frame, ostatní zprávy nad 2048 odmítá parser. Fixtures a testy ověřují chybějící lease/generation, nechtěná pole aktuátoru, délku textu a oba byte limity. Plán/README vysvětlují obálku a omezení.
- Ověření: `kufr/contracts` pod Node 24.21.0 `npm test` — 19 platných a 34 chybných control zpráv + OpenAPI test prošly. `kufr/simulator` s MSVC bridge/OpenSSL `npm test` — WSS/restart scénáře prošly. Firmware není tímto kontraktním krokem změněn.
- Omezení: WSS facade tyto dosud neimplementované směry odmítá; žádný rozhovor, cancel nebo AI výsledek nebyl vykonán. Následuje E2.8b vlastník fake sezení a integrační test. Browser certifikát E2.5 zůstává neověřený.
- Revize a doručení: E2.8a je v commitu `e637db2fcbbf873925416dca830aa26921384201`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D13 – 29. 9. 2026 – E2.7 potvrzení znovuzaložení

- Rozsah: `DeviceControl` přijímá `InventoryReloadCommand` pouze s platným manual lease, bootem, pořadím, revision, deadline a potvrzenou kontrolou obsluhou v bezpečném klidu. Sdílí 64výsledkovou cache a sequence s výběrem, včetně konfliktu stejného ID napříč typy. `CommandJournal::PersistReadyMask` zapisuje novou masku před ACK; fake souborový journal používá samostatný `M` záznam a rekonstruuje jej přes restart. `bridge.cpp` a `server.mjs` předávají `inventory.confirmReload` → `RELOAD` → ACK/snapshot bez spuštění sekvence. Schéma/fixtures, README a plán vymezují explicitní přihrádku a kontrolu.
- Ověření: z dočasné složky MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` nad `SafetyController.cpp`, `SelectionSequence.cpp`, `DeviceControl.cpp` s `device_control_test.cpp`, `simulator/main.cpp` a `simulator/bridge.cpp` — všechny tři cíle sestaveny, C++ test a šest CLI scénářů prošlo. `kufr/contracts`: Node 24.21.0 `npm test` — 16 platných a 29 chybných control zpráv + OpenAPI test prošly. `kufr/simulator`: `npm test` — bridge restart/perzistence a WSS reload/duplicitní ACK/Stop prošly. `kufr/firmware`: místní PlatformIO `pio run` pro Arduino profil ESP32-S3 — úspěšně. Fyzická deska ani NVS nebyly testovány.
- Omezení: fake souborový zápis používá flush, ne fyzickou atomicitu/NVS; browser certifikát E2.5 zůstává mimo důvěru prohlížeče. Čekají conversation/media zprávy. Read-only risk review nenašlo kritickou chybu, upozornilo na chybějící socket test odmítnutí reloadu při expirovaném lease nebo pending recovery; C++ test tyto hranice pokrývá částečně.
- Revize a doručení: E2.7 je v commitu `cf6ffac0394db26cec655b5380fb62cdccb1fbde`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D12 – 29. 9. 2026 – E2.6b audio a cleanup OpenAPI

- Rozsah: `contracts/backend-openapi-v1.json` nyní popisuje i multipart PCM16 mono 32 kHz WAV přepis, speech nad ID validovaného tahu, download krátkodobého `audio/wav`, rušení operace a ukončení sezení. Operation union rozlišuje transcription/turn/speech s typovanými výsledky; stav failed vyžaduje chybu, completed výsledek. Přepis předává deviceRevision pro požadované echo korelace. `verify-backend-openapi.mjs` kontroluje všech osm skupin tras, media type, auth/idempotency metadata a pozitivní/chybné struktury. Plán v kapitole 6 zaznamenává upřesněné formáty.
- Ověření z `kufr/contracts` pod Node 24.21.0: `npm test` — 15 platných a 27 chybných control zpráv a rozšířený test OpenAPI prošly. WAV bajty ani runtime auth/retence/idempotence tento test neověřuje; to patří backendové integraci E7.
- Omezení: pozdější kostýmní snímek je výslovně mimo základní v1; skutečný backend, stream parser a autorizace zatím neexistují. E2 pokračuje lokálním protokolem E2.7.
- Revize a doručení: E2.6b je v commitu `e09c4b6a513b076c0fbd425d43347e7c2007b5a8`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D11 – 29. 9. 2026 – E2.6a základ backend OpenAPI

- Rozsah: `contracts/backend-openapi-v1.json` popisuje device credential výměnu, založení sezení, textový tah a polling jeho operace; oddělená bearer schémata, UUID idempotency hlavičku, ProblemDetails, šestibitovou masku, limit 1000 znaků, stav operace, echo identit a typovaný validovaný návrh. `verify-backend-openapi.mjs` kontroluje reference, požadované trasy/security a pozitivní/chybné hodnoty. `contracts/README.md` a kapitola 6 plánu vymezují tento dílčí kontrakt.
- Ověření z `kufr/contracts` pod Node 24.21.0: `npm test` — 15 platných a 27 chybných control zpráv a test E2.6a prošly. Backend ještě neexistuje, takže scope, skutečná idempotence, kvóty a retence nebyly ověřeny v HTTP runtime.
- Omezení: dokument má zatím čtyři trasy; WAV přepis, syntéza, binární audio a rušení/úklid chybí. Otevřené detaily těchto tras se neprohlašují za hotové. Pokračuje E2.6b.
- Revize a doručení: E2.6a je v commitu `49ef3f9e283ac36738406a1280383e7d99dc4a80`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D10 – 29. 9. 2026 – E2.5 webová časová osa simulátoru

- Rozsah: `simulator/server.mjs` vystavuje na stejném loopback TLS portu stránku `/simulator` a pouze čtecí `GET /v1/observe` s bearer autorizací, nejvýše čtyřmi pozorovateli a NDJSON snapshot/událostmi ze stejného C++ modelu. `dashboard.html`/`dashboard.js` viditelně označují `SIMULACE`, zobrazují šest přihrádek, fázi, režim, journal, revizi a časovou osu. Stránka neposílá řídicí povely; token použije v HTTPS hlavičce a vymaže vstupní pole. `verify-socket.mjs` kontroluje stránku, script, odmítnutí bez tokenu, úvodní snapshot a právě jednu událost uvolnění po opakovaném příkazu. Plán v kapitole 11 zaznamenává vývojový pozorovací proud odděleně od nativního WSS kontraktu.
- Ověření z `kufr/simulator`: Node `--check` pro `server.mjs`, `dashboard.js`, `verify-socket.mjs` a Node 24.21.0 `npm test` s MSVC bridge a OpenSSL v dočasné složce — úspěšně. In-app Browser pokus o `https://127.0.0.1:8443/simulator` skončil `ERR_CERT_AUTHORITY_INVALID` na dočasném self-signed certifikátu; žádná výjimka z TLS nebyla udělena. Vykreslení, interakce a mobilní viewport proto nejsou potvrzeny.
- Omezení: `GET /v1/observe` je pouze vývojové rozhraní hostitelského procesu; neprokazuje TLS důvěru telefonu, ESP32 síť ani skutečnou mechaniku. K vizuálnímu browser ověření je třeba certifikát důvěryhodný pro testovací prohlížeč. E2 pokračuje E2.6.
- Revize a doručení: E2.5 je v commitu `11c358017a9ca1ca2e7a26d75fb5379a81df3840`; `./scripts/Push-CurrentBranch.ps1` potvrdil push `main` na `origin/main` v této revizi.

### D9 – 29. 9. 2026 – E2.4d fake journal a restart WSS

- Rozsah: `simulator/bridge.cpp` ukládá append-only fake záznamy Begin/Resolve do cesty předané z `KUFR_SIMULATOR_JOURNAL`, při startu rekonstruuje pending a masku; neúplný, chybný nebo po předchozí inicializaci ztracený log selže uzavřeně. Soubor `.lock` vynucuje jediného zapisovatele; Node čeká na skončení bridge před restartem. Po neuzavřeném záznamu nenastává automatické zotavení. Místní proces `run-server.mjs` nabízí servisní simulaci kontroly `inspected`, která přes stejný C++ model označí nejistou přihrádku jako nedostupnou. Node server vystaví fázi/fault z journalu a předá stejnou cestu přes restart. Opravena i falešná terminální událost při vypršení neaktivního lease.
- Ověření: z `%TEMP%\dildovander-e2-simulator` MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` nad `SafetyController.cpp`, `SelectionSequence.cpp`, `DeviceControl.cpp` a `simulator/bridge.cpp` s `/Fe:bridge.exe` — úspěšně. Z `kufr/simulator` pod Node 24.21.0 s `KUFR_SIMULATOR_BRIDGE` a `OPENSSL`: `npm test` — úspěšně. Testy nyní pokrývají restart s otevřeným journalem, odmítnutí takeover, servisní zotavení s maskou 39, další restart s perzistovanou maskou, souběžného zapisovatele, poškozený i ztracený log (mask 0/fault), pozdní heartbeat i odpojení během acquire. Před opravou expirace neaktivního lease WSS test odhalil chybné `selection.result` s prázdným ID; po opravě znovu prošel.
- Omezení: fake log používá flush, neověřuje fyzickou atomicitu zápisu ani výpadek napájení/NVS. Současná kontrola `.lock` byla sestavena a testována na Windows; POSIX větev ještě ne. Simulovaný `inspected` je výslovná akce obsluhy, nikoli důkaz mechanické kontroly. Následuje E2.5.
- Revize a doručení: E2.4d je v commitu `e1ecd3b0fe4ae3b0a9943126726c0b2d401eedba`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D8 – 29. 9. 2026 – E2.4c místní WSS facade

- Rozsah: `simulator/bridge.cpp` je hostitelský proces nad stejným `DeviceControl`, `SelectionSequence` a fake portem/journalem. `server.mjs` přijímá na loopback TLS/WSS jeden socket, vyžaduje bearer v handshake, 10s hello a přísný E2 parser, odmítá kompresi a control payload nad 2048 B. Převzetí, heartbeat, výběr, ACK/retry, Stop, reset a snapshot volají C++ model. Stop předbíhá běžnou frontu, zneplatní před ním přijatý reset a počet čekajících zpráv je omezený. Odpojení zruší lease i po souběžném acquire. `run-server.mjs` čte certifikát, klíč, testovací token a cestu bridge z prostředí; nic z toho není ve zdroji. `verify-bridge.mjs` a `verify-socket.mjs` testují fake čas, pozdní lease, TLS/autorizaci, handshake, jediné uvolnění po retry, Stop se zatíženou frontou včetně staršího resetu, snapshot a chybný JSON. `simulator/package.json`/lockfile připíná `ws@8.21.3` a Node 24.21.0.
- Ověření z `%TEMP%\dildovander-e2-simulator`: MSVC `cl /nologo /std:c++14 /W4 /WX /EHsc` nad `SafetyController.cpp`, `SelectionSequence.cpp`, `DeviceControl.cpp` a `simulator/bridge.cpp` s `/Fe:bridge.exe` — úspěšně. Z `kufr/simulator` po nastavení `KUFR_SIMULATOR_BRIDGE` na tento soubor, `OPENSSL` na místní OpenSSL CLI a PATH na Node 24.21.0: `npm ci`, `npm test` — obojí úspěšně; test vytváří krátkodobý certifikát mimo repo a ověřuje jej klientem. Z `kufr/contracts` pod stejným Node proběhlo `npm ci`, `npm test` — 15 platných a 27 chybných zpráv prošlo. Před opravami test odhalil duplicitně čtenou ACK ve verifieru a nesprávný exit po zavření socketu; oba opraveny, opakovaný běh prošel. `npm ci` hlásilo 0 známých zranitelností pro `ws` v tomto lokálním auditu.
- Omezení: `BridgeJournal` je dosud pouze v RAM; restart procesu nedrží pending/masku. Socket test zatím neprokazuje close/acquire závod ani přerušení během motoru, chybí OpenAPI, UI a fyzický hardware. `server.mjs` je vývojový loopback server s dočasnou identitou; nahrání do ESP32 nebo produkční provisioning nebyly provedeny. Další E2.4d výslovně řeší tuto mezeru.
- Revize a doručení: E2.4c je v commitu `8351223d6dbea2d3fc9f75033c3a5114db0f3aa0`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D7 – 29. 9. 2026 – E2.4b zprávy spojení a lease

- Rozsah: plán v kapitole 4 upřesňuje tvar `hello`/`welcome`, `state.get`, `control.acquire/release` a heartbeat, vždy s oddělením tokenu do TLS handshake. `contracts/control-message-v1.schema.json` přidává jejich přísné varianty; sedm platných fixtures a čtyři negativní mutace hlídají verzi, pole navíc, vložený snapshot a lease. Contracts README nyní uvádí rozsah schématu.
- Ověření z `kufr/contracts`: `& 'C:\Users\vojtech.petrik\AppData\Roaming\nvm\v24.21.0\npm.cmd' test` — 15 platných a 27 chybných zpráv správně rozlišeno. Schéma neověřuje identitu, pořadí handshake ani životnost lease; bez WSS testu je E2.4 stále rozpracované.
- Revize a doručení: E2.4b je v commitu `fd3d211e7f0c0a5f91f6a99339cc9827a8aebeef`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D6 – 29. 9. 2026 – E2.4a přenosný vstupní parser

- Převzetí: po doručení E2.3 v `e03672f2fe974a790b374cc1ad5ca5ea4a15451a` začala E2.4. Před WSS vstupem bylo nutné použít skutečná E2.1 pravidla z runtime modulu, nikoli je zkopírovat do síťové vrstvy.
- Rozsah: `contracts/control-validator.mjs` vyčlenil přísné UTF-8/JSON/schema ověření do exportu `validateControlMessage(bytes)`, `verify-fixtures.mjs` ho nyní používá, contracts README popisuje hranici. Žádná změna schématu ani wire kontraktu.
- Ověření z `kufr/contracts`: `& 'C:\Users\vojtech.petrik\AppData\Roaming\nvm\v24.21.0\npm.cmd' test` — 8 platných a 23 chybných zpráv správně rozlišeno. WSS adaptér ještě neexistuje, takže toto není síťová integrace; pokračovat v E2.4.
- Revize a doručení: E2.4a je v commitu `5a55716dfd661355d456cb69b58120ed39b89764`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D5 – 29. 9. 2026 – E2.3 hostitelský simulátor

- Převzetí: E2.2 bylo doručeno v `7b0fbc82996651e5e2387e269a22bb498abe806a` a krok E2.3 zapsán jako aktivní. Čistý model výběru a journalu umožnil proces bez desky.
- Rozsah: `simulator/main.cpp` spouští `DeviceControl`/`SelectionSequence` s řízeným časem, fake portem a journalem; časová osa výslovně značí `SIMULACE`. `simulator/verify-scenarios.mjs` bere ID a přihrádku ze sdílené platné fixture a ověřuje běžné dokončení, dva retry po ztraceném ACK, Stop, ztrátu lease, restart při uvolňování a chybu zápisu. Kontroluje nejvýše jedno uvolnění, vypnutí před Stop, pending journal a masku dostupnosti. `simulator/README.md` uvádí build/běh a hranice důkazu, kořenové README ukazuje nový adresář. Kontrola navíc odmítá neplatné číslo přihrádky na CLI.
- Ověření z `%TEMP%\dildovander-e2-simulator`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /std:c++14 /W4 /WX /EHsc C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\src\features\device_control\DeviceControl.cpp C:\Development\DildoVander\kufr\simulator\main.cpp /Fe:simulator.exe'` — úspěšně bez warningů. Z kořene `& 'C:\Users\vojtech.petrik\AppData\Roaming\nvm\v24.21.0\node.exe' .\kufr\simulator\verify-scenarios.mjs (Join-Path $validationDirectory 'simulator.exe')` s `$validationDirectory = Join-Path $env:TEMP 'dildovander-e2-simulator'` — šest scénářů prošlo. Neplatné `4junk` na CLI vrací exit code 2. Firmware `pio run` se neopakoval, protože se zdroje firmwaru neměnily.
- Neprovedeno: syrová fixture neprochází C++ parserem; použily se jen `commandId` a přihrádka, ostatní pole jsou sestavena podle živého modelu. Neexistuje socket, UI časová osa, skutečný NVS/TLS ani fyzická časová osa. Revize, sekvence, lease a source fixture se proto tímto testem neověřují. Po dokončení doručení E2.3 navazuje E2.4.
- Revize a doručení: E2.3 je v commitu `e03672f2fe974a790b374cc1ad5ca5ea4a15451a`; `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem.

### D4 – 29. 9. 2026 – E2.2 model příkazu a journalu

- Převzetí: E2.1 bylo doručeno v `b2e9b8d9fc2ddb362b2ae31b0a98fad4a53bd12e`; krok E2.2 byl při zahájení zapsán jako aktivní. Schéma E2.1 validuje syrové zprávy, tento krok navazuje až na strukturovaný příkaz; propojení parseru s modelem zůstává na E2.3 a další E2 slices.
- Rozsah: nové `firmware/src/features/device_control/CommandJournal.h` a `DeviceControl.h/.cpp` modelují ruční lease 6 s, `bootId`/revision/pořadí, 64 posledních výsledků, konflikt ID, zamítnutí starého příkazu a zápis journalu před prvním pohybem. Přijetí a spuštění jsou samostatné operace. Při přerušení zůstává journal otevřený; po kontrole obsluhou se označí přihrádka jako nedostupná. Dokončení musí v jedné trvalé operaci uložit novou masku a uzavřít journal. Po selhání uzavření se nesmí emitovat dokončený výsledek. `firmware/tests/device_control_test.cpp` používá fake čas, port a journal; firmware README popisuje omezení.
- Ověření z `%TEMP%\dildovander-e2-validation`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /std:c++14 /W4 /WX /EHsc C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\src\features\device_control\DeviceControl.cpp C:\Development\DildoVander\kufr\firmware\tests\device_control_test.cpp /Fe:device_control_test.exe'`, pak `.\device_control_test.exe` — úspěšně. Scénáře pokrývají ztracený ACK, stejné/změněné ID, 64 výsledků a vytěsnění, neplatnou obálku, zpožděný příkaz, auth/lease, poruchu journalu, restart po přijetí i přerušeném uvolnění, perzistovanou nedostupnost po dokončení a vypnutí při ztrátě lease.
- Firmware build z `kufr/firmware`: `& 'C:\Users\vojtech.petrik\.platformio\penv\Scripts\pio.exe' run` — **SUCCESS**, `espressif32@7.1.3`/Arduino; 18 660 B RAM a 279 157 B flash. První pokus selhal na C++11 inicializaci `CommandReceipt`; po přidání konstruktoru prošel. `DeviceControl` se překládá, ale není zapojený do `main.cpp`, takže velikost výsledné binárky není měřením jeho provozní paměti.
- Revize a doručení: E2.2 je v commitu `7b0fbc82996651e5e2387e269a22bb498abe806a`, `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem. Neprovedeno: skutečná NVS atomicita, síť/parser/TLS, hardware a společný C++/TypeScript/.NET fixture test. Fake ověření nesmí nahradit tyto integrační a fyzické kontroly.
- Předání: E2 zůstává rozpracované. Následuje E2.3 podle horní části souboru.

### D3 – 29. 9. 2026 – E2.1 první sdílené kontrakty

- Převzetí: E1.2 bylo jako softwarové jádro pushnuto na `origin/main`; E2 podle kapitoly 12 smí začít bez desky. Před změnou byl krok E2.1 zapsán jako aktivní.
- Rozsah: nové `contracts/control-message-v1.schema.json` (JSON Schema 2020-12) pro `selection.start`, `command.ack`, `selection.result`, `state.snapshot`, `safety.stop` a `safety.reset`; `contracts/fixtures/` se sdílenými platnými/chybnými zprávami; `verify-fixtures.mjs`, připnuté `package.json`/lockfile/`.nvmrc` a README. Přísný vstup odmítá neplatné UTF-8, duplicitní klíče, desetinný/exponenciální zápis celočíselných polí a zprávy nad 2048 bajtů. Schéma odmítá nadbytečná pole, chybné typy/rozsahy a vynucuje oddělené kódy poruch, bezpečnostní povely a `eventSequence` watermark snapshotu. Kořenové `kufr/README.md` ukazuje existující adresář.
- Ověření z `kufr/contracts`: s Node `24.21.0` a npm `11.19.0` podle `.nvmrc` proběhlo `npm ci` a `npm test` — **8 platných a 23 chybných zpráv správně rozlišeno**, žádná chyba. Přísné schéma se zkompilovalo; `npm ci` nahlásilo 0 známých zranitelností v instalovaných osmi balíčcích. `git diff --check` bez chyb. Neprováděl se firmware, mobilní ani CI build, protože se změnily jen kontrakty a jejich vlastní verifier.
- Revize a doručení: E2.1 je v commitu `b2e9b8d9fc2ddb362b2ae31b0a98fad4a53bd12e`, `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem. Schéma neověřuje identitu/TLS, lease, boot, čas, revision, idempotenci ani journal; tyto stavové kontroly jsou následující E2 slices. Přijímače musí přísný parser a pravidla přenést do svých adaptérů; tento test je nespojuje se skutečnou sítí.
- Předání: E2 zůstává rozpracované. Následuje E2.2 podle horní části souboru; B1/B2/B4 nebrání hostitelskému modelu.

### D2 – 29. 9. 2026 – E1.2 hostitelský timeout a ztráta řízení

- Převzetí: čistý `main` na `f03a1a8`, E1.1 v kódu odpovídalo deníku D1. Krok E1.2 byl při zahájení zapsán jako aktivní.
- Rozsah: `firmware/src/features/wand_selection/SelectionSequence.h/.cpp` rozlišuje samostatný a ruční původ výběru, při ztrátě ručního lease zastaví pouze ruční výběr a při mezeře řídicí smyčky 250 ms vyžádá Stop před dalším sériovým povelem. `firmware/src/features/safety/SafetyController.h/.cpp` uchovává poruchu do výslovného zotavení i při souběžném Stop. `src/main.cpp` provádí kontrolu mezery na začátku průchodu; firmware README popisuje hranici hostitelského modelu. Všechny výstupy zůstávají jen požadavky přes port bez GPIO.
- Hostitelské ověření z `%TEMP%\dildovander-e1-validation-final`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /std:c++14 /W4 /WX /EHsc C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\tests\selection_sequence_test.cpp /Fe:selection_sequence_test.exe'`, pak `.\selection_sequence_test.exe` — obojí úspěšně. Testy navíc pokryly výpadek lease ve všech fázích, bez vlivu na samostatný výběr, poruchu při aktivním motoru/západce, přednost Stop, zachování příznaku poruchy a timeout smyčky včetně přetečení času.
- Firmware build z `kufr/firmware`: `& 'C:\Users\vojtech.petrik\.platformio\penv\Scripts\pio.exe' run` — **SUCCESS** na PlatformIO `espressif32@7.1.3`/Arduino; 18 660 B RAM a 270 493 B flash. `git diff --check` bez chyb. Pomocné `.obj` z prvního hostitelského buildu byly odstraněny, zdroje nebyly dotčeny.
- Revize a doručení: E1.2 je v commitu `d960210bc2352c3318690e3ec470b5f8df94edb6`, `./scripts/Push-CurrentBranch.ps1` potvrdil shodu `origin/main` s tímto commitem. Nenahrávalo se do desky. Softwarová kontrola mezery vypne výstup až při návratu do smyčky; při úplném zamrznutí může pohon zůstat aktivní a napájení se odpojuje zelenou kolébkou podle D156. Tento test není důkaz skutečného fyzického vypnutí.
- Předání: softwarová kritéria E1 kapitoly 12 jsou hostitelsky doložena; další je E2.1 podle horní části souboru.

### D1 – 29. 9. 2026 – E1.1 Stop a bezpečnostní požadavky bez desky

- Převzetí: po čtení kapitol 5, 8 a 12 plánu, firmware README a živého kódu byl převzat E1.1. Původní hostitelský test prošel přes Visual Studio Build Tools; `g++` a `pio` nebyly v PATH.
- Rozsah: nové `firmware/src/features/safety/SafetyPort.h` a `SafetyController.h/.cpp`; upravené `wand_selection/SelectionSequence.h/.cpp`, `SerialSelectionPort.h/.cpp`, `src/main.cpp`, `tests/selection_sequence_test.cpp` a firmware README. Sekvence po startu i Stop vyžaduje výslovné zotavení; Stop hned volá port pro vypnutí, nepokouší se znovu zamknout západku. Safety controller odmítá souběh motoru a západky; motor je provozně 150 ms, pozdní průchod od 250 ms, západka od 500 ms a sekvence od 5 s vyvolají Stop. Sériový port pouze vypisuje požadavky, žádné GPIO.
- Hostitelské ověření z `C:\Users\vojtech.petrik\AppData\Local\Temp\kufr-e1-validation`: `cmd /c '"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /EHsc /W4 /WX /std:c++14 C:\Development\DildoVander\kufr\firmware\src\features\safety\SafetyController.cpp C:\Development\DildoVander\kufr\firmware\src\features\wand_selection\SelectionSequence.cpp C:\Development\DildoVander\kufr\firmware\tests\selection_sequence_test.cpp /Fe:selection_sequence_test.exe && selection_sequence_test.exe'` — průchod bez chyb. Testy pokrývají původní pořadí/validaci, Stop ve všech fázích, opakovaný i externí Stop, zotavení, pozdní průchod, meze 250/500/5000 ms a přetečení čítače včetně aktivního motoru/západky. Celý `src/main.cpp` a sériový adaptér navíc prošly kompilací s dočasnou hlavičkou Arduino mimo repo; tento stub není náhradou skutečného PlatformIO buildu.
- Firmware build z `kufr/firmware`: `& (Join-Path $env:TEMP 'kufr-pio-venv\Scripts\python.exe') -m platformio run` — **SUCCESS**, PlatformIO Core 6.2.0, platforma `espressif32@7.1.3`, Arduino; 18 660 B RAM a 270 217 B flash. CLI bylo nainstalováno do dočasného venv mimo repo; běžný příkaz `pio run` bez tohoto prostředí v PATH nefunguje. `.pio/` je ignorovaný výstup.
- Revize: žádný commit/push; checkout nemá HEAD a všechny původní soubory jsou untracked. Neproběhlo nahrání do desky, měření napájení/OE, ověření serva ani iPhone/CI. Softwarový Stop vydává požadavek; nepředstavuje důkaz fyzického vypnutí při zatuhlé smyčce.
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
