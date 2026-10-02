# Tisknutelná mechanika AI kufru – prototyp

CAD návrh pro **nadzvednutí celé přihrádky**, šest hůlek do **200 mm** a kufr
**450 × 310 × 200 mm**, podle zadání majitele 2. 10. 2026. Je to vyrobitelná
sada geometrických dílů pro první mechanický prototyp; skutečné usazení,
nosnost a stabilita bez proudu dosud nejsou ověřené. Firmware se tím nemění.

**Nejprve vytisknout měrku a jednu přihrádku.** Před tiskem celého kompletu
změřit vnitřek kufru, prostor pod víkem, průměr/nepravidelnou rukojeť hůlek,
doručené servo, U059, PCB, konektory a powerbanku. U rozměrů kufru zatím není
potvrzeno, zda jde o vnitřní prostor. Průměr hůlky **16 mm** a tisková plocha
**220 × 220 mm** jsou pracovní předpoklady, nikoli údaje od majitele.

## Co dostaneš

- Jednotlivé **STL v milimetrech**, každý soubor obsahuje jeden pevný díl.
- Jednotlivé **STEP**, sestavu jedné přihrádky a sestavu všech šesti.
- `nahled.png` a samostatný offline `nahled.html` s otáčením a ukázkou zdvihu.
- `tiskovy-kusovnik.csv`, `verification.json` a celý balík ZIP.
- Editovatelný parametrický zdroj `model.py`, `preview.py`, `parametry.json`.

Generované výstupy jsou v `output/`, nejsou verzované. ZIP obsahuje návod,
parametry, zdroj, STL, STEP, náhledy a ověření. Zdroj se uchovává také v repozitáři.

## Rozvržení

Osa X je šířka kufru **310 mm**, Y délka **450 mm**, Z výška **200 mm**.
Šest modulů má rozteč **48 mm**, délku **240 mm** a přihrádku širokou
**28 mm**; délková rezerva pro 200mm hůlku je 20 mm na každém konci.
Mechanické moduly zaberou přibližně **288 × 240 mm**. Pod nimi je servo
a vibrační jednotka. Vzadu jsou dva servisní sloupce na samostatných policích.
Model kontroluje vejití do zadaného obálkového prostoru, nikoli skutečnou
truhlu Marketos, panty, výstelku či kování. Do rozměrů je nutné započítat
montážní vůli a odlehčení kabelů. Náhled obsahuje pracovní obálky hůlek,
serva, U059 a powerbanky, nikoli přesné tovární modely.

Zvolená přihrádka se zvedne přibližně **28,3 mm**. Pro první kus není třeba
tisknout velkou společnou vanu: každá základna i přihrádka má tři 80mm úseky,
spojené šroubovanými pásky. Dvě kovové tyče na koncích vedou vložku vzhůru.
Klika serva s válečkem je zachycená v příčné drážce pod vložkou. Horní poloha
je kinematický návrh; pulzy konkrétního serva se musí individuálně kalibrovat.

**Klika je zpětně pohánitelná.** Bez nezávislého mechanického zajištění může
zvednutá vložka po odpojení proudu klesnout. Návrh proto zatím **nesplňuje
požadavek plánu na stabilitu bez napájení** a není hotová provozní mechanika.
Spodní podpěrné sloupky nesou vložku v klidové poloze. Před poháněným
předváděním je třeba doplnit a ověřit pasivní zachycení/protiváhu a chování
ve všech polohách včetně Stop, ne pouze v horním a spodním bodě. Váleček,
horní krytky a převody serva nejsou bezpečnostní zámek. U první zkoušky
vložku přidržet a posouvat ručně s odpojeným pohonem.

## Usazení elektroniky podle nákupního seznamu

