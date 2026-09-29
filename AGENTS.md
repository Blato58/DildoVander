# Pokyny pro tento repozitář

## Projekty a zdroje pravdy

- `kufr/` je současná priorita: firmware ESP32-S3 v `kufr/firmware/` a samostatný mobilní ovladač ve `kufr/mobilni-aplikace/`.
- `obludarium/firmware/` je oddělený projekt PlatformIO pro Arduino Nano; neměň ho při práci na kufru bez zadání, které se ho týká.
- Architekturu, kontrakty, závislosti a kritéria etap určuje [implementační plán](kufr/docs/implementacni-plan.md). Skutečný postup a předání práce vede [stav implementace](kufr/docs/stav-implementace.md). README jsou přehled a návod pro aktuálně existující kód, nikoli alternativní roadmapa.
- Pro zapojení a napájení čti `kufr/docs/efekty-a-napajeni.md`, pro podkladovou pinovou mapu `kufr/docs/pin-mapa-esp32-s3.md` a pro omezení kamery `kufr/docs/kamera.md`. Předběžné profily A/B a bezpečnostní obvod jsou v kapitolách 8 a 10 plánu.

## Jak začít nebo pokračovat v implementaci

- Pokyn „začni/pokračuj implementovat aplikaci podle implementačního plánu“ znamená pokračovat na systému kufru podle tohoto postupu. Pokud uživatel výslovně omezí práci na mobil, backend či firmware, respektuj omezení a vyber příslušný krok se splněnými závislostmi.
- Nejdříve přečti plán (zejména kapitoly 2–5, 8, 11–14), celý stav implementace a README dotčené komponenty. Zkontroluj skutečný kód, konfiguraci, testy a lokální změny; záznam postupu není důkaz, že aktuální checkout odpovídá poslednímu ověření.
- Pokračuj v rozpracovaném kroku, jinak vezmi položku „Další konkrétní krok“. Ověř její závislosti podle plánu. Není-li platná, oprav záznam a vyber nejbližší proveditelný krok. Implementuj malý ověřitelný celek a jeho relevantní testy; neskonči pouhým novým návrhem plánu.
- Chybějící deska, Apple identita nebo cloudový klíč blokují jen závislou část. Použij simulátor/fake adaptéry a pokračuj v nezávislém softwaru. Bez rozhodující informace si ji vyžádej, zaznamenej blokaci a neprohlašuj dotčené kritérium za splněné.
- Stav aktualizuj při převzetí kroku a před každým předáním: přesný rozsah, změněné soubory, provedené ověření a výsledek, neprovedené kontroly, blokace a další krok. „Hotovo“ znamená všechna kritéria etapy z plánu doložená; samotný kód, fake ani úspěšný build nenahrazují HW/iPhone akceptaci.
- Přijatá rozhodnutí znovu neotvírej bez nového důkazu. Nutnou změnu architektury/kontraktu nejprve zdůvodni v plánu, zaznamenej dopad a migraci ve stavu a uprav související README. Nepřepisuj nesplněná kritéria jen kvůli uzavření etapy.
- Běžné lokální implementace a ověření vyplývají z implementačního zadání. V tomto repozitáři majitel výslovně autorizoval commit a push po každém dokončeném a ověřeném implementačním slice; postup je níže. Krátké „pokračuj“ samo neznamená placené AI testy, nákup, publikaci Release/Pages, cloudové nasazení ani nahrání do desky. Kód a kontrolovatelný výsledek připrav před případnou žádostí o chybějící oprávnění.

## Git předání po implementačním slice

- Po ověření slice aktualizuj stav implementace, zkontroluj `git status` a obsah změn, přidej pouze soubory patřící k danému slice a vytvoř commit. Následně bez další žádosti o potvrzení spusť z kořene repozitáře `./scripts/Push-CurrentBranch.ps1`; používá pro tento remote účet `Blato58`, aniž změní aktivní účet GitHub pro ostatní projekty. Do stavu a odpovědi zapiš commit a skutečný výsledek push.
- Cizí nebo nesouvisející úpravy nikdy automaticky nepřidávej. Neúspěšný build/test neopravuj commitem, který tvrdí dokončení; rozpracovaný stav zaznamenej a pokračuj podle konkrétní chyby.
- Pokud chybí remote/upstream, přístup nebo jiný nezbytný údaj, dokonči bezpečný lokální commit a vyžádej jen chybějící údaj. Push neprohlašuj za hotový, dokud není ověřen. Commit/push neopravňuje merge, release, nasazení ani nahrání firmware do desky.

