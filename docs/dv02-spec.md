# DV02 — Unified Show Engine
## Jednotná dramaturgie efektů, deterministické časování a průkazný průběh

**Revize:** 2.0 FINAL · 13. 10. 2026
**Stav:** návrh pro implementaci; dokument nedokládá implementaci, audit aktuálního HEAD ani fyzickou akceptaci.
**Podklady:** uživatelem dodané `SPEC(8).md` a `CODEX_PROMPT(2).md`; pro hranici s postavou také relevantní části `DV01_FINAL_CODEX.md` revize 2.1.
**Rozsah:** rozšíření současného presentation/effects core. Jeden skutečný vlastník fyzických výstupů, nikoli další paralelní runtime.

---

## 0. Základní rozhodnutí

DildoVander nepotřebuje univerzální software pro divadelní techniku. Potřebuje spolehlivě převést několik autorsky připravených scén na to, co jeho konkrétní hardware opravdu umí, a stejný průběh srozumitelně ukázat při vývoji i diagnostice.

**Jeden zdroj choreografie → jeden deterministický překlad → existující vlastníci výstupů → pravdivá evidence.**

DV02 nesjednocuje všechny úlohy pod nový orchestrátor. Sjednocuje význam scén, jejich validaci, identitu a pozorovatelnost. `WandPresentation`, `DanceCoordinator`, audio a safety zůstávají vlastníky svých životních cyklů, pokud audit neprokáže, že dnes existuje jiná kanonická struktura.

### Co se oproti vstupu zpřesňuje

| Oblast | Rozhodnutí této revize |
|---|---|
| Neurčité `atMs` / `after` | Základ tvoří konečná posloupnost fází a časově omezené dekorativní cues uvnitř fáze. Začátek, konec a pozdní příchod mají přesný význam. |
| Volné `wand.lift` / `wand.lower` | Pohyb je součást validovaného úplného presentation lifecycle, nikoli obecně dostupná jednotlivá akce. |
| Plochý žebříček priorit | Priorita nerozdává vlastnictví. Rozhoduje konkrétní resource owner a explicitní admission/cleanup pravidla. |
| `Stop` jako zrušení timeru | Oddělené rušení response, neprovedeného intentu, fyzické show a globální bezpečnostní Stop; pozdní callback nemá oprávnění. |
| „Synchronizace s audiem“ | Oddělit generování, enqueue, DMA přenos a skutečné akustické měření. Bez dostupných markerů používat jen fázovou návaznost. |
| Obecná fronta scén | Žádná fronta fyzických show k pozdějšímu automatickému přehrání; busy/expiry jsou konečné výsledky konkrétní žádosti. |
| Deduplikace | Identita operace, fingerprint obsahu, boot a evidence neznámého výsledku; žádný slepý replay po restartu. |
| Viewer jako další simulátor | Viewer vykresluje plán a stopy stejného core; nevymýšlí vlastní safety ani stavový automat pohybů. |
| Nové scény současně s migrací | Nejdříve prokázat shodu obou stávajících prezentací. Nový umělecký obsah je oddělená opt-in změna. |
| Globální pomocné soubory | Operační zadání je samostatné; žádná nutná závislost na `GLOBAL_RULES.md` ani `plan.py`. |

**Převzaté požadavky:** reuse současných komponent, omezení hardware, samostatný Dance owner, žádný LLM→GPIO, verzování, timeline, restart/Stop a oddělené fyzické ověření.
**Nové návrhové volby:** fázový profil jazyka, přesná admission semantika, typy důkazů, cancellation scopes, migrační strategie, scénáře a skills. Nejde o tvrzení, že tyto mechanismy již existují.

## 1. Produktový výsledek a hranice rozsahu

### 1.1 Co má být skutečně lepší

Host vnímá jednu postavu: světlo přitáhne pozornost, krátký akcent podpoří repliku, prezentace má jasné vyvrcholení a po skončení nezůstává náhodně svítící nebo vibrující část. Efekty nemají přehlušit řeč ani vyplnit každé ticho.

Autor dokáže změnit jednu schválenou choreografii a z téhož zdroje získat firmware tabulky, plán pro simulaci a čitelný přehled změn. Obsluha pozná rozdíl mezi odmítnutou žádostí, přijatou show, dokončeným časovaným pohybem a neznámým fyzickým výsledkem.

Vývojář dokáže odpovědět: „Která verze scény běžela? Proč se tento tón nepřehrál? Kdy se zablokoval další pohyb? Je stopa úplná?“ Bez rekonstruování pravdy z volného textu několika logů.

### 1.2 Povinné jádro A–F

Audit relevantní cesty a baseline; přesný deklarativní kontrakt a sémantický validátor; deterministický překlad současných prezentací; integrace do současného owner/core; typovaný bridge scénických záměrů; read-only timeline a omezená telemetrie/replay; kompatibilita, negativní testy, provozní dokumentace a projektové skills.

Reálný ESP32, nativní iPhone, akustické měření a pohyby tvoří samostatnou bránu G. Jejich nedostupnost neblokuje ostatní autorizovanou softwarovou práci. Neověřené výstupy zůstávají označené a fyzicky nepovolené podle existujících bran.

### 1.3 Co v tomto zadání nevzniká

Žádný nový obecný workflow engine, distribuovaný event bus, druhá databáze pro historii show, nový audio mixer, nový TTS/Live adapter, změna voice/modelu, automatická generace scén modelem, runtime scripting, libovolný editor GPIO nebo cloudové ovládání serv mimo firmware.

Neimplementovat DV01 charakter, DV04 optimalizaci hlasu, DV05 offline fallback, DV08 tracking, DV10 portréty/QR ani DV11 pravděpodobnost a oprávnění Secret Events. Nepřepisovat Dance, mechaniku, kalibraci ani elektrické zapojení. Nepřidávat další snímače či watchdog jako skrytý předpoklad DV02.

## 2. Autority a audit skutečného stavu

Cesty uvedené ve vstupu jsou vodítka k auditu, nikoli důkaz existence v aktuálním checkoutu:

- `mechanika/final/effects.json`, `generate_effects.py`, `PresentationScore.h`;
- `WandPresentation`, `PhysicalPresentation`, `PhysicalEffects`, `SafetyController`;
- `DanceCoordinator`, příslušný player, SD a audio owner;
- `OperationJournal`, device journal, lokální WSS kontrakty a jejich fixtures;
- `docs/hardware-software-integration.md`, aktuální zapojení, napájení a kalibrace;
- současný simulator, viewer, design-system a případný DV01 `SceneIntent`.

Audit má být zaměřený na změnu DV02. Nemá začít opakovaným auditem celého repozitáře.

| Předmět | Autorita | Co jí není |
|---|---|---|
| Výběr hůlky a jeho podklady | Současná rozhodovací služba a validovaný výsledek | Název scény, barva nebo replika |
| Slot a dostupnost při provedení | Aktuální autorizovaný device stav a jeho revize | Starý katalogový snapshot z vieweru |
| Bezpečnost a povolení pohybu | Existující fyzický/safety vlastník | Podpis scény, úspěch JSON validace |
| Časy a barvy choreografie | Jediný schválený autorský zdroj | Ručně upravený generovaný header |
| Délka servo pohybu | Platná kalibrace příslušného slotu | Ilustrační animace CAD |
| Přehrávání a audio cleanup | Současný audio owner | Dokončení modelového generování |
| Fyzická poloha | Dostupný skutečný způsob měření, pokud existuje | ACK, časový odhad nebo CAD mesh |
| Historie nejasného povelu | Současný journal a doložená rekonsiliace | Nově otevřená session nebo znovu připojený telefon |

Současný kód popisuje realitu, ale současná chyba není výjimka z požadavků. Rozpor zaznamenat, vyřešit nejmenší adaptací a přidat regresi. Nezvyšovat fyzické limity proto, aby nová scéna prošla.

Audit zachytí i matici capability: skutečně dostupné marker události, flush/cancel audia, deduplikaci, boot identitu, kalibraci, stav po restartu, formát účinné konfigurace a limity rámců. `unknown` není `supported`.

## 3. Proveditelná architektura bez druhého runtime

### 3.1 Logické stavební bloky

**SceneDefinition** je autorský popis. **CompiledScene** je omezený deterministický plán. **ExecutionContext** obsahuje validované vazby a revize. **ExecutionProjection** popisuje průběh existujícího owneru. **TraceEvent** je neblokující diagnostické pozorování.

To nejsou povinné názvy pěti nových tříd. Mohou být rozšířením současných struktur a generátoru. Choreografická logika má být na jednom místě; nekopírovat stavový automat do C++, C# a JavaScriptu.

Překlad, parsování, načítání assetů a zápis reportů probíhají mimo kritickou řídicí cestu. Runtime pracuje s již validovanými omezenými tabulkami. Výstupy stále provádějí existující porty se svými gates.

### 3.2 Presentation a Dance nesmějí získat společného nejasného vlastníka

Prezentace hůlky může používat společný plán a cue typy přímo. Dance smí publikovat stejný formát diagnostiky a reuse vhodné čisté cue/časové primitivum. Nadále však sám streamuje hudbu, řídí svůj lifecycle a uvolňuje své zdroje.

Dlouhou skladbu nepřekládat na tisíce uložených beatových uzlů jen kvůli společnému schématu. Beatová projekce může být omezená procedurální část současného Dance, nikoli druhý obecný scene player. Shodný název cue neznamená právo zastavit cizí audio.

### 3.3 Účinný snapshot jedné show

Před přijetím zachytit `sceneId`, `sceneVersion`, `sceneHash`, `bundleHash`, `runtimeSemanticsVersion`, vybranou variantu, relevantní hardware profile a kalibraci. Slot, catalog/decision reference a safety revize mají známý původ.

Pravidla scény se za běhu nemění. Zpřísnění safety a ztráta oprávnění se uplatní ihned. Běžná změna katalogu scén platí až pro další spuštění. Změna kalibrace/pin profilu vyžaduje klidový servisní postup; nepřepisuje právě běžící pohyb.

## 4. Jazyk scén: omezený fázový profil

### 4.1 Proč ne obecný časový graf

Ve vstupním příkladu není řečeno, zda `after: light` čeká na zápis RGB, zhasnutí, nebo konec fáze. Servo by tak dvě implementace mohly spustit v jiný okamžik.

Pro první verzi volíme **konečnou lineární páteř fází a omezené paralelní dekorace uvnitř fáze**. To vyjadřuje hledání, pulzy, zdvih, vystavení a návrat bez obecného runtime DAG interpretu. Případný už existující bohatší formát lze zachovat, pokud se bezeztrátově přeloží do stejné jednoznačné sémantiky a nezvýší pravomoci.

Autorské makro lze rozvinout při build-time; firmware nedostává smyčky, dynamické výrazy ani vnořené spouštění scén. Závislost na audiu se řeší před přijetím fyzické show nebo dostupným lokálním dekorativním markerem, ne čekáním na cloud se zvednutou přihrádkou.

### 4.2 Identita a základní pole

