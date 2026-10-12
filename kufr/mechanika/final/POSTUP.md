# Postup sestavení F2

Generováno z `assembly-guide.json`; stejná data používá Workshop.
CAD a prostorové rezervy nejsou fyzickou přejímkou. Před stavbou přečíst
[montáž a přejímku](MONTÁŽ.md) a [potvrzenou kabeláž](KABELAZ.md).

## 1. Ověření podkladů a zkušební výtisk

Vytisknout měrku se 100% výplní a jeden zkušební modul. Před tiskem velkých kotev nasunout skutečné matice M3 i M4 bočními tunely měrky pod uzavřenou střechu a vyzkoušet šrouby. Ověřit také usazení standardních hlav šroubů v zahloubeních nad průchozími otvory M3/M4; potom matice vyjmout pro finální montáž. Ve stejném výtisku ověřit přemostění dutiny epoxidového zámku Ø7,5/Ø10,5 a obě horní Ø2,5 odvzdušnění. Malou dávkou epoxidu z rezervy předplnit komoru odspodu; obě horní ústí musí ukázat naplnění. Uvnitř nezavřít podpory. Zkouška plnění neprokazuje přilnutí ke dřevu. Změřit skutečné hůlky s pouzdrem, konektory a osu serva. Nasucho vložit obrys 400 × 240 mm přes volný horní obdélník 430 × 269 mm nad středovou výztuhu 80 × 5 mm. Šablonou ověřit oba oblouky 45 × 45 mm a usazení tvarovaných kotev; nelze použít staré kotvy pro ploché dno. Kotvy tisknout plochou horní stranou na podložce, zakřiveným spodkem vzhůru; STL má tuto orientaci již nastavenou. Lůžko tisknout otočené +90° kolem Y, integrální Ø6 čep směřuje vzhůru bez podpor na kluzné ploše. Nevyhovující vůli opravit tiskovým profilem nebo parametrem a znovu vytisknout; nikdy pilníkem, vrtákem ani změnou měřítka STL. Ověřit, že skutečné původní otvory páky přijmou M2 bez vrtání; jinak upravit tištěnou spojku nebo použít vhodnou standardní neupravenou páku před finálním tiskem.

**Místo:** Dno skutečného kufru; X doprava, Y k pantům, Z nahoru; Podle označených přístupových bodů.

**Předchůdci:** žádní.

**Již připravené kroky:** žádné.

**Díly:** 2 × díl tiskové měrky (čep, kapsy M3/M4, epoxidový zámek a drážka); 1 × půdorysná šablona.

**Materiál:**
- 3 × kg PETG — zásoba pro sestavu, podpory a zkušební tisky (`PETG`)
- 3 × Bison Epoxy Metal 24 ml (`EPOXY`)
- 1 × PET fólie 0,5 mm; čtyři dočasné podložky filmu, po vytvrzení vyjmout (`SHIM05`)

**Nástroje:** posuvné měřítko, pravítko, 3D tiskárna, rukavice, míchací miska a stěrka.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.

**Kontrola:** Zapsané rozměry odpovídají parametrům; čep a vedení mají funkční vůli; obě matice jdou bočně zasunout i vyjmout bez opracování a šrouby se ručně chytí do závitu i usadí hlavy do zahloubení; epoxidový zámek jde naplnit odspodu s průchodnými oběma odvzdušněními; víko se nedotýká obálky.

**Servis:** Zkušební výtisky a šablony odstranit; nejsou součástí nosné sestavy.

## 2. Rám sestavit mimo kufr

Na stole spojit čtyři PETG panely s integrovanými předními a zadními žebry. Osadit osm přeplátovaných spojů a jejich spodní matice. Všechny otvory jsou vytištěné; kovové šrouby a matice použít standardní, bez opracování.

**Místo:** Pracovní stůl mimo kufr; Na stole, přístup shora i zespodu.

**Předchůdci:** fit.

**Již připravené kroky:** fit.

**Díly:** 4 × rámový panel s integrovanými žebry; 8 × přeplátovaný spoj.

**Materiál:**
- 8 × M3 × 6 zápustný šroub (`M3x6`)
- 8 × M3 matice (`NUT3`)

