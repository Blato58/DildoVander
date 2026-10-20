# Zpracování dat prototypem

Tento technický přehled vychází ze zdrojů a výchozí konfigurace, nikoli z auditu
konkrétního provozu. Před použitím s hosty musí provozovatel sdělit účel,
poskytovatele, skutečně zapnuté režimy a dobu uchování a vyřešit souhlasy,
přístup, mazání a zálohy. Běžné vývojové testy používají syntetická data.

| Data | Zpracování a uchování |
| --- | --- |
| Text dialogu | Explicitní cloudový režim předává text a vybraný kontext OpenAI; fake režim cloudového poskytovatele nevolá. Dokončené výměny mohou obsahovat jména a jiné údaje vyslovené hostem. |
| Paměť večera (legacy) | Samostatná lokální SQLite drží dokončené texty a odpovědi podle zařízení/večera, přežije restart a konec sezení. Nemá časovou expiraci; limit je 2000 výměn nebo 2 MB textu na večer, nejstarší se vytěsňují. Nový večer zachová starší archivy. |
| Volitelná hybridní paměť | Ve zdrojové konfiguraci jsou `Enabled`, `CloudIndexingEnabled` a `LongTermPersonalEnabled` vypnuté. Po aktivaci existuje lokální paměť s výchozí retencí rozhovoru 24 h, motivů 1 den a osobních záznamů 30 dní; cloudové indexování může odeslat obsah do OpenAI vector stores. Osobní paměť vyžaduje zvláštní identitu/oprávnění hosta. Tyto TTL nemažou automaticky starou legacy paměť večera. |
| Mikrofon a hlas | Audio vyžaduje `consent.audio=true`. Zapnuté OpenAI STT/Realtime/GPT-Live režimy odesílají zvuk poskytovateli. Uploady a generované audio backend drží v RAM, nikoli jako WAV v SQLite; výsledky mají omezenou životnost. Dokončený přepsaný/mluvený dialog může vstoupit do obsahové paměti. |
| Volitelná fotografie kostýmu | Jedna vědomě aktivovaná fotografie vyžaduje `consent.image=true`. iPhone Debug používá systémový náhled a potvrzení fotografie; ESP po explicitním obrazovém souhlasu pořídí jeden snímek automaticky, bez stejného náhledu. JPEG má nejvýše 256 KiB / 640 × 640, bez EXIF/GPS; cloudový adaptér jej pošle OpenAI pro nejistý popis kostýmu. Vlastněné buffery se po analýze/chybě vynulují, lookup výsledku expiruje po 120 s. Fotografie se přímo neukládá do journalu, paměti, logů nebo na SD. Vyslovená zmínka o kostýmu může být součástí uložené výměny. |
| Technické logy a audit | Oddělený SQLite log uchovává technické události standardně 24 h / 5000 záznamů; obsah rozhovorů, audio a tajemství do něj nepatří. Journal drží korelace/idempotenci, technický nákladový audit 7 dní a agregáty 90 dní. Log API vyžaduje admin nebo samostatný reader přístup, běžný device token nestačí. |
| Párování telefonu | Místní token, pin certifikátu a potvrzená route jsou v nativním Keychain; ESP ukládá provisionované identity do lokální storage. QR, PoP a pairing materiál jsou citlivé a nepatří do veřejných reportů. |

Samotné ukončení/smazání rozhovoru **nemaže paměť večera**. Autorizované API
mazání aktivního večera vytvoří nový prázdný večer; ostatní archivy zachovává.
Správce musí samostatně řešit starší večery, hybridní záznamy, cloudové indexy
a zálohy. API `no-store`, nulování vlastněných bufferů a SQLite `secure_delete`
nejsou zárukou odstranění kopií v SDK, OS/TLS, zálohách nebo u poskytovatele.
`store=false` v Responses není prohlášením o nulové retenci všech provider služeb.

Neumisťuj skutečné přepisy, fotografie, nahrávky, databáze či diagnostické exporty
s osobními údaji do issues, PR nebo artefaktů. Požadavek na souhlas v protokolu
sám nedokazuje informovaný souhlas člověka. Režimy a doby uchování skutečného
nasazení se mohou lišit od výchozích hodnot a musí je ověřit provozovatel.

Podrobnosti a API: [backend README](../backend/README.md),
[sdílené kontrakty](../contracts/README.md),
[diagnostika](../backend/codex/README.md). Bezpečnostní hlášení vede
[SECURITY.md](../SECURITY.md).
