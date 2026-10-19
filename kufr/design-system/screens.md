# Specifikace obrazovek Vapour Chrome

Společný základ: Inter pro celé UI, IBM Plex Mono pro technické údaje,
klidná Midnight/Mist plocha a Periwinkle výběr. Tmavé téma je výchozí,
světlé používá stejné komponenty s odvozenými sémantickými barvami.
[Náhled systému](mockups/index.html) ukazuje obě témata vedle sebe.

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

## Akceptace tří klíčových pohledů

Akceptace rozlišuje statickou ukázku a skutečného spotřebitele. Mockup splní
kompozici a označení; teprve test skutečného UI ověřuje níže uvedené chování.
Kontrolní šířky jsou 1440, 1024, 390 a 320 px, dále 200% zoom a Reduced Motion.
Stránka jako celek nesmí mít horizontální scroll; tabulka, timeline nebo CAD
viewport může mít jasně ohraničený lokální scroll. Interaktivní cíle jsou 44 px.

| Pohled | Povinné ověření |
| --- | --- |
| Desktop 3D Workshop | Na desktopu čitelné Explorer 248 px, dominantní model a Inspector nejméně 320 px; Motion Studio oddělené dole. Vybraný objekt se shoduje ve stromu, Inspectoru a modelu. Všechny režimy, vrstvy, pohledy a návrat z Inspect zůstávají funkční. Na úzké šířce jsou panely dosažitelné, nezakrývají trvale celý viewport a otevřené skupiny lze posouvat. Ukázka neslibuje funkční rotaci/zoom. |
| Mobilní Domov / ovládání | Na 390/320 px čitelný spojovací a bezpečnostní stav, přihrádky a Stop bez překryvu. Safe area a otevřená klávesnice neskrývají aktivní vstup ani cestu ke Stop. Snapshot update neničí focus nebo rozepsaný text. Observer, manual, busy, Stop a disconnected mají pravdivé akce a popisy. Nativní iPhone je samostatná akceptace. |
| Simulation Lab | Trvalé SIMULACE, read-only připojení, šest modelových přihrádek, režim/fáze/journal/revize a omezený log. Token se po zahájení vymaže a odchází jen v autorizační hlavičce. Neautorizovaný proud je odmítnut. Odpojení neukazuje aktuální green stav. Publikovaná kopie má zablokované zadávání/připojení a bez tajemství zobrazuje veřejný náhled. |

Klávesnicí projděte navigaci, všechny dostupné akce a vstupy v logickém
pořadí. Focus je viditelný, není pod sticky panelem a nemizí při snapshotu.
Tabs respektují šipky/Home/End pouze v tablistu. Strom/vrstvy a výběr objektu
mají cestu bez canvas pickingu; drawer/Inspector jde zavřít tlačítkem i Escape
s návratem focusu. Časová osa/log lze posouvat klávesnicí bez automatického
návratu na nejnovější řádek. Reduced Motion zachová význam a okamžitost Stop.

Pro skutečné UI ověřte `loading`, `empty`, `ready`, `pending`, `error`,
`disconnected`, `unknown` podle [kontraktu komponent](components.md).
Při chybě odezvy fyzického povelu nesmí UI zaměnit přijetí, dokončení a
změřenou polohu. Browser ani hostitelský model neprokazují fyzickou akceptaci.

## Následné obrazovky — v této dodávce neimplementováno

Následující cílová informační architektura je návrh pro další slices.
Neprohlašuje nové routy, kompletní produktové obrazovky nebo backend funkce
za hotové. Existující aplikace má Ovládání, Debug a Síť kufru; jejich současné
části hlasu/diagnostiky mohou být později použity, nikoli nahrazeny jen kvůli
novým názvům. Závislosti určuje implementační plán, nikoli tento vizuální návrh.

| Budoucí pohled | Zamýšlený účel a hranice |
| --- | --- |
| `Home` | Stav kufru, připojení, právě probíhající sezení a další bezpečný krok; nevytváří sezení pouhým otevřením. |
| `Control` | Ruční akce 1–6, dostupnost, lease a potvrzení již schválených operací. Zachová offline ruční řízení a bezpečnostní bránu zařízení. |
| `Voice` | Poslech/promluva/čekání s jednoznačnou mikrofonní autorizací, zdrojem a cancel. Provozní stav nemá vypadat jako nahrávání při nedostupném mikrofonu. |
| `Service` | Párování, síť, kalibrace a zotavení s doloženým stavem; reset neprohlašuje fyzickou kontrolu bez jejího skutečného potvrzení. |
| `AI Studio` | Přehled konfigurace a testovacího kontextu AI oddělený od fyzických povelů. Náhled nespouští placené volání a neukazuje provider secret. |
| `Diagnostics` | Technické logy, runtime verze, filtry a korelační ID; oprávnění a redakce podle existujícího diagnostického kontraktu. |
| `Sessions` | Sezení a paměť večera s jasným původem, uchováním a oprávněnými akcemi. Historie není živý bezpečnostní snapshot. |
| `ShowMode` | Návštěvnický prezentační pohled na skutečné provozní stavy a spojení, bez administrace a ovládacích povelů. Obslužný Stop zůstává dostupný v ovladači; pohled nevytváří nový autonomní hardware režim. |

Každý nový pohled vyžaduje vlastní rozsah, skutečný model stavů, odkazy z
navigace, relevantní test a ověření zařízení tam, kde na něm funkce závisí.
Dokumentace ani mockup nenahrazují tuto implementaci.
