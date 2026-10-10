# Předání F2 — 10. 10. 2026

F2 je vložka do skutečného dřevěného kufru. Nahrazuje nový tištěný
korpus F1, původní plstěnou brzdu a stará či nedostatečně upevněná servisní rozhraní.
Zachovává šest přihrádek, potvrzený inventář a elektrické zapojení.

## Rozhodnutí

- Bezpečný kvádr 400 × 240 × 170 mm podle majitele, klidová zástavba pod 114,3 mm.
- Čtyři tištěné panely a dvě Al lišty; čtyři lepené Al kotvy k holému dřevu.
- Zdvih 16 mm zachycenou vačkou, skutečné dolní podpěry, šest ručních pásků.
- Elektronika má tvarové dorazy, pásky a vlastní nosiče. Hůlky, banka
  a textil nejsou součástí nosné cesty. Kufr se nevrtá ani neřeže.
- ESP/kamera vzadu uprostřed, mikrofon vpředu uprostřed, reproduktory po stranách.
- 135 samostatně identifikovaných vodičů se společnými daty pro výkres/viewer.
- Montáž má 15 kumulativních kroků; provoz/servis/přeprava jsou explicitní stavy.
- Hlavní i krátká choreografie pochází z jednoho souboru pro firmware a náhled.

## Ověřování a důkazy

Autoritou geometrického výsledku je `exports/verification.json`, který
obsahuje přesné SHA-256 zdrojů, počty dílů a provedených kontrol. Úspěšný
status je výhradně `PASS_CAD_NOT_PHYSICAL_ACCEPTANCE`. Balicí skript
nepřijme starší ověření, změněné zdroje ani chybějící aktuální soubory.
Počet typů STL, počet výtisků a tiskové rozměry jsou v `exports/print-bom.csv`.
Samotné vygenerování vieweru či ZIPu není fyzická přejímka.

Samostatná kontrola firmware 10. 10. 2026: cílový ESP-IDF build prošel,
RAM 91 644 B, flash 1 746 349 B. Hostitelský test fyzických efektů prošel.
Nezávislá kontrola kódu ověřila Stop, časování, souběh hlasu a akcentů;
velký lokální PCM buffer byl přesunut mimo zásobník. Objektový kód
`PumpCue` má zásobníkový rámec 96 B proti původním 5 216 B. Se zásobníkovým
rámcem `Capture` je součet 6 384 B před dalšími voláními.
Skutečnou rezervu zásobníku a fungování I2S potvrdí až deska.

Kontrola zdrojové geometrie: 32 typů STL / 47 výtisků, platná
jednotlivá tělesa a tisková plocha 220 × 220 mm. Montáž pokrývá 589 dílů
ve scéně a všech 135 vodičů; počty spojovacího materiálu odpovídají CSV.
Rozsah celé regenerace zahrnuje tři celé sestavy, 102 poloh mechanismů
se sousedy, 36 poloh víka, 37 kroků vyjmutí banky a 24 dráh nástroje.
Kabelový audit ověřuje plné obálky vodičů včetně rohů: klid s kryty,
zavřenou přepravu a každou přihrádku v polohách 0/80/160°.
Kontrola navíc požaduje oddělení vzdálených úseků téhož vodiče,
zaplnění 102 zadních vedení a šesti lokálních pruhů, stabilní otisk
zdrojů a úplný výstup. Výsledek regenerace, počty skutečných porovnání
a aktuální otisky potvrzuje výhradně uvedený JSON.

Při CAD review byly opraveny retenční hlavy/matice vodítek, sdílení
konektorových obálek, skutečné šestihranné kapsy matic a kontrola kontaktu
s materiálem dolních podpěr. Kontrola neuzavírá níže uvedené fyzické body.

Logika prohlížeče na `http://127.0.0.1:8915/viewer.html` byla ověřena:
15 kroků vpřed, krok zpět, restart, všech šest ručních zdvihů 16 mm,
volba vodiče D2-2, síť DRIVE5, pin CH0, svazek I2C, Stop a servisní reset.
Přehrána celá hlavní sekvence pozice 1 a krátké sekvence pozic 2–6,
vždy návrat do klidu. Ověřen viewport 390 × 844 i běžný panel, bez chyb
konzole; 18 regresních testů vieweru prošlo. Skutečný Shift klik byl
ověřen na degson_2:2 (D2-2), servo_1:S (S1-S) a esp32:GPIO1 (I2C),
včetně šesti zvýrazněných vodičů sběrnice při zvětšení displeje 125 %.
Nepřipnutá karta již nezachytává klik určený vodiči.
PR #170 doplnilo regresi přerušení při změně režimu: po Stopu se zachovají
všechny úhly a další režim je servis. Přepravu, montáž i provoz odblokuje
až explicitní servisní reset; přepnutí samo neobnovuje klidovou polohu.
Přepnutí do přepravy ukazuje
pět kroků přípravy a čtyři kroky opětovného uvedení do provozu.
Finální geometrie a kabeláž se po dokončení generují znovu; přesné
integrační kontroly a Git předání se zapisují do hlavního stavu projektu.

## Dosud nesplněné fyzické podmínky

| Podmínka | Další konkrétní krok |
| --- | --- |
| Skutečný kufr/víko/konektory | Změřit a nasucho vložit měrku a rám, ověřit celý chod víka |
| Hůlky a případná pouzdra | Délku do 200 mm a šířku hlavice 38 mm majitel potvrdil 10. 10. 2026; šest pozic zůstává. Ověřit zbývající výškové/tvarové rozměry, hmotnost a skutečné usazení |
| Kamera/FPC a svorky | Potvrdit doručené rozměry, vývody a manipulační prostor |
| Servo a páka | Ověřit polohu osy, náboj, rozteč páky a dodaný středový šroubek na jednom modulu |
| Dřevo/lepidlo/nosné spoje | Vytvrzení a zátěžová přejímka podle MONTÁŽ.md |
| Servo dole bez držení | Změřit proud a reakci po FULL_OFF a porovnat je s odpojeným V+ |
| Zdvih | Zatížené cykly, kalibrace, proud a oteplení prvního a následně všech modulů |
| Kabely | Reálné poloměry, svorkování, polarita, pohyblivé rezervy a vyjmutí banky |
| Přeprava | Zkouška prázdné aretované sestavy při nesení a jízdě |

Žádná položka této tabulky nebyla splněna renderem, testem ani buildem.
Bez fyzické přejímky se nepoužívá označení připraveno k výrobě/provozu.
Není přidaná nová funkční elektronika. Nic nebylo nakoupeno, firmware
nebyl nahrán do desky, neběžel placený AI test ani serverové nasazení.
