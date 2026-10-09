# Podklady, odhady a úplnost osazení F1

Datum návrhu: 9. 10. 2026. Všechny délky v mm. Uživatelské zadání má přednost
před historickými konstrukcemi. Byly přečteny rozměry, potvrzené zapojení,
napájení, kamera, mechanické části plánu, README a aktuální předání v záznamu stavu.
Převzaty byly fyzické údaje a funkční kontrakt; žádná tisknutelná geometrie.

## Fotografie

Originály šesti příloh jsou v `reference/`; soubor `reference/evidence.json`
obsahuje jejich SHA-256. Obrázky se neupravovaly.

| Pozorování | Co z něj vyplývá | Co neprokazuje |
| --- | --- | --- |
| Obrázek 2 uvádí „about 19~20cm“ | Výchozí délka rekvizity 200; 12 mm podélná rezerva lůžka | Nejde o fyzické přeměření doručené hůlky |
| Osa od okolí (235,722) k (554,103) v obrázku 800×800 | Viditelná délka asi 696 px; prsty zakrývají konec | Nelze z toho udělat přesnou metrologii |
| Největší šířka ozdoby kolmo na osu přibližně 110–120 px | Poměr přibližně 0,16–0,17, tedy asi 30–35 mm při délce 190–200 | Perspektiva, ostré ozdoby a zakrytí přidávají nejistotu |
| Dřík výrazně užší než ozdoba | Pro návrh konzervativní obálka Ø16 | Foto nenabízí samostatný boční pohled pro tloušťku |
| Pouze přední pohled na hlavici | Zvolená výšková rezerva 28 a šířka 38 | Výška 28 není měřená ani přímo odvozená |
| Obrázky 1 a 3 mají metr, ale neukazují oba konce měření | Souhlas s proporcemi dřevěného kufru | Není to nové přesné měření celé šířky/délky |
| Obrázky 4–6 ukazují dřevěné víko, kování a příčnou lať | Vzhledová reference; servis je shora | Tloušťka víka, únosnost kování ani rozměr pouzdra hůlky nejsou známé |

Potvrzených 431,8 × 266,7 × 114,3 pro původní kufr pochází z dokumentace
majitele. Nová skořepina má vlastní 4mm stěny/dno. Příčnou dřevěnou lať
nekopíruje: u zcela nového tištěného kufru by neměla konstrukční účel.
Vnitřní výška nové základny je 114,3 od horní plochy dna k okraji.

Parametrické rezervy: hlavice 38 × 55 × 28, dřík Ø16, délka 200;
samet 0,8, měkký podklad 1,2; boční vůle vodítek 0,45; drážka 6,8 pro
váleček Ø6. Tyto údaje se snadno upravují v JSON. Verifikátor odmítne
hůlku delší než lůžko minus 12 mm nebo širší než vyhrazená obálka.
Při změně mechanických pevných bodů je nutná odpovídající úprava CAD,
nikoli jen vypnutí neúspěšné kontroly. Výstupní STL se nesmí škálovat.

## Osazené a rezervní komponenty

Koordináty všech níže modelovaných součástí a jejich obálky jsou v exportním CSV.
Desky představují konzervativní prostorové obálky, nikoli vymyšlené montážní díry.
Konektory mají samostatné rezervy; nejsou zaměněny za rozměry samotných PCB.

