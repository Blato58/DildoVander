# Podklady a otevřené vstupy F2

## Potvrzené údaje

- Majitel: spodní vnitřek 431,8 × 266,7 × 114,3 mm, výztuha dna 5 mm.
- Výrobce podle majitele: vnější 450 × 310 × 200 mm.
- Majitel: kvádr 400 × 240 × 170 mm se vejde s rezervou.
- [Inventář](exports/reference-sources.html#inventar) určuje elektroniku, množství
  a doložené rozměry. Zakoupené rezervy zůstávají evidované.
- [Zapojení](exports/reference-sources.html#zapojeni) určuje GPIO, kanály,
  napájení a bezpečnostní zapojení. F2 je nemění.

Textové rozměrové vstupy a jejich původ shrnuje `reference/evidence.json`.
Potvrzení majitele je zaznamenané v přehledu rozměrů kufru a hůlek;
nenahrazuje fyzickou zkoušku vložky. Tloušťka stěny, výška víka, poloha
konektorů ani montážní rozteče nejsou tímto potvrzením zaměřené.

## Parametrické odhady, které je nutné potvrdit

| Oblast | Použitý předpoklad | Praktický důsledek |
| --- | --- | --- |
| Korpus | Stěna 9,1 mm, dno 6 mm, víko 79,7 mm, osa pantu a šířka/poloha výztuhy | Suché vložení a celý chod víka; korpus se nevyrábí |
| Bezpečný kvádr | V půdorysu centrovaný | Ověřit jeho polohu ve skutečném kufru |
| Hůlky | Majitel 10. 10. 2026 potvrdil celkovou délku do 200 mm a maximální šířku hlavice 38 mm; otázka zahrnovala případné pouzdro | Volba šesti pozic má potvrzený délkový a šířkový vstup. Výška hlavice 28 mm, délka hlavice 55 mm, dřík Ø 16 mm a hmotnost 150 g zůstávají návrhové předpoklady; ověřit usazení v prototypu |
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
Zůstává skutečný dřevěný kufr bez nových děr.
Rozměr bezpečného kvádru doplňuje vnitřní měření; nepřepisuje jej.
Předchozí měkká brzda nebyla doložená pro mezilehlé polohy a je odstraněna.
Stop je ručně kontrolovaný stav, nikoli tvrzení o pasivním zabrzdění zdvihu.

Katalogové tělo desky není její montážní rozteč. Nosiče proto přidržují
okraje a pásky. Powerbanka ani samet nesmějí nahrazovat doraz nebo šroub.
Druhá PCA, sedm svorek a další rezervy nejsou aktivní vnitřní výplň.

Aktualizace 10. 10. 2026 odstranila opracované kovové výztuhy, kotvy a
samostatný sledovač vačky. Konstrukční díly vznikají tiskem, spojovací
materiál zůstává standardní a neupravený. Čtyři tištěné kotvy lepíme na
holé dřevo DNA. Jejich odlité epoxidové zámky zajišťují mechanické zachycení
PETG; chemická adheze k plastu není potvrzená. Aktuální geometrie má
36 831,325 mm² lepených stop a 84 zámků; 0,5mm film a zámky potřebují
minimum 43,271 ml bez ztrát. BOM uvádí tři balení po 24 ml s rezervou.

## Externí podklady pro doplněný materiál

[Bison Epoxy Metal 24 ml, výrobek 6305443](https://www.bison.net/getmedia/d54542f5-f56f-40b2-9406-3ad692c92a30/Print%20PDF.pdf)
byl ověřen v oficiálním technickém listu 10. 10. 2026. Výrobce uvádí dřevo
mezi podklady, poměr 1:1, zpracovatelnost asi 45 minut při 20 °C,
fixaci 10 h a konečnou pevnost po 24 h. Jde o pastovitý/tixotropní epoxid,
proto komory předplnit z otevřené spodní strany dodanou stěrkou před
usazením kotvy; Ø2,5mm horní otvory slouží jen odvzdušnění a kontrole.
Údaj 22 MPa je pro hliník a není důkazem pevnosti tohoto dřeva ani PETG.
Mokré přebytky odstranit před vytvrzením a kapsy matic/šroubové otvory
chránit; konstrukce nevyžaduje opracování vytvrzeného spoje. Nic nebylo
objednáno. Výpočet filmu a zámků je CAD minimum; skutečné nanesení a spotřebu
ověřit vzorkem před lepením sestavy.
