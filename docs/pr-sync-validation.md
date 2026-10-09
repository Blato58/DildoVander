# Ověření synchronizace PR před CI

Datum: 9. 10. 2026. Implementace je v PR [140](https://github.com/Blato58/DildoVander/pull/140).

- Lokální actionlint 1.7.12 prošel, download byl ověřen SHA-256.
- 55 Node testů prošlo, včetně opožděného dokončení aktualizace, změny base
  během čekání, obnovení dispatch a ztráty staršího pending PR eventu.
- Linux CI ověřilo všech 17 Python testů; na Windows má dosavadní
  executable-mode test odlišná POSIX práva.
- První [CI běh](https://github.com/Blato58/DildoVander/actions/runs/37876581300)
  odmítl zaostávající head/base ještě před všemi buildy.
- Kompletní [Linux CI](https://github.com/Blato58/DildoVander/actions/runs/37876637730)
  ověřilo repository checks, mobilní web, backend, host testy i všechny
  firmware profily. PR 140 bylo sloučeno jako `06661ce`; Git tree odpovídá
  otestovanému stromu finálního head `ca42330`.
- Tento dokument vznikl nad předchozím main `30fc338` před merge implementace.
  [Main-push controller](https://github.com/Blato58/DildoVander/actions/runs/37877486084)
  automaticky aktualizoval PR 141 z `592ff84` na `06533a9` a explicitně spustil
  [PR CI](https://github.com/Blato58/DildoVander/actions/runs/37877504456) na nové SHA.
  Freshness i CI required prošly; všech pět projektových buildů bylo
  správně vynecháno pro dokumentační diff. Ruční schválení nebylo potřeba.

Žádné nasazení, podepisování IPA, placené AI testy ani flash firmware
nejsou součástí tohoto ověření.
