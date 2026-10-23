# Vlastní zapamatované údaje

Zákaznická paměť zpřístupňuje osobní údaje původního vlastníka rozhovoru.
Vlastnictví vychází z úplného zachovaného seznamu jeho zákaznických relací,
jejich původního profilu, zařízení a rozhovoru a odvozené ověřené identity hosta.
Stejné jméno, aktuální profil ani připojení ke sdílené památce přístup nepřidává.
Přístup vyžaduje živou účetní relaci a aktuální security stamp.

## Čtení a význam údajů

`GET /app/v1/customer/memory` vrací `{ revision, items, complete }`. Každá položka
obsahuje `id`, vlastní číselnou `revision`, `text`, `kind`, `sourceReference`,
`createdAt`, `profileId`, `customerSessionId`, `canCorrect` a `canForget`.
Revize seznamu je neprůhledný otisk; klient podle ní neodvozuje oprávnění.
Neúplný nebo příliš velký inventář skončí chybou, nikoli zkráceným seznamem
označeným jako úplný. Maximum je 4096 položek a 32 MiB výsledného JSON včetně
escapování. Odpověď nepřenáší interní autorizační důkazy ani security stamp.

Čtení zahrnuje jen aktivní a povolené osobní údaje aktuálního jmenného prostoru
s ověřenou identitou a povolenou dlouhodobou pamětí. Vyžaduje také zachovaný
kontext původní relace a neodvolaný profil. Místní čtení nevyžaduje souhlas
s cloudovým exportem a tento souhlas nemění. Odvolaný profil nebo chybějící
historický kontext přestává poskytovat obsah.

`UserReportedFact` znamená údaj sdělený hostem; lze jej opravit nebo zapomenout.
`ConfirmedSelection` zachovává původ potvrzeného aplikačního výsledku; lze jej
číst a zapomenout. Libovolný nový text nemůže získat tuto potvrzenou provenienci,
proto má `canCorrect: false`. Výsledek dokončený časováním není důkaz fyzického
předání nebo účinku. `sourceReference` má pouze povolené formáty odpovědi, vstupu
nebo výsledku; jiný původ se zobrazuje jako prázdný řetězec.

## Oprava a zapomenutí

`PUT /app/v1/customer/memory/{id}` přijímá `{ expectedRevision, text }`.
Oprava vlastního sděleného údaje vytvoří další revizi stejného ID. Zachová
původní identitu, rozhovor, druh, odkaz na zdroj a historický souhlas. Text musí
být neprázdný a splnit stávající limit UTF-8 paměti. Kapacita a denní rozpočet
se ověřují před zneplatněním původního údaje. Historické revize nadále zabírají
místo; odmítnutí kvůli kapacitě uloží diagnostický čítač a ponechá údaj beze změny.

`POST /app/v1/customer/memory/{id}/forget` přijímá
`{ expectedRevision, confirmed: true }`. Vyžaduje samostatné výslovné potvrzení.
Zapomenutí smaže text všech revizí právě tohoto prokazatelně vlastního ID,
zneplatní jeho použití a vytvoří trvalé úlohy odstranění odvozených indexů.
Tuto cestu zachovává původní vlastník i po odvolání profilu, ztrátě historického
kontextu nebo vypnutí dlouhodobé paměti, pokud zůstává prokazatelný přesný původ
a ověřená vazba hosta. Znalost cizího ID sama oprávnění neposkytuje.

Obě úspěšné změny vracejí 204; klient poté znovu načte seznam. Zápis používá
stejný ověřený účetní cookie, HTTPS origin a CSRF ochranu jako ostatní účetní
změny. Samostatný servisní `Authorization` header cesta odmítá. Před změnou
platí samostatné limity historie revizí, indexových map a vazeb hosta; jejich
překročení končí 413 `memory_mutation_limit` bez změny obsahu.

Zastaralá číselná revize vrací 409 `memory_revision_mismatch`. Neznámý nebo cizí
údaj má obecnou 404 `memory_unavailable`. Chybějící potvrzení vrací 400,
neplatný účet 401, nedostatek místa 413 `archive_capacity`, překročený denní
rozpočet 429 a dočasně nedostupné guardy 503. Po 409
`memory_mutation_outcome_unknown` nebo přerušení odpovědi klient nejprve obnoví
stav; automatické opakování nesmí tvrdit, že předchozí změna byla vrácena.

## Atomické hranice a uchování

Živé účetní a profilové guardy, původ relace, skutečná místní write-admission,
společný journal/památkový guard a paměťový zámek se při změně obsahu drží
do commitu jediné paměťové transakce. Čtení drží ověření až k jednomu synchronnímu přijetí odpovědi;
síťový flush následuje po uvolnění guardů. Oprava a zapomenutí ověřují oprávnění
bezprostředně před commitem. Revize a epochy zneplatní staré snímky, pozdní
indexovací úlohy i dosavadní exportní důkazy. Již přijaté nebo stažené bajty
nelze zpětně odvolat. Odvozené vzdálené soubory odstraňuje existující trvalá
fronta; během transakce se nevolá provider.

Nejde o globální pořadí zámků: běžné přijetí show drží paměťový zámek
a potom rezervuje journal, přičemž neprochází místní write-admission.
Zákaznické čtení postupuje opačně. Proto paměťový zámek zkouší získat
jen jednou, nejvýše 100 ms; při kolizi vrátí 503 bez přijetí výstupu
a synchronně uvolní všechny vnější guardy. Teprve potom může blokovaná
show dokončit rezervaci. Uvnitř těchto guardů se nečeká na síť ani na Task.

Uchování obsahu zůstává ruční. Oprava ani expirace přístupu k exportu nezavádí
časované mazání. Celý účet se odstraňuje existujícím ověřeným postupem popsaným
v [nastavení účtů](account-setup.md); kapacitu a indexy popisuje
[kanonická místní paměť](autonomous-live-memory.md).

## Ověření softwaru

Cílené příkazy `dotnet run --project backend-tests/BackendTests.csproj --`
s přepínači `--customer-memory-data`, `--customer-memory-mutations` a
`--customer-memory-http` používají syntetické účty a místní SQLite. Ověřují
skutečný účetní middleware, původ, limity, CSRF, současné a opožděné zápisy,
revize, zapomenutí i hranici přijetí odpovědi bez provider volání. Testy
nevytvářejí skutečné vzdálené kopie a nenahrazují akceptaci celé exportní
služby nebo nativního klienta.

Běžný offline CI runner spouští společnou sadu `--customer-data`, která
zahrnuje skutečnou paměť i export, produkční startovací matici a vynucenou
kolizi skutečných paměťových a journal zámků. Samostatné přepínače
`--customer-memory-export-integration`, `--customer-data-startup` a
`--customer-data-lock-contention` slouží k cílené diagnostice.
