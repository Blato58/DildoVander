# Integrated application viewer

The maintained application viewer is now `/workshop`, implemented by Vue SFCs
in `kufr/mobilni-aplikace/src/features/workshop/`. ModelBundle loading, separate
CAD/application provenance, account-scoped observation/media and offline packaging
are documented in [ModelBundle](../../docs/model-bundle.md). Application and
offline UI builds do not regenerate CAD. The legacy renderer below remains the
compatibility path for existing manufacturing exports; its observer parsers and
headless media HAL are shared with the Vue viewer.

# F2 — ovládání 3D vieweru

Viewer je samostatný offline WebGL dokument. Neposílá příkazy do kufru,
nespouští AI a nepředstavuje fyzickou přejímku ani důkaz montovatelnosti.
Používá zdrojový [Vapour Chrome](../../design-system/README.md), bez npm
závislostí a bez síťového načítání fontů.

## Assembly Studio

Rozhraní používá sdílený Vapour Chrome: tmavé výchozí téma, přepnutí na
světlý vzhled, Inter pro ovládání a IBM Plex Mono pro identifikátory a měření.
Téma se ukládá jen lokálně; nemění materiály ani barvy fyzických součástí.
Pracovní režimy jsou v horní liště, panely a digitální dvojče v dolní liště.
Na desktopu jsou díly a detail vedle modelu. Vyhledávání **Najít díl** filtruje
název, popis a CAD ID, otevírá odpovídající skupiny a nemění viditelnost modelu.
Prázdný výsledek poskytne konkrétní pokyn. Rozměry a kabeláž zůstávají
v inspekci; vrstvy jsou sbalitelné. WebGL chyba má viditelné vysvětlení.

## Model a panely

Model vyplňuje volný prostor obrazovky. Na telefonu lišta **Díly**, **Simulace**, **Detaily**
a **Digital Twin** otevírá sbalitelné panely; otevřený panel má vlastní scroll.
V portrétu zůstává model nad panelem, v nízkém landscape vedle něj. Zavírací
tlačítko a Escape vrací klávesnicový focus na příslušnou akci lišty.
Rozložení respektuje safe areas a dynamickou výšku browseru.

- Jeden prst nebo levé tlačítko: orbit.
- Dva prsty: posun kamery a pinch zoom současně.
- Pravé tlačítko: posun; kolečko: zoom.
- Šipky při focusu canvasu: orbit; plus/minus: zoom; Home: celek.
- **Pohled**: izometrie, shora, zepředu, zezadu, zleva, zprava, celek a reset.
- **Vybrat díl** nebo Shift: inspekce, skutečné návrhové vodiče a piny.
  Tap bez tažení vybírá díl i bez zapnutí Inspect. Strom a seznam objektů
  poskytují cestu bez canvasu, také ke skrytým dílům.

Výběr ukazuje název, funkci, CAD obálku, polohu, podsestavu a montážní kroky,
pokud je payload obsahuje. **Zaměřit** plynule přiblíží výběr se zachováním
orientace. Reduced Motion přepne okamžitě. Skrýt/Zobrazit a Izolovat nemění
kabelovou topologii; **Obnovit vrstvy / izolaci** ruší ruční izolaci a skrytí.

V panelu Díly jsou režimy **Celá sestava**, **Nosná konstrukce**,
**Elektronika** (okolí průhledné) a **Odkrytý vnitřek** (skrytý korpus a kryty).
Odkrytí je vizuální pomůcka, nikoli geometrický řez. Geometrie se nemění.
Neimplementuje se umělá explozivní animace ani nepodložená dráha instalace.

Kamera omezuje zoom a posun podle obálky modelu; reset obnovuje úplný model.
Gestům se nepřidává setrvačnost: zastaví se s prstem. Plynulost je u cíleného
zaměření (240 ms). Renderer pracuje na vyžádání, DPR je nejvýše 2; změna
velikosti canvasu a browser viewportu obnoví kreslení. Pointer cancel,
ztráta capture a blur ruší aktivní gesta.

