# Kvalita hlasu a humoru kufru

Rešerše a implementační rozhodnutí, 2.–3. 10. 2026 (D127–D135).

## D140: společný prompt podle Live a medium pro Realtime

Majitel 4. 10. žádá zachovat Realtime 2.1, zvýšit jeho effort na medium
a sjednotit prompty podle GPT Live. Oficiální
[Realtime prompting](https://developers.openai.com/api/docs/guides/realtime-prompting)
a [modelová karta 2.1](https://developers.openai.com/api/docs/models/gpt-realtime-2.1)
uvádějí medium jako podporovanou hodnotu; vyšší effort může přidat latenci
a spotřebu výstupních tokenů. Rozpočet 3 USD a rezervace zůstávají vynucené.

Základem `voice-persona.txt` je dosavadní Live role, sprostý konkrétní roast,
backchannel silence a interruption policy. Společná jsou i navazující otázky,
ukázky, hranice hry, paměť a závěr bez potvrzovací otázky. Realtime už nedostává
výjimku pro hlášku při práci funkce. Podle
[Live prompting](https://developers.openai.com/api/docs/guides/live-prompting)
Live nadále dostává zvláštní delegation policy; výběrové funkce patří pouze
Sol/Realtime (`prompt.txt`). Offline testy ověřují instrukce skutečných
provider requestů, ne slyšitelný humor či latenci. Sol zůstává na low.

## D137: zpětná vazba po poslechu – přerušování a dokončení

Majitel 3. 10. potvrzuje lepší Live i Realtime, ale jednou Live přerušil
odpověď a nevybral; v jiném závěru zazněl výběr a aplikace hlásila
`realtime_live_audio_overflow`. Žádá autonomní přidělení bez otázky na
souhlas a ještě sprostší/drzejší tón.

Read-only [snapshot 37155213446](https://github.com/Blato58/DildoVander/actions/runs/37155213446)
z 21:28 UTC obsahuje Live start 21:20:45 a klientské zrušení HTTP 499
v 21:22:12, bez `completed`. `droppedWrites=0`, `truncated=false`.
Log neobsahuje přepis, PCM, stav iOS fronty ani latenci jednotlivých
úspěšných uploadů; nedokazuje přesnou příčinu overflow. V kódu se potvrzuje:

- Hotový Sol mohl odeslat commentary hned po delegaci, než dorazil další
  fragment stejné odpovědi. Nově se výsledek publikuje/přepočítá až po
  650 ms bez input fragmentu. Přidaná latence je nejvýše tato stabilizační
  mezera po posledním fragmentu; pomalejší Sol ji už obvykle překryje.
  Přepisová mezera není důkaz konce řeči. Oficiální [Live prompting](https://developers.openai.com/api/docs/guides/live-prompting)
  rozlišuje backchannel a přebírání tahu: u kufru nyní zakazujeme všechny
  narážky během delegace, krátké pauzy ani zaváhání nejsou pozvánka k řeči.
- Mikrofonní gate se zavíral až po potvrzeném `session.closed`. Nově po
  závěrečném výstupním tichu pošle `speech_stopped` před žádostí close.
  Final usage a validace návrhu stále předcházejí zveřejnění výběru.
- Overflow notifikace z audio tapu neměla identitu vstupu; na hlavní
  frontě mohla dorazit po zavření input gate a zrušit probíhající závěr.
  Nově má epochu a po Stop/novém poslechu se ignoruje. Skutečný aktuální
  backlog pořád selže; 1s fronta a jeden upload v letu zůstávají.
- Sdílený prompt i Live a Realtime instrukce výslovně zakazují žádost
  o souhlas s hůlkou. Sprostota patří k běžnému tónu a příklady jsou
  ostřejší; pointa vychází z řečeného. Odmítnutí hry/tématu platí dál.

Regrese musí prověřit rychlý Sol mezi fragmenty vzdálenými 350 ms,
přepočet s účtováním zastaralé práce, korekci závěru a gate před close.
Nativní test kontroluje epochu opravdové overflow a její neplatnost po
zavření gate/novém vstupu. Poslech musí ověřit delší odpověď se zaváháním,
opravou, hlukem, výběr bez ano/ne a dohraný závěr. Bez telefonu nelze
prohlásit všechna přerušení ani všechny zdroje backlogu za odstraněné.

## D135: ostrý prodavač, oprava čekání a Realtime 2.1

### Podklady před změnou

Majitel 3. 10. žádá výrazně urážlivý, vtipný kufr prodávající hůlky
v Ollivanderově obchodě a upozorňuje na opakované vybízení k čekání.
Před úpravou byly přečteny prompty, opening rotace, Live delegace,
Realtime protokol, validace funkcí, rozpočet, stávající testy a aktuální
primární dokumentace uvedená níže. Následný dotaz majitele na Realtime
2/2.1 rozšířil rešerši o obě modelové karty a ceník.

Read-only [serverový snapshot 37152729168](https://github.com/Blato58/DildoVander/actions/runs/37152729168)
potvrzuje režimy responses/openai a dvě GPT-Live session dne 3. 10.
ve 22:40 pražského času. Obě skončily klientským zrušením (499), druhá
po 94,5 s. Log neobsahuje audio ani přepisy, takže nedokazuje konkrétní
hlášku „počkej“ ani časování delegací. Následující příčiny jsou doložené
kódem a regresními scénáři, nikoli přepisem tohoto hovoru.

### Zjištění a rozhodnutí

1. **Delegace bez přepisu se ztrácela.** Live událost delegace neobsahuje
   výrok hosta; přepis přichází samostatně. Dosavadní kód prázdnou delegaci
   zahodil a poslal pokyn k čekání. Nový kód ji podrží pro doručení textu.
2. **Doplnění během Solu mohlo zastavit postup.** Každý input fragment
   mění verzi. Neaktuální výsledek se správně zahodil, ale další výpočet
   závisel na další delegaci modelu. Nově jej aplikace po dokončení staré
   práce zahájí sama nad aktuálním přepisem. Stará práce se účtuje,
   nepostupuje počítadlo otázek a nesmí zveřejnit hůlku. Počet výpočtů
   včetně opravených vstupů má samostatný limit deset. Jde o změněný
   vstup po úspěšném volání, nikoli retry selhaného requestu.
3. **Pokyny podporovaly čekání.** Frontend musel každou odpověď delegovat,
   nesměl navazovat a dostával další čekací zprávy. Prompt nyní zakazuje
   mluvené provozní výplně a nabízí nejvýše jednu situační narážku během
   delegace; další otázka patří až k výsledku. Duplicitní práce ve stejné
   verzi nevytváří výpočet ani čekací upozornění. Oprava už vysloveného
   závěru také pokračuje automaticky. Instructions mohou přerušit řeč,
   proto pokyn k závěru předchází commentary a nenásleduje po něm.
4. **Osobnost odporovala zadání.** D127 preferoval civilního kámoše,
   minimum kouzelnické atmosféry a opatrnou ostrost. D135 nastavuje
   ostrý roast jako výchozí styl, drzého odborníka na hůlky, krátké repliky
   a výrazné české ukázky. Vulgarita je prostředek pointy, ne její náhrada.
   Šest opening námětů nyní zjišťuje použití hůlky, odkládanou povinnost,
   vlastní zlozvyk, průšvih, reakci na nepovedené kouzlo nebo machrování.
   Další otázka navazuje na odpověď; nejde o kvíz z Harryho Pottera.
5. **Kouzelnická autenticita je motiv, nikoli přepis katalogu.** Rowlingové
   poznámky o dřevech popisují individuální preference hůlek a vzájemné
   učení člověka a hůlky. Poznámky o jádrech rozlišují jednorožce, draka
   a fénixe a upozorňují na souhru dřeva, jádra a majitele. To inspiruje
   komedii o posouzení zákazníka; šest místních party rekvizit nedostává
   nová jádra ani nepravdivý kanonický původ. Povýšená urážlivost kufru
   je naše komediální adaptace, nikoli doložená osobnost Garricka.
6. **Realtime přechází na 2.1 s low reasoning.** Modelová karta 2.1
   uvádí proti 2 lepší rozpoznávání alfanumerických údajů, hluku, ticha
   a přerušení. 2 i 2.1 mají text input/output 4/24 USD za milion tokenů
   a audio input/output 32/64 USD; 1.5 má text output 16 USD.
   Dosavadní účtování všech output tokenů sazbou 64 USD je konzervativní
   i pro reasoning. Max output a 3 USD/session zůstávají. Pinned SDK
   2.14.0 zachovává transport a serializuje reasoning přes podporovaný
   JSON patch. Není nutná změna mobilního/ESP wire kontraktu.
   Samotná modelová karta neprokazuje dostupnost na našem účtu ani
   lepší české vtipy. GPT-Live zůstává samostatný model gpt-live-1.

### Primární zdroje ověřené 3. 10. 2026

- [OpenAI: GPT-Live prompting](https://developers.openai.com/api/docs/guides/live-prompting):
  oddělená osobnost, backchannel/interruption/delegation policy, neopakovat
  delegaci stejné práce, ukázky a reprezentativní rozhovory.
- [OpenAI: Live delegation](https://developers.openai.com/api/docs/guides/live-delegation):
  oddělený přepis a delegace, vlastnictví kontextu, aktualizace změněného
  požadavku; commentary je určeno k řeči, thinking pro kontext.
  Ani backend completion ani append acknowledgment nejsou dohrané audio.
- [OpenAI: Realtime prompting](https://developers.openai.com/api/docs/guides/voice-prompting):
  jasná role a krátké oddíly, konkrétní příklady a minimum protichůdných
  pravidel; Realtime 2 doporučuje začít low a záměrně řídit preambles.
- [Realtime 2](https://developers.openai.com/api/docs/models/gpt-realtime-2),
  [Realtime 2.1](https://developers.openai.com/api/docs/models/gpt-realtime-2.1)
  a [ceník](https://developers.openai.com/api/docs/pricing): schopnosti a
  veřejné sazby, nikoli změřená kvalita na našem zařízení.
- [J. K. Rowling: Wand Woods](https://www.harrypotter.com/writing-by-jk-rowling/wand-woods)
  a [Wand Cores](https://www.harrypotter.com/writing-by-jk-rowling/wand-cores):
  původní poznámky o individuálním párování a souhře materiálů s majitelem.

### Co musí ověřit poslech

Offline testy ověřují změněné řízení, SDK wire nastavení a bezpečný výběr,
ne smích ani odstranění všech mluvených výplní. Při následném živém testu
srovnat stejné situace na 1.5 a 2.1: ochotný roast, jen jméno, „nevím“,
krátká odpověď, delší historka, doplnění při práci, přerušení, hluk,
„to neřeš“ a „stačí“. Zapsat první užitečné přehrané audio, p50/p95,
počet mluvených čekacích výplní (cíl nula), návaznost otázek a lidské
hodnocení point. Volba materiálu ke skutečně vybrané hůlce musí sedět.
Zachovat rozpočet a oddělit dostupnost modelu na účtu od úspěchu testu.
Nasazení ani placený poslech nejsou součástí tohoto offline ověření.

## D127: původní rešerše a doporučení

### Doporučení

Nejdřív opravit kontext a vedení hovoru v existujícím Realtime agentovi.
Pro další kvalitativní srovnání doporučuji **GPT‑Live + samostatný Sol backend**
proti opravenému Realtime. GPT‑Live odděluje hlasový projev od textového
uvažování a podporuje současný poslech a řeč. Zda bude v češtině zábavnější,
musí rozhodnout poslech a slepé porovnání; dokumentace to nedokazuje.
Samotné přidání Agents SDK, LiveKit nebo názvu „agent“ humor nezlepší.

Původní D127 opravil kontext a humor; navazující D128 níže přidává
přepínatelný GPT-Live a nové session limity.
Vlastní fine-tuning neproběhl: mění se konverzační logika a instrukce modelu.

### Zjištěné příčiny

- Sdílený prompt přikazoval začít jménem; Realtime přidával další „zjisti
  jméno“. Obecné „pokaždé nemusíš začít stejně“ tomu odporovalo.
- Při opening Realtime posílal paměť večera jako `user` zprávu. Žádný nový
  host ještě nepromluvil, takže tato zpráva byla jediným vstupem, na který
  mohl model reagovat. To je věrohodná příčina odpovědi o „datech“, nikoli
  důkaz přesného průběhu konkrétního nasazeného hovoru.
- „Hostitel tě představil“ nerozlišovalo znalost role kufru od předchozí
  konverzace s příchozím. Příklady navíc obsahovaly dvě otázky v jedné replice,
  přestože jiné pravidlo dovolovalo jen jednu.

Opening nyní neodesílá archiv ani historii v textové či hlasové cestě.
Archiv zůstává uložený a dostupný v následujících tazích. Server střídá šest
**námětů**, nikoli hotových vět: oslovení jménem, odkládání, zlozvyk,
kamarádská historka, plánování/improvizace a drobné sebevědomí.
Model formulaci vytváří sám. Po první odpovědi má sledovat její obsah.

Rotace je společná pro proces backendu, se startem z náhodné pozice.
Šest po sobě vytvořených opening kontextů má šest různých námětů;
není to trvalá evidence úvodů podle zařízení ani záruka šesti různých
výsledných vět modelu. Restart rotaci resetuje. Realtime uchovává vybraný
námět v kontextu tahu, takže výpočet rezervace a její zkrácení jej nezmění.
Idempotentní operace nadále nevyvolá nový placený pokus.

### Co přebíráme z CARLIN

[Greg Robison: The CARLIN Method](https://gregrobison.medium.com/the-carlin-method-teaching-ai-how-to-be-genuinely-funny-2bd5e45deaf2)
z 21. 11. 2024 popisuje sběr podkladů, hledání rozporů, více kandidátních
point, jejich zhodnocení, zkrácení a podání. Článek uvádí vlastní výsledky
GRAIG; nejde o nezávislé ověření českého živého dialogu našeho kufru.
Přímé načtení Medium blokoval Cloudflare; veřejný text byl dostupný přes
textové zobrazení Jina Reader, včetně částí o procesu a jeho omezeních.

Pro krátký rozhovor se podklady získávají otázkou a poslechem hosta.
Není potřeba webový výzkum každé historky. Sdílený prompt vede model k tomu,
aby zvážil různé úhly, zahodil nedoložené a nesrozumitelné varianty a řekl
jednu krátkou pointu. Například doslovná výmluva, rozpor záměru a výsledku,
jeden absurdní důsledek nebo doložený návrat ke staršímu detailu.
Samotné jméno nedává podklad k osobnímu roastu. Bez podkladu stačí otázka.

To je **adaptace principů CARLIN v jednom volání**, nikoli implementovaný
vícekolový strom hledání nebo garantovaný interní postup modelu. Explicitní
generátor několika variant a samostatný hodnotitel by přidal kontrolu,
ale také volání a čekání. Doporučuji jej případně porovnat u závěrečné
pointy v textovém backendu; nezavádět automaticky několik modelových kol
před každým hlasovým pozdravem. Kvalitu musí hodnotit lidé, nikoli pouze
tentýž model, který vtip vytvořil.

### Srovnání hlasových cest

| Varianta | Přínos pro tento projekt | Omezení a rozhodnutí |
| --- | --- | --- |
| Současný Realtime | Model slyší audio přímo, mluví a volá validované funkce v jedné session. Existuje .NET integrace, paměť a rezervace. | Aplikace je poloduplexní. Opravit úvod a prompt nyní; použít jako základ srovnání. |
| GPT‑Live + Sol | Full duplex; oddělený backend může tvořit kvalitnější pointu, zatímco hlasová vrstva řeší rozhovor. | Nový životní cyklus delegací, zrušení zastaralých výsledků a řízení přehrávání. Hlas účtovaný podle délky + samostatný backend vyžaduje nový výpočet rezervací. Od D128 volitelný testovací režim. |
| STT → Sol → TTS | Úplná kontrola nad textem a možnost porovnat několik point před přehráním. Již existující záloha. | Sekvenční čekání a ztráta části neverbálního kontextu. Není důvod ji bez poslechu vyhlásit vítězem. |
| ElevenAgents | Volitelný LLM, hlasová syntéza a vlastní řízení střídání řeči. | Přínos české výslovnosti a komediálního podání zde nezměřen; další služba a integrace. Kandidát, pokud český hlas zůstane slabinou. |
| LiveKit Agents | Orchestrace, WebRTC, detekce konce řeči a integrace různých modelů. | Framework, nikoli model humoru; další server a transport pro současné HTTPS klienty. Teď není nutný. |

OpenAI výslovně rozlišuje GPT‑Live, Realtime a řetězený hlas. Starší použití
„GPT-live/Realtime“ v historii projektu tyto dnešní odlišnosti nerozlišuje.
Agents SDK je integrační možnost, nikoli nutná podmínka hlasového agenta.

Kvalitnější přerušování vyžaduje otevřený mikrofon při přehrávání a řešení
ozvěny na skutečném zařízení. Pouhé zapnutí provider interrupt nebo změna
VAD nestačí: telefon/ESP musí zastavit přehrávání, backend zneplatnit
zastaralý výsledek a zachovat správnou korelaci výběru hůlky.
Současný server VAD s 650 ms ticha může přerušit přemýšlejícího hosta;
semantic VAD stojí za samostatný test v české řeči a hluku, nikoli za
neověřenou změnu současně s promptem.

### Jak rozhodnout podle kvality

Offline testy dokazují sestavení požadavků, izolaci opening, zachování
paměti pro další tah a validaci výběru. Nedokazují, že se člověk zasměje.
Žádný placený test ani nasazení se v D127 neprovedly.

Navržená akceptace pro schválený živý běh:

1. Dvacet nových rozhovorů: prázdný večer, naplněný archiv, dva stejné názvy
   hostů, restart. Zapsat skutečné první dvě otázky. Nula předstíraných
   návazností a osobních faktů, nula komentářů k technickým datům,
   nejméně čtyři různé náměty; přesná první věta nejvýše dvakrát.
2. Dvanáct krátkých dialogů: pouze jméno, „nevím“, stručný host, rozvitá
   historka, dobrovolný roast, odmítnutí tématu, oprava faktu, návrat známého,
   stejné jméno jiného hosta, dlouhá pauza, hluk a ukončení „stačí“.
   Další odpověď vždy reaguje na skutečnou otázku, ne na pevný scénář.
3. Stejné nahrávky a podmínky pro porovnávané verze, pořadí zaslepit.
   Tři čeští posluchači hodnotí 1–5: návaznost, srozumitelnost, originalitu,
   vtipnost a hlasové podání. Návrh cíle: alespoň 90 % dialogů má medián
   návaznosti/přirozenosti ≥4; vtipnost nesmí být horší než základ.
   Vymyšlené osobní fakty nebo ignorované „stačí“ jsou samostatná chyba.
4. Měřit konec řeči → první **užitečné přehrané audio**, p50/p95, dokončení
   tahu, falešné VAD konce a případně reakci na přerušení. Samotné „hmm“
   nepočítat jako rychlou odpověď. Poslouchat i celý rozhovor, nejen přepis.
5. Zachovat existující omezení výběru a testy nedostupných přihrádek.
   Náklad uvádět odděleně jako odhad a skutečné provider usage; nový
   rozpočet před živým srovnáním stanovit, nepřebírat vyčerpané starší limity.

Samostatný Python eval sdílí prompt a izolaci opening, ale nemodeluje
procesovou rotaci C# ani zvuk. Pro akceptaci rotace použít skutečný backend
(`interactive_dialogue.py` pro text, Realtime live harness a lidský poslech
pro hlas). Předem nahrané odpovědi určené pro starý povinný dotaz na jméno
nepovažovat za adaptivní test nové konverzace.

### Zdroje ověřené při rešerši

- [OpenAI: Voice agents](https://developers.openai.com/api/docs/guides/voice-agents) — tři architektury a metodika hlasového hodnocení.
- [GPT‑Live](https://developers.openai.com/api/docs/guides/live) — full duplex, lifecycle a účtování délky session.
- [Delegation and tools](https://developers.openai.com/api/docs/guides/live-delegation) — client/Responses delegace, kontext a asynchronní výsledky.
- [Realtime](https://developers.openai.com/api/docs/guides/realtime) — hlas, nástroje a session.
- [Realtime prompting](https://developers.openai.com/api/docs/guides/realtime-models-prompting) — konzistentní pravidla, krátké repliky a příklady.
- [Voice activity detection](https://developers.openai.com/api/docs/guides/realtime-vad) — server/semantic VAD a řízení odpovědí.
- [ElevenAgents](https://elevenlabs.io/docs/agents-platform/overview) — ASR, volitelný LLM, TTS a turn-taking.
- [LiveKit Agents](https://docs.livekit.io/agents/) — framework, modely a WebRTC infrastruktura.

Dokumentace dokládá schopnosti rozhraní, nikoli naměřenou českou kvalitu
nebo dostupnost konkrétního nového produktu pro náš provider účet.

## D128: testovaci GPT-Live rezim

Na navazující zadání majitele je implementován **GPT-Live + Sol** jako
samostatná volba, bez nahrazení Realtime. Používá Live API
`wss://api.openai.com/v1/live/sessions`, `session.start`, kontinuální
24kHz PCM, vnořený `delegation.id/target`, backend commentary a potvrzené
`session.closed`. Wire schéma bylo porovnáno s oficiální dokumentací a
OpenAI Python SDK 3.22.1; .NET SDK 2.14.0 Live dosud nemá. Sol zůstává
přes oficiální .NET SDK. Nejde o změnu modelových vah ani fine-tuning.

Po nasazení stejné verze backendu, aplikace a firmware:

1. V telefonu otevři **Debug → Tok → GPT-Live + Sol**. Vyber hlas
   Marin, Cedar nebo Coral a spusť rozhovor. GPT-Live používá rychlost 1×.
   Hlas i mikrofon běží současně přes Apple voice processing. Debug
   nepředává návrh do fyzického kufru.
2. Pro samostatný kufr otevři **Hlas kufru → Hlasový režim → GPT-Live + Sol**,
   použij nastavení, uvolni ruční řízení a spusť hlas na kufru.
   Nastavení se přenese jako `voice.configure mode:gpt-live`; platí do restartu.
3. Nový režim vybírej mezi sezeními. Pro srovnání stejným přepínačem vrať
   Realtime a použij stejné situace a hlas. Poznamenej českou výslovnost,
   latenci, konkrétnost pointy, opakování a reakce na přerušení.
4. Každé sezení má 3 USD rozpočet společný pro hlas a všechny delegace;
   celkový rozpočet zůstává v OpenAI. AI eval má také 3 USD na rozhovor,
   bez výchozího souhrnného limitu celé sady.

Omezení prototypu: ESP nemá ověřené AEC, proto během přehrávání posílá
časově souvislé ticho a začne poslouchat po dohrání segmentu. Na telefonu
je zapnuté AEC, ale skutečná kvalita a hlasitost vyžadují iPhone test.
Live WebSocket nemá událost dokončeného audio tahu; 900ms práh ve výstupním
PCM je heuristika pauzy, včetně závěrečné repliky. Dlouhá vnitřní pauza
může ukončit repliku předčasně. Výběr se publikuje až po `session.closed`,
které potvrzuje konec relace a usage, nikoli sémantickou úplnost řeči.
Sezení má technický timeout 180 s, nejvýše 10 delegací; Stop zavře spojení.
Oprava hosta během rozpracovaného výsledku zabrání použití starého výběru.
Nejasně ukončené placené volání drží rezervaci, nepočítá se jako zdarma.
Živá kvalita a reálné zařízení nebyly tímto offline ověřením potvrzené.
