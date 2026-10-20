# Architektura a hranice prototypu

Tento přehled popisuje současné zdroje. Kontrakty, závislosti etap a kritéria
akceptace určuje [implementační plán](implementacni-plan.md);
ověření konkrétních sestav vede [stav implementace](stav-implementace.md).

| Komponenta | Odpovědnost | Zdroj a kontrakt |
| --- | --- | --- |
| ESP32-S3 / ESP-IDF | Samostatný HTTPS klient, rozhovor, místní bezpečnostní brány, fyzická sekvence a servisní provisioning | [Firmware](../firmware/README.md) |
| ASP.NET Core / SQLite | Device auth, dialog/hlas/fotografie, validované návrhy, idempotence, technický audit a oddělená obsahová paměť | [Backend](../backend/README.md), [OpenAPI](../contracts/backend-openapi-v1.json) |
| TypeScript / Capacitor / Swift | iOS místní WSS ovladač se zabezpečeným BLE provisioningem; samostatná backendová diagnostika | [Mobil](../mobilni-aplikace/README.md) |
| C++ / Node simulátor | Fake fyzické porty, místní WSS facade a pozorovací konzole | [Simulátor](../firmware/simulator/README.md) |
| Python / CadQuery a web | Parametrická vložka F2, ověřované exporty a Workshop viewer | [Mechanika](../mechanika/README.md), [web](../web/README.md) |

Telefon není cloudový prostředník ESP32. Backend přímo nespíná pohony: návrh
obsahuje jen přihrádku 1–6 nebo žádnou a místní brána kontroluje aktuální
stav i bezpečnost. Ruční místní řízení nevyžaduje AI nebo WAN. Odpojení
pozorovacího telefonu nezastavuje samostatný kufr; ztráta ručního lease musí
ukončit ruční řízení bezpečně.

[Sdílené kontrakty a fixtures](../contracts/README.md) vymezují identifikátory,
generace, expirace a formáty control/media/backend zpráv. Firmware je členěn
do `src/features/`, backend do `Features/`; funkce drží své rozhodování
a adaptéry. Obrazové a hlasové vstupy nejsou instrukce pro přímé ovládání GPIO.

Webový mobil a veřejná konzole slouží pro vývoj/náhled. Nepotvrzují nativní
Keychain, BLE, mikrofon, dlouhodobé WSS nebo účinek motoru a serva. CAD ověření
prokazuje generované geometrické podmínky, nikoli správný tisk, sestavení,
napájení či fyzickou přepravní akceptaci. Tyto důkazy se zapisují zvlášť.

Buildy zdrojového repozitáře zachovávají web, firmware, backend, CAD a
podepsanou iOS IPA na GitHub-hosted runnerech. Zdroj je od 12. 10. 2026
veřejný. Produkční nasazení serveru, OTA a distribuce IPA
jsou odděleny v soukromém `Blato58/DildoVander-deploy`. Přístup k tomuto
repozitáři není potřeba pro lokální fake vývoj. Sestavení artefaktu,
jeho podpis a jeho nasazení jsou samostatné výsledky ověření.
