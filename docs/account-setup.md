# Účty, první vlastník a obnova

Tento návod popisuje připravený kód. Sám nezakládá produkční účet, nemění VPS,
nenasazuje aplikaci, nezapíná poskytovatele AI ani nepotvrzuje podepsaný iPhone.
Účty používají ASP.NET Core Identity .NET 10, jeho vlastní implementaci passkeys,
samostatnou SQLite databázi a chráněné serverové cookies. Heslový ani anonymní
„první administrátor“ neexistuje.

## Konfigurace a trvalá data

Účty jsou ve výchozím stavu vypnuté. Před zapnutím nastavte v soukromém prostředí
služby následující veřejné konfigurační hodnoty; nepoužívejte uvedenou ukázkovou
doménu jako skutečnou službu.

| Klíč | Význam |
| --- | --- |
| `ApplicationIdentity:Enabled` | Výslovně `true`. |
| `ApplicationIdentity:DataRoot` | Absolutní neveřejný trvalý adresář; obsahuje `identity.sqlite`, `backups/` a `owner-delivery/`. |
| `ApplicationIdentity:DataProtectionRoot` | Absolutní trvalý adresář klíčů. Pokud existuje DV10, musí být přesně jeho dosavadní `Keepsake:DataProtectionRoot` (nebo dosavadní `Keepsake:DataRoot + ".keys"`). |
| `ApplicationIdentity:RelyingPartyId` | Pevná DNS doména passkeys, bez schématu, portu a cesty. |
| `ApplicationIdentity:AllowedOrigins:0` | Přesný HTTPS origin, například `https://kufr.example.invalid`; další originy mají samostatné indexy. Žádná cesta ani koncové `/`. Doména musí patřit do RP. |
| `ApplicationIdentity:AppleAppId` | Volitelně skutečné `TEAMID.bundle.id` pro AASA. Desetiznakové Team ID nesmí být smyšlené. |
| `ApplicationIdentity:AppleAppIds:0` | Volitelný seznam dalších skutečných `TEAMID.bundle.id`; AASA spojí seznam s původním `AppleAppId` a odstraní duplicity. Neplatná položka zablokuje odpověď. |
| `ApplicationIdentity:RequestsPerMinute` | Technický limit na IP; výchozí 40, povolený rozsah 10–120. |

Proměnné prostředí používají `__`, například `ApplicationIdentity__DataRoot` a
`ApplicationIdentity__AllowedOrigins__0`. Žádný z těchto klíčů neobsahuje provider
credential. SQLite, zálohy a Data Protection klíče nesmí být pod webrootem ani
uvnitř přepínaného release adresáře. Přístup k adresářům musí mít pouze účet
služby a správce záloh. Na Windows připravte neveřejné ACL nad `DataRoot` ještě
před vytvořením souborů; na Unixu provisioning vytváří nové doručovací adresáře
s režimem `0700` a soubory s `0600`.

Nginx musí předávat původní `Host` a HTTPS protokol pouze z důvěryhodného lokálního
proxy. Backend důvěřuje přeposlaným hlavičkám jen od loopbacku. AASA má vlastní
přesnou cestu `/.well-known/apple-app-site-association` pro GET/HEAD, bez
přesměrování a bez přihlášení. Při nesprávném originu mutace selže; RP se nikdy
neodvozuje z libovolného Host hlavičky.

## Inicializace a upgrade schématu

Službu zastavte a spusťte **stejný ověřený backendový build pod stejným účtem a
se stejným soukromým prostředím služby**:

```text
dotnet backend.dll --account-migrate
```

Pro lokální checkout se stejnou explicitní konfigurací lze použít:

```text
dotnet run --project backend/backend.csproj -- --account-migrate
```

