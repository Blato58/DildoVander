# Aktualizace pripravy vysledku

Tato verze pouziva trvale aktualizace uvnitr aplikace. Nativni push a registrace
APNs nejsou soucasti teto verze; aplikace nezada systemove povoleni ani neslibuje
doruceni na zamcene obrazovce. Vysledek lze znovu nacist po prihlaseni.

Uctove aktualizace informuji o priprave vlastni pamatky a portretu. Ziskani
vysledku zustava dostupne bez povoleni systemovych oznameni. Implementace
neprihlasuje zarizeni k APNs, neuklada push tokeny a netvrdi doruceni na telefon.

## Zdroj a trvalost

Zaznam v `dv10_completion_updates` v existujici databazi archivu vznikne ve
stejne SQLite transakci jako prislusna zmena ulohy nebo publikace. Ma trvale
UUID, rostouci poradi, druh vysledku, jeho stav a cas. Opakovany stejny stav
stejne aktualni ulohy nevytvari dalsi logicke oznameni. Zmena lease nebo restart
tak neoznaci probihajici praci za hotovou.

Pripravenost se doklada publikovanym vystupem aktualni ulohy. Drivejsi hotova
edice nesmi oznamit dokonceni nove generace. Soukroma historie opozdene nebo
zrusene ulohy neni hotovy vysledek. Dokonceny pribeh bez ilustrace zustava
castecnym vysledkem; nejasny provider vysledek ceka na overeni a nema automaticky
opakovat placenou generaci.

GET pozadavky nevytvareji udalosti ani generaci. Inicializace neoznamuje stare
historicke vysledky jako nove dokoncene. Obnova skutecne rozpracovanych uloh po
restartu uklada jejich stav a aktualizaci atomicky.

Zaznamy neobsahuji jmeno postavy, text pribehu, prepis rozhovoru, fotografii,
provider obsah ani prihlasovaci udaje. Obsahuji pouze technicke identifikatory
a stav vlastniho vysledku. Nezavadeji automaticke promazavani puvodniho obsahu.
Vymazani pamatky odstrani jeji aktualizace ve stejne transakci jako tombstone;
vymazani fotografii/portretu odstrani jen portretove aktualizace. Zruseni souhlasu
s dalsi generaci samo nemaze jiz prijaty portret. Trvaly blok uctu odstrani jeho
aktualizace a zabrani jejich obnoveni opozdenou ulohou. Data jinych uctu zustavaji.

## Uctove rozhrani

Vsechny cesty vyzaduji stavajici zivou uctovou relaci. Zaznamy jsou dostupne
pouze puvodnimu vlastnikovi podle nemenne vazby a aktualniho access epochu.
Propojena pamatka jineho vlastnika neopravnuje ke cteni jeho aktualizaci.
Detail vysledku pri otevreni znovu overuje opravneni a smazani.

| Pozadavek | Vysledek |
| --- | --- |
| `GET /app/v1/customer/completion-updates?limit=25` | Stranka posledni aktualizace pro kazdou vlastni pamatku a druh vysledku. |
| `GET /app/v1/customer/completion-updates?limit=25&cursor=...` | Starsi stranka stejneho prehledu. Cursor je nepruhledny a vazany na ucet i velikost stranky. |
| `PUT /app/v1/customer/completion-updates/{id}/read` | Telo pouze `{"schemaVersion":1}`; `204` po trvalem oznaceni aktualizace za prectenou. |

Odpoved stranky obsahuje `schemaVersion: 1`, `items` a `nextCursor` (`null` na
konci). Polozka obsahuje `id`, `sequence`, `kind`, `state`, `reason`,
`resultLocator`, `canRetry`, `hasStory`, `hasArtwork`, `occurredAt` a `readAt`.
`kind` je `keepsake` nebo `portrait`; casy pouzivaji ISO 8601. `readAt: null`
znamena neprecteno. Prvni potvrzeny cas cteni je sdileny mezi zarizenimi tehoz
uctu a opakovane potvrzeni jej neposouva.

Stavy jsou `pending`, `processing`, `partial`, `ready`, `failed` a `unavailable`.
Duvody jsou `awaiting_capacity`, `paused_preparing`, `reconciliation_required`,
`generation_failed`, `result_unavailable` nebo `null`. Rozhrani aktualne
neposkytuje primou akci retry (`canRetry: false`); cteni ji nesmi nahrazovat.

Pozadavky na zapis pouzivaji stavajici origin/CSRF ochranu, na webu hlavicku
`X-DV-CSRF`. Chybne telo nebo cursor vraci `400`, prilis velke telo `413`,
odvolana relace `401`. Cizi, smazana, zneplatnena nebo novejsi aktualizaci
nahrazena udalost vraci pri potvrzeni `404`; stare ID nesmi oznacit novejsi
vysledek za precteny. Soukrome odpovedi maji `Cache-Control: no-store`.

## Ohraniceni a integrace

- Vychozi velikost stranky je 25, povoleny rozsah 1 az 50; cursor ma nejvyse
  2048 znaku. Jedna serverova stranka proveri nejvyse 200 kandidatu.
- Poradi pouziva trvale sequence a horni mez prvni stranky. Nove aktualizace se
  objevi po obnoveni; aktualne zneplatneny stary stav se nevraci jen proto, ze
  patril do drivejsi stranky. Prazdna stranka muze mit dalsi cursor.
- Komponenta `CustomerCompletionUpdates` ukazuje nejvyse 200 polozek a ma prop
  `locale` (`cs` nebo `en`). Nevyzaduje upravu sdileneho routeru ani systemove
  povoleni oznameni. Hostitelska obrazovka rozhoduje o jejim umisteni.
- Automaticke cteni probiha pouze pro viditelny online ucet s rozpracovanym
  vysledkem, nejvyse 12 kontrol v jednom cyklu. Rucni obnoveni zustava dostupne.
  Potvrzeni precteni je vzdy samostatna vedoma akce.
- Pri zmene uctu, odhlaseni, smazani uctu nebo odpojeni se pozadavky zrusi a
  stare odpovedi nesmi znovu naplnit obrazovku.

Aplikacni vlastnik vola `MapCustomerCompletionUpdates` s nakonfigurovanym
archivnim ulozistem. Pomocnik nevystavi cesty bez povolene uctove identity nebo
registrovaneho `KeepsakeStore`. Existujici inicializace archivu patri do startu
aplikace. Pro novou samostatnou databazi ani background generator neni duvod.

Zamerene overeni: `dotnet run --project backend-tests/BackendTests.csproj --
--customer-completion-updates`. Sada pokryva trvale projekce, transakcni chyby,
skutecne HTTPS prihlaseni/CSRF a zneplatneni. Frontendove specifikace a izolovane
prohlizecove overeni dokladaji komponentu; podepsany nativni klient, APNs a skutecne
zarizeni vyzaduji samostatne dukazy.
