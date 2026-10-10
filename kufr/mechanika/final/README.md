# F2 — vložka do skutečného dřevěného kufru

F2 nahrazuje chybný tištěný korpus F1. Skutečný kufr se nevrtá ani neřeže.
Uvnitř je čtyřdílný PETG rám s integrovanými předními/zadními žebry, šest
nezávislých vačkových zdvihů a pevné držáky elektroniky. Čtyři tištěné
podlahové kotvy připojují rám k holému dřevu DNA přes spodní epoxidový film
a odlité mechanické zámky Ø6/Ø9. Konstrukce se neopírá o neověřenou
chemickou adhezi epoxidu k PETG. Hůlky, powerbanka a textil jsou odnímatelný obsah;
nejsou nutné pro soudržnost vložky.

**Návrh není fyzicky přijatý ani uvolněný k sériovému tisku.** CAD ověření
nepotvrzuje skutečné rozměry hůlek a konektorů, soudržnost dřeva/lepidla,
moment serva ani jeho chování bez PWM. Tyto podmínky zůstávají výslovně
otevřené v [předání](STAV.md) a [montážním návodu](MONTÁŽ.md).

Výstupy lze přijmout pouze s ověřením, jehož zdrojové SHA-256 odpovídají
aktuálním souborům; starší ověření nepokrývá zjednodušení montáže. Návrh má
37 typů STL / 52 výtisků včetně dvoudílné zkušební měrky pro čep, drážku
a boční kapsy matic M3/M4. Skutečné počty
exportovaných dílů uvádí aktuální tiskový seznam.

## Co otevřít

- [Interaktivní sestava](exports/viewer.html): provoz, servis, kabeláž,
  montáž vpřed/zpět a přeprava; šest samostatných zdvihů a dvě sekvence.
- [Balíček F2](exports/F2-kouzelny-kufr.zip): zdroje, STEP, STL, rendery,
  montážní a kabelové podklady; otisk v `exports/package-sha256.txt`.
- [Výsledek geometrických kontrol](exports/verification.json),
  [tiskový seznam](exports/print-bom.csv), [materiál k doplnění](mechanicky-material.csv),
  [seznam elektroniky](elektronika.csv), [řezný seznam kabelů](exports/cable-cut-list.csv).
- [Specifikace](SPECIFIKACE.md), [měření a předpoklady](PODKLADY.md),
  [montáž](MONTÁŽ.md), [kabeláž](KABELAZ.md), [efekty](EFEKTY.md).
- [Úplný inventář a potvrzené zapojení](exports/reference-sources.html)
  jsou přibalené také pro čtení mimo repozitář.

## Rozměry a návrh

Majitel potvrdil vnitřní spodní prostor 431,8 × 266,7 × 114,3 mm,
vnější údaj výrobce 450 × 310 × 200 mm a bezpečně vložitelný kvádr
400 × 240 × 170 mm. Rám leží nad výztuhou dna vysokou 5 mm. Zástavba v klidu
zůstává pod horní hranou spodní části; při zdvihu se používá otevřený kufr.
Tloušťky dřeva, detail pantů a rozdělení prostoru víka jsou parametry,
nikoli rozměry odvozené jako jisté z perspektivních fotografií.

Zvoleno šest modulů na rozteči 48 mm, s délkou 212 mm a zdvihem 16 mm.
Majitel 10. 10. 2026 potvrdil délku hůlky do 200 mm a šířku hlavice 38 mm
včetně případného pouzdra. Ostatní tvarové rozměry zůstávají předpoklady.
Zachycená vačka řídí zdvih i návrat přes integrální PETG kluzný čep Ø6
na lůžku. Lůžko se tiskne otočené +90° kolem Y, čep vzhůru bez podpory
na kluzné ploše. Nevyhovující vůli řeší změna profilu/parametru a nový tisk;
montáž nepotřebuje pilník, pilku ani vrtačku. Brusný papír patří pouze
k odstranění povrchové úpravy v lepených stopách dřeva. Dole lůžko spočívá na pevných
podpěrách; firmware vypne impulzy serva. Pro přepravu každé prázdné lůžko
samostatně zachytí výrazný pásek široký 8 mm. Bez snímače se odjištění ověřuje ručně.
Stop nepředstírá návrat dolů a vyžaduje kontrolu před zotavením.

