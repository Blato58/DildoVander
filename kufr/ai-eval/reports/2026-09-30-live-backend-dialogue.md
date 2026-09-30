# Živý adaptivní rozhovor přes backend – 30. 9. 2026

Sol je po rozhodnutí majitele jediný povolený dialogový model. Dva skutečné textové rozhovory prošly nasazeným HTTPS backendem až k návrhu přihrádky. Sol si po restartu vybavil Marka a navázal na konkrétní příběh. V tomto malém vzorku je jeho humor přirozenější než u Luny; nejde o statistické srovnání ani akceptaci před skutečnými hosty.

## Jak test probíhal

Návštěvníka hrál integrátor. Každou další odpověď napsal až po přečtení skutečné repliky z API. Žádný hotový scénář ani druhý model návštěvníka. Všechna jména a události jsou smyšlené. Jeden workflow prováděl jeden start/reply/finish/status/clear; tokeny a klíče zůstaly v runneru. Použitý endpoint je backend → OpenAI Responses, nikoli hlasové Realtime API. STT/TTS a skutečná deska nejsou součástí tohoto důkazu.

- Implementace: [PR #35](https://github.com/Blato58/DildoVander/pull/35), merge `c3d2c53`; [PR #36](https://github.com/Blato58/DildoVander/pull/36), merge `78b563e`.
- [Nasazení Luna](https://github.com/Blato58/DildoVander/actions/runs/36651086166) a [nasazení Sol](https://github.com/Blato58/DildoVander/actions/runs/36652694673) prošly. Sol nasazení zahrnulo build, backend testy, HTTP smoke, offline testy klienta a deploy konfigurace.
- [Firmware CI](https://github.com/Blato58/DildoVander/actions/runs/36650589429) a [Sol offline eval](https://github.com/Blato58/DildoVander/actions/runs/36652635877) prošly.
- Server po testu zůstává v `responses` s `gpt-6.1-sol`, Standard tier. Žádný další placený test neběží. Měsíční limit 10 USD se nezměnil.

## Přepisy

### Luna – původní návštěva

*Kufr začíná sám na událost opening.*

**Kufr:** Jak ti mám říkat — jméno, nebo přezdívka?

[Běh 36651380067](https://github.com/Blato58/DildoVander/actions/runs/36651380067) · `ask`

**Návštěvník:** Marek. Klidně Marek, přezdívky tady radši nerozjížděj.

**Kufr:** Marek tedy. Jakou největší kravinu jsi kdy vydával za „to mám úplně pod kontrolou“?

[Běh 36651442718](https://github.com/Blato58/DildoVander/actions/runs/36651442718) · `ask`

**Návštěvník:** Na svatbě kámoše jsem dělal DJ. Prej mám playlist. A novomanželům jsem na první tanec pustil reklamu na sekačku.

**Kufr:** Na první tanec sekačka, to je dost specifickej žánr romantiky. Jak ses z toho vymluvil — a kdo první pochopil, že to nebyl plánovanej remix?

[Běh 36651518341](https://github.com/Blato58/DildoVander/actions/runs/36651518341) · `ask`

**Návštěvník:** Tvrdil jsem, že je to součást písničky. Nevěsta mi ten mobil vyrvala, když se začalo mluvit o sběrným koši.

**Kufr:** „Součást písničky“ ještě šlo, ale sběrnej koš už byl dost konkrétní text. Kdo se smál první — a co jsi řekl, když ti nevěsta zabavila mobil?

[Běh 36651620553](https://github.com/Blato58/DildoVander/actions/runs/36651620553) · `ask`

**Návštěvník:** Ženich se smál. Já řekl, že aspoň ušetřili za DJ. Teď mi posílá fotku posekaný zahrady každej víkend, hajzl.

**Kufr:** Jilmová Knihovní bouře. Sekačka ti vzala první tanec a ženich ti teď každý víkend posílá důkaz, že playlist měl pokračování.

[Běh 36651754749](https://github.com/Blato58/DildoVander/actions/runs/36651754749) · `propose` · přihrádka 3

### Sol – návrat po restartu

*Kufr začíná sám na událost opening.*

**Kufr:** Jak ti mám říkat?

[Běh 36652967864](https://github.com/Blato58/DildoVander/actions/runs/36652967864) · `ask`

**Návštěvník:** Zas Marek, byl jsem tu před chvílí.

**Kufr:** Ten Marek, co novomanželům k prvnímu tanci pustil sekačku?

[Běh 36653030824](https://github.com/Blato58/DildoVander/actions/runs/36653030824) · `ask`

**Návštěvník:** Jo, já. A ženich má za měsíc narozeniny. Chce po mně zase hudbu. Nevím, jestli mi odpustil, nebo mě chce veřejně dorazit.

**Kufr:** Po těch fotkách zahrady bych odpuštění netipoval. Cos mu na tu nabídku odepsal?

[Běh 36653099597](https://github.com/Blato58/DildoVander/actions/runs/36653099597) · `ask`

**Návštěvník:** Že tentokrát jedině za prachy. Poslal mi stovku a napsal, že za ty peníze může být i křovinořez.

**Kufr:** Tak sis nechal zaplatit stovku, aby tě mohl celej večer sekat. Co mu tentokrát pustíš jako první?

[Běh 36653158213](https://github.com/Blato58/DildoVander/actions/runs/36653158213) · `ask`

**Návštěvník:** Asi reklamu na rozvodovýho právníka. Ať vidí, že umím i jiný žánry. Ale nevěsta by mě tentokrát asi zabila.

**Kufr:** Tobě sedí Výbor pro špatné nápady. Za stovku už jim neposekáš zahradu, rovnou rozdělíš majetek.

[Běh 36653228590](https://github.com/Blato58/DildoVander/actions/runs/36653228590) · `propose` · přihrádka 3

## Hodnocení a funkční důkazy

- Luna: dvojité dotazy, opakovaná otázka na již popsanou výmluvu a popisný humor. Navíc řekla „Jilmová Knihovní bouře“, ale vrátila přihrádku 3, zatímco tato hůlka je v katalogu 6. Technický guard ověřuje dostupné číslo; soulad volné repliky s názvem katalogu tím není garantovaný. Luna byla vyřazena. Tento případ zůstává podkladem dalšího sémantického hodnocení.
- Sol: krátké repliky, jedna otázka, správné ověření identity přes detail z historie a konkrétní návrat k ceně DJ a reklamě. Závěr správně jmenuje Výbor pro špatné nápady z přihrádky 3. Některé mezirepliky jsou jen navazující otázky; jeden povedený dialog nezaručuje vtipnost celé akce.
- [Stav po restartu](https://github.com/Blato58/DildoVander/actions/runs/36652923167): stejné eveningId, 5 výměn / 1019 B. Sol dostal jen „Zas Marek, byl jsem tu před chvílí“, sám vybavil první tanec a sekačku; po potvrzení použil i dřívější fotky zahrady.
- [Replay opening](https://github.com/Blato58/DildoVander/actions/runs/36651932113) vrátil stejnou operaci `fe76392e-41f1-4c26-ac16-4cd4699ad9ad`; počet výměn zůstal 5. Nevyvolal další placenou odpověď.
- [Ukončení Luna](https://github.com/Blato58/DildoVander/actions/runs/36652057592) a [ukončení Sol](https://github.com/Blato58/DildoVander/actions/runs/36653288767) prošla. Nové sezení [36653361852](https://github.com/Blato58/DildoVander/actions/runs/36653361852) bylo ukončeno hned po opening bez výběru [36653420740](https://github.com/Blato58/DildoVander/actions/runs/36653420740). Toto ověřuje explicitní API cleanup nedokončeného rozhovoru, nikoli běh ESP32 detektoru nečinnosti.
- [Stav po všech sezeních](https://github.com/Blato58/DildoVander/actions/runs/36653476199): 11 výměn / 1811 B, bez vytěsnění. DELETE sezení nesmazal paměť večera.
- [Smazání testovacího večera](https://github.com/Blato58/DildoVander/actions/runs/36653543541) prošlo pro přesné původní eveningId; před testem byl prázdný a obsahoval pouze naše syntetická data. [Následný stav](https://github.com/Blato58/DildoVander/actions/runs/36653597988) potvrzuje nové eveningId a nulu výměn.

## Firmware a zbývající integrace

Skutečné JSON operace s návrhem od Luny i Solu byly přehrány přes `turn_replay.cpp`: produkční parser → korelační guard → PendingProposal → čistý WandPresentation se simulovaným portem. Pro zachycený čas příjmu oba běhy vrátily `SIMULATED raise 3`, `lower 3`, `complete 3`. Stejný návrh se simulovaným časem o 120 s později nebo readyMask=0 byl odmítnut (exit 2) bez pohybu. Nešlo o pozdější přijetí již expirovaného návrhu.

Hostitelské testy pokrývají Stop/timeout/změnu bezpečnosti, firmware se sestaví pro IDF i Arduino. Síťové ukončení má 90s místní nečinnost a opakovatelný DELETE; nejasný výsledek drží sezení a blokuje nový start. `espidf_main` ale dosud nevolá opening a prezentaci z autorizovaného vstupu, není hotový playback ani kalibrovaný port s potvrzením polohy a atomickými journal/snapshot přechody. Proto celé fyzické zařízení ještě není funkčně přijato. Nahrání firmware, GPIO, servo, I2S a iPhone ovládání se tímto testem nepotvrdily.

## Rozpočet, nikoli faktura

V této sérii proběhlo 11 unikátních provider tahů: 5 Luna + 6 Sol (z toho jeden pouze opening pro test opuštění). Replay a status/finish/clear další provider tah nevytvářejí. Před každým novým tahem byla zapsána konzervativní rezervace; souhrnná kontrolní hodnota skončila na **0,884808 USD**, pod schváleným 1 USD. Není to skutečná útrata.

Základ kontroly 0,162560 USD tvoří horní mez 0,01 USD podle sdělení majitele o předchozím účtu a dalších 0,152560 USD jako konzervativní Fast odhad pozdějšího historického eval běhu. Nové tahy přidaly 0,722248 USD rezervací bez jejich refundace; Sol rezervace začíná na 0,10 USD a roste s horní mezí serializované paměti. Historické vyúčtování nebylo nezávisle načteno a nemusí odpovídat této rezervě. Skutečný účet potvrzuje pouze OpenAI; dřívější odhady se nesmějí prezentovat jako zaplacené částky.

[Úplná strojová evidence včetně veřejné korelace, vstupů, replay výsledků a rezervací](2026-09-30-live-backend-dialogue.json). Žádné credentialy ani bearer tokeny.