`schemaVersion` označuje formát, `sceneVersion` konkrétní obsahovou revizi. Revize tohoto dokumentu není verze wire protokolu. Stabilní `sceneId` nesmí potichu změnit fyzický význam. Číslo `schemaVersion: 1` v ilustračním příkladu není pokyn předefinovat případný již existující produkční v1 kontrakt: při kolizi musí vzniknout nová verze a explicitní migrace.

Minimální definice obsahuje identitu, `kind: accent|presentation`, `maxDurationMs`, známý resource policy profil, seznam capability požadavků a neprázdné `phases`. Formát odlišuje povinné zdroje od volitelných dekorací. Žádná deklarace nesmí tvrdit méně práv, než skutečně vyžadují její operace; validátor je odvodí a porovná.

`variant` je schválené jméno, nikoli libovolná sada parametrů. V první dodávce zachovat běžnou a krátkou prezentaci jako dvě pojmenované varianty stejného katalogu.

### 4.3 Fáze a její konec

Každá fáze má jedinečné ID a `completion` právě jednoho druhu:

- `elapsed`: pevná nenulová doba; nezávisí na tom, zda se volitelný tón skutečně přehrál.
- `calibrated-move`: pouze v presentation scéně; směr `raise` nebo `lower`, target vždy aktuální validovaná volba. Konec určuje existující kalibrovaný driver/lifecycle, nikoli model.

Fáze začínají v pořadí deklarace po skutečném dokončení předchozí fáze podle modelu owneru. Není dovoleno odvozovat konec servo fáze z přijetí požadavku. `calibrated-move` znamená dokončení povelu podle kalibrace, ne změřenou polohu.

Časové intervaly jsou polouzavřené `[start, end)`. Na hranici se nejprve vyhodnotí safety/deadline a ukončení starých výstupů, potom se teprve povolí nové. Žádný přechod nesmí byť na jednu iteraci porušit souběh aktuátorů.

### 4.4 Dekorativní cues

Pro první verzi stačí uzavřená sada `led.solid`, `led.fade`, `vibration.pulse`, `audio.tone`. Další existující typ lze reuse, ale žádný nový typ bez významu, limitů a negativních testů.

Každý cue má ID, offset od začátku fáze, konec `phase-end` nebo konečnou dobu, důležitost, reakci na nedostupný zdroj a politiku zpoždění. Povinná pole nemají různě interpretované implicitní defaulty.

LED může běžet do konce fáze i při kalibrovaném zdvihu; jeho délka se tak odvozuje z téže fáze. Nemá zůstat latched do náhodného příštího zápisu. Zvuk a vibrace mají vždy vlastní konečnou délku; nedostupné audio nesmí zablokovat celou časovou osu.

Cue dokončený okamžitým nastavením driveru není „fyzicky dokončený efekt“. Pole přijetí, řídicího zahájení a řídicího ukončení mají odlišný význam.

### 4.5 Výběr cílové skupiny

Pohyb používá výhradně vazbu `selection`. Dekorativní scan smí použít pevné sloty z ověřeného rozsahu nebo whitelistovaný relativní vztah převzatý z dnešní choreografie. Žádné expression eval, modulo výraz od modelu ani neověřený index.

Mapping hůlka→slot je součást ExecutionContext. Dlouhodobá identita hůlky není číslo přihrádky. Scéna nesmí sama rozhodnout, která hůlka patří hostovi.

### 4.6 Ilustrační úplný příklad

Následující JSON vysvětluje nový formát. **Není golden baseline současné krátké prezentace, kalibrace ani schválený profil k nahrání.** Čísla u `example-reveal` slouží jen k výkladu; implementace nejdříve migruje skutečný autorský zdroj beze změny jeho chování.

```json
{
  "schemaVersion": 1,
  "sceneId": "example-reveal",
  "sceneVersion": "1.0.0",
  "kind": "presentation",
  "maxDurationMs": 15000,
  "resourcePolicy": "existing-presentation",
  "requires": ["validated-selection", "calibrated-motion", "profile-b"],
  "phases": [
    {
      "id": "choice",
      "completion": { "kind": "elapsed", "durationMs": 400 },
      "effects": [
        {
          "id": "choice-light",
          "kind": "led.solid",
          "group": { "binding": "selection" },
          "rgb": [180, 135, 35],
          "level": 64,
          "timing": { "offsetMs": 0, "end": { "kind": "phase-end" } },
          "importance": "optional",
          "onUnavailable": "skip",
          "latePolicy": "sample-current"
        }
      ]
    },
    {
      "id": "rise",
      "completion": { "kind": "calibrated-move", "direction": "raise", "target": "selection" },
      "effects": []
    },
    {
      "id": "display",
      "completion": { "kind": "elapsed", "durationMs": 1500 },
      "effects": []
    },
    {
      "id": "return",
      "completion": { "kind": "calibrated-move", "direction": "lower", "target": "selection" },
      "effects": []
    },
    {
      "id": "rest",
      "completion": { "kind": "elapsed", "durationMs": 250 },
      "effects": []
    }
  ]
}
```

### 4.7 Syntaktická a sémantická validace jsou různé kontroly

Použít explicitně zvolený a připnutý JSON Schema dialect podporovaný toolchainem. Uzavřít objektové varianty, ověřit typy, povinná pole, rozsahy a správnou větev unionu. Při kombinování schémat hlídat skutečný význam `additionalProperties` versus `unevaluatedProperties`; uzavřenost se nesmí ztratit uvnitř `$ref`/kombinace. [S3]

Nad tím sémantický validátor ověřuje zdroje, pořadí a životní cyklus pohybu, omezení kalibrace, deadline, kolize a dostupnost capability. JSON Schema samo tyto vlastnosti nedokazuje.

Parser musí odmítnout duplicitní klíče, neplatné UTF-8, nefinite čísla, překročené velikosti a hloubku ještě před neomezenou alokací. Integrované časy a kanály nepřijímat jako řetězce ani numerické hodnoty tiše zaokrouhlené do jiného významu. Rovnocenné celočíselné JSON reprezentace mají jednu výslovnou normalizaci.

## 5. Překlad a jedna autorita choreografie

### 5.1 Kanonický zdroj

Preferovat rozšíření dnešního `effects.json` a generátoru. Pokud je technicky vhodnější katalog ve více zdrojových souborech, původní cesta se stane jednoznačným odkazem nebo generovaným kompatibilním výstupem. **Nesmějí existovat dvě ručně upravované kopie týchž časů a barev.**

Architektura „nové scenes plus ručně udržovaný starý header pro jistotu“ není přijatelná. Zamražená testovací baseline je historický důkaz, nikoli druhý autorský zdroj.

### 5.2 Deterministický pipeline

Omezený parse → strukturální validace → normalizace → rozvinutí schválených build-time maker → sémantická kontrola → odvození resource envelope → překlad do statických tabulek a čitelného IR → manifest a exports.

Generátor má režim ověření bez zápisu. Stejné vstupní bajty, uzavřená množina závislostí a verze překladače dávají stejné relevantní výstupy. Výsledek nesmí záviset na pořadí filesystemu, absolutní cestě, timezone nebo aktuálním datu v payloadu.

Lidské build metadata může obsahovat datum odděleně; nepatří do hashe, který má označovat deterministický program.

### 5.3 Hash a identita důkazu

Manifest obsahuje minimálně hash přesných vstupů, schématu/semantiky překladače, relevantního profile kontraktu a vygenerovaného plánu. Použít kanonickou serializaci podporovanou projektem nebo přesně specifikované UTF-8 bajty; nepředpokládat univerzální canonical JSON.

Rozlišit `sourceHash`, `compiledHash` a `bundleHash`. Žádný self-referential checksum. Runtime kalibrace se identifikuje samostatnou revizí/hash v ExecutionContext; není automaticky shodná s ilustrační kalibrací exportu.

Hash je identita, nikoli oprávnění. Důvěryhodnost dává schválený zdroj a existující důvěryhodný build/distribuční řetězec. Nový online downloader ani správu podpisových klíčů DV02 neimplementuje.

### 5.4 Parametry, varianty a autorský diff

Runtime smí dosadit jen validovaný slot, platnou kalibraci a schválenou variantu. Nevystavovat rozhraní pro libovolnou délku pohybu, jas nebo PWM z telefonu či backendu.

Sdílené fragmenty rozvíjet jednosměrně při build-time. Diff pro review má ukázat změněné fáze, časy, LED skupiny, nejvyšší jas, audio požadavky, délku vibrací a nejhorší délku prezentace. Nejen změnu binárního hashe.

## 6. Statické invarianty fyzické prezentace

Povinné kontroly před vznikem artefaktu i runtime revalidace před použitím:

1. Accent nemá žádný pohyb. Presentation má právě jeden povolený zdvih a jeden normální návrat téže validované volby; aktuální legacy odlišnost musí mít explicitní mapování.
2. Žádný návrat před zdvihem, dvojitý zdvih, druhá hůlka nebo vynechaný návrat při normálním dokončení. Stop/fault není „normální dokončení“ a návrat nespouští.
3. Zdvih i návrat mají platné mezní kalibrace a spadají do stejné fyzické operace. Nelze je samostatně vydat přes SceneIntent.
4. Servo a vibrace dodržují současný vzájemný zákaz; rezervace aktuátorové větve pokrývá i odpovídající čekání a cleanup.
5. Svítí nejvýše povolený počet LED skupin a jejich výsledný jas nepřekročí aktuální profil. Deklarovaná RGB barva neobchází omezení intenzity.
6. Každý tón/pulz má konec a každá latched vlastnost má deterministické vypnutí nebo schválené nahrazení ve vlastněné vrstvě.
7. Bounded počet fází/cues a omezené rozvinutí maker. Neexistuje smyčka čekající na další modelovou odpověď.
8. Celá sekvence včetně přípravy, přechodů, obou pohybů a úklidu musí být proveditelná před platným hard deadlinem.
9. Chybějící povinná capability blokuje spuštění. Chybějící volitelný tón může vytvořit výslovně omezený, nikoli skrytě plnohodnotný průběh.
10. Statická přejímka nenahrazuje mechanické, elektrické, akustické nebo stack/heap měření.

Pokud audit potvrdí dosavadní hranice 15 s pro prezentaci, 150 ms pro vibrační pulz a 64/255 pro počáteční jas, zachovat je. Tato revize je sama nesmí prohlásit za aktuální měřená fakta ani vytvořit druhý konfigurační zdroj. Použít skutečné kanonické hodnoty a jejich provenienci.

## 7. Časová sémantika a zpožděná řídicí smyčka

### 7.1 Tři odlišné časové domény

**Device monotonic** řídí fyzickou sekvenci a deadline. **Audio sample timeline** popisuje konkrétní stream/playback generaci. **Wall clock** slouží pouze lidskému auditu. Doby mezi zařízeními se neodečítají bez známého mapování a nejistoty.

ESP Timer měří lokální čas od inicializace a po deep sleep začíná znovu; rozlišení timeru nezaručuje přesné spuštění callbacku. Čas proto vážeme na boot a zpoždění měříme zvlášť. [S1]

