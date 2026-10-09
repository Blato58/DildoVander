# Mechanika kouzelného kufru

Jediný aktivní návrh je **[F1 v `final/`](final/README.md)**: nový tištěný
korpus a prázdné otevírací víko, šest servem ovládaných lůžek, skrytá
elektronika a čtyři odnímatelné servisní kryty. Návrh má 29 typů STL,
44 výtisků včetně měrky a je rozdělený pro podložku 220 × 220 mm.
Pro přenášení se vyjmou hůlky a powerbanka; ostatní osazení zůstává zajištěné.

- [Návrh, exporty a regenerace](final/README.md)
- [Montáž, tisk a fyzická přejímka](final/MONTÁŽ.md)
- [Původ rozměrů a předpoklady](final/PODKLADY.md)
- [Stav ověření](final/STAV.md)
- [Potvrzené rozměry zakoupených součástek](rozmery-nakoupenych-soucastek.md)
- [Měření původního dřevěného kufru](rozmery-doruceneho-kufru.md)
- [Závazné elektrické zapojení](../docs/zapojeni-potvrzeneho-hardwaru.md)

F1 používá potvrzený vnitřní prostor 431,8 × 266,7 × 114,3 mm jako rozměrový
podklad pro nový korpus. Není vložkou do původního dřevěného kufru.
Rozměrové podklady a fotografie zůstávají zachované. Předchozí generátory,
modely a jejich montážní návody byly nahrazeny; jejich zdroje lze dohledat
v historii Gitu. Pro výrobu používat pouze exporty z `final/exports/`.

Exporty se generují lokálně a nejsou verzované. Gitem se předává parametrický
zdroj, fotografie, dokumentace a příkaz `final/Regenerate.ps1`; ten vytvoří
STL, STEP, náhledy, kusovníky, ověřovací report a ZIP se shodnými zdroji.
Geometrické kontroly neprokazují fyzickou únosnost, moment serv ani zadržení
při výpadku napájení. Prvním výrobním krokem je měrka a jeden zatížený modul.