Příkaz neotevírá HTTP server a nesestavuje běžící provider adaptéry. Aplikuje
verzované EF migrace pouze do databáze účtů. Před změnou existující databáze
uloží konzistentní privátní SQLite snapshot do `DataRoot/backups/`; nejde o
nekonzistentní kopii živého souboru s opomenutým WAL. Neznámou novější migraci
odmítne. Běžný start serveru neprovádí tichý upgrade: neaktuální schéma vyžaduje
tento příkaz. Původní databáze návštěvníků a její obsah zůstávají oddělené.

Migrace vznikají skutečným `dotnet ef migrations add ... --context
AccountDatabase`; design-time factory nepouští aplikaci a explicitně používá
Identity StoreOptions v3, včetně `AspNetUserPasskeys`. Instalovaný EF CLI 10.0.2
při generování vypisoval upozornění proti runtime 10.0.12. Výsledek se ověřuje
proti skutečné SQLite a aktuálnímu runtime, nikoli pouze generovaným souborem.

Před produkčním upgradem zachovejte také dosavadní Data Protection klíče a
zavedenou privátní zálohu. Samotná záloha SQLite nenahrazuje zálohu těchto klíčů.
Obnova databáze je vědomá administrátorská operace: zastavte službu, zachovejte
současnou databázi včetně WAL pro případnou analýzu, obnovte vybraný konzistentní
snapshot do oddělené soukromé kopie a použijte build odpovídající jejímu
`__EFMigrationsHistory`. SQLite shell podporuje například ověření kopie:

```text
sqlite3 recovery-check.sqlite ".restore '/private/path/identity-backup.sqlite'"
sqlite3 recovery-check.sqlite "PRAGMA integrity_check; SELECT MigrationId FROM __EFMigrationsHistory;"
```

Ověřenou kopii přepněte na cílovou databázi pouze při zastavené službě a po
zachování předchozího stavu; starý WAL se nesmí připojit k obnovenému snapshotu.
Neprovádějte destruktivní automatický downgrade ani mazání databáze kvůli obnově
účtu. Test upgradu skutečně zachovává passkeys, relace a granty, obnovuje
privátní snapshot do izolované databáze a odmítá neznámé novější schéma.

## První vlastník

Po migraci, stále v soukromém prostředí služby, spusťte:

```text
dotnet backend.dll --account-bootstrap
```

Příkaz funguje pouze před vznikem prvního účtu. Vygeneruje kryptografický náhodný
kód, uloží do databáze pouze hash a zapíše soukromý soubor do
`DataRoot/owner-delivery/`. **Kód ani odkaz netiskne do konzole** a nepřijímá je
v argumentech procesu. V konzoli je pouze cesta k souboru. Jeho první řádek je:

```text
https://skutecny-povoleny-origin/signin#invite=<jednorazovy-kod>
```

Druhý řádek uvádí UTC expiraci. Odkaz doručte vlastníkovi soukromě, otevřete na
kanonické HTTPS adrese a vytvořte passkey pomocí Windows Hello, iCloud Keychain
nebo jiného podporovaného autentikátoru. Kód platí 30 minut; opakované spuštění
bootstrapu zneplatní předchozí lokální bootstrap, dokud ještě neexistuje účet.
Vlastník vznikne až po úspěšném ověření attestation. Vytvoření prvního účtu
uzavře ostatní bootstrapové pokusy. Soukromý doručovací soubor po úspěchu ručně
odstraňte; nesdílejte ho do ticketu, logu, screenshotu ani repozitáře.

Zachovaná alternativní konfigurace `ApplicationIdentity:Bootstrap:TokenSha256`
a `...:ExpiresAt` slouží pro předem kontrolovaný jednorázový bootstrap; server
nevyžaduje plaintextový secret v konfiguraci. Běžný provoz tento postup
nepotřebuje. Pozvánkové a obnovovací fragmenty se zachytí a odstraní z URL před
načtením modulů; tajný kód nepatří do query stringu.

## Pozvánky, role a každodenní práce

