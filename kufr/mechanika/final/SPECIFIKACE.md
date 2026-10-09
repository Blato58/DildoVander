# F2 — vložka do skutečného dřevěného kufru

Vlastník návrhu: hlavní agent. Závazné číselné parametry jsou pouze v
`parameters.json`; identifikátory komponent přebírají CAD, kabeláž a viewer.
Datum převzetí 9. 10. 2026. F1 nový tištěný plášť je tímto nahrazen.

## Souřadnice a důkazy

mm, počátek na vnitřním dně vpředu vlevo, X doprava, Y k pantům, Z vzhůru.
Majitel potvrdil vnitřní spodní prostor 431,8 × 266,7 × 114,3, výrobce
vnější 450 × 310 × 200 a majitel dále bezpečný vložitelný kvádr
400 × 240 × 170 s rezervou. Jeho XY polohu centrujeme; polohu skutečného
kvádru v nepravidelném kufru ověřit suchým vložením. Konstrukce se neopírá
o víko. Model pláště má celistvé stěny a prázdné víko. Rozdělení tlouštěk,
osy pantů a XY příčné výztuhy nejsou změřené. Výztuha má potvrzenou výšku
5; rám je nad její nejvyšší plochou. Plášť se nikdy neexportuje k tisku.

## Vybraný konstrukční princip

Šest modulů na rozteči 48, základna 46 × 212, lůžko 44 × 212.
Celý blok má 288 mm včetně LED obálek. V bezpečné šířce zbývá 112 mm
pro dvě boční servisní oblasti. Pět modulů by uvolnilo 48 mm; není třeba
zahazovat šestou pozici, pokud níže uvedené geometrické kontroly projdou.
ESP a kamera jsou vzadu uprostřed; mikrofon vpředu uprostřed; reproduktory
po stranách míří nahoru, protože skutečný plášť se neřeže.

| Varianta při stejné rozteči | Šířka bloku s LED | Zbývá z bezpečných 400 mm | Stav |
| --- | --- | --- | --- |
| Šest pozic | 288 mm | 112 mm celkem po stranách | Vybraná a kompletně modelovaná varianta |
| Pět pozic | 240 mm | 160 mm celkem po stranách | Rezerva při překročení skutečné obálky; samostatná sestava se nevyrábí |

Mezi základnami je 2 mm, mezi lůžky 4 mm; vedení má vůli 0,45 mm
na stranu. Vnitřní šířka lůžka mezi bočnicemi je 40 mm, takže odhadnutá
hlavice široká 38 mm má jen 1 mm na každé straně. Podélně je před hůlkou
za předním dorazem 3 mm a vzadu 6 mm. Vynechání šesté pozice tyto vůle
samo nezvětší; širší hůlka by vyžadovala přepracování rozteče a lůžka.
Kolize vybrané varianty řeší kontrola celé geometrie a kabeláže; nezměřené
pouzdro nelze prohlásit za přijaté jen podle těchto návrhových mezer.

Rám tvoří dvě hliníkové příčné lišty L20×20×3 EN AW-6060 T66, délka400,
a čtyřdílná PETG deska s přeplátovanými šroubovanými spoji a
integrovanými kotvami jednotlivých modulů. Každý modul má vlastní čtyři
M3 šrouby a nezávislé matice. Konce svislých ramen lišt se uříznou
pro boční osazení; jejich 3mm vodorovná ramena pokračují až ke kotvám. Sejmutí kteréhokoli modulu neuvolní další.
Rám připojují čtyři M4 šrouby ke čtyřem běžným hliníkovým úhelníkům
40 × 40 × 3, délka 50. Úhelníky se lepí svislou plochou 50 × 40 na
připravené holé dřevo bočních stěn. Vrtají se pouze nové úhelníky a tisk.
Lepení je trvalá lokální změna povrchu: lak v ploše spoje odstranit;
nespoléhat na přilnavost laku. Typ lepidla a přejímka jsou v montážním
návodu. Bez zkoušky soudržnosti dřeva a lepeného spoje nejde o přijaté
přepravní upevnění. Samotný výpočet není certifikace.

## Zdvih a pasivní stavy

