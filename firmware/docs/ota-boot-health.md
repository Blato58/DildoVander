# OTA boot a kontrola hardware

Potvrzení nového obrazu ověřuje místní běh programu. Neznamená potvrzení polohy
hůlek ani povolení pohonů. Produkční kompozice předává do `BootSelfTestReady`
skutečný `PhysicalEffects`; pro zvolený hardwarový profil vyžaduje platnou
kalibraci a pasivní zdraví adaptéru. Zdraví runtime zahrnuje inicializaci NVS,
čitelný journal, spuštění síťového adaptéru a dostupnost použitého offline audia.
Spuštění adaptéru je jiný údaj než získání IP nebo připojení hotspotu.

`PhysicalEffects::PassiveHealthy()` vyžaduje konfiguraci, alespoň jedno úspěšné
čtení vstupů a nepřítomnost skutečné latched chyby. Selhání `FullOff`, čtení
vstupů nebo hlídané kadence zůstává chybou i po dalším dobrém vzorku.
`Healthy()` navíc vyžaduje dokončenou ruční kontrolu. Pouze explicitní
`RecoverAfterInspection()` potvrzuje ručně obnovený klidový stav; časované
servopohyby nadále nejsou měřením polohy.

Pending OTA vyhrazuje zařízení pro ověření. Po pěti souvislých sekundách
místního zdraví může potvrdit obraz s vypnutými výstupy a stále požadovanou
kontrolou obsluhy. Nepřítomnost hotspotu, IP nebo poskytovatele toto potvrzení
neblokuje. Skutečná místní chyba je nepotvrdí; původní dvouminutový rollback
deadline zůstává. Selhání zápisu potvrzení neoznačí obraz za platný.

Potvrzení, rollback a restart vykonává OTA worker; řídicí Tick pouze předá
požadavek. Worker vzniká také pro pending boot bez síťové OTA konfigurace.
Pomalý či neúspěšný zápis neobnovuje původní absolutní deadline. Ztráta pasivního
zdraví před závěrečnou autorizací zneplatní potvrzení a ponechá rezervaci pro rollback.
Selhání vytvoření workeru ani nedostupný rollback nepředstírají platný obraz.

Před SDK zápisem VALID worker uloží 48bytovou CRC pojistku svázanou s aktuálním
slotem a ELF digestem. Přerušené potvrzení proto drží rezervaci také po restartu,
i když rollback SDK selhal. Čerstvé úspěšné potvrzení jednou autorizuje odstranění
pojistky ještě před tímto I/O; pozdější změna času či zdraví tuto autorizaci
neobnovuje ani zpětně neruší. Selhání odstranění drží aktuální boot. Při restartu
je rozhodující skutečně chybějící pojistka po autorizovaném odstranění, nikoli
ztracený ACK. Nečitelná, nesouhlasící nebo poškozená pojistka vyžaduje servis;
nepovoluje automatické potvrzení ani odstranění.

Vyhledání a stažení aktualizace dál vyžaduje síť a bezpečný idle. Podepsaný
manifest, ověření přesného artefaktu a instalace do neaktivního slotu se nemění.
Otevřený journal po přerušení se samotným potvrzením obrazu neuzavře. Po bootu
musí obsluha zkontrolovat kufr a použít existující místní zotavení, než začne
pohyb. OTA ověření ruční zotavení nenahrazuje.

## Reprodukovatelné host kontroly

Z kořene repozitáře s C++14 kompilátorem (na Windows i VS Build Tools):

```sh
python firmware/tests/run_show_tests.py --only firmware_update_test --only espidf_firmware_update_test --only physical_effects_test --only show_runtime_test --only device_control_runtime_test --only dance_coordinator_test
```

Adapter test skládá skutečný journal, efekty a OTA s falešným platformovým I/O.
Ověřuje potvrzení bez sítě a obsluhy, zachování pending záznamu, nulové povolení
pohonů, ztrátu IP, restart zdravého intervalu a odmítnutí při vadné kalibraci,
úložišti nebo I/O. Host test není fyzický power-cut test NVS ani měření OE.

## Neprovedená kontrola na prototypu

Pro konkrétní podepsaný produkční obraz zaznamenej build ID, SHA-256, profil,
revizi kalibrace, reset reason a stav obou OTA slotů. S odpojeným napájením
pohonů ověř pending boot bez hotspotu, ztrátu iPhone hotspotu během stabilního
intervalu, potvrzení obrazu a stále vyžadovanou kontrolu. Změř, že OE zůstalo
v bezpečném stavu. Ověř skutečnou chybu adaptéru a návrat do předchozího slotu.

Samostatně ověř výpadek napájení při instalaci, bootu a zápisu potvrzení,
zachování provisioning/kalibrace a pending operace. Až po ruční kontrole ověř
stávající zotavení a kalibrovaný pohyb. Mikrofon, reproduktory a kamera vyžadují
samostatnou kontrolu s reálným iPhone hotspotem. Tyto kroky jsou připravený
bench checklist; jejich sepsání nedokládá provedení.