Zdroj dílů: [nákupní seznam](../../docs/nakupni-seznam-kufr.md) a
[efekty/napájení](../docs/efekty-a-napajeni.md). Rozměry krabiček jsou vnější;
stěny mají 2,4 mm, dno 3 mm, vnitřní rohové sloupky zabírají další prostor.
Vývody a kabely musí mít vlastní rezervu. Montážní desky používají univerzální
rastr a M3 sloupky; netvrdí shodu s nezměřenými otvory konkrétních PCB.

| Součást | Usazení | Co je doložené / co změřit |
| --- | --- | --- |
| 6× 180° 9g servo | `servo_strap_cradle`, pásky + `crank_horn_adapter` na původní páce | Typ z košíku; tělo a osa jsou odhad. Rozteč otvorů páky upravit podle kusu. |
| 6× M5Stack U059 N20 | `vibration_cage` pod pohyblivou vložkou | Odhad obálky 32 × 24 × 14 mm. Pásky nesmějí škrtit motor ani konektor. |
| 6× WS2812B úsek asi 200 mm | Svislé lůžko na vnitřní bočnici vložky | Šířka 10 mm je odhad; změřit IP65 silikon, LED a vývody. |
| ESP32-S3 N16R8 | `controller_box` + rastr | Přístup k oběma USB, EN/BOOT, kamerovému konektoru; anténa daleko od kovu a zdroje. |
| PCA9685 | `pca9685_box` + rastr | Rezerva pro konektory šesti serv a šesti vibračních signálů; rozměry změřit. |
| Pixel Shifter 6066 a rezistor 330 Ω | `controller_box` + rastr, odděleně od antény | Změřit PCB a konektory; držák nepředepisuje nezměřené montážní otvory. |
| PD trigger, XY3606, pojistka, vypínač, svorky, kondenzátory | `power_box` + rastr | Dva oddělené držáky/desky, 20V úsek izolovat. Větrání ověřit měřením. |
| Watchdog, load switch, enable logika | `safety_box` + rastr | Rezervovaná krabička; konkrétní bezpečnostní díly ještě nejsou vybrané. |
| MAX98357A | `audio_box` + rastr | Změřit PCB, konektory a větrání. |
| ICS43434 | `microphone_box`, víko s akustickým otvorem | Otvor 6 mm je pracovní; kapsli nasměrovat do něj, upevnit samostatně a oddělit pěnou od vibrací. |
| Waveshare reproduktor | `speaker_box` + mřížka | Obálka **100 × 45 × 21 mm** z košíku. Montážní otvory a akustická vhodnost nejsou ověřené. Zatím jeden kus. |
| Powerbanka | `powerbank_strap_cradle` + dva pásky | Pracovní obálka **155 × 70 × 28 mm**; majitel dosud model nespecifikoval. |
| PTT, hlavní vypínač, PHYSICAL_ENABLE | `control_panel` | PTT otvor 12,4 mm, dvě obdélníková okna 20 × 13 mm. Okna upravit podle skutečných spínačů. |
| Router/AP | `router_optional_box` + rastr | Volitelný držák mimo osazenou sestavu; konkrétní router chybí. Musí dostat místo/napájení po změření, nebo zůstat mimo kufr. |
| Budoucí OV5640 / MCP23017 | Volné místo u ESP / v `safety_box` | Kamera je volitelná. Optická montáž a expander nejsou bez vybraného kusu dokončené. |

Perforované desky slouží pro malé modulové PCB nebo univerzální DPS, včetně
plánovaných 50 × 70 mm desek tam, kde vyjde rozměr. Pod PCB použít nejméně
5mm distanční sloupky, izolující podložky a spojovací materiál odpovídající
otvorům. Čtyři skutečné otvory desky nemusí sedět do rastru: vyvrtat/upravit
montážní desku, nebo upevnit vhodnými pásky bez tlaku na součástky. Reproduktor
a mikrofon vyžadují vlastní upevnění/pěnové těsnění; univerzální ventilované
krabičky jsou montážní obálky, nikoli změřené akustické ozvučnice.

