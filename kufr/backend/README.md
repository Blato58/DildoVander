# Backend kufru (E7)

ASP.NET Core 10 Minimal API pro přímé HTTPS spojení ESP32 s backendem.
E7.1–2 přijímají servisně provisionovaný device credential,
vydávají 15minutový access token, zakládají nejvýše jedno fake sezení pro zařízení
a zpracují až deset textových tahů s deterministickou odpovědí `ask`.
E7.3a–c přidávají SQLite journal technických metadat textového tahu a
založení sezení, atomickou rezervaci nejvýše deseti tahů v jednom sezení a
idempotenci po restartu. E7.4 přidává retenci a explicitní rušení. E7.5a
přidává omezený WAV upload s deterministickým fake přepisem. E7.5b
přidává fake syntézu krátkého tichého WAV pro existující textový tah.
V této verzi nejsou skutečné STT/TTS ani volání placené AI.
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
`DELETE /v1/operations/{id}` a `DELETE /v1/conversations/{id}` vyžadují stejný
access token, UUID `Idempotency-Key` a prázdné tělo. Rušení potlačí publikaci
výsledku v RAM i pozdě dokončeného tahu; již odeslaná placená práce by ale
nebyla refundována. Úspěšný DELETE key lze po restartu opakovat po dobu 24 h.
`POST /v1/conversations/{id}/transcriptions` přijímá pouze se souhlasem
`consent.audio=true` multipart s `clientTurnId`, `bootId`, `generation`,
`deviceRevision` a `wav` (`audio/wav`). WAV musí být PCM16 mono 32 kHz,
nejvýše 30 sekund / 1 920 000 bajtů vzorků; celý požadavek má strop 2 MB.
Backend vrací pevný testovací přepis se zdrojem `deviceMicrophone` bez STT.
Nahrávka ani přepis se nezapisují do SQLite; operace je device-scoped a po
restartu se její ztracený RAM výsledek neobnoví.
`POST /v1/conversations/{id}/speech` bere pouze UUID předchozího
`clientTurnId` a `outputTarget` (`device` nebo `phone`), nikdy klientský text.
Vyžaduje audio souhlas, nejvýše deset fake syntéz v jednom sezení a právě jeden
aktivní výstup. Výsledek operace nese `audioId`; autentizované
`GET /v1/audio/{audioId}` vrací tichý testovací PCM16 mono 32 kHz WAV s
metadaty a `no-store`. Obsah zůstává jen v RAM nejvýše 120 sekund; po expiraci
vrací vlastník `410`, cizí zařízení `403`. Po přehrání klient použije
`DELETE /v1/operations/{id}` k uvolnění aktivního výstupu; jinak jej uvolní
120sekundová expirace. Ukončení sezení také audio odstraní.
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
obsah sezení z RAM zmizel a nový rozhovor vyžaduje nový key. Schéma v4 přidává
`mutation_idempotency` pro DELETE retry. Migrace v1/v2/v3→v4 je dopředná a
schéma v5 přidává typ operace do technického journalu; osazené v4 řádky jsou
typ `turn`. Nákladový rozpočet placených
operací zatím chybí.
`RetentionWorker` každou minutu smaže expirované paměťové sezení/výsledky,
granty a 24hodinové technické SQLite záznamy. Sezení trvá nejvýše
15 minut nečinnosti a 30 minut od vytvoření. Ztracený výsledek se znovu
nevytváří; polling vrátí `result_expired`. Nákladové limity placených volání
a zbývající HTTP trasy doplní další E7 slices. Backend zatím
nesmí být použit pro placené tahy nebo veřejný provoz. Stav a další krok
jsou v `../docs/stav-implementace.md`.
