# Předání F2 — 10. 10. 2026

F2 je vložka do skutečného dřevěného kufru. Nahrazuje nový tištěný
korpus F1, původní plstěnou brzdu a stará či nedostatečně upevněná servisní rozhraní.
Zachovává šest přihrádek, potvrzený inventář a elektrické zapojení.

## Rozhodnutí

- Bezpečný kvádr 400 × 240 × 170 mm podle majitele, klidová zástavba pod 114,3 mm.
- Čtyři PETG panely s integrovanými žebry; čtyři tištěné podlahové kotvy
  se spodním filmem a odlitými epoxidovými zámky na holém dřevu DNA.
- Vačku sleduje integrální Ø6 PETG čep lůžka, tisk +90° kolem Y čepem
  vzhůru. Vůli řeší profil/parametr a nový tisk. Konstrukční montáž
  nepoužívá pilník, pilku ani vrtačku; brusný papír pouze na lepené dřevo.
- Zdvih 16 mm zachycenou vačkou, skutečné dolní podpěry, šest ručních pásků.
- Elektronika má tvarové dorazy, pásky a vlastní nosiče. Hůlky, banka
  a textil nejsou součástí nosné cesty. Kufr se nevrtá ani neřeže.
- ESP/kamera vzadu uprostřed, mikrofon vpředu uprostřed, reproduktory po stranách.
- 135 samostatně identifikovaných vodičů se společnými daty pro výkres/viewer.
- Montáž má 15 kroků: měrka → rám a nosiče na stole → lepení/přejímka
  kotev a boční vložení matic → rám 4 × M4 × 10 → moduly 24 × M3 × 14.
  Provoz/servis/přeprava jsou explicitní stavy.
- Hlavní i krátká choreografie pochází z jednoho souboru pro firmware a náhled.

## Aktuální změna montáže

Zjednodušení 10. 10. 2026 mění geometrii, BOM a montážní pořadí. Návrh má
37 typů STL / 52 výtisků včetně dvoudílné měrky. Výrobní výstupy přijmout
pouze s aktuálním `exports/verification.json` a manifestem, jejichž zdrojové
SHA-256 odpovídají nynějším souborům. Historické ověření tuto variantu
nepokrývá. Fyzická přejímka zůstává nesplněná.

GitHub review PR #172 doplnilo do měrky také skutečné boční kapsy M3/M4
včetně uzavřených střech a slepého dna. První tisk tím ověří osazení matic
ještě před tiskem čtyř velkých kotev; matice se po zkoušce použijí v sestavě.
Úpravu pokrývá regresní kontrola shody profilů a zasunutí matic bez kolize.
Navazující připomínka doplnila do téže měrky i přesnou produkční dutinu
epoxidového zámku včetně spodního vstupu a obou horních odvzdušnění.
Před tiskem kotev se zkouší přemostění i naplnění odspodu; geometrie je
sdílená s kotvami. Počet typů a výtisků tím zůstává nezměněný.

Aktuální kotvy mají 36 831,325 mm² lepených stop a 84 zámků; minimum
spodního filmu a zámků je 43,271 ml bez ztrát. BOM uvádí tři balení
Bison 24 ml, s rezervou přibližně 28,729 ml pro vzorek a ztráty. PETG
kotvy se předplní odspodu; horní porty jsou kontrolní/odvzdušňovací.
Boční vložení 24 M3 a 4 M4 matic před instalací rámu kryje souvislá
3mm střecha. Moduly mají vlastní 24 M3 × 14, rám 4 M4 × 10 ISO4762;
8 M3 × 10 zůstává pouze pro kryty. Integrální čep vyžaduje společné
nasazení a servisní sejmutí lůžka, vačky a serva; dva pásky serva se při
servisu odstřihují a nahrazují.

Pevnostní posouzení používá celý 2D tenzor skupiny zámků i lepené stopy,
nejhorší vodorovný směr a sílu umístěnou postupně do každé skutečné matice.
Zachovává 5 kg, 3 × g, dvě zatížené kotvy a rameno 170 mm. Původní
zámky Ø6/Ø9 tomuto posouzení nevyhověly; nahrazují je zámky Ø7,5/Ø10,5
s upraveným rozložením při nezměněném vnějším tvaru kotev. Nejnižší poměr
předpokládané meze k napětí krčku je 1,473. Meze materiálu nejsou měřené.
Regenerace zahrnuje regresní kontrolu původního nevyhovujícího rozložení,
porovnání s hustým vzorkováním směrů a rovnováhu sil/momentů. Nevyhovující
výpočtový výsledek zabrání vystavení `verification.json`. Aktuální běh,
hash balíčku a Git předání zaznamenává [stav implementace](../../docs/stav-implementace.md).
Fyzické nasazení čepu, spoj dřevo/epoxid, střechy matic a pohyb nebyly testovány.

## Ověřování a důkazy

Autoritou geometrického výsledku je `exports/verification.json`, který
obsahuje přesné SHA-256 zdrojů, počty dílů a provedených kontrol. Úspěšný
status je výhradně `PASS_CAD_NOT_PHYSICAL_ACCEPTANCE`. Balicí skript
nepřijme starší ověření, změněné zdroje ani chybějící aktuální soubory.
`generation-snapshot.json` svazuje výstupy generování s aktuálními zdroji;
ověření zaznamenává také otisky vieweru, výkresů, CSV a obrázků. Balení je
pouze porovná s ověřeným stavem, nevytváří dodatečnou důvěru změněným souborům.
Počet typů STL, počet výtisků a tiskové rozměry jsou v `exports/print-bom.csv`.
Samotné vygenerování vieweru či ZIPu není fyzická přejímka.

