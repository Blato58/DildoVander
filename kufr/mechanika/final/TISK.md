# Výroba F2 bez podpěr

Zdrojem geometrie jsou `chassis.py`, `mechanism.py` a `layout.py`.
Výrobní seznam vzniká ze skutečných `Part` v `build.all_parts()`;
`manufacturing.py` přiřazuje strategii, orientaci, materiál, profil a spoj.
Tento návrh nemá fyzickou přejímku. Zkouška vůlí, lepení, zatížení a
opakovaného zdvihu zůstává podmínkou výroby celé sestavy.

## Současné výrobní soubory

- `exports/stl/<id>.stl`: jeden uzavřený, nezávisle tisknutelný díl,
  již ve výrobní orientaci na Z=0.
- `exports/3mf/<id>.3mf`: stejná orientovaná geometrie. Jde o modelový
  3MF; načíst také **`exports/profiles/<id>.ini`** pro konkrétní díl.
  Základní `support-free-petg.ini` není profil namáhaných dílů.
- `exports/printing-manifest.csv` a `exports/manifest.json`: všechna ID,
  množství, kontaktní plochy, rozměry, profily, náročnost, spoje a materiál.
- `exports/printability.json`: geometrická analýza vrstev a stěn.
- `exports/slicer-validation.json`: skutečný slicer, jeho verze, kontrolní
  součty vstupů, účinné nastavení, vrstvy, podpěry a 3MF.
- `exports/verification.json`: geometrie, STL/STEP, sestava, pohyb,
  servisní přístupy, kabeláž a nominální pevnostní předpoklady.
- `exports/F2-kouzelny-kufr.zip`: ověřené zdroje a právě aktuální výrobní
  výstupy. Historické nebo diagnostické STL nejsou součástí balíčku.

Podrobné G-code a výpisy sliceru zůstávají v lokálním `exports/slicing/`.
Nejsou připravené pro konkrétní tiskárnu a do ZIP se nezahrnují. Do tiskárny
poslat až G-code vytvořený jejím vlastním odpovídajícím profilem.

## Tiskový kontrakt

Skutečná tiskárna ani její kalibrace nejsou doložené. Konzervativní
pracovní předpoklad: FDM prostor **220 × 220 × 250 mm**, PETG, tryska
**0,4 mm**, vrstva i první vrstva **0,2 mm**. Podpěry **NONE**, jejich
automatické generování vypnuté, raft 0, brim 0. Neotáčet ani neměnit
měřítko připravených modelů. Teploty 240/80 °C a chlazení mostů v INI
jsou obecné hodnoty; ověřit je pro skutečný filament a stroj.
Modely ponechat v dodané orientaci; ve sliceru je rozmístit v XY uvnitř
plochy stroje včetně brimu. Ověřovací CLI je centruje na 110,110 a
kontroluje obálku skutečně extrudovaného materiálu včetně šířky dráhy.

Výjimka brimu: `carrier_microphone`, `left_service_carrier_frame_left`
a `left_service_carrier_frame_right` mají **3 mm** kvůli skutečnému
varování o adhezi vysokých úzkých dílů. Opakované řezání s tímto brimem
varování odstranilo. Brim není podporou; všechny ostatní díly mají 0 mm.

Rám, kotvy, základny zdvihu, obě poloviny lůžek, vačky a všechny tři
měrky: **6 perimetrů, 100 % rectilinear, 5 horních/5 spodních vrstev**.
Elektronické držáky a nenosné kryty: **4 perimetry, 40 % gyroid,
5 horních/5 spodních vrstev**, pokud jejich konkrétní profil neurčuje víc.
Řídká výplň nenahrazuje plné průřezy použité v pevnostním výpočtu.

Analýza dovoluje nejvýše 45° od svislice a skutečný volný most do 12 mm
s materiálem na obou koncích; řeší také počátek na podložce, oddělené
počátky, prostor stroje a přetrvávající úzké stěny. Procesní dolní mez
0,4 mm platí pro nenosné membrány, nikoli jako doporučená konstrukční
stěna. Kotevní střechy mají 3 mm; jejich 0,5mm slepé dno je pouze
bariéra špičky šroubu. Lokální vady pod rozlišením vzorkování, adheze,
vrstvení, chlazení, opotřebení a skutečná pevnost vyžadují výtisk.