Nespoléhat na browser časovač jako na fyzickou autoritu. Změna systémového času ani timezone nesmí posunout aktivní show.

### 7.2 Pevný deadline

Pro fyzickou prezentaci používat existující původ hard deadlinu. Pokud je od přijetí příkazu, nesmí nový formát začít počítat až od prvního zdvihu nebo prvního audio markeru.

Logický vztah:

`deadline = deviceAcceptedAt + min(sceneLimit, applicableOwnerLimit)`

Všechny členy mají stejnou lokální doménu a overflow-safe reprezentaci. Rezervace a příprava po acceptance spotřebovávají tentýž čas. Přechod fáze, reconnect ani opakovaná žádost deadline neobnoví.

Nejhorší proveditelná délka zahrnuje pevné fáze, maximální povolené servo kalibrace, kvantizaci skutečné smyčky, handoff a cleanup rezervu. Nejde jen o součet pěkných čísel ze storyboardu. Před novým pohybem se ověří zbývající čas pro jeho schválené dokončení a potřebný zbytek sekvence. Nedostatek se nesmí řešit zrychlením serva.

### 7.3 Offset, délka a vynechání

`offsetMs` cue je od skutečného začátku jeho fáze. Začátek fáze se eviduje jednou a neposouvá se kvůli pozdnímu callbacku dekorace. Offset po konci fáze je neplatný, nikoli „zařadit na další příležitost“.

U pevně dlouhého pulzu je absolutní plánovaný konec od původního due time. Pozdní spuštění nesmí posunout konec o celou délku a prodloužit fyzické působení. Pulz pod minimem použitelnosti se vynechá podle schválené politiky; nejde o novou kompenzační vibraci.

### 7.4 Výslovné late policies

| Druh | Přípustný výchozí postup | Zakázaný postup |
|---|---|---|
| LED stav/fade | Dosadit stav pro aktuální čas, jen pokud interval stále trvá | Přehrát všechny zmeškané snímky |
| Krátký dekorativní tón | Přeskočit při prošlém okně; logovat důvod | Přidat jej na konec cizího audia |
| Vibrační pulz | Přeskočit po vypršení, případně pouze zbývající schválený interval | Dohnat několik pulzů najednou |
| Začátek pohybu | Pouze při aktuální platné fázi, gate a dostatečné rezervě; jinak zastavit/odmítnout | Rychle vykonat zmeškaný raise i lower |
| Běžící pohyb | Ukončování řídí existující fyzický owner a hard limit | Dodatečně prodloužit pohyb kvůli scheduleru |
| Povinné řízení/cleanup | Nevynechat; neproveditelnost je fault se skutečným stavem | Označit scénu za completed bez vypnutí |

Výpočet zpoždění musí používat stejnou doménu jako due time. Žádné odčítání serverových milisekund od času ESP.

### 7.5 Jedna iterace není doháněcí smyčka

Zpracování jedné iterace je omezené. Nejprve safety a revokace, potom deadline a vypnutí prošlých výstupů, pak relevantní vstupní události a nejvýše povolený počet přechodů. Konkrétní počet převzít z baseline a doložit; návrh nesmí přidat neomezené `while (overdue)`.

Více logických změn ve stejném čase se řadí deterministicky. Globální Stop má přednost před novým startem. Ukončení staré vlastněné větve předchází zapnutí nové. Shodné timestampy nerozhoduje náhodné pořadí callbacků.

Při delší pauze řídicí úlohy je dovoleno přejít do fault, ne do „rychle přehraj zbytek“. Zastavený procesor nemůže vykonat software Stop; DV02 neslibuje nezávislou ochranu hardware, kterou sestava nemá.

## 8. Vlastnictví zdrojů a admission

### 8.1 Priorita není preemption oprávnění

Vstupní priority mají být převedeny na skutečné konfliktní vztahy. Bezpečnostní Stop a hard fault mohou výstupy zablokovat podle současného safety kontraktu. Běžná prezentace nesmí zabrat audio tím, že neoprávněně zastaví dialog; servis nesmí sám předběhnout aktivní pohyb jen svým názvem.

Minimální resource mapa rozliší aktuátorovou větev, LED výstup, audio TX a případné SD/I/O zdroje. Nevytvářet nový obecný lock manager, pokud totéž už vlastní aktuální runtime.

### 8.2 Konfliktní matice

| Žadatel | Držitel | Rozhodnutí |
|---|---|---|
| Nová prezentace | Jiná presentation/manual/motion operace | `busy`, žádná fronta ani implicitní převzetí |
| Dekorativní tón prezentace | Aktivní řeč | Skip/mute podle původní policy; žádný cancel hlasu |
| Dialogue accent | Dialogue owner | Připojit do jeho dovolené dekorativní vrstvy, pouze pokud existuje ověřený port |
| Accent | Cizí presentation nebo Dance | `busy`/`skipped`, nikdy změna jeho LED/audio |
| Nová presentation | Dance | Odmítnout; explicitní ukončení Dance je oddělená uživatelská operace |
| Dance start | Aktivní dialog/prezentace/maintenance | Zachovat dosavadní odmítnutí a scope |
| Ambient požadavek | Kterákoli aktivní činnost | Zahodit, neodložit na konec |
| OTA/servis | Aktivní show | Zachovat idle/maintenance admission; nic neukrást |
| Safety Stop | Kdokoli | Existující globální zablokování, nikoli běžná resource soutěž |

Pokud nynější runtime kombinaci dialogue+LED vůbec neumí, DV02 ji nesmí označit za hotovou fake úspěchem. Buď vznikne malý ověřený port do téhož owneru, nebo je konkrétní accent capability nepodporovaná. Nesmí kvůli ní vzniknout druhý effects task.

### 8.3 Rezervace a vlastnický token

Povinná množina zdrojů se přijme atomicky vůči existujícímu řídicímu kontextu, nebo se celé přijetí odmítne. Při částečném selhání rezervace uvolnit pouze skutečně získané prostředky. Žádná větev nesmí držet servo a čekat na síťový zvuk.

Každé uvolnění/cancel ověřuje identitu vlastníka a generaci. Pozdní cleanup show A nesmí vypnout nebo uvolnit audio show B, i když používají stejné fyzické zařízení. Vlastnický token je technická korelace, nikoli nový uživatelský credential.

Optional audio se pokouší získat jen v časovém okně cue; nedostupnost se jednou zaznamená. Nevytvářet nekonečný retry ani per-tick spam. Mandatory audio-only scéna bez audia není úspěšná tichá scéna, pokud takový produktový profil nebyl výslovně navržen.

### 8.4 Admission a liveness

Každá rezervace má konečný čas, rollback a vlastnickou odpovědnost. Příprava nesmí blokovat safety input nebo podmínit jeho zpracování odpovědí jiného workeru. Logování, WSS subscriber ani připojený viewer nesmějí držet lease potřebný k dokončení show.

## 9. SceneIntent a kompatibilita s DV01

### 9.1 Úzký bridge

DV01 používá whitelistované záměry `wake`, `consider`, `choice_accent`, `farewell`. DV02 jim poskytne skutečnou, ale omezenou implementační cestu. Záměr určuje co má být podpořeno, nikoli přesný elektrický průběh.

Logický envelope obsahuje neprůhledné ID intentu, typ, session/epochu, response či decision vazbu podle druhu, platnost, phase reference a cílovou boot/device identitu podle existujícího protokolu. Převzít současné ID/revize; nevytvářet jejich druhou sadu.

Backend/Live nesmějí poslat JSON celé scény, barvy, úhly, PWM, libovolný slot nebo prodloužený deadline. Mapování intent→schválená scéna je v aplikačním/firmware katalogu a respektuje capability.

### 9.2 Zvláštní pravidlo pro `choice_accent`

`choice_accent` **není alias pro nový reveal**. Při připraveném výběru smí připravit či připojit dovolený dekorativní akcent ke stejné prezentaci. Samotný fyzický start stále prochází jediným existujícím selection/presentation příkazem.

Pokud přítomnost DV01 i běžné selection cesty vyvolá dvě žádosti, korelace musí zajistit jednu fyzickou operaci. Duplicitní decoration se zpracuje jako duplicitní či odmítnutá; nesmí vytvořit druhý `WandPresentation`.

### 9.3 Freshness a různé hodiny

Platnost se kontroluje při přijetí intentu i těsně před efektem. Správná session a response generace jsou nutné; stará odpověď nesmí spustit nový akcent.

TTL se nesmí při každém opakovaném doručení restartovat. Reuse existujícího freshness kontraktu; pokud je device deadline v lokálním čase, musí nést správný boot. Relativní TTL začínající až při příjmu sama nedokazuje, že zpráva neležela dlouho v síti. Bez společných hodin se opřít o aktuální generaci, fázi, platný marker a protokolovou revizi; nejistý časově citlivý intent odmítnout.

Při čekání na lokální playback marker je intent omezený a ještě není přijatou fyzickou show. Nemá držet motion resources. Marker jiné response, po přerušení nebo po expiry se ignoruje. Po vypršení se nespustí „hned, jak se to povede“.

### 9.4 Bez DV01

DV02 musí fungovat s existující selection cestou a syntetickými/operátorskými testy bridge. Není-li DV01 implementované, doložit `dv01_integration = not_present`, nikoli zavést nový CharacterDirector. Kontrakt a testy přítomnosti/nepřítomnosti mají být hotové; chybějící upstream nemá blokovat celý DV02.

## 10. Lifecycle, výsledky a síla důkazů

### 10.1 Jeden skutečný lifecycle

Mapovat současný owner na logické fáze `idle → reserved → preparing → playing → completing → terminal`. Nevyžadovat paralelní FSM s těmito doslovnými názvy. `cancel_requested` je požadavek čekající na potvrzení, nikoli důkaz zastavení.

Výsledek má oddělené osy:

| Osa | Příklady významu |
|---|---|
| Stav žádosti | rejected / accepted / duplicate / unknown |
| Terminální průběh | completed / cancelled / stopped / failed |
| Kvalita prezentace | full / reduced / none |
| Fyzická evidence | not_started / commanded / timed_completion / externally_measured / unknown |
| Zotavení | inspection_required / normal_gate_state |

To lze uložit současnými poli a reason codes. Důležité je nemíchat `requires_inspection` do statusu tak, že se ztratí skutečná příčina, ani nepoužívat `completed` jako tvrzení změřeného zdvihu.

### 10.2 Přijetí, spuštění a dokončení

`requested` znamená přijetí požadavku příslušnou transportní vrstvou. `accepted` vznikne až po příslušné admission a zápisu nezbytné korelace. `started` znamená skutečný začátek vlastněného vykonávání. `completed` nastane až po povinném cleanup a uvolnění zdrojů.

Jestli existující protokol má jiný význam ACK, dokumentovat ho a nepřejmenovat jej ve vieweru na silnější důkaz. Transportní ACK samo nesmí odstartovat dependent fyzickou fázi.