Samostatná kontrola firmware 10. 10. 2026 před rozšířením generátoru všech
fází choreografie: cílový ESP-IDF build prošel,
RAM 91 644 B, flash 1 746 349 B. Hostitelský test fyzických efektů prošel.
Nezávislá kontrola kódu ověřila Stop, časování, souběh hlasu a akcentů;
velký lokální PCM buffer byl přesunut mimo zásobník. Objektový kód
`PumpCue` má zásobníkový rámec 96 B proti původním 5 216 B. Se zásobníkovým
rámcem `Capture` je součet 6 384 B před dalšími voláními.
Skutečnou rezervu zásobníku a fungování I2S potvrdí až deska.

Po rozšíření generátoru všech fází choreografie znovu prošel cílový
ESP-IDF build: RAM 91 644 B, flash 1 746 709 B. Hostitelský test ověřuje
barvy/tóny jednotlivých fází, kalibrovaný pohyb a vypnuté PWM během
závěrečných 250 ms klidu před obnovením dostupnosti. Zdrojové kontroly
odmítají nepodporované kanály, souběhy a hodnoty zvuku.

Následné P1 review doplnilo absolutní limit celé prezentace 15 s.
Kontrola předchází obsluze fyzických výstupů i přechodu do další fáze;
příprava, kalibrované pohyby ani závěrečný klid limit neobnovují.
Hostitelské regrese prošly v každé aktivní fázi, při návratu hodin zpět,
při opožděném dokončení i s kalibrací 3 000 ms a průchody po 100 ms.
V posledním scénáři se v 15 s vypne právě běžící návrat, zápis nedokončené
prezentace zůstane zachován a dostupnost se neobnoví bez ruční kontroly.
Probuzení osvítí jen vybranou přihrádku. Generátor odmítá více skupin;
výstupní vrstva přijme pouze nulovou nebo jednobitovou LED masku.
Regrese ověřila všech 256 masek i skutečně zapsané LED snímky.
Náhled dodržuje stejný 15s limit a prošlo všech 20 testů vieweru.
Pasivní USB zásuvka a 300mm kabel jsou povolené doplňky podle explicitního
oddílu aktuálního zadání; oprávnění a stav „dokoupit“ dokládá SPECIFIKACE.md.
Nezávislá kontrola oprav je bez otevřeného nálezu. Následný cílový ESP-IDF
build prošel: RAM 91 660 B, flash 1 746 865 B. Nahrání do desky neproběhlo.

Následující P2 review doplnilo validaci časového rozpočtu upravovaných
efektů a sjednotilo README firmwaru s omezením na jediný LED úsek.
Generátor započítá dvě maximální 3 000ms jízdy, zaokrouhlení každé fáze
na 10ms průchody a 20 ms předání mezi fázemi. Hlavní varianta má
nominální rozpočet 14 790 ms a krátká 11 970 ms. Náhled samostatně
započítává první interval po spuštění, snímky a přechody při nominálních
60 Hz; hraničních 900 snímků
(15 s) odmítne. Všech 15 regresních testů efektů prošlo. Aktuální data
efektů, generovaná hlavička a runtime firmwaru se touto validací nezměnily.
Pomalejší běh nadále zastaví absolutní runtime limit, nikoli prodloužení času.

Náhled měří tento limit od přijetí kliknutí na spuštění monotónními
hodinami prohlížeče. Uspání karty před prvním snímkem neobnoví časový
rozpočet; po vypršení se první opožděný snímek zastaví před výstupy.
Aktuální čas se kontroluje při každém průchodu, nezávisle na starší časové
značce snímku. Všech 27 testů vieweru a 15 testů efektů prošlo; regrese
opožděného prvního snímku před opravou selhala. Nezávislý test skutečné
funkce vieweru potvrdil dokončení při 899 a zastavení při 900 snímcích.
Nominální náhled má 672 snímků / 11 200 ms pro hlavní variantu a
484 snímků / přibližně 8 066,67 ms pro krátkou.

Historické ověření před zjednodušením 10. 10. 2026 (kovové kotvy, výztuhy
a samostatný sledovač; není platné pro nynější variantu): 32 typů STL /
47 výtisků, platná
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

Historická kontrola prohlížeče před změnou geometrie a montážního pořadí
na `http://127.0.0.1:8915/viewer.html` byla ověřena:
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
| Dřevo/lepidlo/nosné spoje | Přejímka holého připraveného dna, vyplněných epoxidových zámků a tištěných střech matic podle MONTÁŽ.md |
| Servo dole bez držení | Změřit proud a reakci po FULL_OFF a porovnat je s odpojeným V+ |
| Zdvih | Zatížené cykly, kalibrace, proud a oteplení prvního a následně všech modulů |
| Kabely | Reálné poloměry, svorkování, polarita, pohyblivé rezervy a vyjmutí banky |
| Přeprava | Zkouška prázdné aretované sestavy při nesení a jízdě |

Žádná položka této tabulky nebyla splněna renderem, testem ani buildem.
Bez fyzické přejímky se nepoužívá označení připraveno k výrobě/provozu.
Není přidaná nová funkční elektronika. Nic nebylo nakoupeno, firmware
nebyl nahrán do desky, neběžel placený AI test ani serverové nasazení.
