# Podklady a otevřené vstupy F2

## Potvrzené údaje

- Majitel: spodní vnitřek 431,8 × 266,7 × 114,3 mm, výztuha dna 5 mm.
- Výrobce podle majitele: vnější 450 × 310 × 200 mm.
- Majitel: kvádr 400 × 240 × 170 mm se vejde s rezervou.
- [Inventář](exports/reference-sources.html#inventar) určuje elektroniku, množství
  a doložené rozměry. Zakoupené rezervy zůstávají evidované.
- [Zapojení](exports/reference-sources.html#zapojeni) určuje GPIO, kanály,
  napájení a bezpečnostní zapojení. F2 je nemění.

Všechny fotografie `reference/photo-1.png` až `photo-6.png` byly prohlédnuty.
Dokládají dřevěný kufr, povrch, příčnou výztuhu, kování a tvar hůlek.
Fotografie je vizuální reference; perspektiva neposkytuje přesnou tloušťku
stěny, výšku víka, polohu konektorů ani rozteč otvorů.

## Parametrické odhady, které je nutné potvrdit

| Oblast | Použitý předpoklad | Praktický důsledek |
| --- | --- | --- |
| Korpus | Stěna 9,1 mm, dno 6 mm, víko 79,7 mm, osa pantu a šířka/poloha výztuhy | Suché vložení a celý chod víka; korpus se nevyrábí |
| Bezpečný kvádr | V půdorysu centrovaný | Ověřit jeho polohu ve skutečném kufru |
| Hůlky | Délka 200 mm z popisku 19–20 cm; hlavice 38 × 28 × 55 mm, dřík Ø 16 mm | Změřit maximum včetně skutečného pouzdra; jinak nelze uzavřít volbu 6 pozic |
| Servo | Tělo z inventáře; poloha osy, šířka a náboj páky dlouhé 35 mm jsou odhadnuté | Měrka jednoho modulu, teprve potom šest výtisků |
| Kamera/FPC | Výměnná obálka 24 × 12 × 24 mm | Potvrdit doručenou variantu a konektory |
| Degson | Návrhová obálka 35 × 22 × 16 mm | Ověřit skutečné páčky, vstupy a nástrojový prostor |
| Konektory USB | 20 mm přímý prostor, 12 × 12 mm průřez; poloha podle rezervací | Potvrdit každou skutečnou zásuvku i kabelovou vidlici |
| Ostatní piny | Relativní návrhové kotvy na skutečných obálkách desek | Souřadnice v kabeláži nejsou zaměřením pinů |
| Kabely | Vnější průměry (OD) a minimální poloměry podle kabelového plánu | Fyzicky projít celý pohyb; délky nejsou zaručené střihy bez zkušebního osazení |
| Napájení ESP přes USB | Pasivní USB-A zásuvka a intaktní kabel USB-A/C uvedené k doplnění | Ověřit dostupnost, proudové zatížení a skutečné rozměry; nejde o doložený nákup |
| Zatížení | Návrhových 5 kg, rázové zrychlení 3 × g; hůlka 150 g | Zvážit skutečnou sestavu; provést zatěžovací zkoušku |

## Vyřešené rozpory

F1 dříve navrhoval nový tištěný korpus. To odporovalo současnému zadání;
F2 jej odstranil z aktivního CAD, tisku, dokumentace a seznamu balíčku.
Zůstává skutečný dřevěný kufr bez nových děr. Původní reference se zachovaly.
Rozměr bezpečného kvádru doplňuje vnitřní měření; nepřepisuje jej.
Předchozí měkká brzda nebyla doložená pro mezilehlé polohy a je odstraněna.
Stop je ručně kontrolovaný stav, nikoli tvrzení o pasivním zabrzdění zdvihu.

Katalogové tělo desky není její montážní rozteč. Nosiče proto přidržují
okraje a pásky. Powerbanka ani samet nesmějí nahrazovat doraz nebo šroub.
Druhá PCA, sedm svorek a další rezervy nejsou aktivní vnitřní výplň.

## Externí podklady pro doplněný materiál

[Bison Epoxy Metal 24 ml, výrobek 6305443](https://www.bison.net/getmedia/d54542f5-f56f-40b2-9406-3ad692c92a30/Print%20PDF.pdf)
uvádí spojování hliníku se dřevem, směs 1:1, fixaci 10 h a konečnou pevnost
po 24 h. Jeho údaj 22 MPa je pro hliník, nikoli pro dřevo tohoto kufru.
[Hydro EN AW-6060](https://www.hydro.com/globalassets/08-about-hydro/hydro-worldwide/austria/nenzing/alloy-data-sheets/hydro-en-aw-6060.pdf)
uvádí pro stav T66 a tloušťku do 5 mm mez kluzu 160 MPa;
návrh konzervativně požaduje doložených
alespoň 150 MPa. Konkrétní profil musí odpovídat požadovanému stavu materiálu.
Příklad českého zdroje rozměru je
[profil L 20 × 20 × 3 mm](https://www.ehlinik.cz/hlinikovy-l-profil-20x20x3/pro-8WS0000101.html).
Nic nebylo objednáno.
