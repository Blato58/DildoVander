# Mechanika kouzelného kufru

Aktivní návrh je [F2 — vložka do skutečného dřevěného kufru](final/README.md).
Plášť zůstává původní, bez nových otvorů. Šest zdvihů a elektronika jsou
připevněné k vlastnímu rámu; powerbanka, hůlky a textil jsou odnímatelné.

- [Návrhová specifikace](final/SPECIFIKACE.md)
- [Montáž, materiál a přejímka](final/MONTÁŽ.md)
- [Potvrzené rozměry kufru](rozmery-doruceneho-kufru.md)
- [Potvrzená délka a šířka hůlek](rozmery-hulek.md)
- [Inventář elektroniky](rozmery-nakoupenych-soucastek.md)
- [Kabeláž](final/KABELAZ.md)
- [Stav a hranice ověření](final/STAV.md)

STL, STEP, obrázky, viewer a balíček se reprodukovatelně generují příkazem
`final/Regenerate.ps1`. Jediný aktivní výstup je `final/exports/` s revizíF2.
Staré F1 skořepiny nejsou díly tohoto návrhu. Před celým tiskem vytisknout
měrku a jeden modul. Fyzická přepravní akceptace zůstává otevřená.

Veřejný [3D Workshop](https://2.28.202.220/workshop/) a celý aktuální ZIP
publikuje automaticky workflow `kufr-site-deploy.yml` po změně příslušných
zdrojů v `main`. Nasazení regeneruje a kontroluje všechny zdroje a exporty;
postup a rollback popisuje [veřejný web](../web/README.md). Vzhled vieweru
určuje společný [Arcane Workshop](../design-system/README.md).
