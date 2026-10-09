# Montáž jednoduché verze

Sada má 18 typů STL a 56 výtisků včetně měrky. [Kusovník](../exports/print-bom.csv)
obsahuje skutečné názvy, počty, rozměry, účel a tiskové parametry všech souborů.
Tištěné konstrukce jsou spojené objemové díly; zakoupený hardware má instalační obálky.

## Díly a tisk

PETG, vrstva 0,2 mm, tryska 0,4 mm, čtyři obvody, pět plných horních/spodních
vrstev, zpočátku 40 % výplně. Pevnost a tečení plastu ověřit na prototypu.
STL mají Z0 na podložce a doporučenou orientaci; přidat brim podle přilnavosti.

| STL / počet | Účel a orientace |
| --- | --- |
| `module_chassis` /6 | Základna, sedlo serva, doraz a zadní čelisti v jednom. Ploché dno, natočení 45°, přibližně 202,3 × 202,3 mm |
| `module_foot` /12 | Patky s vodicími klíči; ploše dnem na bed, vlepit do spodních kapes základny |
| `wand_tray` /6 | Lůžko, V sedla, U059 vodítka a opory transportního pásku. Ploché dno, natočení 45° |
| `crank_horn_adapter` /6 | Adaptér původní servopáky; plochou stranou na bed, integrovaný odstup vzhůru |
| `servo_link` /6 | Táhlo; 4mm tloušťka ve směru Z, čepy kolmo na bed |
| Čtyři `*_cassette` /po1 | Boční kazety s integrovanými nosiči; podlahou na bed |
| `cassette_foot` /8 | Patky kazet; klíče vzhůru |
| `switch_hood`, `ptt_hood` /po1 | Panely ovladačů; střechou dolů, stěny rostou vzhůru |
| `controller_bridge`, `speaker_bridge` /po1 | Odnímatelné horní podpěry; střechou dolů, bez dlouhého mostu při tisku |
| `esp32_carrier`, `speaker_right_carrier` /po1 | Samostatná lůžka na můstcích; plochým dnem dolů |
| `camera_wedge` /1 | Pevný 45° držák; na boční ploše X podle exportu |
| `fit_coupon` /1 | Měrka otvorů M3/M4; vytisknout první |

Dlouhé díly využívají úhlopříčku předpokládané 220mm podložky. Skutečnou plochu
nastavit v `parameters.json`. Krátké tunely a otvory do 4,5 mm zkontrolovat ve
sliceru a případně místně podepřít. Ověřený kontakt velké základny s bedem není
zárukou kvality konkrétní tiskárny. Jinou orientaci závěsů nejprve vyzkoušet.

## Montážní materiál

Pásky, šrouby, výstelka a lepidla jsou nutný doplňkový materiál; nákup není potvrzený.

- 12 šroubů M3×14 a běžných matic pro táhla. Neutáhnout pohyblivá oka napevno;
  kovové závity zajistit při zachování demontáže. Přidané podložky mění potřebnou délku.
- 6 šroubů M4×35, 12 kovových podložek tloušťky 1 mm a 6 běžných matic tloušťky
  3,2 mm. Model ukazuje tyto obálky. Pro samojistnou matici upravit délku šroubu.
- Původní servopáky a výrobcem dodané šrouby. Adaptér není tištěný spline.
  Středový otvor 3,5 a pomocné 2,2 mm jsou návrhové: přizpůsobit dodané páce.
- 20 podlahových dvojic samolepicího suchého zipu: 12 přibližně 42 × 12 mm
  pod mechanismy, 8 přibližně 42 × 10 mm pod kazety; stlačená výška modelu 2 mm.
- Lepidlo vhodné pro konkrétní PETG na klíčované patky, panely, ESP nosič a klín
  kamery. Lepí se tisk k tisku; dřevo drží suchý zip. Vytvrzení dle výrobce.
- Šest 10mm uvolnitelných transportních pásků, v modelu tloušťka 0,8 mm.
  Před pohybem serv sejmout. Zůstávají mimo ornamenty a nad LED na opěrných ploškách.
- Tenké uvolnitelné pásky pro servo, U059, desky a banku; do nejmenších tunelů
  pouze 2,5mm pásek tloušťky nejvýše 1,5 mm. Široký suchý zip se tam nevejde.
  Rozměry a trasy jsou v `assemblies/validation.json` → `service.retainers`.
