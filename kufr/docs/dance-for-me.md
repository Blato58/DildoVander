# Dance for me

V mobilní aplikaci otevřete **Ovládání → Dance for me**. Uvolněte ruční
řízení a ukončete rozhovor. Tlačítko spustí na kufru náhodnou skladbu
z vlastního seznamu na SD kartě. Při alespoň dvou skladbách se poslední
nevybere znovu bezprostředně po sobě. Telefon ukazuje název souboru,
tempo a průběh; samotný zvuk přehrává reproduktor kufru. AI ani internet
nejsou potřeba. Odpojení pozorovatelského telefonu tanec nepřeruší.

**Zastavit tanec** ukončí tuto show a uvolní kufr po dokončení úklidu.
**Stop kufru** a fyzické tlačítko během tance zastaví také hudbu/efekty
a zachovají běžný požadavek místního zotavení. Po chybě pohonů zůstává
nutná místní kontrola. Tanec se sám neobnovuje po restartu nebo výpadku.

## Příprava skladeb

1. Připravte vlastní WAV soubory: PCM signed 16-bit little endian,
   mono, 24 000 Hz, délka 1–600 sekund. Firmware přijímá přesně
   44bajtovou RIFF/WAVE hlavičku `fmt ` + `data`, bez metadat/dalších
   chunků. MP3/AAC ani streamovací služby kufr přímo nepřehrává.
2. Uložte vedle nich `songs.json` podle příkladu níže. `file` je relativní
   k JSON; volitelné `filename` přejmenuje cílový soubor na jednoduchý
   ASCII název. Nastavte skutečné tempo a počátek prvního beatu.
3. Z kořene repozitáře vytvořte **novou, dosud neexistující** složku:

   ```powershell
   python kufr/firmware/tools/prepare_dance.py C:/Music/songs.json C:/Music/kufr-sd
   ```

4. Z výsledné složky zkopírujte `dance.csv` a WAV soubory do kořene
   připravené SD karty. Kartu vyměňujte při vypnutém kufru. Po spuštění
   se ověřená SD montuje jen pro čtení; při každém tanci se seznam
   načte znovu a všechny jeho skladby se validují před přehráváním.

Převod ve vlastní instalaci [FFmpeg](https://ffmpeg.org/ffmpeg.html)
lze připravit takto; `-bitexact` odstraní závislá metadata, viz
[formátové příznaky](https://ffmpeg.org/ffmpeg-formats.html#Format-Options).
Přípravný skript vždy ověří skutečnou hlavičku a velikost výsledku:

```powershell
ffmpeg -i "original.mp3" -vn -ac 1 -ar 24000 -c:a pcm_s16le -bitexact -map_metadata -1 "dance-1.wav"
```

```json
{
  "tracks": [
    {
      "file": "dance-1.wav",
      "bpm": 120,
      "offsetMs": 0,
      "red": 255,
      "green": 100,
      "blue": 20,
      "vibrationEveryBeats": 2
    },
    {
      "file": "dance-2.wav",
      "bpm": 100,
      "offsetMs": 500,
      "red": 60,
      "green": 120,
      "blue": 255,
      "vibrationEveryBeats": 0
    }
  ]
}
```

Seznam má 1–12 položek. Názvy začínají písmenem/číslicí a mají nejvýše
60 ASCII písmen, číslic, podtržítek nebo pomlček a příponu `.wav`.
Názvy se nesmí opakovat ani s jinou velikostí písmen. BPM je 40–240,
`offsetMs` 0–10 000 a musí ležet před koncem skladby, RGB 0–255.
`vibrationEveryBeats: 0` vibrace vypíná; 1–16 je rozestup v beatech.
Skript ověří celý seznam před zápisem a nepřepíše existující složku.
Skutečné písničky nejsou součástí repozitáře.

## Efekty a provozní hranice

Světlo postupuje po šesti přihrádkách a na každý beat krátce zazáří
a pohasne. Volitelné vibrace se řídí nastaveným rozestupem. Jde o
choreografii podle BPM/offsetu konkrétní skladby; automatická detekce
beatu ani proměnlivého tempa není implementovaná.

Hraje nejvýše jedna skladba. SD a audio obsluhuje samostatná úloha
s PCM bloky do 960 B; do RAM se nenačítá celá píseň. Efekty zůstávají
v řídicí smyčce. Průběh vychází z časové osy odeslaného PCM; stávající
I2S DMA znamená odhad zpoždění přibližně 80 ms + jeden 20ms blok,
nikoli změřenou polohu reproduktoru. Výpadek čtení/audio časové osy
tanec ukončí. Zmeškané beaty se nedohánějí frontou vibrací.

Svítí nejvýše jeden LED úsek při stávajícím omezení jasu přibližně 25 %;
běží nejvýše jeden motor, jeho pulz ukončí existující limit 150 ms.
Serva se při tanci nehýbou. Tanec vyžaduje ověřený profil B se SD,
audio, kalibraci a místní zotavení; nezpřístupní neověřené zapojení.
Hlas, výběr hůlky, kamera, SD servis, OTA a změny sítě se s ním
nepřekrývají. Technická historie dovoluje nejvýše 128 přijatých
spuštění na jeden boot a chrání před opakovaným přehráním téhož ID.

Místní WSS přidává `dance.start` s `messageId`, `bootId` a
`expectedRevision`, `dance.stop`, korelované `dance.ack` a volitelný
`dance` stav ve snapshotu. Je to explicitní start samostatné show
z autentizovaného pozorovatelského telefonu, stejně jako start hlasu;
ruční lease se nepředstírá. ESP vlastní show i brány. Během show má
snapshot fázi `dancing` a backendový runtime je zneplatněný. Nový
mobil rozumí také snapshotům starého firmware bez `dance` (tlačítko
zůstane vypnuté). Starší striktní mobilní klient nové pole/fázi
nepřijme: před instalací nového firmware je potřeba aktualizovat mobil.

## Ověření a zbývající akceptace

Hostitelské kontroly z `kufr/firmware/`:

```bash
bash tests/run_dance_tests.sh
python -m unittest discover -s tools -p test_prepare_dance.py
```

Mobilní testy/build a skutečné WSS JSON výstupy ověřují kontrakt
a software; syntetický browser harness je jasně označený.
Na sestaveném kufru je stále potřeba změřit souběh SD/I2S/LED,
plynulost celé skladby, skutečný posun beatu, proudy/teplotu a Stop
včetně krátkého doznění DMA, odpojení Wi-Fi a iPhone lifecycle.
Nahrání do desky, podepsaná iOS dodávka a fyzická akceptace nejsou
součástí tohoto implementačního slice.