Porovnáno: lankový zdvih je jednoduchý, ale má vůli a nezachytí návrat;
nůžky přidávají mnoho čepů a boční vůle; zachycená vačka používá již
existující kompaktní převod s vedením ve dvou bodech a řízeným návratem.
Vybrána vačka se zdvihem 16, úhlem 0–160 a kruhovými krajními úseky.
Ponechat její ověřitelnou kinematickou funkci; přidat pevné dolní podpěry
tak, aby lůžko dosedlo v rámci 0,15mm radiální vůle kladky. Kalibrace
nesmí tlačit proti podpěrám. Na dolním úseku není potřebný moment serva.
Přepravní zajištění tvoří šest 8mm pásků skrz základny a prázdná lůžka.
Pásky zachycují převrácení nezávisle na převodech; před provozem se vyjmou.
Bez snímače jejich stav nepotvrzuje elektronika. Výpadek uprostřed pohybu
znamená zastavení výstupů a ruční kontrolu, nikoli slíbený návrat dolů.

Dole firmware vypne impulzy konkrétního kanálu. PCA FULL_OFF není odpojení
V+ a nedokazuje uvolnění konkrétního serva. Nutný test proudu a reakce při
vypnutých impulzech; zelená kolébka je potvrzené odpojení celé větve pohonů.
Pokud servo dál drží, automatické splnění požadavku zůstává blokované.

## Montážní rozhraní a vzhled

Nakoupené desky mají skutečné obálky z inventáře; jejich neznámé otvory
nenahrazujeme vymyšlenou roztečí. Tištěné kapsy mají pevné okraje a dva
pásky na díl, přišroubované k rámu či servisnímu nosiči. Kamera má výměnnou
drážkovanou montáž. Kabelové příchytky mají vlastní kotvy v rámu/nosiči.
Powerbanka je samostatný odnímatelný obsah s dvěma uvolnitelnými pásky,
porty dopředu a volnou cestou nahoru. Nenese žádný jiný díl.
Kondenzátory místo pásků zachycují dvě pružná ramena s horním přesahem;
vývodové průchody zůstávají volné. Všechny kryty mají mechanické spoje,
bankový kryt přístupné shora i s vloženou bankou. Vedení používá běžné
M4×100 DIN931 s hladkým dříkem, dolní hlavou a horní podložkou/maticí.

Samet 0,8 mm je snímatelný a nenosný. Pohyblivá lůžka mají měkké podložení,
boční ochranu a otevřenou oblast pro prsty; hrany u pohybu jsou založené.
LED 6 × 200 mm / 36 pixelů zůstávají u jednotlivých lůžek, míří šikmo
nahoru k hůlce a mají černé pasivní stínění. Žádné další zdroje světla.
Zásoba elektroniky patří mimo aktivní sestavu; inventář zůstává zachovaný.

## Společná data a dodávka

Zachovat identifikátory `base_1..6`, `tray_1..6`, `cam_1..6`, `servo_1..6`,
`led_1..6`, `u059_1..6`, `esp32`, `camera`, `microphone`, `powerbank`.
PCA serva 0–5, vibrace 6–11, 6 LED na skupinu. Pinout se nemění.
Každý vodič má vlastní ID, konkrétní terminály, síť, svazek a trasu;
odhad umístění skutečného pinu musí být uveden. Rezervace USB v
`connector_reservations` jsou společné pro CAD i kabeláž a musí se ověřit skutečnou vidlicí. Trasy využívají parametry
komponent; viewer nesmí kreslit náhradní křivky mezi středy.

Viewer: vrstvy, strom, skrýt/izolovat/přiblížit, standardní pohledy,
zobrazovací průhlednost, všech šest zdvihů, detail pin/vodič/svazek/síť,
montáž vpřed/zpět/restart a transport bez banky/hůlek/textilu. Odpojený
USB kabel má parkovací příchytku v levé zóně; při transportu není připojen
k nepřítomné bance. Stav se při změně režimu odvozuje deterministicky.

Montáž: příprava a měrka → čtyři kotvy a vytvrzení → rám mimo kufr →
nosiče a mechanismy na stole → přišroubování rámu → pevné desky/reproduktory →
LED/U059 → kabely a parkování → mechanická/polaritní kontrola →
odjištěný omezený test → textil → kontrola zavření. Každý krok nese díly,
počty materiálu, nástroje, kameru, kontrolu a předchůdce.

Ověření: všechny tištěné díly jediné uzavřené těleso, STL/STEP roundtrip,
tisková obálka, průběh každého mechanismu se sousedy, víko, vyjmutí banky,
montážní spoje, dráhy a napájecí větve. Parametrické a fyzicky neověřené
vstupy brání označení připraveno k výrobě. Samostatně uvést výpočet momentu,
nosných spojů a praktickou zkoušku přepravy; nic z toho nepředstírat renderem.
