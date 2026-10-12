# Vývoj bez kompletního kufru

Příkazy níže nepotřebují produkční credentialy ani volání OpenAI. Používej
syntetická data. Verze vybírej podle `kufr/global.json`, jednotlivých `.nvmrc`,
lockfiles a `platformio.ini`; aktualizace se provádí společně s ověřením.
Tabulka uvádí možnosti ověření, nikoli tvrzení o spuštění na každé platformě.

| Část | Nástroje | Co lze ověřit bez zařízení |
| --- | --- | --- |
| Mobilní web a kontrakty | Node.js podle `.nvmrc`, npm | TypeScript, webový build, fixtures a automatické JS testy |
| Backend | .NET SDK podle `kufr/global.json`, Node.js pro HTTP smoke | C# harness, fake HTTP/SQLite chování |
| Hostitelský simulátor | C++14 kompilátor, Node.js, OpenSSL | Posloupnost, fake journal, WSS a token handshake |
| ESP32 firmware | PlatformIO Core, profil v `platformio.ini` | Kompilace ESP-IDF a hostitelské C++ testy |
| CAD F2 | Python 3.11, závislosti `final/requirements.txt`, Node.js, PowerShell | Generace a kontrola geometrií/exportů |
| Nativní iOS | macOS a Xcode, pro podpis vlastní Apple identita | Native harnessy a sestavení; skutečný iPhone je další kontrola |

## Nejmenší webové ověření

Z kořene checkoutu, každý blok ve vlastní shell session nebo po návratu do kořene:

```powershell
Set-Location kufr/mobilni-aplikace
npm ci
npm ci --prefix ../contracts
npm test
npm run build
npm run dev -- --host 127.0.0.1
```

Poslední příkaz je volitelný místní server. Webová fake varianta není nativní
ovladač a nedává přístup do produkce. Pro schémata a fixtures:

```powershell
Set-Location kufr/contracts
npm ci
npm test
```

## Backend v režimu fake

CLI spouštěj z `kufr/`, aby našlo jeho `global.json`. Komitované NuGet
lockfiles zahrnují CI profil `linux-x64`; obecný Windows locked restore může
skončit `NU1004`. Neměň CI lockfile pouze kvůli místní platformě. Windows
lokální test používá oddělený ignorovaný lockfile:

```powershell
Set-Location kufr
dotnet restore backend-tests/BackendTests.csproj -p:NuGetLockFilePath=obj/development.packages.lock.json --source https://api.nuget.org/v3/index.json
dotnet run --project backend-tests/BackendTests.csproj -c Release --no-restore -p:NuGetLockFilePath=obj/development.packages.lock.json
$env:KUFR_BACKEND_DLL = (Resolve-Path backend/bin/Release/net10.0/backend.dll).Path
node backend/tests/smoke.mjs
```

První restore vytvoří lokální graf v `obj`, nikoli změnu komitovaného CI locku;
při následném restore jej lze ověřit pomocí `--locked-mode`. Backendové testy
používají dočasné SQLite a fake provider; nejde o projekt pro `dotnet test`.
Linux CI nadále používá připnutý `linux-x64` restore s `--locked-mode`.
Volitelné živé smoke přepínače potřebují vlastní oprávnění k placenému testu.

Při ručním spuštění služby z `kufr/backend/` nejprve nastav lokální cestu:

```powershell
$databaseDirectory = Join-Path ([IO.Path]::GetTempPath()) ('kufr-dev-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $databaseDirectory | Out-Null
$env:Backend__DatabasePath = Join-Path $databaseDirectory 'journal.sqlite'
$env:Backend__Dialog__Mode = 'fake'
$env:Backend__Voice__Mode = 'fake'
$env:OPENAI_API_KEY = ''
$env:Backend__Memory__Enabled = 'false'
$env:Backend__Memory__CloudIndexingEnabled = 'false'
$env:Backend__Memory__LongTermPersonalEnabled = 'false'
dotnet run --launch-profile https -p:NuGetLockFilePath=obj/development.packages.lock.json
```

HTTPS profil naslouchá na `https://localhost:7043`; potřebuje místní vývojový
certifikát. Chybu nedůvěryhodného certifikátu řeš instalací/důvěrou lokálního
.NET development certifikátu, nevypínej ověřování TLS v klientovi. Bez registrace
zařízení služba nevydá device token; HTTP smoke si vytváří vlastní náhodnou
testovací identitu. Místní databáze mohou obsahovat text testovacího dialogu
i po ukončení služby; nepatří do gitu.

[backend/.env.example](../kufr/backend/.env.example) je referenční seznam
proměnných, nikoli soubor automaticky načítaný ASP.NET Core. Skutečné lokální
hodnoty nastav ve vlastním prostředí mimo git; provider klíč fake režim nepotřebuje.

## Firmware, simulátor a mechanika

Firmware se sestavuje `pio run` z `kufr/firmware/`; pro Arduino Nano je oddělený
projekt `obludarium/firmware/`. Sestavení nenahrává image do desky. Hostitelské
testy a konkrétní zdroje uvádí [firmware README](../kufr/firmware/README.md).

Simulátor není jen `npm test`: potřebuje nejprve sestavený C++ bridge,
`KUFR_SIMULATOR_BRIDGE` a OpenSSL. Přesné Windows příkazy, dočasné certifikáty a
loopback server popisuje [simulátor README](../kufr/simulator/README.md).

CAD regeneruj skutečným [postupem F2](../kufr/mechanika/final/README.md), se
závislostmi z `requirements.txt` v odděleném virtuálním prostředí. Exporty,
`.pio`, `node_modules`, `dist` a lokální virtuální prostředí nejsou zdrojové
soubory. Nepotřebuješ soukromý server pro místní kontrolu. iOS podpis,
produkční deployment, OTA publikace a živé AI evaluace jsou správcovské operace.
