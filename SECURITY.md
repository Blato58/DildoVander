# Bezpečnost

Credentials, provider klíče a Apple podpisové identity patří do oddělených
GitHub Environments nebo servisního provisioningu. PR CI nemá produkční ani
podpisové secrets a nepoužívá produkční self-hosted runner.

Při nalezení zranitelnosti kontaktuj majitele repozitáře `@Blato58` neveřejným
kanálem. Je-li dostupné GitHub private vulnerability reporting, použij jej.
Do issues, logů a PR nevkládej klíče, tokeny, certifikáty ani osobní data.
Uniklý credential ihned odvolej; smazání z aktuálního souboru nestačí.

Nasazení ověřuje úspěšný build, původ artefaktu, sloučené PR, shodu zdrojového
stromu s `main`, SHA-256 a bezpečné rozbalení. Nasazovací job před aktivací
kontroluje, že se `main` během čekání nezměnil. Ruční rollback staršího release
řeší správce podle [backend návodu](kufr/backend/README.md).
