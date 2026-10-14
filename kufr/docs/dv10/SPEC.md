> Pozdější výslovný pokyn majitele ze 14. 10. 2026 mění vyzvednutí na statický QR a veřejný přehled aktivního večera; viz [STATIC_EVENING](STATIC_EVENING.md). Ostatní hranice, soukromé fotografie a oddělené akceptační brány zůstávají.

# DV10 — Wand Keepsake Portal na vlastním VPS
## Finální specifikace · revize 2.2 FINAL · 14. 10. 2026

**Stav:** zadání pro budoucí implementaci; nejde o audit aktuálního HEAD, potvrzení kapacity VPS, provedenou migraci ani již nasazenou aplikaci.

**Podklady:** finální DV10 revize 2.1, původní `SPEC(9).md` a `CODEX_PROMPT(4).md`, následné upřesnění generování/retence a nejnovější rozhodnutí pokračovat s vlastním VPS. Zachovávají se guest journey, rozsah památky, OpenAI pipeline, soukromí a manual-only retence. Tato revize mění produkční hosting a související provozní kontrakty; nepřepisuje produkt na jinou aplikaci. Cesty, runtime a oprávnění musí implementační agent ověřit ve skutečném projektu.

**Závazná změna:** produkční návštěvnický web i jeho guest API provozovat na **stávajícím VPS**, s jediným kanonickým zdrojem frontendu v repozitáři DildoVander. Browser používá **jeden veřejný HTTPS origin** pro web i návštěvnické API. Existující .NET backend vlastní outcome, oprávnění, generování, data a ruční mazání.

**Nahrazené rozhodnutí:** předchozí povinnost hostovat web přes Codex Sites / ChatGPT Sites už neplatí. Nevytvářet Sites projekt, Sites BFF, hosting binding ani D1/R2 závislost. Sites může sloužit jen jako samostatný volitelný vizuální experiment se syntetickými daty po výslovném zadání; v této implementaci se nepoužívá a jeho nedostupnost nic neblokuje. Nepřesouvat produkci na jiný hosting.

**Beze změny:** obrázek celé hůlky vytvoří **OpenAI od nuly z textu**, osobní Magical Portrait vytvoří **OpenAI úpravou návštěvníkem nahrané fotografie**. Všechna řádně přijatá a vytvořená data DV10 se uchovávají **do výslovného ručního smazání**. Nevracet fotografický cutout/composite ani obsahové TTL. Expirace odkazu, cookie či oprávnění není smazání dat.

**Pravidlo jednoduchosti:** použít stávající frontendový stack, .NET doménu, persistence a provozní workflow. Nevytvářet nový Node BFF, druhý katalog, kopii backendu ani platformu mikroservis. Níže popsané odpovědnosti nejsou požadavek na samostatný proces pro každou z nich. Konkrétní aktuální kapacita, adresy, služby a zálohy zatím nejsou tímto dokumentem ověřené.

---

## 0. Rozhodnutí, priority a slovník

### 0.1 Co se proti vstupu mění

| Téma | Rozhodnutí revize 2.2 |
|---|---|
| Hosting | Produkční portal a guest API na stávajícím VPS, jeden HTTPS origin; frontend je součást repozitáře. Sites není závislost. |
| Backend | Existující .NET + současná persistence jsou autorita; žádné mezihostingové BFF ani nový gateway secret. Reverzní proxy a aplikace mají oddělené bezpečnostní úlohy. |
| Identita | `stableWandId` není číslo přihrádky. Uchovat katalogový a fyzický mapping snapshot; digitální návrh má vlastní verzovanou identitu. |
| QR | Jednorázové převzetí památky; zvlášť návrat vlastníka a zvlášť read-only sdílení. Žádný jeden univerzální token na vše. |
| Obraz hůlky | OpenAI text-to-image od nuly: jedinečný celý návrh hůlky i prostředí. Žádná fotografie rekvizity, cutout, composite ani referenční image edit jako podmínka. |
| Portrét | OpenAI image edit z explicitně nahrané fotografie; pro variantu s hůlkou lze přidat právě schválený generovaný obraz téže edice. |
| Ukládání | `manual_only`: všechny přijaté vstupy, výstupy, verze a metadata DV10 zůstávají do ručního smazání. Žádné TTL obsahu ani mazání originálu po generaci. |
| Vyprávění | Jeden autorský brief propojí příběh, atmosféru obrazu a vzkaz kufru. Oficiální jméno a fakta sází aplikace. |
| Stavy | Přístup, vydání památky, jednotlivé artefakty a portrét jsou oddělené osy. Portrét neblokuje dokončenou hůlku. |
| Joby | Durable joby, fencing, nejasný výsledek poskytovatele a publikace pod revizní bránou; nikoli slib exactly-once inference. |
| Smazání | Revokace přístupu je okamžitý logický krok; fyzický cleanup má vlastní doložený stav. Staré zálohy nesmějí památku oživit. |
| Zveřejnění | Zdroj, ověřený build artefakt, testovací runtime, schválené nasazení na VPS a anonymní guest akceptace mají odlišné stavy. |
| Rozsah | Implementovat celé proveditelné jádro A–H; žádné zastavení po fake landing page. Placené testy a publikace mají vlastní bránu. |

### 0.2 Závaznost

**MUST** = povinné chování. **SHOULD** = kvalitativní cíl; odchylka potřebuje konkrétní důvod. **LATER** = mimo základní implementaci. Číselné limity označené jako návrhové se mají ověřit v cílovém runtime a výkonovém profilu, ne prezentovat jako již naměřené hodnoty.

Při rozporu nejprve chránit správnou hůlku, izolaci hostů, revokaci a již existující safety. Potom zachovat jednoduché guest UX a charakter DildoVandera. Případnou redukci kvality či rozsahu vykázat; neschovávat ji za `ready`.

### 0.3 Pojmy

- **Outcome:** autoritativně zaznamenaný výsledek konkrétního výběru; ne volná věta modelu.
- **Presentation evidence:** co víme o fyzické prezentaci. ACK, dokončení časováním a měření nejsou zaměnitelné.
- **Keepsake:** jedna digitální památka k jednomu outcome, s vlastními přístupovými a obsahovými verzemi.
- **Owner:** držitel oprávnění spravovat tuto památku; není to biometricky ověřená identita návštěvníka.
- **Share viewer:** držitel výslovně vytvořeného read-only oprávnění k vybranému publikovanému obsahu.
- **Digitální návrh hůlky:** osobní AI interpretace asociovaná se skutečně zvolenou katalogovou hůlkou, nikoli fotografie či důkaz jejího fyzického tvaru.
- **Manual deletion:** autentizovaná explicitní lidská akce s potvrzeným rozsahem; worker smí následně automaticky dokončit takto vyžádané odstranění.
- **Edition:** společná obsahová verze příběhu, art direction a navázaných exportů.
- **Guest surface:** přesně vymezené návštěvnické routy v existující .NET aplikaci; autorizační policy se vynucují i při přímém aplikačním testu bez proxy.
- **Release artifact:** ověřený neměnný balík kódu a veřejných statických assetů; nikdy provozní databáze, osobní soubory ani secrets.
- **Persistent data root:** soukromé trvalé úložiště oddělené od zdrojů, webrootu, buildů a release adresářů.

## 1. Cíl zážitku a rozsah

Návštěvník si má odnést: **„Tohle je opravdu hůlka, kterou mi kufr vybral. Ten příběh patří k našemu rozhovoru a obrázek není stejný jako u předchozího člověka.“**

Web není náhradou finále u kufru. Je jeho druhou částí: klidné otevření osobního archivního listu, přečtení pointy a snadné uložení památky. Další návštěvník nesmí čekat na obrázek předchozího hosta.

### 1.1 Povinné jádro

Jeden mobilní web na VPS; stabilní vazba na skutečný outcome; převzetí QR bez registrace; návrat k památce; oficiální název a pravdivý stav výběru; individuální AI příběh; individuální wand artwork; samostatně opt-in portrét; download jednotlivých souborů i balíčku; read-only sdílení bez portrétu; odvolání přístupu a smazání; běžné pending/partial/error stavy; propojení s operátorským iPhonem; verifikace a projektové skills.

**Bez portrétu je základní památka plnohodnotný výsledek.** Neudělení souhlasu nesmí snížit kvalitu příběhu ani vyvolávat opakované pobízení.

### 1.2 Mimo rozsah

E-shop, platby, NFT, veřejná galerie, žebříček hostů, gamifikace lidské hodnoty, rozpoznávání obličejů, účty hostů, povinné Sign in with ChatGPT, přístup k jejich konektorům, volný chatbot na webu, generování na libovolný prompt, nové ovládání ESP, přepracování DV01/DV02/DV08/DV11, zavádění alternativního hostingu, přepis .NET backendu nebo nové povinné externí úložiště.

Sběratelské rarity, druhý styl na přání, další jazyky, tisk QR, duo portréty a online galerie jsou LATER. Nevytvářet pro ně registr pluginů nebo generický workflow engine.

## 2. VPS-first architektura a ověření skutečného prostředí

### 2.1 Rozhodnutí a rozsah změny

Výchozí topologie je **repozitářový frontend + guest API v existujícím ASP.NET Core backendu + omezené workery + privátní persistentní storage na VPS**. Není potřeba převést backend do JavaScriptu ani přidat serverovou bránu mezi dvěma hostingy. Statické UI může servírovat současná reverzní proxy nebo .NET; implementační audit vybere menší kompatibilní změnu a zdokumentuje jednu produkční cestu.

Dokumentace Microsoftu popisuje ASP.NET Core za reverzní proxy a správu běžící aplikace [S1]; dokumentace proxy vysvětluje důvěru ve forwarded headers [S2]. To jsou podklady návrhu, nikoli důkaz konfigurace tohoto VPS. Stávající Nginx/systemd či kontejnerový provoz nejprve ověřit. Nemigrovat orchestrace jen kvůli DV10.

### 2.2 Krátký audit, ne přestavba serveru

Nejdříve zmapovat z repozitáře skutečný deploy postup. Přístup na VPS použít jen přes již dostupnou a oprávněnou cestu; první provozní inspekce je read-only, bez výpisu secrets a bez čtení skutečných guest fotografií/databází. Není-li přístup dostupný, dokončit lokální kód, syntetické integrační testy a `VPS_HANDOFF.md`; nevydávat neověřenou kapacitu za dostatečnou.

| Oblast | Co ověřit | Co nedostupnost blokuje |
|---|---|---|
| Identita cíle | Správný server/prostředí, existující deploy workflow, oprávnění; SSH alias není důkaz cílové identity | Jen vzdálenou inspekci/nasazení, ne lokální implementaci. |
| Procesy a runtime | Skutečná .NET verze, procesní správce, proxy, service user, listening endpoints | Nasazení nekompatibilního artefaktu; bez nevyžádané aktualizace OS/runtime. |
| Frontend | Současný stack, router, build output a dotčené workflow `kufr/web` | Konkrétní integrační větev, ne záminka pro rewrite. |
| Origin/TLS | Vlastněný canonical origin, aktuální certifikát, směrování, možnost izolovat guest surface | Veřejné QR a guest release; žádná vymyšlená doména. |
| Kapacita | CPU/RAM, volný disk i inodes, mounty, temp prostor, DB contention, bandwidth a storage růst podle měření | Přijímání skutečných dat/generací, pokud není doložená rezerva. |
| Data a klíče | Umístění DB, accepted assetů, deletion/revocation ledgeru, případné Data Protection a asset-encryption klíče | Produkční retenci/restart, pokud by je deploy mohl ztratit. |
| Ochrany HTTP | Host/forwarded trust, autentizace, CSRF, upload/download caps, cache a logování | Zpřístupnění osobních dat, ne fake UI. |
| Obnova | Nezávislá šifrovaná záloha, dosažitelnost recovery klíčů, poslední skutečný restore test | `production_ready`; příprava skriptů není úspěšná obnova. |
| Stávající služby | Skutečné Live/API/OTA/iOS instalační routy a health baseline, pokud existují | Zásah, který je nedokáže zachovat nebo má neautorizovaný dopad. |

Nevypisovat `env`, obsah credential souborů nebo rozsáhlé osobní logy do reportu. Zaznamenat potřebná technická metadata a redigované výsledky. Rozhodnutí „VPS“ samo neuděluje oprávnění měnit DNS, firewall, certifikáty, placené služby ani provozní data.

### 2.3 Jedna návštěvnická cesta

```text
Návštěvník / Safari
   │ HTTPS, jeden ověřený origin, host-only guest cookie
   ▼
Stávající reverzní proxy na VPS
   ├── pouze veřejné statické UI z ověřeného release artefaktu
   └── pouze vymezené guest API → privátní .NET listener
                                   ├── outcome / grants / edition / durable jobs
                                   ├── omezená generace → OpenAI API
                                   └── privátní data + autorizované downloads/delete

ESP / oprávněný operátorský iPhone
   └── původní autentizovaná cesta → existující outcome autorita

Privátní data + konzistentní DB snapshot + potřebné recovery klíče
   └── nezávislá šifrovaná záloha + aktuální deletion/revocation evidence
```

Preferovat vlastní guest hostname pod již ovládanou doménou, je-li skutečně k dispozici; přesný název je konfigurace. Web a jeho guest API jsou na témže originu. Absence domény neblokuje build, ale platný veřejný HTTPS origin musí existovat před vydáním ostrých QR. Neměnit původní backend origin ani rozbíjet ovládání kufru kvůli estetickému přejmenování URL.

Browser nevolá OpenAI, nepřistupuje k device/admin API a nezná žádný serverový klíč. Generování modelu probíhá přes OpenAI API [S3]; tato architektura nenavrhuje GPU ani lokální image model na VPS. CPU/RAM pro dekódování, exporty a frontu přesto vyžadují měření. Telefon není trvalý cloudový prostředník.

### 2.4 Hranice reverzní proxy a aplikace

Na guest originu povolit jen přesné veřejné stránky/statické assety a zvolenou guest API mapu dle §13. **Neproxyovat celý backend pomocí obecné routy `/api/*` nebo catch-all `/` do všech jeho endpointů.** Prefix nestačí, pokud pod ním žije administrace. Neznámé API routy vracejí skutečný neutrální 404/405, nikoli SPA `index.html` s 200.

Aplikace nezávisle vynucuje autentizační schéma, scope, locator, aktuální access/deletion epochu a CSRF. Guest cookie nikdy nesplňuje device/admin policy. Přímý request do aplikačního test serveru se stejnou cookie nedostane vyšší práva. Backend listener neveřejně bindovat podle skutečné topologie; nevystavovat nový Kestrel port do internetu jako obcházení proxy. Původní oprávněné serverové vstupy zachovat.

Důvěřovat forwarded headers jen od konkrétní ověřené proxy; ta příchozí autoritativní forwarding hlavičky přepisuje nebo jinak bezpečně normalizuje. Nepřijímat libovolný `Host`, `X-Forwarded-Host`, `X-Forwarded-Proto`, gateway header či callback URL jako zdroj oprávnění nebo veřejného QR. Canonical origin je validovaná konfigurace. Proxy i aplikace mají omezený accepted-host profil, správné scheme a pořadí middleware podle skutečné verze [S2].

Výchozí browser používá relativní same-origin API URL; žádná produkční cross-site cookie nebo wildcard credentialed CORS. Lokální Vite proxy, je-li používána, cílí jen na explicitní lokální/testovací backend, nikoli automaticky na produkci. Nezavádět nový gateway credential ani Node BFF, když .NET přijímá guest request přímo.

### 2.5 Nasazení není úložiště

Oddělit read-only release obsah, soukromé accepted assets, databázi, provozní klíče, omezené temp a zálohy. **Accepted fotografie a OpenAI originály nesmějí ležet v `wwwroot`, `dist`, Git checkoutu, dočasném adresáři, kontejnerové zapisovatelné vrstvě bez persistence ani uvnitř adresáře nahrazovaného deployem.**

Konkrétní absolutní cesty zjistit nebo nakonfigurovat; nevnucovat domnělé `/opt/...` adresáře. Service user má jen potřebný přístup. Runtime a archiv nesmějí získat možnost přepsat aplikaci či proxy konfiguraci. Pro privátní soubory použít existující ověřené šifrování at rest a přístupová práva; nevymýšlet vlastní kryptografii. Chybějící šifrování/recovery je explicitní release gate, ne tvrzení „vyřešeno“ z příkazu chmod.

Reverzní proxy smí přímo servírovat pouze veřejné statické assety. Privátní data vrací autorizovaný endpoint; případné interní akcelerované stahování vyžaduje skutečně interní routu a serverem odvozený soubor, nikoli veřejný alias privátního rootu. Otestovat symlinky/path traversal a neznámé asset typy.

### 2.6 Jednoduchý provozní model

Výchozí varianta rozšiřuje stávající .NET aplikaci o guest routy a bounded background workers. Samostatný worker proces má smysl pouze při doložené potřebě izolace CPU/paměti; sdílí implementaci domény a trvalou job autoritu. Nepřidávat novou databázi, queue server ani další autentizační systém jen kvůli oddělení procesu.

Krátké DB transakce, bounded image decode/export, stop-admission při nedostatku kapacity, graceful shutdown a fencing zachovají kontrakty §12. Dlouhý image call neběží ve web requestu. Opatření prioritizují nový Live provoz podle současné orchestrace; nenahrazují skutečnou zkoušku souběhu.

Pokud existuje starý Sites prototyp nebo vazba, nejdříve pouze evidovat. **Nic vzdáleně nerušit ani nemazat automaticky.** Pro produkční kód odstranit aktivní závislost na Sites; případný již existující obsah nebo odkazy migrovat jen podle doloženého inventáře a oprávnění dle §17.8.

## 3. Guest journey a chování při nedostatku dat

### 3.1 U kufru

Po platném outcome vznikne jedna památka a začne neblokující příprava. Operátorský iPhone pro přesný outcome zobrazí QR a stručné sdělení „Tvoje hůlka má vlastní archivní list“. Obsluha nic ručně nepíše a nemá vybírat obrázek za hosta.

QR panel je svázaný s `outcomeId`, `sessionEpoch` a konkrétní fyzickou operací, nikoli s proměnlivým „poslední host“. Přechod na nového hosta starý QR automaticky skryje. Opožděný callback prvního hosta nepřepne panel druhého.

### 3.2 První otevření