Po ukončení session smí pozdní výsledek původní operace upřesnit její journal. Nesmí oživit starou session, audio, osobní kotvy nebo nové cues.

### 10.3 Konečnost a cleanup

Každá accepted show musí skončit terminálním výsledkem nebo explicitní nejistotou s obnovovacím postupem. Uvolnění zdrojů je součást dohledatelného průběhu. Selhání cleanup se nesmí změnit na běžné `completed`.

Běžně úspěšná prezentace obnovuje dostupnost podle existujícího lifecycle. Po Stop/fault se neuvolní provozní brána jen proto, že timer doběhl. Nepotvrzený fyzický stav zůstává nepřijatý až do stanovené kontroly.

## 11. Přerušení, cancellation a Stop

### 11.1 Scope tabulka

| Událost | Povinný účinek | Co se automaticky nestane |
|---|---|---|
| Zrušení dosud neprovedeného intentu | Invalidace ID/generace, žádný pozdější cue | Žádný pohyb ani globální audio Stop |
| Barge-in / cancel response | Zrušit jen dekorace a audio příslušné response v příslušném owneru | Nezrušit celou session ani nezávislou přijatou presentation |
| Revokace evidence před acceptance | Zneplatnit závislé ready intent/presentation žádosti | Nevydat novou hůlku starým příkazem |
| Ukončení session | Žádné další session-scoped efekty; fyzická operace se řeší původním operation lifecycle | Nezapomenout rozpracovaný fyzický povel |
| Explicitní show cancel | Požádat skutečného vlastníka o podporované ukončení; výsledek potvrdit | Neprovést automatický servo návrat jako kompenzaci |
| Safety Stop / hard fault | Současné zablokování výstupů, zákaz nových cues, příslušný cleanup/inspection | Žádné outro, error zvuk ani restart show |
| Odpojení pozorovatele | Žádný zásah do autonomní show, pokud tak stanoví dnešní owner policy | Nezastavit cizí Dance nebo hlas kvůli subscriberu |

Jestli explicitní show cancel po prvním fyzickém pohybu současná implementace podporuje pouze jako safety Stop, zachovat to. DV02 nemá zavádět „jemný automatický návrat“.

### 11.2 Cancel není jen odregistrace timeru

Invalidovat odpovídající generaci, zablokovat nové enqueue, zrušit příslušná čekání a zamezit provedení pozdních callbacks. Každý callback těsně před zápisem ověřuje dosud platnou autoritu svého owneru.

Běžné stop API ESP Timer nemusí přerušit již rozběhnutý callback. Nutná je kontrola vlastníka/generace, nikoli jen úspěch stop API. Kritickou smyčku neblokovat čekáním na callback. [S1]

### 11.3 Hard stop výstupů a fyzická nejistota

Stop cesta má přednost před generátorem, telemetrií, storage i audio workerem. Provést stávající výstupní inhibit/disable; žádné nové fyzické zapojení se tímto dokumentem nepředpokládá.

Naměřené časy rozlišují přijetí Stop, zákaz nového plánování, zápis výstupního disable, doběh audia a skutečné ztichnutí/zastavení pozorované při testu. Již předané DMA nebo mechanická setrvačnost se nesmějí označit za nulové jen podle return hodnoty.

## 12. Deduplikace, restart a neznámý výsledek

### 12.1 Identita operace

Reuse existujícího `commandId/operationId`, cílového bootu a relevantních revizí. Scene identity obsahuje hash, nikoli jen textový název. Před prvním nevratným krokem musí existovat dostatečná deduplikační evidence podle současného journalu.

Stejné ID a stejný fingerprint vrací původní stav/výsledek bez dalších efektů. Stejné ID s jiným obsahem je konflikt, nikoli aktualizace běžící operace. Nové ID nemůže obejít pravidlo jedné prezentace pro stejné selection/presentation oprávnění.

### 12.2 Exactly-once se neslibuje

Cílem je nevykonat duplicitní operaci v podporované oblasti deduplikace a neudělat automatický druhý pokus při nejistotě. Síťový timeout neprokazuje, že příkaz nebyl přijat.

Není-li po restartu dostatečná durable evidence, zvolit konzervativní blokaci a rekonsiliaci, nikoli deklaraci exactly-once. Nepřidávat slepý retry se stejným nebo novým ID.

### 12.3 Crash okna

Povinně pokrýt restart před admission, po rezervaci, po záznamu ale před výstupem, po prvním pohybu, po fyzickém dokončení ale před ACK a během cleanup. Journal a jeho recovery musí odpovídat skutečné implementaci; nepředstírat atomickou transakci mezi SQLite, ESP flash a servem.

Po bootu se žádná scéna automaticky nedohraje. Nevydává se návrat serva jen proto, že záznam končí fází `raise`. Známý terminal výsledek lze zobrazit; neznámý zůstává oddělený a postupuje podle současných recovery bran.

Vypršení TTL či odstranění diagnostické stopy nesmí zpětně změnit nejasný fyzický povel na oprávnění k novému pokusu. Tombstone/dedupe retenci řeší současný journal. Při naplnění jeho kapacity je přípustné další start odmítnout, nikoli potichu smazat ochranu.

## 13. Audio: od generování k tomu, co je skutečně slyšet

### 13.1 Čtyři různé okamžiky

`generation_finished`, `pcm_enqueued`, `dma_sent` a `audible_end_measured` nejsou zaměnitelné. Cue „po replice“ se nesmí odvozovat od ukončení síťového generování, pokud část audia ještě čeká.

I2S driver pracuje s DMA a poskytuje příslušné události přenosu. Jejich význam je přenos bufferu, nikoli měření zvuku v místnosti. Blokující zápis/read a práci s buffery držet mimo řídicí tick a těžkou práci neprovádět v ISR callbacku. [S2]

### 13.2 Capability profily synchronizace

| Profil | Co smí tvrdit | Co nesmí slibovat |
|---|---|---|
| phase-only | Scéna navazuje na potvrzenou aplikační fázi | Přesnou synchronizaci s konkrétním slovem |
| enqueue-proxy | Čas je proxy podle předaných PCM dat | Že zvuk v této chvíli opustil reproduktor |
| playback/DMA-aware | Přesnější lokální timeline s doloženým bufferovým zpožděním | Nulovou akustickou odchylku bez měření |
| externally-measured | Konkrétně změřenou odchylku v dané konfiguraci | Platnost pro všechny hlasitosti, firmware a mechanické sestavy |

Schopnost zvolit podle skutečného adapteru, ne podle přání ve specifikaci. Chybějící synchronizace neospravedlňuje nový audio subsystém v DV02.

### 13.3 Vzorkový čas a marker vazba

PCM pozice je celočíselný počet frames/samples v přesně definovaném streamu a sample rate. Pro přepočet používat overflow-safe racionální výpočet, ne opakované přičítání zaokrouhlených milisekund. Reset či znovuvytvoření streamu má novou generaci.

Marker musí patřit ke stejné response, stream generaci a owneru jako čekající cue. Přepis textu, regex nad replikou nebo počet znaků nejsou playback marker.

Audio wait před fyzickou presentation má konečný limit a neblokuje prostředky. Po acceptance fyzického pohybu se nemá čekat na nová cloudová data. Žádná umělá dlouhá režijní pauza neprodlužuje hard deadline.

### 13.4 Bez směšování dvou audio systémů

DV02 nepřidává mixing, ducking ani automatické pozastavení a pokračování Dance. Pokud některá schopnost již existuje a má testovaný owner kontrakt, lze ji znovu použít. Jinak tón ustoupí nebo se audio-only akcent odmítne.

Selhání volitelného tónu může dát `quality=reduced`. Selhání společného audio zařízení nebo nejasný stav owneru se musí promítnout do skutečné fault politiky, ne převést na neškodný skip.

## 14. Dance adapter a zachování dnešní show

Dance zůstává samostatný existující audio/SD owner. DV02 nesmí změnit výběr skladeb, jejich formát, repeat pravidla, sample rate, ovládání z telefonu nebo chování při odpojení, pokud to není explicitně schválená oprava konkrétní vady.

Adaptace zahrnuje společné pojmenování resource/cue událostí, capability a časové provenance. Beat není důvod ovládat servo. Oprávnění Dance k LED/vibracím zůstávají v původním profilu.

Je-li tempo konstantní, beat index vychází z absolutní pozice v audio timeline, ne z opakovaného přičítání zaokrouhlené periody. Přesná formule a reprezentace BPM musí odpovídat existujícímu formátu; DV02 ho bez důvodu nemigruje. Testy dlouhých syntetických skladeb ověří drift a reset generace.

Při vynechaných beatech se nevyrobí fronta vibračních pulzů. Při pauze nebo poruše audio timeline se postupuje podle původního Dance kontraktu; nezavádět automatické resume po bootu. Historii dlouhé skladby agregovat nebo omezit; neposílat každou změnu LED jako neomezený WSS stream.

## 15. Režijní katalog: malé množství čitelných scén

Nejprve převést existující běžnou a krátkou prezentaci. Nové akcenty přidat odděleně a vypnuté, dokud nejsou ověřené. Níže uvedené délky jsou **návrhové autorské cíle**, nikoli nové limity skutečného zařízení.

| Scéna | Účel a zamýšlený tvar | Brány a výchozí stav |
|---|---|---|
| `wake` | Jeden krátký světelný nádech; pozornost bez hlučného startupu. Přibližně 0,3–0,8 s. | Bez pohybu/vibrací; start hosta nebo oprávněný intent; volitelný tón nesmí přebít řeč. |
| `consider` | Jeden nenápadný přechod mezi povolenými světelnými body; žádný nekonečný „thinking“ loop. | Omezený jednorázový akcent; nesmí předstírat již vybranou hůlku. |
| `wand-reveal` | Převzatá plná prezentace, od přípravy po návrat a klid. | Jediná skutečná selection operace, všechna motion gates, stávající deadline. |
| `wand-reveal` / short | Převzatá zkrácená varianta, stejný bezpečnostní a návratový kontrakt. | Short nesmí zkrátit kalibrovaný pohyb nebo recovery. |
| `farewell` | Krátké zhasnutí či schválený světelný závěr. | Jen při běžném dovoleném uzavření; nikdy po Stop, expirované session nebo zákazu dalšího výstupu. |
| `ambient` | Jednorázový velmi slabý světelný moment. | Katalogová možnost bez autonomního plánovače; aktivace/scheduling mimo povinné DV02. |
| `error` | Srozumitelný neinvazivní technický akcent pouze při dovoleném provozním stavu. | Po hard Stop pouze UI/diagnostika; žádný „bezpečnostní“ tón přes zákaz výstupů. |
| `special-referral` | Krátké světelné rozlišení zvláštního předání. | Bez serva a nového slotu; upstream DV11 musí dodat platný výsledek. Bez DV11 jen neaktivní katalog/fixture. |
| `dance` projekce | Časová reprezentace současné hudební show. | Není běžná nová presentation scéna; adapter zachovává původního vlastníka. |