Police jsou nesené **M4 kovovými sloupky / závitovými tyčemi s maticemi**,
ne víčky krabiček. Krabičky upevnit nezávisle do polic přes montážní otvory
nebo pásky. Výšky levých polic 0/50/105/150 mm, pravých 0/50/95 mm.
Horní reproduktor končí asi ve 189 mm: zadaná výška 200 mm nechává malou
rezervu. Zmenšení vnitřního prostoru víkem znamená nutnou změnu rozvržení.
V univerzálních krabičkách nejsou modelované tovární PCB ani kabely; jejich
reálné vejití je samostatná zkouška.

Vibrace se přenášejí pevně do vložky přes cage. Dvě vodicí tyče však část
vibrací přenesou i do rámu. Není dokázáno, že se bude třást pouze vybraná
hůlka; jeden prototyp musí ověřit účinek a případně doplnit pružné uložení.
LED i U059 cestují s přihrádkou: nechat nejméně **40 mm volného zdvihu
kabelové smyčky**, zajistit konce a ručně projet celý rozsah. Konektory ani
vodiče se nesmějí dotknout kliky/drážky. `cable_clip` patří na pevný rám.

## Tisk a montáž jedné přihrádky

1. Tisknout `fit_coupon`: první řada otvorů je M3, druhá řada od X− do X+
   má průměry **4,1 / 4,3 / 4,5 / 4,7 / 4,9 mm**. Podle skutečné 4mm tyče
   vybrat běžnou vůli; otvory případně vystružit. Tiskárna ani materiál dosud
   nebyly potvrzené. Model ověřuje jen obálku 220 × 220 × 250 mm.
2. PETG jako pracovní materiál, tryska 0,4 mm, vrstvy 0,2 mm, nejméně
   4 obvody. Kliku, follower a spoje lokálně zesílit; výplň 35–50 % je
   výchozí nastavení, nikoli výpočet únosnosti. PLA u měniče nedoporučujeme.
3. Základny a vložky tisknout plochým dnem na podložce. Vložky mají pod
   vodicími pouzdry a montážními výstupky převisy: podpěry jen pod mosty,
   ne uvnitř otvorů. Follower položit boční plochou k podložce, kliku
   rovnou stranou, váleček osou svisle, krabičky a klec otevřené nahoru.
   STL jsou automaticky posunuté nad Z=0, **nejsou automaticky optimálně
   natočené pro slicer**. U servo držáku podpěry podle orientace.
4. Spojit `base_1..3` dvěma `base_splice`, `tray_1..3` dvěma `tray_splice`.
   Použít M3 šrouby, matice a podložky. Krátké spoje cca M3×12, skutečnou
   délku ověřit podle matic, podložek a dostupného místa. Nepočítat se
   závitem přímo ve vrstveném plastu. Vruty do truhly navrhnout podle tloušťky dřeva.
5. Osadit **dvě hladké kovové 4mm tyče délky 120 mm**, spodní lůžka,
   posuvná pouzdra a horní krytky. Tyče vzájemně rovnoběžně; pomalý ruční
   posuv musí jít bez zadírání. Krytky přilepit/zajistit až po rozebrání
   a kontrole, nenahrazují servisně odnímatelný pevný doraz.
6. Namontovat dva `lower_rest_pillar`; při spodní poloze nese vložku rám.
   Dva `wand_saddle` jsou na koncích hůlky, vlepit po zkoušce. Boční výřez
   sedel nechává průchod RGB pásku a silikonového krytu. Hůlku zajistit
   dvěma pružnými pásky nebo čirými smyčkami;
   nesmí při zdvihu vypadnout. Univerzální sedla nejsou kopie nepravidelné rukojeti.
7. U059 upevnit do cage pásky. Klec přišroubovat přes čtyři montážní body
   pod vložkou; výška 21 mm vyžaduje dlouhé šrouby přibližně M3×30,
   nepřešroubovat do motoru. Osadit jeden RGB úsek na vnitřní bočnici.
