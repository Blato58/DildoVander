# Montáž a fyzické ověření

Tento návod patří k nové sestavě v `cad/`, nikoli k původnímu velkému kufru.
Přesné názvy souborů, počty, obálky a účel každé tisknutelné položky jsou
v [aktuálním kusovníku](../exports/print-bom.csv). Kusovník vzniká ze skutečných
instancí sestavy; pouze dvě zkušební měrky se přidávají jako příslušenství.

## Tisk

Výchozí materiál PETG, tryska 0,4 mm, vrstva 0,2 mm, čtyři obvody, alespoň
pět plných horních/dolních vrstev, výplň přibližně 40 %. Jsou to počáteční
parametry, nikoli deklarace pevnosti. Šroubové klouby a dlouhé nohy se zkouší
na vytištěném kusu. V teplém uzavřeném kufru je třeba sledovat tečení plastu.

STL již mají nejnižší bod na Z=0. Táhlo, páka a podložky jsou otočené s osou
čepu kolmo na podložku. Lůžka a základny se tisknou dnem dolů. Pod vodorovnými
otvory závěsu a okem táhla použít pouze nezbytné lokální podpěry. U vysokých
servisních dílů posoudit oporu/bridging a brim ve sliceru; generátor není slicer.
Rozměr tiskárny nejprve upravit v `parameters.json`; 220 × 220 × 250 mm není
potvrzeným modelem uživatelovy tiskárny.

| Tisknutelná skupina | Účel a montáž |
| --- | --- |
| `base_1`, `base_2`, `base_splice`, `base_floor_pad` | Dělená základna každého modulu, spojená M3 a uchycená k příčným lištám |
| `tray_1`, `tray_2`, `tray_splice` | Dělené pohyblivé lůžko; spoj přes zapuštěné hlavy pod LED kanálem |
| `hinge_support`, `hinge_spacer` | Zadní M4 čep a seřiditelné tření; není ověřený bezpečnostní zámek |
| `lower_rest` | Pevné dosednutí sklopeného lůžka |
| `servo_strap_cradle` | Podepření skutečné obálky serva, jištění páskem; otevřená strana pro výstup a páku |
| `crank_horn_adapter`, `servo_link`, `link_spacer_3mm` | Páka, táhlo a odstupy M3 kloubů; otvorovou rozteč původní servopáky nutno přizpůsobit |
| `wand_saddle` | Dvě sedla na rekvizitu, průchozí otvory pro pružné jištění, LED kanál |
| `vibration_cage` | U059 pod lůžkem, přístupný konektor a páskové jištění |
| `frame_rail_*`, `frame_splice_*` | Segmentované příčné podlahové lišty a spojky mimo nohy modulů |
| `service_floor_*` | Dělené boční podlahy, horní montážní rovina Z=8, odlehčení výztuhy pod nimi |
| `harness_clip` | Příchytky kabelů k přední liště, nikoli k pohyblivým částem |
| `*_carrier`, servisní platforma/rack a panely | Podpěry hran, páskové otvory, spojení na vyjímatelnou podlahu; konkrétní účel je v kusovníku |
| `camera_saddle` | Provizorní šikmý držák, dopřesnit podle doručeného modulu |
| `fit_coupon`, `head_fit_gauge` | Zkouška otvorů M3/M4 a prostoru ozdobné hlavice před tiskem celé sady |

## Spojovací materiál

Šrouby, matice, podložky, měkké izolační podložení a uvolnitelné pásky nejsou
v potvrzeném nákupním inventáři doložené. Jsou nutným montážním materiálem
tohoto návrhu, nikoli tvrzením o uskutečněném nákupu.

- M3 průchozí šrouby, matice a podložky pro základny, příčné lišty, nosiče a rack.
  Délku volit podle skutečné sestavy a přístupné strany matice; kabely nesmějí
  ležet na vyčnívajícím závitu. Není vhodné zašroubovat M3 do 3mm nevyztužené stěny.
- Nosiče servisní elektroniky používají bočně vložené běžné M3 matice,
  tloušťka nejvýše 2,4 mm. Šrouby vložit **odspodu před vložením rámu do kufru**.
  Hlavy jsou zápustné Ø6 mm a musí lícovat se skutečnou spodní plochou, zejména
  nad dřevěnou výztuhou. Délky ke špičce jsou pro každou polohu uvedené v
  `assemblies/validation.json` → `frame.service_floor_fasteners`.
  Jde o geometrické cílové délky, nemusí odpovídat prodávané délkové řadě:
  vhodný šroub zkrátit/odjehlit při zkoušce nebo upravit spoj. Závit nesmí
  vyčnívat nad matici do PCB; CAD nepředstírá, že libovolný M3×14 vždy pasuje.
- Čtyři M3 zápustné šrouby na spoj lůžka, celkem 24 pro šest lůžek; hlava
  nejvýše Ø6 mm musí být zarovnaná s podlahou před nalepením LED.
- Dva M3×14 čepy táhla na modul, celkem 12; běžná matice tloušťky nejvýše
  2,4 mm podle modelované obálky. Distanční díly zachovají boční vůli. Neutáhnout
  pohyblivé oko napevno. Přístup k zajištění závitu ověřit na prvním modulu.
- Jeden M4 kloub na modul, celkem 6, s podložkami a pojistnou maticí.
  Délka závitu podle skutečného průchozího souvrství; model ukazuje 36mm obálku čepu.
- U059 a servo: uvolnitelné izolační pásky. Hůlka: dvě pružné smyčky na modul.
  Powerbanka/PCB: tenké uvolnitelné izolační pásky, jištění nesmí tlačit na SMD
  ani blokovat port či odtrhnout součástku. Šířku ověřit v otvorech nosiče.
  U nejmenších nosičů použít tenké **2,5mm** pásky; širší 5–10mm pásek se
  nevejde do všech tunelů. Přesná přípustná šířka patří k metadatům nosiče.

