# Backend kufru

## Show Pack

[Provoz a společné kontrakty](../docs/show-pack.md) rozšiřují současné DV01/DV10,
nikoli vlastnictví zařízení. `Features/Show` obsahuje deterministickou režii,
immutable finále svázané s opravdovým proposal ID, duel a příjem offline výsledků.
Paměť/kronika používají stávající evening store; identita a soukromé proroctví
stávající DV10 dokument. [HTTP rozšíření](../contracts/show-openapi-v1.json)
odděluje admin/device/public/duel-side scopes. Aditivní migrace probíhá při
otevření stejných SQLite; ruční archivní retence a nejisté provider pokusy zůstávají.
Z adresáře `` použij pinned SDK přes `global.json` a
`dotnet run --project backend-tests/BackendTests.csproj -- --show`.
Pro cross-origin webového operátora nastav přesný `Show:AllowedOrigins` seznam.
`Show:DuelCommentaryEnabled` má default false; běžné testy nepoužívají poskytovatele.

Produkční rozhovor má dva enginy: **GPT Live `gpt-live-1`** (výchozí) a
**GPT Realtime 2.1 `gpt-realtime-2.1`** (výslovná alternativa). Live tvoří
běžnou řeč sám. Sol rozhoduje jen o skutečné hůlce; samostatná vision služba
zpracovává fotografie. Deterministické adaptéry jsou testovací pomůcky.

[Aktuální architektura a provozní kontrakt](../docs/autonomous-live-memory.md)
popisuje paměť, transport, migraci a akceptaci.
[privátní evidence](../docs/development-coordination.md) odděluje implementaci,
CI, nasazení a skutečný iPhone/kufr.

## Vlastnictví a API

ASP.NET Core/.NET 10 vlastní identitu rozhovoru, katalog, paměť a validované
návrhy. ESP32-S3 se připojuje přímo přes HTTPS, vlastní fyzickou sekvenci a
ověřuje dostupnost, revizi, kalibraci a Stop. Telefon není cloudový prostředník.
`GptLive` a `Realtime` vlastní protokoly; `Conversations` společný lifecycle.

- `POST /v1/conversations` založí aplikační rozhovor idempotentně.
- `POST /v1/conversations/{id}/gpt-live` nebo `/realtime-live` otevře engine.
- `GET /v1/conversations/{id}/{gpt-live|realtime-live}/{streamId}/audio-stream`
  je autentizovaný WebSocket. PCM16 mono24kHz zprávy mají `inputId`, `sequence`,
  `audio` base64; ACK potvrzuje vstup, nikoli přehrání výstupu. Fronta je omezená,
  po překročení deadline klient skončí ve vysvětlené chybě bez replay mikrofonu.
- Fyzický audio klient může používat `/audio` se stejnou posloupností; během
  WebSocketu nelze přimíchat HTTP vstup. `/played` potvrzuje klientské dohrání.
- `DELETE /v1/conversations/{id}` ukončí provider a kontext; archiv zachová.
- Diagnostika a server modes vracejí `engines.schemaVersion=1`,
  `primaryEngine=gpt-live`, přesné modely, hlasy, tempo, audio vstup a kamery.
  `ready` je místní konfigurace; přístup účtu ověří až provider handshake.

Řetězený STT/dialog/TTS, Responses dialog, skriptovaný Live a oddělený Realtime
PTT jsou vyřazeny. `/turns`, `/opening`, `/transcriptions`, `/speech` a
`/realtime-turns` vracejí **410 `conversation_engine_upgrade_required`**.
Totéž platí pro změnu starých `dialogMode`/`voiceMode`. Aktualizuj nativní IPA;
webový bundle sám nenahradí instalovaný Swift bridge. Nový firmware textové
příkazy explicitně odmítá, hlasové enginy a offline ruční řízení zůstávají.

## Fotografie

`POST /v1/conversations/{id}/image` přijímá dekódovatelný JPEG do256KiB a640×640
bez EXIF/GPS. Korelační hlavičky: `Idempotency-Key`, `Client-Turn-Id`, `Boot-Id`,
`Generation`, `Device-Revision`, `Catalog-Version`. Aktuální klient přidává
`Image-Id` UUID, `Image-Source` (`phone`, `browser`, `suitcase`), `Captured-At`,
rostoucí `Image-Revision` a `Image-Consent: true` při výslovném přiložení.
Server kontroluje vlastníka/generaci; UUID nelze přebindovat jinému rozhovoru.