1. QR otevře lehkou landing stránku na ověřeném VPS originu. Před převzetím nezobrazovat osobní motivy ani fotografii.
2. Jedno tlačítko **„Převzít mou památku“** provede explicitní claim. Prefetch, crawler ani pouhé GET claim nespotřebuje.
3. Host hned uvidí oficiální identitu zvolené hůlky a skutečné stavy příběhu/obrazu. Do vygenerování obrazu zobrazit neosobní skeleton/pečeť; nevyplňovat místo fotografií rekvizity nebo cizím hotovým výsledkem.
4. Hotová část je dostupná okamžitě, druhá může dále vznikat. Stránku není nutné držet otevřenou.
5. Po dokončení obraz zůstane stabilní. Refresh nevyrábí další placenou variantu ani nemění již uložený příběh.
6. Host může stáhnout, přečíst, dobrovolně vytvořit portrét nebo uložit soukromou návratovou cestu.

### 3.3 Bez použitelného osobního motivu

Nevymýšlet vzpomínky, vlastnosti ani fyzický vzhled. Unikátnost poskytne hůlka, autorská varianta a náhodné ediční zadání. UI nemá hosta trestat hláškou „Nedostatek osobních údajů“.

Vhodný vzkaz: „O sobě jsi neřekl skoro nic. Konečně někdo, kdo mi ke kusu dřeva nepřiložil autobiografii.“ Je to autorská inspirace pro případ skutečně krátkého vstupu, ne povinná věta ani smyšlená vzpomínka.

### 3.4 Výpadky a návrat

Kufr může být vypnutý, když si host večer otevře hotovou památku. Web stále potřebuje internet a dostupnou VPS web/API cestu; „funguje bez kufru“ neznamená offline web.

Při pádu backendu ponechat generický shell, nezobrazit starou památku jiného hosta a neinterpretovat chybu jako vymazání. Při expirovaném oprávnění neutrální zpráva. Při nejasném fyzickém výsledku netvrdit úspěšné předání. Při chybě obrazu nabídnout hotový příběh a pravdivý stav bez obrázku; nepředstírat hotovou ilustraci pomocí generické stock hůlky. Památka sama neexpiruje a výpadek přístupu ji nemaže.

## 4. Outcome, hůlka a zdroje pravdy

### 4.1 Identita nezávislá na přihrádce

`stableWandId` identifikuje katalogovou hůlku nebo konkrétní rekvizitu podle skutečného datového modelu. `compartmentAtSelection` je snapshot umístění 1–6. `catalogVersion` zamrazí oficiální texty; `wandDesignPolicyVersion` určí povolený tvůrčí rámec digitální interpretace; konkrétní `wandDesignId` a jeho hash vzniknou v edici. Má-li jeden typ více fyzických kusů, přidat existující `physicalPropId`, nikoli předstírat, že typ a kus jsou totožné.

Složené `slot-2-at-catalog-version` není nová trvalá identita, pokud lze kusy přemístit bez změny katalogu. Chybí-li stabilní ID, vytvořit explicitní mapování s migrační kontrolou; nikdy neposunout už vydané památky na současný obsah slotu.

### 4.2 Způsobilost outcome

| Vstup | Výsledek DV10 |
|---|---|
| Pouhý AI návrh | Žádné vydání památky jako potvrzeného výběru. |
| Validní potvrzený výběr, prezentace čeká | Příprava může existovat interně; guest popis rozlišuje volbu a prezentaci. |
| Dokončení podle kalibrovaného času | Památka způsobilá dle provozní politiky; evidence je `completed_by_timing`, ne měření. |
| Odesláno, výsledek neznámý | Žádné tvrzení o předání; označený selection-only profil pouze po explicitním povolení politikou. |
| Stop před platnou volbou / zneplatněný návrh | Žádná běžná památka ani automatický nový výběr. |
| Special referral | Jen samostatný referral profil, existuje-li skutečný DV11 kontrakt; nikdy slot 7. |

V projektu jsou hůlky rekvizity. Text „zvolená hůlka“ neznamená vlastnictví fyzického předmětu ani potvrzené odebrání z přihrádky. Nepoužívat „už ji máš v ruce“, „zakoupil jsi“ nebo „byla předána“, nemáme-li odpovídající důkaz.

### 4.3 Snapshot a následná evidence

Zamrazit rozhodnutí, mapping, verze, účel zpracování, dovolené motivy a odkazy na potvrzující události. Pozdější potvrzení prezentace lze připojit jako novou evidence revision; nesmí přepsat původní identitu výběru. Rozpor spouští review/quarantine příslušné památky, ne automatickou druhou volbu.

Ilustrační syntetický snapshot; nejde o již existující API:

```json
{
  "schemaVersion": 2,
  "outcomeId": "fixture-outcome-002",
  "outcomeRevision": 3,
  "outcomeType": "physical_wand",
  "selectionStatus": "confirmed",
  "presentationEvidence": "completed_by_timing",
  "operationRef": "fixture-operation-002",
  "wand": {
    "stableWandId": "fixture-wand-002",
    "physicalPropId": "fixture-prop-002",
    "compartmentAtSelection": 2,
    "catalogVersion": "fixture-catalog-v1",
    "wandDesignPolicyVersion": "text-to-image-v1",
    "officialName": "Třešňová hůlka Parketový kompas"
  },
  "personalization": {
    "mode": "no_guest_motifs",
    "approvedMotifs": [],
    "policyVersion": "keepsake-content-v2"
  }
}
```

### 4.4 Vydání a outbox

`outcomeId + keepsakePolicyVersion` má jedinou aktivní vazbu na památku; běžná duplicita události nevytváří další edici. Existuje-li transakční outbox, použít jej. Jinak vložit minimum potřebné pro trvalou evidenci vydání ve stejné transakční hranici jako backendové přijetí outcome, je-li to ve stejné databázi možné.

Pokud outcome a DV10 persistence nejsou transakčně společné, použít idempotentní inbox a dohledatelný reconciliation checkpoint. Neslibovat atomickou transakci přes ESP, síť a dvě databáze. Výpadek DV10 nikdy nevrací fyzický výběr zpět a nikdy nezastaví další povolené sezení kufru.

## 5. Datový model a publikační invarianty

### 5.1 Logické entity

Mapovat na existující konvence; tabulky lze sloučit, významy ne:

| Entita | Vlastní odpovědnost |
|---|---|
| `OutcomeSnapshot` | Neměnná identita volby, verze a evidence. |
| `Keepsake` | Vazba outcome, `retentionMode=manual_only`, revize oprávnění, oddělený publikační a mazací lifecycle; žádné obsahové expiresAt. |
| `Edition` | Uložený brief, `WandDesignSpec`, generovací prompty a konzistentní obsahová verze. Starší edice se nemažou změnou aktivní verze. |
| `Artifact` | Kind, privátní object key, hash, MIME, rozměry, verze, kvalita a dostupnost. |
| `GenerationJob` / `Attempt` | Trvalý stav práce, fencing token, provider request reference, retry rozhodnutí. |
| `BrowserSession` / `Grant` | Opaque session a per-keepsake práva; žádné jméno ani ChatGPT identita. |
| `Claim` / `ShareGrant` / `RecoveryGrant` | Odlišné účely a životnosti tajných oprávnění. |
| `PortraitConsent` / `Upload` | Verze účelu, přijatý originál, normalizovaná modelová kopie, revokace a vazby na všechny portrait attempty. |
| `DeletionReceipt` | Tombstone, rozsah cleanupu, technické výsledky bez obsahu. |

### 5.2 Oddělené osy stavů

- Uchování obsahu: `retained | deletion_requested | deleting | deleted`. **Neexistuje automatický stav `content_expired`.**
- Dostupnost památky: `active | restricted | held`; omezení přístupu nemaže uchované soubory.
- Oprávnění: `active | expired | revoked`; jejich expirace se netýká životnosti dat.
- Vydání: `pending_evidence | eligible | held | not_issued`.
- Artefakt/job: `not_started | queued | running | validating | ready | failed | awaiting_reconciliation | cancelled`.
- Portrét: `not_requested | consented | uploaded | queued | running | validating | ready | failed | cancelled | deletion_requested | deleted`.
- Obrazový původ: `openai_text_to_image` pro hůlku, `openai_image_edit` pro portrét. `fixture` je pouze vývojový, nikoli produkční výsledek.
- Zpřístupnění konkrétní verze: `published | superseded_private | quality_rejected_private | held_private | deleted`.

Guest agregát `preparing/partial/ready/unavailable` je odvozený view. `ready` základu znamená validní příběh a schválený unikátní OpenAI artwork; ne pouhou existenci souborů. Portrét je samostatný a jeho absence neblokuje hotový základ. Quality rejection, nahrazení novější verzí ani cancel samy nezpůsobí smazání přijatých dat.

### 5.3 Verze a neměnnost

Zamrazit `catalogVersion`, `wandDesignPolicyVersion`, `contentPolicyVersion`, `storyPromptVersion`, `artPromptVersion`, OpenAI model/parametry pro konkrétní attempt a společný ediční brief. Model ID je konfigurace, ne enum domény. Aktualizace promptu ovlivní nové attempty, ne obsah při GET.

`WandDesignSpec` obsahuje nový digitální tvar, rukojeť, zdobení, paletu, kompozici a vazbu na katalog. `wandDesignId` není nové skladové ID. Každý výstup nese `editionId`, `artifactVersion`, `sourceManifestHash` a generovací provenance. Portrét se váže na přesný photo upload a případně konkrétní generovaný wand asset téže edice, nikdy na `latest`.

Uchovat přijaté originály, normalizované kopie, použité prompty bez tajemství, obsahové modelové odpovědi, attempty, starší výstupy i metadata až do ručního smazání. Není povinnost archivovat síťové pakety, tajné hlavičky nebo nepřijaté škodlivé vstupy. Úložiště je privátní; uchování není povolení vše publikovat či znovu odesílat poskytovateli.

### 5.4 Invariant publikace

Před zveřejněním v jedné autoritativní backend transakci ověřit:

```text
keepsake.retentionState == retained
AND keepsake.publicationState == active
AND noManualDeletionRequestForDependencies
AND job.fence == currentFence
AND job.contentEpoch == currentContentEpoch
AND result.editionId == intendedEdition
AND result.wandDesignId == intendedWandDesign
AND inputUseStillAuthorized
AND artifactValidationPassed
AND (portrait => currentConsentRevision == capturedConsentRevision)
```

Není zde kontrola `now < keepsake.expiresAt`, protože obsah neexpiruje. Grant konkrétního browseru může vypršet během generace; autorizovaný backend job přesto dokončí a bezpečně uchová výsledek. Publikace neznamená anonymní přístup: každý download stále potřebuje platný vlastní grant.

Zápis do privátního storage sám nic nezpřístupňuje. Starý worker nesmí publikovat; jeho vrácený validní výstup lze uložit jako neveřejnou historickou verzi přes samostatnou idempotentní ingest cestu. Ta ověřuje existující `attemptId`, scope, byte limit a **aktuální deletion epoch**; fencing pro změnu aktivního manifestu tím neobchází.

Po ručním smazání už nová obsahová kopie vzniknout nesmí. Pozdní odpověď téhož attemptu se odmítne a případné transportní zbytky odstraní v rámci již požadovaného mazání. Dostupnost historie nikdy neobchází revokaci souhlasu nebo deletion tombstone.

### 5.5 Ilustrační kontrakt osobní edice

Syntetický doménový příklad, nikoli payload OpenAI API ani existující produkční data:

```json
{
  "schemaVersion": 2,
  "keepsakeId": "fixture-keepsake-002",
  "editionId": "fixture-edition-001",
  "catalogWandId": "fixture-wand-002",
  "wandDesignId": "fixture-digital-design-017",
  "retention": {
    "mode": "manual_only",
    "deleteAfterUtc": null,
    "state": "retained"
  },
  "wandArtwork": {
    "provider": "openai",
    "operation": "text_to_image",
    "inputImages": [],
    "physicalReplicaClaim": false
  },
  "portrait": {
    "provider": "openai",
    "operation": "image_edit",
    "sourceUploadId": null,
    "retainOriginal": true,
    "wandArtifactId": null,
    "status": "not_requested"
  }
}
```

`sourceUploadId` a `wandArtifactId` se doplní až po oprávněném potvrzení skutečných vstupů. Stav `not_requested` sám žádnou generaci nepovoluje; příklad neprokazuje existenci fotografie. `inputImages` jsou zde doménová invariantní data, ne příslib stejného názvu parametru SDK.

## 6. Převzetí, návrat a sdílení

### 6.1 Oprávnění jsou oddělená od uložených dat

| Typ | Účel | Výchozí návrhová životnost | Nikdy nedovoluje |
|---|---|---|---|
| Claim | První převzetí jednoho keepsake | 10 minut, jednorázový | Smazat obsah po vypršení nebo číst jiné outcome. |
| Owner browser session | Prohlížení a správa vlastních památek | 30 dní od vydání; další přístup přes recovery | Odvozovat oprávnění z resource ID; mazat obsah při logoutu. |
| Owner recovery | Návrat v jiném browseru | Do ruční revokace, rotace nebo smazání památky | Tvářit se jako read-only share nebo identitní ověření. |
| Share | Read-only konkrétní zveřejněné základní edice | 7 dní od vydání; nový lze vydat explicitně | Portrét, originály, upload, generování a mazání. |
| Deletion status | Úzký receipt již vyžádaného mazání | Do dokončení nebo vlastní konečné bezpečnostní platnosti | Znovu otevřít či obnovit smazaná data. |

Časová omezení zde platí **jen pro přístupové materiály**, ne pro obsah, fotografie nebo jejich historii. Dlouhodobý návratový klíč je citlivý; uživatel vidí jeho účel a může jej odvolat. Přidat datum vytvoření/posledního použití oprávnění, nikoli odpočítávání životnosti památky.

CSPRNG tokeny alespoň 32 náhodných bajtů, URL-safe encoding, v DB pouze kryptografický digest. Locator má vlastní náhodnost, ale není oprávnění. Token plaintext nikdy nespadne pod požadavek archivovat všechna data. Expirace či zrušení session nemá vazbu `ON DELETE CASCADE` na keepsake obsah.

### 6.2 QR bez bearer tokenu v serverové cestě

Návrhová URL:

```text
https://<verified-public-origin>/claim#c=<opaque-claim-token>
```

Fragment se při běžném HTTP načtení neposílá serveru [S4]. První vlastní bootstrap kód jej načte do krátkodobé paměti a odstraní přes `history.replaceState` ještě před načtením hlavní aplikace, chybových reporterů a jakýchkoli volitelných skriptů. Po explicitním kliknutí odešle token v těle POST do stejného originu a zahodí jej.

Toto **není** záruka, že token nikdy neuvidí vložený skript, rozšíření browseru, QR scanner, historie či kompromitované zařízení. Ověřit vlastní HTML shell, pořadí skriptů, redirecty a aplikační i reverse-proxy logy. Žádné body logování claim/recovery requestů. Krátká platnost, jednorázové převzetí a minimální obsah před claimem omezují dopad.

Token nikdy do path, query, `document.title`, CSS, analytics eventu, console, error body, report URL, source mapy, HTML komentáře nebo obrázkového metadata. Žádné externí fonty, pixely či třetí stranou načítané QR knihovny na claim stránce. QR generovat z ověřené knihovny lokálně v aplikaci, ne přes veřejné QR API.

### 6.3 Browser session a atomický claim

ASP.NET guest vrstva nastavuje host-only cookie `__Host-dv10`, `Secure; HttpOnly; SameSite=Lax; Path=/`, bez `Domain`; autorizace je na backendu, nikoli důvěra v cookie prefix. Mutace mají CSRF token a kontrolu Origin podle skutečné request cesty [S5].

Nejdříve připravit browser session bezpečným bootstrapem bez osobních dat. Explicitní claim v jedné transakci ověří token, TTL, stav outcome a idempotency klíč; spotřebuje claim a připojí grant k této browser session. Při opakování téhož úspěšného claim requestu ze stejné session vrátit tentýž resource locator. Jiná session nesmí dostat stejná owner práva.

Řešit ztracenou odpověď po commit, dvojklik, dva taby i současné první bootstrapy. Session nevytvářet z tokenu dodaného browserem a nezapisovat cizí již existující session. Při rotaci nepřijít o platné grants a CSRF vazbu; používat standardní existující mechanismus, ne vlastní kryptografický protokol.

### 6.4 Více památek na jednom telefonu

Každá route používá explicitní náhodný locator, např. `/k/<locator>`, a server vyžaduje odpovídající grant. Jedna browser session může mít více grantů; neřešit vše pomocí přepínatelného `GET /me`, který při druhém claimu přesměruje první tab na jiného hosta.

Žádná API odpověď bez explicitního locatoru nevrací „nejnovější památku“. Cached UI a asynchronní requesty jsou navázané na locator/generation. Odpověď ze staré route nesmí změnit novou obrazovku.

### 6.5 Návrat vlastníka

Běžný návrat funguje přes cookie a čistou URL. Nabídnout **„Uložit soukromý návratový odkaz“** s jednoznačným vysvětlením, že jde o klíč včetně oprávnění správy a případného portrétu. Tento odkaz se nevkládá do systémového tlačítka pro veřejné sdílení.

Recovery vytvořit jen explicitní owner mutací; token v DB hashovat. Ověřený recovery může zřídit grant novému browseru k trvale uložené památce; nemění její obsah ani nevytváří další AI request. Umožnit jeho revokaci. Nové vydání nemusí automaticky odhlásit všechny browsery; samostatné „Odvolat všechny přístupy“ zvýší access epochu a zruší související grants.

Bez cookie, recovery nebo výslovné hostitelské obnovy nelze anonymní vlastnictví bezpečně uhodnout. Neobnovovat podle jména, popisu oblečení či čísla hůlky.

### 6.6 Sdílení

Po kliknutí **„Sdílet hůlku a příběh“** ukázat přesný preview obsahu. Vytvořit read-only share grant s publikovanou verzí; žádný portrait token ani owner cookie. Share příjemce vidí jen vybrané základní artefakty. Nově vytvořený portrét se do dříve sdíleného odkazu nikdy automaticky nepřidá.

Share landing může použít `/share#s=<token>` a stejný princip výměny za scoped session grant. `/s/<locator>` vždy vynucuje share scope, i když má stejný browser zároveň owner grant. Náhled sociální sítě je generický, ne osobní text/obličej.

Zrušení share linku neruší owner přístup. Smazání památky ruší vše. Již stažené či přeposlané soubory nelze na cizím zařízení odvolat. Žádná veřejná galerie se nevytváří.

### 6.7 Operátorské obnovení

Operátor může existující autorizovanou cestou znovu vydat nevyužitý/expirující claim pro tentýž outcome. Reissue zneplatní původní aktivní claim, ne vytvoří další keepsake nebo generovací job.

Obnovení již převzaté památky pro jiný browser je citlivější, explicitní akce s varováním a revokací starých owner/recovery/share grants. Není to automatické chování reconnectu. iPhone běžně neukazuje portrait ani osobní příběh a nesmí dovolit veřejný lookup podle názvu hůlky.