## Pořadí sestavení

1. Zkontrolovat vnitřní usazení, skutečnou výztuhu a překážky pantů. Nejprve
   vytisknout měrky, jednu dvojici základny/lůžka a jedno sedlo. Ověřit hůlku,
   servo, skutečnou páku a kabely. Model neukládá nákup dalších senzorů.
2. Sestavit segmenty přední/zadní příčné lišty. Přidat boční podlahové díly,
   spojit je s lištami přes jejich konstrukční otvory. Zkusmo vložit do kufru
   a zase vyjmout na stůl; další montáž vyžaduje přístup odspodu. Podpěry se
   vyhýbají 5mm výztuze; bez souhlasu nevrtat ani neřezat dřevo.
3. Mimo kufr osadit spodní PCB nosiče, předem založit boční matice a odspodu
   vložit zápustné šrouby správné délky. Teprve potom upevnit desky. Pájené spoje
   zaizolovat, PCB držet přes měkké podložení za bezpečné hrany. Změřit výstup
   měniče na 5 V před připojením ESP32. Výkon 100 W na powerbance není výkon
   ani proudová zatížitelnost celého 5V rozvodu.
4. Sestavit jednu mechaniku: základna, servo, spodní opěra, zadní čep,
   spojené lůžko, originální servopáka s adaptérem, táhlo. Ověřit ručně celý
   rozsah s odpojeným pohonem. Rozměr 35/20 mm je délka servopáky, nikoli
   potvrzená rozteč jejích malých otvorů. Adaptér přizpůsobit skutečnému kusu.
5. Osadit U059, LED úsek 200 mm a dvě sedla. LED pás vede mimo táhlo,
   rekvizitu zajistit pružnými smyčkami. Zajistit vodiče na pevné i pohyblivé
   straně; u kloubu ponechat smyčku. Ověřit, že konektor U059 jde vyjmout.
6. Po úspěšné zkoušce opakovat pro zbývajících pět modulů. Připevnit moduly
   na příčné lišty. Každý lze demontovat samostatně; servis serva vyžaduje
   odložení vlastní rekvizity a uvolnění vlastního lůžka/táhla.
7. Osadit horní pravý rack, zesilovače a reproduktory. Pro spodní konektory
   je rack odnímatelný; nezalévat šrouby a nefixovat přes ně trvalý svazek.
   Powerbanku vložit na hranu vlevo a zajistit uvolnitelnými pásky.
8. ESP32 a kameru propojit originálním FPC až podle skutečné varianty,
   orientace kontaktů a délky. Návrhová smyčka není potvrzení kompatibility
   neznámého FPC. Kameru nesmí zakrývat stěna, pásek ani hůlka; vyzkoušet
   náhled při skutečné výšce stolu. Mikrofonní otvor ponechat volný.
9. Osazený rám vložit do kufru a zkontrolovat, že žádná hlava šroubu netlačí
   do dřeva ani výztuhy. Před každým zavřením ručně/povelem ověřit sklopenou polohu všech lůžek,
   uklidit volné kabely a zavírat pomalu. Pro přenášení ověřit fixaci celého
   rámu měkkým vymezením a jištěním; samotná gravitační opora není důkaz,
   že sestava drží při převrácení kufru. Elektronika ani powerbanka nesmí volně vypadnout.

## Vedení kabelů a konektory

V sestavě jsou vyznačené rezervy konektorů a servisní přístupy, nikoli
detailní digitální kopie celého pružného svazku. Příčnou trasu lze vést
před mechanikou mezi předním lemem a opěrami, případně za zadními čepy;
nesmí procházet smykem kliky a táhla. Použít příchytky rámu a jištění obou
stran pohyblivé smyčky. Nevést žádný kabel do prázdného víka.

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

## Přejímka prototypu

| Zkouška | Kritérium a záznam |
| --- | --- |
| Usazení | Rám se nekývá o výztuhu, stěny se neprohýbají, žádný tlak na kování; zaznamenat skutečné vůle |
| Ruční pohyb | Celý rozsah bez drhnutí, táhla a kabely nekolidují, šrouby dosažitelné |
| Zatížení a ztráta napájení | Zvážit každou pohyblivou sestavu; ověřit držení v dolní, střední i horní poloze bez proudu, včetně tečení PETG |
| Servo | Individuální pulzy a zdvih, bez opírání do tvrdého dorazu nebo zbytečného trvalého momentu; měřit proud a teplotu |
| Powerbanka | Odpojit a vyjmout vzhůru bez demontáže okolních dílů; žádný kabel ani pásek nesmí zůstat zachycený |
| Servis | Vyměnit jedno servo, ESP a spodní PCB; pro rack ověřit pořadí šroubů/konektorů |
| Víko | Ručně pomalu zavřít se sklopenými hůlkami a kompletní kabeláží; nic nepřiskřípnout |
| Transport | Odpojené napájení, zajištěné rekvizity i celý rám; opatrný náklon všech směrů, teprve po ověření manipulace |
| Kamera a zvuk | Skutečný záběr a poloha návštěvníka; poslech obou reproduktorů, akustický port a ozvěna mikrofonu |
| Vytrvalost | Až po předchozích kontrolách 100 cyklů na přihrádku podle E6, sledovat povolené šrouby, otěr a teploty |

Pokud zatížené lůžko při výpadku napájení klesá nebo vyžaduje tření přesahující
možnosti serva, není mechanismus akceptovaný. Změnit mechanické zajištění;
nepovažovat samotnou převodovku serva ani dotaženou PETG matici za důkaz bezpečnosti.