**Nástroje:** imbus 2 mm, pinzeta.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.
- Použít podpěrky pod rám; zakryté spodní šrouby dokončit před vložením do kufru.
- V kufru zatím nesmějí být: tray_*, cam_*, servo_*.

**Kontrola:** Rám leží rovně; žádná hlava nebo matice se neopírá o 5mm výztuhu. Svěrné šrouby nepromáčknou PETG.

**Servis:** Odpojit napájení, sejmout horní skupiny, banku, oba reproduktory a pravou kazetu; povolit 24 M3 a 4 M4, vytáhnout celou vložku.

## 3. Nosiče a odnímatelné boční kazety na stole

Na stole připevnit spodními šrouby všechny pevné nosiče. Sedm matic odnímatelných bočních kazet vložit do spodních kapes rámu; kazety připevnit sedmi M3 × 8 shora. Zatím bez desek. Vnější rámy nenesou elektroniku; vlastní sestavená vložka zůstává nosná bez banky, hůlek a textilu.

**Místo:** Pracovní stůl mimo kufr; Na stole, přístup shora i zespodu.

**Předchůdci:** frame.

**Již připravené kroky:** fit, frame.

**Díly:** Sada pevných nosičů dle tiskového seznamu; každý 1 ×; držáky kondenzátorů 3–6 po 1 ×.

**Materiál:**
- 43 × M3 × 8 nízká půlkulatá hlava (`M3x8`)
- 4 × M3 × 8 zápustná hlava (`M3CSK8`)
- 1 × M3 × 6 zápustný šroub (`M3x6`)
- 48 × M3 matice (`NUT3`)

**Nástroje:** imbus, pinzeta, stranové kleště.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.
- Použít podpěrky pod rám; zakryté spodní šrouby dokončit před vložením do kufru.
- V kufru zatím nesmějí být: tray_*, cam_*, servo_*.

**Kontrola:** Každý nosič drží samostatně; šrouby nekolidují s PCB, kabely mají průchody.

**Servis:** Odpojit napájení, sejmout horní skupiny, banku, oba reproduktory a pravou kazetu; povolit 24 M3 a 4 M4, vytáhnout celou vložku.

## 4. Osadit elektroniku na otevřené vložce

Vše osadit na stole před vložením rámu a před nasazením zdvihových lůžek. Nejprve spodní deska pravé kazety a její vodiče, potom horní deska, nakonec Degson; levou kazetu osadit jističem a zesilovačem před měničem. Pásky provléknout před položením desky. ESP a kamera zůstávají vzadu uprostřed, mikrofon vpředu. Reproduktory osadit a svázat až po utažení sedmi kazetových šroubů; před pozdějším utažením čtyř M4 kotev je dočasně odložit.

**Místo:** Pracovní stůl mimo kufr; Na stole, přístup shora i zespodu.

**Předchůdci:** carriers.

**Již připravené kroky:** fit, frame, carriers.

**Díly:** 1 × ESP, kamera, mikrofon, PCA, MCP, Pixel, měnič, PD, jistič a PTT; 2 × zesilovač, reproduktor a univerzální PCB; 3 × svorka; 2 × kolébka; 6 × kondenzátor.

**Materiál:**
- 36 × pásek šířky 2,5 mm (`TIE25`)

**Nástroje:** stranové kleště, pinzeta.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.
- Použít podpěrky pod rám; zakryté spodní šrouby dokončit před vložením do kufru.
- V kufru zatím nesmějí být: tray_*, cam_*, servo_*.

**Konektory:**
- Zapojovat bez napájení; ID a piny jsou v KABELAZ.md.

**Kontrola:** Každá deska má pevný doraz a dva pásky; vodiče lze zapojit před zakrytím vyšší policí. Pod sestavou je volný přístup pro spodní šrouby. ESP, kamera, PCA a MCP nejsou zakryté lůžky.

**Servis:** Odpojit USB/FPC a signály, odstranit pásky desky; nosič lze nechat na rámu. Kamera/FPC vyžadují přejímku skutečného dílu.

## 5. Napájecí svazek na stole, banka odpojená