Vlastník otevře **Nastavení → Správa**, vytvoří pozvánku s požadovanou rolí a
soukromě předá zobrazený odkaz nebo jednorázový kód. Pozvánka platí hodinu a
spotřebuje se jediným úspěšným ověřením passkey. Nový účet se nikdy nestane
vlastníkem jen proto, že přišel první. Role jsou `Owner`, `Administrator`,
`Operator`, `Viewer`, `Customer` a `Tester`. Pouze vlastník vydává pozvánky, obnovu a mění role;
správce přiděluje prostředky a spravuje dostupné provozní funkce. Posledního
aktivního vlastníka nelze zablokovat ani degradovat, včetně souběžných změn.

Správa vyžaduje passkey ověření z posledních deseti minut a výslovné potvrzení
změny. Odkaz **Znovu ověřit účet** otevře `/signin?return=/settings/admin`.
Ověřuje se právě přihlášený účet; jiný klíč ho nesmí tiše zaměnit. Následně
zopakujte zamýšlenou změnu. Klient nepřehrává citlivou mutaci automaticky.
Při chybě správa přímo v aplikaci ukáže bezpečný kód a HTTP stav. Například
`invalid_role` může znamenat, že starší backend nepodporuje roli nabízenou novou
aplikací; ověřte skutečně aktivovanou verzi backendu. Úspěšně vrácená pozvánka
zůstane zobrazená nezávisle na obnovování seznamu účtů. `Tester` je samostatný
izolovaný přístup do [Testing Sessions](testing-sessions.md), nikoli produkční správa.

V **Nastavení → Večery kufru** vyberte konkrétní povolené zařízení. Oprávnění
`read` umožní jen přehled; `operate` spolu s rolí obsluhy umožní vytvoření a
aktivaci večera. Vlastník a správce mají širší přístup k nakonfigurovaným kufrům.
Změnu potvrďte; klient posílá očekávaný aktivní večer a jedinečný idempotentní
požadavek do stávajících `/v1/evenings` endpointů. Nejistý síťový výsledek lze
zopakovat stejným požadavkem. Souběžnou změnu nebo probíhající rozhovor řešte
obnovením přehledu. Starší večery zůstávají uložené, tato správa nespouští AI.

Nastavení ukazuje klíče a přihlášené instalace. Přidejte druhý klíč, než odstraníte
původní; poslední klíč odstranit nelze. Odstranění klíče odhlásí ostatní relace.
Nové passkeys mají ve webovém i nativním výběru čitelný název, například
`DildoVander Tester · Vojta`; interní účet a WebAuthn user handle zůstávají stejné.
Nový záznam klíče v aplikaci obsahuje také skutečné datum vytvoření v UTC.
V Nastavení lze použít vlastní označení, například „Osobní iPhone“; přihlášení
ho nepřepisuje. Pojmenování v aplikaci nemění starší záznam v Apple Heslech nebo
jiném správci passkeys. Jeho zobrazovaný název se upravuje u daného poskytovatele;
existující klíče kvůli přejmenování nemažte.
Relace mají pevnou platnost 12 hodin, lze je jednotlivě odvolat; označení
„důvěryhodná“ je spravovaný záznam instalace a samo neprodlužuje přihlášení.
Změna role či blokace účtu ukončí jeho dosavadní relace. Role, odvolání a granty
se znovu kontrolují na každém chráněném požadavku.

Test telefonu nabízí jen aktuální GPT Live a výslovnou alternativu GPT Realtime
2.1. Hlasy, tempo, kamera a připravenost pocházejí z backendu; nepřístupný engine
se sám nenahradí jiným. Běžné přihlášení používá účet i pro nativní audio
WebSocket. Další audio vstup znovu ověřuje relaci, aktuální roli a grant.
Textový rozhovor skutečného kufru byl vyřazen. Samostatný nativní simulátor má
deterministickou TEXT fixture; [Testing Sessions](testing-sessions.md) naopak
posílají psané zprávy přes skutečné provider adaptéry a virtualizují jen hardware.
Podrobnosti určuje [aktuální hlasový kontrakt](autonomous-live-memory.md).

