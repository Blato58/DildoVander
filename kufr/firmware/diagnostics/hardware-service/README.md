# Servisní konfigurace fyzického profilu B

Samostatný ESP-IDF obraz zapisuje pouze namespace `wand_hw` v existující
NVS. Neinicializuje GPIO, PCA, MCP ani audio; nemaže NVS a nemění identitu,
párování nebo TLS klíče. Nahrání je samostatně autorizovaná servisní akce
s fyzicky vypnutou zelenou kolébkou a zálohou podle dosavadního USB postupu.
Při této implementaci se žádný obraz do desky nenahrával.

Podle [zapojení](../../../docs/zapojeni-potvrzeneho-hardwaru.md) zkopírovat
`service_config.example.json` do ignorovaného `service_config.json`
a vyplnit šest skutečně změřených kalibrací. Příklad má flag false
a nulové pulzy: **pohony zakazuje**, není doporučenou kalibrací serv.
Revizi zvýšit při změně kabeláže nebo pulzů. Credentialy sem nepatří.

| Pole | Co musí být ověřené |
| --- | --- |
| `profile_b_ok` | B-effects, GPIO2/OE/48, I2C adresy a 3,3V logika, audio piny, přímé napájení pohonů za zelenou kolébkou |
| `camera_verified` | Volitelné boolean, výchozí false. True až po kontrole OV5640 konektoru, RESET/PWDN, napájení, PSRAM, obrazu a nekolidujících pinů podle [kamery](../../../docs/kamera.md); vyžaduje `profile_b_ok=true`. Nenahrazuje jednorázový souhlas návštěvníka. |
| `sd_verified` | Volitelné boolean, výchozí false. True až po změřeném ověření napájení, pullupů a SDMMC 1bit CMD38/CLK39/DATA40 bez JTAG na GPIO39/40; vyžaduje `profile_b_ok=true`. Aplikace kartu pouze čte a nikdy ji neformátuje. |
| `rest_us` / `raised_us` | Individuální odlišné pulzy 500–2500 µs pro oba dorazy |
| `travel_ms` | Změřený pohyb s rezervou v rozsahu 20–3000 ms; čas je odhad polohy, není zpětná vazba |

Z kořene `kufr/firmware`:

```sh
python diagnostics/hardware-service/make_config.py diagnostics/hardware-service/service_config.json
pio run -e diag-hardware-service
```

Generátor validuje JSON a vytvoří ignorovaný
`src/diagnostics/hardware-service/service_config.h`.
Build nic nezapisuje do desky. Servisní obraz nejdřív uloží vypnuté flags,
potom kalibrace a nakonec požadované flags. Při chybě zápisu nespoléhat
na NVS a nechat pohony fyzicky odpojené. Potom obnovit aplikační obraz
bez erase NVS nebo přepisování identity/TLS partition image.

Aplikace po každém bootu vyžaduje ruční kontrolu všech šesti přihrádek
ve spodní poloze a přidržení tlačítka 2 s. Poloha se nesnímá.
