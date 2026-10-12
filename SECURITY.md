# Bezpečnost

Projekt je prototyp; bezpečnostní opravy se připravují proti aktuálnímu `main`.
Starší sestavy nemají deklarovanou dobu podpory. Přilož verzi/commit dotčené
komponenty a rozlišuj backend, firmware, mobil, simulátor a distribuční nástroje.

## Neveřejné hlášení zranitelnosti

Použij **Report a vulnerability** v
[GitHub Security Advisories](https://github.com/Blato58/DildoVander/security/advisories).
Tento neveřejný kanál byl zapnut a jeho konfigurace ověřena přes GitHub API
12. 10. 2026 při schváleném zveřejnění repozitáře. Postup hlášení popisuje
[návodu GitHub](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository).
Příjem skutečného oznámení nebyl testován. Pokud tlačítko chybí, lze veřejně
požádat `@Blato58` pouze o dostupný soukromý kontakt, bez detailů zranitelnosti;
jiný veřejný bezpečnostní kontakt zatím nebyl určen.

Soukromé hlášení má obsahovat komponentu/verzi, dopad, kroky reprodukce a
minimální syntetický příklad. Nevkládej skutečné credentialy, data hostů ani
produkční konfiguraci. Neprováděj testy na cizím či produkčním zařízení,
spouštění pohonů, placená volání nebo zátěž bez samostatného oprávnění.
Projekt neslibuje SLA ani odměnu za hlášení.

## Hranice bezpečnosti

Provider klíč patří pouze na backend. Device identita a místní párování se
provisionují servisně; Apple podpisové identity a OTA privátní podpisový klíč
patří do odděleného správcovského prostředí. Nedávej je do image, frontend
balíčku, logů ani git historie. Nález uniklého credentialu musí správce řešit
odvoláním/rotací a posouzením historie, logů i artefaktů; smazání souboru nestačí.

AI vrací pouze validovaný návrh. Místní ESP32 brána rozhoduje podle snapshotu,
kalibrace a bezpečného stavu. Simulace ani úspěšný test nepotvrzují fyzický
efekt nebo polohu. Pro běžné PR používej syntetická data a fake provider.
Nasazení, podpis, údržba serveru a skutečné pohony vyžadují správcovské oprávnění.

Zpracování a uchování návštěvnických dat popisuje
[dokumentace soukromí](docs/public-privacy.md). Konfigurace CI a nasazení je v
[provozním návodu](docs/ci-cd.md); její existence nedokazuje nastavení ochrany
GitHub environments, větví nebo izolaci skutečných runnerů.