Na stole bez powerbanky připojit napájecí svazky podle KABELAZ.md. Na levé kazetě nejprve připojit jistič a zesilovač, potom měnič; na pravé kazetě popsat MAIN5, DRIVE5 a GND. Mezilehlé vodiče přichytit k rámu a ponechat odpojitelnou rezervu, žádná smyčka nesmí nést kazetu. Volné vývody k PCA, servům, LED a U059 zůstanou označené a odpojené do montáže zdvihů. USB závit drží vlastní kolébka. Zelená kolébka dál přerušuje jen DRIVE5; GPIO2 přímo ovládá PCA OE.

**Místo:** Pracovní stůl mimo kufr; Na stole, přístup shora i zespodu.

**Předchůdci:** electronics.

**Již připravené kroky:** fit, frame, carriers, electronics.

**Díly:** Napájecí a zemní vodiče: každý uvedený ID 1 ×; 1 × USB zásuvka; 1 × krátký USB kabel.

**Materiál:**
- 1 × sada označených vodičů dle řezného seznamu (`WIRE`)
- 1 × pasivní USB-A zásuvka 3 A (`USB_SOCKET`)
- 1 × USB-A/C 300 mm, nejméně 3 A (`USB300`)
- 184 × štítek s ID vodiče (`LABELS`)
- 2 × pásek USB cívky 3,6 × 250 mm (`COIL_TIE`)

**Nástroje:** multimetr, pájka, odizolovací kleště.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.
- Použít podpěrky pod rám; zakryté spodní šrouby dokončit před vložením do kufru.
- V kufru zatím nesmějí být: tray_*, cam_*, servo_*.

**Konektory:**
- Zapojovat bez napájení; ID a piny jsou v KABELAZ.md.

**Kontrola:** Měřením vyloučit zkrat, ověřit kolébky a spojitou GND. Žádné napětí před dokončením kontroly.

**Servis:** Odpojit banku a vývody, vyjmout banku a levý reproduktor a vyparkovat USB konektor; tři M3 shora, poté zvednout celou kazetu včetně jejího přišroubovaného USB klipu.

## 6. Signály, audio a servisní vývody na stole

Připojit všechny pevné signály a audio na stole; spodní PCB zapojit před osazením horní PCB. Zachovat potvrzené piny v KABELAZ.md. Servo, Grove U059 a jednotlivé LED vývody označit a připravit pro pozdější připojení. Pro každou kazetu nechat délku umožňující odpojení konektorů při zvednutí nejvýše o 20 mm; teprve po odpojení lze kazetu úplně vyjmout. Pohyblivé svazky nesmějí být již napnuté v klidu.

**Místo:** Pracovní stůl mimo kufr; Na stole, přístup shora i zespodu.

**Předchůdci:** power.

**Již připravené kroky:** fit, frame, carriers, electronics, power.

**Díly:** Signální vodiče: každý uvedený ID 1 ×; odpory podle potvrzeného zapojení.

**Materiál:**
- 1 × sada označených vodičů dle řezného seznamu (`WIRE`)
- 86 × štítek s ID vodiče (`LABELS`)

**Nástroje:** multimetr, pájka, štítky.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.
- Použít podpěrky pod rám; zakryté spodní šrouby dokončit před vložením do kufru.
- V kufru zatím nesmějí být: tray_*, cam_*, servo_*.

**Konektory:**
- Zapojovat bez napájení; ID a piny jsou v KABELAZ.md.

**Kontrola:** Všechny vodiče mají vlastní ID; kanály serv0–5, U0596–11 a 36LED odpovídají zapojení.

**Servis:** Po sejmutí pravého zadního krytu odpojit označené vývody, povolit čtyři M3 a zvednout kazetu.

## 7. Přilepit a přezkoušet čtyři podlahové kotvy