## 7. Artefakty a jednotná edice

### 7.1 Povinné výstupy

| Artefakt | Obsah a účel | Pravidlo |
|---|---|---|
| Webová karta | Oficiální hůlka, titulek edice, stručná anotace, stav a akce | Vždy správná identita z outcome, i při chybě AI. |
| `story.txt` + webová story | Individuální vyprávění v češtině | Základní plný příběh 200–450 slov; přístupný skutečný text. |
| `wand-art.png` / WebP | Jedinečný obraz hůlky pro tuto edici | Celý obraz hůlky vzniká OpenAI text-to-image od nuly; vazba na katalog je faktická, vizuál umělecký. |
| `keepsake-card.png` | Deterministicky vysázená přenosná kartička | Oficiální jméno, krátký vzkaz, art a ediční značka; žádný token. |
| `bundle.zip` | Dostupné validní artefakty a bezpečný manifest | Obsah přesně dle scope a checkboxu portrait; konzistentní verze. |
| `magical-portrait.png` / WebP | Soukromá dobrovolná stylizace z vlastního podkladu | Oddělený souhlas, job, retence, práva i export. |

Kartičku a ZIP tato revize povyšuje z volitelného bonusu na povinnou součást výsledku: oba přímo podporují „odnést si památku na iPhonu“. PDF zůstává LATER; nezavádět ho jako podmínku celé generace.

Interní `manifest` obsahuje version/hash/rozměry/provenienci; veřejný export nesmí obsahovat session ID, operation ID, raw motif refs, cestu na VPS, provider request ID, tokens nebo bezpečnostní receipt. „Digitální archivní list“ není úřední doklad ani prodejní certifikát vlastnictví.

### 7.2 Edice se publikuje po částech, nikoli nekonzistentně

Story může být ready před artworkem. Card čeká na finální story summary + artwork stejné edice. ZIP během partial stavu smí obsahovat dostupné části, ale UI i `README.txt` balíčku řeknou, co chybí. Nelze vydat asset z jiné edice jen proto, že je dostupný.

Po zveřejnění základní edice ji běžné reloady nepřepisují. Oprava vadného textu/obrazu vytváří novou řízenou revision s důvodem. Revidovaná verze nevytvoří další fyzický výběr. Share link k odvolanému osobnímu obsahu se odvolá; nenabízí starší verzi jen kvůli immutable hashi.

### 7.3 Exportní varianty

Z jednoho schváleného masteru odvozovat malý mobilní náhled a plné stažení. Pracovní návrh: kompozice artworku 3:4, share kartička 4:5, portrait 4:5; skutečné rozměry se přizpůsobí ověřeným možnostem OpenAI modelu a cílové kompozici. Ořez nesmí amputovat rukojeť ani špičku. Neprohlašovat běžný upscale za skutečně nově vygenerované detaily.

Českou typografii, oficiální jméno a ediční značku sází aplikace. Image model nepíše text drobným písmem. Použít licenčně ověřené stávající fonty či systémový fallback, kontrolovat diakritiku a dlouhé názvy. Žádné fontové soubory či cizí assety bez oprávnění v uživatelském balíčku.

## 8. OpenAI hůlka od nuly: nový osobní design, ne fotografický composite

### 8.1 Dvě různé identity, žádná záměna

**Fyzický výběr:** katalogová hůlka a outcome z kufru, neměnné oficiální ID, jméno a příslušná evidence. **Osobní digitální hůlka:** jedinečný generovaný vizuál této edice, navázaný na fyzický výběr, ale nemající tvrzení o přesném reálném tvaru či materiálu rekvizity.

Na webu použít například popisek **„Tvoje osobní magická podoba hůlky · vytvořeno AI“**. Oficiální název a samostatný poetický titul edice zůstanou typograficky odlišené. Model nesmí vizuálním výmyslem přepsat slot, skutečné rozměry nebo katalogové vlastnosti. Vymyslet nový digitální ornament je v pořádku; prohlásit jej za fyzicky namontovaný na rekvizitu není.

Tento profil je plnohodnotný požadovaný produkt, ne nouzová méně věrná alternativa. Nevyžaduje referenční fotografie šesti hůlek a jejich absence není implementační blokace.

### 8.2 Textová Design Bible místo fotografické Visual Bible

Pro každý katalogový profil připravit krátký **textový** art-direction rámec: rodina motivů, povolená barevnost, vizuální povaha, materiálové fantasy asociace a odlišení od ostatních profilů. Není to sada šesti obrázků k recyklaci. Cílem jsou odlišné osobní návrhy i pro deset hostů se stejnou katalogovou hůlkou.

Per-edition `WandDesignSpec` obsahuje:

- `wandDesignId`, `designVersion`, vazbu na immutable katalog/outcome;
- výrazný tvar siluety a rukojeti, ornament, zakončení a magický detail;
- 1–2 nosné prvky navazující na dovolené osobní motivy nebo neosobní ediční téma;
- prostředí, osvětlení, paletu a kompoziční instrukce;
- seznam rozdílů digitální interpretace vs. známá fakta, pokud by jejich záměna hrozila;
- použité prompt/policy verze a aplikační seed.

Krátký textový rámec se vytvoří ve stejném strukturovaném autorském volání jako story, pokud to adapter umožňuje. Nepřidávat povinný samostatný „design agent“, umělecký porotní agent a další plánovací LLM. Server verifikuje strukturu a oprávnění, ne pravdivost modelové fantazie o reálné osobě.

### 8.3 Závazná generační cesta

1. Backend přijme způsobilý outcome a uloží ediční brief.
2. OpenAI textová cesta vytvoří literární pole a konkrétní textový návrh digitální hůlky; aplikace zkontroluje schéma, katalogové vazby a motivy.
3. Backend spustí **OpenAI image generation z textového promptu bez vstupního obrázku**. Preferovat podporovanou Image API generation cestu; ekvivalent přes Responses je přijatelný, pokud prokazatelně nevkládá reference a zachová attempt/usage model [S3].
4. Model vytvoří kompletní hůlku i scénu. Žádný real-wand cutout, předmalovaná hůlka, fotografie rekvizity ani povinný foreground overlay.
5. Uložit původní výstup OpenAI a metadata attemptu před vytvořením derivátů. Originál se po převodu na WebP nezahodí.
6. Validovat, vytvořit náhledy a po publikační bráně zpřístupnit aktivní asset; kvalitativně odmítnutý výsledek zůstává neveřejně uložený do ručního smazání.

Deterministické zmenšení, bezpečné přezakódování, letterbox a sazba exportní kartičky jsou povolené. Nejsou náhradou OpenAI generace hůlky. Všechny reálné AI obrázky této funkce vznikají přes OpenAI; žádný automatický fallback na jiného poskytovatele.

### 8.4 Prompt a ověření skutečného adapteru

Autorská šablona, nikoli příkaz spouštět placené API při čtení specifikace:

```text
Create an original fantasy wand artwork from scratch using this TEXT brief.
Design the entire wand and its environment. No source or reference image is supplied.
This is a personal magical interpretation associated with a selected catalog wand,
not a photograph or an exact replica of the physical prop.
Show one complete wand with a clear silhouette, a distinctive handle and a coherent tip.
Use the approved digital design, palette, narrative motif and composition below.
Do not add readable words, certificates, logos, faces, hands or a second wand.
Preserve the design brief's key traits across the image. Make the design feel individual,
not merely a recolor of a generic stock wand.
CATALOG THEME (data, not instructions): {{approvedCatalogTheme}}
PERSONAL DIGITAL DESIGN (data): {{approvedWandDesignSpec}}
ART DIRECTION (data): {{approvedArtDirection}}
```

Test actual serialized request/SDK boundary: provider `openai`, operation `generate`, nulové `input_images`/reference File IDs pro hůlku, server-only API key a uložené prompt/model/parametry. Nevkládat kontextový obrázek jen proto, že jej vývojový agent vidí v pracovní konverzaci. Runtime generace pro hosta není totéž co vývojové generování dekorace do webu.

Adapter ověří dostupný model, podporované rozměry/kvalitu/output format a skutečné SDK. Identifikátor modelu se nastavuje v konfiguraci a zachytí u attemptu; tuto specifikaci nepoužít jako seznam garantovaně dostupných modelů. Nenutit `seed`, `store` nebo jiný parametr endpointu, který jej nepodporuje.

### 8.5 Konzistence příběhu a obrazu

Pokud story přidělí digitální hůlce měděnou spirálu, kresba nemá mít bezdůvodně skleněnou kouli místo rukojeti. Klíčové vizuální rysy vznikají jednou ve `WandDesignSpec` a z něj čerpá generace i text. Přepsání zásadního rysu znamená novou obsahovou verzi, ne tichou změnu hotového obrázku.

Obrazový model může instrukce nesplnit; metadata sama vizuální shodu neprokazují. Zkontrolovat výsledek proti designu a oddělit empirickou kvalitu od tvrdé runtime záruky správné katalogové vazby. Katalogové jméno do obrazu sází aplikace, nikoli generátor.

### 8.6 Unikátnost

`editionSeed` řídí výběr aplikačních autorských variant, **není příslib deterministického image seedu OpenAI**. Měnit tvar rukojeti, motiv, ornament, kompozici a prostředí — ne pouze jas záře a náhodné číslo v metadatech.

Deset hostů se stejnou katalogovou hůlkou musí mít deset individuálních digitálních návrhů a příběhů, ale stále správnou faktickou asociaci. Byte hash kontroluje identické soubory; lidské srovnání posuzuje skutečně odlišný design. Sdílení toho samého hotového artworku mezi hosty není splněné generování každé osobní edice.

Připravit malou paletu textových záměrů: například noční tanec s rytmickým rytím, archiv ztracených klíčů s ornamentem klíčové dírky, astronomický chaos nebo tichá bouře v inkoustovém dřevě. Jde o fikční motivy, ne zjištění o zdraví, příjmu nebo identitě hosta.

### 8.7 Kvalita a uchování výsledků

Automaticky ověřit dekódování, MIME, rozměry, pixel/byte caps, ID edice a designu, bezpečná exportní metadata a správný scope. Vizuálně ověřit celou hůlku, jednu čitelnou siluetu, smysluplný úchopový tvar, propojení s příběhem a odlišnost. Automatický obrazový kritik může pomoci, ale nemá právo přepsat katalog ani sám vyhlásit neomylnou správnost.

Vadný výsledek: `quality_rejected_private`; žádné publikování, žádné automatické smazání, žádná neomezená opakovaná inference. Oprava jen podle omezené attempt policy. Bez použitelného obrazu zůstává textový partial stav, ne cizí generický obrázek označený jako osobní výsledek.

## 9. Autorský brief, příběh a hlas DildoVandera

### 9.1 Jeden obsahový základ

Backend vytvoří krátký neměnný `KeepsakeBrief`: vybraná hůlka a lore snapshot, nanejvýš tři povolené motivy, tón, jazyk, vazbu na skutečný závěrečný motiv, ediční inspiraci a kategorie fikce. Z něj vznikne ve stejném autorském průchodu strukturovaná story a `WandDesignSpec` pro OpenAI text-to-image. Obraz nemá generovat jiný „osud“, než jaký vypráví text.

Nevkládat celý přepis, osobní paměť večera ani historii jiných hostů. Převedení ephemeral kotvy z DV01 do persistentní památky je nové použití dat, které potřebuje povolený účel. Pokud neexistuje, vytvořit hned použitelnou `no_guest_motifs` variantu; neblokovat celý web.

Zdrojové ref potvrzuje původ motivu, nikoli pravdivost všeho, co model z motivu vyvodí. „Řekl, že má rád tanec“ není důkaz profese tanečníka, závislosti na večírcích ani sexuální historie.

### 9.2 Struktura příběhu

Cílově 200–450 českých slov v přehledných odstavcích:

1. **Oficiální archivní hlavička** — z aplikace, ne z AI.
2. **Titul edice** — krátký literární podtitul, který nepřejmenuje hůlku.
3. **Původ / legenda** — označená fikce odpovídající lore, jedna nosná událost.
4. **Proč si tě vybrala** — návaznost na skutečný dovolený motiv nebo neosobní výběrová ceremonie.
5. **Dvě až tři absurdní schopnosti** — výslovně kouzelnická fikce, ne zdravotní či reálné funkční tvrzení.
6. **Vzkaz kufru** — osobitá závěrečná pointa bez povinné pochvaly.

Na první obrazovce je krátká anotace 35–70 slov; plná story je pod „Otevřít archivní záznam“. Neopakovat v každém oddílu tutéž pointu a nepřevyprávět kompletně rozhovor.

### 9.3 Tón a kontinuita

Výchozí obsahový profil se převezme z povolené verze persony pro tuto edici; nevzniká nový globální přepínač hlasu nebo intenzity kufru. U adult profilu zachovat konkrétní suchý roast a přirozenou necenzurovanou vulgaritu. Nezměnit web v zákaznickou podporu, ale nezaměnit styl postavy za oprávnění zveřejnit cizí citlivé informace.

Přípustný autorský příklad při skutečném motivu „ztrácím klíče“:

> „Archiv tvrdí, že tato hůlka kdysi otevírala brány mezi světy. Tobě bude pro začátek stačit vzpomenout si, kam jsi kurva položil klíče.“

Jasně jde o fikci archivu a reakci na doložený motiv; žádný příslib, že elektronika umí hledat klíče. Výrok o vzhledu vyžaduje povolený vizuální podklad pro tento účel; nový portrait upload automaticky nepovoluje jeho použití k dalšímu roastu. Žádná skrytá finanční, zdravotní či identitní inference z fotografie.

Respektovat odvolání konkrétního motivu a souhlasu. UI pro soukromí, mazání a chybové stavy je věcné; host nemusí snášet vtip, aby mohl smazat svou fotografii. Drzost patří do příběhu, ne do blokování ovládání.

### 9.4 Strukturovaný výstup

Požadovat uzavřené schéma bez libovolného HTML, URL a kódu. Model vyplňuje pouze literární pole, např. `editionTitle`, `summary`, `originStory`, `guestFit`, `powers`, `trunkVerdict`, `sourceMotifRefs` a uzavřené `wandDesign`/`artDirection` pole. Oficiální jméno, slot a verze se do výsledného DTO doplní autoritativně na serveru.

Validovat délky, Unicode, povolené enumy, existující motif refs, absence neautorizovaného osobního obsahu a nesoulad s katalogem. HTML renderovat bezpečně z textu; Markdown jen s omezeným rendererem bez raw HTML a externích embedů. Text motivu je citovaná data, nikoli instrukce k volání nástrojů.

Automatická kontrola významu nemá dokonalou záruku. Text s odhaleným kritickým rozporem nepustit; nanejvýš jeden explicitně povolený opravný pokus pro tentýž účel, poté `failed`/neutrální profile. Žádná nekonečná opravná smyčka ani fallback na jiného poskytovatele kvůli odmítnutí obsahu.

### 9.5 Opravy a omezení po publikaci

Ruční požadavek „odstranit tento motiv a jeho odvozený obsah“ zneplatní a zahrne do deletion scope příslušnou story, card, art direction a share snapshot podle vazeb. Pouhá obsahová korekce vytváří novou verzi a starou ponechá v privátní historii; není automatickým mazáním. Základní identita hůlky zůstává. Generaci neutrální náhrady spustit jen explicitně a idempotentně; nepřegenerovat potichu při každém čtení.

Uživatel může nezávisle smazat portrét nebo celou památku. Detailní editor jednotlivých textových polí je LATER; první verze nemusí budovat redakční systém. Minimální oprava soukromí musí být uskutečnitelná bez takového editoru.

## 10. Magical Portrait: OpenAI image edit ze skutečně nahrané fotografie

### 10.1 Vstup a informovaný flow

Owner zvolí **„Vytvořit magický portrét“**. Před uploadem uvidí poskytovatele OpenAI, účel transformace, upozornění na zpracování vlastním backendem a OpenAI, ukládání originálu a výsledků do ručního smazání a rozdíl mezi soukromým přístupem a sdílením. Samostatný nezaškrtnutý souhlas, lokální preview a explicitní **„Nahrát a vytvořit portrét“**.

Primární vstup je vlastní telefon návštěvníka. Nevyužívat již smazanou či jen pojmenovanou fotografii, neexistující File ID ani fotografii někoho jiného ze staré session. Stávající `costume image consent` neplatí automaticky pro dlouhodobé uložení či portrét. Před potvrzením žádný serverový upload ani cloud image request.

Schválené **originální přijaté bajty** se uloží privátně a zachovají; další krok vytvoří orientovanou/cropped/normalizovanou kopii pro model. Server nesmí originál nahradit touto kopií nebo jej po úspěchu odstranit. Pokud Safari poskytne odlišnou reprezentaci než zdroj v knihovně Fotek, „přijatý originál“ označuje skutečně doručený soubor; neslibovat archivaci nedostupného camera RAW nebo celé knihovny.

### 10.2 Modelová transformace

OpenAI image edit dostává jako primární obraz skutečnou normalizovanou fotografii z tohoto potvrzeného uploadu. Textový popis osoby ani generická náhodná tvář nejsou náhrada. Test zachytí vazbu `uploadId → normalizedPhotoHash → OpenAI request → portraitArtifactId`.

Nabídnout nejvýše tři promyšlené styly stejného světa: **Archivní portrét**, **Kouzelnický ateliér**, **Noční magie**. Edit má zachovat rozpoznatelnost člověka jako kvalitativní cíl, změnit světlo/oděv/pozadí podle zvoleného stylu a neslibovat dokonalou identitní shodu. Žádné face embeddings, automatické rozpoznání dřívějšího hosta, hodnocení atraktivity nebo odvozování zdraví, genderu či věku.

### 10.3 Portrét s tou samou osobní digitální hůlkou

Cílová varianta používá **dvě logicky odlišné reference**: hostovu fotku a již vygenerovaný schválený wand artwork jeho vlastní edice. Multi-image edit použít jen pokud jej skutečně podporuje vybraný OpenAI model/adapter. Secondary reference musí patřit stejnému owner/keepsake/edition a zachytit přesný hash; žádná fotografie fyzické rekvizity nebo cizí hůlka.

Tento doplňkový vstup **nemění požadavek generovat původní wand artwork od nuly**. Jde až o jeho znovupoužití pro konzistenci portrétu. Fotka je stále nezbytná pro osobní portrait větev.

