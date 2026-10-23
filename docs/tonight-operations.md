# Provozní přehled večera

Přehled skládá stav oprávněného zákaznického rozhovoru, režie, místního kufru
a kapacity společného archivu. Otevření ani obnovení přehledu nespouští nový
rozhovor, negeneruje památku a nevytváří rezervaci nebo oprávnění k řízení.

Stránka `/tonight` je dostupná přes položku **Dnes** v provozní navigaci.
Používá skutečný účet a jeho aktuální seznam ovladatelných zařízení; bez
odpovídajícího oprávnění nevytváří řídicí kontext. Místně spárovaný kufr má
přednost jen tehdy, je-li také v tomto seznamu. Další povolený kufr lze zvolit
ručně. Odkazy na Režii kufru a Zákaznické relace otevírají existující nástroje.

## Význam stavu

- Zákaznická relace zachovává serverové `starting`, `context_ready`, `running`
  nebo `recovering`. `endRequested` znamená žádost o ukončení; není potvrzením
  konce rozhovoru ani uvolnění zařízení. Více současných relací nebo neznámý
  stav se nesmí změnit na tvrzení, že je kufr volný.
- Čerstvost přehledu vychází z času úspěšného přijetí odpovědi v telefonu.
  Výchozí hranice projekce je 12 sekund včetně; jde o stáří místního snímku,
  nikoli čas potvrzení fyzického zařízení. `createdAt` relace je v Unix
  sekundách a není časem posledního kontaktu. Chybějící, chybná nebo budoucí
  časová značka znamená neznámý stav.
- Starší platné údaje mohou zůstat jako výslovně označený poslední známý
  snímek. Oprávnění a dostupnost akcí se řídí existující režií; projekce sama
  neposkytuje `canStart`, oprávnění Stop ani důkaz volné lease.
- Fáze hlasu neprokazuje slyšitelný zvuk. Připojení telefonu nebo úspěšná
  odpověď backendu samy neprokazují síťové spojení s kufrem. Chybějící měření
  zůstává neznámé, včetně pořadí dalšího hosta a připravenosti jeho památky,
  dokud pro ně není oprávněný a korelovaný zdroj.
- Místní připravenost vyžaduje nativní snímek se shodnou identitou vybraného,
  uloženého i diagnostikovaného kufru. Jeho čerstvost používá čas přijetí
  zařízení; monotónní čas diagnostiky se nesrovnává s kalendářním časem.
  Samotný webový prohlížeč tuto připravenost nepotvrzuje.

## Stop a povely

Přehled používá společný řadič obnovy relace s intervalem 5 sekund. V jednom
cyklu načítá relace a kapacitu; při chybě používá jeho existující odklad
opakování. Dvě účetní čtení za cyklus znamenají nejvýše 24 pravidelných čtení
za minutu na jeden otevřený přehled. Sdílený limit IP adresy mohou spotřebovat
i další klienti; odpověď 429 proto není důkazem odpojeného kufru. Místní
sekundový časovač pouze přepočítává stáří snímků a neposílá síťové požadavky.

Globální Stop v horní liště zachovává své existující nezávislé cesty. Jeho
registrace pro režii přežívá opuštění stránky, zatímco soukromé snímky a
rozpracovaná čtení přehledu se zneplatní. Změna účtu nebo cílového zařízení
zneplatňuje příslušný řídicí kontext podle společných pravidel aplikace.

Běžné zásahy používají existující `DirectorSession` a `DirectorClient`, jejich
aktuální revizi, identitu relace, krátkou platnost a idempotenci. Přijetí
backendem se zobrazuje odděleně od runtime potvrzení; není důkazem přehrání
nebo fyzického účinku. Po neznámém výsledku obnovte autoritativní stav a
ověřte kufr. Přehled povel automaticky neopakuje a při opětovném otevření
nevyrábí náhradní start. Podrobné nástroje zůstávají v Režii kufru.

## Účetní čtení kapacity

`GET /app/v1/tonight/devices/{deviceId}/memory-status` je pouze čtení pro
živý povolený účet. Owner/Administrator zachovávají existující oprávnění ke
konfigurovaným zařízením. Operator potřebuje aktuální grant `operate` nebo
`manage` přesně k požadovanému zařízení. Customer, Viewer, grant pouze
`read`, cizí zařízení a neplatná relace přístup nezískají. Ověřuje se aktuální
účet, relace, security stamp, role a grant v databázi. Legacy bearer tato
cesta nepřijímá, ani při současném účetním cookie.

Odpověď obsahuje pouze `storedBytes`, `capacityBytes`, `storageStatus`,
`rejectedWrites`, `storageIncomplete`, `lastStorageErrorCode` a
`lastStorageErrorAt`. Neobsahuje záznamy hostů, ID rozhovorů, obsah, provider
ani store ID nebo tokeny. Neznámý chybový kód a jeho čas se vrací jako null;
jediný povolený kód je `archive_capacity`. Úspěšná odpověď a autorizované
handlerové chyby používají společné `no-store` a `nosniff` hlavičky.

Bez platného účtu je odpověď 401; nepovolený cíl má obecnou 404
`device_unavailable`. Při vypnuté účetní identitě platí existující 503
`accounts_not_configured`. Existující `/v1/admin/memory` a jeho starší
administrátorská autentizace se nemění.

Přestože oprávnění ke čtení je svázané se zařízením, čísla popisují **společný
globální archiv**, nikoli pouze data tohoto kufru nebo hosta. Aktuální tlak
na kapacitu je oddělený od trvalé historie odmítnutých zápisů. Zvýšení
kapacity ani ruční smazání nedoplňuje dříve odmítnutý obsah. Podrobnosti
limitu, měření a ručního úklidu popisuje
[kanonická místní paměť](autonomous-live-memory.md#kapacita-místního-archivu).

## Ověření

`dotnet run --project backend-tests/BackendTests.csproj -- --tonight-memory-status`
ověřuje skutečný HTTPS účetní middleware, oprávnění i jejich odvolání a přesnou
sedmipoložkovou odpověď nad místní fixture bez provider volání. Testy projekce
a komponent používají stejný frontendový Vitest runner jako ostatní aplikace.
Browserové ověření potvrzuje software; skutečné audio, BLE, pohyb a předání
hostovi se ověřují samostatně při akceptaci večera na zařízení.
