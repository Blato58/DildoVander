# Export vlastnich dat

Export pouziva stavajici prihlaseni pomoci passkey a ochranu uctovych pozadavku
proti CSRF. Pristup ke stazeni patri konkretnimu uctu a prihlasene instalaci,
ktera export vyzadala. Identifikator ulohy sam pristup neposkytuje.

## Obsah verze 1

JSON dokument obsahuje `schemaVersion: 1`, cas pozadavku a seznam zahrnutych
a vynechanych kategorii primo v polich `included` a `excluded`.

| Zahrnuta kategorie | Pole |
| --- | --- |
| Ucet | Identifikator vlastniho uctu. |
| Historie postav a rozhovoru | Identifikatory vlastnich zakaznickych sezeni, postavy a jeji tehdejsi revize, zarizeni, pripadne rozhovoru; priznaky odvolaneho profilu a dostupnosti ulozeneho snimku. |
| Vlastni archiv | Identifikator pamatky puvodniho vlastnika, revize dokumentu a cas prirazeni. Sdilena nebo pozdeji propojena cizi pamatka vlastnictvi nezaklada. |
| Povolene osobni vzpominky | Identifikator a revize zaznamu, text, druh, bezpecny odkaz na mistni puvod, cas vzniku a vazba na vlastni postavu a sezeni. |

Verze 1 vynechava vstupy a obohaceni postavy, cele pribehy, fotografie a dalsi
media, surove prepisy, poskytovatelske logy a prompty. Vynechava take autentizacni
sezeni, prihlasovaci udaje, security stampy, vnitrni dukazy opravneni, jine hosty
a soukromy obsah dostupny pouze pres cizi sdileny archiv. Dostupnost snimku
v technicke historii neopravnuje ke cteni jeho obsahu. Jde o vymezeny export,
nikoli kopii vsech databazi a souboru aplikace.

Osobni pamet a identita postavy jsou samostatne oblasti. Oprava nebo zapomenuti
vzpominky nemeni automaticky postavu. Odvolany nebo smazany profil neposkytuje
pristup k jeho puvodnimu osobnimu obsahu pres zachovanou technickou historii.

## Ulohy a pristup ke stazeni

| Stav | Vyznam |
| --- | --- |
| `requested` | Pozadavek byl trvale prijat a ceka na zpracovani. |
| `preparing` | Server overuje opravneni a sestavuje ohraniceny dokument. |
| `ready` | Priprava uspela; stazeni jeste vyzaduje platne prihlaseni a shodne zdrojove revize. |
| `failed` | Priprava, platnost pristupu nebo revize zdroju uz neumoznuji stazeni; `reason` uvadi duvod. |

Pristup ke stazeni plati jednu hodinu od uspesne pripravy. Zmena opravneni,
odvolani instalace, security stampu nebo role, smazani uctu, zmena relevantni
historie, archivu ci osobni pameti muze pristup ukoncit drive. Stary export
nesmi vratit zapomenutou nebo opravenou vzpominku. Po zmene je potreba vedome
vyzadat novy export.

Server uklada pouze ohranicena metadata ulohy, revizni dukazy a SHA-256
pripraveneho dokumentu. Pripravene JSON bajty zahodi. Pri stazeni dokument znovu
sestavi a overi jeho obsah i opravneni. Nevznika trvala surova exportni kopie.
Expirace pristupu nemaze puvodni ulozeny obsah; jeho retence zustava rucni.

Na ucet pripada nejvyse jedna uloha. Novy pozadavek s novym klicem predchozi
ulohu nahradi a zrusi jeji stazeni. Opakovani stejneho pozadavku se stejnym
klicem a opravnenim vraci tutaz dosud aktualni ulohu. Prijate klice zustavaji
v ohranicenem trvalem registru i po nahrazeni nebo vymazani ulohy; opozdeny
pozadavek vraci `409 / request_superseded` a neobnovi starsi export. Zmena
opravneni vraci `409 / request_conflict`. Vymazani ulohy maze pouze metadata
konkretni ulohy, nikoli tyto minimalni zaznamy o prijatych pozadavcich.
Stavajici samostatna akce smazani uctu zustava jedinou cestou pro
smazani uctu a jeho trvaly uklid.

Po trvalem zablokovani smazaneho uctu odstrani tento existujici uklid exportni
ulohy i registr prijatych klicu. Minimalni bezpecnostni zaznam s identifikatorem
smazaneho uctu zabrani opozdenemu pozadavku znovu vytvorit jeho exportni metadata.
Obsah ani prihlasovaci udaje tento zaznam neobsahuje.

Pri preruseni procesu skonci drive zpracovavana uloha jako
`failed / preparation_interrupted`. Server ji bez noveho pozadavku znovu
negeneruje. Bezne duvody zahrnuji `source_changed`, `download_expired`,
`payload_too_large`, `inventory_unavailable` a `export_failed`.

## HTTP rozhrani

Vsechny cesty zacinaji `/app/v1/customer/data/exports` a vyzaduji stavajici
uctovou autentizaci. Mutace pouzivaji stavajici kontrolu originu a CSRF, na webu
hlavicku `X-DV-CSRF`. Nativni klient zachovava svou existujici ochranu instalace.