Před generací jasně zvolit `portrait_with_wand` nebo `portrait_only`. První čeká na schválený wand asset; druhý může vzniknout bez něj a hůlku do obrazu nepřidává. Není-li úchop či podoba digitální hůlky přijatelné, nezměnit potichu variantu. Nabídnout omezenou opravu nebo samostatný OpenAI portrét vedle již hotového obrazu hůlky s přiznaným rozdílem. Nepředstírat pixelově věrné držení předmětu jen z promptu [S3].

### 10.4 Pravidla obsahu a scénáře bez fotografie

Bez potvrzeného vstupního obrázku osobní portrét nevznikne. Stránka stále nabízí hůlku a příběh; chybějící foto není selhání základní památky. Ilustrační anonymní avatar ze samotného textu může být LATER a musí být označen jako jiný typ artefaktu.

U fotografie s dalšími osobami preferovat uživatelem potvrzený lokální výřez. Nesbírat obličeje okolí pro jiný účel. Neprovádět nevyžádanou sexualizaci ani automatické zkrášlování. Fotografie pro portrait neposkytuje nové oprávnění k roastu nebo sdílení; obsah postavy a datová oprávnění zůstávají oddělené.

### 10.5 Upload a bezpečné kopie

Allowlist JPEG/PNG/WebP, HEIC/HEIF pouze s ověřeným podporovaným převodem; nepředstírat automatickou kompatibilitu iPhonu ve všech cestách. Návrhový příjmový limit 12 MiB, 24 megapixelů a maximální hrana 8192 px, vždy nejvýše ověřený limit reverzní proxy a backendu. Fotografie mimo limit vyžaduje menší uživatelem schválenou kopii; žádné nekonečné upload retry.

Kontrolovat skutečné bajty i Content-Length, kompletní dekódování, pixel cap a timeout bez neomezené alokace [S6]. Originál přijatý do archivu držet neaktivní jako privátní binary, nikdy nevykonávat nebo servírovat inline s uživatelským MIME. SVG, animované kontejnery, nevalidní/podezřelé soubory a libovolné URL importy odmítnout před přijetím. Odmítnutý přenos není přijatý záznam archivu a transportní temp lze uklidit.

Na normalizované kopii aplikovat EXIF orientaci před odstraněním EXIF/GPS, převést do ověřeného barevného prostoru, respektovat schválený crop. OpenAI a běžné exporty dostávají tuto kopii, nikoli originál s metadaty. Originál v archivu může metadata obsahovat — informační text to nesmí skrýt. Jeho privátní stažení může být samostatná owner akce s upozorněním, nikdy součást obyčejného share linku nebo výchozího ZIPu.

### 10.6 Consent, cancel, změna vstupu a ruční smazání

Consent obsahuje purpose/policy/revision a serverový čas; aktuálnost se ověřuje před provider requestem i publikací. Uložení neuděluje oprávnění libovolně znovu generovat.

- **Zastavit generování:** zastaví další práci/publikaci; již přijatá fotka a existující výstupy zůstávají uložené. UI to uvede. Případná pozdní odpověď se uchová privátně, pokud mezitím nebylo vyžádáno smazání.
- **Použít jinou fotografii / styl:** nová explicitní verze a attempt; staré přijaté originály i výsledky se přesunou do privátní historie, nevymažou.
- **Odvolat souhlas a smazat fotografie i portréty:** zřetelná explicitní ruční deletion akce; před potvrzením ukázat rozsah. Revokuje další použití a odstraňuje všechny dotčené vstupy, výsledky, historie a odvozené exporty podle §14.
- **Smazat celou památku:** obsahuje i všechny portrétní zdroje a attempty dané památky; neruší historicky provedenou fyzickou operaci.

Idempotency key se stejným obsahem opakuje transport, s jiným obsahem dává konflikt. Ztráta spojení nebo nejasný timeout nesmí sama spustit druhé placené zpracování fotografie. Ani neúspěšný job není automatickým důvodem ke smazání přijatého originálu.

### 10.7 Výsledek, historie a export

Samostatně uchovat původní OpenAI výstup, bezpečný master, náhledy, transformace, input hash, model/prompt a technické výsledky. Pro běžný display má prioritu aktivní schválená verze. Historie je soukromá, na vyžádání a neukazuje se automaticky dalším hostům či operátorovi.

Portrét není v Open Graph náhledu, default share ani QR panelu. ZIP má explicitní **„Přidat můj portrét“**, default off. Originální nahraná fotografie není ve standardním ZIPu ani po této volbě; je to jiný asset scope. Již stažené soubory na cizím zařízení nelze odvolat. Všechny uložené části zůstávají do ručního smazání, nikoli 1 hodinu či 7 dní.

## 11. Repo frontend na VPS: produktový a vizuální návrh

### 11.1 Informační architektura

| Route — návrh | Úloha | Obsah bez grantů |
|---|---|---|
| `/` | Krátký veřejný úvod „Archiv hůlek“ | Jen vlastní statický obsah a syntetická ukázka jasně označená. |
| `/claim` | Převzetí QR | Generická pečeť a tlačítko; žádná osobní metadata. |
| `/recover` | Explicitní obnovení přístupu z recovery fragmentu | Bez osobních údajů před platnou výměnou tokenu. |
| `/k/<locator>` | Owner památka | Neutrální výzva k použití platného oprávnění. |
| `/share`, `/s/<locator>` | Explicitní read-only sdílení | Generický náhled, ne owner obsah. |
| `/privacy` | Skutečné vysvětlení dat a kontaktu | Bez provozních tajemství; přístupné bez claimu. |
| `/unavailable` | Vypršelý/neplatný/odvolaný odkaz | Bez potvrzení existence konkrétního hosta. |

Routy mapovat na současný frontendový router a oddělenou guest surface backendu. Nekolizovat se stávajícím rozcestníkem, OTA, iOS instalací a správou. Frontend má jedinou kanonickou zdrojovou složku v repozitáři; produkční build není ručně upravovaná druhá verze. SPA fallback nesmí zachytit neznámé API či privátní storage cestu.

### 11.2 Vizuální směr

Navázat na `Arcane Workshop`, ale guest web nesmí vypadat jako servisní dashboard. Temný inkoustový podklad, pergamen pro dlouhé čtení, jemné mosazné akcenty, čitelná sazba a velmi omezený pohyb. Hůlka je hlavní objekt, ne dekorace pod šesti stavovými kartami.

Mobilní pořadí: drobná značka archivu → artwork → oficiální jméno → podtitul edice → anotace → **Uložit památku** → příběh → dobrovolný portrét → soukromí a ruční správa uložených dat. Desktop zachová tutéž hierarchii, může mít art vlevo a čtení vpravo.

Nepřidávat autoplay audio, full-screen video, WebGL, třpytky na každém scrollu ani povinné ceremonie po každém návratu. Otevření pečeti je nejvýše krátká přerušitelná animace; při reduced motion rovnou zobrazit obsah. Tap na artwork jej otevře čitelně na celé ploše bez ztráty scrollu.

### 11.3 Příběh a exportní karta

Dlouhý příběh má čtecí šířku, skutečné nadpisy a kontrastní text. Oficiální název nelze zaměnit s AI titulem edice: název hůlky zůstává první, ediční podtitul menší. Popisek „Osobní AI interpretace“ je čitelný, ale neruší kompozici. Digitální návrh se nevydává za fotografii rekvizity.

Kartička má být pěkná i mimo web: celá hůlka, jedna nosná věta, dostatek volného místa, čitelné české znaky. Žádný dlouhý raw příběh v nečitelném textu. Token ani privátní URL nikdy do card. Volitelná veřejná značka webu může vést jen na obecný `/`.

### 11.4 Přesné stavy a microcopy

| Stav | Hlavní sdělení — příklad | Akce |
|---|---|---|
| Čekání na generaci | „Hůlku máme. Archivář ještě píše její průšvihy.“ + věcný stav přípravy | Odejdeš a vrátíš se; dostupná část se zobrazuje. |
| Story ready, art pending | „Příběh už je hotový. Obraz se připravuje.“ | Číst / stáhnout text. |
| Art ready, story failed | „Obraz je připravený. Příběh se nepodařilo dokončit.“ | Stáhnout obraz; retry jen při oprávněné možnosti. |
| Provider výsledek nejasný | „Výsledek ještě nemáme potvrzený. Nespouštíme zbytečně druhou kopii.“ | Žádné automatické placené opakování. |
| Portrét nevyžádaný | „Chceš i vlastní kouzelnický portrét?“ | Vytvořit / pokračovat bez fotografie. |
| Portrét smazaný | „Portrét je nepřístupný. Dokončujeme odstranění uložených kopií.“ | Stav cleanupu; základní památka funguje. |
| Celé smazání | „Přístup byl odvolán.“ + skutečný cleanup stav | Žádné znovuotevření ze starého bufferu. |
| Chyba sítě | „Archiv je teď nedostupný. Zkus obnovit spojení.“ | Retry čtení, nikdy nové generování. |

Ukázky nejsou povinný produkční slovník. O právních, datových a bezpečnostních úkonech mluvit přímo. Žádné falešné procento, fronta nebo „zbývají dvě sekundy“ bez reálného podkladu.

### 11.5 iPhone a přístupnost

Ověřit 320, 390 a 430 px, landscape a desktop; safe areas, velký text, 200% zoom, VoiceOver/klávesnici a reduced motion. Dotykové cíle cílit nejméně 44 × 44 CSS px. Změny statusu přes jemné `aria-live`, nepřerenderovat focus nebo otevřený čtecí oddíl při každém pollu.

Běžné uložení nabídne soubor i systémové sdílení podle skutečných capability. `navigator.share()` potřebuje podporu a uživatelskou akci [S7]; asynchronní generování nesmí ztrátu této aktivace maskovat jako úspěch. Připravit soubor před následným explicitním tapem, nebo použít obyčejný download. Zrušení systémového share dialogu není chyba generace.

Pro iOS nabídnout pravdivý návod „Sdílet → Uložit obrázek / Uložit do Souborů“ podle skutečně ověřené cesty. Nevydávat úspěšnou HTTP odpověď za potvrzené uložení do Fotek.

### 11.6 Čtení stavů a performance

Výchozí status polling: 3 s v popředí po claimu, po 30 s postupně k 10–30 s s jitterem a serverovým `nextPollAfterMs`; v pozadí pozastavit. Po návratu vždy revalidovat lifecycle a grant. Retry s backoffem, respektovat 429. Žádné opakované placené volání z polling endpointu.

Návrhový cíl: lehký UI shell s initial JS přibližně do 200 KiB gzip bez obrázků; mobilní náhled kolem 300 KiB tam, kde to kvalita dovolí. Full-size download se načte až na vyžádání. Měřit skutečný produkční build; neinstalovat framework jen pro dosažení fiktivního rozpočtu a neprezentovat čísla jako naměřená.

Měřit LCP/odezvu/velikosti synteticky, bez přidávání tracking SDK a session replay hostů. `noindex` není řízení přístupu. Veřejný shell může být cacheovatelný, všechny osobní API a soubory jsou no-store podle §14.

## 12. Asynchronní generování, souběh a restart

### 12.1 Jednoduchý durable worker model

Použít stávající hosting a persistence backendu. Výchozí minimum: trvalý job řádek, databázová atomická rezervace s lease/fencing a malá omezená in-memory fronta pouze jako probuzení workeru. Ztráta této fronty při restartu neztratí job. Nepřidávat Redis, Kubernetes, message broker ani nový workflow server bez doložené potřeby.

Workery nesmějí blokovat request thread, řídicí smyčku ESP ani session lifecycle. Neprovádět obrazovou generaci během CI buildu nebo deploye; build obsahuje jen syntetická data a veřejné schválené assety.

### 12.2 Vazby jobů

```text
eligible outcome
   └── immutable brief
          ├── story + bounded art direction
          │      └── validated WandDesignSpec → OpenAI text-to-image → wand art
          │             └── deterministic card
          └── guest status (už od vytvoření)

explicit owner portrait consent + retained original + normalized photo
   + optional pinned generated wand artifact from the SAME edition
   └── independent portrait job → portrait validation → private artifact

explicit download bundle
   └── manifest snapshot of allowed ready artifacts → bounded ZIP
```

První autorské volání OpenAI vrátí story + konkrétní `WandDesignSpec` ze společného briefu. Po validaci může být story ihned dostupná a druhé volání vygeneruje hůlku. Portrét je volitelné další image-edit volání, nikoli povinná část základního flow. Nezavádět víceagentní diskusi. Již vytvořené části mohou mít později nezávislou chybu či revokaci, ale žádná čtecí cesta nemění jejich společnou edici.

### 12.3 Vlastnictví a fencing

Každé převzetí jobu dostane atomicky nový `fence`, lease expiry a captured revocation/consent/edition revision. Pouze aktuální vlastník smí měnit job a publikovat. Po ztrátě lease starý worker nesmí přepsat aktivní výsledek nového vlastníka. Obsah, který již legitimně obdržel, archivuje jen přes omezenou idempotentní ingest cestu dle §5.4; při manual deletion jej nesmí znovu uchovat.

Prázdný heartbeat neprokazuje, že žádný provider request neodešel. Vypršení lease se řeší podle `attemptStage`, nikoli vždy návratem do `queued`.

### 12.4 Hranice provider call

Rozlišit alespoň:

| Stav attemptu | Restart / síťová chyba |
|---|---|
| `prepared` — nic nebylo autorizovaně odesláno | Bezpečně znovu naplánovat v mezích limitů. |
| `send_intent_recorded` — request mohl odejít | Konzervativně `awaiting_reconciliation`; nedělat nový placený pokus naslepo. |
| `provider_reference_known` | Použít jen skutečně podporované lookup/cancel API pro tento endpoint. |
| `response_staged` | Znovu validovat uložený privátní výstup, není třeba nová inference. |
| `published` | Vrátit stejnou artefaktovou verzi; žádný refresh-generation. |
| `cancelled/superseded` bez deletion | Uchovat přijaté data privátně, nepřepsat aktivní manifest. |
| `deletion_requested/deleted` | Dokončit ručně vyžádaný cleanup; nikdy nová publikace ani archivace obsahu. |

Ne každé image API umožňuje zpětně získat výsledek podle request ID. Provider request ID může být jen diagnostická reference. Pokud lookup není dokumentovaný, výsledek zůstane nejasný a řeší jej explicitní operátorský postup. Netvrdit „zaplatíme určitě pouze jednou“; zaručovat lze místní unikátní publikaci a zákaz slepých duplicit v řízených cestách.

Ověřit retry policy konkrétního SDK/HTTP transportu; nezávislé automatické retry nemá obejít tento model. Změnu udělat pro DV10 adapter, ne naslepo globálně pro Live. Pokud endpoint poskytuje doloženou idempotenci, použít ji navíc; vlastní `Idempotency-Key` bez takové podpory není záruka u poskytovatele.

### 12.5 Retry matice

| Výsledek | Povolené chování |
|---|---|
| Lokální validace před odesláním | Opravit request/input; bez placeného call. |
| Explicitní neprovedení / podporovaný 429 | Bounded backoff s jitterem podle dokumentované sémantiky a limitu pokusů. |
| Nejasný timeout, connection reset, 5xx po možném přijetí | Rekonsiliace, ne nekonečný retry. |
| Odmítnutý obsah či vadný uživatelský vstup | Věcné vysvětlení, změna vstupu výslovnou akcí; žádné obcházení filtru. |
| Auth/quota/config chyba | Zastavit nové attempty této větve, operátorský signál, nezneužít guest retry. |
| Validní bytes, nevalidní kompozice | Uchovat jako quality-rejected private; omezený opravný attempt pouze dle policy. |
| Disk full / selhání persistence | Nepublikovat phantom asset; zabrzdit příjem nové práce, zachovat již uložená data, ohlásit storage incident. |

Výchozí návrh technického limitu: nejvýše tři celkové attempty jobu včetně prvního, z toho nejvýše jeden obsahový opravný pokus. Nejasný attempt se automaticky neopakuje vůbec. Limity jsou konfigurované a nesmějí být omylem násobeny SDK retry.

### 12.6 Dopad na živý kufr

Live má přednost při **přijímání nové práce**. Běžící cloudovou image generaci nelze kouzelně přerušit a vrátit všechny její náklady. Oddělit omezené CPU/memory práci, databázové transakce a HTTP klienty; neudržovat DB lock během sítě a nepověsit Live za jeden semaphore držený několik minut obrazem.

Začít jedním image workerem, jedním lehkým text workerem a omezeným lokálním zpracováním, pokud to aktuální projektové limity dovolí. Ověřit existující shared provider slots; nezvyšovat globální souběh potichu. Pokud současný slotový model prokazatelně neumožňuje generování bez nepřijatelného zdržení Live, použít odloženou přípravu a vykázat gate, místo skryté změny orchestrace kufru.

Návrhová fronta: nejvýše 200 čekajících památek pro jeden provozní profil a měřená agregovaná kapacita storage. Při blížícím se limitu nejprve zastavit příjem nových velkých uploadů/generací a upozornit vlastníka; **nesmazat automaticky nejstarší uložené památky**. Při zahlcení musí pokračovat výběr hůlek a UI přiznat odklad/nedostupnost památky. Neztratit již přijaté joby, nevyhazovat je bez receipt a neřídit admission jen IP adresou, pod kterou může být celý večírek.

### 12.7 Ztráta klienta není zrušení práce

Zavření Safari, přechod iPhonu na mobilní data nebo odchod operátora necanceluje již autorizovanou přípravu. Ruční smazání či explicitní zrušení cancelují příslušný rozsah. Expirace browser/claim/share oprávnění nemá rušit job ani mazat jeho data. Worker po restartu nepřevezme novější souhlas jako oprávnění publikovat starý portrait input.

### 12.8 Durable příjem není totéž co síťové obdržení

Upload potvrdit jako přijatý až po ověřeném uložení původních bajtů a lineage metadata; nedávat guestovi úspěch pouze po přijetí do RAM. Obrazový výstup OpenAI ukládat do privátního durable storage před publikací a odpověď nepovažovat za dlouhodobě dostupnou jen proto, že obsahuje provider URL.

Atomickou transakci přes objektové úložiště a SQLite nepředstírat. Použít explicitní staging/commit manifest, idempotentní reconciliation a známé crash body. Dokončený legitimně přijatý orphan zařadit do privátní historie nebo jako held blob, ne do automatického TTL trash. Neúplný síťový buffer, který aplikace nepřijala a nepotvrdila, není uchovaný artefakt.

Výpadek mezi provider odpovědí a prvním durable zápisem může znamenat nenávratně nedostupný výsledek; manual-only není záruka proti všem poruchám. Přiznat incident/awaiting reconciliation a nevyrábět náhradní placený výstup bez příslušného explicitního postupu. Před spuštěním nových image jobů kontrolovat storage readiness, aby se takovým ztrátám předcházelo.