Nad modelem zůstává název skutečného směru kamery (při otáčení **Volný pohled**),
režim a počet viditelných dílů. **Pohled a ovládání** obsahuje také návod pro
dotyk, myš a klávesnici. Při změně vrstev nebo izolaci je přímo nad modelem
dostupné **Obnovit zobrazení**. Obnoví viditelnost a průhlednost výchozí pro
aktuální režim a kameru; ponechá montážní krok, kabelové trasy, rozměry i
případnou závadu po Stop. Servis proto zůstane odkrytý a montáž na stole
nepřidá korpus. Skrytý díl vybraný ze seznamu má výslovně označený skrytý stav.

## Montážní průvodce

**Montáž** otevře pokyny. Kompaktní lišta drží Předchozí, Instrukce s počítadlem
a Další i při sbalení sheetu. Seznam kroků umožňuje skok; progress ukazuje
pozici v návodu, nikoli fyzické dokončení. K dispozici je zaměření oblasti,
restart a ukončení. Počet kroků je převzatý z `assembly-guide.json`.
Nad tlačítky v dolní liště zůstává název aktuálního kroku, pracoviště, směr
a souhrn dílů i po sbalení instrukcí. Dlouhý souhrn má vlastní posun;
úplné pokyny jsou stále v panelu. Kontrolní krok ani pozice v průvodci
nepotvrzují fyzické dokončení.

Průvodce zobrazuje kumulativní fyzické osazení v kufru z `caseParts`
(s fallbackem na `parts` u starších payloadů), periwinkle zvýraznění dílů aktuálního
kroku, slovní seznam následujících dílů, instrukce, materiál, nástroje,
předchůdce, kontrolu a volitelné údaje o místě, směru, přístupu, konektorech
a demontáži. Volitelné údaje chybějící ve starším payloadu nejsou vyžadované.

`installation.kind = bench` znamená práci **na stole mimo kufr**. Viewer
v tomto kroku zobrazí kumulativní stolní podsestavu z `benchParts`.
U staršího payloadu izoluje současné díly a cíle z `installation.targets`.
`preparedParts` označuje díly pouze připravované na stole: zobrazí a zvýrazní
se v bench náhledu, ale nepřičtou se ke kumulativnímu osazení. Teprve jejich
následující instalační krok je přidá přes `caseParts` (ve starším payloadu
přes `parts`). Montážní krok vložení může převést celou stolní podsestavu
a zvýrazní všechny nově umístěné díly, nejen spojovací materiál. Elektrický bench krok bez
nových dílů nebo cílů zachová hotovou podsestavu bez korpusu. Ukazuje jejich
navržené vzájemné uspořádání; skrytý kufr a výrazný popisek brání záměně za
zasouvání do sestavy. `vertical` zobrazuje výslednou montážní polohu bez
pohybové animace. `installation.absent` jsou podmínky pro skutečný postup,
nikoli příkaz skrýt chybným pořadím osazené díly. `removal` je servisní text.

Volitelné `subassemblies` propojují díl s funkcí, instalací a demontáží.
Payload a pravidla mechanické montáže vlastní generátor; viewer je nemění.
Souřadnice, obálky a kabelové trasy zůstávají návrhové údaje.

## Simulace a hranice

Panel Simulace zachovává šest nezávislých zdvihů, sdílené sekvence, LED skupiny,
lokální tóny, deadline a zastavení se servisním zotavením. **Stop simulace**
zůstává v liště dostupný i po sbalení běžící simulace. Přepnutí režimu během
pohybu stále vyvolá bezpečný modelový Stop. Žádné ovládání nepotvrzuje polohu
skutečného serva, provoz iPhonu, moment, lepený spoj nebo bezpečnost přepravy.

## DV02 timeline a core replay