- 6mm páska kolem řídicího můstku a podlahy kazety v Y145..151; 8mm páska kolem
  reproduktoru, jeho můstku a celé pravé zadní kazety v Y234..242. Délku podle
  skutečného obvodu. Modeluje se i jejich trasa, nikoli podrobná přezka.
- Měkká výstelka V sedel, dvě pružné uvolnitelné smyčky na dřík každé hůlky,
  samolepicí páskové příchytky pro pevný kabelový svazek. Netlačit na ozdobné výčnělky.

## Sestavení

1. Vytisknout měrku a jeden mechanismus. Ověřit skutečnou servopáku, obálku hůlky,
   hmotnost a tiskovou plochu. Fotografie dokládá délku 190–200 mm; průměry 16/40
   jsou pouze rezervy. Celou sadu tisknout až po zkoušce jedné přihrádky.
2. Patky nasucho zasunout do kapes, vyčistit tiskové zbytky a přilepit k podkladům.
   Po vytvrzení aplikovat suchý zip zespodu. Patky nesmějí stát na středové lati.
3. Servo vložit do sedla a zajistit dvěma tenkými pásky. Nasadit původní páku,
   adaptér a táhlo. CAD úhly −45..45 nejsou hotová PWM kalibrace skutečného serva.
4. Lůžko připojit M4 čepem se dvěma podložkami; M3 spoji připojit táhlo. Ručně
   projet celý rozsah. Dolní poloha dosedá na pevný doraz, servo do něj nesmí
   trvale tlačit. Boční přístup ke šroubům získáme vyjmutím jediného modulu.
5. U059 položit mezi vodítka na lůžku a zajistit tunely. Vlevo nalepit LED pásek,
   šest LED na přihrádku. V sedla měkce vypodložit podle dříku a osadit smyčky.
   Hůlka zůstává v lůžku; nevytvářet svěrný spoj přes kovový ornament.
6. Do kazet osadit desky a upevnit každou pásky. Zkontrolovat spodní vývody,
   izolaci a tlak na SMD. Panely vlepit do podlah, ověřit pojistné prvky tlačítek.
   Čtyři provizorní svorky jsou přístupné po vyjmutí powerbanky.
7. ESP nosič a kamerový klín vlepit na řídicí můstek, desky zůstávají vyjímatelné
   po uvolnění pásků. Můstek jistí 6mm obvodový pásek. Reproduktorový nosič sedí
   přímo na jeho můstku bez mezivrstvy; společný 8mm pásek drží celý komplet
   k zadní kazetě. Uvolněním můstků se zpřístupní spodní elektronika.
8. Rozmístit podle [souřadnic](../assemblies/placements.csv), ověřit vodorovnost
   a přilnavost suchého zipu na mořeném dřevě. Nevtlačovat díly násilím. Vyzkoušet
   samostatné vytažení banky a dostupnost každého serva.
9. Dokončit kabely, jistit konce pohyblivých smyček a vyzkoušet pomalý pohyb
   nejprve bez rekvizity, potom s ní. Kalibrovat každé servo individuálně.

## Kabely a elektrické návaznosti

Příčné koridory jsou před a za mechanismy. Svazek vést po pevných částech,
ke každému lůžku ponechat měkkou smyčku s oběma konci odlehčenými. Žádný vodič
nejde do víka. Model zachycuje rezervy, nikoli celý ohebný svazek.

PCA9685 má jednu aktivní desku, druhá zůstává rezervní. Serva 0–5 a U059
6–11 používají společný PCA; U059 se z PCA pouze řídí, nenapájí. I²C je
GPIO1/14, přímé OE GPIO2, LED data GPIO48 přes Pixel Shifter, audio
GPIO21/41/42/47. Kamerové SCCB GPIO4/5 a SD GPIO38–40 zůstávají oddělené
podle [napájení a efektů](../../../docs/efekty-a-napajeni.md).
MicroSD je na desce; samostatná čtečka ani náhradní SD karta se do CAD nepřidává.

