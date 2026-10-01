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
D106 přidává volitelné skutečné STT/TTS adaptéry za explicitní přepínač.
Textový tah má výchozí fake režim;
výslovný režim `responses` používá GPT-6.1 Sol se sdíleným party promptem,
šesticí hůlek z `../ai-eval/wand-catalog.json`, přísnými schématy nativních funkcí a kontrolou dostupné přihrádky. Model dostává jen dostupné položky a hraje mluvící kouzelný kufr Ollivanderova obchodu. Před odesláním do
OpenAI transakčně rezervuje nejméně 0,10 USD pro Sol (více podle paměti), jeden backend spustí nejvýše jedno
placené volání současně a výsledek dokončí asynchronně. Nejasný výpadek
ponechá rezervaci a restart volání neopakuje. Zrušený tah nezveřejní pozdní
výsledek, ale skutečně vrácené tokeny vyúčtuje. Návrh přihrádky vzniká na
serveru s korelací a expirací 60 sekund.
Žádná trasa neovládá aktuátory.
V režimu `responses` vytváří otázky i pointu model. První čtyři dokončené
dialogové tahy vyžadují otázku; nabídka funkcí v nich neobsahuje výběr přihrádky.
Dvě poslední výměny zůstávají v RAM pro aktuální tah. Celé dokončené textové
výměny se navíc ukládají do `Backend:DatabasePath` + `.evening.sqlite`.
Tato paměť je oddělená podle zařízení, přežije konec sezení i restart a nemá
časovou expiraci. Po 2000 výměnách nebo 2 MB UTF-8 textu vytěsňuje nejstarší
výměny; status uvádí počet vytěsnění. Žádné audio ani fotografie neukládá.
Soubor má být ve stejném soukromém adresáři služby jako journal, ale vynechaný
z technických záloh. SQLite používá secure_delete a rollback journal; smazání
není zárukou odstranění kopií v externích zálohách nebo u poskytovatele.

Model dostane nejvýše 16000 znaků vybraných původních replik: aktuální
rozhovor, starší rozhovory podle shody slov ve jméně/odpovědi a nedávný kontext.
Jde o lexikální vybavení, ne neomylný profil člověka ani biometrickou identitu.
Dva Petrové vyžadují upřesnění. Jména/opravy musí vycházet z řečeného.
Obsah paměti se nikdy nepovažuje za instrukce či aktuální dostupnost hůlek.
Limit základního JSON těla je 14 kB plus JSON-encoded velikost paměti;
rezervace před voláním je 0,10 USD plus cache-write cena Fast
přidaných bajtů. Všechny tři stávající nákladové stropy zůstávají účinné.

`POST /v1/conversations/{id}/opening` přijímá pole textového tahu **bez text**.
Vytvoří běžnou pollovatelnou turn operaci: kufr promluví první a zeptá se na
jméno bez představování. Po prvním přijatém tahu se nový opening odmítne;
retry se stejným klíčem vrací tutéž operaci. Starý `/turns` zůstává kompatibilní.
Firmware má explicitní vstup opening a ukončení sezení (D75); reálný vstup návštěvníka a samostatný hlas vyžadují dokončení E4/E9.

`GET /v1/evening-memory` vrací pro device token `eveningId`, počty a limity,
nikoli obsah. Obsluha smaže konkrétní večer pomocí
`DELETE /v1/evening-memory/{eveningId}`, prázdného těla a UUID `Idempotency-Key`.
Smazání také ukončí aktivní sezení; zrušená pozdní odpověď nesmí nic obnovit.
Stará/cizí ID vrací neškodné 204; nemohou smazat nový večer nebo jiné zařízení.
Samotné `DELETE /v1/conversations/{id}` paměť večera nemaže. UI tlačítko a
lokální autorizovaná cesta ze spárovaného telefonu jsou ještě integrace E10.
Technický journal neobsahuje text. Zápis paměti předchází publikaci odpovědi;
při pádu mezi dvěma SQLite commity může obsahovat vygenerovanou repliku,
kterou zařízení nepřevzalo. Ani dokončený tah není důkazem přehrání zvuku.

