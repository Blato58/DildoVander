# Inventář personalit kufru

| ID | Jméno v iOS aplikaci | Zdroj personality | Průběh |
| --- | --- | --- | --- |
| `party-original` | Původní party kufr | [voice-persona.txt](voice-persona.txt) | Dosavadní výchozí personalita, obvykle 4–7 otázek. |
| `ollivander-trunk` | Ollivanderův kouzelný kufr | [ollivander-trunk-persona.txt](ollivander-trunk-persona.txt) | Dodaný text beze změn, standardně výběr po dvou odpovědích; dříve při dostatku podkladů nebo výslovné žádosti. |

Nová personalita je uložená v UTF-8 včetně původních příkladů, vulgarit a
nadpisu `KDYŽ JE TICH0`. Její SHA-256 je
`99a72891209ef2bf5ad5a0120a7998c5a08bf46317f38812fb1d3816a5c3099d`;
backendový test hlídá přesné bajty embedded zdroje. Původní soubor se nemění.

## Přepnutí a test v iOS

1. V **Debug → Server** nastavte existující přístup správce a obnovte stav.
2. Vyberte **Personalita** a stiskněte **Použít nastavení serveru**.
3. Ve **Zkušební rozhovor a výběr hůlky** ověřte zobrazené jméno aktivní
   personality a použijte **Otestovat personalitu · AI** nebo **· fake**.

AI test používá zvolený tok Realtime, GPT-Live + Sol nebo řetězený hlas a
může čerpat dosavadní limit 3 USD na sezení. Fake ověřuje cestu a počet
otázek (původní čtyři, nový kufr dvě); negeneruje charakter ani humor a
vrací tiché WAV. Test používá simulované přihrádky a neovládá pohony.
Pro srovnání s čistou pamětí lze zvolit nový testovací večer.

Volba platí pro celý backend včetně samostatného ESP32 a přežije restart
v SQLite journalu. Existující instalace začíná s `party-original`.
Změna vyžaduje admin credential, aktuální revizi a žádný aktivní rozhovor
ani dobíhající provider volání; sama AI nevolá. Původní klient, který
`personalityId` neposílá, zachová současnou volbu. Nová aplikace u staršího
serveru ukáže potřebu aktualizace a přepnutí nenabízí.

Backendový [PersonalityCatalog](../backend/Features/Conversations/PersonalityCatalog.cs)
je zdroj inventáře pro iOS. Obsah se skládá z jediné vybrané personality
a stávajících pravidel funkcí/katalogu; Live má vlastní delegaci na Sol.
Původní čtyřotázková brána a opening zůstávají jen původní personalitě.
Nová vyžaduje úvodní otázku, další rozhodnutí nechává pravidlům promptu.
Delší prompt má vlastní přírůstek limitu requestu a nákladové rezervace;
celkový limit sezení se nezvyšuje.

Další personalitu přidejte jako samostatný embedded UTF-8 zdroj do
`backend.csproj`, do katalogu s jedinečným stabilním ID a její politikou
otázek. iOS načítá položky ze serveru a nevyžaduje nový hardcoded seznam.
Python evaluátor nadále používá původní výchozí personalitu; volba serveru
jej nepřepíná. Tón a věrnost živého modelu vyžadují poslech, offline testy
ověřují předané instrukce a řízení.