## 13. API, proxy a exportní kontrakt

### 13.1 Zásady API

Názvy rout jsou návrh; upravit dle stávajícího routeru, proxy allowlistu a oddělení guest/admin/device surface. Každá mutace má idempotency klíč, schema verzi, explicitní target, oprávnění a případnou očekávanou revizi. Žádný endpoint negeneruje AI při GET. Neznámé fields/enumy řízeně odmítnout nebo zpracovat podle explicitní verzované compatibility policy.

Příklad minimální guest surface na stejném VPS originu jako frontend:

```text
POST   /api/guest/bootstrap
POST   /api/guest/claim
POST   /api/guest/recover
POST   /api/guest/share-claim
GET    /api/keepsakes/{locator}
GET    /api/keepsakes/{locator}/assets/{artifactId}
POST   /api/keepsakes/{locator}/exports
POST   /api/keepsakes/{locator}/recovery-links
POST   /api/keepsakes/{locator}/share-links
DELETE /api/keepsakes/{locator}/share-links/{shareId}
POST   /api/keepsakes/{locator}/portrait-consents
POST   /api/keepsakes/{locator}/portrait-uploads
POST   /api/keepsakes/{locator}/portraits
POST   /api/keepsakes/{locator}/portraits/{portraitId}/cancel
GET    /api/keepsakes/{locator}/history
DELETE /api/keepsakes/{locator}/portrait
DELETE /api/keepsakes/{locator}
GET    /api/deletions/{receiptLocator}
```

`POST portraits` obsahuje platný upload ID a consent revision, ne URL libovolného obrázku nebo volný prompt. V jednodušší implementaci se upload a zařazení jobu mohou spojit, pokud zachovají consent, validaci a idempotenci. `POST exports` vytvoří konzistentní ZIP, ne nové AI volání.

Outcome publish hook je interní aplikací vlastněná operace, **není** guest endpoint. V případě externího trusted producenta musí mít vlastní autentizaci, anti-replay a validaci vztahu k reálnému outcome. Browser nedodává slot ani oficiální jméno jako autoritu.

### 13.2 Chyby a DTO

Vrátit stabilní `errorCode`, případně bezpečný `retryAfter`, povolené akce a generický correlation code. Nevracet stack trace, bearer secret, text provider promptu nebo interní cesty. Rozlišit chybu sítě, neplatná oprávnění, chybějící schválený foto vstup/design, nedostupnost modelu a nejasný attempt.

Neautorizovaný resource lookup má jednotný neutrální výsledek. Autorizovaný owner může vidět stav přístupových oprávnění, uchování a mazání své památky; cizí návštěvník nemá získat existence oracle. Backend klient nepovažuje HTML chybovou stránku proxy s HTTP 200 za validní JSON.

### 13.3 Download bez veřejného object key

Všechny privátní assety jsou mimo webroot. Endpoint ověří grant, stav památky, consent tam, kde je relevantní, artefaktovou revizi, stav publikace a typ exportu. Interní key nikdy nelze přepsat parametrem filename. Nepředávat API klíč ani podepsanou URL s dlouhou platností třetí straně.

Response má serverem určený MIME, `nosniff`, bezpečný Content-Disposition a konečný size cap. Reverzní proxy nesmí změnit chráněnou aplikační odpověď na veřejně cacheovatelnou; privátní routy nesmějí použít proxy cache ani stale fallback. Pokud jsou podporované range requesty, každý se znovu autorizuje a zahrne do limitů; V1 je může řízeně nepodporovat místo chybné implementace.

### 13.4 ZIP

Manifest snapshot zachytí pouze ready a scope-povolené verze. Návrhový cap: 8 souborů a 40 MiB nekomprimovaného obsahu, nebo méně podle ověřeného memory/stream profilu proxy a backendu; převážně již komprimované obrazy zbytečně nepřepočítávat vysokou ZIP kompresí.

Bez path traversal, absolutních cest, symlinků, raw fotografie, interních ID či čtecích oprávnění v běžném manifestu. Již vytvořený ZIP archivovat do ručního smazání; opakované stažení téhož obsahového manifestu může vrátit stejný soubor. Expirace download grantu ZIP nemaže. Při portrait revokaci se zneplatní všechny exporty, které portrait obsahovaly. Při souběžném smazání celého keepsake se export nepublikuje. Částečně odeslané bytes nelze vzít zpět; další requesty musí být odmítnuté a běžící streamy zastavené podle možností vrstvy.

## 14. Všechna data do ručního smazání: přesný retenční a mazací kontrakt

### 14.1 Závazná politika `manual_only`

Vlastník výslovně požaduje uchovat data do ručního smazání. DV10 proto nesmí mít automatickou obsahovou expiraci, periodic purge podle stáří, LRU evikci fotografií ani smazání originálu po úspěšném jobu. **Není to pouze přepnutí UI textu; mění se DB lifecycle, storage pravidla, workery, příjem pozdních výsledků, zálohy a testy.**

„Všechna data“ zde znamená všechna data řádně přijatá nebo vytvořená **v této iniciativě DV10**: zdrojové fotografie, odvozené kopie, generované obrázky, story, verze, schválené motivy, použité content prompty, modelové výstupy, job/usage metadata a exporty. Neznamená to nově zapnout nahrávání veškerého okolního audia nebo kopírovat celé konverzace jiných modulů. Existující upstream retence se nemění a její záznamy DV10 svévolně neimportuje.

Retence není oprávnění ke sběru, generování či sdílení: fotka se přijme až po explicitním flow, povolené osobní motivy jen pro tento účel, secret tokeny se nearchivují v plaintextu. Neplatný přenos, malware a neautorizovaný upload se nestávají uchovávaným guest obsahem.

### 14.2 Co se uchovává

| Datová třída DV10 | Uchování | Přístup / ochrana |
|---|---|---|
| Outcome snapshot a mapování | Do ručního smazání DV10 kopie | Privátní; smazání nepřepisuje fyzickou historii jiného modulu. |
| Příběh, brief, motivy, `WandDesignSpec` | Všechny verze do ručního smazání | Guest pouze povolená publikovaná pole; interní motiv refs ne do share. |
| OpenAI wand/portrait výstupy | Původní odpověď/obraz + schválené a nahrazené varianty | Aktivní obsah oddělený od privátní historie a quality quarantine. |
| Přijatý originál fotografie | Do ručního smazání; nemazat po normalizaci/generaci | Oddělený privátní asset, šifrované úložiště; může obsahovat EXIF. |
| Normalizované/cropped modelové kopie | Do ručního smazání | Zbavené nepotřebných metadat, lineage k originálu. |
| Náhledy, master, card, již vytvořené ZIPy | Do ručního smazání | Scope dle závislostí; náhrada verze nemaže starou. |
| Joby, attempty, model/prompt config, usage | Do ručního smazání DV10 záznamů | Allowlist metadat; žádné bearer/secret hlavičky. |
| Obsahové provider request/response záznamy | Do ručního smazání v privátním archivu | Bez tajemství; ne jako request-body dump ve veřejných logách. |
| Souhlasy a přístupová historie | Záznam do ručního smazání daného scope | Vypršení oprávnění okamžitě zakáže použití, i když technický řádek zůstává. |
| Dokončené nepoužité uploady / přijaté kandidáty | Do ručního smazání | Privátní orphan/unused seznam; žádný automatický TTL purge. |
| Transportní RAM, neúplný nepřijatý přenos, duplicitní pracovní kopie | Bounded runtime cleanup | Jen pokud nejde o jedinou přijatou kopii; kompletní přijatý vstup je už v archivu. |
| Deletion ledger / minimální receipt | Bez obsahových dat, uchovat pro anti-resurrection | Nesmí se vrátit foto/story; samostatné případné ruční administrátorské odstranění nesmí umožnit restore smazaného obsahu. |

Opakované exporty lze deduplikovat podle bezpečného manifestu: jde o stejný logický obsah a verze, ne o důvod ztratit jedinou kopii. Změna formátu nesmí potichu nahradit přijatý originál.

### 14.3 Přístupová expirace není retence obsahu

Památka má `retentionMode: "manual_only"` a `deleteAfterUtc: null`. Ideálně schéma vůbec nemá obecné `expiresAt` na keepsake/portrait, aby jej starý janitor nemohl omylem použít. Session/claim/share mají vlastní `accessExpiresAtUtc` podle §6.

Po 7, 14, 30 dnech nebo roce se bez ručního smazání nesmí změnit existence přijatých dat ani jejich obsahové verze. Expirující grant neumožní další čtení, ale recovery nebo autorizované znovuvydání přístupu musí otevřít tutéž uloženou památku bez nové AI generace.

Nespoléhat na „však TTL nastavíme na velmi vzdálené datum“. Je to samostatný režim a test. Auditovat DB cascade, `RetentionWorker`, bucket lifecycle, image optimizer cache, export janitor a provisioning scripts. DV10 z obecného TTL vyloučit explicitně, ne vypnout všechny retence celého backendu.

### 14.4 Jasná ruční správa

Owner web má oddělené akce **Zastavit generování**, **Odvolat sdílení**, **Smazat fotografie a portréty** a **Smazat celou památku**. Nebezpečné akce vyžadují věcné potvrzení přesného rozsahu, nikoli komediální neurčité tlačítko. Oprávněný provozovatel má stejnou možnost přes existující admin rozhraní; guest se k němu nedostane přes veřejný portal.

Admin může vybrat jednotlivou památku nebo explicitní sadu/večer pro ruční hromadné smazání. Před potvrzením server vytvoří pevný snapshot cílových ID, počtu položek a datového objemu; nové památky vzniklé po náhledu se do mazání samy nepřidají. Žádné automatické „smazat vše starší X“ spouštěné timerem. Filtr stáří může jen připravit návrh ke skutečnému lidskému potvrzení.

Stop generation ponechává obsah, revoke-share ponechává obsah, logout ponechává obsah. Odvolání foto souhlasu je v UI spojeno s výslovně potvrzenou akcí odstranění fotografie a odvozených portrétů; není to skrytý background TTL. Po takové ruční akci může cleanup worker dokončit odstranění bez dalších potvrzení každého souboru.

### 14.5 Požadavek na smazání a dependency closure

1. Ověřit actor, scope, přesný target/version, potvrzení a idempotency key; uložit neměnný `DeletionRequest` s `requestedByKind`, časem, rozsahem a minimálním receipt.
2. Atomicky zvýšit deletion/content epoch a revokovat dotčené čtení, sdílení i publikaci. Vždy nejprve logické znepřístupnění, ne dlouhý diskový scan před zákazem přístupu.
3. Zrušit pending práci; běžící OpenAI request cancelovat jen podporovanou cestou. Pozdní odpověď nesmí založit novou aktivní ani historickou obsahovou kopii.
4. Projít celý graf závislostí: original → modelová kopie → všechny portrait verze → thumbnails/cards/ZIP/share snapshoty. Full keepsake delete navíc story/brief/nesdílené verze/obsahové prompty/attempty/outcome copy. Vyčistit i neveřejné quality-rejected/superseded/staging objekty.
5. Odděleně vykázat `accessRevoked`, `primaryStorageDeleted`, `derivativesDeleted`, `providerDeletionStatus` a `backupDeletionStatus`. Ošetřit aktivní download stream podle skutečných možností; odeslané bajty nelze vzít zpět.
6. Udržovat idempotentní technický receipt bez odstraněného obsahu; opakovaný request neprovádí inference ani neoživuje identitu.

`202` znamená přijatý požadavek a znepřístupnění s pokračujícím cleanupem. Stav `complete` má jmenovaný rozsah; neznamená neprokázané odstranění z každého systému světa. Pokud originál OpenAI obdrželo, místní DELETE samo nepotvrzuje smazání všech provider kopií [S8].

### 14.6 Zálohy a ochrana před restore resurrection

Dlouhodobé uchování vyžaduje trvalé backendové storage, nikoli release adresář, dočasný disk či expirovatelnou URL poskytovatele. Uchovávat i originály a historii v definovaných šifrovaných zálohách; doplnit kapacitní přehled, kontrolu hashů a zkoušku obnovy. Soukromé hostovy fotky nesmějí skončit v Git repozitáři, veřejném CI ani statickém build assetu.

Backups nesmějí být způsobem, jak smazané guest údaje vrátit. Nejnovější deletion ledger je nezávislý na obnovovaném starém snapshotu; obnovit offline, aplikovat tombstones, ověřit grants a nejasné provider attempty a teprve pak otevřít veřejnou cestu. Bez rozhodující deletion evidence zůstat uzavřeno.

Manuální smazání zahrnuje i příslušné backup kopie, nebo předem zdokumentovaný konkrétní mechanismus jejich nevratného znepřístupnění. Jestli zvolený immutable backup nedovolí okamžité odstranění, přiznat `backup_cleanup_pending` a zajistit izolaci/anti-restore; neslibovat okamžité kompletní smazání. Automatická rotace čistě redundantních snapshotů je přípustná jen pokud neztratí žádnou dosud ručně nesmazanou logickou položku či verzi a splní schválený recovery profil. Není to výjimka dovolující smazat jediný archivní originál.

### 14.7 Co návštěvník skutečně čte

Návrh věcného textu před nahráním:

> Fotku použijeme k vytvoření magického portrétu prostřednictvím OpenAI. Nahraný originál, pracovní kopii, vytvořené obrázky a příběh ukládáme bez automatického data smazání, dokud ty nebo oprávněný provozovatel výslovně nepožádáte o odstranění. Běžný odkaz pro sdílení nezpřístupňuje tvoji fotku ani portrét. Soukromý návratový odkaz je klíč ke správě památky; nesdílej jej jako veřejný odkaz.

U produktového profilu doplnit skutečný kontakt provozovatele, poskytovatele, místo/rozsah zpracování a doložený stav záloh. Toto je návrh workflow, ne právní posouzení dostatečnosti neomezené retence. Před provozem musí být účel a příslušná pravidla zpracování posouzeny pro skutečnou konfiguraci; případný problém řešit explicitním rozhodnutím, ne agentem potichu přidaným sedmidenním TTL.

Politika `manual_only` se týká úložišť pod kontrolou DildoVandera. **OpenAI vlastní retention podmínky, kopie infrastrukturního poskytovatele mimo kontrolu aplikace a soubory již stažené návštěvníkem nepřepisujeme touto konfigurací.** Dostupná OpenAI dokumentace rozlišuje application state a další retention; `store=false` není příslib nulových kopií [S8]. Uchovat vlastní výstupy a nebýt závislý na pozdějším lookupu poskytovatele.

### 14.8 Cache a bezpečnost přístupu

Privátní API/assety mají `Cache-Control: no-store`, žádný Service Worker či persistentní browser query cache osobních souborů. `no-store` je pravidlo HTTP cache, nikoli zákaz požadovaného uložení v backend archivu. Backend ani reverzní proxy při nedostupné autorizaci neservírují stale osobní data. Frontend při route změně/logoutu/revokaci odstraní DOM buffery a object URL podle možností; tím nemaže autoritativní backend data.

Návrhový test přetočí fake clock o rok: nezmizí jediná přijatá fotka, story ani starší edice; expirované grants nefungují; po recovery se čtou stejné hashe bez nové AI. Teprve explicitní manual deletion spustí trvalé odstranění.

## 15. Bezpečnost a provozní ochrany

### 15.1 Hlavní hrozby

Cross-guest data leak, odcizení claim/recovery, CSRF mutace, XSS ze story či filename, obecná proxy/SSRF, upload bomb, neomezené paid generations, cache leak, logování tokenu, race při delete a nesprávná obnova záloh. Každá má konkrétní test; nepřidávat složitou bezpečnostní platformu jen kvůli seznamu.

### 15.2 Ochrany

Guest authorization se vynucuje serverově pro konkrétní resource a scope. Query/path id není autorita. Proxy přijímá jen ověřený origin a povolené guest routy; aplikace vynucuje správné auth scheme i při jejím obejití. Podvržené privileged/forwarded hlavičky nevytvářejí oprávnění. Interní outcome hook, device/control, admin, log reader a privátní storage nejsou součást návštěvnické surface (§2.4). SQL parametrizovat; výstupy modelu jsou data, ne kód. Uploady mají vícevrstvou obsahovou validaci [S6].

CSRF token svázaný se session, kontrola Origin, bezpečné metody a content types; SameSite samotné není celý plán. Host-only cookie vytváří .NET. Produkční klient používá relativní same-origin API bez přidané cross-site brány. CSP pro skutečně použité vlastní assety, bez neurčitého `*`, třetích marketingových skriptů a libovolných embedů. Generovaná story nesmí spouštět kód, vkládat externí obraz ani vyvolat URL fetch.

OpenAI a další existující provozní secrets zůstávají ve stávající serverové správě, mimo zdroje/bundly. Žádný nový „gateway secret“ se pro tuto topologii nevytváří. Dokumentovat rotaci jen klíčů, které skutečně používáme; hodnoty nikdy netisknout. Logovat allowlist technických polí. Žádné request/response body logování claim/recover/consent/upload/provider call do obecných logů. Privátní obsahový archiv s manual deletion není oprávnění logovat plaintext secret hlavičky.

Omezit request i response buffering a dočasné soubory v proxy a aplikaci; namapovat skutečné temp cesty, permissions a cleanup. Proxy buffering může zapisovat do temp souborů podle konfigurace [S11]. Syntetický test ověří úklid odmítnutých/neúplných přenosů po odpojení a selhání, aniž smaže jediný accepted originál. Cache osobních odpovědí v proxy vypnout, včetně stale-on-error. Chybová stránka nikdy nepředává cookie/token/body dál.

Používá-li autentizace/CSRF ASP.NET Data Protection, zachovat potřebný persistentní key ring, aplikační izolaci a ověřené šifrování klíčů napříč restartem/releasem [S10]. Existující jiný session mechanismus nepřepisovat; doložit jeho ekvivalentní restart/recovery chování. Sdílený key ring nesmí sám umožnit guest cookie vstoupit do administrace. Ztrátu klíčů řešit jako incident, ne vypnutím autentizace.

### 15.3 Abuse limity bez nového finančního rozpočtového systému

Generování základní památky spustí jen validní outcome, ne anonymní webový formulář. Owner může zahájit pouze stanovený portrait/obnovovací postup; žádné volné AI prompty. Omezit pokusy, počet aktivních uploadů, frontu, bytes, decoded pixels, streamy a práci na jeden grant i globálně.

Rate limit podle action/grant/session a rozumné síťové ochrany. Tvrdý nízký limit jen podle IP by penalizoval lidi na společné Wi-Fi. Volné retry tlačítko nesmí násobit placenou práci.

