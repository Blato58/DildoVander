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
V této verzi nejsou skutečné STT/TTS. Textový tah má výchozí fake režim;
výslovný režim `responses` používá GPT-6.1 Sol se sdíleným party promptem,
přísným JSON schématem a kontrolou dostupné přihrádky. Před odesláním do
OpenAI transakčně rezervuje 0,05 USD, jeden backend spustí nejvýše jedno
placené volání současně a výsledek dokončí asynchronně. Nejasný výpadek
ponechá rezervaci a restart volání neopakuje. Zrušený tah nezveřejní pozdní
výsledek, ale skutečně vrácené tokeny vyúčtuje. Návrh přihrádky vzniká na
serveru s korelací a expirací 60 sekund.
Žádná trasa neovládá aktuátory.
V režimu `responses` vytváří otázky i pointu model. První dva dokončené
dialogové tahy vyžadují otázku; schema v nich nepovolí návrh přihrádky.
Dvě poslední výměny (nejvýše 500 znaků vstupu a 300 znaků odpovědi v každé)
zůstávají jen v RAM po dobu sezení a další tah je předá modelu. Ukončení či
expirace sezení je odstraní. Celé odeslané JSON tělo je omezeno na 14 kB,
takže i s historií zůstává konzervativní rezervace 0,05 USD na tah.

`responses` je zatím pouze vývojový režim pro syntetické vstupy přes loopback.
Vyžaduje `Backend__Dialog__Mode=responses`, `OPENAI_API_KEY`, všechny tři
kladné limity `Backend__Usage__*CapMicroUsd` a projektový OpenAI hard spend
limit. Samotná přítomnost klíče režim nepřepíná. Neznámá konfigurace zastaví
start; výchozí `fake` nevolá OpenAI. Před veřejným provozem chybí veřejné
HTTPS, skutečná identita zařízení, fyzická akceptace a obsahové hodnocení.

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
typ `turn`. Schéma v6 přidává technický nákladový journal a agregáty bez
obsahu. Fake operace rezervují nulu. Budoucí placený adaptér musí v transakci
vzniku operace rezervovat horní odhad a před voláním odmítnout `CostRejected`.
Bez všech tří kladných serverových hodnot `Backend__Usage__SessionCapMicroUsd`,
`Backend__Usage__DailyCapMicroUsd` a `Backend__Usage__MonthlyCapMicroUsd` se
kladná rezervace odmítne; částečná nebo neplatná konfigurace odmítne start.
Jednotkou je miliontina USD, limity jsou na sezení a globálně na UTC den/měsíc
napříč zařízeními. Po odpovědi `SettleCost` zaúčtuje skutečnost; levnější
odpověď uvolní rozdíl, vyšší cenu eviduje a zablokuje další placenou práci.
Neznámý výsledek po restartu si rezervaci ponechá. Technický audit se maže po
7 dnech, denní/měsíční agregáty po 90 dnech. Schválené limity jsou na
testovacím serveru nakonfigurovány a D61 přidal ověřenou konzervativní sazbu
pro `gpt-6.1-sol`; explicitní režim `responses` ji používá. Výchozí režim
`fake` žádné placené volání nespouští.
`RetentionWorker` každou minutu smaže expirované paměťové sezení/výsledky,
granty a 24hodinové technické SQLite záznamy. Sezení trvá nejvýše
15 minut nečinnosti a 30 minut od vytvoření. Ztracený výsledek se znovu
nevytváří; polling vrátí `result_expired`. E8 stále vyžaduje širší evaluaci
a fyzické potvrzení, backend nesmí být použit pro veřejný provoz. Stav a
další krok jsou v `../docs/stav-implementace.md`.

## Ubuntu runner a nasazení

Před první aktivací lze z GitHub Actions na větvi main ručně spustit
workflow Kufr backend host probe. Na self-hosted runneru jen přečte účet
procesu, dostupnost neinteraktivního sudo, release adresáře, instalačního
helperu, stav služby a odpověď lokálního /healthz. Nevypisuje tajemství,
nevytváří adresáře a nespouští bootstrap. Chybějící releases directory
znamená, že níže uvedený jednorázový bootstrap ještě neproběhl.

Pokud správce nemá na serveru checkout soukromého repozitáře, ruční workflow
Kufr backend bootstrap source stáhne aktuální `main` na serverovém runneru,
uloží `bootstrap-ubuntu.sh`, `deploy-helper.py` a `kufr-backend.service` do
jednoho soukromého adresáře runneru a vypíše absolutní cestu i jejich SHA-256.
Správce porovná hashe v root shellu příkazem `sha256sum CESTA/*`
a spustí `sudo bash CESTA/bootstrap-ubuntu.sh github-runner`. Soubory není nutné kopírovat do
`/root`; workflow skript samo nespouští ani nepoužívá sudo.


