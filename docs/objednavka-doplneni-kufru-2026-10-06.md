# Objednávka a potvrzená elektronika kufru

**Aktualizace 9. 10. 2026:** majitel v tomto zadání výslovně potvrdil nákup
položek ze dvou přiložených snímků (montážní seznam je datovaný 8. 10.).
Jejich množství jsou níže přidaná k dříve doloženým fakturám, nikoli
považovaná za elektricky ověřené zapojení. Původní objednávka byla z 6. 10.;
její staré součty 1 529/1 142 Kč už nevyjadřují zbývající nákup.

Máme **dva DFR0954 a dva 8Ω reproduktory**, měnič SEG-28582, MCP23017,
dva PH2 kabely, dva nové podsvícené kolébkové vypínače a montážní materiál.
Zelená kolébka bude servisní odpojení pohonů; jedna červená hlavní vypínač,
druhá červená rezerva. U dvojlinek je na snímku pouze „1×“: bez faktury
s jednotkou ani měření nelze tvrdit, že dorazilo původně navržených 5/2 m.

Powerbanka **UGREEN 20000mAh PD 100W (PB720 / 25188)** je zvolená
v předchozím zadání; oba nové nákupní snímky její koupi nedokládají.
Použije se s už potvrzeným PD triggerem nastaveným na 12 V a tímto 5V
měničem. Akyga není nutná při použití vhodné zvolené powerbanky.

Provozní limity: nejvýše jeden 20cm úsek WS2812B a jedno servo NEBO
jedna vibrace. Světlo může svítit s pohonem. PCA OE řídí GPIO2 přímo;
napájení pohonů odpojuje zelená servisní kolébka. Poloha serv se odvozuje
z kalibrovaných pulzů a času, fyzické měření E5/B1 ještě neproběhlo.

## 1. Botland – dosud nepotvrzené

