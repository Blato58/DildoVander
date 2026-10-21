# SG90 servopáka — zkušební tisk pro trysku 0,4 mm

Oboustranná páka má délku 35 mm, rameno 6 × 2 mm, náboj Ø9 mm a celkovou
tloušťku 3,75 mm. Dva průchozí otvory Ø2,4 mm jsou od osy vzdálené 9 mm
(rozteč 18 mm) pro původní M2 spoje vačky. Pouzdro kopíruje 20 mělkých
drážek dodaného `reference/SG90-Servo.stp`; nejde o univerzální SG90 standard.
Identita `stock_horn_*` ve vieweru zůstává kvůli vazbám animace, geometrie je tištěná.

Nejdřív vytiskni `servo_horn_fit_coupon.stl`. Jedna, dvě a tři zářezy na okraji
označují maximální průměr pouzdra 4,95 / 5,00 / 5,05 mm. Měrka má stejný
náboj, hloubku a dno jako páka. Otvory zkoušej jednotlivě na odpojeném servu,
bez násilí a bez zatlačování páky středovým šroubem. Vyber nejmenší pouzdro,
které se plně usadí a nemá citelnou vůli. Pokud nevyhoví žádné, uprav fit
podle skutečného výtisku; nepřeměřuj celou páku procentním škálováním.

V sadě jsou odpovídající `servo_horn_495`, `servo_horn_500` a `servo_horn_505`
(STL, STEP, 3MF). Sestava používá prostřední variantu 5,00 mm. Všechny
exporty už leží správnou plochou na Z=0: rovná přední plocha ramene dolů,
otevřené drážkování nahoru. Neotáčet automaticky. PETG, tryska 0,4 mm,
vrstva 0,2 mm, 6 perimetrů, 100% výplň, podpěry vypnuté. Profil je obecný
auditní profil, nikoli G-code pro konkrétní tiskárnu.

Pouzdro má hloubku 2,95 mm, modelovou mezeru 0,15 mm za čelem hřídele
a 0,20 mm před osazením serva. Dno je silné 0,8 mm a má otvor Ø2,2 mm pro
původní středový šroub serva. Jeho závit, délka a průměr hlavy nebyly změřeny:
ověř dosednutí hlavy na dno, dostatečné zašroubování a že šroub nenarazí na
dno otvoru hřídele. Přístup vede středovým otvorem vačky 6,4 × 6,4 mm.

Drážky STEP jsou hluboké jen přibližně 0,16 mm. Slicer ani geometrická
kontrola neprokazují přenos momentu skutečného výtisku z trysky 0,4 mm.
Před zatížením ověř ručně usazení, vůli a protáčení, potom celé zdvihové
cykly s podepřeným lůžkem. Do té doby je díl prototyp, bez ověřené únosnosti.

Samostatné přegenerování sady bez ostatních dílů kufru:

```powershell
mechanika/final/.venv/Scripts/python.exe mechanika/final/export_servo_horn.py
```

Volitelné `--slicer C:/cesta/prusa-slicer-console.exe` provede skutečný audit
toolpathů a 3MF; výsledek je v `exports/servo-horn/slicer-validation.json`.
Sada ZIP neobsahuje strojový G-code. Fyzická zkouška ani kompletní výrobní
akceptace kufru nejsou tímto cíleným exportem nahrazeny.
