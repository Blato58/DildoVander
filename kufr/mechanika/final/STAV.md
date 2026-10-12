# Rozměrová revize kufru — 12. 10. 2026

Nové měření nahrazuje původní pravoúhlý plášť a odhad polohy výztuhy.
Volný horní obdélník 430 × 269, stěna 5, horní výztuhy 20 mm včetně
stěny a 40/30 mm dolů od hrany; spodní tečné rozměry 45 × 45 mm,
středová výztuha 80 × 5 mm. CAD používá kruhovou interpolaci R45.
Čtyři kotvy mají zakřivený spodek a 0,5mm svislý film; matice se vloží
před lepením. Tisková orientace kotev má plochou horní stranu na podložce.
Rozšířené příčné lepené stopy mají 43 806,325 mm² projektované plochy
a 103 epoxidových zámků; minimum 52,381 ml, fyzická pevnost nepotvrzená.
Fotografie majitele jsou pouze lokálním podkladem. Přesná měření a přetrvávající předpoklady vede ../rozmery-doruceneho-kufru.md.
Dřívější geometrické kontroly níže samy nepřijímají tuto změněnou sestavu;
aktuální výstupy přijímat pouze přes zdrojově svázaný verification.json.
Fyzická přejímka dna, oblouku, kotev, vrstvení tisku a pantů zůstává otevřená.

---

# Aktuální předání DFA — 12. 10. 2026

Implementovaná montážní a mobilní revize F2 má dvě
odnímatelné boční kazety, sedm horních M3, skutečné přístupové drážky,
16 kroků s odlišením stolu/kufru a přípravu vodicích šroubů před vložením.
`POSTUP.md` je generovaný z `assembly-guide.json`; starší pořadí níže je
historický podklad a nesmí se použít k montáži této revize. Autoritou
výrobních výstupů zůstává aktuální zdrojově svázaný verification.json.
Ověřeno: viewer 47/47 regresí a syntaxe; montážní regrese 8/8, kotvy 11/11,
offline styly 4/4 a kontrakt 16 kroků / 542 osazených meshů / 135 vodičů.
Chrome se skutečným WebGL: 320×568, 360×800, 390×844, 844×390, 768×1024
a 1440×900; dotykový orbit/pan/pinch přes CDP, pohledy a focus, režimy,
montáž vpřed/zpět/restart a Stop prošly bez chyb konzole nebo requestů.
Prohlížeč je desktopová emulace, nikoli důkaz rychlosti/dotyku na iPhonu.
Samostatně prošly obě kazety, vložení rámu a šest horních skupin; po
poslední úpravě polohy zesilovače prošlo i sedm spojitých obálek hlav
servisních šroubů. Levý zesilovač má polohu (46; 94; 49), podpěra kazety
je před ohybem USB kabelu a přívod buck IN+ obchází válec spínače přes Y61.
Zadní šroub levé kazety má osu Y111,7 a nejméně 1 mm k pásku zesilovače;
trasy levých kondenzátorů procházejí před tímto páskem přes Y95,5.
Úplný aktuální výsledek exportů, kabelových těles,
pohybu a těchto montážních drah přijímat jen z `exports/verification.json`
svázaného se zdrojem; ZIP má samostatný SHA-256 a ověřený inventář.
Indexovaná tessellace zmenšila samostatný viewer ze 70 na přibližně 25,6 MB
bez změny ploch; GPU dostává stejné plošné facety. Žádná nová závislost.
Nezávislé source review vedlo k opravě jištění vodítek, skutečného
stolního/kufrového stavu, montážních předchůdců a servisního přístupu.
Git předání a přesný endpoint jsou v `../../docs/stav-implementace.md`.
Fyzická přejímka a uvolnění k tisku zůstávají otevřené.

---

# Aktuální systém F2

F2 je vložka do skutečného dřevěného kufru se šesti přihrádkami,
potvrzeným inventářem a elektrickým zapojením. Kufr se nevrtá ani neřeže.
Návrh není fyzicky přijatý ani uvolněný k sériovému tisku.

## Aktuální návrh

- Volný horní obdélník je 430 × 269 mm, stěna 5 mm, dolní hloubka 114,3 mm.
  Zaoblení má tečné rozměry 45/45; středová výztuha 80 × 5 a horní 20/40/30.
  Návrhová obálka 400 × 240 × 170 nenahrazuje kontrolu skutečných těles.
- Čtyři PETG panely mají integrovaná žebra. Čtyři tištěné podlahové kotvy
  se spodním filmem a odlitými epoxidovými zámky připojují rám na holé dřevo DNA.
- Zdvih 16 mm řídí zachycená vačka s integrálním Ø6 PETG čepem lůžka.
  Dolní podpěry jsou pevné; šest ručních pásků zajišťuje prázdná lůžka při přepravě.