**Scéna není trigger.** Přítomnost `ambient` či `special-referral` v katalogu sama nezpůsobuje spuštění, losování ani nový rozhovor. Reálnou událost typu „pošli hosta za Ollivanderem pro speciální hůlku“ vybírá DV11; DV02 ji nevymýšlí a neinterpretuje libovolnou repliku jako oprávnění.

### 15.1 Autorská zásada: jeden hlavní vrchol

Úvodní akcent má být slabší než reveal. Během hostovy odpovědi se efekty nezesilují jen proto, že trvá déle. Pauza nemusí mít zvuk. V jednom malém kufru nemá každý subsystém současně soutěžit o pozornost.

Každá nová scéna má krátký scénický záměr, podmínky, seznam použitých výstupů, ukončení a fallback. Kvalita neznamená větší jas, delší vibrace nebo více pohybů.

## 16. Výstupní sémantika a konzistence vzhledu

### 16.1 LED barva a intenzita

Oddělit autorskou barvu, autorskou intenzitu a aktuální fyzický strop. Je nutné přesně říct, zda vstupní RGB už obsahuje snížení jasu; jinak může být aplikováno dvakrát nebo vůbec.

Preferovaná nová normalizace pro nové scény: RGB je odstín v rozsahu 0–255, `level` je požadovaná intenzita v témže rozsahu a účinná intenzita je minimum autorské hodnoty a všech platných stropů. Převod na driver má jednu deterministickou integer formulaci a známé zaokrouhlení. **Převod legacy scén však musí nejdříve zachovat jejich skutečný současný výstup**; matematickou změnu prezentovat jako samostatnou vizuální revizi, ne jako technickou migraci.

Žádné sčítání jasů nezávislých vrstev nad strop. Při zákazu současných skupin se více skupin odmítá, nikoli pouze ztlumí. Fade nesmí na hranici zaokrouhlením přetéci a blink nesmí vzniknout nechtěným náhradním vynulováním mezi dvěma navazujícími stavy.

### 16.2 Vibrace a servo

Pulz vibrace není volně prodlužovatelný fallback. Délka a opakovací interval se kontrolují i po rozvinutí maker. Po přerušení se nedohání zmeškané impulzy.

Servo nedostává úhel ani pulz z authoring externího vstupu. Schválená operace předá target a platný kalibrační profil existujícímu driveru. Způsob deaktivace signálu/napájení po pohybu zůstává podle aktuální fyzické konfigurace; žádný nový předpoklad, že softwarové vypnutí znamená odpojené V+.

### 16.3 Tóny

Tón má explicitní identitu, délku a dovolené vlastnictví. Nepřidávat libovolné cesty k souborům, URL nebo generování řeči. Nové tóny se hodnotí poslechem na kufru; hlasitostní a frekvenční návrh bez měření není akustická akceptace.

## 17. Telemetrie: pravda i při ztracených událostech

### 17.1 Události a jejich význam

Použít existující diagnostickou cestu. Minimum: žádost/admission/rejection, phase entered/exited, cue planned/skipped/commanded/ended, owner acquire/release, deadline/fault/Stop, terminal outcome. Neměřit každou PWM periodu ani každý LED frame.

Událost má event schema verzi, zdroj, boot, sekvenční číslo, execution identitu, scene/bundle hash, phase/cue ID, důvod a časovou doménu. K fyzické operaci náleží síla důkazu. Osobní texty, raw audio, fotografie, tokeny a přepisy nejsou součástí trace.

`dueAt` je plán. `acceptedAt` je potvrzené přijetí portem. `commandedAt` je doložený řídicí zápis. `endedAt` je konec odpovídajícího modelu/portu. `measuredAt` existuje jen s konkrétním měřicím podkladem. Chybějící pole zůstává chybějící; nevypočítává se tak, aby vizualizace vypadala kompletní.

### 17.2 Ztráty a backpressure

Trace má omezený ring buffer a jasnou overflow politiku. Pomalý odběratel nesmí zablokovat řídicí smyčku. Při vynechání událostí zvýšit drop counter a oznámit gap se známým intervalem sequence IDs, pokud ho lze určit.

Aktuální owner/safety snapshot musí jít rekonstruovat nezávisle na úplnosti detailního trace. Kritická idempotence evidence není v ztrátovém diagnostickém bufferu. Pokud chybí trace, report je neúplný, ne automaticky bezchybný.

Export i fan-out probíhají mimo tick; žádné filesystem flush, formátování dlouhého JSON nebo síťové čekání ve fyzickém driveru. Chyba diagnostiky sama nevytvoří nový pohyb ani nový audio output.

### 17.3 Retence

Nevytvářet novou produkční databázi kvůli DV02. Použít současnou krátkodobou technickou retenci a oprávnění; případný export je syntetický nebo schválený redigovaný incident. Scene trace není obsahová paměť hosta.

ID se nesmějí tvářit jako anonymní, pokud je lze jinde spojit se session. Nezařazovat reálné provozní traces s tajemstvími do veřejných testovacích fixtures.

## 18. Timeline, simulace a replay

### 18.1 Tři jasně odlišené režimy

**Plán** ukazuje zamýšlenou scénu včetně ilustrační kalibrace. **Replay** ukazuje zaznamenaný běh a mezery. **Živé pozorování** zobrazuje dostupný stav připojeného zařízení. Žádný z nich není automaticky fyzický ovladač.

Viewer má trvalý štítek režimu, scene/bundle verzi, runtime profil, kalibraci nebo `unknown` a sílu důkazů. Historický soubor nesmí vypadat jako aktuálně připojený kufr.

### 18.2 Jedna časová sémantika

Preferovaný simulátor spouští stejné čisté C++ core s fake clock a porty. Viewer načítá jeho plán/trace a vykresluje; nemá druhou ručně napsanou implementaci deadline a servo FSM.

Je-li browser interaktivní simulace nutná a společné core tam nyní nelze použít, rozlišit její ilustrační renderer od autoritativního host replay. Nezavádět velký nový WASM toolchain jen kvůli tomuto dokumentu. Paritní testy mohou porovnávat generovaný plán a reprezentativní trace fixtures.

### 18.3 Povinné prvky pohledu

Přehled fází; oddělené tracks LED, vibrací, pohybu, audia a owner stavů; plánované versus doložené časy; důvod vynechaného cue; deadline; Stop a přenosové mezery. Klik na cue ukáže parametry, vazbu na autoritu a dostupný důkaz, ne osobní obsah hosta.

Neznámá poloha se zobrazí jako neznámá. Lze navíc ukázat odhad modelu, ale s viditelným označením. Nepohybuje-li se telemetry mesh kvůli chybějícím datům, nesmí to vypadat jako potvrzený nulový pohyb.

Na mobilu nevyžadovat přesné klikání do úzkého canvasu; seznam cue událostí musí být použitelnou alternativou. Horizontální scroll smí mít timeline, ne celá stránka. Respektovat existující design-system, reduced motion a přístupnost. Není potřeba nový samostatný dashboard.

### 18.4 Čistě read-only replay

Seek, opakování a změna rychlosti přehrávání se týkají pouze vizualizace. Importovaný trace nesmí obsahovat akční odkazy, HTML skripty ani způsob, jak znovu vydat fyzické povely. Exportovaný incident není podepsaná scéna ani credential.

Syntetický replay má vstupní události, fake clock, konfiguraci, calibration fixture, seed jen pokud se používá, očekávané invarianty a verzi core. Tentýž záznam dává stejný core výsledek; nepožaduje se shodná živá modelová řeč.

### 18.5 Porovnání baseline a kandidáta

Porovnávat uspořádanou stopu významných řídicích akcí, trvání/hranice, cílové sloty, normalizovaný LED výstup, pulzy, návrat, vlastnictví a Stop. Diferenční report vysvětlí změnu v lidských pojmech.

Golden očekávání nesmí vzniknout pouze z téhož nového generátoru, jehož chybu mají odhalit. Použít zachycené legacy stopy a nezávisle ručně odvozené malé případy. Aktualizace golden není automatická oprava červeného testu.

## 19. Paměť, výkon a determinismus

### 19.1 Ohraničení návrhu

Žádné neohraničené fronty, dynamické rozšiřování kolekcí za běhu nebo parsování JSON při pohybu. Statické definice patří do vhodného immutable úložiště; runtime drží jen potřebný index/stav. Nepřidávat druhou task pouze proto, že dokument používá slovo Engine.

Počáteční **testovací návrhový profil**, který agent ověří a případně před implementací přizpůsobí doložené baseline:

| Položka | Výchozí návrhový strop | Význam |
|---|---:|---|
| Fáze jedné krátké scény po expanzi | 64 | Není určeno pro rozvinutí celé Dance skladby |
| Cues jedné krátké scény po expanzi | 256 | Zahrnuje vygenerované opakování |
| Cues jedné fáze | 8 | Jejich fyzická souběžnost je dál omezena profilem |
| Hloubka vstupního JSON | 16 | Ochrana parseru, nikoli runtime rekurze |
| Zdrojový JSON pro jeden katalogový vstup | 256 KiB | Parser kontroluje velikost před alokací |
| Detailní trace ring | 128 událostí | Finite buffer; velikost záznamu změřit |

Tyto hodnoty nejsou naměřené schopnosti ESP. Menší současný bezpečný limit má přednost. Nutnou odchylku od návrhového stropu uvést s dopadem, ne zvyšovat čísla během testu tak dlouho, až všechno projde. Žádný z těchto softwarových stropů nepovoluje vyšší proud, delší pohyb nebo nový hardware.

### 19.2 Runtime budget

Audit zaznamená RAM/flash delta, velikosti stavových struktur a bufferů, call stack a maximální počet operací jednoho ticku. Pro pure core je návrhový cíl režie pod 10 % existující periody řídicí smyčky na skutečném cíli při největší povolené scéně; nejde o prokázaný real-time bound.

Bez fyzického měření uvést host benchmark a jeho prostředí, nikoli „ESP limit splněn“. Stack odhad z build výstupu je jiný důkaz než high-water při dlouhé show. Nepřidávat velké lokální buffery do callbacku nebo již zatížené úlohy.

Espressif doporučuje krátké timer callbacky. [S1] DV02 nemění timer API ani task priority bez konkrétního důvodu a regresních testů.

### 19.3 Přesná matematika

Časové konverze jsou checked, s definovaným roundingem a reprezentací napříč C++/JS. Velká `uint64` čísla se při exportu nesmějí tiše poškodit JavaScript `Number`; použít bezpečný rozsah/relativní čas nebo konzistentní textovou reprezentaci.

Testovat nulové/mezní délky, overflow při převodu ms→µs a frame→čas, monotonic discontinuity, shodné timestampy, přesný deadline a zpoždění o jeden tick. Nezavádět náhodnost do fyzického scheduleru kvůli „živějšímu“ dojmu.

## 20. Kompatibilita, zapnutí a rollback

### 20.1 Staré a nové komponenty

Nový klient se starým firmware nesmí posílat neznámý fyzický cue. Starý klient s novým firmware musí zachovat stávající výběr, Stop a pozorování. Rozšíření zpráv musí respektovat současné přísné validátory; nepočítat automaticky s tím, že cizí pole ignorují.

