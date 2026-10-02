# Kvalita hlasu a humoru kufru

Rešerše a implementační rozhodnutí, 2. 10. 2026 (D127).

## Doporučení

Nejdřív opravit kontext a vedení hovoru v existujícím Realtime agentovi.
Pro další kvalitativní srovnání doporučuji **GPT‑Live + samostatný Sol backend**
proti opravenému Realtime. GPT‑Live odděluje hlasový projev od textového
uvažování a podporuje současný poslech a řeč. Zda bude v češtině zábavnější,
musí rozhodnout poslech a slepé porovnání; dokumentace to nedokazuje.
Samotné přidání Agents SDK, LiveKit nebo názvu „agent“ humor nezlepší.

Původní D127 opravil kontext a humor; navazující D128 níže přidává
přepínatelný GPT-Live a nové session limity.
Vlastní fine-tuning neproběhl: mění se konverzační logika a instrukce modelu.

## Zjištěné příčiny

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

## Co přebíráme z CARLIN

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

## Srovnání hlasových cest

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

## Jak rozhodnout podle kvality

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

## Zdroje ověřené při rešerši

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
