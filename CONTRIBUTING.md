# Vývoj a předání

Zdroj postupu je [AGENTS.md](AGENTS.md), architektura je v
[plánu](kufr/docs/implementacni-plan.md) a důkazy ověření ve
[stavu implementace](kufr/docs/stav-implementace.md).

1. Pracuj ve vlastní větvi nad aktuálním `main`; změny předávej PR.
2. Používej připnuté SDK, `npm ci` a NuGet locked restore. Generované výstupy
   ani přístupové údaje necommituj.
3. Ověř dotčený projekt a uveď skutečně provedené testy, blokace a další krok.
4. Před merge aktualizuj větev z `main`. PR s kódem musí mít úspěšné
   **CI required** pro dotčené projekty. U změn samotné dokumentace CI
   neběží; ověř odkazy, konzistenci a `git diff --check`.
5. Nasazuj existující ověřený artefakt podle [CI/CD návodu](docs/ci-cd.md).

Dependabot otevírá týdenní skupinové PR pro Actions, npm a NuGet. Aktualizace
SDK, Xcode a PlatformIO se kontrolují ručně společně s jejich lock/pin soubory.
CODEOWNERS určuje vlastníka review. Vynucení ochrany větve závisí na dostupnosti
GitHub rulesets; skutečná omezení přístupu a tarifu jsou popsaná v CI/CD návodu.