Geometrický most je mezera bez kotev. Délka extrudované dráhy sliceru
obsahuje také kotvení, takže údaj `max_bridge_toolpath_segment_mm` může
být větší. Pouze varování `Long bridging extrusions` lze přijmout po
samostatné kontrole skutečně uložených drah předchozí vrstvy: oba konce
musí být na materiálu a každá jednotlivá mezera nejvýše 12 mm. Výsledek,
původní varování a mezery zůstávají v protokolu. `Floating bridge anchors`,
`Loose extrusions`, neznámá či neúplná varování vždy kontrolu zastaví.
Samotné vypnutí podpor v sliceru není důkaz tisknutelnosti; obě brány
musí projít. Stav `SKIP_SLICER_UNAVAILABLE` znamená pouze analýzu,
nikoli ověření slicerem.

Profily `carrier_pca9685` a `carrier_pixel_shifter` předepisují
`bridge_angle = 90`: střechy páskových tunelů se překlenují po krátké
ose. Skutečné zkušební řezání při automatickém směru odhalilo plovoucí
kotvy; při 90° prošly všechny dráhy bez varování a bez podpor.
Samostatná kontrola uložených drah našla nejvýše 2,501 mm volné mezery.
Geometrie STL se touto volbou nemění; dodaný profil je součást výroby.

## Rozhodnutí o dílech

Rám si ponechává čtyři panely. Přeplátování nahradily svislé rovinné
rybiny pouze v 4mm desce; původních osm M3 svírá oba panely. Pod žebry
se rybiny nevyřezávají, aby každé žebro vyrůstalo z vlastního materiálu.
Boční otvory mikrofonu/kamery mají propojené šikmé střechy nad původními
osami a zahloubeními. Krajní kazetová matice má otevřený boční vstup.

Čtyři kotvy zůstávají v jednom kuse každá, plochou horní stranou dolů.
Krček odlitého epoxidového zámku přechází z Ø7,5 na Ø10,5 pod 45°.
Souvislá 3mm střecha nad každou maticí zůstává. Vnější přístup k matici
je odspodu otevřený; slepé tvarované dno zůstává pod konečnou polohou
matice. Před lepením osadit a dočasným šroubem usadit každou matici
ke střeše. Chránit otevřené vstupy, závity a dutiny před epoxidem.
Odlehčení pod hlavami M3 si ponechává plný Ø6,2 prostor pro hlavu;
pod ním se slepá kapsa uzavírá kuželem 45°. U volného okraje kotvy
zůstává vstup otevřený, aby nevznikal nový nepodepřený lem.
Kužel končí malou plochou Ø0,6 místo špičky; žádné odlamování není nutné.

Každé lůžko má **levou a pravou polovinu**. Jeden společný výtisk měl
neslučitelné orientace pro obě vedení a čep. Levá hrana je na podložce
po −90° Y, pravá po +90° Y; čep a zadní vedení zůstávají integrální
na pravé polovině. Dva spodní příčníky mají krátké klíče, otevřené
matice a dva příčné M3 × 45. Spoj nenese žádná hůlka, elektronika ani
pásek. Otevřené průchody Y60–68 zachovávají přepravní aretaci.

Základna má šikmé podpory drobných lemů kolébky. Vačka si ponechává
zákon pohybu, zdvih 16 mm a drážku 6,8 × 6 mm; trvalé můstkové piloty
odstraňují vnitřní převisy u páky bez odlamování nebo vrtání.
Kryty jsou samostatné a tisknou se širokou vnější střechou dolů;
USB parkovací klip bokem. Kryty ani odnímatelný obsah nenesou rám.
Elektronické držáky mají lokální šikmé přechody, silnější patky a lemy
při zachování skutečných obálek a přístupů uvedených v montážním návodu.
Slepé montážní kanály nezůstávají uzavřenými dutinami: ESP, PCA, Pixel
a pravý zesilovač mají původní Ø3,4mm průchod otevřený skrz nosnou podlahu.
MCP, PD, samostatné kondenzátorové klipy a dvě patky sloupků banky mají
malý boční kanál nad montážní membránou: šířka 0,8 mm dole, 0,2 mm nahoře,
výška 0,8 mm, Z15,3–16,1. Jeho šikmé stěny zachovávají dosednutí matice
i podpory stropu; nic se nevyplňuje podpěrami. Po otevření montážního
průchodu je vnitřek dostupný také zespodu.