Soukromá paměť rozhovoru má samostatné ověření identity a samostatné souhlasy
s dlouhodobou pamětí a cloudovým exportem. Jméno, hlas ani fotografie hosta
identitu neověřují. Kontrolu a mazání paměti v diagnostice provádí vlastník či
správce; citlivá změna vyžaduje čerstvé passkey ověření a výslovné potvrzení.
Zobrazené příznaky souhlasu nejsou odvozené jen z přítomnosti identifikátoru.
Tato vazba se nevytváří automaticky propojením návštěvnické památky s účtem.

V Provozu serveru jsou skutečné režimy/persona a revize, technická připravenost,
stránkované logy a dostupné úlohy archivu. Čtení nevolá AI. Režimy se mění proti
aktuální revizi; dostupnost poskytovatele a pravidla běžících rozhovorů dál
ověřuje dosavadní backend. Výběr modelu používá pouze připravené ověřené neměnné
balíčky a samostatnou kontrolu integrity; UI neprovádí CAD export, shell ani
nahrávání libovolných souborů na server.

## Kufry, Dílna a soukromý archiv

Správce nejprve přidělí přesný prostředek `device` nebo `workshop` s oprávněním
`read`, `operate` či `manage`. Operátor potřebuje zároveň svou roli a konkrétní
grant pro ovládání; čtenář se grantem nepovýší na operátora. Dílna filtruje
seznam podle grantů, ovládání i soukromá média připouští pouze pro simulaci,
nikoli živý kufr nebo replay. Zrušení grantu se projeví na dalším požadavku.

Pro kufr následuje nezávislé místní párování přes BLE PoP a nativní Keychain.
Teprve potom lze uložit přátelský název a vazbu již oprávněného zařízení na účet.
Tvrzení telefonu o úspěšném párování samo nevytváří cloudový grant. Odebrání
vazby neodstraňuje místní Keychain ani párování; odebrání cloudového grantu
nepředstírá zrušení offline oprávnění na odpojeném kufru.

Návštěvnické cookie/claim oprávnění zůstává oddělené. Vlastník návštěvnické
památky může **výslovně** propojit přesný locator s aktuálním účtem. Sdílený ani
veřejný pohled to nesmí provést. Propojení používá jednorázový, pět minut platný
doklad vázaný na cílový účet a epochu oprávnění památky. Účet pak čte příběh a
výsledné portréty; nečte původní nahrané fotografie a nezískává návštěvnické
mutace. Odvolání všech přístupů nebo smazání památky zneplatní také účetní vazby.

## Ztracené klíče a kontrolovaná obnova

Před obnovou zkuste zálohovaný passkey ve správci hesel nebo jiný klíč již
registrovaný u účtu. Na důvěryhodném zařízení s platnou relací lze v Nastavení
účtu zkontrolovat klíče; přidání dalšího vyžaduje příslušné čerstvé ověření.

Pokud žádný klíč nelze použít, podpora převezme žádost s minimálním ne-tajným
identifikátorem účtu a popisem problému. Nevyžaduje passkeys, obnovovací odkazy,
párovací QR ani soukromé fotografie či rozhovory. Vlastnictví musí ověřit
nezávislými důvěryhodnými podklady v existujícím procesu podpory; samotné
zobrazované jméno nebo sdílený odkaz na památku nestačí. Pokud vlastnictví nelze
ověřit, podpora přístup nevydá a předá nevyřešenou žádost vlastníkovi služby.
Ověření podpory samo nemění účet ani jeho oprávnění a nenahrazuje ověření
nového klíče serverem.

Po ověření předá podpora cílový účet vlastníkovi služby. Obnovu smí vydat pouze
role `Owner`; role `Administrator` ani `Operator` toto oprávnění nemají.
Odkaz se předává ověřenému příjemci soukromě, nepatří do logů ani veřejné
komunikace a nesmí se přeposílat dalším osobám.

