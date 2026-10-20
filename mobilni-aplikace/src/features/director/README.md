# Ambient v kufru a poslech v telefonu

„Jednorázový ambient“ posílá původní Show povel skutečnému kufru. Přijetí
backendem nepotvrzuje zvuk; kufr jej musí sám vyzvednout a přehrát.

Panel **Poslech ambientu v telefonu · simulace** přehrává jednu existující
repliku Show repertoáru přímo v aplikaci. Výslovný stisk **Přehrát ambient
v telefonu** aktivuje místní AudioContext a požádá backend o hlas. Nepotřebuje
připojený kufr, nemění Show stav, neposílá povel pohonům ani device playback ACK.
Účet musí mít oprávnění `operate` pro vybraný kufr, včetně čtení výsledného zvuku.
Platí stejný CSRF transport a omezení cest ve webu i Capacitoru.

- `POST /v1/show/devices/{deviceId}/ambient-previews` přijímá
  `{ "operationId": "UUID" }`. Pouze POST smí spustit generování.
- `GET /v1/show/devices/{deviceId}/ambient-previews/{operationId}` čte stav
  `pending`, `ready` nebo `failed`; nic negeneruje. Odpověď obsahuje identitu
  operace, `lineId`, `text`, `synthetic` a u hotového výsledku `wavBase64`.
- Používá se existující `ShowSpeechProvider` a nastavení hlasu/tempa. Běží nejvýše
  jeden náhled současně, odděleně od slotu skutečných replik kufru, aby je
  náhled nezdržoval. `Show:DeviceCueSpeechMode=off` náhled zakazuje; `fake`
  vrací výslovně označenou tichou fixture bez volání poskytovatele.
- WAV je PCM16 mono 32 kHz, nejvýše osm sekund. V paměti je nejvýše 16 výsledků,
  dostupných 60 sekund. Existující audit a idempotence operací mají 24hodinové
  okno; restart ani vypršení zvuku v tomto okně neopakují stejnou operaci.
- Klient posílá POST právě jednou a krátce čte jeho výsledek. Stop, skrytí
  aplikace, změna účtu/zařízení/oprávnění a odchod z pohledu zruší místní
  přehrávání i čekání. Již přijatá serverová generace má vlastní osmivteřinový
  limit a může doběhnout; klient ji automaticky neopakuje.

Ověření bez placeného poskytovatele:

```text
dotnet run --project backend-tests/BackendTests.csproj -- --show-ambient-preview
cd mobilni-aplikace
npx vitest run src/features/director/AmbientPreviewPlayer.spec.ts src/features/director/DirectorPanel.spec.ts src/features/identity/accountPaths.spec.ts
npm run build
node tests/director-ambient-preview.browser.mjs
```

Browser test dekóduje nenulovou WAV fixture ve skutečném zvukovém grafu a ověřuje
přirozené dokončení, chybový stav a desktopovou/mobilní šířku. Nevolá AI ani kufr.
Tento výsledek neprokazuje slyšitelnost reproduktoru, chování přepínače tichého
režimu ani podepsanou aplikaci na skutečném iPhonu.