Čtyři nakreslené pětipólové svorky Degson představují **provizorní prostorové
rozvržení**, nikoli ověřenou elektrickou kapacitu všech větví. Nakoupeno je
deset kusů; zbývajících šest není automaticky označeno za nepotřebnou rezervu.
Před zapojením rozpočítat společné póly, propoje, servo/PCA větev, šest U059,
LED a audio. Případné další bloky vyžadují doplnění CAD a nové ověření prostoru.
Nezvětšovat proudovou kapacitu pouhým paralelním spojováním neověřených cest.

Originální servo vodič 150 mm a U059 kabel 200 mm nemusí dosáhnout k pravému
PCA z levého modulu. Změřit trasu v sestavě a doplnit izolované propojení z
nakoupených vodičů; spojky nejsou součástí pohyblivé smyčky. Neprodlužovat
kamerový FPC náhodným kabelem. Přebytek 1m USB kabelu musí být svázán mimo
powerbankovou výsuvnou dráhu a pohony. Ověřit veškeré skutečné délky a ohyby;
CAD průniková kontrola je automaticky neakceptuje.

Rozvod a elektrická bezpečnost mají zdroj pravdy v projektu. Zelená kolébka
odpojuje napájení pohonů, GPIO2 řídí OE. Použít nejvýše jeden vibrační motor
a při pohybu serva motor vypnout. Pro první zkoušky LED přibližně 25 % jasu.
Tento návrh nepřidává polohová čidla ani nevydává časovaný pohyb za změřenou polohu.

## Přenášení bez hůlek a powerbanky

Vypnout napájení, vyjmout všech šest hůlek i powerbanku a sklopit lůžka na dorazy.
Každé prázdné lůžko a jeho základnu obejmout 10mm páskem na dvou ploškách u předního
konce. Pásek běží nad LED a brání poskočení lůžka bez přenosu rázu na servo.
Horní můstky, elektronika, reproduktory a kabely musí mít své retenční pásky.
Volný USB konec upevnit k pevné kazetě, zavřít víko. CAD této polohy je
`transport-open.step` / `transport-closed.step` a `renders/viewer-transport.html`.

Nosná cesta: součást → pásek/nosič → kazeta či základna → vlepená klíčovaná
patka → suchý zip → dno. Nic nesmí držet jen vlastní vahou. Transportní pásky
před zapnutím všech serv odstranit; konstrukce nemá snímač jejich přítomnosti.

## Fyzická přejímka

| Zkouška | Kritérium |
| --- | --- |
| Zip a lepení | Zkušební patka na skutečném povrchu po vytvrzení; tah kolmo i ve smyku. Nesmí se odtrhnout lak ani spoj PETG |
| Uchycení | Zvážit osazený modul bez hůlky/banky. Na stolním přípravku zatížit spoj trojnásobkem vlastní tíhy ve směru odtržení i smyku; je to návrhová zkouška, ne přepravní certifikace |
| Přenášení | Nad měkkou podložkou pozvolna naklonit vypnutý zavřený kufr na všechny boky a prohlédnout. Poté pět běžných přenesení za madlo: žádný posun, povolený pásek nebo odlepení. Nejde o pádový test |
| Pohyb | Celý rozsah bez drhnutí, i s finálním svazkem; tlak pouze na dřík, ne na ornament |
| Servo | Individuální kalibrace, měření proudu/teploty, bez trvalého tlaku do dorazu |
| Výpadek napájení | S hůlkou zkusit dolní, střední a horní polohu; nechtěný pokles znamená nutnou změnu tření nebo jištění |
| Zavření | Pomalu zavřít se sklopenými lůžky a kabeláží. Banka končí v Z111, návrhová rezerva pod hranicí spodní části je 3,3 mm |
| Servis | Vyjmout banku bez rozborky, vyměnit jedno servo a desky; ověřit skutečné konektory a délky |
| Kamera, zvuk, vytrvalost | Reálný záběr u stolu, poslech a ozvěna; potom 100 cyklů každé přihrádky dle E6 |

Fyzická přejímka dosud neproběhla. Únosnost zipu/lepidla, hmotnost hůlek, moment
serv, přesný FPC a konektory nejsou potvrditelné z fotografie ani z CAD. Při
selhání spoje upravit uchycení a zkoušku zopakovat; zelená geometrie není zkouška nosnosti.
