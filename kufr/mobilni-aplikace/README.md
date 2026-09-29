# Mobilní ovládání kufru

Současný kód nabízí vývojovou webovou obrazovku se šesti přihrádkami, ručním převzetím, potvrzením výběru/založení a Stop. Přes Vite používá místní fake stav; přes E3.2a vývojový most komunikuje s E2 C++ simulátorem. Oba režimy jsou výslovně označené. Žádný z nich se nepřipojuje k fyzickému kufru. Capacitor a nativní iOS projekt zatím nejsou. Zvolenou architekturu určuje [implementační plán](../docs/implementacni-plan.md), skutečný postup a předání [stav implementace](../docs/stav-implementace.md).

## Cílová role aplikace

První platforma je **iOS/iPhone**: zachovat TypeScript/Vite UI a doplnit Capacitor se Swift adaptéry. Zdrojové soubory vyvíjet na Windows, nativní build/test/podpis provádět vzdáleně na macOS GitHub Actions. Webový režim slouží pro vývoj a simulátor, ne jako finální produkční ovladač.

ESP32 samo komunikuje s backendem; aplikace není cloudový prostředník a nemá AI provider klíč. Místní WSS nabízí ruční výběr 1–6, potvrzení/Stop, snapshot, opravu AI návrhu, text a případně telefonní mikrofon přes stejné device sezení. Offline ovládání musí fungovat bez WAN. Připojení je nejprve pozorovatelské; explicitní převzetí vytvoří ruční lease. Odchod do pozadí nebo ztráta lease zastaví ruční řízení, odpojení pozorovatele neovlivní samostatný kufr.

Nativní adaptér bude vlastnit TLS pin, Bonjour discovery, oprávnění LAN/audio a Keychain. UI nesmí přímo časovat motory ani přijímat AI text jako povel. Konkrétní slices a kontrakty jsou v kapitolách 3–5 plánu.

## Současný lokální webový vývoj

Ve složce `kufr/mobilni-aplikace`, po zajištění kompatibilního Node.js podle lockfile (pro plánovaný Capacitor zvolit připnutou verzi podle plánu):

```text
npm ci
npm run dev
```

Kontrola sestavení:

```text
npm run build
npm test
```

Build provádí `tsc --noEmit && vite build`; test kontroluje nové lease, opakování totožného příkazu, chybějící výsledek a rekonciliaci po odpojení. Ani jedna kontrola neověřuje iOS podpis, LAN nebo fyzický kufr. Ve webovém fake režimu lze převzít řízení, potvrdit výběr nebo založení 1–6 a vyzkoušet Stop. Ztráta viditelnosti stránky uvolní fake lease; rozběhnutý výběr skončí jako nejistý a vyžaduje výslovnou simulovanou kontrolu. Stav se neukládá přes reload stránky.

Pro E2 simulátor nejprve sestavte mobilní web pomocí `npm run build`, spusťte E2 WSS server a pak místní most podle [návodu simulátoru](../simulator/README.md). Stránka na `http://127.0.0.1:5174` vybere simulátor podle odpovědi `/dev-mode`; Vite stránka zůstává na fake adaptéru. Web nevidí bearer token ani certifikát upstreamu. Po ztrátě spojení zablokuje povely a ukáže nedostupný stav; nejasný výsledek předchozího výběru vyžaduje kontrolu mimo UI. Snapshot a ACK simulátoru nejsou důkaz fyzického otevření. Native build zatím nemá příkaz.

## Etapy a vzdálený iOS build

E3a přidá nativní projekt a CI; E10 dokončí native TLS/discovery a skutečnou mobilní zálohu. Úspěšné UI proti E2 simulátoru není hardwarovým ani iPhone ověřením.

Majitel popsal fungující postup Windows → GitHub → macOS/Xcode → testy → dočasný keychain s `.p12` a provisioning profilem ze Secrets → podepsaná IPA artifact → volitelný Release/Pages/feed/OTA/ntfy → úklid vždy. **V tomto checkoutu žádné workflow zatím není.** Při E3a převzít jeho skutečný zdroj a zachovat kompatibilní ověřený postup; nevydávat tento popis za dokončené CI.

Přesné kroky a distribuční podmínky jsou v kapitole 3 plánu. Výchozí doporučení je Ad Hoc pro registrovaný iPhone; artifact není veřejný instalační URL. Feather/AltStore a OTA mají odlišné podmínky instalace/přepodpisu. Certifikáty, hesla, provisioning soubory a tokeny se necommitují. Stabilní identita aplikace a fyzická zkouška aktualizace/Keychain jsou součástí akceptace, nejen úspěšný export IPA. Veřejná publikace je samostatný volitelný krok.