- Hůlky do 200 mm se šířkou hlavice 38 mm včetně případného pouzdra
  mají potvrzený délkový a šířkový vstup. Ostatní tvarové údaje jsou předpoklady.
- Elektronika má dorazy, pásky a vlastní nosiče. Hůlky, banka a textil
  nejsou součástí nosné cesty. ESP/kamera jsou vzadu uprostřed,
  mikrofon vpředu uprostřed, reproduktory po stranách.
- Montáž má 16 kroků a kabelový plán 135 samostatně identifikovaných vodičů.
  Hlavní i krátká choreografie pochází z jednoho souboru pro firmware a náhled.
- Návrh má 37 typů STL / 52 výtisků včetně dvoudílné měrky pro čep,
  drážku, boční kapsy M3/M4 a epoxidový zámek s odvzdušněním.

Kotvy mají 43 806,325 mm² projektovaných lepených stop a 103 zámků Ø7,5/Ø10,5.
Minimum spodního filmu a zámků je 52,381 ml bez ztrát; BOM uvádí tři
balení Bison 24 ml. PETG kotvy se předplní odspodu. Boční vložení
24 M3 a 4 M4 matic kryje souvislá 3mm střecha. Moduly mají vlastní
24 M3 × 14, rám 4 M4 × 10 ISO4762; 8 M3 × 10 slouží pro kryty.
Integrální čep vyžaduje společné nasazení a servisní sejmutí lůžka,
vačky a serva; dva pásky serva se při servisu nahrazují.

Pevnostní posouzení používá celý 2D tenzor skupiny zámků i lepené stopy,
nejhorší vodorovný směr a sílu v každé skutečné matici. Návrhové zatížení
je 5 kg, 3 × g, dvě zatížené kotvy a rameno 170 mm. Meze materiálu nejsou
měřené. Výpočtová kontrola neprokazuje soudržnost skutečného spoje.

## Reprodukovatelnost a důkazy

CAD používá Python 3.11, CadQuery 2.8.0, OCP 7.9.3.1.1 a VTK 9.6.2
podle připnutého `requirements.txt`. Regenerace nevyžaduje referenční
fotografie; `reference/evidence.json` zaznamenává textové rozměrové vstupy
a jejich původ. Inventář a zapojení zůstávají přibalené jako textové zdroje.

Úplný postup `Regenerate.ps1` zahrnuje regrese vieweru, zdrojů, balení,
mechanismů, kotev, zatížení a montáže, generování CAD/renderů, geometrické
ověření a ZIP. Samotný úspěšný test nebo render není fyzická přejímka.
Rozměry a počty se porovnávají s aktuální specifikací a parametry;
starší neověřený export není srovnávací geometrický důkaz.

Autoritou konkrétního běhu je `exports/verification.json` se zdrojovými
SHA-256, počty dílů a výsledky. Úspěšný status je výhradně
`PASS_CAD_NOT_PHYSICAL_ACCEPTANCE`. `generation-snapshot.json` svazuje
výstupy s aktuálními zdroji; ověření zaznamenává otisky CAD, vieweru,
výkresů, CSV a renderů. Balení odmítá změněné zdroje, chybějící soubory
a změněné výstupy. Tiskový seznam je v `exports/print-bom.csv`, otisk ZIPu
v `exports/package-sha256.txt`. Git předání vede hlavní stav projektu.

## Dosud nesplněné fyzické podmínky

| Podmínka | Další konkrétní krok |
| --- | --- |
| Skutečný kufr/víko/konektory | Změřit a nasucho vložit měrku a rám, ověřit celý chod víka |
| Hůlky a případná pouzdra | Ověřit zbývající výškové/tvarové rozměry, hmotnost a skutečné usazení |
| Kamera/FPC a svorky | Potvrdit doručené rozměry, vývody a manipulační prostor |
| Servo a páka | Ověřit polohu osy, náboj, rozteč páky a dodaný středový šroubek na jednom modulu |
| Dřevo/lepidlo/nosné spoje | Přejímka holého připraveného dna, vyplněných epoxidových zámků a tištěných střech matic podle MONTÁŽ.md |
| Servo dole bez držení | Změřit proud a reakci po FULL_OFF a porovnat je s odpojeným V+ |
| Zdvih | Zatížené cykly, kalibrace, proud a oteplení prvního a následně všech modulů |
| Kabely | Reálné poloměry, svorkování, polarita, pohyblivé rezervy a vyjmutí banky |
| Přeprava | Zkouška prázdné aretované sestavy při nesení a jízdě |

Žádná položka této tabulky není splněna renderem, testem ani buildem.
Dalším fyzickým krokem je tisk měrky a jednoho zkušebního modulu podle
[montážního postupu](MONTÁŽ.md). Není přidaná nová funkční elektronika.
