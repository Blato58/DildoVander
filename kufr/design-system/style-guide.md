# Styl a tokeny

## Charakter dílny

Poměr 80 / 15 / 5 je návrhová pomůcka: 80 % klidná technická srozumitelnost,
15 % řemeslný charakter mosazi, geometrie a materiálu, 5 % magie v názvu,
symbolu nebo krátkém překvapení. Není to plošná kvóta barev. V bezpečnostním
stavu dominuje jasný technický význam.

Budujeme vlastní kouzelný svět DildoVanderu: dílna, kufr, hůlky, rituál a
mechanická řemeslnost. Nekopírujeme rozpoznatelné znaky, postavy, typografická
loga ani slovník cizí fantasy značky. Humor je doplňkový komentář; nikdy
nenahrazuje název akce, stav, chybu, bezpečnostní upozornění nebo instrukci.
Stop se vždy jmenuje Stop. Časování, servisní postup a nejistá poloha mají
věcný popis i tehdy, když sousední doplňkový text obsahuje vtip.

## Barva, typografie a prostor

Základ je tmavá, přehledná dílna. Cinzel Semibold v hlavním titulku a značce,
Inter pro ovládání a text, IBM Plex Mono pro revize, časy a technické údaje.
Cinzel nepatří do dlouhého textu, tabulek ani tlačítek. Používejte sentence
case v češtině; kapitálky pouze u krátkého metadatového štítku.

| Token | Hodnota | Použití |
| --- | --- | --- |
| `--aw-void` | `#0D1218` | Pozadí a zapuštěné oblasti |
| `--aw-surface` | `#171F29` | Karty, postranní navigace |
| `--aw-elevated` | `#25313D` | Vybrané položky, zvýšené vrstvy |
| `--aw-brass` | `#D9B777` | Hlavní akce, výběr, značka |
| `--aw-ivory` | `#F3ECE0` | Primární text |
| `--aw-muted` | `#AAB7C2` | Pomocný text |
| `--aw-success` | `#79C6A4` | Úspěch s textovým označením |
| `--aw-danger` | `#E7807C` | Stop a chyba s textovým označením |
| `--aw-warning` | `#E3BC73` | Simulace a upozornění |
| `--aw-info` | `#8CBBDD` | Informace a focus |
| `--aw-border` | `#6B7E8E` | Hranice vstupů a ovládacích prvků |
| `--aw-border-subtle` | `#34424F` | Dekorativní oddělení karet |

Primární tlačítko má mosazné pozadí a text `--aw-void`. Stavové barvy používejte
pro text/obrys na tmavé ploše, nikoli pro bílý text na světlém stavovém pozadí.
Hranice vstupu musí zůstat čitelná proti zapuštěnému pozadí. Dekorativní
hranice není jediným vodítkem pro kliknutí.

Základní jednotka je 4 px; běžné mezery jsou 8, 16, 24 a 32 px. Karta má
radius 12 px a padding 24 px (na mobilu 16 px), tlačítko a vstup radius 8 px.
Dotykový cíl je nejméně 44 × 44 px. Stavové štítky jsou neinteraktivní.
Nepřidávejte blikání, textury pod telemetrii ani světelné efekty, které
vypadají jako potvrzení skutečného pohybu.

`tokens/` definuje barvy, typografii a prostor; `components/patterns.css`
definuje opakovatelné vzory. Sémantický stav musí vycházet ze skutečných dat;
design systém nevyhodnocuje dostupnost, lease, kalibraci ani připojení.

## Pohyb a ikony

`tokens/motion.css` definuje `--aw-motion-fast: 150ms` pro hover/focus změnu,
`--aw-motion-normal: 200ms` pro běžné otevření panelu a
`--aw-motion-slow: 250ms` pro větší přechod rozložení. Nevkládejte animační
prodlevu před Stop, chybový stav nebo změnu dostupnosti. Tyto délky neřídí
servo, firmware ani modelovanou fyzickou sekvenci. V Reduced Motion jsou
všechny tři tokeny `0ms`; nevynucujte náhradní zoom, posun či pulzování.

Kanonické ikony jsou [lokální Lucide outline SVG](icons/README.md): viewBox
24 × 24, tah 2 px, kulaté konce a rohy, `currentColor`, bez výplně.
Ikona nenahrazuje čitelný název ani stav. Stávající znaky ve spotřebitelích
jsou přechodný stav; tato dodávka je nemigruje a nepřidává npm závislost.