Na stole před lepením zasunout zboku všech 24 M3 a 4 M4 matic pod střechy čtyř tištěných kotev. Po přilepení už spodní oblouk omezuje přístup k některým vstupům matic. Nasucho ustavit kotvy na DNĚ podle sestaveného rámu; zakřivený spodek musí souhlasit s oběma oblouky a přední/zadní ramena vynechat pás Y94,5–174,5 mm středové výztuhy. Brusným papírem odstranit povrchovou úpravu pouze v jejich skutečných lepených stopách na pevné holé dřevo. Chránit kapsy matic a otvory před lepidlem. Použít Bison Epoxy Metal ze zásoby připravené v prvním kroku a smíchat 1:1; dodanou stěrkou PŘEDEM naplnit zámky Ø7,5/Ø10,5 z otevřené spodní strany kotvy, pak kotvu ustavit na čtyři dočasné měřené 0,5mm PET podložky u přístupných vnějších okrajů na ROVNÉ části dna (přední kotvy kolem Y60, zadní kolem Y200); nedávat rovnou podložku do oblouku. Podložky určují výšku filmu; po vytvrzení je vyjmout, nepřilepit je ani rám. Tloušťku kontrolovat měřítkem před zatížením. Horní otvory Ø2,5 slouží odvzdušnění a vizuální kontrole, nikoli tlakové injektáži pasty. Mokré přebytky otřít před vytvrzením. Rám nepřilepit. Fixovat 10 h, při 20 °C nechat 24 h vytvrdnout a přezkoušet spoj podle MONTÁŽ.md. Po vytvrzení zkontrolovat čisté závity již zachycených matic a vyjmout ochranu otvorů před vložením rámu. Připravit tři balení po 24 ml, včetně rezervy pro vzorek, přebytky a ztráty. Množství lepidla a počet zámků přebrat z aktuálního verification.json; číselný výsledek starší varianty není přejímkou nových kapes kazet.

**Místo:** Dno skutečného kufru; X doprava, Y k pantům, Z nahoru; Podle označených přístupových bodů.

**Předchůdci:** signals.

**Již připravené kroky:** fit, frame, carriers, electronics, power, signals.

**Díly:** 4 × tištěná podlahová kotva; spodní film a odlité zámky; 24 × M3 a 4 × M4 zachycená matice.

**Materiál:**
- 24 × M3 matice (`NUT3`)
- 4 × M4 matice (`NUT4`)

**Nástroje:** brusný papír pouze na dřevo v lepené stopě, svěrky, rukavice, míchací miska a stěrka, pinzeta, posuvné měřítko pro 0,5mm podložky.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.

**Kontrola:** Kotvy jsou na dně, nikoli stěnách. Matice M3 leží Z2,6–5 a M4 Z1,8–5 pod souvislou 3mm střechou; žádné zalepení kapsy a nejméně 2 mm plastu mezi epoxidem a kapsou. Zámky jsou vyplněné; nepokračovat při odlupování dřeva, praskání nebo nevyhovující zátěžové zkoušce. Zakřivené spodky sedí bez kývání; matice vložené před lepením zůstaly čisté a šrouby lze volně nasadit rukou.

**Servis:** Odpojit napájení, sejmout horní skupiny, banku, oba reproduktory a pravou kazetu; povolit 24 M3 a 4 M4, vytáhnout celou vložku.

## 8. Základny a vodicí šrouby před vložením

Na stole vložit dvanáct M4 × 100 zespodu rámu a položit šest holých základen. Ihned nasadit finální podložky a matice na horní konce vedení Z95/95,8; tím šrouby při přenosu nevypadnou. Matice přitáhnout rukou bez deformace PETG. Bez lůžek, vaček a serv. Rám se už neobrací; podepřít jej mimo elektroniku a vodiče. Dlouhé vodicí šrouby se po vložení do kufru zespodu nedostanou na místo.

**Místo:** Pracovní stůl mimo kufr; Na stole, přístup shora i zespodu.

**Předchůdci:** anchors.

**Již připravené kroky:** fit, frame, carriers, electronics, power, signals, anchors.

**Díly:** 6 × holá základna; 12 × M4 × 100 s hladkým dříkem; 12 × horní podložka a matice držící šrouby při přenosu.

**Materiál:**
- 12 × M4 × 100 vodicí šroub, hladký dřík (`GUIDE4`)
- 12 × M4 DIN934 matice vodítka, výška 3,2 mm (`GUIDE_NUT`)
- 12 × M4 podložka Ø9/4,3 × 0,8 mm (`GUIDE_WASHER`)