Workflow `.github/workflows/kufr-backend.yml` při změnách backendu v `main`
sestaví a otestuje samostatný `linux-x64` release na GitHub runneru. Ruční
`workflow_dispatch` z `main` navíc aktivuje artefakt na self-hosted runneru
se štítky `self-hosted`, `Linux`, `X64` a
`kufr-backend` v environmentu `backend-production`.
Vstup `dialog_mode` při ručním nasazení volí `fake` (výchozí) nebo `responses`;
volba se uloží do konfigurace daného release a rollback ji vrátí s releasem.
Režim `responses` používej pouze pro jednorázový vývojový test s připravenými
limity a následně znovu nasaď `fake`.
Na serveru je nutný
jednorázový bootstrap z důvěryhodné kopie repozitáře:

```bash
sudo bash kufr/backend/deploy/bootstrap-ubuntu.sh RUNNER_USER
```

`RUNNER_USER` je skutečný účet, pod kterým běží GitHub runner, nikoli název
runneru; zjistíš jej na serveru přes `ps -eo user,args | grep '[R]unner.Listener'`.
Bootstrap vytvoří odděleného systémového uživatele `kufr-backend`,
datový adresář `/var/lib/kufr-backend`, systemd unit a root-owned aktivační
helper. Sudo pro runner dovolí jen tento helper s příkazem `activate`.
Release leží v `/opt/kufr-backend/releases`; `current` se po úspěchu přepne
atomicky. Při neúspěšném startu či `GET /healthz` se obnoví předchozí release
i konfigurace. SQLite leží mimo releasy a rollback schématu databáze se
neprovádí; před změnou schématu je nutné ověřit kompatibilitu.

Environment `backend-production` má tyto GitHub Actions secrets:

| Secret | Hodnota |
| --- | --- |
| `DEVICE_ID` | Nyní `dev-simulator-1` pro vývoj; před použitím skutečného kufru jej nahradit servisně provisionovaným ID společně s hashem. |
| `DEVICE_CREDENTIAL_SHA256` | 64 hex znaků SHA-256 credential pro dané ID; nyní odpovídá vývojovému `DEV_DEVICE_CREDENTIAL`. |
| `DEV_DEVICE_CREDENTIAL` | Jen pro ruční loopback smoke nasazené vývojové instance; odpovídá hashi vývojového `DEVICE_ID`, není provisionovaný v kufru. Před fyzickým nasazením jej nahradit skutečnou identitou a oddělit testovací prostředí. |
| `OPENAI_API_KEY` | Projektový OpenAI API klíč. Čte jej pouze explicitní režim `responses`; ve výchozím `fake` se OpenAI nevolá. |
| `SESSION_CAP_MICRO_USD` | Kladný limit v miliontinách USD; spolu s dalšími dvěma limity. |
| `DAILY_CAP_MICRO_USD` | Kladný denní limit v miliontinách USD. |
| `MONTHLY_CAP_MICRO_USD` | Kladný měsíční limit v miliontinách USD. |

Po prvním úspěšném nasazení lze ručně spustit workflow **Kufr backend
deployed smoke** z `main`. S vývojovou identitou v `backend-production`
ověří přes `127.0.0.1:5080` vydání tokenu, sezení, idempotentní textový tah,
výsledek operace a ukončení sezení. Výchozí vstup `dialog_mode=fake` nevolá
OpenAI; `dialog_mode=responses` provede jeden syntetický placený tah a čeká
na jeho výsledek. Vstup musí odpovídat právě nasazenému režimu. Test
nevypisuje credential ani access token a nedokazuje veřejné HTTPS ani
spojení skutečného kufru.

Všechny hodnoty mohou pro první fake nasazení chybět. Bez dvojice zařízení
se nikdo nepřihlásí; bez všech tří nákladových limitů backend odmítne
budoucí placené rezervace. Částečná dvojice nebo trojice nasazení zastaví.
Environment je omezený na větev `main`; podle potřeby nastav požadované
schválení. Při změně secrets spusť workflow znovu; běžící služba se sama
nepřekonfiguruje. Klíč nevkládej do chatu, repozitáře ani mobilní aplikace.

Služba poslouchá pouze `http://127.0.0.1:5080`; port 5080 nemá být veřejně
otevřený. Pro kontrolu na serveru použij
`curl -fsS http://127.0.0.1:5080/healthz` a `systemctl status kufr-backend`.

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
