# Efekty F2

Jeden zdroj `effects.json` řídí náhled a generovaný soubor pro firmware
`PresentationScore.h` (`generate_effects.py --check` hlídá shodu).
Generuje všechny fáze včetně odhalení, čekání, návratu a klidu; neplatné
pořadí, kanály nebo nepodporované kombinace odmítne. Ilustrační čas
pohybu není náhradou kalibrace skutečného serva.
Šest přihrádek, šest LED na pozici, celkem 36. Počáteční limit jasu je 64 z 255.
Volba je vždy konkrétní číslo 1–6 z existující místní bezpečnostní brány.

| Fáze | Hlavní sekvence | Opakování |
| --- | --- | --- |
| Probuzení | 600 ms, vybraná pozice fialově, nízký tón | vynecháno |
| Hledání | 6 × 120 ms, světlo postupuje přes pozice 1–6 s rostoucími tóny | vynecháno |
| Náznaky | 300 ms soused vpravo, 300 ms soused vlevo | vynecháno |
| Volba | 500 ms, zlaté světlo | 400 ms, zlaté světlo |
| Puls | 3 × (150 ms vibrace + 150 ms pauza) | 150 ms vibrace + 150 ms pauza |
| Ticho | 200 ms | zahrnuto v pauze |
| Odhalení | kalibrovaný zdvih, světlá zlatá, tón 880 Hz | stejné |
| Prezentace | 5 s nahoře, teplé světlo | stejné |
| Návrat | kalibrovaný návrat, tlumené teplé světlo | stejné |
| Klid | 250 ms, výstupy vypnuté | 250 ms, výstupy vypnuté |

Příprava trvá 3 520 ms nebo 700 ms. Náhled používá ilustračních 1 000 ms na
každý pohyb; firmware použije servisně naměřenou kalibraci konkrétního serva.
Kalibrace na skutečné sestavě dosud nebyla provedena. Hůlka zůstává v lůžku.
Bez snímače se neodvozuje její vyjmutí ani stav přepravního pásku.
Automatický normální návrat obnoví dostupnost. Opakování používá krátkou
variantu; ruční řízení zachovává dosavadní samostatnou sekvenci.

Během pohybu serva neběží vibrace; zapnutý je nejvýše jeden motor a jeho
puls má 150 ms. Svítí nejvýše jeden 20cm LED úsek; generátor odmítá
souběžné skupiny a výstupní vrstva odmítá vícebitovou masku.
Celá prezentace má pevný limit 15 s od přijetí návrhu, včetně přípravy,
obou pohybů a závěrečného klidu. Přechody mezi fázemi jej neprodlužují.
Při prvním průchodu řídicí smyčky při dosažení 15 s nebo později se výstupy vypnou
a vznikne chyba bez automatického návratu či obnovení dostupnosti.
Stejný limit dodržuje náhled, i když prohlížeč pozastaví animaci.
Lokální syntetické tóny využívají existující I2S B profil,
nevolají AI a ustupují živému hlasu. Je-li audio nedostupné, tón se přeskočí.
Stop okamžitě zakáže nové efekty a deaktivuje řídicí výstupy; již přijatý
krátký DMA blok audia může doběhnout. Není přidaný žádný nový reproduktor,
zesilovač, zdroj světla ani napájecí spínač.

Stop/chyba nikdy neposílá automatický návrat. Poloha je neznámá a vyžaduje
podepření, kontrolu a explicitní zotavení. Dole je požadováno mechanické
podepření a servo bez aktivního držení. FULL_OFF odpojuje signál, nikoli
napájení V+; skutečný kus serva musí projít testem v montážním návodu.
Zelená kolébka je potvrzené ruční odpojení větve pohonů.
