# Kamera OV5640 pro AI kufr

**D155, 9. 10. 2026:** fyzické efekty a audio mají implementovaný
B-effects-no-SD. GPIO38 je GOOD watchdogu, krajní kontakty jsou na MCP4–15;
slot SD zůstává prázdný. Kamerové GPIO jsou rezervované, samotný kamerový
driver a HW akceptace E13 zůstávají otevřené. Nové snímky nákupu OV5640
nedokládají. [Aktuální kabelový plán](zapojeni-potvrzeneho-hardwaru.md).

Majitel 6. 10. 2026 zvolil kameru jako součást kompletního nákupu. Funkční zprovoznění nadále patří do E12/E13; nákup nepředbíhá jejich závislosti. [Aktuální objednávka](../../docs/objednavka-doplneni-kufru-2026-10-06.md) po revizi 8. 10. obsahuje sadu stejné řady desek s OV5640 a MCP23017; microSD s čtečkou je odložená jako volitelné úložiště assetů. Samotný snímek kamery kartu nepotřebuje. Samotná kamera nezaručuje shodu FPC pinoutu: před zasunutím porovnat revize a kontakty, při rozdílu použít párovanou desku ze sady. Hlavní hlas může fungovat bez kamery. Pro hlavní elektroniku je nyní vybraná [deska ESP32-S3 N16R8 s konektorem OV5640](https://dratek.cz/arduino-platforma/232701-vyvojova-deska-esp32-s3-s-konektorem-pro-kameru-ov5640-wifi-bluetooth.html). **Kamera není součástí této varianty.** Pokud budeme chtít kameru koupit hned, existuje také [sada se stejnou deskou a kamerou OV5640](https://dratek.cz/arduino-platforma/232702-vyvojova-deska-esp32-s3-s-kameru-ov5640-wifi-bluetooth-nespajeny-s-pinovou-listou-v-baleni.html). [Dodaná pinová mapa](pin-mapa-esp32-s3.md) označuje 14 GPIO pro konektor kamery; před první instalací je třeba potvrdit revizi desky a vyřešit kolize s audiem, servy, vibracemi a MicroSD.

## Co má umět

Platí [implementační plán](implementacni-plan.md), etapy E12/E13;
aktuální odložení a důkazy jsou ve [stavu implementace](stav-implementace.md).
Základní samostatný provoz ESP32→backend se realizuje bez kamery.

- Po vědomém spuštění rozhovoru může aplikace pořídit jednotlivý snímek a předat AI stručný popis scény.
- Rozpoznání **kostýmu nebo postavy** může změnit repliku: například „Vidím, že dorazil Voldemort“ a tematický výběr hůlky.
- Výsledek je jen vodítko pro dialog. Mobilní ovladač musí umožnit ruční opravu nebo zadání postavy, když kamera kostým nepozná.
- Neodvozovat identitu, věk ani pohlaví z obličeje. Pro repliku použít pouze opatrný popis viditelného kostýmu a dobrovolné zadání/opravu; biometrie není součástí návrhu.

## Možné zdroje obrazu

1. **Kamera telefonu:** nejjednodušší cesta pro první pokusy, protože mobilní aplikace už je v plánu. Vyžaduje povolení kamery v telefonu a funguje jen během aktivního ovládání.
2. **Kamera v kufru:** deska už má konektor pro OV5640. Samotný modul lze doplnit později nebo vzít rovnou s deskou jako sadu. Fyzické umístění objektivu a k němu vedoucí kabel určíme po změření kufru.

ESP32 vlastní rozhovor i ovládání: jeden povolený snímek předá backendu a získá datový výsledek pro dialog. Popis kostýmu je pouze kontext konverzace, **není příkaz firmwaru k pohybu**. Jediným vstupem fyzické sekvence zůstane validované a explicitně potvrzené číslo přihrádky 1–6. Selhání rozpoznání, kamery nebo SD nesmí vyvolat pohyb ani blokovat místní Stop/ruční výběr.

Kamera kufru není prosté zasunutí modulu do hotového profilu A: jeho audio/tlačítko používají část kamerových pinů. E13 vyžaduje přestavbu na profil B podle kapitoly 10 plánu, MCP23017, ověření sdílených LED pinů a souběhu SD, audia, USB/debug i bezpečnostních signálů. Pokud konkrétní revize zapojení nesplní, použít v plánu popsanou alternativu druhého řadiče. MicroSD slouží jen lokálním assetům, ne trvalému archivu snímků/rozhovorů.

## Co zachovat při stavbě kufru

- Před finálním připájením podle [pinové mapy desky](pin-mapa-esp32-s3.md) přidělit piny pro PCA9685, adresovatelný RGB pásek, šest vibračních motorů, audio a tlačítko. Zachovat možnost kamery jen po vyřešení souběhu jejích GPIO s MicroSD a ostatními periferiemi.
- Nechat přístupné místo pro malý objektiv, jeho nenápadný otvor a kabelovou trasu; finální otvor vrtat až po výběru modulu.
- Přepočítat příkon se zapnutou kamerou. Pro rozšířený kufr je navržené [silnější napájení](efekty-a-napajeni.md); proudové špičky změříme na prototypu.
- Při použití kamery ji viditelně označit, snímek pořizovat jen při aktivaci, nezapisovat ho trvale a umožnit funkci vypnout.