**Nástroje:** klíč 7 mm.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.
- Použít podpěrky pod rám; zakryté spodní šrouby dokončit před vložením do kufru.
- Lůžka, vačky a serva ještě mimo kufr; reproduktory a pravá kazeta odložené pro přístup ke kotvám.
- V kufru zatím nesmějí být: tray_*, cam_*, servo_*.

**Kontrola:** Dvanáct spodních hlav je v předepsaných odlehčeních a všech dvanáct horních podložek/matic zajišťuje dříky proti vypadnutí; základny odpovídají rozteči 48 mm. Všech 24 modulových otvorů je shora volných.

**Servis:** Odpojit vývody, sejmout matice a dva servo pásky; lůžko, vačku a servo vyjmout společně.

## 9. Vložit předzapojenou vložku a utáhnout základny

Powerbanka je mimo kufr. Reproduktory dočasně vyjmout z pásků; pravou kazetu uvolnit jejími čtyřmi horními M3, odpojit a odložit. Tím zůstane přímý přístup ke všem čtyřem M4 kotvám. S horními podložkami a maticemi stále nasazenými vložku spouštět svisle dvěma rukama za přední a zadní žebro, nikoli za elektroniku nebo vodicí šrouby. Při dosednutí zkontrolovat správné zapadnutí hlav a žádný přiskřípnutý vodič. Utáhnout 4 × M4 × 10 a všech 24 × M3 × 14 holých základen. Pravou kazetu i reproduktory vrátit a připojit. Lůžka a serva zatím nejsou v cestě šroubováku.

**Místo:** Dno skutečného kufru; X doprava, Y k pantům, Z nahoru; Svisle shora.

**Předchůdci:** bases.

**Již připravené kroky:** fit, frame, carriers, electronics, power, signals, anchors, bases.

**Díly:** 1 × rám s nosiči; 4 × M4 × 10 ISO4762.

**Materiál:**
- 4 × M4 × 10 ISO4762; hlava Ø7 × 4 mm; imbus 3 mm (`M4x10`)
- 24 × M3 × 14 ISO7380, standardní neupravený šroub modulu (`M3x14`)

**Nástroje:** imbus 3 mm, imbus 2 mm.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.
- Lůžka, vačky a serva ještě mimo kufr; reproduktory a pravá kazeta odložené pro přístup ke kotvám.
- Přední levá kotva: rovný imbus 3 mm projde otvorem v polici; červený spínač je posunutý na Y49 mm. Kryt zůstává sejmutý.
- V kufru zatím nesmějí být: tray_*, cam_*, servo_*.

**Kontrola:** Všechny čtyři kotvy a 24 modulových šroubů dotaženy bez deformace PETG; základny sedí na rámu. Všechny dříky jsou svislé, rám leží nad 5mm výztuhou. konektory obnovené.

**Servis:** Odpojit napájení, sejmout horní skupiny, banku, oba reproduktory a pravou kazetu; povolit 24 M3 a 4 M4, vytáhnout celou vložku.

## 10. Pohyblivé horní skupiny připravit na stole

Každé lůžko, vačku s pákou a servo sestavit společně na stole: integrální čep zachytit v drážce bez násilí. Nasadit U059 a LED, provléknout jejich pásky a připájet LED vodiče před vložením. Připravit chráněnou pohyblivou smyčku, zkontrolovat odlehčení a izolaci. Zatím skupinu neuzavírat maticemi vedení. Každou skupinu zkoušet na tiskové měrce a popsat 1–6; sestavené skupiny zůstávají mimo kufr.

**Místo:** Pracovní stůl mimo kufr; Na stole, přístup shora i zespodu.

**Předchůdci:** install.

**Již připravené kroky:** fit, frame, carriers, electronics, power, signals, anchors, bases, install.

**Díly:** 6 × U059; 6 × úsek LED po 200 mm; samostatné pohyblivé rezervy.

**Materiál:**
- 1 × tenká oboustranná páska pro LED (`LED_TAPE`)
- 24 × pásek šířky 2,5 mm (`TIE25`)
- 12 × M2 × 10 spoj vačky (`COUPLER2`)
- 6 × původní středový šroubek konkrétního serva (`SERVO_CENTRE`)

