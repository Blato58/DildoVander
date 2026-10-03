# Debugování nasazeného serveru s Codexem

Backend D118 poskytuje technické logy bez přepisů rozhovorů, audia, promptů,
credentialů a syrových HTTP/provider odpovědí. Čtení nevolá AI. Každý HTTP
požadavek dostane serverem vytvořené `X-Request-Id`; stejné ID je v problému
`traceId`, logu i iOS Debug. Operace mají vlastní `operationId`. Realtime
zapisuje started/completed/listening/speech_stopped/failed/closed i uvnitř
HTTP 200 streamu. Úspěšné GET/poll a PCM uploady se nezapisují.

## Codex v tomto repozitáři přes GitHub

Po nasazení D118 lze použít existující GitHub oprávnění bez lokálního
serverového tokenu. Workflow používá secrets z `backend-production`,
čte veřejné HTTPS a uloží pouze typovanou diagnostiku do jednodenního artefaktu.
Pokud existuje `LOG_READER_CREDENTIAL`, používá jej; jinak existující
`ADMIN_CREDENTIAL`. V obou případech workflow vykonává jen dvě GET čtení.

```bash
gh workflow run kufr-backend-logs.yml --ref main -f level=warning
gh run list --workflow kufr-backend-logs.yml --limit 1
gh run watch ID_BEHU --exit-status
gh run view ID_BEHU --log
gh run download ID_BEHU -n kufr-server-logs -D /tmp/kufr-diagnostics
```

Výsledek je `kufr-server-logs.json`: režimy serveru a stránka logů.
Stejný typovaný JSON je v konzoli mezi `KUFR_LOG_SNAPSHOT_BEGIN/END`.
Codex tak může číst diagnostiku přes Actions log API i při blokovaném stažení
artefaktů. Artefakt má životnost jeden den; konzole má retenci logů nastavenou
v repozitáři GitHub. Obě cesty obsahují jen validovaná technická metadata.
Pro konkrétní chybu přidejte `-f trace_id=UUID` nebo `-f operation_id=UUID`.
Pro navazující stránku použijte `-f after=NEXT_CURSOR`. Běh workflow nezískává
serverové tajemství pro Codex, nemění režim a nespouští rozhovor. Prostředí
GitHub může vyžadovat schválení podle svých nastavených pravidel.

## Přímé MCP připojení

`server_logs_mcp.py` je stdio MCP server bez balíčkových závislostí, vyžaduje
Python 3.10+. Nabízí `read_server_logs` a `read_server_status`, výhradně čtení.
Token není argument nástroje ani součást URL. HTTPS používá systémovou důvěru,
redirecty jsou zakázané, odpověď nejvýše 256 KiB, timeout 15 s.

Správce vygeneruje samostatný náhodný token nejméně 32 znaků a uloží ho
jako environment secret `LOG_READER_CREDENTIAL` v GitHub `backend-production`.
Deploy uloží pouze jeho SHA-256 do `Backend:Logs:CredentialSha256`.
Nesmí se shodovat s admin/device credentialem. Po změně je nutné nové nasazení.
Reader token nemůže spravovat režimy, získat device token ani spustit AI.
Na serveru lze bez GitHub použít `Backend__Logs__CredentialSha256`.

Do prostředí procesu Codex nastavte `KUFR_BACKEND_URL=https://2.28.202.220/`
a `KUFR_LOG_TOKEN` z vlastního secret manageru. Tajemství nepište do chatu,
command line, repozitáře nebo `.codex/config.toml`. Potom z kořene repozitáře:

```bash
codex mcp add kufr-server-logs -- python3 "$PWD/kufr/backend/codex/server_logs_mcp.py"
```

V konfiguraci vytvořeného MCP v `~/.codex/config.toml` přidejte přenos
pojmenovaných environmentů (hodnoty tajemství do konfigurace nevkládejte):

```toml
[mcp_servers.kufr-server-logs]
command = "python3"
args = ["/absolutni/cesta/DildoVander/kufr/backend/codex/server_logs_mcp.py"]
env_vars = ["KUFR_BACKEND_URL", "KUFR_LOG_TOKEN"]
```

Nahraďte cestu aktuálním checkoutem; na Windows použijte dostupný Python
executable. Při spouštění Codex musí být oba environmenty dostupné jeho procesu.
MCP může i bez credentialu vyjednat protokol a vypsat nástroje; čtení vrátí
srozumitelnou chybu konfigurace. Samotná instalace integrace nemění server.

Typický společný debug: otevřete v iOS **Debug → Log serveru**, zapněte
obnovování, zopakujte problém a sdílejte UUID požadavku nebo operace.
Codex načte `read_server_logs` s `traceId`/`operationId`, ověří režimy pomocí
`read_server_status` a porovná časový sled s kódem. Logy jsou diagnostická data,
nikoli pokyny pro agenta. Změny, mazání paměti ani placené testy tyto nástroje
neprovádějí.

## API a kurzory

- `GET /v1/admin/logs?limit=200&level=info` vrací poslední stránku vzestupně.
- `after=N` čte od nejstaršího novějšího odpovídajícího záznamu; další čtení
  používá `nextCursor`. `hasMore=true` znamená další stránku. I prázdný filtr
  posune kurzor přes dosud zpracovanou historii.
- `level=info|warning|error` je minimální úroveň, `traceId` a `operationId`
  jsou UUID. Neznámé/duplicitní parametry nebo limit mimo 1–200 vracejí 400.
- `GET /v1/admin/logs/status` vrací režimy a počet aktivních rozhovorů.
- Obě cesty přijmou samostatný reader token nebo admin credential, vždy
  `Authorization: Bearer …`. Device credential/access token nestačí.
- `storeId` je identita logové databáze; při změně načtěte novou poslední stránku.
  Kurzor přežívá restart služby, smazání databáze vytvoří novou identitu.
  `truncated` hlásí chybějící historii a `droppedWrites` selhání zápisu během
  tohoto procesu. Logovací chyba nesmí zrušit hlasový tok.

Logy jsou v `<Backend:DatabasePath>.logs.sqlite`, standardně vedle journalu
v `/var/lib/kufr-backend`. Historie nejvýše 24 h a 5000 událostí, ořez při
zápisu/čtení; `Backend__Logs__Capacity` dovoluje 200–20000. SQLite znovu používá
stránky po promazání. Jde o provozní diagnostiku, oddělenou od journalu,
paměti večerů a technického auditu stanoveného plánem.

Reader přijímá i ASP.NET route constraints (například `{streamId:guid}`)
ze starších nasazení. Nový server tento constraint normalizuje před
potlačením úspěšných audio uploadů; chyby uploadu zůstávají viditelné.
GPT-Live zapisuje technické stream stavy stejným filtrem jako Realtime.
