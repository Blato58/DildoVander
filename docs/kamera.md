# Kamera OV5640 pro AI kufr

**D156, 9. 10. 2026:** fyzické efekty/audio používají B-effects:
GPIO2 přímo PCA OE, GPIO38–40 zůstávají rezervované pro SD.
Kamerové piny jsou volné; D157 přidává volitelný driver, fyzická akceptace E13 zbývá.
Majitel potvrdil, že samotná kamera je objednaná a čeká na doručení;
na stávající desce je zatím pouze konektor.
[Aktuální kabelový plán](zapojeni-potvrzeneho-hardwaru.md).

Použijeme majitelovu stávající [ESP32-S3 N16R8 s konektorem OV5640](https://dratek.cz/arduino-platforma/232701-vyvojova-deska-esp32-s3-s-konektorem-pro-kameru-ov5640-wifi-bluetooth.html).
Tato prodejní varianta kameru neobsahuje; majitel ji už objednal samostatně.
[Aktuální objednávka](objednavka-doplneni-kufru-2026-10-06.md)
nepočítá s druhou ESP deskou ani další sadou deska + kamera.
Přesný kód a provedení objednaného modulu zatím nejsou doložené.
Po doručení před zasunutím ověřit FPC, orientaci kontaktů a revizi vůči
stávající desce. [Dodaná pinová mapa](pin-mapa-esp32-s3.md) označuje
14 GPIO pro kamerový konektor; B-effects je ponechává volné.
Funkční zprovoznění a souběh periferií nadále patří do E12/E13.
MicroSD s čtečkou je volitelné úložiště assetů; jednotlivý snímek ani
hlavní hlas ji nepotřebují.

## Co má umět

Platí [implementační plán](implementacni-plan.md), etapy E12/E13;
aktuální postup a důkazy jsou ve [stavu implementace](stav-implementace.md).
Základní samostatný provoz ESP32→backend se realizuje bez kamery.

- Po vědomém spuštění rozhovoru může aplikace pořídit jednotlivý snímek a předat AI stručný popis scény.
- Rozpoznání **kostýmu nebo postavy** může změnit repliku: například „Vidím, že dorazil Voldemort“ a tematický výběr hůlky.
- Výsledek je jen vodítko pro dialog. Mobilní ovladač musí umožnit ruční opravu nebo zadání postavy, když kamera kostým nepozná.
- Neodvozovat identitu, věk ani pohlaví z obličeje. Pro repliku použít pouze opatrný popis viditelného kostýmu a dobrovolné zadání/opravu; biometrie není součástí návrhu.

## Možné zdroje obrazu

1. **Kamera telefonu:** nativní Debug nabízí focení, náhled, opakování a výslovné „Použít fotku“. Povolení fotoaparátu se žádá až při focení. Překreslený JPEG bez metadat (nejvýše 640 px a 256 KiB) zůstává v RAM pro jediný start nejvýše dvě minuty; pozadí, zrušení nebo spotřeba ho odstraní. Webové rozhraní používá pouze syntetický testovací adaptér.
2. **Kamera v kufru:** ESP-IDF používá připnutou komponentu `espressif/esp32-camera` 2.1.8, OV5640, JPEG 640 × 480 a jediný PSRAM framebuffer. `voice.start` přijímá volitelný `imageConsent`, výchozí hodnota je false; fyzické tlačítko snímek nepořizuje. Hlasový panel telefonu nabízí souhlas pro jeden start pouze při `cameraAvailable: true`. Samotný modul je objednaný a čeká na doručení. Fyzické umístění objektivu a k němu vedoucí kabel určíme po změření kufru.

Backend přijímá přímý JPEG přes `POST /v1/conversations/{id}/image`,
výsledek se čte stávajícím pollingem operace. `PUT .../costume-context`
přijímá ruční popis, který má přednost. Nová fotka se přijímá pouze před
zahájením dialogu, s odděleným souhlasem a korelací se snapshotem.
Lokální vizuální kontext a stav operace mají dobu platnosti dvě minuty;
již předaný kontext živého provider spojení může zůstat do jeho ukončení.
Fotografie ani samostatný vizuální kontext se trvale nezapisují. Běžný
přepis dialogu se dál ukládá a může obsahovat slovní zmínku o kostýmu.
Fake režim ověřuje tok dat, nikoli skutečné rozpoznání obrazu.
Chyba fotografie je volitelné varování; rozhovor pokračuje bez ní.

ESP32 vlastní rozhovor i ovládání: jeden povolený snímek předá backendu a získá datový výsledek pro dialog. Popis kostýmu je pouze kontext konverzace, **není příkaz firmwaru k pohybu**. Jediným vstupem fyzické sekvence zůstane validované číslo přihrádky 1–6 přijaté místní bránou ESP32. Selhání rozpoznání, kamery nebo SD nesmí vyvolat pohyb ani blokovat místní Stop/ruční výběr.

Historický profil A má kolize audio/tlačítka s kamerovými piny.
Implementovaný B-effects používá MCP23017 pro tlačítko a přesouvá audio
na 21/41/42/47, LED na GPIO48 a PCA OE na GPIO2. Driver používá SCCB
port 1 na GPIO4/5 odděleně od PCA/MCP I2C portu 0 na GPIO1/14.
E13 ještě vyžaduje ověření souběhu kamery, SD, audia, USB/debug a efektů
na konkrétní revizi stávající desky. MicroSD slouží jen lokálním assetům,
ne trvalému archivu snímků/rozhovorů.

Kamera zůstává vypnutá, dokud servisní konfigurace `wand_hw/camera_verified`
nepotvrdí ověřený konektor, orientaci FPC, pinovou mapu, napájení a souběh.
Použít existující `firmware/diagnostics/hardware-service/make_config.py`
a servisní writer; výchozí i příkladová hodnota je false. Samotný build
ani nastavení tohoto příznaku nejsou doklad fyzické funkce. GPIO reset/PWDN
jsou v adaptéru -1 a musí odpovídat skutečnému modulu před povolením.

## Co zachovat při stavbě kufru

- Před finálním připájením podle [pinové mapy desky](pin-mapa-esp32-s3.md) přidělit piny pro PCA9685, adresovatelný RGB pásek, šest vibračních motorů, audio a tlačítko. Zachovat možnost kamery jen po vyřešení souběhu jejích GPIO s MicroSD a ostatními periferiemi.
- Nechat přístupné místo pro malý objektiv, jeho nenápadný otvor a kabelovou trasu; finální otvor vrtat až po výběru modulu.
- Přepočítat příkon se zapnutou kamerou. Pro rozšířený kufr je navržené [silnější napájení](efekty-a-napajeni.md); proudové špičky změříme na prototypu.
- Při použití kamery ji viditelně označit, snímek pořizovat jen při aktivaci, nezapisovat ho trvale a umožnit funkci vypnout.
