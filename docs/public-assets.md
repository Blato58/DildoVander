# Licence, assety a zdroje

Stav inventáře: 12. 10. 2026, aktuální sledované soubory. Jde o evidenci původu,
nikoli právní potvrzení všech distribučních práv. Majitel 12. 10. 2026 zvolil
AGPLv3: vlastní zdrojový kód, dokumentace a CAD zdroje projektu používají
[AGPL-3.0-only](../LICENSE). Materiály třetích stran se tím nepřelicencují;
neověřené obrázky nelze považovat za pokryté projektovou licencí. Kopie
obrázků bez doložených redistribučních práv byly ze zdrojů odstraněny.
Oprávnění přispěvatelů a práva ke zbývajícím materiálům je nutné posuzovat
samostatně.
Zveřejnění samo nenahrazuje licenci; GitHub rozlišuje veřejný repozitář a
[licencovaný open source](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository).

| Soubory / kategorie | Doložený původ a podmínky | Otevřený krok |
| --- | --- | --- |
| `design-system/fonts/{cinzel,inter,ibmplexmono}.ttf` | [Fontový inventář](../design-system/fonts/README.md) uvádí Google Fonts commit `bd8f81ddb5c74d5c8897b36ad88b440266245103`, původní názvy a tři uložená oznámení SIL OFL 1.1. | Zachovat copyright a OFL text v každém balíčku obsahujícím fonty; zkrácený název souboru není licence nového fontu. |
| 12 SVG v `design-system/icons/` | Lucide 0.548.0, commit `e560b8bf3a4f962b4ce2a357366d91271add06a9`; [inventář](../design-system/icons/README.md), [URL a SHA-256](../design-system/icons/provenance.json), [ISC a Feather MIT notice](../design-system/icons/LICENSE.txt). | Kopírovat oznámení s ikonami i při vložení inline; projektová licence je nepřepisuje. |
| Dodavatelská pinová mapa | Kopie PNG odstraněna. [Technický záznam](pin-mapa-esp32-s3.md) zachovává historický přepis popisků, omezení návrhu a rozpor USB. | Ověřit schéma a skutečné vedení konkrétní desky; textový přepis nepředstavuje měření. |
| Šest referenčních fotografií F2 | Kopie PNG odstraněny. [Textová evidence](../mechanika/final/reference/evidence.json) uchovává rozměry potvrzené majitelem, jejich původ a nezměřené položky. | Při doplnění vlastních podkladů doložit původ a potřebná oprávnění; generované CAD podmínky nenahrazují fyzickou akceptaci. |
| Nativní iOS AppIcon a launch screen | Čtyři PNG šablony odstraněny. [Vlastní geometrický generátor](../mobilni-aplikace/scripts/generate-native-assets.mjs), AGPL-3.0-only, kreslí kufr se šesti přihrádkami z Arcane Workshop tokenů; launch storyboard používá jednobarevné pozadí. Bez stažených obrazových či fontových podkladů. | PNG ikona vzniká jako ignorovaný výstup před nativním buildem; 1024 × 1024, RGB bez alfa kanálu. Nativní kompilace a podpis vyžadují samostatné ověření v existujícím macOS workflow. |
| CAD, viewer a výrobní exporty | [F2](../mechanika/final/README.md) je parametrický projekt; vlastní zdroje používají AGPL-3.0-only. STL/STEP/ZIP a náhledy se generují skutečným postupem. Aktuální git index neobsahuje STL, STEP ani audio nahrávky. | U generovaných modelů posoudit odvozené prvky a distribuční podmínky; samotná licence generátoru není automatickou licencí všech jeho výstupů. Každý distribuovaný ZIP/web/IPA/firmware potřebuje vlastní inventář. |
| AI persony a katalog | Aktuální produktové role Stínový čaroděj a Mistr hůlek mají původní popisy a prompty; katalog obsahuje vlastní party rekvizity. Starší názvy souborů a interní klíče zůstávají kvůli kompatibilitě uložených nastavení a zařízení. Další replika vzniká při běhu poskytovatele. | Před distribucí posoudit konkrétní výstupy a marketing; tato náhrada neprohlašuje právní bezrizikovost ani oprávnění k cizím dílům. |

OFL dovoluje bundling za svých podmínek, včetně přiložení licence a copyrightu;
upravené fonty podléhají pravidlům reserved font names. Viz
[oficiální SIL OFL](https://openfontlicense.org/open-font-license-official-text/).
Podmínky ikon potvrzuje [Lucide licence](https://lucide.dev/license).
Výrobní balíček a veřejný web nejsou automaticky pokryty tím, že jsou licence
uloženy ve zdrojovém repozitáři: ověř obsah výsledného artefaktu.

Mobilní `npm run build` kopíruje projektovou AGPL, původní tři OFL texty,
oznámení ikon a licence Capacitor core/iOS do `dist/third-party-notices/`
(přes ignorovanou generovanou
složku `public/third-party-notices/`). Stejný obsah přebírá web i Capacitor sync.
Samostatný Workshop CSS obsahuje OFL oznámení přímo. Tato kontrola nenahrazuje
inventář dalších knihoven; licenční oznámení závislostí
nenahrazují projektovou AGPL.

## Závislosti nejsou projektovou licencí

Připnuté verze a graf uvádějí npm lockfiles, NuGet `packages.lock.json`,
Python `requirements.txt`, PlatformIO konfigurace a Swift package resolution.
Při distribuci sestaveného programu zahrň i přechodné a nativní závislosti,
zejména SQLite, ESP-IDF runtime a CAD OCP/OCCT; licence přímého balíčku sama
nedokazuje podmínky celého artefaktu. Python requirements nejsou úplný hash
lock přechodných knihoven. Tento inventář netvrdí úplné licenční schválení
npm/NuGet/Python/firmware/iOS závislostí.

## Historický kontext

Starší plány a reporty zachycují někdejší fanouškovské reference; současné
produktové role je nahrazují původními popisy. Historie není zdrojem současné
identity produktu. Projekt není přidružený k držitelům práv Harryho Pottera ani jimi schválený.
[Oficiální právní oznámení](https://www.harrypotter.com/legals) uvádí příslušná
práva Warner Bros. a J. K. Rowlingové. Disclaimer, nekomerční záměr, zakoupení
rekvizity ani připsání autora nejsou samy svolením k redistribuci chráněných
assetů či použití značky. Vlastník musí před zveřejněním vyřešit rozsah
použití a nevyřešené položky výše. Dokumentace neprohlašuje automatickou
výjimku pro parodii ani právní bezrizikovost.
