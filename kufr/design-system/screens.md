# Specifikace obrazovek

## Desktop Workshop

[Ukázka](mockups/desktop-workshop.html) je 3D dílna F2: Explorer se stromem
vrstev a objektů, dominantní centrální viewport, Inspector vybraného dílu
a spodní Motion Studio s ilustrační časovou osou. Priorita:
orientace v sestavě → výběr objektu → rozměry a vazby → artefakty.
Explorer má šířku 248 px, Inspector 320 px, viewport vyplňuje zbývající prostor.
Pod 1100 px se Inspector řadí pod oba sloupce; pod 680 px je nejprve viewport,
potom Explorer a Inspector v jednom sloupci. Motion Studio zůstává dole;
jeho fáze přejdou ze šesti na tři sloupce. Ukázková akční tlačítka jsou vypnuta;
otevírání skupin pomocí `details` umožňuje prohlédnout strukturu i klávesnicí.
Skutečný Workshop viewer zachová své ovládání kamery, výběr, vrstvu zapojení
a generování CAD; geometrická validace se nikdy neoznačí za fyzickou akceptaci.

## Mobilní Domov / ovládání

[Ukázka](mockups/mobile-home.html) má horní stav spojení a bezpečnosti,
přihrádky, hlavní kartu sezení a viditelný Stop. Dolní navigace používá
existující sekce skutečné aplikace při implementaci; ukázka pouze znázorňuje
hierarchii. Není specifikací nového transportu ani nových povelů.
Unknown / offline nesmí napodobovat připojení. Akce zůstávají řízeny skutečným
lease, kalibrací a bezpečnostním snapshotem. Mobilní sloupec má maximálně
480 px; širší okno přidá kontext ukázky, neroztáhne cíle do celé šířky.

## Simulation Lab

[Ukázka](mockups/simulation-lab.html) odděluje stav modelu, přihrádky a časovou
osu. Trvale ukazuje SIMULACE, pevná ilustrační data a že neovládá pohony.
Skutečný [dashboard](../simulator/dashboard.html) je pozorovací klient
`GET /v1/observe`, nepřebírá manual lease a neposílá ovládací povely.
Token je pouze v paměti, pole se po zahájení vymaže, nikdy není v URL ani
localStorage. Veřejná publikovaná kopie má `data-static-preview` a zakázané
připojení; místní autentizované pozorování zůstává dostupné na lokálním serveru.

Do veřejného přehledu nepřenášejte skutečný snapshot jako statický potvrzený
stav. Odpojený dashboard ukazuje pomlčky a žádné události. Časová osa může
zobrazit požadavek na západku, nikoli tvrzení o změřeném otevření.