Čtyři horní matice krytů mají boční vstupy 6,7 × 4,3 × 2,7 mm
a celou šestihrannou kapsu vysokou 2,7 mm pro 2,4mm matici.
Přední / zadní vstup sloupků banky směřuje −Y / +Y; stejně oba sloupky
pravého reproduktoru. Matice zasunout na stole před kryty a textilem,
v poloze Z102,45 s 0,15mm vůlí k oběma vodorovným plochám; při dotažení
dosednou na střechu Z105. Pro dočasné přidržení použít M3 × 6 z původního BOM,
nebo skutečný 3mm kryt / 3mm distanci s M3 × 10. Holý sloupek s M3 × 10
by dovolil šroubu narazit na dno slepého otvoru. Kapsu ani střechu
neroztahovat silou; vnější opěrná stěna pravého reproduktoru má 0,8 mm
a vyžaduje zkušební tisk i šetrné dotažení.

Původní společná měrka se rozdělila podle skutečných výrobních
orientací: `fit_coupon` pro čep, vedení a hlavy; `follower_groove_coupon`
pro drážku; obrácený `floor_anchor_coupon` pro maticové tunely, mosty
a zámek epoxidu. Tyto tři díly se do kufru neinstalují.

Celkem **51 typů STL / 71 výtisků** včetně tří měrek; do sestavy patří
48 typů / 68 výtisků. Každý soubor a počet vede aktuální manifest.

Konzervativní profil při samostatném řezání všech 71 kusů odhaduje
přibližně 258 hodin celkového tisku. Jednotlivé odhady jsou ve
`slicer-validation.json`; nejde o změřený čas ani optimalizované společné
desky konkrétní tiskárny.

## Dělené kazety, kamera a ovladače

Pro servis levé kazety v kufru nejprve odpojit a vyjmout všechny tři
ovladače, vyšroubovat čtyři horní spoje, zvednout ovládací desku a pak
volný pravý boční rám. Levý boční rám se spodními šrouby zůstává na místě.
Teprve potom lze povolit tři horní šrouby kazety a svisle ji vytáhnout.
Při obnově je pořadí opačné, spínače až po dotažení horních spojů.
`assembly_access.py` kontroluje jednotlivé kroky včetně skutečného
korpusu, ponechané levé opory, matic a vytažení šroubů. Západky spínačů,
rukojeti a rozpojování skutečných vodičů vyžadují prototyp.

Každá elektronická kazeta má `*_frame_left`, `*_frame_right` a
`*_tier_1`, `*_tier_2`, `*_tier_3`. Patra se tisknou dnem dolů, rámy na
svých původních montážních patkách. Jednodílné kazety měly zavěšená
vodorovná patra, dlouhé vnitřní mosty a špatně dostupné spodní spoje.
Každá spojka má dvě polohovací pera s 0,25mm vůlí a boční svěrný M3.
Patro kondenzátorů levé kazety má jednu spojku; zbývajících pět pater
má dvě. Svěrné plochy jsou součástí rámu, desky ani pásky je nenahrazují.
Spodní plinta má otevřenou drážku pro USB; nová trasa u banky zachovává
20mm výstup konektoru a tečné oblouky R20. Původní krátká klenba zůstává
pro odpojené parkování. Čtyři průchody vývodů kondenzátorů zůstávají
otevřené i po vytvoření spojek.
Každá dvojice vývodů má jeden obdélníkový vstup 7,8 × 3,8 mm;
odstraněná tenká přepážka a křivkové křížení nezasahují do opěrného kroužku.
Kondenzátor 1 navíc používá dvě oddělené zadní trasy Y110,2 a Y112,9
v Z58,5, které obcházejí hlavu prostředního spoje. Kondenzátor 2 zůstává
ve svých předních kanálech. Polohy a elektrické připojení se nemění.
Místní horní otevření zadního kanálu odstraňuje tenkou střechu pod C1.
Nominální opěrná plocha jeho dna klesá z 36,69679 na 21,86511 mm²;
u C2 z 36,69679 na 29,30932 mm². Přední dosednutí a všechny čtyři
pružné opory i boční stěny každého klipu zůstávají geometricky celé.
Menší plocha není důkaz stejné fyzické únosnosti: výtisk ověřit s oběma
skutečnými kondenzátory, opakovaným vyjmutím a přepravním zatížením.
Šest krajních kazetových šroubů má návrhovou rezervu 0,35 mm do bezpečné
obálky, plný 2,4mm závit matice a 0,8mm přesah špičky za matici.
Pravý rám levé kazety nemá původní úzký zadní výběžek nad Z48:
jeho krček vyvolal skutečné varování sliceru o křehké části. Zadní pero
horního patra je v pevné části u Y82 místo Y92; obě pera, svěrný spoj,
podpora PCB a pásky zůstávají funkční. Nevzniká další výtisk ani spoj.