| Inventář | Počet / umístění a fixace |
| --- | --- |
| ESP32-S3 57×28×13 | 1, pravý přední stolek, izolující suchý zip a záložní pásek; USB směrem dozadu |
| PCA9685 70×25×15 | 1 aktivní vpředu uprostřed; 1 odpojená rezerva napříč základnami 1–2, v izolovaném pouzdře |
| Mikroservo 22×11,5×27 | Všech 6 v tuhých lůžkách, dva uvolnitelné pásky na každém; proti pootočení působí stěny lůžka |
| U059 32×24×8 | Všech 6 pod pohyblivými lůžky, dorazy a pásky přes otvory lůžka |
| WS2812B 10mm pás | 6×200 mm = 36 LED, na vnější pravé svislé hraně lůžek; světlo do podélných mezer |
| Pixel Shifter 25,5×15×10,2 | 1 v přední servisní liště, suchý zip, izolované pájené spoje |
| ICS43434 16,51×12,7 | 1 uvnitř přední stěny, otvor zvuku ven; 5mm výška je rezerva |
| DFR0954 18×18 | Oba moduly na příslušných bočních stolcích; výšková rezerva 8 |
| Waveshare 100×45×21 | Oba reproduktory dole vpředu po stranách, ploché pěnové podložky a dva pásky; zvuk bočními štěrbinami |
| MCP23017 43×18×5 | 1 vpředu, na 6mm izolační podložce + 2mm suchém zipu kvůli maticím spoje dna |
| PD trigger 28×14×11 | 1 v pravé zadní dolní servisní zóně, vstup USB dopředu |
| Joy-IT 31×46×19 | 1 na pravém zadním stolku, 15 mm vzduchu nad komponentou, horní a boční nezakryté větrání |
| Bílé PTT 14×14×35 | 1 v levém předním krytu, závit v otvoru 12,4; dílčí otvor před tiskem ověřit měrkou |
| Červené kolébky 24×24×30 | 1 skrytá hlavní; 1 odpojená rezerva pod lůžkem 2 |
| Zelená kolébka 24×24×30 | 1 skrytá vedle červené, napájení pohonů dle závazného zapojení |
| Jistič 45×28×13 | 1 na levém stolku, pod bankovým krytem, tlačítko a svorky nesmí sevřít pásek |
| Kondenzátory Ø10×17 | 6 v zapojení: dva u audia vlevo, dva u výkonového rozvodu, dva u logiky/LED; 4 izolované rezervy pod lůžkem 1 |
| Degson 5pin | 3 oddělené pracovní bloky pod lůžky 3–5; dalších 7 odpojených na základnách pod lůžky. Obálka 35×22×16 je zatím rezerva |
| 2 univerzální PCB 50×70 | Pravá zadní zóna: jedna dole, druhá na spodní straně vyjímatelného stolku; pracovní výška 1,6 |
| Kamera | Vyhrazená obálka 24×24×12 před ESP, vyměnitelný slotted držák; doručený typ není známý |
| UGREEN PB720 | 139×80×26,5; orientace v modelu 26,5 X ×139 Y ×80 Z; dvě smyčky a mechanické dorazy |

Rezistory 330 Ω, 100 kΩ, 15 kΩ, 22 kΩ se montují na univerzální PCB nebo
izolovaně do kabeláže podle zapojení. Jsou to spotřební součástky, nikoli
32 dalších samostatných držáků. Pracovní počty: 1×330, 6×100k, 1×15k,
2×22k; ostatní zůstávají označenou zásobou. Kabely C–C 1 m, A–C 1 m,
Dupont 200, PH2 100, dvojlinky 0,5/1,5 mm² a bužírky tvoří svazky;
zbytek rolí, rezervní vodiče a multimetr patří do servisní výbavy mimo kufr.
Všech 32 řádků nákupního inventáře je tím přiřazeno k osazení, kabeláži,
spotřební zásobě nebo nástroji. Jejich obaly nejsou modelovány jako výrobek.

Vnější kovové panty, západky, textilní postroj a konkrétní šrouby jsou
montážní materiál návrhu, nikoli tvrzení o dalších potvrzených elektronických
nákupních položkách. Nic nebylo objednáno. Před montáží ověřit dostupný rozměr
kování proti drážkám; referenční kování není přesný model konkrétního výrobce.

## Elektrické a servisní hranice

Zůstává jedna ESP32, jedna aktivní PCA na 0x40, MCP na 0x20,
PCA 0–5 serva, 6–11 U059. GPIO 1/14 I2C, 48 LED, 2 OE, 21/41/42/47 audio;
19/20 USB, 38–40 SD a kamerové piny se neuvolňují pro jinou funkci.
Používá se jeden pohon celkem a vypínání impulzů po pohybu. Zelená
kolébka zůstává skutečným vypínačem výkonu, nikoli momentovým tlačítkem.

Napájení: PB → C–C → 12V PD → Joy-IT 5 V → jistič → červená → hvězda;
pohony za zelenou. Samostatné bloky +5 V, +5 V pohonů a GND se nikdy
nespojují do jedné společné Degson. Každý zesilovač má vlastní reproduktor;
SPK− není zem. Žádné propojení reproduktorových výstupů.

Délka/orientace FPC kamery ani úplný tvar USB vidlic nejsou známé.
Rezerva pro PB počítá s C1 orientovaným k přední horní části kapsy;
ověřit na fyzickém PB před nalepením dorazových pěn. ESP a kamera leží
těsně vedle sebe a držák dovoluje podložení/posun; případná jiná kamera
vyžaduje změnit jen její malý držák. Viditelný otvor objektivu není tlačítko.