Tlačítko **DV02** otevře read-only plán ze stejného překladu jako firmware.
Volba stopy oddělí LED, vibrace, pohyb, audio a owner/deadline. Detail cue
ukazuje parametry ze zdroje; časy pohybu v plánu jsou ilustrační kalibrace.
Původní panel Simulace zůstává ilustrační renderer, nikoli autorita core.

Pro syntetické záznamy spusťte z kořene repozitáře:

```powershell
python kufr/firmware/tests/run_show_tests.py --only show_trace_replay --fixtures "$env:TEMP/dv02-replay"
```

Načtěte `show-trace-success.json` jako baseline a `show-trace-stop.json` jako
kandidáta. Soubory vznikají v C++ owneru s fake clock a porty; viewer je pouze
čte. Seek, přehrávání, pauza a rychlost mění místní seznam. Porovnání ukáže
pořadí a relativní časy řídicích akcí, sloty, masky, RGB, pulzy a důvody.
Skutečný řídicí zápis, modelový konec a měření mají rozdílnou sílu důkazu;
chybějící časy zůstanou `unknown`. Import je historický i s observer metadaty.
Mezery, drop a neshoda bundle se zobrazí samostatně. Import přijme nejvýše
1 MiB a 4096 událostí; neznámá pole a nepřesně reprezentovatelné časy odmítne.
Detaily a seznam fungují klávesnicí; Escape vrátí focus na tlačítko DV02.

Podrobnosti kontraktů a fyzických bran jsou v [DV02](../../docs/dv02.md).

## Digital Twin runtime

Panel **Digital Twin** používá stejný CAD model pro nativní simulaci a živého
ESP32 pozorovatele podle [wire kontraktu](../../contracts/digital-twin.md).
Trvalý štítek odlišuje návrhový náhled, SIMULACI, LIVE a REPLAY i při sbaleném
panelu. Živá data jsou kalibrované odhady; `unknown`/`moving` zachovají poslední
zaznamenanou polohu s průhledným dílem. Na začátku bootu je průhledná referenční
CAD poloha `unknown`, nikoli hlášení návratu. Stop nepohybuje modelem do klidu.

Soukromý `/twin` nebo lokální HTTP origin umožňuje observer/control scoped
Digital Twin session credential. Přihlášení používá vždy stejný origin.
Credential se po odeslání vymaže,
není uložen ve vieweru ani v záznamu; server vrátí HttpOnly session cookie.
Publikovaný `/workshop` pole pro credential nemá a používá pouze existující
observer session svého originu. Offline file export podporuje návrh a replay;
pro runtime otevřete soukromý `/twin`, cross-origin cookie není podporována.
Tlačítko **Načíst zdroje** čte `/v1/twin/instances`.
Výběr zdroje zruší předchozí stav a polling čte plné snapshoty každých 250 ms.
Přijetí serverem určuje age; zařízení má oddělené přesné decimal string clocks.
Po 2 s je zdroj stale, po 10 s offline; geometrie se sama dál neposouvá.

Control session smí posílat typované povely jen simulaci: syntetický text
1–6/main/short, Stop, inspect, dance, fake clock a fault. Observer session a
LIVE ani replay nemají příkazy. Chyby a capabilities zůstávají viditelné.
Přijatý `vibrationMask` s `outputsEnabled` zvýrazní odpovídající CAD `u059_n`;
jako commanded output bez fyzického třesení či tvrzení měření. Audio evidence
enqueue/cancel/voice-busy zvýrazní skutečné reproduktory/zesilovače z manifestu.
Inspector vždy odlišuje frontu/cancel/busy od slyšitelného zvuku.
Profil `deterministic` používá syntetický text a injected clock. Profil
`integration` používá GPT-Live nebo Realtime 2.1 v hlasovém panelu. Původní
`integrationText` vrací `conversation_engine_upgrade_required` ještě před
vytvořením rozhovoru či přípravou fyzického povelu; starší klient vyžaduje
aktualizaci. Deterministický syntetický text zůstává testovací vstup.
Integrační běh používá wall-clock a nemá
pauzu, krok ani změnu rychlosti. Typovaný povel `release` výslovně uvolní
výhradní vlastnictví ovládání; následující povel může o vlastnictví znovu požádat.
Profil je viditelný ve výběru zdroje, trvalém štítku, capabilities a exportu.
Návrhový přehrávač efektů je při runtime projekci pozastaven. Výběr CAD objektu
aktualizuje runtime inspector; tlačítko doložené trace události vybere příslušný
mesh a slot. Události se nepřevádějí na nové povely.