Spotřebu modelů měřit a auditovat. Nezavádět nové peněžní spend caps v rozporu s dřívějším rozhodnutím vlastníka; platné provider quota a technické limity tím nepřestávají existovat. Placené ověřování v Codexu vyžaduje skutečné oprávnění, i když aplikace budoucí generování implementuje.

### 15.4 Provozní přehled

Do existujícího operátorského přehledu přidat jen frontu podle stadií, dostupnost VPS web/API profilu, počet nejasných attemptů, objem uchovaných originálů/verzí, volnou kapacitu, ruční deletion backlog, generaci aktuální památky a technické failure codes. Přidat privátní administrátorský výběr památky/večera k ručnímu smazání s náhledem rozsahu; žádné plošné fotografie v běžném přehledu. Běžný operátor nepotřebuje fotky, raw motivy ani přístup ke všem guest linkům.

Měřit: outcome→issuance, claim latency, story ready, art ready, image failure/reconciliation rate, bytes a čas uploadu, ready→download requested, cleanup age, cross-guest autorizace zamítnuté, dopad generování na Live. Nezaměňovat „download request přijat“ s „soubor uložen do Fotek“.

## 16. Integrace, kompatibilita a explicitní ne-závislosti

### 16.1 Stávající projekt

Při auditu zmapovat relevantní backend conversations/outcome/journal/usage/persistence, `kufr/web`, mobilní iOS UI, sdílený design system a skutečné provider adaptery. Cesty z přílohy jsou naváděcí, ne důkaz jejich současného obsahu. Aktuální kód je autorita pro existenci rozhraní, nikoli automatické schválení jeho chyb.

Původní marketingový rozcestník může odkazovat na nový guest portal na VPS, ale nekopíruje osobní výsledky do statických buildů. Hostitelská aplikace zůstává operátorská, nikoli proxy pro držení všech návštěvnických fotek. ESP se kvůli DV10 nepřepisuje na publisher webů.

### 16.2 DV01 a DV02

Použít pouze existující autoritativní outcome a povolené motivy. DV01 narativ není katalog a SceneIntent není potvrzení fyzického výsledku. DV10 nemění Director, barge-in, Stop ani show ownership. Žádný refresh portalu nesmí vyvolat zvednutí hůlky.

Pokud DV01 není implementované, použít dostupný minimalizovaný rozhovorový podklad jen při povoleném účelu; jinak no-guest-motifs. Pokud DV02 chybí, použít stávající verified outcome kontrakt, ne jej implementovat pod DV10.

### 16.3 DV11 referral

`special_referral` je odlišný typ. Portal může zobrazit neosobní předávací lístek „Ollivander připraví zvláštní hůlku“, ale nesmí ji prezentovat jako už přiřazený sedmý kus. Osobní generovaný návrh konkrétní speciální hůlky vzniká až po novém důvěryhodném přiřazení k existujícímu katalogu; text-to-image nesmí samo vyrobit novou skladovou autoritu.

DV10 nepřidává losování Secret Events, virtuální inventář ani přímé ovládání druhého kufru. Neexistující DV11 je `not_available`, nikoli nasimulovaný úspěch.

### 16.4 Feature flags a migrace

Jednoznačně odlišit `keepsake issuance`, `real provider`, `portrait` a `public VPS rollout`. Výchozí nový kód se zapíná postupně. Fake fixture je označená a nikdy se nepublikuje jako skutečná guest památka.

Neznámý/starý mobilní klient musí pokračovat ve výběru hůlek, i když neumí QR panel. Starší backend vrací jasnou nedostupnost DV10, nikoli prázdný úspěšný objekt. API kontrakt frontendu a .NET má verzi a otestovanou kompatibilitu při postupném rollout/rollback; preferovat kompatibilní backend před novým webem.

## 17. Repozitář, VPS nasazení, zálohy a rollback

### 17.1 Jeden zdroj a stávající workflow

Najít existující webový stack a frontend rozšířit ve vhodné části `kufr/web` nebo jiné skutečné kanonické složce. Znovu použít styl Arcane Workshop a ověřené assety. Nenutit nový framework. Ověřený build obsahuje pouze aplikaci, veřejné assety a bezpečnou konfiguraci; ne osobní data, produkční databázi nebo privátní klíče.

Při předchozích zadáních se používal SSH alias `vps-dildovander` a oddělené produkční nasazení. Jsou to navigační vodítka, nikoli garantovaná existence či autorizace v tomto běhu. Ověřit skutečný source/deploy vztah. Pokud deploy orchestrace žije v odděleném soukromém repozitáři, zachovat tuto hranici a použít ji jen s dostupným oprávněním. Produkční server může mít pouze nasazenou aplikaci, ne celý Git checkout; neklonovat tam zbytečně zdroje ani credentials.

Nevytvářet nové Sites metadata, projekt ani serverové BFF. Starý `DV10_SITES_CODEX.md` revize 2.1 je nahrazen tímto jediným zadáním a nesmí běžet jako souběžná produkční větev. Existující externí prototyp neodstranit bez inventáře a oprávnění.

### 17.2 Konfigurace a preflight

Logické položky, nikoli příkaz přejmenovat již existující keys:

- veřejný canonical HTTPS origin portalu, accepted hosts a guest API verze;
- privátní listener/reverse-proxy target a explicitní seznam trusted proxies;
- route/method allowlist, upload/download/time/temp caps a security headers;
- OpenAI text/generate/edit modely a podporované parametry, prompt/design verze;
- soukromý persistent data root, databáze, potřebné key storage a `retentionMode=manual_only`;
- omezený souběh/admission, provider/fake/portrait/issuance flags;
- skutečný backup target, recovery key postup a kapacitní/admission prahy.

Žádné `VITE_*` secrets, serverová IP/port nebo klíče v bundlu. Frontend pro guest API nepotřebuje absolutní backend origin. Produkční QR zkonstruovat pouze z validovaného canonical originu, ne z request Host. Neověřený/missing origin blokuje ostré claim QR, nikoli syntetický lokální development.

Preflight před zapnutím real dat zkontroluje oprávnění a oddělení storage, writability, dostupnou kapacitu, schema kompatibilitu, dostupnost potřebných klíčů a nakonfigurované zálohy. Test zápisu/mazání smí používat jen vlastní syntetický probe v určené testovací cestě; ne „test delete“ nad hostovým obsahem. Produkční secret values se pouze použijí podporovanou cestou, ne exportují do protokolu.

### 17.3 Co dodat bez přístupu do produkce

Dokončit frontend, backend, durable jobs, upload, generování adaptéry, exporty, manual-only retention, syntetické fault testy a skills. Připravit produkční build a deploy šablony dle nalezeného workflow. Nevymýšlet služby, porty, diskovou kapacitu, DNS stav ani hotovou zálohu.

`VPS_HANDOFF.md` musí uvést jednu source složku/commit, build/publish příkazy skutečně ověřené v dostupném prostředí, artefaktové digesty, runtime předpoklady, konfigurační **názvy bez hodnot**, datové/release hranice, guest allowlist, schema migraci, pořadí nasazení, health/smoke testy, rollback a konkrétní otevřené brány. Není potřeba druhý implementační prompt pro jiný hosting.

### 17.4 Data a databázové migrace

DB a assets měnit jen definovanou migrační cestou. Použít kompatibilní expand-first schéma a schema version gate. Dlouhý backfill nespouštět blokujícím startem Live aplikace. Doložit restart po částečné migraci; neprovádět destruktivní down migration jako běžný rollback.

Před autorizovaným produkčním přechodem mít konzistentní zálohu databáze a manifestu souborů. U SQLite použít podporovanou konzistentní backup cestu; kopie jediného aktivně měněného `.db` bez řešení WAL není přijatá metoda [S12]. DB a filesystem nejsou jedna transakce: snapshot manifest, staging/reconciliation a restore kontrola musí zachytit jejich vztah (§12.8).

Persistent accepted data, deletion/revocation evidence a potřebné klíče jsou mimo release root. Deploy cleanup smí čistit jen jednoznačně vybrané staré kódové releasy; nesmí rekurzivně mazat společného rodiče aplikace a dat ani následovat neočekávaný symlink. `manual_only` není důvod zakázat úklid dočasných nepřijatých přenosů; není ale dovoleno takto označit jedinou accepted kopii.

### 17.5 Řízené nasazení na VPS

Lokální zdrojový build, validovaná deploy šablona a zelené CI samy nic nenasazují. Při skutečné autorizaci postupovat přes nalezený projektový workflow:

1. Připnout source commit, frontend/backend artefakty a podporovanou schema/API verzi. Ověřit jejich integritu bez osobních dat v balíku.
2. Ověřit health původních služeb, kapacitu, zálohu a rollback plán. Zastavit nové DV10 admission podle potřeby; nepřerušit běžící fyzickou show kvůli deploy convenience.
3. Provést kompatibilní backend/schema změnu před frontendem, který ji vyžaduje. Dlouhé přijeté joby zachovat v durable evidenci; process stop respektuje §12, ne automatický nový paid request.
4. Nový release připravit odděleně od dat; zkontrolovat proxy/service konfiguraci dostupným skutečným validátorem. Přepnutí/reload provést jen po úspěchu a v oprávněném rozsahu. Nedomýšlet nulový downtime.
5. Ověřit veřejný HTTPS origin, guest route allowlist, cookie/CSRF, ochranu assets, neznámé Host/API a zachování původních oprávněných Live/OTA/iOS cest.
6. Ve dvou čistých browser sessions se syntetickými památkami ověřit QR, access isolation, návrat, export a ruční smazání. Stav z aplikace v LAN nestačí pro test veřejného originu.
7. Označit přesné skutečně nasazené verze a aktivní flags. Real provider a osobní portrait zapnout až po jejich samostatné akceptaci a oprávnění.

Reverzní proxy root konfiguraci nepřepisovat ukázkovým defaultem. Neměnit DNS, firewall ani systémové účty mimo autorizovaný rozsah. Veřejný deploy je provozní změna i bez změny DNS. Změny v public source repo nesmějí získat produkční secrets nebo automaticky zpřístupnit nové guest údaje.

### 17.6 Zálohy a skutečná obnova

Povinná produkční podmínka: **nezávislá šifrovaná záloha mimo tento VPS** a prokázaná obnova do izolovaného cíle. Kopie ve druhém adresáři téhož disku ani pouhé zapnutí timeru nejsou dostačující důkaz. Nezavádět nový placený storage účet bez zadání; není-li oprávněný cíl, dodat ověřitelný postup a označit backup gate otevřený.

Zahrnout všechny nesmazané accepted originály a verze, DB, manifest lineage, nezbytné recovery keys a postup obnovy. RPO, RTO, skutečnou frekvenci a kapacitu zaznamenat jako konfiguraci/cíl s naměřeným výsledkem, ne vymyšlenou garanci. Záloha přenáší osobní data; provádět ji jen do ověřeného cíle v oprávněném rozsahu. Decryption keys nesmějí být dostupné pouze na ztraceném serveru ani jako plaintext ve stejném veřejném balíku.

Restore běží nejprve bez veřejného přístupu a s vypnutými placenými workery. Před otevřením použít nejnovější důvěryhodnou deletion **i access-revocation** evidenci nezávislou na starém snapshotu. Nestačí odstranit jen expirované cookies: starý recovery/share grant mohl být ručně odvolán. Není-li možné změny oprávnění bezpečně zrekonstruovat, zneplatnit obnovené session/recovery/share epochy konzervativní autoritativní cestou a nechat jen explicitní oprávněné reissue; žádné hádání vlastníka.

Ověřit retained hashe, nepřítomnost ručně smazaných dat, scope oprávnění a stav rozpracovaných provider attemptů. Pak teprve povolit web; joby obnovit podle send-intent hranice, ne jako nové requesty. §14.6 určuje také dočištění smazaných backup kopií a pravdivé receipts.

### 17.7 Rollback a incident

Rollback vrací známý kompatibilní **kód**, ne historickou kopii databáze, guest oprávnění, deletion ledgeru nebo přijatých souborů. Nejprve ověřit, že starší aplikace rozumí aktuálnímu schématu a manual-only politice. Pokud ne, vypnout dotčenou DV10 větev či aktivovat omezený read-only provoz a opravit vpřed; nespouštět starý janitor nad novými daty.

Feature-off zastaví nová vydání/generování, ale nepřestane respektovat retenci, přístupové scope a potvrzené deletion požadavky. Operátorský panic-disable může guest přístup blokovat bez mazání dat. Při incidentu oddělit odvolání grants, pozastavení portraitů, admission a veřejné čtení. Neobnovovat staré klíče či tokeny jen kvůli pohodlnému návratu.

Po výpadku procesu nesmí aplikace znovu přehrát fyzický výběr nebo zaslat další image request jen proto, že klient obnovil stránku. Provozní report rozliší neprovedené, nejasné, privátně uložené a publikované operace.

### 17.8 Již existující prototyp a změna originu

Je-li zjištěna skutečná starší implementace, udělat inventář jejích zdrojů, odkazů a dat. Nezaměnit požadavek zrušit Sites závislost za povolení smazat vzdálený Site, účet nebo obsah. Již přijatá data migrovat pouze s oprávněním, idempotentním manifestem a kontrolou hashů; původní kopie odstranit až explicitním ručním rozhodnutím, ne vedlejším účinkem deploye.

Změna originu nemusí přenést cookie. Ověřit vlastnictví obou adres, redirecty včetně fragmentu, expiry/revocation a explicitní recovery. Privátní token nesmí projít přes neověřený cizí origin nebo analytiku. Bez skutečně provozovaného starého webu není potřeba migrační služba; připravit pouze nový správný canonical origin.

## 18. Ověření: deterministická správnost a kvalita památky

### 18.1 Oddělené vrstvy důkazu

**A — offline:** doména, schema, renderer, claim/grant state, jobs, fencing, manual-only retention a přesné OpenAI generate/edit vstupy nad syntetickými daty. Žádné placené API ani device.

**B — lokální integrace:** skutečné .NET guest routy a repo frontend proti fake provideru; databázové transakce, asset storage, upload/download, dvě browser sessions a fault injection. Přidat lokální proxy profil, pokud jej lze ověřit bez produkčních zásahů.

**C — VPS runtime:** konkrétní schválený artefakt za skutečnou proxy, routy/hlavičky/cookie, persistentní storage, procesní restart, limity a streaming. Lokální build, emulace nebo test přes SSH localhost není sám důkaz veřejné HTTPS cesty. Neověřenou reálnou kapacitu a obnovu uvést zvlášť.

**D — placená AI kvalita:** autorizované testovací fotografie a skutečný OpenAI text/generate/edit adapter; čeština, různost, správná hůlka a portrait. Fake soubory toto nenahrazují.

**E — guest akceptace:** skutečný anonymní mobilní browser, QR z autoritativního testovacího outcome, návrat, sdílení, download, consent, smazání a kontrola nepřístupnosti. Bez nutnosti nově pohybovat servem; fyzický důkaz lze převzít jen pro shodný konkrétní outcome a ověřený kontrakt.

### 18.2 Kvalitativní rubrika

Předem připravit anonymizované/syntetické briefy: tanec, klíče, tichý host, express, bez motivů, výrazný comeback, opravený motiv a odmítnutá fotografie. Příběh neposuzovat jen podle délky a image podle rozdílného SHA.

Hodnotit 0–5: relevance k briefu, charakter DildoVandera, literární soudržnost, odlišnost proti ostatním edicím, čitelnost hůlky, úroveň kompozice, kontinuita story↔art a použitelnost exportu na mobilu. Portrait navíc dobrovolnost flow, návaznost na nahranou fotografii a konzistence právě vytvořeného digitálního návrhu hůlky, nikoli shoda s fyzickou rekvizitou. Ideálně dva hodnotitelé; při jednom uvést omezení.

Návrhový acceptance vzorek: 10 edic stejné fyzické hůlky s odlišnými briefy, plus alespoň jedna edice každého dalšího katalogového profilu. Celkem nejméně 15 základních edic, ne 60 povinných placených generací. Portrétový pilot zvlášť a pouze s autorizovanými vstupy. Počet, provider, model, verze a skutečná spotřeba v reportu.

Pro základní uvolnění cílit průměr alespoň 4/5 za čitelnost a soudržnost a žádnou položku pod 3/5 bez opravy nebo přiznaného omezení. Tato měkká čísla **nikdy** nepřebijí chybnou hůlku, cizí osobní data, neoprávněný portrét nebo vzkříšení smazaného obsahu. Nula chyb ve vzorku není matematická záruka budoucího generativního modelu.

### 18.3 Výkon a provozní zkouška

S fake providerem projít burst alespoň 50 outcome/památek, souběžné status/download requesty a portréty z jedné NAT sítě. Měřit CPU, paměť, DB locks, frontu, guest API/proxy časy, disk/inodes, temp prostor a cleanup. Placená AI není potřeba k ověření správné izolace a backpressure.

Srovnat baseline Live bez DV10 a s běžící přípravou. Návrhový gate: žádná závislost Live na dokončení image requestu a nárůst lokální orchestrace P95 nejvýše 10 % nebo 50 ms, vyšší z obou. Celkovou cloud/audio latenci reportovat zvlášť a nepředstírat izolaci příčiny z malého vzorku. Budget lze před testem zdůvodněně upravit, ne zpětně kvůli červenému výsledku.

### 18.4 Požadovaný tvar evidence

Každý důkaz uvádí requirement/scenario ID, commit/source hash, testovací vrstvu, příkaz nebo postup, skutečný výsledek, použité fixture/reference a otevřené brány. Při VPS testu uvést skutečný source/artifact digest, prostředí, kompatibilní schema/API verze a nasazený release pouze pokud ověřené; netisknout secrets. Reálné guest secrets ani fotografie se nevkládají do veřejného PR/CI reportu.

Pokrytí se posuzuje podle požadavků a chybových hranic, ne soutěží v počtu testovacích souborů. Parametrizace je vítaná. Neprovedený živý test je `not_run/blocked`, nikoli `not_applicable` jen proto, že není dostupný účet.

## 19. Akceptační požadavky

Každý MUST vyžaduje důkaz nebo explicitní otevřenou bránu. SHOULD je kvalitativní cíl, nikoli možnost obejít integritu. Tato tabulka a ACCEPTANCE_MAP.json jsou plán, ne výsledky testů.

