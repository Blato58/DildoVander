# Lokální fonty

Cinzel variable (hlavní nadpisy používají Semibold 600), Inter variable
a IBM Plex Mono Regular jsou uloženy lokálně. Žádný runtime požadavek na
Google Fonts ani jiné vzdálené CDN není potřeba. Při nedostupném fontu CSS
volí Georgia, systémové sans-serif a Consolas; `font-display: swap` zachová text.

Zdroj: [Google Fonts](https://github.com/google/fonts), commit
`bd8f81ddb5c74d5c8897b36ad88b440266245103`.

| Lokální soubor | Původní soubor | Licence |
| --- | --- | --- |
| `cinzel.ttf` | `ofl/cinzel/Cinzel[wght].ttf` | [SIL OFL](cinzel-OFL.txt) |
| `inter.ttf` | `ofl/inter/Inter[opsz,wght].ttf` | [SIL OFL](inter-OFL.txt) |
| `ibmplexmono.ttf` | `ofl/ibmplexmono/IBMPlexMono-Regular.ttf` | [SIL OFL](ibmplexmono-OFL.txt) |

Fontové bajty nejsou změněny, pouze mají kratší lokální název souboru.
Při kopírování/vložení do distribuovaných artefaktů zachovejte příslušné
licence v balíčku. Pro web server povolte pouze místní zdroj fontů v CSP.
