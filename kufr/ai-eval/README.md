# Vývojový test dialogu AI bez kufru

Tento adresář je samostatná E8 příprava. Posílá **syntetické** české
texty přímo do OpenAI Responses API z GitHub hosted runneru. Nevyžaduje
ESP32, Device ID, device credential, backend, server ani mobilní aplikaci.
Nevydává žádný příkaz k pohonu. Skutečný E8 backendový adaptér a hlasové
testy jsou další práce.

Prompt je v prompt.txt: mluvící kouzelný kufr prodejce z Ollivanderova obchodu ve světě Harryho Pottera pro soukromou party 18+, s ostrým, vtipným roastem. Původní šestice hůlek je v wand-catalog.json; backend i eval předávají modelu jen dostupné položky. Výchozí model je gpt-6.1-sol s nízkým reasoning effort. Stejné scénáře
lze ručně porovnat s gpt-6-astra a gpt-6-luna. STT a TTS se v tomto
textovém testu nevolají. Model musí vrátit přísné JSON schéma; parser navíc
kontroluje délky, decision, compartment a aktuální readyMask.
Neplatný, odmítnutý či neúplný výsledek končí bezpečnou chybou. Kvalitu
češtiny a stylu je třeba vyhodnotit člověkem.

## Příprava OpenAI Platform

1. Založ samostatný projekt například kufr-dev a projektový API klíč.
   Vytvářet Agent Builder agenta pro tento úzký dialog není potřeba.
2. V projektu otevři Limits → Spend → Edit spend limit, nastav měsíční
   limit **10 USD** a zapni **Enforce a hard limit**. Upozornění můžeš
   nastavit například na 8 USD. Hard limit se neprosazuje okamžitě, takže
   může být mírně překročen.
3. API klíč vlož do GitHub repozitáře
   Blato58/DildoVander → Settings → Environments → backend-production →
   Environment secrets pod názvem OPENAI_API_KEY. Neposílej jej do chatu
   ani do souborů. Workflow kufr-ai-eval.yml ho použije pouze při
   ručním spuštění z větve main; běžné PR testy klíč nedostanou.

## Spuštění

V Actions vyber **Kufr AI development eval → Run workflow**, model a
jeden identifikátor z 62 případů v `cases.json` (60 českých a dvě jazykové
odbočky). Jeden běh = jeden placený
textový tah. Korpus pokrývá dostupnost, nejasný výběr, pokusy změnit pravidla,
fyzickou hranici, party humor a různé podoby češtiny; samotná existence scénářů
neznamená, že model prošel jejich živým ani lidským hodnocením. Výsledek je v logu
a v artefaktu kufr-ai-evaluation. Všechny scénáře v cases.json jsou
fiktivní; nepoužívej osobní údaje skutečných návštěvníků.

Lokální kontrola bez placeného volání:

    python -m unittest discover -s kufr/ai-eval -p 'test_*.py'

`dialogue_cases.json` má přirozené scénáře: opening bez návštěvnického textu,
Petr s taxíkem, návrat Petra s pamětí a Lenka odmítající téma ex. Fixtures
ověřují skládání požadavků a hranici návrhu, nikoli skutečný humor modelu.
Backend archivuje celý večer odděleně od krátkého sezení; viz
[backend README](../backend/README.md). Eval používá stejný prompt a přísné
schéma. Paměť ve vývojových scénářích je výhradně fiktivní.

## Adaptivní srovnání humoru

Ruční workflow má volbu `party_compare`. Spustí `evaluate_party.py`, který
porovná Sol a Astra na stejných třech fiktivních osobnostech. Návštěvník
simulovaný modelem Sol odpovídá na **skutečnou poslední otázku**, místo
předem napsané repliky bez souvislosti. Každý dialog má nejvýše pět replik
kufru; hned končí návrhem/no_selection. Další placené volání se předem
rezervuje; všechny návštěvnické i kufrové modely sdílí celkový strop 1 USD,
každý rozhovor nejvýše dosavadních 0,50 USD. Vstup `party_models=selected` použije jen vybraný model; `party_budget_micro_usd`
snižuje strop na zbývající schválený rozpočet mezi iteracemi.
Síťový výpadek se neopakuje
ani nepovažuje za bezplatný. Ruční běh je placený; běžné PR CI pouze offline.

Výstup `evaluation.json` obsahuje celé **syntetické** dialogy, modely, tokeny,
latence, response ID, původní provider usage včetně cache/reasoning a konzervativní ceníkový odhad. `billingVerified: false` a `billedCostMicroUsd: null` výslovně označují, že skutečnou účtovanou cenu neznáme. `upperCostMicroUsd` je pouze pojistka rozpočtu, nikdy údaj o utracených penězích.
Přepis posuzuj podle konkrétnosti pointy, přirozené češtiny, návaznosti,
zbytečného představování, respektování změny tématu a správného připomenutí
historie. Platný JSON sám není známka dobrého humoru. Model návštěvníka také
může produkovat umělou řeč, takže finální akceptace patří skutečné obsluze.

## Nastavení OpenAI pro tento projekt

- Responses API + strict Structured Outputs, výběr přihrádky validuje backend.
- Sdílený prompt je verzovaný v Gitu; žádné další skryté instrukce v Playgroundu.
- Sol s reasoning `low` je výchozí. Astra je kandidát pro slepé porovnání
  kvality; vyšší cena ani reasoning automaticky nezlepší dialog.
- `store:false`, vlastní textová paměť na backendu. Conversations API umí
  trvalé objekty napříč sezeními, ale majitel výslovně zvolil vlastní archiv.
  Compaction je alternativa pro dlouhý provider kontext, nikoli náhrada
  archivu s mazáním a pravidly přiřazení lidí. V tomto slice se nevolá.
- Klíč pouze v serverovém/GitHub secret store, samostatný projekt s potvrzeným
  hard limitem. Zachovat 10 USD/měsíc, aplikační rezervace a měření latence.
- Prompt a výběr kontextu nejsou trénování modelu. Fine-tuning, agentní
  framework ani vector store nemají bez měřeného přínosu smysl přidávat.

Oficiální zdroje ověřené 30. 9. 2026:
[conversation state](https://developers.openai.com/api/docs/guides/conversation-state),
[compaction](https://developers.openai.com/api/docs/guides/compaction),
[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol).

Pro lokální placený pokus nastav OPENAI_API_KEY pouze v prostředí procesu
a spusť python kufr/ai-eval/evaluate.py --case none_ready. Klíč se
neukládá do projektu. Při časovém limitu se POST neopakuje automaticky,
protože už mohl být účtován.

Skript před voláním používá konzervativní odhad maximální ceny jednoho
tahu a odmítne odhad nad 0,50 USD. Skutečné tokeny a horní cenový odhad
zapíše do výsledku. Tato ochrana není průběžný měsíční účet ani přesná
faktura; celý účet chrání projektový hard spend limit. Backendové
SESSION_CAP_MICRO_USD=500000 a MONTHLY_CAP_MICRO_USD=10000000 jsou
samostatné limity pro pozdější E8 integraci.

[Živé syntetické přepisy a pozorování z 30. 9.](reports/2026-09-30-party-dialogue.md)
