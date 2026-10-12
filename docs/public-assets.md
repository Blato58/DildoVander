# Licence, assety a zdroje

Stav inventáře: 12. 10. 2026, aktuální sledované soubory. Jde o evidenci původu,
nikoli právní potvrzení všech distribučních práv. Licence vlastního kódu,
textů a CAD nebyla vybrána a kořenový `LICENSE` neexistuje. Vlastník musí před
publikací rozhodnout rozsah licence a ověřit oprávnění všech přispěvatelů.
Zveřejnění samo nenahrazuje licenci; GitHub rozlišuje veřejný repozitář a
[licencovaný open source](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository).

| Soubory / kategorie | Doložený původ a podmínky | Otevřený krok |
| --- | --- | --- |
| `kufr/design-system/fonts/{cinzel,inter,ibmplexmono}.ttf` | [Fontový inventář](../kufr/design-system/fonts/README.md) uvádí Google Fonts commit `bd8f81ddb5c74d5c8897b36ad88b440266245103`, původní názvy a tři uložená oznámení SIL OFL 1.1. | Zachovat copyright a OFL text v každém balíčku obsahujícím fonty; zkrácený název souboru není licence nového fontu. |
| 12 SVG v `kufr/design-system/icons/` | Lucide 0.548.0, commit `e560b8bf3a4f962b4ce2a357366d91271add06a9`; [inventář](../kufr/design-system/icons/README.md), [URL a SHA-256](../kufr/design-system/icons/provenance.json), [ISC a Feather MIT notice](../kufr/design-system/icons/LICENSE.txt). | Kopírovat oznámení s ikonami i při vložení inline; projektová licence je nepřepisuje. |
| `kufr/docs/pin-map-esp32-s3.png` | [Technický přepis](../kufr/docs/pin-mapa-esp32-s3.md) uvádí obrázek dodaný pro projekt a produktovou stránku desky. Autor/redistribuční licence obrázku nejsou doloženy. | Vlastník ověří autora a svolení k publikaci, nebo připraví vlastní dokumentovaný diagram. Odkaz na obchod nedává právo kopírovat obrázek. |
| `photo-1.png`, `photo-3.png`, `photo-5.png`, `photo-6.png` v `kufr/mechanika/final/reference/` | Měření/skutečný kufr, fotografie dodané pro projekt; [evidence.json](../kufr/mechanika/final/reference/evidence.json) uvádí otisky, nikoli autora/licenci. `photo-6.png` obsahuje i část těla, `photo-5.png` domácí okolí. | Potvrdit autorství, právo k publikaci a vhodnost těchto osobních detailů; případnou změnu podkladu provést s novou evidencí a regenerací. Otisk není právo k redistribuci. |
| `photo-2.png`, `photo-4.png` ve stejné složce | Vizuálně produktový obrázek zdobené hůlky s rozměrovým nápisem a katalogový kufr na bílém pozadí. Skutečný dodavatel, autor ani licence nejsou doloženy; vzhled sám autorství neurčuje. | Dohledat zdroj a svolení, nebo nahradit vlastními podklady se zachováním technických důkazů a regenerací. Nelze je automaticky považovat za vlastní fotografie majitele. |
| 4 PNG v `kufr/mobilni-aplikace/ios/App/App/Assets.xcassets/` | AppIcon a tři Splash varianty; ikona vizuálně odpovídá značce Capacitor. Zdrojový commit a oprávnění pro konkrétní obrázky nejsou v repozitáři evidovány. | Dohledat původ a podmínky šablony/značky nebo nahradit vlastními assety. Vizuální podobnost není ověřená provenance. |
| CAD, viewer a výrobní exporty | [F2](../kufr/mechanika/final/README.md) je parametrický projekt; STL/STEP/ZIP a náhledy se generují skutečným postupem. Aktuální git index neobsahuje STL, STEP ani audio nahrávky. | Vlastník určí licenci zdrojů i generovaných modelů a posoudí případné odvozené prvky. Každý distribuovaný ZIP/web/IPA/firmware potřebuje vlastní inventář. |
| AI persony a katalog | Texty a názvy v `kufr/ai-eval/` používají svět Harryho Pottera a Ollivandera; další replika vzniká při běhu poskytovatele. | Posoudit veřejné použití postav/názvů, výstupů a marketingu; neslibovat oprávnění k cizím značkám nebo dílům. |

OFL dovoluje bundling za svých podmínek, včetně přiložení licence a copyrightu;
upravené fonty podléhají pravidlům reserved font names. Viz
[oficiální SIL OFL](https://openfontlicense.org/open-font-license-official-text/).
Podmínky ikon potvrzuje [Lucide licence](https://lucide.dev/license).
Výrobní balíček a veřejný web nejsou automaticky pokryty tím, že jsou licence
uloženy ve zdrojovém repozitáři: ověř obsah výsledného artefaktu.

Mobilní `npm run build` kopíruje původní tři OFL texty, oznámení ikon a licence
Capacitor core/iOS do `dist/third-party-notices/` (přes ignorovanou generovanou
složku `public/third-party-notices/`). Stejný obsah přebírá web i Capacitor sync.
Samostatný Workshop CSS obsahuje OFL oznámení přímo. Tato kontrola nenahrazuje
inventář dalších knihoven nebo práv k PNG; projektovou licenci neurčuje.

## Závislosti nejsou projektovou licencí

Připnuté verze a graf uvádějí npm lockfiles, NuGet `packages.lock.json`,
Python `requirements.txt`, PlatformIO konfigurace a Swift package resolution.
Při distribuci sestaveného programu zahrň i přechodné a nativní závislosti,
zejména SQLite, ESP-IDF runtime a CAD OCP/OCCT; licence přímého balíčku sama
nedokazuje podmínky celého artefaktu. Python requirements nejsou úplný hash
lock přechodných knihoven. Tento inventář netvrdí úplné licenční schválení
npm/NuGet/Python/firmware/iOS závislostí.

## Fanouškovský kontext

Projekt není přidružený k držitelům práv Harryho Pottera ani jimi schválený.
[Oficiální právní oznámení](https://www.harrypotter.com/legals) uvádí příslušná
práva Warner Bros. a J. K. Rowlingové. Disclaimer, nekomerční záměr, zakoupení
rekvizity ani připsání autora nejsou samy svolením k redistribuci chráněných
assetů či použití značky. Vlastník musí před zveřejněním vyřešit rozsah
použití a nevyřešené položky výše. Dokumentace neprohlašuje automatickou
výjimku pro parodii ani právní bezrizikovost.