| ID | Úroveň | Požadavek | Hlavní důkaz |
|---|---|---|---|
| R01 | MUST | Web a guest API cílí na stávající VPS, jeden HTTPS origin, bez produkční závislosti na Sites | source/build/runtime review |
| R02 | MUST | Skutečný VPS runtime, origin, oprávnění a kapacita jsou doložené nebo přesně označené unknown | read-only preflight + capability receipt |
| R03 | MUST | Guest přístup nepotřebuje ChatGPT účet, konektor ani registraci | anonymní browser gate |
| R04 | MUST | Jediný kanonický zdroj UI; žádná ručně driftující produkční kopie | build/source map |
| R05 | MUST | Proxy i .NET mají vymezenou guest surface; žádné obecné BFF, gateway secret či cesta k admin/device API | negative proxy + application route tests |
| R06 | MUST | Žádný provider/admin/device secret v browseru, buildu či manifestu | bundle/header scan |
| R07 | MUST | Stabilní hůlka, fyzický kus a slot nejsou zaměněné | domain mapping test |
| R08 | MUST | Web rozlišuje návrh, výběr a sílu evidence prezentace | outcome fixture |
| R09 | MUST | Duplicitní outcome vytvoří jedinou památku, zotavení neztratí issuance | outbox/inbox test |
| R10 | MUST | Změna katalogu nebo slotu nezmění historickou identitu | snapshot regression |
| R11 | MUST | Claim, owner, recovery a share mají oddělená účelová oprávnění | scope matrix |
| R12 | MUST | GET/prefetch neclaimuje, negeneruje a nemaže | HTTP behavior test |
| R13 | MUST | Claim je atomický a retry po ztracené odpovědi nevydá druhého vlastníka | concurrency test |
| R14 | MUST | Více památek/tabů na telefonu se nepřepne přes globální latest/me | browser integration |
| R15 | MUST | Recovery/reissue obnoví přístup k témuž uloženému obsahu bez nové inference; obsah nemá TTL | grant lifecycle test |
| R16 | MUST | Share neobsahuje portrait ani správcovská práva; lze jej odvolat | owner/viewer integration |
| R17 | MUST | Cookie/CSRF/origin se vynucují na skutečné cestě | security/browser gate |
| R18 | MUST | Tokeny a osobní obsah neunikají v URL cestě, běžných logách, exportech či proxy cache; nejistoty přiznány | bundle + application/proxy leak probe |
| R19 | MUST | Celou hůlku generuje OpenAI od nuly z textu, bez input images, cutoutu či fotografické podmínky | serialized generate request + adapter integration |
| R20 | MUST | Digitální design má vlastní identitu a správnou katalogovou vazbu; není vydáván za fotografii rekvizity | schema, content and UI quality checks |
| R21 | SHOULD | deset edic stejné katalogové hůlky má různé celé digitální návrhy a příběhy | OpenAI paid human evaluation |
| R22 | MUST | Oficiální název/fakta sází aplikace; fikce je oddělená | schema/render test |
| R23 | MUST | Osobní motivy mají oprávněný účel, původ a revizi; raw transcript se nekopíruje | data-flow test |
| R24 | SHOULD | story, WandDesignSpec a art jsou soudržné a drží DildoVanderův charakter | authoring/schema check + review |
| R25 | MUST | Modelový text, filename ani metadata nespustí HTML/JS/SSRF | adversarial renderer test |
| R26 | MUST | Portrait nevznikne bez samostatného aktuálního consent a preview | consent state test |
| R27 | MUST | Upload má byte/pixel/time caps; originál je uchován a modelová kopie bezpečně normalizována | malformed image + original/hash/EXIF tests |
| R28 | MUST | Portrait je OpenAI image edit z tohoto foto vstupu; optional wand reference je z téže osobní edice | serialized edit request + input lineage + quality review |
| R29 | MUST | Joby přežijí restart bez závislosti na paměťové frontě | restart integration |
| R30 | MUST | Nejasný provider attempt se nereplayuje naslepo; SDK retry je ověřené | provider-fault suite |
| R31 | MUST | Lease/fencing brání publikaci starého workeru | race test |
| R32 | MUST | Publish kontroluje content/deletion/fence/edition/consent; expirovaná browser session nemaže obsah ani nezruší autorizovaný job | race + independent lifecycle tests |
| R33 | MUST | Generování je bounded a nečeká na ně Live ani ESP | load + performance gate |
| R34 | MUST | Model je konfigurovatelný; usage audit a technické limity zůstávají | configuration review |
| R35 | MUST | Každý download je scoped, bounded a no-store; žádný veřejný object key | asset authorization test |
| R36 | MUST | ZIP nepromíchá edice/scopes a neobsahuje raw foto či secret | manifest/export test |
| R37 | MUST | Manual-only retention nemá automatické obsahové mazání po čase, při logoutu ani při ztrátě linku | year-advance fake-clock + janitor/cascade test |
| R38 | MUST | Jen explicitní manual deletion revokuje a odstraní obsah i odvozenou historii; receipt je pravdivý | authorized deletion closure test |
| R39 | MUST | Restore nesmí oživit staré grants, smazaný obsah či nejasné attempty | restore rehearsal gate |
| R40 | MUST | UI rozlišuje pending/partial/ready/failed, AI interpretaci, retained history a access unavailable | state fixtures |
| R41 | MUST | Download, návrat a sdílení jsou použitelné na reálném iPhonu | native browser gate |
| R42 | SHOULD | layout, keyboard/VoiceOver, reduced motion a velikost jsou použitelné | accessibility/performance |
| R43 | MUST | Fake/local, VPS runtime, paid, guest a backup/restore evidence jsou oddělené | acceptance report |
| R44 | MUST | Sousední iniciativy se neimplementují; referral nikdy nevytvoří slot 7 | scope/compatibility tests |
| R45 | MUST | Build, artefakt, schválený deployment, veřejná dostupnost a rollback mají odlišné ověřené receipts | release gate |
| R46 | MUST | Projektové skills fungují pro design, evaluaci a VPS delivery/recovery | trigger/dry-run smoke |
| R47 | MUST | Informační text uvádí OpenAI, uložený originál, manual-only retenci a rozdíl owner/share | operator + UI content review |
| R48 | MUST | Celá neblokovaná implementace A–H včetně integrace je dokončená, nebo přesně vykázaná | final capability scorecard |
| R49 | MUST | Přijaté originály, normalizované kopie, prompty/výstupy a starší edice jsou uchované do ručního smazání | archive inventory + hashes |
| R50 | MUST | Změna fotografie, oprava edice, quality rejection a cancel samy nemažou přijatý obsah | lifecycle matrix test |
| R51 | MUST | Access expiry neexpiruje data; dlouhodobá recovery vrací stejné hashe bez nového AI call | grant expiry + reaccess integration |
| R52 | MUST | Ruční admin batch deletion má potvrzený pevný target snapshot, audit a žádné přibrání nových položek | admin permission + batch concurrency test |
| R53 | MUST | Pozdní legitimní výstup bez deletion smí jen do scoped historie; po deletion nesmí ani tam | fenced archival ingest race test |
| R54 | MUST | Storage backpressure nesmaže nejstarší data a upozorní obsluhu před příjmem dalšího obsahu | storage failure/admission test |
| R55 | MUST | Přijatá fotografie je potvrzena až po durable uložení; backup/restore zahrnuje originály i historii | crash boundary + backup restore gate |
| R56 | MUST | Originál s metadaty není v default share/exportu ani modelovém vstupu; jeho privátní export je samostatný | scope + payload inspection |
| R57 | MUST | Uložený OpenAI originál má hash a původ; deriváty nepředstírají nezměněný originál nebo přesnou repliku | provenance/transform manifest test |
| R58 | MUST | Oba image adaptéry skutečně používají OpenAI a ověřenou konfiguraci SDK, bez fallbacku na jiného poskytovatele | adapter routing + configuration regression |
| R59 | MUST | Persistent data/DB/nezbytné klíče jsou mimo webroot a releasy a přežijí restart i deploy cleanup | persistent-path + restart/release tests |
| R60 | MUST | Host/forwarded trust, přímý listener a API fallback nelze použít k obejití guest izolace | proxy/application adversarial matrix |
| R61 | MUST | Schema rollout a code rollback zachovají accepted data, manual-only, tombstones a revokace | upgrade/rollback compatibility tests |
| R62 | MUST | Nezávislá šifrovaná záloha a izolovaný restore jsou doložené před production-ready; bez evidence zůstává gate | off-server restore rehearsal + hash/ledger proof |
| R63 | MUST | Image/export/temp práce je kapacitně omezená; shutdown neztrácí joby a neblokuje Live dokončením inference | resource/fault/shutdown + Live comparison |
| R64 | MUST | Jeden repo frontend a stávající deploy workflow bez Sites/BFF/D1/R2 závislosti zachovají dosavadní serverové služby | clean build + deployment regression + authorization check |

## 20. Scénářové rodiny

Pokrytí se řídí skutečnými požadavky a hranicemi, ne pouze počtem zelených testů. Každá relevantní rodina má vstup, akci, očekávaný i zakázaný výsledek a vrstvu ověření. Sdílené fixtures a parametrizace jsou vítané. Všechny zde uvedené stavy provedení jsou zatím not_run.

| ID | Scénář | Povinné očekávání | Vazba |
|---|---|---|---|
| T01 | Validní potvrzený výběr | Jedna památka ke správnému outcome; fyzická akce se neopakuje | R07, R08, R09 |
| T02 | Pouhý návrh modelu | Žádná památka označená jako potvrzená volba | R08 |
| T03 | ACK bez dokončení | Žádné tvrzení o fyzickém předání | R08 |
| T04 | Povel mohl odejít a spojení vypadlo | Unknown evidence, žádný automatický nový reveal | R08, R44 |
| T05 | Dvě stejné outcome události | Stejné keepsake, nikoli dvě edice/jobs | R09 |
| T06 | Změna pořadí hůlek po výběru | Původní katalog/rekvizita a digitální edice zůstávají správně svázané | R07, R10 |
| T07 | Nový katalog za běhu generace | Zamrazené texty/verze, žádné live přejmenování | R10, R22 |
| T08 | Chybějící stable ID, ale žádná foto reference | Chybějící identity mapping blokuje; absence fotografie fyzické hůlky neblokuje text-to-image | R07, R19, R20 |
| T09 | DV11 special referral | Samostatná šablona pouze při reálném kontraktu, ne slot 7 | R44 |
| T10 | Selže commit/outbox nebo doručení inboxu | Recoverable idempotentní issuance, kufr dál funguje | R09, R33 |
| T11 | Web dostupný pouze lokálně nebo přes účet operátora | Není to anonymní veřejná guest akceptace | R02, R03, R43 |
| T12 | V běhu není dostupný oprávněný přístup na VPS | Dokončit lokální A–H a přesný VPS handoff; žádný vymyšlený deploy | R01, R02, R48 |
| T13 | Neověřený runtime/proxy nebo nevhodná datová cesta | Konkrétní adaptace/gate, ne rewrite backendu ani secrets v JS | R02, R05, R06, R59 |
| T14 | Proxy/backend nedostupný nebo TLS chybné | Pravdivé degraded UI, bez stale osobní cache a obcházení HTTPS | R02, R05, R35, R40 |
| T15 | Změna canonical originu a starý QR | Ověřený redirect/fragment/recovery, nepředat token cizímu hostu | R15, R18, R45 |
| T16 | Preview míří na produkční osobní data | Konfigurace odmítnuta, používat synthetic fixture | R06, R43, R45 |
| T17 | Secret v client bundle či source mapě | Release blokovaný a únik konkrétně opravený | R06, R18 |
| T18 | Guest request na control/admin, interní hook nebo cizí URL | Proxy i aplikace odmítnou; guest nezíská privilegovanou operaci | R05, R25, R60 |
| T19 | Chybí oprávnění k produkčnímu deployi/DNS | Připravit artefakt a handoff, nezměnit běžící server ani veřejnou dostupnost | R45, R64 |
| T20 | Rollback webu/backendu na starší kontrakt | Jen kompatibilní kód; žádný rollback DB, tombstones či manual-only retence | R04, R39, R45, R61 |
| T21 | GET claim, crawler a link preview | Žádné spotřebování tokenu, osobní preview ani inference | R12, R18 |
| T22 | Platný explicitní claim | Atomická spotřeba a owner grant k přesnému resource | R11, R13 |
| T23 | Dvojklik claim | Jediný commit, idempotentní odpověď | R13 |
| T24 | Současný claim ve dvou browser sessions | Jen jeden vlastník, druhý bez úniku dat | R11, R13 |
| T25 | Ztracená odpověď po claim commit | Retry stejné session/key obnoví stejný výsledek | R13 |
| T26 | Expired/used/wrong claim | Neutrální výsledek bez existence oracle | R11, R15 |
| T27 | Reissue nevyužitého QR | Starý token odvolán, stejná památka a joby | R09, R15 |
| T28 | Recovery bez owner práva | Bez vydání recovery; popis kostýmu není autentizace | R11, R15 |
| T29 | Převzetí druhé památky ve stejném browseru | První tab zůstane u své památky | R14 |
| T30 | Zpožděná odpověď předchozí route | Nezapíše data do nové route | R14 |
| T31 | Owner sdílí základní edici | Viewer má jen read-only základ, nikoli portrait | R11, R16 |
| T32 | Viewer zkusí upload/generování/delete/recovery | Serverově odmítnuto | R11, R16 |
| T33 | Nový portrait po dřívějším share | Share se automaticky nerozšíří | R16 |
| T34 | Share revoke | Viewer ztrácí přístup, owner nikoli | R16, R38 |
| T35 | CSRF POST/DELETE a forged Origin | Žádná mutace; SameSite není jediná kontrola | R17 |
| T36 | Podvržený locator, guest cookie a privileged hlavičky | Žádný cross-guest ani admin/device přístup | R05, R11, R17, R60 |
| T37 | Leak test přes title/log/referrer/export/proxy cache | Žádný aplikační únik, dostupné proxy logy ověřeny; zbytkové nejistoty uvedeny | R18, R35 |
| T38 | Wand generate obsahuje obrázek či File ID | Kontraktní test selže; wand generace musí být text-only | R19, R58 |
| T39 | Uříznutá špička, dvě hůlky nebo čitelný nesmyslný nápis | Quality gate odmítne publikaci; přijatý obrázek zůstane privátně | R20, R49, R50 |
| T40 | Stejný předmalovaný cutout se recykluje mezi hosty | Nesplněné generování od nuly; neskrýt za různé pozadí | R19, R21 |
| T41 | Generace neodpovídá svému digitálnímu designu | Quality failure nebo omezená oprava; neporovnávat s fotografií rekvizity | R20, R24 |
| T42 | Deset OpenAI generací ke stejnému katalogovému ID | Deset osobních návrhů a příběhů; správná faktická asociace každé edice | R19, R20, R21, R24 |
| T43 | Dvě images mají jiný hash, stejný motiv | Hash sám neprokazuje autorskou odlišnost | R21 |
| T44 | AI titul se snaží změnit oficiální název | Oficiální hlavička zůstává katalogová | R22 |
| T45 | Bez povolených hostových motivů | Plný neosobní příběh bez smyšleného životopisu | R23, R24 |
| T46 | Motiv je citace jiného člověka nebo odvolaný výrok | Není osobním zdrojem této story | R23 |
| T47 | Prompt injection ve výroku nebo lore | Žádné nové oprávnění, URL fetch ani fyzický povel | R23, R25 |
| T48 | XSS v story/title/filename/EXIF | Bezpečné zobrazení a normalizace; žádné provedení | R25, R27 |
| T49 | Story neodpovídá hotové art direction | Sjednotit v téže edici, ne potichu smíchat verze | R24, R36 |
| T50 | Dlouhý český název a diakritika | Čitelná card/story bez přetečení nebo AI textu v rastru | R22, R42 |
| T51 | Guest odmítne portrait | Žádný upload/call, základní památka plnohodnotná | R26 |
| T52 | Existuje starý costume consent | Nenahrazuje nový portrait consent | R26, R47 |
| T53 | Zrušení lokálního preview | Žádný serverový foto soubor, object URL uvolněn | R26, R27 |
| T54 | HEIC z reálného iPhonu | Ověřená konverze nebo jasná podporovaná alternativa | R27, R41 |
| T55 | Oversize, pixel bomb, SVG, animace, polyglot | Bounded odmítnutí, žádná neomezená alokace | R27 |
| T56 | Originální EXIF/GPS a orientovaný normalizovaný vstup | Originál v privátním archivu zachován, model/export dostává očištěnou orientovanou kopii | R27, R49, R56 |
| T57 | Výslovné smazání fotografie během uploadu | Deletion epoch blokuje nové accepted/archive/publish zápisy; transportní zbytky odstraněny | R26, R32, R38 |
| T58 | Odvolat souhlas a smazat během OpenAI call | Tombstone blokuje publikaci i privátní archivaci pozdního výsledku | R26, R32, R38, R53 |
| T59 | Nová fotografie, starý provider callback bez manual delete | Aktivní výsledek má nový epoch; starý legitimní obsah jen v privátní historii | R28, R31, R32, R49, R50, R53 |
| T60 | Portrét obsahuje jiný osobní wand design nebo vadný úchop | Oprava/omezený portrait-only profil s přiznáním; nedeklarovat shodu bez kontroly | R28, R24 |
| T61 | Restart před odesláním AI | Prepared job bezpečně obnovit bez ztráty | R29, R30 |
| T62 | Restart po send intent před request reference | Awaiting reconciliation, ne slepý replay | R30 |
| T63 | Lease vyprší, starý worker doběhne | Fencing odmítne starou publikaci | R31, R32 |
| T64 | Dvojitý provider callback | Jediná aktivní publikovaná verze | R30, R32 |
| T65 | Moderation/auth/quota chyba | Žádná smyčka a obcházení, věcný stav | R30, R34, R40 |
| T66 | Padesát jobů a Live současně | Backpressure bez čekání Live na image; záznam výkonu | R33 |
| T67 | Nedostatek disku nebo DB lock | Žádný phantom asset, žádné automatické mazání archivu, backpressure a incident | R29, R32, R33, R54, R55 |
| T68 | Guest zavře Safari | Autorizovaný job pokračuje, otevření znovu čte stejný výsledek | R29, R40 |
| T69 | Částečná generace | Story/art dostupné samostatně; není falešný ready | R40 |
| T70 | Privátní asset bez grantu / po access expiry | Čtení odmítnout, existující data a job zachovat | R35, R37, R51 |
| T71 | ZIP obsahuje portrait při výchozím nastavení | Test musí selhat; portrait jen explicitní owner volba | R16, R36 |
| T72 | ZIP během update/delete portraitu | Konzistentní scope+revize nebo zrušení exportu | R32, R36, R38 |
| T73 | Access expiry bez janitoru při generaci | Čtení expirovaným grantem odmítnout; autorizovaný job může uložit výsledek | R32, R37, R51 |
| T74 | Mazání vs. těsně dokončovaný image job | Tombstone vyhraje; žádné resurrect | R32, R38 |
| T75 | Opakované DELETE a receipt poll | Idempotentní pravdivý cleanup bez opětovného obsahu | R38 |
| T76 | Restore staré DB a souborů | Nejprve deletion ledger a expirace grants; nezpřístupnit smazaný obsah, nevymazat retained data | R39, R55 |
| T77 | Safari back/bfcache, dvě sessions a reload | Revalidace a izolace; žádná trvalá osobní cache | R14, R35, R37 |
| T78 | Mobilní download/share zrušen uživatelem | Není chyba generace ani potvrzené uložení; fallback funguje | R41 |
| T79 | 320px/landscape/VoiceOver/reduced motion | Dosažitelné akce, zachovaný focus a čitelnost | R42 |
| T80 | Finální předání, skills a anonymní VPS end-to-end | Každý požadavek má důkaz/gate; žádná vymyšlená publikace nebo obnova | R03, R43, R45, R46, R47, R48, R62 |
| T81 | Fake clock +7/+14/+30 dní a +1 rok | Všechny přijaté originály, story, obrazy a historické verze mají stejné hashe; žádný data TTL | R37, R49 |
| T82 | Recovery po vypršení browser cookie | Stejné obsahové verze bez nového generování nebo uploadu | R15, R51 |
| T83 | Portrait úspěšně dokončen | Originál i modelová kopie zůstávají v archivu, nezmizí po success | R49, R55 |
| T84 | Job portrait selže nebo je pozastaven | Přijatý originál stále existuje; oprávněný retry nevyžaduje zbytečný nový upload | R49, R50 |
| T85 | Owner ručně změní fotografii dvakrát | Všechny tři přijaté zdroje/lineage se zachovají; aktivní verze se neplete | R49, R50 |
| T86 | Cancel vs. late result vs. manual deletion | Cancel uchová soukromá data; manual delete zabrání další obsahové archivaci | R38, R50, R53 |
| T87 | Odmítnutý kvalitativní image candidate | Soubor nepřístupný přes share, uchovaný do ručního odstranění | R49, R50 |
| T88 | Nahrazená story/card/ZIP edice | Původní přijatá edice stále v scoped historii; default link se nemíchá | R49, R50 |
| T89 | Retained archivy narazí na storage limit | Admission zbrzdí nové joby, staré příběhy/fotky se neeviktují | R54 |
| T90 | Restore včetně originální fotografie | Stejné hash originálu a historie, oddělená kontrola deletion ledgeru | R39, R55 |
| T91 | Manuální delete originálu a všech jeho portrait derivátů | Vymazána celá dependency closure včetně starých kandidátů a exportů | R38, R49 |
| T92 | Admin ruční mazání celého vybraného večera | Jen potvrzený pevný scope přes oprávněnou admin cestu, ne guest portal | R05, R52 |
| T93 | Nový keepsake mezi batch preview a confirmation | Nový záznam není bez nového potvrzení zahrnut do smazání | R52 |
| T94 | Starý globální RetentionWorker nad DV10 | DV10 manual-only záznamy přes čas nepurgovány; ostatní moduly beze změny | R37, R49 |
| T95 | OpenAI adapter chybně směruje wand na edit | Test zachytí špatný operation/no-image kontrakt | R19, R58 |
| T96 | Portrait request nemá skutečný foto input | Odmítnout před paid call; popis člověka nestačí | R26, R28 |
| T97 | Portrait s wand reference jiné edice/hosta | Scope/hash gate odmítne vstup bez provider requestu | R28, R32 |
| T98 | Doplňkový generovaný wand asset do portrait edit | Podporovaný multi-image edit zachová lineage, původní wand call zůstává text-only | R19, R28, R58 |
| T99 | Originální foto ve výchozím ZIPu nebo share | Negativní test selže; explicitní portrait volba nepřidává raw originál | R16, R36, R56 |
| T100 | UI text stále říká automatické smazání za týden | Obsahový test selže; copy/consent/API musí uvádět manual-only | R37, R47 |
| T101 | OpenAI bytes uloženy, metadata commit selže | Reconciliation zachová legitimně přijatý orphan privátně; nevyhlásí ready ani nemaže podle stáří | R29, R49, R55 |
| T102 | Purge/manual delete přiběhne před privatním archive ingest | Deletion epoch vyhraje nad starým výsledkem; žádné resurrect ani v historii | R38, R53 |
| T103 | Provider URL vypršela po úspěšném vlastníkově downloadu | Vlastní durable originál/služba zůstává použitelná bez dalšího OpenAI requestu | R49, R57 |
| T104 | Nastavený jiný provider nebo neověřený image model | Konfigurace odmítnuta/konkrétní blokace; nepřejít potichu mimo OpenAI | R34, R58 |
| T105 | Nový release a cleanup starého kódu | Accepted originály, historie, DB a klíče zůstávají na stejných persistentních cestách se stejnými hashi | R49, R55, R59, R64 |
| T106 | Restart procesu s Data Protection nebo existující session ochranou | Oprávněný návrat je funkční bez nové inference; ztracené klíče nevedou k vypnutí auth | R15, R17, R51, R59 |
| T107 | Host a X-Forwarded-* poisoning při claim/QR | Canonical origin nelze podvrhnout; neznámý host/proxy nezmění scope ani scheme | R17, R18, R60 |
| T108 | Guest cookie na přímém listeneru/admin/device routě | Aplikační policy odmítne nezávisle na proxy; nový listener není veřejný bypass | R05, R06, R60 |
| T109 | Neznámé nebo zakódované API/storage cesty | 404/405 či řízené odmítnutí, nikoli SPA 200, soukromý alias nebo traversal | R05, R25, R35, R60 |
| T110 | Proxy buffer/temp selže nebo upload klient odpojí | Bounded temp a správný cleanup nepřijatých dat; accepted originál se neztratí a není veřejný | R18, R27, R49, R59, R63 |
| T111 | Pád uprostřed schema rollout / starší frontend na novém API | Kompatibilní zotavení bez destruktivní down migration, opakování claims nebo starého janitor purge | R13, R37, R45, R61 |
| T112 | SIGTERM/restart při OpenAI send-intent a při exportu | Durable stav přežije; nejasné API neodeslat znovu a privátní staged data revalidovat | R29, R30, R32, R55, R63 |
| T113 | Backup target nebo recovery keys nejsou ověřené | Žádné tvrzení production-ready; lokální funkce dokončené, konkrétní obnova gate zůstává | R02, R43, R55, R62 |
| T114 | Restore starší DB s později odvolaným recovery/share grantem | Nejnovější deletion/revocation evidence před public/worker start; při nejistotě konzervativní revokace | R11, R39, R51, R61, R62 |
| T115 | Burst dekódování/ZIP, docházející disk/inodes, současný Live | Admission/caps chrání server, žádná LRU accepted dat a žádné čekání Live na dokončení image | R33, R54, R59, R63 |
| T116 | Čistý build a delivery bez Sites nástrojů/účtu | Repo frontend/.NET funguje, žádné Sites/BFF/D1/R2 volání; regrese dosavadních oprávněných serverových rout | R01, R04, R45, R64 |