Je-li dostupný jiný účet s rolí `Owner`, jeho držitel vydá ve Správě konkrétnímu
účtu hodinový obnovovací odkaz. Obnova po úspěšném ověření nového klíče zneplatní
původní passkeys a relace, zachová účet a jeho granty. Neověřená či neplatná
attestation nesmí původní přístup odstranit.

Při ztrátě klíčů posledního vlastníka má správce infrastruktury kontrolovanou
lokální cestu pod soukromým prostředím stejné služby:

```text
dotnet backend.dll --account-recovery <existujici-account-id>
```

ID je identifikátor účtu ze Správy, nikoli device ID ani návštěvnický locator.
Příkaz nemění vlastníka, nevytváří nový účet a nepřijímá plaintextový token.
Zapíše soukromý jednorázový odkaz s fragmentem `#recovery=` do stejného
doručovacího adresáře; platnost je 30 minut. Nepotřebujete resetovat databázi,
mazat návštěvnický archiv ani znovu zapínat první anonymní bootstrap.

## Smazání vlastního účtu

V Nastavení účtu může přihlášený uživatel po novém ověření stejným passkey
výslovně potvrdit nevratné smazání účtu. Poslední aktivní vlastník musí nejprve
předat roli jinému účtu. Přijetí žádosti ihned zablokuje účet a zneplatní jeho
přístupy; odolná úloha následně maže jeho postavu, vlastní fotografie, portréty,
příběhy a vlastní testovací kontext. Pouhý přístup ke sdílené památce nepřenáší
vlastnictví a neopravňuje smazat data jiného hosta. Sdílené globální revize
PersonaLabu a účty ostatních hostů zůstávají zachovány.

Mazání se při nedostupném úložišti opakuje i po restartu. Technické zákazy
dalšího zápisu zabraňují tomu, aby opožděná úloha obsah obnovila. Oddělené
neprůhledné potvrzení platné sedm dní dovoluje na původním zařízení sledovat
stav na `/account-deletion` i po odhlášení; neobnovuje oprávnění účtu. iOS toto
potvrzení ukládá odděleně od přihlašovacího a offline materiálu v Keychain.
Při přihlášení k jinému účtu se dřívější potvrzení nezobrazuje.

Dokončení potvrzuje úklid v aplikaci. Požadavky na odstranění cloudových indexů
zůstávají ve své trvalé frontě; doby uchování poskytovatelů a záloh se řídí
jejich pravidly. Před nasazením této funkce proveďte standardní
`--account-migrate` s konzistentní soukromou zálohou; nová migrace pouze přidává
tabulku žádostí a nemění existující passkeys.

## iOS a servisní diagnostika

Podepsaná aplikace musí mít skutečně nastavené build hodnoty `KUFR_ACCOUNT_ORIGIN`
a `KUFR_PASSKEY_RP_DOMAIN`, Associated Domains entitlement
`webcredentials:<RP-domain>` a platné AASA se skutečným Team ID a bundle ID.
Ruční signing workflow čte repository variable `KUFR_PASSKEY_RP_DOMAIN` a přes
ověřený signing helper nastaví doménu i `KUFR_ACCOUNT_ORIGIN=https://<doména>`
pouze v App Release konfiguraci. Chybějící nebo neplatná doména podpis zablokuje;
nejde o provider credential. Doména backendu a jeho AASA musejí odpovídat.
Bez nich nativní přihlášení vypíše chybějící konfiguraci. Nativní passkeys běží
přes `ASAuthorizationPlatformPublicKeyCredentialProvider` na iOS 16+; stejná
backendová Identity ověřuje jejich attestation/assertion. Nejde o vlastní OAuth
server ani o tvrzení, že Safari sdílí cookies s aplikací.