1. Na stole zasunout matice M3 ISO4032 z vnější strany do otevřených kapes
   rámů. Patra přiložit ke dvěma perům; rámy nasunout vodorovně podél X.
2. Spoje stáhnout z vnitřní strany šrouby odpovídající délky z manifestu.
   Nepřitahovat nezasunuté pero silou. Klíč zůstane přístupný před PCB;
   při servisu nejprve uvolnit pásky příslušné desky.
   **Levá kazeta: svěrný spoj patra 2 dotáhnout před uzavřením patra 1.**
   Spodní doraz jinak omezuje délku přímého dříku nástroje. Při servisu
   prostředního spoje nejprve odpojit a vyjmout spodní patro 1 na stole.
   Spodní PCB předem zapojit na jeho samostatném patře; rámy zatím
   ponechat volné pro boční vložení. Sestavu teprve nakonec celou stáhnout.
   Všechny kazetové spoje dotáhnout bez USB v konečné trase. Potom bez
   banky a reproduktoru položit neporušený odpojený C–C kabel, dosud
   nesvinutý do cívky. Přední místní průchod vložit otevřeným okrajem −Y,
   zadní +Y a pod spodním patrem použít otevřenou drážku. Konektor se
   neprotahuje malým otvorem. Tři závity cívky vytvořit a stáhnout až
   nakonec; před dotažením kabel odložit mimo dráhu nástroje.
3. Spodní desky připojit před horními patry. Kazetu vyzkoušet samostatně,
   teprve potom upevnit její původní patky třemi/čtyřmi horními M3 × 8.
4. Vyjímání celé kazety: odpojit označené rozhraní, uvolnit její horní
   šrouby, zvednout svisle. Reproduktor u příslušných přístupů dočasně
   vyjmout z pásků. Při rozebrání patro a oba rámy položit na stůl.

Malý zadní výřez lemu `right_front_cover` uvolňuje rozšířená kazetová
maticová sedla. Horní deska, krytové šrouby a nosný rám zůstávají celé.
Kryt ani po tomto odlehčení nenese elektroniku nebo konstrukci.

![Levá kazeta — rozložené díly](exports/images/left_service_carrier-exploded.png)
![Pravá kazeta — rozložené díly](exports/images/right_service_carrier-exploded.png)

Držák kamery tvoří `camera_post_left`, `camera_post_right`, `camera_angle`.
Sloupky se tisknou vnější boční plochou dolů, deska dnem dolů. Tím zaniká
dlouhý most mezi sloupky. Původní kamera zůstává vzadu uprostřed nad ESP.
Patky vynechávají zadní žebro; stojiny mají odstup od držáku ESP.
Do sloupků z vnitřní strany vložit dvě M3 ISO4032, přiložit desku a
shora spojit dvěma M3 × 10 ISO7380-1. Patky upevnit původními šrouby
kamery. Kamera/FPC se osazuje až potom; konektory nesmějí nést sloupky.
Pro servis uvolnit kameru a horní šrouby, nikoli páčit uzavřenou sestavu.

![Kamera — dva sloupky a deska](exports/images/camera-exploded.png)

