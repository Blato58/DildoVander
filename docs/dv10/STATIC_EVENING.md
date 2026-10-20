# Statický QR a společný přehled večera

Majitel 14. 10. 2026 změnil vyzvednutí DV10: jeden statický QR vedle kufru
otevírá `/keepsake/evening/`, poslední hotovou hůlku a seznam hůlek aktivního
večera. Výslovně schválil veřejný přehled hůlek a příběhů. Tento pokyn má
přednost před individuálním QR a neveřejným základním listem v původním
[SPEC](SPEC.md). Fotografie, portréty a správa zůstávají soukromé.

Telefon obsluhy není potřebný k vyzvednutí. ESP stále samostatně potvrzuje
konkrétní fyzický outcome přes HTTPS. Po platném serverovém ACK jej trvale
odznačí jako doručený; nevyzvedává claim token a nezveřejňuje QR v LAN snapshotu.
Změna nemění pohyb, bezpečnostní brány, firmware kalibraci ani audio.

## Večer a veřejný scope

Autoritou aktivního večera je existující `EveningMemoryStore`, pro každé zařízení
samostatně. Serverové `Keepsake:PublicDeviceId` výslovně určuje jeden kufr;
URL ani prohlížeč nesmí zařízení přepnout. Veřejné čtení nezakládá večer.

UUID večera se zachytí z kontextu návrhu do technického outboxu před pozdějším
fyzickým ACK a přenese do immutable snapshotu. Platí pro text, Realtime i Live.
Staré záznamy bez této vazby se automaticky nepřiřadí novému aktivnímu večeru.
Aktivace jiného večera pouze mění veřejnou viditelnost; archiv nemaže.

Přehled řadí podle okamžiku publikování hotového základního obrazu. Ukazuje
nejnovější publikovanou základní edici každé památky, s omezenou stránkou a
kurzorem vázaným na večer. Zrušené, neveřejné, nehotové a smazané edice nejsou
hotovou veřejnou hůlkou. Syntetický výsledek je zřetelně označený.

## Rozhraní a soukromí

- `GET /guest/v1/evening` vrací aktivní večer, poslední hotovou hůlku a seznam.
- `GET /guest/v1/evenings/{eveningId}/keepsakes/{id}/editions/{editionId}/assets/{assetId}`
  vrací pouze autorizovaný základní obraz nebo TXT/PNG/ZIP bez portrétu.
- Veřejný web nebootstrapuje cookie, neclaimuje, negeneruje a nic nemaže.
  Exporty vznikají omezenou lokální prací backendu, nikoli při GET.
- API nezveřejňuje fotografie, prompty, provider response, osobní historii,
  device identitu, privátní přístupy ani libovolný storage path.
- Soukromé owner/recovery/portrait rozhraní zůstává oddělené a potřebuje svůj
  oprávněný přístup. Výběr řádku veřejné galerie tato práva neuděluje.

Assety jsou no-store, omezeně streamované a chráněné celkovým souběhem downloadů.
Web stahuje pouze vybraný obraz, aby všichni hosté nesdíleli velký burst náhledů.
Změna večera, smazání a stale odpověď nesmí vrátit předchozí obsah do nové stránky.

## Aktivace a důkazy

Produkční flags zůstávají OFF. QR lze vytisknout pro schválený canonical HTTPS
origin s cestou `/keepsake/evening/`; token ani datum se do něj nepřidává.
Konkrétní veřejný origin/TLS, skutečný iPhone, kvalita OpenAI a nezávislá záloha
vyžadují původní oddělené akceptační brány. Lokální fake a build je nenahrazují.

Aktuální ověření a omezení: [README](README.md), [capabilities](CAPABILITIES.json)
a [delivery](DELIVERY.md). `manual_only` se změnou vyzvednutí nemění. Vypnutí
portálu zachovává nakonfigurovaný archiv, admin přístup a dokončení potvrzeného
mazání; nezapíná poskytovatele AI.
