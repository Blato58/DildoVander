# Doručený kufr – rozměry a zástavba

Aktuální měření majitele z **12. 10. 2026**, doplněné dvěma fotografiemi
bočního profilu a následným upřesněním v konverzaci, nahrazuje půdorys
odvozený z palcových hodnot z 9. 10. 2026.

| Veličina | Potvrzený údaj | Význam v CAD |
| --- | --- | --- |
| Vnitřní obdélník | **430 × 269 mm** | Volný prostor mezi výztuhami u horního okraje, nikoli celý půdorys hlavních stěn |
| Tloušťka stěny | **5 mm** | Boční dřevěné stěny |
| Přední a zadní výztuha | **20 mm** široká včetně stěny | Přidává 15 mm dovnitř od hlavní stěny |
| Přední výztuha | **40 mm** vysoká | Dolů od horního okraje spodní poloviny |
| Zadní výztuha | **30 mm** vysoká | Dolů od horního okraje spodní poloviny |
| Zaoblení spodního bočního profilu | **45 mm vodorovně a 45 mm svisle** | CAD používá čtvrtkružnici R45 mezi rovnou stěnou a dnem; přesný průběh ověřit přiložením šablony |
| Výztuha dna | **80 mm** široká, **5 mm** vysoká | Uprostřed; běží přes celou délku 430 mm |
| Výška spodní části od dna po horní okraj | **114,3 mm** | Dřívější potvrzení 4,5 palce z 9. 10.; nové fotografie nejsou přesnějším měřením výšky |

Souřadnice XY začínají v předním levém rohu **volného obdélníku**.
Hlavní přední a zadní stěna mají vnitřní plochy Y−15 a Y284 mm;
rovné dno mezi oblouky začíná na Y30 a končí na Y239 mm. Středová
výztuha leží na Y94,5–174,5 mm. Horní výztuhy sahají do prostoru
Y−15–0 a Y269–284, se spodními hranami Z74,3 a Z84,3 mm.

Z těchto údajů vychází vnější půdorys přibližně **440 × 309 mm**.
Je to odvozený rozměr, nikoli nové měření vnějšího kování. Údaj výrobce
450 × 310 × 200 mm zůstává historickou obálkou včetně možného kování;
neurčuje tloušťku modelovaných stěn. Tloušťka dna 5 mm, rozdělení víka
a jeho zaoblení a přesná osa pantů zůstávají parametrickými předpoklady.

## Přijaté uspořádání

- Kufr se při prezentaci používá **otevřený**.
- Mechanika a elektronika patří do **spodní části**. Víko zůstává prázdné.
- Plášť ani víko se netisknou a nevrtají; modelují se jako reference.
- Rám 400 × 240 mm má návrhovou polohu XY15,9/13,35 a spodní plochu Z8.
  Jeho kvádrová obálka není důkazem průchodnosti skutečným zakřiveným dnem.
- Tištěné kotvy kopírují zaoblení dna a vynechávají širší středovou výztuhu.
  Jejich epoxidový film má 0,5 mm ve svislém směru. Suchá montáž musí
  ověřit skutečný oblouk a místo kontaktu před lepením a finálním tiskem.

## Historické údaje a hranice ověření

Palcové hodnoty 17 × 10,5 palce (431,8 × 266,7 mm) jsou nahrazené
aktuálním měřením volného obdélníku. Dřívější potvrzení kvádru
400 × 240 × 170 mm z 9. 10. není potvrzením plošného kontaktu lepených
kotev s obloukem ani nově doplněných výztuh. Zástavbu je nutné kontrolovat
proti skutečným BREP tělesům pláště a výztuh, vedle návrhové obálky.

Před finálním tiskem ověřit šablonou průběh oblouku, tloušťku dna,
uložení všech čtyř kotev, průchod rámu horním obdélníkem, panty a zavírání.
CAD a fotografie nenahrazují fyzickou montáž ani pevnostní přejímku.

Aktuální [návrh F2](final/README.md) a [montáž](final/MONTÁŽ.md) používají
tuto rozměrovou revizi. Architekturu a akceptaci určuje
[implementační plán](../docs/implementacni-plan.md), postup vede
[stav implementace](../docs/stav-implementace.md). Další rozměry jsou ve
[společném přehledu součástek](rozmery-nakoupenych-soucastek.md) a
[přehledu hůlek](rozmery-hulek.md).