`responses` je zatím pouze vývojový režim pro syntetické vstupy přes loopback.
Vyžaduje `Backend__Dialog__Mode=responses`, `OPENAI_API_KEY`, všechny tři
kladné limity `Backend__Usage__*CapMicroUsd` a projektový OpenAI hard spend
limit. Samotná přítomnost klíče režim nepřepíná. Neznámá konfigurace zastaví
start; výchozí `fake` nevolá OpenAI. Veřejné HTTPS je dostupné; skutečná identita zařízení, fyzická akceptace a obsahové hodnocení návštěvníky zůstávají otevřené.

## Vývojové spuštění

D109 přidává `GET/PUT /v1/admin/modes` za samostatným raw bearer credential
správce. Server zná jen `Backend:Admin:CredentialSha256`; přístup se nikdy
neodvozuje z device access tokenu. Hash ani rezervované ID `debug-server-admin`
nesmí kolidovat s `Devices` (start se odmítne). Credential správce se může
vyměnit za běžný token této oddělené testovací identity, nikdy identity ESP.
GET vrací revizi, dialog/hlas režimy, dostupnost providerů, počet živých sezení
a `lifetime=until_restart`. PUT přijímá `expectedRevision`, `dialogMode`
(`fake/responses`) a `voiceMode` (`fake/openai`). Vrací potvrzený stav; chybná
revize, živý rozhovor/doznívající provider či nedostupný provider vrací 409.
Změna a create sdílejí zámek a nikdy nerozdělí probíhající rozhovor mezi režimy.
Stejný stav je povolený no-op. Režimy nejsou trvalé; po restartu platí konfigurace
nasazení a nová revize. Přepnutí nemění model Sol, credential, limity ani paměť
večera a samo nevolá OpenAI. Ve fake režimu mohou být klienti připraveni, ale
žádný provider se nepoužije.

Ruční deploy očekává silný samostatný secret `ADMIN_CREDENTIAL` v environmentu
`backend-production`. Workflow odvodí hash do konfigurace release; provider
klíč stále zapisuje stávající root helper. Parametr `voice_mode` doplňuje
dosavadní `dialog_mode`. Veřejný `server_mode_smoke.py` ověřuje admin auth,
izolovaný debug token, no-op PUT, stale revizi a diagnostics přes běžné PKI;
nemění produkční režimy ani nevytváří rozhovor/placené provider volání.

D108 přidává autentizované `GET /v1/diagnostics` s provider režimy, aktuálním
katalogem a audio formátem. Endpoint používá device access token, `no-store`,
nevrací žádné tajemství/obsah paměti a sám nevolá AI. iOS Debug může pro test
opening → speech → telefon použít samostatné provisionované zařízení `debug-*`.
Registrace a hash credential používají stávající `Devices` konfiguraci;
provider klíč zůstává pouze na serveru. Testy mají stejné kvóty a paměť večera
jako ostatní sezení a žádné zvláštní právo převzít aktivní sezení. Příprava kódu
neznamená jeho nasazení. Aktuální nasazení D109, provider režimy a ověření
eviduje [stav implementace](../docs/stav-implementace.md).

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
`ask` a `proposal:null`. Text návštěvníka se neukládá do technické operace ani logu; dokončený textový tah patří do oddělené paměti večera.
`DELETE /v1/operations/{id}` a `DELETE /v1/conversations/{id}` vyžadují stejný
access token, UUID `Idempotency-Key` a prázdné tělo. Rušení potlačí publikaci
výsledku v RAM i pozdě dokončeného tahu; již odeslaná placená práce by ale
nebyla refundována. Úspěšný DELETE key lze po restartu opakovat po dobu 24 h.
`POST /v1/conversations/{id}/transcriptions` přijímá pouze se souhlasem
`consent.audio=true` multipart s `clientTurnId`, `bootId`, `generation`,
`deviceRevision` a `wav` (`audio/wav`). WAV musí být PCM16 mono 32 kHz,
nejvýše 30 sekund / 1 920 000 bajtů vzorků; celý požadavek má strop 2 MB.
Ve výchozím `fake` režimu backend vrací pevný testovací přepis se zdrojem
`deviceMicrophone`; `openai` použije skutečný STT adaptér popsaný níže.
Nahrávka ani přepis se nezapisují do SQLite; operace je device-scoped a po
restartu se její ztracený RAM výsledek neobnoví.
`POST /v1/conversations/{id}/speech` bere pouze UUID předchozího
`clientTurnId` a `outputTarget` (`device` nebo `phone`), nikdy klientský text.
Vyžaduje audio souhlas, nejvýše deset syntéz v jednom sezení a právě jeden
aktivní výstup. Výsledek operace nese `audioId`; autentizované
`GET /v1/audio/{audioId}` vrací PCM16 mono 32 kHz WAV (ve fake režimu ticho) s
metadaty a `no-store`. Obsah zůstává jen v RAM nejvýše 120 sekund; po expiraci
vrací vlastník `410`, cizí zařízení `403`. Po přehrání klient použije
`DELETE /v1/operations/{id}` k uvolnění aktivního výstupu; jinak jej uvolní
120sekundová expirace. Ukončení sezení také audio odstraní.
Odpovědi `/v1` mají `Cache-Control: no-store`; chyby používají
`application/problem+json` se stabilním `code` a `traceId`.

