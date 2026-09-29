# Backend kufru (E7)

ASP.NET Core 10 Minimal API pro přímé HTTPS spojení ESP32 s backendem.
Současné E7.1–2 slices přijímají servisně provisionovaný device credential,
vydávají 15minutový access token, zakládají nejvýše jedno fake sezení pro zařízení
a zpracují až deset textových tahů s deterministickou odpovědí `ask`.
E7.3a–b přidávají SQLite journal technických metadat textového tahu a
atomickou rezervaci nejvýše deseti tahů v jednom sezení.
V této verzi nejsou STT/TTS ani volání placené AI.
Žádná trasa neovládá aktuátory.

## Vývojové spuštění

SDK je připnuté v `../global.json`; .NET 10 ASP.NET Core runtime je nutný.
Pro běh nastav `Backend__DatabasePath` na absolutní cestu k místnímu
SQLite souboru mimo git (adresář musí existovat). Na Windows PowerShell:

```powershell
$databaseDirectory = Join-Path $env:LOCALAPPDATA 'KufrBackend'
New-Item -ItemType Directory -Path $databaseDirectory -Force | Out-Null
$env:Backend__DatabasePath = Join-Path $databaseDirectory 'journal.sqlite'
```

Backend bez cesty odmítne start; žádný soubor s daty ani credential není
součástí repozitáře.
Z adresáře `kufr/backend/`:

```text
dotnet build backend.csproj
dotnet run --launch-profile https
node tests/smoke.mjs
dotnet run --project ../backend-tests/BackendTests.csproj
```

HTTPS profil používá `https://localhost:7043` a důvěryhodný místní
vývojový certifikát. Smoke test sám spouští krátkodobý backend pouze na
`127.0.0.1`, vytváří náhodné credentials a ověřuje skutečné HTTP odpovědi.
Test je samostatný proces, vytvoří dočasný SQLite soubor a nepotřebuje
poskytovatele AI.
Samostatný C# test s řízeným časem ověřuje expiraci tokenu/sezení a retry.
Používá dočasný SQLite soubor a ověřuje také atomický souběh rezervací a
znovuotevření po restartu. `Microsoft.Data.Sqlite` je připnutý na `10.0.12`.

Zařízení se registrují mimo HTTP API konfigurací `Devices__0__DeviceId`
a `Devices__0__CredentialSha256` (64 hex znaků SHA-256 credential).
V repozitáři není žádný credential ani ukázkový platný hash. Unikátní
náhodný credential se zadá do zařízení servisním postupem; hash se přidá
do konfigurace backendu mimo git. Další zařízení používá index `1` atd.
Bez konfigurace zařízení se backend spustí, ale token nevydá.
Duplicitní ID nebo hash credential zabrání startu. Na jedno zařízení lze mít
nejvýše 16 současně platných access tokenů; další výměna vrátí 429.

`POST /v1/device-access/token` přijímá provisionovaný credential jako bearer.
`POST /v1/conversations` a `POST /v1/conversations/{id}/turns` vyžadují
vydaný access token, UUID hlavičku `Idempotency-Key` a tělo dle
`../contracts/backend-openapi-v1.json`. `GET /v1/operations/{id}` vrací
fake operaci ve stavu `completed` s identitami požadavku, pevnou odpovědí
`ask` a `proposal:null`. Text návštěvníka se neukládá do operace ani logu.
Odpovědi `/v1` mají `Cache-Control: no-store`; chyby používají
`application/problem+json` se stabilním `code` a `traceId`.

Sezení, tokeny a fake obsah výsledku jsou jen v paměti jedné instance.
SQLite drží hash těla, idempotency key, identifikátory, status a 24hodinovou
expiraci textové operace, nikdy text návštěvníka. Po restartu lze opakovat
stejný turn key bez druhého požadavku. Rozběhnutá rezervace přejde na
`unknown`; již dokončený, ale z RAM ztracený výsledek se čte jako
`failed/result_expired`. Nové tahy starého sezení nejsou po restartu možné.
Soubor `journal.sqlite.instance.lock` dovoluje právě jeden běžící backend
nad databází; druhá instance skončí chybou před recovery.
Schéma v2 přidalo `conversation_turn_usage` a atomicky zapisuje kvótu spolu
s pending operací a idempotency key. Schéma v3 přidává
`conversation_creates`: create key, hash těla, původní ID odpovědi a expirace
přežijí restart. Po restartu je starý conversationId pouze identitou retry;
obsah sezení z RAM zmizel a nový rozhovor vyžaduje nový key. Migrace v1/v2→v3
je dopředná a přidává pouze tabulky/indexy. Nákladový rozpočet placených
operací zatím chybí.
`RetentionWorker` každou minutu smaže expirované paměťové sezení/výsledky,
granty a 24hodinové technické SQLite záznamy. Sezení trvá nejvýše
15 minut nečinnosti a 30 minut od vytvoření. Ztracený výsledek se znovu
nevytváří; polling vrátí `result_expired`. Explicitní rušení operací,
nákladové limity placených volání a zbývající HTTP trasy doplní další E7
slices. Backend zatím
nesmí být použit pro placené tahy nebo veřejný provoz. Stav a další krok
jsou v `../docs/stav-implementace.md`.