ESP a kamera jsou vzadu uprostřed, mikrofon vpředu uprostřed a reproduktory
po stranách. Aktivní jsou jen díly skutečně používané v potvrzeném zapojení;
rezervní nakoupené kusy zůstávají v inventáři mimo kufr. Každá deska má
pevné dorazy a pásky, nosiče vlastní spojení s rámem. Powerbanku lze po
odpojení USB a uvolnění krytu a dvou pásků vyjmout nahoru.
Pasivní USB-A zásuvka a kabel pro napájení ESP jsou uvedené k doplnění
v kabelovém plánu; jejich dostupnost není potvrzeným nákupem. Zadání
výslovně dovoluje tyto pasivní montážní díly; podklad je ve specifikaci.

## Ovládání vieweru

Přepínač režimu obnovuje příslušné vrstvy a polohy. Strom dovoluje skrýt,
izolovat a přiblížit díl; průhlednost a kóty jsou pouze zobrazení.
Pohledy: celek, shora, zepředu a zboku. Tažení otáčí, kolečko přibližuje.
Držení Shift nebo tlačítko Inspect zvýrazní povrch; klik připne kartu,
Esc nebo klik do prázdna výběr zruší. Díly lze volit ve stromu nebo kartě;
piny, vodiče, svazky a celé sítě v kabelovém panelu. Vedení používá data jednotlivých
vodičů z kabelového plánu, nikoli náhradní spojnice středů desek.

Každý ze šesti zdvihů lze prohlédnout zvlášť. Hlavní a krátká sekvence
používají společná data `effects.json`; čas jízdy ve vieweru je ilustrativní,
v reálném firmware jej určí kalibrace. Stop zmrazí neznámou mezilehlou
polohu; reset vyžaduje servisní režim. Generátor předává všechny fáze
sekvence a odmítá nepodporované pořadí, kanály nebo souběhy. Závěrečný
klid trvá 250 ms s vypnutými výstupy. Zvuk prohlížeče je volitelný.
Svítí jen jeden LED úsek. Pevný limit celé prezentace je 15 s; při jeho
dosažení se sekvence zastaví s požadavkem na kontrolu a servisní reset.
Montážní režim ukazuje kumulativní stav s krokem, materiálem, nástroji a
kontrolou. Přeprava odstraní banku, hůlky a textil a ukáže šest pásků i
odpojený parkovaný USB konektor. Přepnutí režimu ovládá pouze model.

## Reprodukovatelnost

Zdroje CAD a prohlížeče jsou v této složce. `parameters.json` řídí rozměry,
`harness.py` jednotlivé vodiče, `assembly-guide.json` postup a `effects.json`
choreografii. Exportovaný plášť je referenční STEP; nemá žádné STL k tisku.
STL jsou orientované pro tisk, sestavy STEP v souřadnicích celého kufru.
X doprava, Y k pantům, Z nahoru; počátek na vnitřním dně vpředu vlevo.

Použity jsou Python 3.11, CadQuery 2.7, Node.js a stávající závislosti z
`requirements.txt`. Z této složky na Windows:

```powershell
.\Regenerate.ps1 -Python ..\.venv\Scripts\python.exe
```

V samostatně vybaleném ZIPu vytvořit prostředí a instalovat
`requirements.txt`, potom předat cestu jeho Pythonu.
V repozitáři se navíc kontroluje shoda generovaného firmware souboru
`PresentationScore.h`; samostatný ZIP tuto kontrolu vynechá, pokud adresář
firmware není přítomný. Balíček obsahuje mechanické podklady a sdílená data
efektů; jeho regenerace není ověřením sestavení firmware. Generování nejprve
vytvoří CAD, viewer a výkresy a sváže jejich obsah se zdroji v
`generation-snapshot.json`. Ověření kontroluje zdrojové otisky, oba podklady
Inspect, geometrii, STEP/STL, mechanismy, montážní a kabelová data a zapíše
otisky všech exportů. Balení porovná každý soubor s tímto ověřeným záznamem;
změněný obrázek, CSV nebo viewer odmítne stejně jako CAD.
Starší ZIP/STL nejsou součástí F2. Samostatné `preview.py --viewer-only`
nepotvrzuje nové ověření ani neobnovuje výrobní balíček.

Pro spolehlivé otevření používat místní HTTP server v `exports/`, například
`python -m http.server 8915 --bind 127.0.0.1`, a adresu
`http://127.0.0.1:8915/viewer.html`. Žádná data se tím neposílají mimo počítač.