Capability není důkaz kalibrace. Client UI může zobrazit nepodporovanou funkci, ale nemá obcházet gate. Starší firmware se neaktualizuje sám proto, že otevřel nový viewer.

### 20.2 Zapnutí bez souběhu dvou playerů

Použít existující konfigurační mechanismus. Režim vykonávání se vybírá pro nové operace, ne během pohybu. V jednom okamžiku je pro konkrétní fyzickou operaci právě jedna účinná cesta.

Migraci tabulek lze ověřovat paralelně pouze offline/shadow bez fyzických side effects. „Pro jistotu spusť nový i starý a porovnej“ je v živém režimu zakázané.

Nové dramaturgické scény jsou defaultně vypnuté. Běžná a krátká legacy prezentace po migraci zachovávají pozorovatelné chování; změny obsahu se reviewují zvlášť.

### 20.3 Rollback

Běžný rollback se projeví až v klidovém stavu pro nové operace; aktivní show nemění schéma pod rukama. Nouzové zastavení vždy přes existující Stop.

Pokud je ke skutečnému rollbacku nutný návrat firmware, připravit pouze artefakt/postup v mezích oprávnění. Neprovádět OTA, flash, release nebo deployment z tohoto návrhového úkolu. Vypnutí DV02 nesmí ztratit journal, pending outcome nebo fyzickou recovery bránu.

### 20.4 Změna katalogu a důvěryhodnost

Zkompilované scény přicházejí se schváleným firmware/build balíkem. Runtime příjem neověřených scén po WSS/HTTP není součástí DV02. Podpora budoucích podepsaných balíků neznamená, že ji nyní agent musí implementovat.

Poškozený hash/manifest nebo nepodporovaná verze odmítnou dotčenou cestu; nelze přepnout na podobně nazvanou scénu bez identity důkazu. Import autorských souborů v nástrojích je omezený na povolené lokální zdroje, bez path traversal a automatického síťového načítání.

## 21. Implementační etapy a volba pořadí iniciativ

DV02 je samostatná iniciativa. Uživatel smí implementovat, odložit nebo přeskočit ostatní DV položky. Číslování není příkaz implementovat DV01 jako závislost ani automaticky pokračovat DV03.

Uvnitř DV02 dávají smysl tyto závislé slices:

| Slice | Konkrétní dodávka | Důkaz dokončení |
|---|---|---|
| A · Reality map a baseline | Vlastníci, protokol, současné normální/short stopy, runtime a audio capability, zdrojová identita | Ověřené zdroje nebo explicitní unknown; žádný údaj jen z předchozího chatu |
| B · Jazyk a migrace | Přísné schema, sémantický validátor, deterministický compiler, obě legacy choreografie | Parita stop, negativní fixtures, hash/manifests a check režim |
| C · Vertikální integrace | Existující owner skutečně použije compiled plán; jediné admission, Stop, resource cleanup | Jeden úplný syntetický selection→presentation→terminal průchod, včetně selhání |
| D · Bridge a kompatibilita | SceneIntent adapter, cancellation scopes, Dance diagnostická projekce, starý/nový protokol | Žádný druhý reveal, žádný cizí audio Stop, žádné skryté závislosti na DV01/11 |
| E · Pozorování a replay | Omezený trace, čitelný timeline, gap handling, baseline/candidate diff | Read-only import, společná sémantika, přístupná mobilní alternativa |
| F · Dokončení softwaru | Katalog/opt-in, regrese, výkonové evidence, dokumentace, skills, rollout/rollback | Requirements→evidence mapa a přesné otevřené brány |
| G · Externí akceptace | Skutečný ESP/audio/iPhone/provoz, jen s platným oprávněním | Naměřené výsledky na identifikované sestavě; jinak blocked/not-run |
| H · Volitelná budoucnost | Runtime podepsané scene packy, pokročilý editor, větší synchronizace | Mimo toto zadání; pouze krátké backlog body, je-li to užitečné |

Agent má dokončit všechny proveditelné části A–F. Slices slouží pro malé ověřené diffy, ne jako výmluva skončit po schématu. Externě blokovaná G neblokuje offline jádro. Chybějící autorizace pro flash není neaplikovatelnost fyzických testů.

Pokud by jednotlivý požadavek potřeboval velký přepis sousední iniciativy, uvést přesnou mezeru a dokončit zbytek. Nelze potichu zaměnit nehotovou integraci za úspěšný no-op.

## 22. Akceptační požadavky

**MUST** je povinná vlastnost příslušné dodávky. **SHOULD** je kvalitativní cíl s doloženou výjimkou. **GATE** musí být splněn před označením odpovídajícího živého profilu za přijatý; jeho nedostupnost není důvod předstírat úspěch. **LATER** zůstává mimo běžný implementační rozsah.

Následující ID používají prefix DV02, aby nekolidovala s R/T z DV01.

| ID | Úroveň | Požadavek | Přiměřený důkaz |
|---|---|---|---|
| DV02-R01 | MUST | Jeden autoritativní zdroj choreografických časů/barvy | Mapa zdrojů a check generátoru |
| DV02-R02 | MUST | Žádný druhý vlastník fyzického runtime | Integrační mapa a review volání portů |
| DV02-R03 | MUST | Běžná i krátká legacy varianta zachovají observable semantics | Zachycené baseline trace a diff |
| DV02-R04 | MUST | Schema má uzavřené varianty, správné typy a bounded parse | Pozitivní i negativní corpus |
| DV02-R05 | MUST | Fáze/cues mají jednoznačný začátek, konec a dependency význam | Časové boundary fixtures |
| DV02-R06 | MUST | Presentation lifecycle nelze rozložit na svévolné vnější raise/lower | Validátor a adversarial integration |
| DV02-R07 | MUST | Servo/vibrace/LED/jas nepřekročí současné fyzické limity | Statická kontrola + výstupní guard test |
| DV02-R08 | MUST | Hard deadline zahrne přípravu i cleanup a nikdy se neobnovuje | Fake clock a reconnect/retry testy |
| DV02-R09 | MUST | Zmeškané pulzy a pohyby se nedohánějí | Stall/jitter replay |
| DV02-R10 | MUST | Resource priority nekrade cizí vlastnictví | Konfliktní matice adapter testů |
| DV02-R11 | MUST | Rezervace je all-or-nothing a nezůstává viset | Selhání každé acquisition fáze |
| DV02-R12 | MUST | Starý owner nesmí uvolnit zdroj nového owneru | Generační a ABA race test |
| DV02-R13 | MUST | Optional audio nespouští cancel řeči/Dance | Audio fault a contention regrese |
| DV02-R14 | MUST | Dance zachová lifecycle, Stop a disconnected chování | Stávající a nové Dance regrese |
| DV02-R15 | MUST | Audio časy nezaměňují generování/enqueue/DMA/zvuk | Typy událostí a capability report |
| DV02-R16 | MUST | Fyzická show nečeká se zdvihem na cloudový marker | Zpožděný/chybějící marker test |
| DV02-R17 | MUST | SceneIntent neobsahuje libovolné výstupy nebo vykonatelný kód | Contract/adversarial fixtures |
| DV02-R18 | MUST | Choice accent nikdy nevyrobí druhý reveal | Dvojitá cesta DV01/selection test |
| DV02-R19 | MUST | Freshness a revize platí při admission i provedení | Epoch/TTL/response fixtures |
| DV02-R20 | MUST | Cancellation má scope a nevrací zrušenou response do života | Response/session/show Stop závody |
| DV02-R21 | MUST | Safety Stop má přednost a žádný automatický návrat/outro | Stop ve všech phase rodinách |
| DV02-R22 | MUST | Pozdní callback po Stop nemá právo k výstupu | In-flight callback test |
| DV02-R23 | MUST | Dedupe odliší opakování od konfliktu obsahu | ID/fingerprint/journal testy |
| DV02-R24 | MUST | Restart/unknown nikdy automaticky neopakuje fyzickou show | Crash-window corpus |
| DV02-R25 | MUST | ACK, timed completion a měření nejsou totéž | Stavové i UI fixtures |
| DV02-R26 | MUST | Cleanup je součást completion a nevynuluje inspection gate | Failure/recovery test |
| DV02-R27 | MUST | Compile a export jsou deterministické a zdrojově svázané | Dvojí čistý build + manifest kontrola |
| DV02-R28 | MUST | Kalibrace/profil mají explicitní identitu a platnost | Mismatch a změna konfigurace test |
| DV02-R29 | MUST | Replay je read-only, ne cestou k hardware | Import boundary/adversarial test |
| DV02-R30 | MUST | Viewer neudržuje druhou autoritativní fyzickou sémantiku | Core/trace parity a review |
| DV02-R31 | MUST | Chybějící telemetry neznačí úspěch a neblokuje tick | Gap/drop/backpressure test |
| DV02-R32 | MUST | Časy nesměšují boots/domény a neničí se konverzí | Clock/overflow/serialization test |
| DV02-R33 | MUST | Kritická cesta nemá nové neomezené alokace nebo I/O | Call-path audit + bounded stress |
| DV02-R34 | MUST | Source/runtime/trace mají tvrdé velikostní limity | Max/max+1 a parser fuzz corpus |
| DV02-R35 | MUST | Starý i nový klient/firmware mají pravdivou kompatibilitu | Capability/version matrix |
| DV02-R36 | MUST | Rollback nemění běžící show ani nesmaže nejistotu | Config/operation/journal replay |
| DV02-R37 | MUST | Nové scény jsou explicitní opt-in, nikoli automatické triggery | Disabled/startup/idle test |
| DV02-R38 | MUST | DV01/11/08 nepřítomnost nevede k falešnému úspěchu | Present/absent capability test |
| DV02-R39 | MUST | Trace/logy neobsahují raw obsah hosta ani tajemství | Redaction/schema/fixture audit |
| DV02-R40 | MUST | Skills jsou součást skutečné dodávky | Trigger/link/dry-run důkazy |
| DV02-R41 | MUST | Všechny relevantní scénáře mají mapu na test nebo gate | Coverage manifest |
| DV02-R42 | MUST | Model, hlas, hardware a ostatní iniciativy se nemění bokem | Scope review a baseline config |
| DV02-R43 | SHOULD | Přidání scény nevyžaduje kopírování timingů mezi komponentami | Průchod autorským workflow |
| DV02-R44 | SHOULD | Timeline je čitelná na mobilu a bez canvas-only ovládání | UI/browser + keyboard review |
| DV02-R45 | GATE | Režie ticku, heap a stack přijaty na skutečném ESP profilu | Zátěžové měření a build identity |
| DV02-R46 | GATE | Audio/LED/pohyb a Stop ověřeny na identifikované sestavě | Fyzický protokol a naměřené prodlevy |
| DV02-R47 | GATE | Event-ready je konkrétní přijatá kombinace konfigurace a verzí | Release acceptance record |
| DV02-R48 | LATER | Runtime scene packy, autonomní ambient scheduler a pokročilý editor | Oddělená iniciativa/rozšíření |

