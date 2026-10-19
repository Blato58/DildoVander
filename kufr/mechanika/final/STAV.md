# Výrobní revize F2-SF-DFA — 14. 10. 2026

Implementované CAD zdroje: `chassis.py`, `mechanism.py`, `layout.py`.
Rám má rovinné rybiny, kotvy tisknutelné kuželové epoxidové zámky;
každé lůžko dvě klíčované poloviny, každá kazeta dva rámy a tři patra,
kamera i ovladače dva boční nosiče a desku. Tři měrky mají samostatné
výrobní orientace. Výrobní sada má 51 typů / 71 výtisků, z toho
48 typů / 68 instalovaných výtisků. Navíc 29 standardních šroubů a
29 matic podle `mechanicky-material.csv`; jejich nákup není potvrzený.

`manufacturing.py` generuje manifest, orientovaný STL/STEP/modelový 3MF
a konkrétní profil každého dílu. Podpory NONE, raft 0; pouze mikrofonní
držák a oba levé kazetové rámy mají 3mm brim po skutečném varování adheze.
40 M3 střech má dvě 0,4mm vrstvy, lokálně nejvýše 0,8 mm k otevření
hladkým ručním šídlem. Trvalé piloty jsou i v instalovaném CAD; nejmenší
revidované dosednutí zadní matice levého reproduktoru má 9,718 mm².
Levé patro 2 se dotahuje před patrem 1; při servisu opačné pořadí.
Pro vyjmutí levé kazety z kufru se předem odpojí tři ovladače,
uvolní čtyři horní spoje a vyjme deska a volný pravý rám ovladačů.
Levý rám s původními spodními šrouby zůstává. Každý krok má vlastní
objemovou servisní kontrolu; původní přímá cesta bez těchto kroků koliduje.
Pravý rám levé kazety má odstraněný slabý zadní výběžek; zadní pero
horního patra se přesouvá z Y92 do pevné oblasti Y82. Pět částí kazety
i původní PCB/pásky zůstávají. USB se pokládá otevřenými hranami až po
dotažení svěrných spojů, s oběma konektory odpojenými a cívkou dosud
nesvinutou. Vzorkované místní cesty ověřuje `assembly_access.py`.
Uzavřené dutiny montážních kanálů odstraňují průchozí podlahové otvory
a malé boční kanály se šikmou střechou. Čtyři horní matice krytů mají
boční vložení a 0,3mm celkovou výškovou vůli; kryty se osazují až poté.
PCA a Pixel mají v konkrétních profilech směr mostů 90° po krátké ose
páskových tunelů; skutečné oddělené řezání a kontrola drah odstranily
plovoucí kotvy při zachování identických STL. Úplné ověření celé revize
musí tento profil použít znovu pro všech 51 typů.

Kabelové trasy zachovávají všech 135 připojení a deklarované prostorové
průměry včetně neověřených předpokladů. Průměry nejsou fyzicky změřené.
Nová otevřená USB klenba a obnovené vývody kondenzátorů zachovávají
pevné konce. Kondenzátor 1 obchází svěrný šroub dvěma oddělenými zadními
kanály; kondenzátor 2 používá původní přední vstupy.
Horní otevření kanálu snižuje opěrnou plochu C1 z 36,69679 na
21,86511 mm² a C2 na 29,30932 mm²; pružné opory a přední dosednutí
zůstávají celé. Fyzické držení kondenzátorů vyžaduje samostatnou zkoušku.
Tečný R20 výstup USB u banky má vlastní skutečné objemové těleso.
Vratné R8 smyčky mají pevnou
nohu Y79, pohyblivou Y63 a poloviční pohyb oblouku; délka zůstává
konstantní při nezávislém zdvihu všech šesti modulů. První společná kontrola
odhalila staré jednodílné testy a kolize vodičů s novými spoji;
skutečné výrobní díly mají vlastní testy
a přesměrované trasy se kontrolují konečnými objemovými kapslemi.
Žádné kolizní filtry ani elektrické zapojení se tím neuvolňují.
`tray_joint_clearance.py` navíc prokazuje spojitý zdvih 0–16 mm proti
novým dolním příčkám a spojovacím prvkům konzervativními obálkami
z CAD. Nenahrazuje skutečný tvar ohybu kabelů ani spojitou rotaci vaček.

Reprodukovatelná brána: `Regenerate.ps1 -Python ../.venv/Scripts/python.exe`
s `KUFR_SLICER` pro PrusaSlicer 2.9.4. Zahrnuje regresní testy, uzavřenost
a křížení STL, STEP round-trip, vrstevnou analýzu, skutečné řezání, objem
tiskárny včetně brimu, servisní dráhy, sestavu, pohyb, kabeláž a ZIP.
Aktuální výsledek, přesné počty/vzorky a zdrojové/výstupní SHA-256 určují
`exports/verification.json`, `printability.json`, `slicer-validation.json`
a `generation-snapshot.json`. ZIP se vydává pouze po těchto branách;
historické výsledky níže nepotvrzují tuto revizi. Celkové ověření a Git
předání této práce vede také privátní evidence.

Fyzický tisk a montáž nejsou provedené. Konkrétní tiskárna, PETG
tečení/vrstvy, dotažení matic, kluzný čep, reálné konektory/FPC, prsty,
kabelové rezervy, dřevo/epoxid, servo a přeprava nejsou přijaté.
Další krok: tři PETG měrky, jeden celý modul a kazetový spoj; ověřit
vůle, celý zdvih a servis, potom vzorek kotvy a zátěž podle `MONTÁŽ.md`.
Návrh zůstává bez fyzického uvolnění k sériové výrobě.

---

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

# Historický systém F2 před revizí bez podpěr

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
