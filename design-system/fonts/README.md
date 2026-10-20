# Lokální fonty

Aktivní UI Vapour Chrome používá Inter variable pro text, ovládání, značku
a všechny nadpisy. IBM Plex Mono Regular používá pouze pro technické údaje.
Fonty jsou lokální; za běhu se nepřipojuje Google Fonts ani jiné CDN.
Fallback je systémový sans-serif a Consolas, `font-display: swap` zachová text.

Zdroj: [Google Fonts](https://github.com/google/fonts), commit
`bd8f81ddb5c74d5c8897b36ad88b440266245103`.

| Lokální soubor | Původní soubor | Licence |
| --- | --- | --- |
| `inter.ttf` | `ofl/inter/Inter[opsz,wght].ttf` | [SIL OFL](inter-OFL.txt) |
| `ibmplexmono.ttf` | `ofl/ibmplexmono/IBMPlexMono-Regular.ttf` | [SIL OFL](ibmplexmono-OFL.txt) |
| `cinzel.ttf` | `ofl/cinzel/Cinzel[wght].ttf` | [SIL OFL](cinzel-OFL.txt) |

Cinzel je zachovaný historický asset pro existující archivní artefakty;
aktivní CSS jej neregistruje ani nepoužívá. Fontové bajty nejsou změněny.
Při distribuci zachovejte příslušné licence a pro web povolte místní fonty v CSP.