Fotografie může přibýt během hovoru. Vision má vlastní souběh; chyba nezruší
hlas. Live dostává ohraničené pozorování; Realtime také skutečný `input_image`.
Text uvnitř fotografie je nedůvěryhodný. Kostým není identita ani souhlas
s osobním roastem. `PUT /costume-context` opraví popis. `DELETE /image` se
stejnou korelací a `Image-Revision` zneplatní i opožděný upload dané revize,
ponechá hlas běžet a zachová archiv. Explicitní smazání uloženého obsahu:
`DELETE /v1/admin/conversations/{id}/media?deviceId=...`. Správce se autentizuje
odděleně; aktivní provider obsahující smazané údaje se ukončí.

## Lokální vývoj a ověření

Soukromý `Backend__DatabasePath` patří mimo checkout, provider klíč pouze na
backend. `.env.example` se automaticky nenačítá. Bez `OPENAI_API_KEY` server
běží, oba enginy hlásí nedostupnost. Staré dialog/voice přepínače nic nezapínají.
OpenAI SDK2.14.0 obsluhuje Responses/Realtime; izolovaný Live používá oficiální
WS protokol. Úzké vision/decision/transcription schopnosti nejsou další engine.

```powershell
dotnet build backend-tests/BackendTests.csproj
dotnet run --project backend-tests/BackendTests.csproj -- --conversation-engines
dotnet run --project backend-tests/BackendTests.csproj -- --autonomous-memory
dotnet run --project backend-tests/BackendTests.csproj -- --memory
dotnet run --project backend-tests/BackendTests.csproj -- --image
dotnet run --project backend-tests/BackendTests.csproj -- --character
```

Fixture testy nevolají providera a nedokazují skutečný poslech/kameru/pohony.
Placený test vyžaduje autorizaci. Starý dialogue smoke není akceptace nových enginů.

