# Předání F1 – 9. 10. 2026

Rozsah je výhradně nový mechanický návrh v `kufr/mechanika/final/`.
Starší CAD, elektrické zapojení, aplikace, firmware a nasazení se nemění.
Záznam zůstává v určené složce, jak požaduje toto zadání. Historický globální
stav není přepsán na tvrzení o fyzicky hotovém kufru.

Dokončené zdroje: nový šestidílný korpus a šestidílné prázdné víko,
šest drážkových zdvihů s brzdou, montážní zóny pro potvrzený hardware,
otevřená kapsa banky, čtyři kryty se clonami a skrytými kolébkami,
kabelové a konektorové rezervy, servisní a transportní uspořádání.
Původní tisknutelné tvary ani kloubové mechanismy nebyly importovány.
Návrh, implementaci, review zdrojů a validaci dělal jediný agent.

Kontrolovatelný výsledek: parametrické Python/JSON zdroje, 29 typů STL
(44 výtisků s měrkou), STEP díly a pět sestav, skutečné rendery, samostatný
WebGL prohlížeč, dva SVG výkresy, kusovníky, návod a balíček ZIP.

Autoritou provedených kontrol je aktuální `exports/verification.json`
a shoda jeho zdrojových SHA-256. `Regenerate.ps1` balení zastaví při selhání.
Kontroly zahrnují validitu jediného pevného tělesa každého výtisku,
orientaci/podložku včetně brimu, zpětné načtení STEP, objem a topologii STL,
tři sestavy, 81 materiálových poloh mechanismu, 36 poloh víka, 37 kroků
vyjmutí banky, rezervy konektorů a kolize s montážními maticemi.
Nejde o spojitý důkaz všech poloh ani simulaci pružného kabelového svazku.

Při review byly řešeny skutečné nálezy: průniky stolkových noh s rámy,
servisní prostor USB, podhled do elektroniky, kontakt bočních krytů
s kováními, retence stolků a přístup k hlavám/maticím, pozice náhradních
PCB vůči šroubům a pozitivně řízený návrat proti přibrzdění. Opravy jsou
součástí nového výsledku; staré exporty odstraněných dílů generátor uklízí
jen podle vlastního předchozího manifestu.

Finální kontrola zahrnuje také hlavu, pouzdro a matici válečku, zapuštěné
matice servopáky a obě sousední přihrádky při všech 81 kontrolovaných polohách.
Kontakt matice se sousedním LED páskem byl odstraněn přesunem LED na pravou
hranu lůžka. Tisková síť vačky byla opravena nahrazením drobných hran
hladkými přechody a skutečnými kruhovými klidovými úseky.

Neprovedeno: tisk, osazení, měření serva a brzdy, reálné přenášení, testy
lepení/sametu, teploty/proudy, akustika, skutečné zorné pole/FPC kamery.
Polohy osy a otvorů konkrétní servopáky, tloušťka IP65, rozměry svorek
a detail konektorů nejsou vydávány za změřené. Fyzická kritéria E5/E6/B1/E13
zůstávají otevřená; žádný firmware se nekalibroval ani nenahrával.

Další fyzický krok: měrka, jeden modul s hůlkou do návrhových 150 g,
pasivní držení po vypnutí ve všech polohách a zatížený návrat. Pokud nelze
současně splnit tření brzdy a sílu serva, neprohlašovat konstrukci za
akceptovanou a neopakovat ji šestkrát; upravit mechanismus podle měření.

Předání zůstává lokální. Existující stav tohoto worktree zaznamenává zákaz
push; jeho předchozí nepublikované změny se neodesílají spolu s tímto návrhem.
Žádný nákup, push/PR/merge, publikace, cloudové nasazení nebo hardwarový zásah
neproběhl. Aktuální případný lokální commit lze z této složky dohledat `git log -- .`.