Export obsahuje nejvýše 2048 plných snapshotů / 8 MiB, jejich source versions,
trace, device clocks a server receipt. Po dosažení limitu odstraní nejstarší
snapshot a ukazuje drop counter. Import stejného formátu umožňuje seek,
přehrávání, pauzu a rychlost; rekonstruuje pouze zaznamenané snapshoty, bez
časové interpolace či síťového ovládání. Nový boot zahájí samostatnou epochu.

`twin_manifest.py` odvozuje stabilní mesh IDs, sloty, kanály PCA/LED, osy,
pivoty a vzorky kinematiky ze skutečných parameters, mechanism.cam_law a effects
layoutu. `preview.py --viewer-only` vytváří `exports/twin.manifest.json` a stejný
manifest vloží do vieweru. CAD hash je oddělený od provozní kalibrace; chybějící
nebo neodpovídající revision má trvale čitelné upozornění. Parametrové testy
spouštějí skutečný mechanism.cam_law přes AST bez CAD závislostí.
Audio mapování obsahuje pouze existující mesh IDs dodané skutečnou CAD
generací. Provenance `geometryDigest` váže revizi na úplná vygenerovaná mesh
data, kanonicky seřazená podle názvu; změna stejnojmenné geometrie změní hash.

```powershell
node --check kufr/mechanika/final/twin_viewer.js
node --test kufr/mechanika/final/test_twin_viewer.cjs
Push-Location kufr/mechanika/final
python -m unittest test_twin_manifest.py
Pop-Location
```

## Zdroj, generování a kontrola

Upravují se `viewer.js`, `viewer-template.html`, `viewer-workshop.css`, nikoli
samotný export. [preview.py](preview.py) přes `viewer_document.py` vloží CAD
payload, sdílený styl, fonty, kanonický UI runtime, `show_timeline.js` a zdrojově ověřený compiler
plán/manifest do `exports/viewer.html`. Neaktuální hashe odmítne. Indexovaný payload obsahuje
pro každý mesh sdílené `vertices` (ploché XYZ) a `triangles` (ploché indexy).
Viewer před uploadem spočítá jednotkovou normálu každé facety a rozbalí
pozice/normály do GPU bufferu; původní pole pak uvolní. Tessellace a pořadí
vrcholů se nemění. Degenerované facety s normálou menší než 1e-12 se vynechají.
Starší payload s rozbaleným `data` je podporovaný. Indexování zmenšuje export
a jeho přenos/parsing; nezmenšuje GPU buffer ani počet skutečných facet. Z kořene final ve skutečném
CAD prostředí lze spustit `python preview.py --viewer-only`; pro změny geometrie
je potřeba nejprve skutečná kompletní generace a geometrické ověření.

`viewer_ui.js` skládá režimy a akce z nativního ESM modulu
`kufr/design-system/ui.js`; Twin používá stejné renderery a timeline jeho
`bindDialog` nad nativním HTML dialogem. `viewer_runtime.py` vloží oba UI moduly
do offline HTML bez importů a síťových závislostí. Nepodporovaný import/export
odmítne. Generovaný `workshop-ui-runtime.js` je přesný fallback kanonického
runtime pro obnovu ze samostatného F2 ZIP, stejně jako CSS snapshot; neupravuje
se ručně. `viewer_style.snapshot_styles()` obnoví oba sdílené assety před
zachycením zdrojových hashů, aby pozdější render nezměnil ověřovaný snapshot.

