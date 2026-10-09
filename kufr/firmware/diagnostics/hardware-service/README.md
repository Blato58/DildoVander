# Servisní konfigurace fyzického profilu B

Samostatný ESP-IDF obraz zapisuje pouze namespace `wand_hw` v existující
NVS. Neinicializuje GPIO, PCA, MCP ani audio; nemaže NVS a nemění identitu,
párování nebo TLS klíče. Při chybě NVS nic nemaže automaticky.
Nahrání a změna NVS jsou samostatná servisní akce s explicitní autorizací,
fyzicky vypnutou pohonovou větví a zálohou podle dosavadního USB postupu.
Tímto zadáním se firmware pouze připravuje a ověřuje, nenahrává.

Před zprovozněním postupovat podle [zapojení](../../../docs/zapojeni-potvrzeneho-hardwaru.md).
Zkopírovat `service_config.example.json` do ignorovaného
`service_config.json` a vyplnit šest skutečně změřených kalibrací.
Příklad má všechny flags false a nulové pulzy: **hardware zakazuje**.
Neslouží jako doporučená kalibrace serva. Revizi zvýšit při změně kabeláže
nebo pulzů. Nikdy do tohoto souboru nevkládat credentialy.

| Pole | Co musí být skutečně ověřené |
| --- | --- |
| `profile_b_ok` | B-effects-no-SD, GPIO2/48/38, I2C adresy/pull-upy, audio piny a prázdný SD slot |
| `mute_ok` | Odpojené pull-upy SD→5V, pull-downy, LOW <0,16 V při bootu/resetu/odpojení 3V3 |
| `inhibit_ok` | Nezávislý watchdog a odpojení výkonu, OE LOW/HIGH a reset/brownout/I2C chyba, do 250 ms |
| `positions_ok` | Dva správně ověřené krajní kontakty každé přihrádky, odpojení/zkrat, pasivní držení |
| `rest_us` / `raised_us` | Individuálně změřené odlišné pulzy v rámci 500–2500 µs |
| `travel_ms` | Změřené dokončení včetně 30ms stabilizace snímače, rozsah 20–500 ms |

Z tohoto adresáře:

```sh
python make_config.py service_config.json
pio run
```

Generátor pouze ověří JSON a vytvoří ignorovaný `src/service_config.h`.
Sestavení nic nezapisuje do desky. Nezaměňovat tento obraz za aplikaci:
po samostatně autorizovaném nahrání se konfigurace jednou uloží, gate se
nejdřív uzavře. Při chybě zápisu nespoléhat na stav NVS a ponechat výkon
fyzicky odpojený. Potom obnovit hlavní aplikační
obraz bez vymazání NVS. Neflashovat celé nové NVS partition image přes
stávající provisioning. Automatické vytvoření klíčů ani erase není součást.

Hlavní aplikace po každém bootu ještě vyžaduje místní kontrolu a přidržení
momentového tlačítka 2 s se všemi přihrádkami potvrzenými dole.
Samotné přepsání flags nedokazuje měření nebo akceptaci zařízení.
