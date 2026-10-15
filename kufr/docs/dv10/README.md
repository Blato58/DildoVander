# DV10 — soukromá památka z kufru

Kanonický mobilní web je v `kufr/web/keepsake/`. Relativní `/guest/v1` API,
SQLite autorita a bounded worker žijí v existujícím .NET backendu. Web i API
běží na stejném HTTPS originu stávajícího VPS. Majitel 15. 10. 2026 autorizoval
nasazení a zapnutí placeného generování se stávajícím backendovým klíčem;
[veřejný přehled](https://2.28.202.220/keepsake/evening/) je nasazený a ověřený.
Příběhy používají `gpt-6.1-sol`, obrazy `gpt-image-2`. Přesné releasy, konfiguraci
a kontroly uvádí [produkční záznam](../../../docs/project/artifacts/DV10-production-v1.json)
a [VPS handoff](VPS_HANDOFF.md). [Specifikace 2.2](SPEC.md) nadále odděluje
implementaci a nasazení od ověření kvality, iPhonu, hardware a obnovy.

## Vyzvednutí jedním QR

[Upřesnění majitele](STATIC_EVENING.md) zavádí společný přehled `/keepsake/evening/`: poslední hotová hůlka a seznam aktivního večera. Hůlky a příběhy jsou veřejné pro návštěvníky; fotografie, portréty a správa zůstávají soukromé. Firmware i mobil přestávají vydávat individuální QR.

## Co je implementované

| Etapa | Zdroj a chování |
|---|---|
| A | Ověřený .NET 10 / OpenAI 2.14.0 / SQLite stack; read-only VPS preflight a audit oddělené retence. |
| B | Atomický technický selection outbox; důvěryhodný producent potvrzuje konkrétní časovanou prezentaci. Jedna památka na outcome, stabilní katalogová ID a immutable snapshot. |
| C | Náhodná browser session, jednorázový claim, explicitní recovery a edičně vázaný share. Owner, share a operator scopes se ověřují přímo v aplikaci. |
| D | Trvalá fronta a attempts, flags/backpressure, story před obrazem, skutečný SDK generate z textu, nejistý attempt bez slepého retry. Fake je zřetelně syntetický. |
| E | Mobilní archivní list, přístupný český text, PNG karta s kanonickým Interem, obraz, bounded ZIP s manifestem a soukromé/read-only sdílení. |
| F | Samostatný souhlas, lokální foto preview, plný bounded decode/orientace/normalizace, zachovaný originál, skutečný SDK image edit, historie a scoped ruční mazání. |
| G | ESP32 samostatně vydává outcome; společný přehled aktivního večera nepoužívá telefon obsluhy ani individuální QR v LAN snapshotu. [Firmware evidence](FIRMWARE_OUTCOME.md). |
| H | Tři tracked skills, přesná kompatibilní proxy, code-only release packager, consistent SQLite backup, klíče a fail-closed restore verifier. |

Žádný guest GET negeneruje, neclaimuje ani nemaže. Hůlka je nový digitální návrh
spojený s katalogovou rekvizitou; obrázek není fotografie ani přesná replika.
Fyzická evidence `completed_by_timing` je kalibrované časování, nikoli měření
polohy nebo předání rekvizity návštěvníkovi. Backend nikdy nespíná aktuátory.

## Konfigurace a bezpečné vypnutí

Všechny tři flags jsou ve výchozím nastavení vypnuté; v nasazené produkci jsou
`Enabled`, `GenerationEnabled` a `PortraitEnabled` zapnuté. Pro čtení již uložených
památek lze nechat `Enabled=true` a vypnout nové generování. Čekající práce a
obsah zůstávají; re-enable nevymaže data a neřeší nejasné placené attempty retry.

| Nastavení | Účel |
|---|---|
| `Keepsake:Enabled` | Guest, veřejná galerie a producer routy. False vrací 404; nakonfigurovaný archiv/admin a potvrzený cleanup zůstávají dostupné. |
| `Keepsake:GenerationEnabled` | Přijetí a dispatch generování; false pozastaví trvalou frontu. |
| `Keepsake:PortraitEnabled` | Samostatná foto/edit cesta, bez vlivu na dokončený základ. |
| `Keepsake:ProviderMode` | `fake` nebo explicitní `openai`. Fake nikdy nepoužije ambient OpenAI credential. |
| `Keepsake:PublicDeviceId` | Výslovně vybraný kufr pro statický přehled, jeho aktivní večer z existujícího evening store. Bez konfigurace přehled 404. |
| `Keepsake:Origin` | Ověřený HTTPS origin bez cesty, query, userinfo a fragmentu; nikdy se neodvozuje z Host/forwarded vstupu. |
| `Keepsake:DataRoot` | Soukromý persistentní archiv mimo webroot, checkout a releases. |
| `Keepsake:DataProtectionRoot` | Persistentní key ring; výchozí `DataRoot + ".keys"`. Zahrnout do šifrované obnovy. |
| `Keepsake:StoryModel`, `Keepsake:ImageModel` | Explicitní modely ověřené pro účet a pinned SDK před placeným pilotem. |
| `Keepsake:MaxQueuedJobs` | Technický limit 1–32. Přijaté snapshots čekají při backpressure a zůstávají zachované. |
| `Keepsake:MinimumFreeDiskBytes` | Admission rezervuje kapacitu; nikdy nemaže přijaté památky pro uvolnění místa. |

Backend používá stávající `OPENAI_API_KEY`, jak majitel potvrdil 14. 10. 2026.
Klíč ani admin/device credential nejdou do webu, QR, artefaktů nebo archivu.
Produkční změna, provider pilot, firmware flash a podpis zůstávají samostatné
autorizované kroky. [Přesné deployment a restore kroky](VPS_HANDOFF.md).

## Přístup a správa dat

Statický QR je `/keepsake/evening/` bez tokenu. Oddělený soukromý claim link má `/keepsake/#c=…`. Nejčasnější inline skript zahodí fragment před načtením
assetů. Explicitní kliknutí vymění token přes POST; .NET vydá host-only
`__Host-dv10` cookie, Secure/HttpOnly/SameSite=Lax. Bootstrap je mezi záložkami
serializovaný Web Locks; podporovaný secure-context browser je podmínkou claimu.
Clean owner URL má `?k=locator`, share má také `view=share&editionId=…`.

Soukromý návratový odkaz uděluje správu a přístup k portrétu. Veřejné tlačítko
sdílení vydává jen read-only hůlku a příběh pevné edice, včetně base-only exportů.
Přidání portrétu sdílený obsah nerozšíří. Standardní ZIP neobsahuje portrét;
owner jej přidává samostatným checkboxem. Originální foto není v žádném ZIP.

Claim platí deset minut, session třicet dní a share sedm dní. Recovery platí do
odvolání. Expirace a logout nezpůsobují smazání ani novou inferenci. Opětovné
vydání claimu neclaimované památky je idempotentní; po převzetí je běžné device
reissue odmítnuté. Citlivý `POST /v1/keepsakes/{id}/restore-owner` vyžaduje
existující server-admin credential, idempotency key a body
`{"schemaVersion":1,"confirm":"revoke_prior_access"}`. Atomicky odvolá původní
owner/recovery/share grants a vrátí claim stejné uložené památky.

Ruční provozovatelská správa používá stejné server-admin oprávnění na
`/v1/admin/keepsakes`. Seznam vrací omezená technická metadata. Mazání má dva
kroky: `POST /v1/admin/keepsake-deletions/preview` dostává explicitní pole
`targetIds` a scope `all` nebo `portrait`; potvrzení
`POST /v1/admin/keepsake-deletions/{previewId}/confirm` požaduje `confirm=delete`
a `previewDigest`. Změněná revize nebo soubory odmítnou celou dávku před revokací.
Nově vzniklé památky nejsou součástí starého náhledu. Receipts a cancel/share
revokace jsou pod stejnou admin hranicí; guest cookie ani device token nestačí.

Veškerý přijatý a vytvořený DV10 obsah má `retentionMode=manual_only` a
`deleteAfterUtc=null`. Patří sem originály, normalizace, story/design, modelové
obsahové odpovědi, prompty bez secrets, zrušené/superseded výsledky a exporty.
Retence jiných modulů se nemění. Odvolání souhlasu nebo cancel uchovává obsah;
explicitní ruční smazání fotografie odstraní originály, kopie, portréty i všechny
navázané derivace včetně orphan souborů po pádu. Mazání celé památky odvolá vše.

DELETE vyžaduje explicitní potvrzení a očekávanou revizi; opakovaný request
vrátí stejný receipt i po revokaci. Receipt rozlišuje primární lokální cleanup,
provider stav `unknown_not_provider_confirmed` a pending backup reconciliation.
Lokální unlink není potvrzení smazání u OpenAI, ve staré záloze ani u příjemce
již staženého souboru. Restore zůstává izolovaný, dokud neprojde kontrolou
nejnovější nezávislé deletion/revocation evidence a hashů.

## Ověření bez placených volání

Z kořene `kufr/` po restore podle pinned lockfile:

```powershell
dotnet restore backend-tests/BackendTests.csproj -p:SelfContained=true --locked-mode
dotnet build backend-tests/BackendTests.csproj --no-restore
dotnet run --project backend-tests/BackendTests.csproj --no-build -- --keepsake
node backend/tests/keepsake-http.mjs
```

HTTP harness spouští skutečný lokální .NET listener s oddělenou DB, syntetickými
credentials a `ProviderMode=fake`. Testuje skutečné autorizace i bez nginx;
loopback hlavičky simulují důvěryhodnou proxy, nikoli skutečnou veřejnou TLS.
Core fake-handler testy zachycují serializovaný oficiální SDK generate/edit
request, včetně skutečného foto vstupu a nulového automatického retry.

Z kořene repozitáře:

```text
python -m unittest kufr.web.keepsake.test_portal
python -m unittest discover -s kufr/web -p test*.py
python -m unittest discover -s kufr/web/deploy -p test_keepsake*.py
python -m unittest discover -s kufr/backend/deploy -p test_keepsake*.py
node kufr/web/keepsake/test-browser.cjs
node kufr/web/keepsake/test-evening-browser.cjs
```

Browser harness potřebuje Playwright a Edge; opakované scénáře používají
syntetické DTO, aby deterministicky ověřily souběh/stale callbacks a žádné AI
volání. Neprokazuje OpenAI výtvarnou kvalitu ani iPhone. Mobil: `npm ci`, také
`npm ci --prefix ../contracts`, `npm test` a `npm run build`. Firmware: `pio run`
a host test z [firmware handoff](FIRMWARE_OUTCOME.md). SkiaSharp 4.153.1 používá
MIT licencované native assets; PNG karta vkládá existující OFL Inter font.

Samostatný webový build ze stejného zdroje nepotřebuje CAD ani mobilní build:
`python kufr/web/build-site.py --keepsake-only --destination NOVY_ADRESAR`.
Obsahuje `/keepsake/`, `/keepsake/evening/` i lokální design-system/fonty; manifest přiznává dirty
zdroj. Linux backend vytvoří `dotnet publish` podle workflow. Code-only packager
a adresářové předání jsou v [delivery návodu](DELIVERY.md); sestavení není deploy.

## Skills a zbývající gates

[Design/art direction](../../../.agents/skills/dildovander-keepsake-design/SKILL.md),
[reliability/privacy eval](../../../.agents/skills/dildovander-keepsake-eval/SKILL.md)
a [delivery/recovery](../../../.agents/skills/dildovander-keepsake-delivery/SKILL.md)
jsou tracked repo skills. Skutečné `codex app-server` `skills/list` s
`forceReload=true` je objevilo jako enabled/repo; samotný validator tuto
integraci nenahrazuje. Tento již běžící chat se automaticky nepřepíše.

Aktuální výsledky jsou v [projektové položce DV10](../../../docs/project/work-items/DV10.json)
a [capability scorecard](CAPABILITIES.json). Veřejné nasazení a zapnutí placeného
generování jsou dokončené. Při ověření byl přehled prázdný a neproběhla skutečná
inference. Kvalita výstupů a portrétu ze souhlasné fotografie, skutečný iPhone
guest, fyzická akceptace a nezávislý encrypted backup/restore zůstávají neověřené;
lokální syntetické testy je nenahrazují. `event_ready` zůstává false.