Kritické invarianty nevyvažuje průměrná synchronizační metrika. Jeden neautorizovaný pohyb, dvojí reveal, porušení Stop, cizí audio cleanup nebo zatajená fyzická nejistota blokuje dotčený provozní profil.

## 23. Testovací strategie a scénářové rodiny

### 23.1 Vrstvy ověření

**L0 — parser/compiler:** čisté validace, normalizace, deterministický output, source/hash vztah a hand-derived malé příklady. Bez hardware, sítě nebo modelu.

**L1 — core a porty:** fake clock, zachycené akce, stateful testy a scénáře zpoždění. Používat stejný core jako firmware; fake nesmí obejít skutečné admission/generační kontroly.

**L2 — integrace:** selection→show→journal, SceneIntent, původní Dance/audio adaptery, kontrakty a read-only viewer. Stávající host/native harnessy nejsou nahrazeny novým umělým DTO testem.

**L3 — skutečný hardware/provoz:** ESP, audio/DMA, světla, aktuátory, dlouhý provoz a reálný iPhone. Jen s platným oprávněním a bezpečnou připravenou sestavou. Nevytvářet si green `profile_b_ok` kvůli splnění testu.

Modelové nebo placené AI volání není běžnou závislostí DV02. Syntetický SceneIntent postačuje pro deterministický bridge; případný skutečný DV01 end-to-end průchod se vykáže samostatně.

### 23.2 Povinné mapování

Všech 80 níže definovaných rodin musí mít výsledek `covered`, `not_applicable` s konkrétním důvodem, nebo `blocked/not_run` s konkrétní branou. Nevybírat pohodlnou podmnožinu jen proto, že prošel stanovený počet testů. Parametrizace je žádoucí; rodina, fixture a počet opakování jsou různé údaje.

U každého záznamu uvést požadavky DV02-R, vrstvu, vstupy, očekávanou stopu/invariant, zakázaný výsledek a konkrétní příkaz/evidence. Staticky nedosažitelný neplatný program se testuje odmítnutím compilerem; není nutné jej skutečně poslat do serva.

| ID | Situace | Povinná kontrola |
|---|---|---|
| DV02-T01 | Běžná legacy presentation pro všechny dostupné sloty | Parita významných výstupů a normálního návratu |
| DV02-T02 | Krátká legacy presentation pro všechny sloty | Short zachová fyzické trvání a stejný lifecycle |
| DV02-T03 | Nová korektní accent scéna | Žádný pohyb; konečný cleanup všech vlastněných dekorací |
| DV02-T04 | Neznámá verze schématu nebo cue druh | Odmítnutí, nikoli tiché vynechání |
| DV02-T05 | Neznámé pole a chybné union varianty | Uzavřené schema i ve vnoření |
| DV02-T06 | Duplicitní JSON klíče, špatné UTF-8, příliš hluboký vstup | Bounded parse odmítne před runtime |
| DV02-T07 | Prázdné/duplicitní phase a cue ID | Odmítnutí nejednoznačnosti |
| DV02-T08 | Negativní/neceločíselný čas, string slot a overflow | Žádné tiché zaokrouhlení do platného příkazu |
| DV02-T09 | Zdroj, expanze nebo cue count na max a max+1 | Limit funguje před neomezenou alokací |
| DV02-T10 | Legacy `after` nemá doložitelný význam | Migrátor chybu označí, význam nevymyslí |
| DV02-T11 | Accent obsahuje calibrated-move | Kompilace odmítnuta |
| DV02-T12 | Lower před raise, dva raise, chybějící lower | Neplatný presentation lifecycle |
| DV02-T13 | Raise/lower používají jiný target | Nelze změnit hůlku uprostřed show |
| DV02-T14 | Vibrace zasahuje do servo fáze | Statické odmítnutí a runtime guard |
| DV02-T15 | Dvě LED skupiny či výsledný jas nad povolením | Žádný souběh ani přetečení stropu |
| DV02-T16 | Fade/level legacy normalizace | Bez dvojího scalingu a změny barev skryté migrací |
| DV02-T17 | Pulse končí přesně na hranici fáze | Vypnutí před začátkem konfliktního výstupu |
| DV02-T18 | Chybějící/neplatná/novější kalibrace | Reject či jasná blokace; žádná simulovaná fyzická hodnota |
| DV02-T19 | Nejdelší kalibrace a phase přechody | Worst-case deadline zahrnuje skutečnou scheduler sémantiku |
| DV02-T20 | Teď je přesně deadline, před ním a za ním | Žádný nový výstup v okamžiku zákazu |
| DV02-T21 | Dlouhá preparing fáze | Spotřebovává tentýž deadline; neobnoví jej start |
| DV02-T22 | Tick vynechá jednu či několik dekorací | Sample/skip podle typu, žádná doháněcí fronta |
| DV02-T23 | Tick přeskočí plánovaný začátek pohybu | Ověřená current phase a tail budget, jinak stop/fault |
| DV02-T24 | Stall během aktivní vibrace/pohybu | Po návratu owner zpracuje limit; report přizná dobu bez vykonávání |
| DV02-T25 | Nová scéna při aktivní presentation | Busy, žádné pozdější automatické přehrání |
| DV02-T26 | Volitelné audio obsazené řečí | Tón skipped, hlas nezrušen |
| DV02-T27 | Povinné audio obsazené nebo nedostupné | Žádný falešný plnohodnotný úspěch |
| DV02-T28 | Nová presentation během Dance | Žádné převzetí audio/SD/LED |
| DV02-T29 | Dance start během jiného owneru | Zachování současného refusal a UI |
| DV02-T30 | Partial resource acquisition selže | Rollback jen vlastních rezervací |
| DV02-T31 | Stará release událost po předání zdroje novému owneru | Žádné vypnutí nové show ani ABA leak |
| DV02-T32 | Zpožděný audio worker vrátí chybu staré operace | Nezastaví nesouvisející aktuální audio |
| DV02-T33 | OTA/servis při aktivní show | Zachována maintenance gate, žádné ukradení owneru |
| DV02-T34 | Dvojí současná žádost o start | Jediné atomické přijetí, druhá duplicate/busy podle identity |
| DV02-T35 | Platný DV01 wake/consider intent | Jen whitelistovaná dekorace aktuální session |
| DV02-T36 | DV01 choice_accent a běžná selection cesta současně | Přesně jeden oprávněný fyzický start, žádný druhý reveal |
| DV02-T37 | Intent jiné session/response/boot/revize | Reject; pozdní událost nesmí oživit starý kontext |
| DV02-T38 | Expirovaný intent a jeho retry | TTL se nedostane znovu na začátek |
| DV02-T39 | Payload s GPIO, úhlem, arbitrary slot, výrazem či kódem | Odmítnutí, žádná eval/runtime interpretace |
| DV02-T40 | Chybějící nebo nepodporované DV01/DV11 | Pravdivé not-present; žádná skrytá implementace souseda |
| DV02-T41 | Všechny dekorace byly vynechány | Výsledek neuvádí plně provedený akcent; quality none/reduced |
| DV02-T42 | Barge-in na response-scoped cue | Zrušení pouze příslušné response; session/presentation nezanikají bokem |
| DV02-T43 | Revokace evidence před fyzickým přijetím | Stará ready volba nemůže projít |
| DV02-T44 | Revokace po možném odeslání fyzického příkazu | Žádný nový reveal, unknown se neztratí |
| DV02-T45 | Zrušení session během existující presentation | Budoucí dekorace skončí, fyzický výsledek zůstane evidovaný |
| DV02-T46 | Explicitní show cancel v nehybné a pohybové fázi | Původní owner kontrakt, žádný automatický návrat |
| DV02-T47 | Stop v každé fázi a těsně před acceptance | Zákaz nových výstupů, žádné outro/error show |
| DV02-T48 | Stop versus start ve stejném ticku | Stop má deterministicky přednost |
| DV02-T49 | Timer callback byl dispatchnut před Stop | Generace/vlastník znemožní nový zápis po revokaci |
| DV02-T50 | Cleanup selže nebo nedokončí audio worker | Žádné běžné completed ani uvolnění recovery brány |
| DV02-T51 | Stejné ID se stejným payloadem | Původní stav, žádné další efekty |
| DV02-T52 | Stejné ID s jiným sceneHash/slotem/variantou | Conflict, nikoli editace běžící show |
| DV02-T53 | Nové ID pro tutéž již použitou selection autoritu | Dedupe/session guard neobejit |
| DV02-T54 | Send timeout a ztracené ACK | Unknown, bez slepého retry nebo nového ID |
| DV02-T55 | Restart v každém definovaném crash okně | Žádné resume, lower nebo další reveal bez evidence |
| DV02-T56 | Plný journal, prošlá trace a cleanup starých záznamů | Nezmizí ochrana před duplicitou fyzické operace |
| DV02-T57 | Pozdní skutečný výsledek po uzavření session | Upřesní operation journal, neoživí řeč ani cues |
| DV02-T58 | Device ACK bez completion | UI i navazující logika nezamění za hotový zdvih |
| DV02-T59 | Timed completion bez senzoru | Viditelná síla důkazu, žádná změřená poloha |
| DV02-T60 | Generování audia skončí před playbackem | Neodstartuje audio-dependent cue příliš brzy |
| DV02-T61 | Marker jiné stream/response generace nebo po expiry | Ignorován a neprodlužuje čekání |
| DV02-T62 | Audio timeline stall/underflow/cancel | Žádné přehrání nahromaděných dekorací |
| DV02-T63 | Dlouhá Dance stopa s nepohodlným BPM | Bez kumulovaného rounding driftu a bez obří cue tabulky |
| DV02-T64 | Odpojení telefonu během autonomní show/Dance | Zachování přesné současné disconnect policy |
| DV02-T65 | Restart audia změní sample counter | Nová generace, staré marker vazby neplatí |
| DV02-T66 | Clock reset, wall-clock skok, jiné boot a velké číslo v JS | Žádné falešné časové korelace nebo overflow |
| DV02-T67 | Dvojí build v jiném adresáři/pořadí souborů | Stejný deterministic compiled payload |
| DV02-T68 | Poškozený manifest/hash nebo odlišný bundle u vieweru | Odmítnutí či mismatch, ne falešná shoda |
| DV02-T69 | Změna jen kalibrace nebo profile revize | Evidence odliší nový execution kontext od starého měření |
| DV02-T70 | Pomalý subscriber a plný trace ring | Tick neblokuje; drop/gap viditelný |
| DV02-T71 | Chybí start/end event a terminal snapshot existuje | Pravdivá částečná rekonstrukce, žádné vymyšlené časy |
| DV02-T72 | Trace import obsahuje skript/HTML/akční pole nebo velký vstup | Bez vykonání a bounded import |
| DV02-T73 | Seek/replay/speed ve vieweru | Nikdy nevydá fyzický povel |
| DV02-T74 | Browser tab je pozastaven nebo klient offline | Jen vizualizace; fyzická show nezávisí na requestAnimationFrame |
| DV02-T75 | Starý/nový klient/firmware/protokol | Capability fallback a zachování přísných parserů |
| DV02-T76 | Konfigurační změna/rollback uprostřed show | Snapshot zůstane, Stop funguje, journal se neztratí |
| DV02-T77 | Startup/idle s novými scénami vypnutými | Žádný náhodný wake/ambient/referral trigger |
| DV02-T78 | Maximalistická povolená scéna pod zátěží | Bounded práce, report RAM/stack/tick; reálný ESP samostatně |
| DV02-T79 | Skills, dokumentační cesty a autorský workflow | Ověřené příkazy, správné triggery, žádná duplicita timingů |
| DV02-T80 | Fyzická show, zvuk, Stop a mobil na skutečné sestavě | Oddělený autorizovaný acceptance report, ne fake physical pass |