Při změně NuGet závislostí regeneruj oba lockfiles společně přes
`dotnet restore backend-tests/BackendTests.csproj -p:SelfContained=true --force-evaluate`.
Zachovej deklarované `RuntimeIdentifiers` (`linux-x64;win-x64`) a potom ověř
stejný příkaz s `--locked-mode` místo `--force-evaluate`. Microsoft.Data.Sqlite
je aktuálně připnutý na10.0.13; OpenAI SDK zůstává2.14.0.
Dependabot zahrnuje oba projekty; [automatizace generovaných závislostí](../docs/ci-cd.md#generovane-zavislosti-v-dependabot-pr)
oba lockfiles přegeneruje a commitne do jeho větve ještě před finálním CI.

## Nasazení a rollback

Používej existující CI a artefakt ověřeného Git tree. Před první aktivací udělej
[soukromou konzistentní zálohu a migraci](../docs/autonomous-live-memory.md#aktivace-ab-a-rollback).
Databáze se neresetuje, nové tabulky vznikají aditivně. Záloha není GitHub
artefakt. Starší release s automatickou retencí nesmí zpracovat jedinou kopii archivu.

Runner `[self-hosted,Linux,X64,kufr-backend]`, existující `ci.yml` a soukromý
deploy workflow používají ověřený `build_run_id`. Cesty:
`/opt/kufr-backend/current`, `/opt/kufr-backend/releases`,
`/etc/kufr-backend/backend.env`, `/var/lib/kufr-backend`; služba
`kufr-backend.service`, loopback `127.0.0.1:5080`. Před změnou konfiguraci
zálohuj; po aktivaci ověř službu, health, capabilities a data. Diagnostika:
[codex návod](codex/README.md). Rollback vrací předchozí kompatibilní release,
nikdy neobnovuje třetí engine v aktuálním runtime.

## Podepsaná firmware distribuce (D158)

Veřejné read-only `GET /v1/firmware/manifests/{channel}.json` a
`GET /v1/firmware/artifacts/{sha256}.bin` nevyžadují device token. Autoritu
určuje P-256 podpis provisionovaný na ESP, nikoli samotný checksum.
`Backend:Firmware:Directory` je absolutní admin-owned adresář; neplatná
konfigurace/release vrací 404. Shared concurrency limit 4, queue 0, 429.
Root-only publikace ověřeného CI artefaktu udržuje podpisové klíče mimo
runner/source/image. [Kontrakt a přesný deployment](../docs/firmware-ota.md).



### Veřejné HTTPS na IP adrese

Workflow **Kufr backend IP HTTPS** na `main` má ruční režim `stage`, který
na serverovém runneru připraví přesně commitované soubory z
`deploy/ip-https/` v soukromém adresáři a vypíše cestu a SHA-256. Skript se
automaticky nespustí s root právy. Správce ověří hash a spustí jej v root
shellu jako `sudo bash CESTA/bootstrap-ip-https.sh 2.28.202.220`.
Předpoklady: běžící loopback backend, volné porty 80/443 a příchozí TCP
80/443 povolené i ve firewallu poskytovatele. Skript odmítne cizí Nginx site,
nainstaluje Nginx a Certbot 5.4+ ze snapu, vystaví veřejně důvěryhodný
krátkodobý certifikát s IP SAN přes HTTP-01 webroot a zapne HTTPS proxy.
Port 80 slouží obnově certifikátu a jinak přesměrovává na HTTPS. Nginx
nikdy neposílá provoz přímo na veřejný port backendu.

`kufr-cert-renew.timer` kontroluje obnovu dvakrát denně; deploy hook po
obnově otestuje a znovu načte Nginx. Bootstrap ověří lokální důvěru TLS,
IP SAN a zkušební obnovu. Režim workflow `verify` z GitHub runneru ověří
veřejné `https://IP/healthz`, důvěru IP certifikátu, alespoň 48 hodin
platnosti a HTTP přesměrování. Stejná kontrola běží denně s repository
variable `KUFR_BACKEND_PUBLIC_IPV4`. Bez úspěšného veřejného běhu se B4
nepovažuje za ověřené. Certifikát je krátkodobý, proto je průběžná obnova
součástí provozu. Při chybě lze zastavit jen Nginx (`systemctl stop nginx`);
backend zůstane dostupný na loopbacku pro runner/deploy diagnostiku.

### Safari instalace iOS aplikace

Po ověření veřejného HTTPS připrav režimem `stage` téhož workflow aktuální
soubory a jako root spusť `sudo bash CESTA/install-ios-host.sh 2.28.202.220
github-runner`. Skript přidá do vlastního Nginx site pouze statickou cestu
`/ios/`, vytvoří stránku „podepsaná verze se připravuje“ a při chybě
konfigurace vrátí předchozí site. Adresář `/var/www/kufr-ios` vlastní
serverový runner, který smí publikovat pouze statické soubory. Přístup k
backendovým secrets ani k portu 5080 stránka nemá.

Po úspěšném Ad Hoc podpisu **Build signed Kufr IPA** samostatné workflow
**Deploy Kufr IPA** se vstupem ID podepsaného buildu nahraje do tohoto adresáře
verzovanou IPA, `manifest.plist`, SHA-256 a instalační stránku. Změna
hlavní stránky proběhne atomicky až po uložení celé verze. Samostatný
job na témže serverovém runneru ověří přes veřejné HTTPS stránku, manifest, MIME typy,
staženou IPA a checksum. Instalaci `itms-services` a spuštění může potvrdit
jen registrovaný iPhone v Safari; serverový test to nenahrazuje a neprokazuje
dostupnost z jiné sítě. Soubory
pod `/ios/` jsou veřejně čitelné, proto IPA nesmí obsahovat tajemství.

Responses požadavky explicitně používají `service_tier: default`. Jiný nebo chybějící vrácený tarif se odmítne a drží rezervaci (včetně rezervy pro 2× sazbu Fast), bez automatického opakování. Potvrzený Standard se vypořádá konzervativně podle tokenů; tato interní částka není účetní údaj z OpenAI. Ochrana tarifu byla přidána po živém eval, který vracel Fast při neuvedeném tarifu.

Úplný postup a omezení opakovaného použití artefaktů jsou v [CI/CD návodu](../docs/ci-cd.md).

## Serverové logy a společný debug D118

Backend ukládá technické události do oddělené SQLite, výchozí historie 24 h
a 5000 záznamů. `GET /v1/admin/logs` a `/v1/admin/logs/status` používají
credential správce nebo samostatný reader token; device access nestačí.
Každý požadavek vrací `X-Request-Id`, Realtime zapisuje i fáze/chyby uvnitř
streamu. Úspěšné GET a audio uploady se vynechávají. Bez textů/audio/tajemství.

[Codex integrace a API](codex/README.md) popisují kurzory, filtry, úložiště,
MCP, společný postup s iOS a GitHub workflow **Kufr server logs**. Pro přímé
MCP lze v `backend-production` přidat volitelný secret `LOG_READER_CREDENTIAL`;
deploy uloží jeho hash. Bez něj funguje iOS/admin a GitHub čtení přes existující
admin secret. Čtení neprovádí provider volání ani změny serveru.