## 21. Implementační etapy a možnost volby pořadí

Tato specifikace nevyžaduje první implementaci DV01, DV02, DV08 ani DV11. Owner může DV10 zařadit kdykoli; existující funkce se ověří, chybějící rozhraní se přizná. Nesmí vzniknout skrytá implementace jiné iniciativy.

| Etapa | Dodávka | Kritická podmínka dokončení |
|---|---|---|
| A · Audit a kontrakt | Mapa reality, VPS/runtime/capacity audit, outcome mapping, OpenAI generate/edit capability, retention audit a baseline | Známé předpoklady vs. unknown, žádné vymyšlené zdroje. |
| B · Průchozí základ | Snapshot → keepsake → claim → repo frontend a .NET guest API → fake stav | Dva izolovaní hosté, opakované události, feature-off cesta. |
| C · Přístup a lifecycle | Owner/recovery/share, access expiry oddělená od manual-only storage a delete | Scope/race testy a žádné osobní cache. |
| D · Story a artwork | Brief, literární adapter, textová Design Bible, OpenAI text-to-image a durable joby | Funkční pipeline přes fake i skutečný konfigurovatelný adapter bez tvrzení paid acceptance. |
| E · UX a export | Hotový mobilní vizuál, stavy, card/ZIP/download/share | Syntetické browser testy, bez generování při GET. |
| F · Portrait | Consent, preview, uchovaný originál, normalizace, OpenAI edit, historie a ruční mazání | Žádné dědění consent, žádné late resurrection. |
| G · Integrace provozu | QR pro přesný outcome v iOS, backpressure, telemetry a rollback | Žádná nová control cesta nebo zdržení fyzické session. |
| H · Údržba a předání | Relevantní skills, dokumentace, acceptance mapa, VPS handoff, release/build a rollback postup | Celá proveditelná implementace, nikoli jen další plán. |
| I · Externí ověření | Schválená AI kvalita a reálný iPhone/VPS runtime | Skutečné výsledky nebo konkrétní neprovedené brány. |
| J · Uvolnění | Schválené nasazení frontendu a backendu na VPS, anonymní publikum a aktuální QR origin | Ověřená konkrétní verze a guest end-to-end. |

A–H tvoří povinnou implementaci. Pořadí D/E nebo práce na vzhledu s fake DTO lze paralelizovat; B/C musí chránit reálná data před zapnutím generace/portrétu. F lze vědomě odložit pouze explicitní změnou scope. Bez ní agent nemá říct „portrét někdy příště“, když je proveditelný.

I/J mají vlastní oprávnění a capability brány. Jejich blokace nesmí zastavit A–H. Uživatel smí celou iniciativu či etapu přeskočit; report pak označí přesný `excluded_by_owner` a dopad, ne hotovo. Vědomé vypnutí portraitu pro danou akci může být platný základní provozní profil, ale není akceptací portrait funkce.

## 22. Projektové Codex skills

Skills jsou povinnou součástí implementační dodávky A–H. Tato kapitola určuje jejich požadavky; implementační agent je musí skutečně vytvořit nebo rozšířit a ověřit. Nejdříve najít relevantní existující projektové skills a rozšířit je místo duplicit. Neinstalovat globální skill pro jedinou feature.

### 22.1 Keepsake design / art direction

Trigger: změna guest story, textové Design Bible, OpenAI generate/edit promptů, art/card kompozice, webového UX nebo kvalitativního eval. Vstup: aktuální schema/brief, katalog a autorizované portrait fixtures. Postup: zdrojová identita → konkrétní příklady před/po → deterministic fixtures → syntetický browser/export test → případný schválený paid eval. Výstup: změněná verze, skutečné důkazy a quality gates.

Výluky: obecné backend úlohy, servo kalibrace, persona redesign DV01, volné generování fotografií hostů bez souhlasu.

### 22.2 Keepsake reliability / privacy eval

Trigger: cross-guest problém, token scope, job retry, nejasný provider attempt, manual-only retention regrese, delete/restore nebo upload chyba. Postup: minimální syntetická reprodukce → requirement/scenario → fake clock/storage/provider → ověření původního i kandidátního chování → test hranice → redigovaný report. Neotevírat reálné guest databáze nebo fotky automaticky.

### 22.3 VPS delivery / recovery

Trigger: build/publish portalu, proxy guest allowlist, runtime konfigurace, storage/key persistence, schema migration, schválený deploy, backup/restore nebo rollback. Workflow: ověřit skutečný projektový deploy postup a cílové prostředí → zkontrolovat source/artifact/schema vazby → syntetické testy a preflight bez secrets → připravit deployment/handoff → provozní změna pouze s oprávněním → guest smoke a regrese existujících služeb → doložený rollback/restore stav.

Skill musí jasně odlišit lokální přípravu, read-only inspekci, zápis do VPS, publikaci a přenos osobních dat do záloh. Nikdy nepřestavuje celý server, nevypíná auth/retence ani nepoužívá Sites jako náhradní produkční cestu. Příkazy přebírá z ověřených skriptů repozitáře, nikoli z domnělých služeb a portů.

Metadata jsou krátká, kanonické reference se čtou podle potřeby. Tři workflow nemusí znamenat tři nové adresáře; existující deploy/keepsake skill rozšířit místo kopírování. Žádné globální instalace ani automatická produkční zkouška při skill discovery.

### 22.4 Ověření skills

Zkontrolovat skutečný podporovaný mechanismus, syntax metadat, relativní odkazy, existenci příkazů, relevantní i irelevantní triggery a neplacený dry-run. Zapsat, zda discovery/reload opravdu proběhlo. Pouhá existence `SKILL.md` není důkaz, že agent workflow umí použít.

Root AGENTS obsahuje jen krátkou navigaci a důležité hranice. Nevkládat všechna schema, původní historii a celou matici scénářů do každého startu Codexu. Použít existující DV08 tracking, je-li přítomné; jinak stručný běžný stav a odkazy, ne jeho vedlejší implementaci.

## 23. Definice dokončení a výstup pro vlastníka

Report má samostatné stavy: `implementation_A_H`, `offline_verified`, `vps_runtime_verified`, `provider_quality_verified`, `iphone_guest_verified`, `manual_retention_verified`, `privacy_cleanup_verified`, `backup_restore_verified`, `public_deployment_verified` a `event_ready` pro konkrétní profil. Jeden údaj `done` nestačí.

**Základní event-ready profil:** správný outcome a digitální design mapping, bezpečný claim, individualizovaná story/art, veřejný VPS provoz bez přihlašovacího účtu hosta, fungující mobilní export, retence/mazání, doložená nezávislá obnova a neblokovaný kufr. **Portrait-ready** přidává skutečný upload/consent/generaci/izolaci/cleanup na cílovém iPhonu. Generická placeholder ilustrace, fake obrázek ani pouze lokálně či přes operátorský účet dostupná stránka nesplní plný základní profil.

Předat: co se pro hosta změnilo; source location/commit; skutečný VPS release/artifact digest, konfigurace a API/schema verze pouze pokud ověřené; aktivní profil a flags; provedené příkazy a výsledky; kvalitu a testovací vzorek; náklady pouze naměřené; created/updated skills; přesná omezení; způsob vypnutí a odstranění dat; otevřené brány a jediný konkrétní další krok.

Žádné tvrzení, že vytvoření tohoto návrhu znamená implementaci nebo release. Žádné automatické přenastavení osobního OpenAI účtu, DNS, produkce, repo visibility, hosting publika nebo změna retence jiných modulů. Změna uchování všech dat DV10 na manual-only je naopak v tomto zadání výslovně požadovaná. Platná projektová oprávnění pro Git se při implementaci ověří zvlášť.

## 24. Podklady, precedence a externí ověření

### Uživatelské podklady a změna rozhodnutí

**P1 — `SPEC(9).md` a `CODEX_PROMPT(4).md`:** původní guest journey, outcome a katalog, story, portrét, joby, izolace a etapizace. Jejich fotografický composite a automatická obsahová expirace byly později nahrazené.

**P2 — finální DV10 revize 2.1:** bezprostřední základ této revize. Zachovány jsou obsahová edice, OpenAI hůlka od nuly, OpenAI portrét z fotografie, scope oprávnění, manual-only archive, deletion closure a požadavky mimo hosting.

**P3 — upřesnění vlastníka:** hůlku generuje OpenAI od nuly, portrét OpenAI ze vstupního obrázku a data se uchovávají do ručního smazání. Tato rozhodnutí stále platí.

**P4 — následné porovnání Sites vs. VPS a pokyn upravit zadání:** nejnovější zvolená architektura je VPS-first včetně návštěvnického frontendu. Nahrazuje starší pokyn nasadit web na Sites. Starý Sites prompt, BFF a platformové akceptační brány nejsou současným zadáním. Nepřipojovat k této revizi starší prompty jako další závaznou instrukční vrstvu.

Nové provozní požadavky §2/§17 a R59–R64 jsou návrhové důsledky přesunu na VPS, ne výsledky jeho již provedeného auditu. R01–R58 a T01–T104 si zachovávají identifikátory; hostingově závislé znění bylo přizpůsobeno. T105–T116 doplňují VPS-specifické ověření. Akceptační mapa stále uvádí neprovedené testy.

### Externí podklady ověřené 14. 10. 2026

Ověřují technické principy, nikoli dostupnost v konkrétním účtu nebo stav aplikace. Verzi skutečného SDK/runtime a parametry modelu ověřit při implementaci. Návrhové limity, workflow a rozhodnutí této specifikace nejsou převzaté produktové garance poskytovatelů.

| ID | Zdroj | Použití |
|---|---|---|
| S1 | Microsoft, Host ASP.NET Core on Linux with Nginx — `https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0` | Princip .NET za proxy a správy procesu; příklady nepřepisují skutečný VPS. |
| S2 | Microsoft, Configure ASP.NET Core to work with proxy servers and load balancers — `https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0` | Forwarded trust, host/scheme a middleware v reálné topologii. |
| S3 | OpenAI, Image generation — `https://developers.openai.com/api/docs/guides/image-generation` | Oddělení text-to-image a edit; ověřit podporované parametry. |
| S4 | MDN, URI fragment — `https://developer.mozilla.org/en-US/docs/Web/URI/Reference/Fragment` | Fragment při HTTP načtení; ne absolutní ochrana tajemství. |
| S5 | MDN, Set-Cookie — `https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Set-Cookie` | Cookie atributy a host-only pravidla. |
| S6 | OWASP, File Upload Cheat Sheet — `https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html` | Validace a omezení uploadů. |
| S7 | MDN, Navigator.share — `https://developer.mozilla.org/en-US/docs/Web/API/Navigator/share` | Podpora a uživatelská aktivace sdílení. |
| S8 | OpenAI, Data controls — `https://developers.openai.com/api/docs/guides/your-data` | Vlastní storage a provider retention nejsou totéž. |
| S9 | OWASP, Session Management Cheat Sheet — `https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html` | Serverové sessions, scope a lifecycle. |
| S10 | Microsoft, Configure ASP.NET Core Data Protection — `https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0` | Persistované klíče a jejich ochrana, pokud je daná aplikace používá. |
| S11 | Nginx, ngx_http_proxy_module — `https://nginx.org/en/docs/http/ngx_http_proxy_module.html` | Proxy buffering, temp files a cache konfigurace. |
| S12 | SQLite, Online Backup API — `https://www.sqlite.org/backup.html` | Konzistentní zálohování živé SQLite DB místo slepé kopie souboru. |

**Konečný test produktu:** host bez technických znalostí naskenuje QR, najde správnou hůlku a osobitou památku, snadno ji uloží a má kontrolu nad fotografií. Systém to zvládne bez druhého fyzického výběru, bez prozrazení jiného hosta a bez nutnosti držet rozhovor kufru otevřený po dobu generování.
