# Vývojový test dialogu AI bez kufru

Tento adresář je samostatná E8 příprava. Posílá jeden **syntetický** český
text přímo do OpenAI Responses API z GitHub hosted runneru. Nevyžaduje
ESP32, Device ID, device credential, backend, server ani mobilní aplikaci.
Nevydává žádný příkaz k pohonu. Skutečný E8 backendový adaptér a hlasové
testy jsou další práce.

Prompt je v prompt.txt: drzý, vtipný prodejce pro soukromou party 18+, s občasným přátelským roastem. Výchozí model je gpt-6.1-sol s nízkým reasoning effort. Stejné scénáře
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
jeden případ. Jeden běh = jeden placený textový tah. Výsledek je v logu
a v artefaktu kufr-ai-evaluation. Všechny scénáře v cases.json jsou
fiktivní; nepoužívej osobní údaje skutečných návštěvníků.

Lokální kontrola bez placeného volání:

    python -m unittest discover -s kufr/ai-eval -p 'test_*.py'

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