Automatická kontrola z kořene repozitáře:

```powershell
node --check kufr/mechanika/final/viewer.js
node --test kufr/mechanika/final/test_viewer.cjs
node --check kufr/mechanika/final/show_timeline.js
node --test kufr/mechanika/final/test_show_timeline.cjs
Push-Location kufr/mechanika/final
python -m unittest test_viewer_document.py test_viewer_style.py
Pop-Location
```

Fake GPU testy ověřují reálné event handlery, stavy, matematiku kamery,
rozhraní payloadu a zachování inspekce/efektů. Neověřují vykreslený pixel,
browser layout ani skutečný dotykový hardware. Nad přegenerovaným exportem
ověřte 360 px portrét, landscape, desktop, otevření/sbalení sheetů, gesta,
klávesnici, reduced motion, čitelnost a konzoli. Reálná montáž vyžaduje prototyp.


## Rozměry skutečného kufru

Horní průchozí obdélník má potvrzené X 430 × Y 269 mm mezi výztuhami lemu.
Výztuha lemu má celkem 20 mm včetně 5mm stěny; přední klesá 40 mm a zadní
30 mm od horního lemu. Dolní profil YZ má tečné vzdálenosti 45 × 45 mm od
hlavních vnitřních stěn. Středová výztuha dna má Y 80 × Z 5 mm přes celé X 430.
Hlavní výška 114,3 mm je dříve potvrzená; dno 5 mm a víko 79,7 mm zůstávají
předpoklady. Rozhoduje plášť BREP z `chassis.shell()`, nikoli prázdný obdélník.
`safe_zone`/`safe_origin` vymezují návrhové meze a nezaručují volný kvádr.
Popisky rozměrů a inspektor tuto hranici zachovávají; pant používá skutečnou
osu z payloadu. Výkres řezu vzorkuje přímo hrany řezu stejného BREP, včetně
oblouků, výztuh lemu a předpokládaného víka. Není to výkres k výrobě kufru.


## UI náhled nad existujícím exportem

Pro kontrolu samotného rozhraní bez výpočtu CAD použijte ze složky `final/`:

```powershell
python refresh_viewer.py --source C:/cesta/k/existujicimu/exports/viewer.html --output exports/viewer-preview.html
python -m http.server 8915 --directory exports --bind 127.0.0.1
```

Otevřete `http://127.0.0.1:8915/viewer-preview.html`. Skript načte skutečný
vložený JSON z existujícího exportu a použije stejné `viewer_document.render_document`
a `viewer_style.snapshot_styles` jako `preview.py`. Původní export nepřepisuje.
Výstup je označen **UI náhled** a uvádí UTC datum souboru se vstupní geometrií.
Datum není potvrzení čerstvosti CAD. Nevytváří ověření, generation snapshot ani
výrobní ZIP; úplná regenerace a kontrola balíčku nadále vyžaduje `Regenerate.ps1`.
Náhled vyžaduje pouze Python, nepotřebuje instalovat CAD knihovny.

Když existující export není k dispozici, ovládání a offline balení lze ověřit
na malém syntetickém bloku přes tutéž cestu `render_document`:

```powershell
python preview_ui_fixture.py --output "$env:TEMP/dv-ui-framework/viewer.html"
```

Výstup trvale uvádí **SYNTETICKÁ UI FIXTURE**, nenabízí výrobní ZIP a není
modelem kufru ani CAD či fyzickým důkazem. Test `test_viewer_document.py`
ověřuje sestavený runtime v Node a obnovu z fallbacku bez sdíleného stromu.
Fixture má dva výslovně syntetické kroky pro ověření práce s instrukcemi;
nenahrazují skutečný postup z `assembly-guide.json`.