**Nástroje:** pájka, smršťovací bužírka, kleště.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.
- Použít podpěrky pod rám; zakryté spodní šrouby dokončit před vložením do kufru.

**Konektory:**
- Zapojovat bez napájení; ID a piny jsou v KABELAZ.md.

**Kontrola:** Čep zůstává zachycený ve vačce, servo i obě pohyblivé desky jsou dostupné. LED spoje jsou izolované. Skupina jde bez páčení nasadit na dvojici vodicích dříků.

**Servis:** Odpojit vývody, sejmout matice a dva servo pásky; lůžko, vačku a servo vyjmout společně.

## 11. Nasadit hotové horní skupiny a připojit vývody

Po dotažení holých základen sejmout shora obě dočasně nasazené podložky/matice daného modulu, dříky podepírají kotvy. Pak spouštět po jedné kompletní skupinu lůžka, vačky a serva svisle na oba vodicí dříky. Servo uložit do kolébky a připevnit dvěma pásky. Nasadit horní podložky a matice; matice nesmějí předepnout kluzné vedení. Připojit původní třípinový servo konektor, Grove U059 a připravené LED spoje. Připevnit smyčky mimo sousední vačky. Před další skupinou ověřit celý zdvih, dolní podpěry a možnost zpětného sejmutí.

**Místo:** Dno skutečného kufru; X doprava, Y k pantům, Z nahoru; Svisle shora.

**Předchůdci:** moving.

**Již připravené kroky:** fit, frame, carriers, electronics, power, signals, anchors, bases, install, moving.

**Díly:** 6 × základna, lůžko s integrálním čepem, vačka a servo; 12 × vodítko; 24 × M3 × 14.

**Materiál:**
- 12 × pásek šířky 2,5 mm (`TIE25`)

**Nástroje:** imbus, klíč 5,5 a 7 mm.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.

**Konektory:**
- Zapojovat bez napájení; ID a piny jsou v KABELAZ.md.

**Kontrola:** Každá skupina dosedá na podpěry, nevisí na servu. Zdvih 16 mm je bez zadrhávání, vodiče mají rezervu v obou krajích. K servisnímu sejmutí stačí odpojit vývody, sejmout horní matice a vyměnit dva pásky serva.

**Servis:** Odpojit napájení a vývody, vyjmout hůlku a vlastní měkkou vložku, sejmout horní podložky/matice a dva servo pásky; lůžko, vačku a servo vyjmout společně nahoru. Sousední moduly zůstávají.

## 12. Kontrola bez napájení

Provést úplný ruční pohyb, odpojení konektorů, sejmutí krytů a vyjmutí banky. Použít šest přepravních pásků a ověřit zajištění bez hůlek a textilu; před dalším krokem všechny pásky vyjmout.

**Místo:** Dno skutečného kufru; X doprava, Y k pantům, Z nahoru; Podle označených přístupových bodů.

**Předchůdci:** mechanisms.

**Již připravené kroky:** fit, frame, carriers, electronics, power, signals, anchors, bases, install, moving, mechanisms.

**Díly:** 6 × přepravní pásek pro zkušební zajištění, potom vyjmout.

**Materiál:**
- 6 × přepravní pásek 8 × 250 mm (`BED_WRAP`)

**Nástroje:** pravítko, multimetr.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.

**Kontrola:** Nic volně nevisí; žádná součást se neopírá o odnímatelný obsah. Zkouška spojů a přepravy je zaznamenaná.

**Servis:** Odpojit napájení, sejmout horní skupiny, banku, oba reproduktory a pravou kazetu; povolit 24 M3 a 4 M4, vytáhnout celou vložku.

## 13. Omezená zkouška a kalibrace

Odjistit všech šest přepravních pásků. Vložit banku do samostatného držáku, zajistit dvěma pásky a připojit USB. Mimo návštěvníky změřit měnič 5 V, poté jeden LED úsek, jeden motor a servo bez spojení s vačkou. Kalibrovat pulzy, připojit mechanismus a změřit proud i chování po FULL_OFF. Testovat hlavní, krátkou sekvenci a Stop. Firmware se tímto krokem sám nenahrává.

**Místo:** Dno skutečného kufru; X doprava, Y k pantům, Z nahoru; Svisle shora.