8. Servo uchytit pásky v jeho cradle. **Použít původní servo páku**,
   nikoli tištěné drážkování. Adaptér má pracovní otvory M2,2 ve vzdálenosti
   6 a 12 mm; před tiskem přizpůsobit páce. Váleček na M3 šroubu/hladkém
   osazení nechat volně otáčet, ne sevřený maticí. Drážka 8,6 mm počítá
   s válečkem 8 mm. Šroub musí bezpečně zachytit váleček v ose X.
9. S odpojeným napájením ověřit zdvih, krajní vůle, všechny šrouby a kabely.
   Teprve po doplnění pasivní podpory a bezpečnostní elektrické větve
   vyzkoušet samostatné nezatížené servo, pak jeden zatížený modul.
   Neočekávat, že 9g servo zvládne libovolnou hmotnost celé přihrádky.

Při radiusu kliky 20 mm vyžaduje 100g zvedaná hmotnost ideálně až cca
**0,20 kg·cm** momentu; tření, rázy, páka a rezerva jej zvyšují. Hmotnost
výtisku + hůlky + U059 + pásku + kabelů **změřit** a porovnat s reálným
momentem serva při použitém napětí. Tento odhad nenahrazuje zátěžovou zkoušku.

Pro šest modulů: 6 serv, 6 U059, 12 vodicích tyčí, 12 podpěr, 12 sedel,
6 klik, 6 followerů, 6 válečků, 18 základnových a 18 vložkových segmentů.
Úplný tiskový kusovník je CSV; běžné šrouby, matice, podložky, pásky,
stahovací kabelové smyčky a M4 distanční sloupky jsou další materiál,
ne tištěné díly. Volitelný router má v CSV počet 0.

## Regenerace a ověření

Python 3.11+ a prostředí s CadQuery:

```sh
python -m venv .venv
# Linux/macOS: .venv/bin/python; Windows: .venv\Scripts\python.exe
.venv/bin/python -m pip install -r requirements.txt
.venv/bin/python model.py
.venv/bin/python verify_exports.py
```

`parametry.json` dovoluje změnit délku/šířku vložky, průměr hůlky, rozteč,
tyče, zdvih, krabičky a pracovní obálky součástek. **Rozvržení zadních
sloupců a způsob držení serva jsou konkrétní pro tento prototyp**:
výrazně jiné rozměry vyžadují změnu `preview.py`/držáku, nejen JSON.
Generátor odmítá známé nepřípustné kombinace. Není univerzální automatický
návrh jakékoliv truhly a jakéhokoliv serva.

Ověření: validita BREP a jediný solid každého dílu, kladný objem, STL/STEP
export, obálka tiskárny, obálka sestavy v pracovním prostoru kufru a průniky
pohyblivé vložky a kliky s pevnými tištěnými díly a vodicími tyčemi v
**11 polohách**. Skutečné exportované STL navíc procházejí samostatnou
kontrolou uzavřených hran, kladného orientovaného objemu a shody objemu
s CAD do 2 % při použité triangulaci. Pohyblivé tištěné díly a pracovní
obálky RGB/U059 se kontrolují také vzájemně.
Kontrola nezahrnuje pevnost, nepřetržitou dráhu mezi vzorky, všechny vzájemné
průniky, tovární díly, šrouby, kabely, anténu, teplotu ani pasivní stabilitu.
Náhledový posuv neotáčí servo/kliku; slouží jen k zobrazení pohybu vložky.

Napájení, watchdog, PHYSICAL_ENABLE, zákaz souběhu motoru a serva a reakce
na Stop se řídí existujícím [implementačním plánem](../docs/implementacni-plan.md).
Krabička `safety_box` bezpečnostní obvod nenahrazuje. Hotový CAD ani export
STL neznamená dokončenou hardwarovou etapu E5/E6.
