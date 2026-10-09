# Předání F1 – 9. 10. 2026

## Doplnění prohlížeče: Inspect

Navazující softwarový slice přidává inspektor viditelné geometrie při
držení Shiftu, názvy dílů nad prázdnou plochou, připnutí karty a volbu barvy
s místním trvalým uložením. Kabelové vazby používají potvrzené zapojení;
rozměry a neznámé údaje vycházejí z rozměrového Markdown podkladu.
Tištěná geometrie ani elektrické zapojení se nemění.

Zdroje: `viewer-template.html`, nový `viewer.js` a `inspect_data.py`,
`preview.py`, `build.py`, `Regenerate.ps1`, nové testy a ověřená kopie
podkladů v `reference/inspect-sources.json`. Popisy jsou přiřazené všem
232 objektům; náhled obsahuje 92 dokumentovaných logických vazeb.
Přesné konektory,
kabelové délky a síťové přiřazení konkrétních ID svorek nejsou domyšlené.

Ověření 9. 10. 2026: `node --check viewer.js`, 20/20 testů metadat a
balení, `preview.py --viewer-only` a tentýž generátor v izolované kopii
zdrojů bez externích Markdown souborů prošly. Browser přes localhost
v oknech 611 × 764 a 1280 × 900: skutečný výběr PCA, Shift nad prázdnou
plochou, propojení, výběr seznamem, zdvih 16 mm, skryté vrstvy a obnovení
barvy po zavření/znovuotevření stránky; konzole bez chyb. Přímý test
`file://` odmítla bezpečnostní politika nástroje. Nezávislé review opravilo
chybějící podklady ve vybaleném balíčku a popisy výstelky/kování;
finální review nemá otevřený nález. Doručení uvádí globální stav.
V tomto slice se regeneruje pouze
`exports/viewer.html`; starší geometrické reporty a celý ZIP tím nejsou
znovu ověřené. Další krok: používat Inspect při prohlídce; fyzická
akceptace prvního mechanismu zůstává podle původního předání níže.

## Původní mechanické předání

Aktuální mechanický návrh je v `kufr/mechanika/final/`. Následný požadavek
majitele na úklid odstranil z verzovaných souborů předchozí generátory,
modely a návody; potvrzené rozměrové podklady zůstaly zachované.
Elektrické zapojení, aplikace, firmware a nasazení se tímto návrhem nemění.
Globální stav ani tento záznam netvrdí, že je kufr fyzicky hotový.

Dokončené zdroje: nový šestidílný korpus a šestidílné prázdné víko,
šest drážkových zdvihů s brzdou, montážní zóny pro potvrzený hardware,
otevřená kapsa banky, čtyři kryty se clonami a skrytými kolébkami,
kabelové a konektorové rezervy, servisní a transportní uspořádání.
Původní tisknutelné tvary ani kloubové mechanismy nebyly importovány.
Návrh, implementaci, review zdrojů a validaci dělal jediný agent.

Kontrolovatelný výsledek: parametrické Python/JSON zdroje, 29 typů STL
(44 výtisků s měrkou), STEP díly a pět sestav, skutečné rendery, samostatný
WebGL prohlížeč, dva SVG výkresy, kusovníky, návod a balíček ZIP.

Autoritou provedených kontrol je aktuální `exports/verification.json`
a shoda jeho zdrojových SHA-256. `Regenerate.ps1` balení zastaví při selhání.
Kontroly zahrnují validitu jediného pevného tělesa každého výtisku,
orientaci/podložku včetně brimu, zpětné načtení STEP, objem a topologii STL,
tři sestavy, 81 materiálových poloh mechanismu, 36 poloh víka, 37 kroků
vyjmutí banky, rezervy konektorů a kolize s montážními maticemi.
Nejde o spojitý důkaz všech poloh ani simulaci pružného kabelového svazku.

Při review byly řešeny skutečné nálezy: průniky stolkových noh s rámy,
servisní prostor USB, podhled do elektroniky, kontakt bočních krytů
s kováními, retence stolků a přístup k hlavám/maticím, pozice náhradních
PCB vůči šroubům a pozitivně řízený návrat proti přibrzdění. Opravy jsou
součástí nového výsledku; staré exporty odstraněných dílů generátor uklízí
jen podle vlastního předchozího manifestu.

Review PR167 navíc odhalilo možnost přibalit starší obrázky, sestavy nebo
reporty ponechané v exportním adresáři. Balení proto používá výslovný seznam
aktuálních artefaktů a ověřených zdrojů; chybějící aktuální soubor je chyba.
Manifest musí obsahovat přesně pět současných sestav. Regresní test vytvoří
staré exporty a ověří skutečný ZIP bez nich. Místní ad hoc doklady a staré
náhledy se do výrobního balíčku nepřebírají pouhým procházením adresáře.

Finální kontrola zahrnuje také hlavu, pouzdro a matici válečku, zapuštěné
matice servopáky a obě sousední přihrádky při všech 81 kontrolovaných polohách.
Kontakt matice se sousedním LED páskem byl odstraněn přesunem LED na pravou
hranu lůžka. Tisková síť vačky byla opravena nahrazením drobných hran
hladkými přechody a skutečnými kruhovými klidovými úseky.

Neprovedeno: tisk, osazení, měření serva a brzdy, reálné přenášení, testy
lepení/sametu, teploty/proudy, akustika, skutečné zorné pole/FPC kamery.
Polohy osy a otvorů konkrétní servopáky, tloušťka IP65, rozměry svorek
a detail konektorů nejsou vydávány za změřené. Fyzická kritéria E5/E6/B1/E13
zůstávají otevřená; žádný firmware se nekalibroval ani nenahrával.

Další fyzický krok: měrka, jeden modul s hůlkou do návrhových 150 g,
pasivní držení po vypnutí ve všech polohách a zatížený návrat. Pokud nelze
současně splnit tření brzdy a sílu serva, neprohlašovat konstrukci za
akceptovanou a neopakovat ji šestkrát; upravit mechanismus podle měření.

Majitel následně výslovně požádal o úklid nepoužívaného CAD, push a merge;
tím nahradil předchozí omezení na lokální předání. Návrh byl původně uložen
v commitu `14ff82a`. Výsledek předání přes PR a aktuální kontroly zaznamenává
[globální stav](../../docs/stav-implementace.md). Generované exporty zůstávají
lokální a reprodukovatelné. Nákup, cloudové nasazení ani hardwarový zásah
nejsou součástí tohoto předání.