Ovladače tvoří `switch_frame_left`, `switch_frame_right`, `switch_shelf`.
Boční rámy se tisknou vnější plochou dolů, deska spodkem dolů. Jednodílná
vysoká police měla nepodepřený strop; nyní ho nesou čtyři sloupky.
Na stole vložit matice z vnitřní strany a přiložit desku. Přední dva
spoje používají M3 × 10 ISO7380-1, zadní dva **M3 × 10 DIN7991** se
zapuštěnými hlavami. Zadní hlavy musejí být v rovině Z105; vyšší válcová
hlava se pod vypínač nevejde. Nejprve dotáhnout všechny čtyři spoje,
potom osadit vypínače a PTT, nakonec nenosný kryt. Při servisu zadních
spojů se nejprve sejme kryt a vyjmou ovladače. Původní spodní patky
zůstávají samostatně přišroubované k rámu.

![Ovladače — dva rámy a horní deska](exports/images/controls-exploded.png)

Všechny tyto spoje jsou rozebíratelné; šrouby ani matice se nezkracují.
Neznámé doručené konektory a kamera/FPC vyžadují skutečnou zkoušku přístupu.

## Spojení lůžka

1. Vytisknout `wand_bed_left` a `wand_bed_right` pro každý modul 1–6.
2. Na stole zasunout klíče přímo podél X; součet vůlí styčných ploch
   0,25 mm, v kapse také 0,25 mm na každé vedené straně. Bez násilí.
3. Vložit tenké **M3 DIN439, 5,5 mm přes plochy, výška 1,8 mm** do
   otevřených levých kapes příčníků Y74 a Y205. Vyšší DIN934 nepasuje.
4. Zprava osadit **2 × M3 × 45 ISO7380-1**, hlava Ø5,7 × 1,65 mm.
   Klíčem/pinzetou podržet matice, jemně dotáhnout. Celý závit matice
   zabírá, špička modelově přesahuje 1,2 mm uvnitř obálky modulu.
5. Provléknout přepravní pásek; osadit U059/LED, vačku a servo podle
   [POSTUP.md](POSTUP.md). Šrouby spoje osadit před těmito součástmi.
6. Celou horní skupinu vložit na vodítka až po dotažení holých základen.
   Pro rozdělení lůžka skupinu vyjmout na stůl; okolní moduly zůstávají.

![Lůžko — levá a pravá polovina](exports/images/bed-exploded.png)

Celkem navíc pro lůžka **12 M3 × 45 a 12 tenkých M3 DIN439**.
Nejsou prohlášené za nakoupené. Úplný materiál, nové spoje kazet a
účel každého spojovacího prvku vede [mechanicky-material.csv](mechanicky-material.csv).

## Nový spojovací materiál

Navíc proti původní výrobní sadě; žádný z těchto kusů není prohlášený
za nakoupený. Zbylý materiál sestavy zůstává v úplném BOM.

| Místo | Šrouby | Matice |
| --- | --- | --- |
| Šest dvoudílných lůžek | 12 × M3 × 45 ISO7380-1 | 12 × tenká M3 DIN439, výška 1,8 mm |
| Jedenáct spojů kazet | 10 × M3 × 12, 1 × M3 × 10 ISO7380-1 | 11 × M3 ISO4032, výška 2,4 mm |
| Kamera | 2 × M3 × 10 ISO7380-1 | 2 × M3 ISO4032 |
| Ovladače | 2 × M3 × 10 ISO7380-1 + 2 × M3 × 10 DIN7991 | 4 × M3 ISO4032 |

Celkem **29 šroubů a 29 matic navíc**, běžné metrické provedení.
Podrobné souřadnice, délku každého spoje a sousední díly vede manifest.

## Minimální dokončení M3 otvorů

Výrobní CAD má **40 míst** s dvoustupňovou střechou matic: 34 v nosičích
a šest v krytových sloupcích. První 0,4mm pilotní stupeň odlehčuje most,
druhý 0,4mm uzávěr zavírá Ø3,4 otvor. Maximum lokálního materiálu pro
propíchnutí je **0,8 mm**. Přesné nativní souřadnice a rozměry pilotu
jsou v `shaft_membranes_native_coordinates` manifestu; s dílem se otáčejí.
Na stole před šrouby otevřít hladkým ručním šídlem, vyjmout úlomek a
zkontrolovat volný průchod Ø3,4. Nepilovat, nevrtat, neubírat maticové
dosedací plochy. Jde o dvě vrstvy malého otvoru, nikoli podpory v dutině.