| Metoda a cesta | Odpoved |
| --- | --- |
| `POST /app/v1/customer/data/exports` | `202` a stav ulohy; telo pouze `{"schemaVersion":1}`, hlavicka `Idempotency-Key` obsahuje neprazdne UUID ve formatu D. `Location` ukazuje na stav. |
| `GET /app/v1/customer/data/exports/{id}` | `200` a aktualni stav, vcetne overeni revizi pripravene ulohy. |
| `GET /app/v1/customer/data/exports/{id}/download` | Overeny JSON dokument jako priloha, `Cache-Control: no-store`. |
| `DELETE /app/v1/customer/data/exports/{id}` | `204`; vymaze vlastni metadata ulohy. |

Stavova odpoved obsahuje `schemaVersion`, `id`, `state`, `reason`, `requestedAt`,
`readyAt`, `downloadExpiresAt` a `downloadPath`. Nepouzitelne casy, duvod a cesta
jsou `null`. Prihlasovaci udaje ani serverove revizni dukazy v odpovedi nejsou.
Stav lze kontrolovat ohranicenym GET pollingem; kontrola nezaklada novou ulohu.

Chybne telo nebo klic vraci `400`, neprihlaseny ci odvolany pristup `401`, cizi
nebo nezname ID `404`, nepripraveny ci zmeneny zdroj `409`, vyprsely pristup
`410`, prekroceny obsahovy limit `413`, naplnena kapacita uloh `429` a docasne
nedostupny nebo zamceny zdroj `503`. Chyby pouzivaji stavajici ProblemDetails.

## Limity a provozni integrace

- Nejvyse 4096 vlastnich historickych sezeni, 4096 vlastnich pamatek a 4096
  osobnich vzpominek; prekroceni skonci chybou, seznam se tise nezkracuje.
- Nejvyse 32 MiB vysledneho UTF-8 dokumentu. Klient musi povolit tento limit
  pouze pro overenou exportni cestu.
- Nejvyse 128 uloh v serverovem zurnalu; metadata se automaticky nepromazavaji.
  Uzivatel muze vlastni ulohu vymazat nebo nahradit.
- Registr prijatych klicu ma ve vychozim stavu 128 zaznamu na ucet a 4096
  celkem. Po naplneni vraci `429 / export_request_limit`; starsi klice se kvuli
  prijeti noveho pozadavku nevyhazuji. Konfigurace
  `AccountDataExport:MaxReceiptsPerAccount` dovoluje 1 az 4096 a
  `AccountDataExport:MaxReceipts` 1 az 65536, pricemz celkovy limit musi byt
  alespon roven limitu na ucet. Zmena limitu neobchazi stare zaznamy.
- Nejvyse dva soubezne prenosy exportu; obsazena kapacita vraci `429`.
  Priprava uloh probiha postupne. Cekani na sitovy zapis konci po 30 sekundach.
- Trvaly soubor `ApplicationIdentity:DataRoot/account-data-export.sqlite`
obsahuje pouze metadata, dukazy uloh a minimalni zaznamy o prijatych klicich
a smazanych uctech. Zaradte jej do odpovidajici spravy
  trvalych uctovych dat; nevkladejte jej do webrootu ani repozitare.

Registracni metody jsou `AddAccountDataExports` a `MapAccountDataExports`.
Uloziste exportnich metadat musi zustat registrovane pri povolenem smazani
uctu i tehdy, kdyz exportni HTTP cesty nejsou vystavene. Chybejici registrace
zablokuje prijeti smazani jeste pred deaktivaci uctu; uklid se nesmi tise vynechat.
Vyuzivaji skutecny sdileny adapter osobni pameti a existujici sluzby uctu,
postav, zurnalu a archivu. Webovy test s fake adapterem sam neprokazuje
atomickou revokaci skutecne pameti ani funkcnost podepsaneho nativniho klienta.

Overeni implementace pouziva synteticka data a zamerene sady
`--account-data-export-authority`, `--account-data-export-store`,
`--account-data-export-document`, `--account-data-export-download-guard`
a `--account-data-export-http`
v projektu `backend-tests`.

## Zakaznicke rozhrani a predani souboru

V uctu otevira zakaznik oblast Moje data vyslovne. Teprve otevreni nacte
povolene osobni vzpominky a stav exportu. Oprava i zapomenuti konkretni
vzpominky vyzaduji aktualni revizi; zapomenuti navic potvrzeni. Nejasny vysledek
zapisu vyzaduje nove nacteni pred dalsim rucnim pokusem. Zadna z techto akci
nespousti automaticke promazavani ani expiraci ulozeneho obsahu.

Web stahuje JSON jako soubor. Nativni aplikace po vyslovne akci preda stejny
overeny JSON systemove nabidce sdileni. Povolen je pouze pevny nazev
`customer-data-export.json`, MIME `application/json` a nejvyse 32 MiB.
Klient pred predanim overi uplny UTF-8 JSON, jeho verzi a identitu aktualniho
uctu. Zmena uctu, zadost o smazani, offline stav, skryti obrazovky nebo vyprseni
pristupu zrusi cekajici predani. Jakmile jiz jina aplikace prijala kopii,
aplikace ji nedokaze odvolat. Systemove potvrzeni dokonceni sdileni samo
neprokazuje ulozeni ani doruceni souboru.

Nativni most pouziva chraneny docasny soubor a uklidi jej po skonceni vlastni
nabidky. Limit 32 MiB pro odpoved plati pouze pro presny GET osobni pameti
a presny GET souboru exportu; obecne limity ostatnich cest zustavaji zachovany.
Vyprseni pristupu ke stazeni nema vliv na jiz ulozenou nebo sdilenou kopii.