## Hlasové adaptéry E9.1

`Backend__Voice__Mode=openai` zapne nezávisle na textovém dialogu
STT `gpt-transcribe` (český vstup) a TTS `gpt-4o-mini-tts-2025-12-15`,
hlas `marin` s českými instrukcemi. Vyžaduje `OPENAI_API_KEY` a všechny tři
kladné nákladové limity uvedené níže. Výchozí `fake` nevolá poskytovatele;
neznámý režim nebo chybějící předpoklad zastaví start. TTS čte pouze uloženou
validovanou repliku dokončeného dialogu, nikoli libovolný klientský text.

Trasy a schéma v1 se nemění. Hlasové operace vrací `202` a pollují se stejně
jako dialog. Jeden backend dovolí nejvýše jedno placené volání současně
napříč dialogem, STT a TTS. Každé audio volání má 15sekundový deadline;
nejasný výpadek se neopakuje. Zrušení, konec sezení, shutdown nebo pozdní
odpověď nesmějí publikovat výsledek. Worker vlastní samostatnou omezenou
kopii uploadu, kterou po dokončení vynuluje. Nahrávky ani generované audio
se neukládají do SQLite; přepis v RAM lze následně předat textovému dialogu.

Poskytovatel vrací [PCM 24 kHz, mono, signed 16-bit little-endian](https://developers.openai.com/api/docs/guides/text-to-speech).
Backend provede skutečný filtrovaný převod na 32 kHz a vytvoří WAV;
nepřeznačuje pouze hlavičku. Přijme nejvýše 30 sekund zvuku. Delší výsledek
odmítne celý bez částečného přehrávání; i platná 500znaková replika může tento
limit překročit a již odeslané volání může být účtované. Délku a český projev
je nutné změřit při autorizované živé hlasové evaluaci.

Před dispatch rezervuje STT 0,01 USD a TTS 0,05 USD. Jde o konzervativní
vývojové odhady, nikoli ověřené skutečné účty nebo zaručený cenový strop
poskytovatele. Bez spolehlivého billing údaje zůstávají rezervace započtené
i po úspěchu či chybě; neodeslaná práce se vypořádá nulou. Projektový hard
spend limit zůstává nutný. Testy používají falešný HTTP provider a syntetické
PCM, ověřují spektrum/délku převodu, kontrakty, rušení, TTL a nákladové
rezervace. Neprokazují reálný český hlas ani reproduktor ESP32.

Tento krok nemění konfiguraci běžícího serveru ani nasazovací helper.
Fyzické PTT/I2S a firmware audio tok jsou navazující části E9.

Sezení, tokeny a fake obsah výsledku jsou jen v paměti jedné instance.
SQLite drží hash těla, idempotency key, identifikátory, status a 24hodinovou
expiraci textové operace, nikdy text návštěvníka; ten má oddělený obsahový soubor popsaný výše. Po restartu lze opakovat
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


Workflow `.github/workflows/kufr-backend.yml` při změnách backendu v PR i `main`
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

Po nasazení lze spustit **Kufr backend deployed smoke**: fake režim ověřuje
technický tah, responses režim pouze opening. **Kufr backend public dialogue**
ověřuje jeden placený opening, autentizaci, idempotentní retry, poll a ukončení
přes veřejné HTTPS. Tyto smoke testy nehodnotí humor.

**Kufr backend interactive dialogue** slouží pro skutečné vedení rozhovoru.
Každý ruční běh vykoná jedinou z akcí `start`, `reply`, `finish`, `status`, `clear`.
`start` založí sezení a vyvolá opening; artefakt `interactive-dialogue` obsahuje
veřejný stav a skutečnou repliku. Člověk/agent si ji přečte a až poté zadá další
`reply` s vráceným `state`, vlastním textem a novým UUID `request_id`. Retry
původního UUID a stejného vstupu používá stejné idempotentní klíče. Žádný tajný
credential/token není vstupem ani součástí artefaktu; zůstává v runneru.
`finish` uvolní sezení, paměť zůstává. `status` ukazuje limity/počty večera;
`clear` vyžaduje přesné aktuální `expected_evening_id`. Před mazáním ověř, že jde
výhradně o vlastní testovací data. Pro testování platí schválený souhrnný limit
1 USD a stávající měsíční limit 10 USD; ceníkový odhad není skutečná fakturace.

Nasazení volí `dialog_model=gpt-6.1-sol`; backend čte
`Backend__Dialog__Model` a jiný model v responses režimu odmítne. Funkce
`continue_conversation`, `propose_wand`, `end_conversation` se mapují na stávající
`ask/propose/no_selection`. Terminální odpověď zůstává pollovatelná, její replay
nevolá model; nový tah dostane 409 `conversation_finished`, dokud ESP32 neprovede
DELETE. AI sama vybírá hůlku. Místní autonomní brána ESP32 provede prezentaci
pevné rekvizity a návrat; nevyžaduje potvrzení obsluhy. Stop a poruchy mají přednost.

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

### Safari instalace iOS aplikace

Po ověření veřejného HTTPS připrav režimem `stage` téhož workflow aktuální
soubory a jako root spusť `sudo bash CESTA/install-ios-host.sh 2.28.202.220
github-runner`. Skript přidá do vlastního Nginx site pouze statickou cestu
`/ios/`, vytvoří stránku „podepsaná verze se připravuje“ a při chybě
konfigurace vrátí předchozí site. Adresář `/var/www/kufr-ios` vlastní
serverový runner, který smí publikovat pouze statické soubory. Přístup k
backendovým secrets ani k portu 5080 stránka nemá.

Po úspěšném Ad Hoc podpisu workflow **Kufr iOS** nahraje do tohoto adresáře
verzovanou IPA, `manifest.plist`, SHA-256 a instalační stránku. Změna
hlavní stránky proběhne atomicky až po uložení celé verze. Samostatný
GitHub-hosted job ověří přes veřejné HTTPS stránku, manifest, MIME typy,
staženou IPA a checksum. Instalaci `itms-services` a spuštění může potvrdit
jen registrovaný iPhone v Safari; serverový test to nenahrazuje. Soubory
pod `/ios/` jsou veřejně čitelné, proto IPA nesmí obsahovat tajemství.

Responses požadavky explicitně používají `service_tier: default`. Jiný nebo chybějící vrácený tarif se odmítne a drží rezervaci (včetně rezervy pro 2× sazbu Fast), bez automatického opakování. Potvrzený Standard se vypořádá konzervativně podle tokenů; tato interní částka není účetní údaj z OpenAI. Ochrana tarifu byla přidána po živém eval, který vracel Fast při neuvedeném tarifu.