| Stav | Položka / přímý odkaz | Množství | Orientační cena | Role |
| --- | --- | ---: | ---: | --- |
| ☐ | [USB-A zásuvka, modul](https://botland.cz/prislusenstvi-kontaktnich-desek/16154-modul-se-zasuvkou-usb-typu-a-pajene-konektory-5904422300692.html) | 1 | 21 Kč | Napájení ESP přes jeho USB-UART; vynechat, pokud máme vhodné hotové vedení |
| Volitelné | [Akyga AK-CH-31 USB-C PD 65 W](https://botland.cz/plug-in-napajeci-zdroje/27953-napajeci-zdroj-s-usb-typu-c-a-usb-typu-a-5-20v-135-3a-akyga-ak-ch-31-5906708750672.html) | 1 | 387 Kč | Jen náhrada powerbanky pro stolní napájení; není součást povinného zbytku |

Měnič, MCP a reproduktorové PH2 kabely už znovu neobjednávat.

## 2. Kamera – objednaná, čeká na doručení

Majitel 9. 10. potvrdil, že má současnou
[ESP32-S3 N16R8 s konektorem OV5640](https://dratek.cz/arduino-platforma/232701-vyvojova-deska-esp32-s3-s-konektorem-pro-kameru-ov5640-wifi-bluetooth.html)
a samotnou kameru už objednal. Zatím má pouze konektor na desce.
**Druhou ESP desku ani sadu deska + kamera neobjednávat.** Přesný kód,
cena a provedení objednaného kamerového modulu nejsou doložené.

V dosavadním seznamu zůstává pouze podmíněný USB-A modul za orientačních
21 Kč (cena z 6. 10., nově neověřená); vynechat jej při vhodném hotovém
vedení. Původní součet 500 Kč se ruší. Délky koupených kabelů,
I2S audio, OE/resetové stavy a montážní materiál se musí ověřit zvlášť;
úplný rozpočet montáže z těchto podkladů určit nelze.

Po doručení kamery před připojením porovnat FPC, kontakty a revizi
se stávající deskou. Kamerový profil B vyžaduje jiné
audio/RGB piny než A. MikroSD je pro základní hlas a jednotlivý snímek
volitelná. [Zapojení a napájení](../kufr/docs/efekty-a-napajeni.md).

## 3. Teď neobjednávat – volitelné doplňky

| Stav | Položka / odkaz | Původní množství | Cena celkem | Proč vynechat |
| --- | --- | ---: | ---: | --- |
| Vynechat | [Grove → čtyři šroubové svorky](https://botland.cz/grove-prevadece/11348-grove-4pinovy-sroubovy-konektor-5904422340063.html) | 6 ks | 258 Kč | Pohodlí při montáži. Lze použít koupené Grove kabely: jeden konec upravit, vodiče napojit pájením a izolovat; konektor u U059 zachovat |
| Vynechat | [JST-SM třípinová zástrčka + zásuvka s vodiči](https://botland.cz/prislusenstvi-pro-led-pasky/11125-konektor-pro-led-pasky-a-pasky-jst-sm-3kolikove-sparkfun-cab-14575-5904422317249.html) | 6 párů | 390 Kč | Pohodlné odpojení LED. Pro funkci stačí připájené a izolované vodiče, se zachovaným DIN/DOUT |
| Vynechat | [Goodram M1A4 microSD 32 GB + adaptér + USB čtečka](https://botland.cz/pametove-karty-microsd-sd/6946-goodram-all-in-one-m1a4-32-gb-100-mb-s-microsd-pametova-karta-trida-10-adapter-ctecka-otg-5908267930274.html) | 1 sada | 274 Kč | Volitelné lokální assety. Boot, základní hlas a kamera kartu nepotřebují; základní nouzové hlášky jsou podle plánu ve flash |

Druhý zesilovač a dva PH2 kabely jsou od 9. 10. potvrzené nákupy.
Osadíme oba reproduktory; každý má vlastní zesilovač. Výstupy zesilovačů
nikdy neparalelizovat, SPK− není GND.

Grove napájí jednotlivý modul, nikoli celou motorovou větev. Do páčkových
svorek nedávat tenké Dupont vodiče: Degson je určený od 0,2 mm². Pro LED
nepoužívat tlakové klipsy bez ověření rozteče a silikonového krytu IP65;
vybrané konektory se jednou připájí a zaizolují.

## 4. Už koupené – znovu neobjednávat

| Díly | Doložené množství | Použití |
| --- | ---: | --- |
| ESP32-S3 N16R8 s konektorem OV5640, Drátek 232701 | 1 | Stávající hlavní řadič, osazené samčí lišty; samotná kamera je objednaná, dosud nedoručená |
| PCA9685 | 2 | Jedna deska stačí pro 6 serv + 6 digitálních motorových vstupů, druhá jako rezerva |
| Polohovací mikroservo 180° | 6 | Vždy jen jedno aktivní; ostatní nemusí silou držet polohu |
| M5Stack U059 N20 včetně Grove kabelů | 6 | Vždy jen jedna vibrace; výkonový driver už obsahuje modul |
| WS2812B 5 V, 30 LED/m, IP65 | 1 role 5 m | Použijeme 6×20 cm, zbytek rezerva |
| Adafruit Pixel Shifter 6066 | 1 | Datová linka RGB 3,3 → 5 V |
| Adafruit ICS43434 | 1 | Mikrofon, napájení 3,3 V |
| DFRobot DFR0954 | **2 celkem** | Původní 1 + nově potvrzený 1; jeden zesilovač pro každý reproduktor |
| Waveshare 14595, 8 Ω / 5 W | 1 pár / **2 kusy** | Oba osadit, nezávislé SPK výstupy |
| USB-C PD modul s přepínačem, Drátek 1782992576 | 1 | 12 V, max. 3 A; pro nový návrh vyhovuje |
| USB-C → USB-C 60 W / 3 A | 1 | Zdroj → PD modul; nový 5A kabel netřeba |
| USB-A → USB-C datový kabel | 1 | 5V USB-A zásuvka → USB-UART ESP32 |
| Bíle podsvícené momentové tlačítko | 1 | **Zahájení rozhovoru / PTT**, nikoli hlavní napájení |
| Červeně podsvícená kolébka | **2 celkem** | Původní 1 + nově potvrzený 1; hlavní vypínač, druhý rezerva |
| Zeleně podsvícená kolébka | 1 | Nově potvrzená; servisní vypínač pohonů |
| Tepelný vratný jistič 5 A | 1 | Hlavní 5V přívod; není okamžité elektronické proudové omezení |
| Rezistory 330 Ω | 10 | RGB data, pomocné sériové odpory |
| Kondenzátory 470 µF / 50 V | 10 | Dvojice paralelně u LED, pohonů a elektroniky |
| Dupont M-F 20 cm, 40 vodičů | 2 balení | Signály; nejsou náhrada F-F |
| Smršťovací bužírky | 1 sada | Izolace pájených spojů |
| Univerzální desky 50×70 mm | 2 | Rozvod a pomocné montážní spoje |
| Joy-IT měnič 9–35 V → 5 V / 5 A, SEG-28582 | 1 | Nově potvrzený, jediný měnič kufru |
| MCP23017 Adafruit 5346, ADA-21037 | 1 | Nově potvrzený, pomocné vstupy/výstupy kamerového profilu B |
| JST PH 2,0 mm dvoupinový kabel, DNG-06563 | 2 | Nově potvrzené, dva oddělené reproduktorové kabely |
| Dupont F-F, 40×20 cm | 2 balení | Nově potvrzené; signály, nikoli výkonový rozvod |
| PROSKIT MT-1210 | 1 | Nově potvrzený multimetr pro polaritu, kontinuitu a napětí |
| CYH 2×0,50 mm² | 1 objednávková položka | Délka není ze snímku doložená; přeměřit před návrhem kabelových tras |
| CYH 2×1,50 mm² (VM00VH-H) | 1 objednávková položka | Délka není ze snímku doložená; hlavní napájecí vedení |
| Rezistory 100 kΩ / 0,25 W / 1 % | 20 | Nově potvrzené, výchozí stavy; dvě sady po 10 nesčítat s nepotvrzenou objednávkou |
| Rezistory 15 kΩ / 0,25 W / 1 % | 10 | Nově potvrzené, pull-up a horní odpor děliče |
| Rezistory 22 kΩ / 0,25 W / 1 % | 10 | Nově potvrzené, dolní odpor děliče |
| Degson 5pin rychlosvorka | 10 | Nově potvrzené; všech 5 pozic tvoří jeden elektrický uzel |

Neobjednávat Pololu D24V90F5, 100W zdroj, Adafruit 5807 ani hlavní tlačítkový
vypínač Pololu 2813 z dřívějšího návrhu.
Další momentové tlačítko není
pro nynější ovládání potřeba.

## 5. Co ještě zajistit pro montáž a provoz

- Multimetr PROSKIT MT-1210 je koupený; stav páječky není potvrzený. Připravit také pájecí
  drát s tavidlem, odizolovací a štípací nástroj; není doloženo, že jsou doma.
- Vývody obou kolébek lze připájet a izolovat koupenou bužírkou. Fastony
  ani krimpovací kleště tedy nejsou nutné pro tento způsob montáže.
- Držáky desek, serv, kamery a reproduktorů, šrouby a mechaniku přihrádek
  určit podle skutečných otvorů a prostoru kufru; rozměry dosud nejsou
  změřené. Malé moduly bez montážních otvorů umístit do izolačního držáku,
  nevrtat skrz jejich PCB. Nezakrýt chlazení měniče ani mikrofonní otvor.
- Zajistit existující 2,4GHz Wi-Fi s internetem (router/hotspot pro kufr).
  Nový router se automaticky nekupuje; není nutný pro místní offline řízení.
- Kamera vyžaduje profil B a ověření GPIO2/48, ne jen zasunutí modulu.
  Adaptéry efektů/audio jsou implementované, kamerový driver E13 a fyzická měření ještě zbývají.
- Před zapojením zbývá ověřit montážní rozměry, proudy, OE, resetové stavy a audio
  propojení podle technického dokumentu; případné doplnění dílů určit ze
  skutečného schématu a měření, ne z úspěšného nákupu.