### 23.3 Metamorfní a stavové testy

Přidat vhodné vlastnosti nad corpus: snížení dovoleného jasu nikdy nezvýší výstup; opakovaný intent nepřidá fyzickou akci; posunutí události za expiry ji neoživí; přidání pomalého observeru nemění fyzickou stopu; vypnutí volitelného tónu nezmění kalibraci; po Stop už neexistuje autorizovaný výstup staré generace.

Fuzzing generátoru/parseru je offline, omezený a reprodukovatelný. Nejde o náhodné live povely do hardware. Fuzz/minimal counterexample může využít současné testovací knihovny; nevytvářet novou platformu jen pro počet testů.

## 24. Fyzická akceptace a měření

### 24.1 Předpoklady testu

Před pohybem ověřit současné hardwarové a servisní gates, kalibraci a platné oprávnění. Existující bezpečný servisní postup má přednost před obecným seznamem v tomto dokumentu. První test nesmí být souběžná maximální show všech výstupů.

Identifikovat firmware, source commit/tree, scene/bundle hash, hardware profil, kalibraci, napájení a relevantní audio nastavení. Změna kteréhokoli významného parametru může omezit přenositelnost výsledku.

### 24.2 Co skutečně měřit

Cue due→řídicí zápis, maximální jitter ticku, Stop→disable, dostupný audio playback proxy versus zvuk při konkrétním měření, délku a opakovatelnost pohybu, neočekávané souběhy, high-water stack/heap a chování při dlouhém provozu.

Nenavrhujeme povinný nový osciloskop, senzor či jiné vybavení k nákupu. Použít dostupnou vhodnou metodu a uvést její přesnost/omezení. Pokud měření chybí, příslušná hodnota je unknown, nikoli nula.

### 24.3 Výsledky po schopnostech

Samostatně vykázat `compiler_verified`, `core_verified`, `integration_verified`, `viewer_verified`, `audio_sync_profile`, `esp_resource_verified`, `physical_presentation_verified` a `event_ready`. Každý má konkrétní evidence reference nebo otevřenou bránu.

Lze mít hotový authoring/replay a nepovolený fyzický provoz. Celé DV02 nesmí být označeno za event-ready jen na základě webového timeline nebo zeleného CI.

## 25. Projektové Codex skills

Skills jsou povinnou částí budoucí implementace podle přiloženého operačního promptu. Nejsou další runtime kufru. Přednostně rozšířit relevantní existující skill, nikoli založit duplicitu.

### 25.1 Workflow `dildovander-show-authoring`

**Trigger:** změna scény, časování, barvy, fázového programu, generátoru nebo kompatibilních show variant.
**Výluky:** persona/roast, výběrová politika, kalibrace skutečného serva, hardware návrh, běžné nasazení.

Postup: zjistit kanonický zdroj a effective profile → vytvořit malý obsahový diff → validate/compile/check → porovnat s baseline → otevřít read-only timeline → provést relevantní regrese → zapsat změněnou identitu a otevřené fyzické brány.

Skill nesmí uchovávat druhou kopii časů, RGB ani aktuálních katalogových hodnot. Odkazuje na pravé zdroje a existující příkazy. Pokud je změna pouze autorská, nemá navrhovat přepis playeru.

### 25.2 Workflow `dildovander-show-debug`

**Trigger:** pozdní cue, špatná synchronizace, zaseknutý owner, duplicitní show, problém Stop, restart nebo neúplná trace.
**Výluky:** volná změna humoru, obecný refactoring projektu, automatické zkoušky na hardware.

Postup: určit verze a sílu dostupného důkazu → rozlišit plán a skutečný průběh → reprodukovat minimální syntetickou stopou → ověřit časovou doménu, generace a vlastníka → přidat regresi → porovnat baseline/kandidáta → uvést potřebné reálné měření, pokud ho offline test nenahradí.

### 25.3 Forma a discovery

Oficiální model skills používá metadata pro výběr a úplný `SKILL.md`/reference načítá podle potřeby; dokumentace uvádí také projektové `.agents/skills`. Konkrétní scope a podporu ověřit v používané verzi Codexu. [S4]

Krátká metadata `name`/`description`, přesné triggery a výluky, stručné workflow, odkazy a skutečně ověřené příkazy. Celou specifikaci nedávat do root AGENTS ani do description. Žádné globální změny nastavení, automatické použití dražšího modelu nebo domnělé subagent capability.

Ověřit syntax, relativní cesty, duplicity jmen, pozitivní/negativní trigger příklady a neplacený dry-run. Nedostupné discovery/reload označit jako neověřené; existence souboru není důkaz, že jej konkrétní běh Codexu načetl.

## 26. Dokumentace, evidence a udržovatelnost

Povinné minimum: mapa architektury/autority; malá reference jazyka; migration parity report; ověřené příkazy; requirement/scenario→evidence mapa; popis cancellation/unknown outcome; authoring a replay návod; kompatibilita a rollback; capability/physical gates.

Použít existující DV08 tracking, pokud je opravdu přítomný. Jinak aktuální projektové minimum. Nevytvářet vedlejší tracking platformu, novou nekonečnou kroniku ani automaticky nemaž historii původních dokumentů. Dlouhé testové výpisy patří do odpovídajících artefaktů, ne do AGENTS.

Výsledné soubory, třídy a příkazy mají navazovat na konvence repository. Názvy v této specifikaci jsou logické kontrakty, pokud nejsou výslovně označené jako pevné wire ID.

## 27. Tři end-to-end příklady správného chování

### A. Běžný osobní reveal a přerušení řeči

Backend má platnou volbu. DV01 dokončuje repliku a navrhne `choice_accent`. Bridge připojí dekoraci k připravené presentation; druhý fyzický start nevydá. Stávající selection cesta po gate přijme právě jednu operaci.

Host přeruší řeč. Response-scoped audio/dekorace se zruší. Fyzická presentation nepřijde o svou identitu; její další postup určuje současný owner, případně explicitní Stop. Žádný automatický druhý reveal pro novou formulaci repliky.

### B. Tón není k dispozici, prezentace funguje

Běží dialogové audio a scéna má jen volitelný krátký tón. LED a fyzický průběh splní všechny gates; tón se vynechá s `audio_owned_by_other`. Časové fáze se neprodlouží. Výsledek má `quality=reduced`, ne plný audio-synchronizovaný profil.

Je-li i samotný akcent pouze volitelné audio a nic se reálně neprovede, vrátit srozumitelný skipped/none výsledek podle současného envelope. Nehlásit hostiteli „efekt přehrán“.

### C. Ztracená odpověď a reboot

Příkaz mohl být přijat a začal zdvih. Backend ztratí spojení a ESP se restartuje. Session může skončit, ale operation ID a nejistota se nezmění na nový požadavek. Po bootu nic automaticky nevyjede ani se nevrací.

UI ukáže neznámý výsledek a existující recovery gate. Dostupné journal údaje se použijí k rekonsiliaci. Ani opakované připojení mobilu, ani otevření replay, ani nově vygenerovaná replika neopakují fyzickou show.

## 28. Co implementační agent smí rozhodnout sám

Samostatně zvolit interní názvy, přesné umístění schema/IR a testů, reuse stávajících portů, nejmenší podporovaný JSON dialect, vhodné integrační body, mapování logických stavů na současné a rozumnou parametrizaci testů.

Nesmí sám zvýšit fyzické limity, změnit hlas/model/personu, vynechat podstatnou část návratového lifecycle, přidat motorové runtime scripting, automatické nasazení či jiné iniciativy. Nemá prohlásit velký framework za nutnost bez porovnání s malým rozšířením současného core.

Subagenty lze použít pro izolovanou práci nebo nezávislé review, pokud jsou dostupné. Hlavní agent vlastní integrační kontrakt, source of truth a výslednou verifikaci. Delegace nesmí vytvořit několik nekompatibilních schema nebo různých vlastníků téhož hardware.

## 29. Zdroje, původ a hranice ověření návrhu

**P1 — uživatelský vstup `SPEC(8).md`:** záměr sjednotit scény nad současnými komponentami, zachovat Dance, safety, timeline a fyzické brány.
**P2 — uživatelský vstup `CODEX_PROMPT(2).md`:** implementační pracovní postup DV02, audit skutečnosti a zákaz přeskakování gate.
**P3 — `DV01_FINAL_CODEX.md` revize 2.1, §7.4–7.5 a související hranice:** response/session/operation identity, whitelist SceneIntent a oddělení fyzické operace. Tento dokument specifikuje kompatibilitu, ne hotovou integraci.

Vnější technické reference ověřené pro návrh 13. 10. 2026:

**S1 — Espressif, ESP Timer, ESP32-S3.** Lokální čas, dispatch omezení a význam zastavení callbacků. URL: `https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/api-reference/system/esp_timer.html`

**S2 — Espressif, I2S, ESP32-S3.** DMA přenos a callbacky; nepředstavují akustické měření. URL: `https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/api-reference/peripherals/i2s.html`

**S3 — JSON Schema, Object reference.** Uzavírání objektů a rozdíly additional/unevaluated properties. URL: `https://json-schema.org/understanding-json-schema/reference/object`

**S4 — OpenAI, Build skills.** Struktura, progressive disclosure, discovery a ověřování skills. URL: `https://learn.chatgpt.com/docs/build-skills`

Adresy `stable` jsou proměnlivé. Implementační agent musí ověřit kompatibilitu se skutečně připnutou verzí SDK a nástroji; tato revize sama nenařizuje jejich upgrade. Ostatní kontrakty, profily, příklady, rozpočty a akceptační tabulky v tomto dokumentu jsou návrhové rozhodnutí DV02, nikoli externě naměřená fakta.

Při přípravě této revize se neupravoval repozitář, nespouštěl firmware, neprovádělo nasazení ani fyzické testy. Kontrola úplnosti dokumentu a syntaxe příkladů není důkaz implementace scénického jazyka.

---

**Konečný test DV02:** autor změní jednu scénu, systém z téhož zdroje vytvoří vykonatelný plán a čitelný náhled, skutečný kufr provede pouze oprávněné efekty a při chybě lze přesně rozlišit, co bylo plánované, co bylo přikázané a co je opravdu známé. Bez dalšího paralelního engine a bez falešných záruk.