## Přijaté hranice systému

- ESP32-S3 je samostatný klient backendu přes HTTPS a vlastník rozhovoru i fyzické sekvence. Telefon není cloudový prostředník. Backend vrací validovaná data, nikdy přímo neotevírá západku.
- iOS je první mobilní platforma: TypeScript/Vite + plánovaný Capacitor/Swift, vývoj na Windows, nativní build/test/podpis na macOS GitHub Actions. Popsané existující workflow musí být převzato od majitele; v tomto checkoutu zatím není. Webový režim slouží pro vývoj/simulátor.
- AI používá oddělené STT → strukturovaný dialog → TTS; PTT/poloduplex, návrh pouze 1–6 nebo žádný. Aktivace vyžaduje fyzické potvrzení, případně explicitní potvrzení spárované obsluhy v ručním režimu. Offline ruční řízení nesmí potřebovat AI ani WAN.
- Rozdělení vertical slices všech komponent je v kapitole 3 plánu. Backend bude ASP.NET Core se SQLite; dosud neexistuje. Migrace nynějšího Arduino runtime na ESP-IDF patří do E4, neprováděj ji jako vedlejší úklid E1.

## Firmware kufru

- Rozvíjej firmware po vertical slices v `src/features/<funkce>/`. Drž rozhodování funkce a její adaptéry pohromadě; `src/main.cpp` skládá funkce a volá je ze smyčky. Časované efekty řiď bez blokujícího `delay()`.
- `wand_selection/SelectionSequence` je čistá C++ posloupnost pro výběr přihrádky 1–6. Sériový adaptér zatím pouze vypisuje kroky; nevydávej je za ověřené ovládání LED, motorů nebo serv.
- Pinová mapa dodaná k projektu je v `kufr/docs/pin-mapa-esp32-s3.md`. Rozlišuj popisky obrázku, předběžná přidělení externích pinů A/B v plánu a skutečně změřené zapojení; před nahráním firmwaru ověř revizi desky a vlastnosti flash, PSRAM a USB.
- Při zapojování fyzických efektů dodrž návrh napájení: změř 5V výstup měniče před připojením ESP32, omez jas RGB zpočátku přibližně na 25 %, spouštěj nejvýše jeden vibrační motor a neudržuj motor zapnutý při uvolňování západky. Skutečné limity potvrď měřením prototypu.
- Příkazy pro západky přijímej jako explicitní číslo přihrádky 1–6; volný text z AI nesmí přímo řídit aktuátory. Pohony musí mít nezávislou bezpečnostní spínanou větev/watchdog podle plánu. Odpojení pozorovatelského telefonu nesmí zastavit samostatný kufr; ztráta ručního lease musí bezpečně ukončit ruční řízení.
- Přístupové údaje necommituj ani nevkládej do firmware image či mobilního balíčku. Device identita se provisionuje servisně, provider klíč je pouze na backendu; Apple podpisové identity pouze v odděleném CI prostředí.

## Sestavení a ověření

- V `kufr/firmware/` spusť `pio run`. Čistou posloupnost lze testovat hostitelským C++ kompilátorem podle příkazu v `kufr/firmware/README.md`; testy jsou v `kufr/firmware/tests/`.
- V `kufr/mobilni-aplikace/` je lockfile: pro reprodukovatelnou instalaci použij `npm ci`, pro kontrolu `npm run build` (`tsc --noEmit && vite build`). Další příkazy dokumentuj až s jejich skutečným zavedením. Mobilní aplikace zatím nemá komunikaci s ESP32 ani AI službou.
- V `obludarium/firmware/` je sestavovací příkaz `pio run`; projekt je zatím jen startovní kostra.
- `**/.pio/`, `**/node_modules/` a `**/dist/` jsou generované nebo lokální výstupy. Neupravuj je jako zdrojové soubory.
- Po změně kódu ověř nejmenší dotčený projekt a relevantní testy. Pokud chybí hardware pro ověření skutečného efektu, uveď tuto mezeru ve výsledku.
- Při změně pouze dokumentace ověř lokální odkazy, konzistenci etap a rozsah změn; nespouštěj nesouvisející buildy. Do stavu zapisuj pouze skutečně provedené kontroly, u staršího výsledku zachovej datum a rozsah.
