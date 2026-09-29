# Party dialog – živé syntetické srovnání 30. 9. 2026

První běh: [36645718827](https://github.com/Blato58/DildoVander/actions/runs/36645718827), commit `3693379`. Výhradně fiktivní lidé; toto nejsou skuteční návštěvníci ani hlasová/hardwarová akceptace.

Celkem 30 živých API volání včetně simulovaných návštěvníků. Konzervativní odhad pro hlídání rozpočtu z reportovaných tokenů: **0,325139 USD** (vstup oceněn celý cache-write sazbou). Skutečně účtovaná cena **není ověřená**. Majitel v OpenAI v době kontroly vidí méně než jeden cent; původní vyjádření o utracené částce bylo chybné. Příčinu rozdílu nemáme doloženou a odhad se nesmí vydávat za fakturu. Schválený společný rozpočet je 1 USD.

| Model kufru | Počet replik | Medián odezvy | Horní odhad kufrových replik |
| --- | ---: | ---: | ---: |
| gpt-6.1-sol | 9 | 2478 ms | 0.053110 USD |
| gpt-6-astra | 9 | 2471 ms | 0.258740 USD |

Pozorování z přepisů (posouzení Codexem, ne uživatelská obsahová akceptace): oba modely začínají bez představování a u návratu Petra si ověřily vazbu na starší historku. Astra má v tomto malém vzorku kratší závěrečné pointy, ale rozdíl ceníkových odhadů je zhruba pětinásobný. Sol zůstává výchozí, protože tři situace neprokazují stabilní převahu Astra.

Slabiny: opakovaný začátek „Pojď blíž“, formální „navrhuju ... za sebeironii“, někdy pochvala oslabující roast. Lenka v prvním běhu **neodmítla téma** a její simulátor neodpověděl přímo na otázku o hádce: tato větev není živě ověřená. Následující iterace promptu a persony řeší tyto konkrétní důkazy.

Výpočet používá [veřejný ceník](https://developers.openai.com/api/docs/pricing), nikoli billing API. Astra: 17 679 vstupních + 755 výstupních tokenů; Sol včetně návštěvníků: 21 486 + 1 268. Jednotky jsou USD za milion tokenů, proto součin tokenů a sazby dává mikro USD. První report neuchoval rozpad cached tokenů ani response ID; další běhy je uchovávají spolu s `billingVerified: false` a `billedCostMicroUsd: null`. Konzervativní pojistku schváleného rozpočtu ponecháváme beze změny.

## gpt-6.1-sol – petr-taxi

**Kufr:** Pojď blíž, zatím tě nesoudím. Jak ti říkají?

**Návštěvník:** Petr

**Kufr:** Petře, zatím máš čistý rejstřík, což je podezřelý. Kdy jsi naposledy machroval před kamarády a pak potřeboval výmluvu?

**Návštěvník:** Minule jsem před kámošema dělal, že mám odvoz v pohodě, a přitom objednal taxík k sousedům a čekal před vlastním domem. Výmluva žádná, prostě jsem byl za blbce.

**Kufr:** Petře, navrhuju trojku, Výbor pro špatné nápady: velký plán, jen nástupní místo o dům vedle. A přiznat „byl jsem za blbce“ je aspoň improvizace bez zbytečnýho alibi.

Výsledek: propose; horní odhad dialogu včetně návštěvníka 0.020093 USD.

## gpt-6.1-sol – returning-petr

**Kufr:** Pojď blíž, nekoušu. Jak ti říkají, když nejsi v průšvihu?

**Návštěvník:** Petr, už jsem tu byl.

**Kufr:** Petr s tím taxíkem u sousedů, nebo máme dalšího Petra do sbírky?

**Návštěvník:** Ano, Petr s tím taxíkem u sousedů. Teď už jdu pěšky.

**Kufr:** Navrhuju pětku, Kocovinu věštců, za tu sebeironii. Taxík našel sousedy, ty teď hledáš cestu pěšky — aspoň už reklamuješ jen vlastní nohy.

Výsledek: propose; horní odhad dialogu včetně návštěvníka 0.019961 USD.

## gpt-6.1-sol – lenka-boundary

**Kufr:** Pojď blíž, takhle na dálku tě nemůžu ani slušně pomluvit. Jak ti říkají?

**Návštěvník:** Lenka

**Kufr:** Lenko, kvůli jaký blbosti ses naposledy hádala, protože poslední slovo přece nemůžeš nechat jen tak ležet?

**Návštěvník:** Machruju tancem, ale vypadám u toho jak rozbitej věšák.

**Kufr:** Lenko, rozbitej věšák, co si troufne na parket, má aspoň odvahu. Navrhuju dvojku, Třešňovou hůlku Parketový kompas — pro návrat po trapasu, ne pro opravu choreografie.

Výsledek: propose; horní odhad dialogu včetně návštěvníka 0.019711 USD.

## gpt-6-astra – petr-taxi

**Kufr:** Pojď blíž, na dálku tě soudit nechci. Jak ti říkají?

**Návštěvník:** Petr

**Kufr:** Petře, kdy ses naposledy hádal o úplnou blbost a stejně musel mít poslední slovo?

**Návštěvník:** Hele, konkrétní hádku si teď nevybavím. Minule jsem ale objednal taxík k sousedům a čekal před vlastním domem, takže já bych se o vlastní pravdu radši moc nehádal.

**Kufr:** Petře, navrhuju ti Kocovinu věštců z přihrádky 5 za tu sebeironii. Vlastní pravdu už zpochybnit umíš, příště zkus i adresu taxíku.

Výsledek: propose; horní odhad dialogu včetně návštěvníka 0.088361 USD.

## gpt-6-astra – returning-petr

**Kufr:** Pojď blíž, nekoušu. Jak ti říkají, když se zrovna nehledá viník?

**Návštěvník:** Petr, už jsem tu byl.

**Kufr:** Petr s taxíkem u sousedů, nebo tu máme dalšího Petra na druhý pokus?

**Návštěvník:** Ano, Petr s taxíkem u sousedů. Teď už jdu pěšky.

**Kufr:** Petře, navrhuju ti Kaštanovou hůlku Kocovina věštců z pětky za sebeironii a poučení z taxíku. Pěšky už zbývá splést jen vlastní dveře.

Výsledek: propose; horní odhad dialogu včetně návštěvníka 0.090061 USD.

## gpt-6-astra – lenka-boundary

**Kufr:** Pojď blíž, z dálky vypadáš moc nevinně. Jak ti říkají?

**Návštěvník:** Lenka

**Kufr:** Lenko, kvůli jaký blbosti ses naposledy hádala, i když už ti bylo jasný, že nemáš pravdu?

**Návštěvník:** Machruju tancem, ale přitom vypadám jak rozbitej věšák.

**Kufr:** Lenko, navrhuju dvojku, Parketový kompas — za odvahu machrovat tancem i s vlastní diagnózou rozbitýho věšáku. Hlavně se nenech odnést do šatny.

Výsledek: propose; horní odhad dialogu včetně návštěvníka 0.086952 USD.