**Předchůdci:** coldtest.

**Již připravené kroky:** fit, frame, carriers, electronics, power, signals, anchors, bases, install, moving, mechanisms, coldtest.

**Díly:** 1 × powerbanka a 2 × její pásek; zkoušet postupně po jednom LED úseku, motoru a servu.

**Materiál:**
- 2 × uvolnitelný pásek banky (`BANK_WRAP`)

**Nástroje:** multimetr s měřením proudu, servisní ovladač.

**Přístup:**
- Připojit banku pouze pro omezenou zkoušku; před každou montážní úpravou odpojit napájení.

**Kontrola:** Nejvýše jeden motor, nikdy současně se servem. Dole nesmí servo aktivně držet. Neúspěch znamená blokaci provozu.

**Servis:** Banka nahoru po odpojení USB a dvou pásek; hůlky volně vyjmout, kryty M3 shora.

## 14. Měkké podložení a odnímatelný textil

Pod hůlky vložit 1,2mm měkké podložení a 0,8mm textil s pevně založenými kraji. Nezakrýt LED, mikrofon, reproduktory, chlazení ani štítky aretace.

**Místo:** Dno skutečného kufru; X doprava, Y k pantům, Z nahoru; Podle označených přístupových bodů.

**Předchůdci:** powered.

**Již připravené kroky:** fit, frame, carriers, electronics, power, signals, anchors, bases, install, moving, mechanisms, coldtest, powered.

**Díly:** 6 × měkká vložka lůžka; 1 × snímatelná výstelka víka.

**Materiál:**
- 1 × snímatelný samet 0,8 mm (`TEXTILE`)
- 1 × měkká vložka 1,2 mm (`FOAM`)

**Nástroje:** nůžky, jehla nebo šicí stroj.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.

**Kontrola:** Textil nelze zachytit vačkou nebo vedením; jeho sejmutí neuvolní žádný konstrukční díl.

**Servis:** Před servisem horního modulu vyjmout jeho volnou vložku i hůlku; textil nepřenáší konstrukční zatížení.

## 15. Přišroubovat servisní kryty

Namontovat čtyři kryty na jejich vlastní držáky. Bankový kryt zůstává snadno odnímatelný. Větrací a akustické otvory ponechat volné.

**Místo:** Dno skutečného kufru; X doprava, Y k pantům, Z nahoru; Podle označených přístupových bodů.

**Předchůdci:** soft.

**Již připravené kroky:** fit, frame, carriers, electronics, power, signals, anchors, bases, install, moving, mechanisms, coldtest, powered, soft.

**Díly:** 4 × servisní kryt; 8 × upevnění krytu.

**Materiál:**
- 8 × M3 × 10 nízká hlava (`M3x10`)
- 8 × M3 matice (`NUT3`)

**Nástroje:** imbus.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.

**Kontrola:** Kryty nevyztužují rám; přístup ke kolébkám a USB parkování zůstává možný.

**Servis:** Banka nahoru po odpojení USB a dvou pásek; hůlky volně vyjmout, kryty M3 shora.

## 16. Provozní sestava

Po úspěšné přejímce vložit hůlky. Ověřit zajištění banky, USB a všech šest ODJIŠTĚNÝCH přepravních pozic. Hůlka zůstává při prezentaci v lůžku.

**Místo:** Dno skutečného kufru; X doprava, Y k pantům, Z nahoru; Podle označených přístupových bodů.

**Předchůdci:** covers.

**Již připravené kroky:** fit, frame, carriers, electronics, power, signals, anchors, bases, install, moving, mechanisms, coldtest, powered, soft, covers.

**Díly:** Až 6 × hůlka; ostatní části již namontované.

**Materiál:**
- Použít již připravené díly.

**Nástroje:** ruční kontrola.

**Přístup:**
- Banka odpojená; žádná síla přes PCB, konektory nebo vodiče.

**Kontrola:** Před zavřením vše dole a napájení vypnuté. Běžná přeprava je podporována bez banky a hůlek, se šesti pásky.

**Servis:** Banka nahoru po odpojení USB a dvou pásek; hůlky volně vyjmout, kryty M3 shora.
