# Backend kufru (E7)

ASP.NET Core 10 Minimal API pro přímé HTTPS spojení ESP32 s backendem.
Současné E7.1–2 slices přijímají servisně provisionovaný device credential,
vydávají 15minutový access token, zakládají nejvýše jedno fake sezení pro zařízení
a zpracují až deset textových tahů s deterministickou odpovědí `ask`.
V této verzi nejsou STT/TTS, SQLite ani volání placené AI.
Žádná trasa neovládá aktuátory.

## Vývojové spuštění

SDK je připnuté v `../global.json`; .NET 10 ASP.NET Core runtime je nutný.
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
Test je samostatný proces a nepotřebuje poskytovatele AI ani databázi.
Samostatný C# test s řízeným časem ověřuje expiraci tokenu/sezení a retry.

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

Sezení, operace a idempotence jsou nyní jen v paměti jedné instance. Po restartu
se tokeny i sezení ztratí; trvalou atomickou idempotenci, limity placených
operací, cleanup timer, rušení operací a retenci doplní další E7 slices. Backend zatím
nesmí být použit pro placené tahy nebo veřejný provoz. Stav a další krok
jsou v `../docs/stav-implementace.md`.
