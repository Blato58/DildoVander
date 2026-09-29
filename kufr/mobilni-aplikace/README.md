# Mobilní ovládání kufru

Současný kód je pouze statická TypeScript/Vite obrazovka. Nemá ovládání přihrádek, síť, testovací simulátor, Capacitor ani nativní iOS projekt. Zvolenou architekturu určuje [implementační plán](../docs/implementacni-plan.md), skutečný postup a předání [stav implementace](../docs/stav-implementace.md). Při dalším zadání začni podle [AGENTS.md](../../AGENTS.md), nikoli novým návrhem transportu.

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
```

Build provádí `tsc --noEmit && vite build`; neověřuje iOS podpis, LAN ani fyzický kufr. Zatím není testovací script ani příkaz pro native build. Nové příkazy doplnit až spolu s jejich zavedením a výsledkem do stavu implementace.

## Etapy a vzdálený iOS build

E3 dodá ruční UI proti simulátoru E2. E3a přidá nativní projekt a CI; E10 dokončí native TLS/discovery a skutečnou mobilní zálohu. Pouhé UI proti fake adaptéru není dokončením E3 ani hardwarovým ověřením.

Majitel popsal fungující postup Windows → GitHub → macOS/Xcode → testy → dočasný keychain s `.p12` a provisioning profilem ze Secrets → podepsaná IPA artifact → volitelný Release/Pages/feed/OTA/ntfy → úklid vždy. **V tomto checkoutu žádné workflow zatím není.** Při E3a převzít jeho skutečný zdroj a zachovat kompatibilní ověřený postup; nevydávat tento popis za dokončené CI.

Přesné kroky a distribuční podmínky jsou v kapitole 3 plánu. Výchozí doporučení je Ad Hoc pro registrovaný iPhone; artifact není veřejný instalační URL. Feather/AltStore a OTA mají odlišné podmínky instalace/přepodpisu. Certifikáty, hesla, provisioning soubory a tokeny se necommitují. Stabilní identita aplikace a fyzická zkouška aktualizace/Keychain jsou součástí akceptace, nejen úspěšný export IPA. Veřejná publikace je samostatný volitelný krok.