Účtový URLSession má vlastní cookie jar a privátní Keychain záznam; návštěvnický
URLSession má oddělenou cookie jar a Keychain. Veřejný přehled neposílá ani
návštěvnickou cookie. Mosty připouštějí pouze vyjmenované cesty/metody, velikostní
meze a kanonický HTTPS origin, odmítají přesměrování. Nativní export používá
výslovně vyvolaný systémový share sheet s dočasným souborem a následným úklidem.

Běžný uživatel potřebuje účet, přesný grant a místní párování. Ručně vložený
administrátorský bearer není běžné přihlášení. Dosavadní nativní profil Debug
se samostatným servisním credentialem zůstává pokročilou servisní kompatibilitou.
Při zapnutí účtů se tento alternativní administrátorský vstup vypíná, pokud
správce výslovně neponechá `Backend:Admin:LegacyServiceCredentialEnabled=true`.
Analogicky staré lidské Twin relace vyžadují
`DigitalTwin:LegacyServiceSessionsEnabled=true`. Zachování konkrétní existující
servisní integrace musí být vědomé a dočasné rozhodnutí; běžný dashboard je
nenabízí jako obcházení účtu. Samostatné firmware/device credentialy, guest
claims a ingest identita se tímto přepínačem nemění.

Director otevřete z Kufru na `/suitcase/director` a vyberte přidělený kufr.
Běžné řízení a Stop používají jeho grant; odhalení proroctví, ukončení večera,
hráčské granty a import výsledků vyžadují příslušné oprávnění, potvrzení a čerstvé
přihlášení. Místní offline režim používá spárovaný kufr a jeho bezpečnostní stav
i bez WAN. Zvolený vzdálený kufr se nesmí zaměnit za jiný místně připojený kus.
Kronika nabízí oprávněné kufry a jejich existující večery. Hráčský odkaz duelu
opravňuje jen danou stranu ke konkrétní hře; přihlášený účet ho nenahrazuje.
Vazba památky na účet zpřístupní veřejnou kouzelnickou identitu. Soukromé
proroctví nadále vyžaduje původní vlastnický přístup hosta a odemčení serverem.

Windows webové a backendové testy nepotvrzují podpis, provisioning profil,
Apple Associated Domains ani skutečný passkey/audio/export na iPhonu.
Nativní kontrola zahrnuje `scripts/test-native-identity.sh` a skutečný unsigned
iOS Simulator build; podepsaný přístroj zůstává samostatnou akceptací.

## Ověření bez živých služeb

```text
dotnet run --project backend-tests/BackendTests.csproj -- --identity
dotnet run --project backend-tests/BackendTests.csproj -- --keepsake
dotnet run --project backend-tests/BackendTests.csproj -- --model-releases
```

Identity test vytváří skutečné P-256 WebAuthn attestation/assertion a předává je
nezaměněnému Identity verifieru přes lokální HTTPS; zahrnuje CSRF/origin,
expiraci/replay, obnovu, poslední klíč/vlastníka, souběžné změny, migraci se
zálohou a účetní vazby. Autorizační fixture simulace nespouští nativní proces a
neprokazuje hardware ani úplný streaming; jeho vlastnictví médií má vlastní
testy. Izolovaná browser fixture `--identity-fixture` po sestavení UI obsluhuje
skutečný `dist/`, vytváří nové soukromé testovací databáze a nečte zděděnou
provider konfiguraci. Privátní bootstrapový soubor se nesmí tisknout do logu.

V Nastavení je verze právě běžící aplikace: zdrojový Git commit a výslovné
označení místních změn nebo neznámého původu. API, ModelBundle a firmware mají
samostatnou verzi. Připojení ukazuje poslední potvrzený místní kontakt s kufrem
podle hodin telefonu, jen v této relaci aplikace; tento čas nepotvrzuje, že je
kufr právě online. Samotné párování ani pokus o připojení jej nevytvářejí.