Trvalý pilot zůstává i v instalovaném CAD. Typická dosedací plocha M3
klesá z 17,525 na 12,824 mm²; nelze tvrdit nezměněnou únosnost všech
matic. Zvlášť kontrolovat zadní levou matici reproduktoru vedle otevřené
servisní patky: zúžený pilot 1,2 × 6,7 mm zde zachovává 9,718 mm²
z původních 12,376 mm², snížení 21,5 %. Dotažení bez prasklin, tahová zkouška a opakované servisní
rozebrání se ověří na PETG výtisku. Šroub nepoužívat k násilnému prorážení.

## Regenerace a kontrola

Použít verze z `requirements.txt` ve vlastním Python prostředí.
Z adresáře `final/` spustit `./Regenerate.ps1 -Python ../.venv/Scripts/python.exe`.
Pro skutečné řezání nastavit `KUFR_SLICER` na dostupný
`prusa-slicer-console.exe`; použitá cesta není součástí zdrojů.
PrusaSlicer 2.9.4 se zde používá jako externí ověřovací nástroj,
nikoli jako přibalený software nebo důkaz vlastností domácí tiskárny.

Regenerace spustí regresní testy, z prázdného adresáře vytvoří aktuální
exporty, geometrickou bránu, případný slicer, viewer a obrázky,
ověří sestavu a vydá ZIP teprve po všech kontrolách. Změna zdroje,
profilu, STL, 3MF nebo jiného výstupu zneplatní kontrolní součty.
STL projdou také kontrolou uzavřeného orientovaného hranového grafu,
degenerací a křížení trojúhelníků s tolerancí 0,0003 mm; STEP se znovu
načte a objem porovná s CAD. Vrstevná analýza používá krok 0,2 mm a
vzorkování šířek/mostů 0,8 mm. Nenahrazuje detekci všech vad menších
než její rozlišení ani pevnostní zkoušku.
Tečné úsečky materiálu kratší než tolerance sítě 0,03 mm se při
hodnocení mostu vynechávají. Vyhodnocený most musí mít skutečnou
nenulovou délku a každý rozlišitelný průřez dvě plné kotvy; celé
nerozlišitelné oblasti ani izolované začátky tím kontrolou neprojdou.

Kabeláž navíc kontroluje spojitý zdvih 0–16 mm vůči novým dolním
příčkám lůžek a jejich spojovacím prvkům. Konzervativní obálky skutečných
CAD příček a šroubů se porovnávají s kabelovými kapslemi zvětšenými
o 0,3 mm a chybu tětivy známých oblouků; kladná mezera prokazuje celý
zdvih i nezávislé polohy modulů.
Tato kontrola neprokazuje skutečný tvar ohybu kabelu ani spojitou rotaci
vačky. Ostatní objemové kabelové kontroly používají polohy 0°, 80° a
160° a parkování pro přepravu; přesný rozsah vede `verification.json`.
Montáž USB navíc používá skutečné místní R20 trubice Ø5,1 proti uzavřené
kazetě včetně jejích deseti šroubů/matic: 41 poloh po 0,5 mm pro každý
otevřený okraj a obě úplné 12 × 20 × 12mm obálky konektorů. Jde o
vzorkovanou cestu pokládání volného kabelu, nikoli spojitou deformaci
nebo zasunutí celé pevné předtvarované smyčky. Reálný kabel vyžaduje zkoušku.

## Fyzické přejímky

Nejprve tři měrky a jeden celý modul: skutečné vůle, jakost čepu/drážky,
průchod pásků, celé zabírání matic, dotažení bez praskání a zatížený
zdvih ve všech polohách. Samostatně otestovat spoje polic kazet a jejich
vyjmutí s reálnými konektory.
Oba levé kondenzátory mají po otevření kanálů menší opěrné plochy uvedené
výše; ověřit jejich dosednutí, pružné opory a držení při otočení kazety.
Potom zkušební kotva, plnění komory, adheze k holému dřevu a zátěžové
zkoušky z [MONTÁŽ.md](MONTÁŽ.md).
Nominální výpočet kotev neověřuje radiální štípání/ohyb nového kužele,
PETG creep, únavu, lepidlo ani chování v horkém autě. Servo musí zvládnout
aktualizovanou hmotnost dvoudílného lůžka a spojovacích prvků.
