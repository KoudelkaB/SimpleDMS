# Jednorázové Google nastavení pro správce

Google přihlášení je volitelné. Archiv funguje přes složku synchronizovanou
Google Drive for desktop (Insync, rclone) bez jakéhokoli OAuth klienta.
Klient je potřeba jen pro doplňování Google ID do sloupce Q, díky kterému
odkaz v M a QR kód na štítku vedou přímo na Google Drive.

Běžný uživatel oficiálních balíčků nemusí vytvářet Google Cloud projekt ani
importovat klientský JSON. Konfigurace desktop OAuth klienta je přibalená;
na kartě Archiv stačí vložit adresu složky s registrem i dokumenty, zvolit
**Přihlásit Google a propojit** a schválit přístup pro vlastní účet.

## Stav oficiální veřejné bety

Od 5. 10. 2026 je oficiální aplikace v režimu **External / In production**.
Přihlášení není omezené na seznam Test users. Aplikace zatím není ověřená
Googlem, takže při autorizaci zobrazí upozornění a platí limit **100
uživatelů celkem za dobu projektu**, nikoli 100 současně přihlášených.
Limit se průběžně neuvolňuje odhlášením uživatelů. Správce Google Workspace
může přístup firemním účtům omezit i v produkčním režimu.
[Pravidla Google](https://developers.google.com/identity/protocols/oauth2/production-readiness/overview)

[Zásady ochrany osobních údajů](https://github.com/KoudelkaB/SimpleDMS/wiki/Privacy-Policy)
jsou dostupné z veřejné [domovské stránky aplikace](https://github.com/KoudelkaB/SimpleDMS/wiki)
i z konfigurace souhlasu Google.

## Oprávnění a hranice archivu

Aplikace žádá `openid` a `email` pro identifikaci účtu a
`drive.metadata.readonly` pro názvy a ID položek. Přes Google API nikdy nečte
obsah souborů a nic nezapisuje. Soubory přenáší synchronizační klient.
Google nevynucuje omezení na zadanou složku; SimpleDMS je vynucuje ve vrstvě
`ArchiveDriveClient`: prochází jen root archivu a jeho složku dokumentů,
neznámá ID nehledá načítáním cizích položek a před každým čtením kontroluje
řetězec rodičů.

Oficiální balíčky stahujte z repozitáře `KoudelkaB/SimpleDMS`. Stejný OAuth
Client ID v jiném programu nepotvrzuje jeho původ ani dodržování této ochrany.

## Vlastní klient pro vývoj nebo jinou distribuci

Následující kroky jsou určené správci vlastní distribuce nebo vývojáři.
Oficiální beta už je má nastavené.

1. V [Google Cloud Console](https://console.cloud.google.com/) vytvořte nebo
   vyberte projekt a zapněte **Google Drive API**.
2. V **Google Auth Platform** nastavte Branding (název aplikace, kontaktní
   e-mail, veřejnou domovskou stránku a zásady ochrany osobních údajů),
   Audience a Data Access. Pro vývoj lze začít režimem External / Testing
   s Test users. Pro veřejnou betu použijte External / In production
   (Audience → Publish app) a počítejte s omezeními neověřené aplikace.
   Internal je dostupné jen pro příslušnou Google Workspace organizaci.
3. Požadované scopes jsou `openid`, `email` a
   `https://www.googleapis.com/auth/drive.metadata.readonly`.
4. V **Clients → Create client** zvolte **Desktop app**. Stáhněte klientský
   JSON. Web application ani service account nejsou vhodné. Přesměrování
   obstará lokální HTTP callback 127.0.0.1 na dočasném portu a PKCE.
5. Pro místní provoz použijte v aplikaci **Nastavení → Importovat Google
   client JSON**. Pro klienty je jednodušší přibalit tento JSON pod názvem
   `oauth-client.json` vedle aplikace nebo jej dodávat automaticky v balíčcích.

## Automatická konfigurace vydávaných balíčků

V GitHub repozitáři v Settings → Secrets and variables → Actions nastavte:

- Repository variable `GOOGLE_CLIENT_ID`: hodnota `installed.client_id`.
- Repository secret `GOOGLE_CLIENT_SECRET`: hodnota `installed.client_secret`.

Release workflow vytvoří `oauth-client.json` pro oba systémy. JSON ani tokeny
uživatelů se necommitují. OAuth Desktop app je veřejný klient; klientské
tajemství je v distribuované aplikaci dostupné a není heslem archivu ani
přístupovým tokenem uživatele. Samotná tato konfigurace přístup k Drive
neuděluje; každý účet musí projít Google souhlasem. GitHub Actions secrets
se nepřenášejí do klonů ani forků repozitáře, ale klientskou konfiguraci lze
z veřejného balíčku převzít. Client ID tedy není zárukou původu programu.
Obnovovací token uživatele se ukládá odděleně do OS klíčenky
(Windows DPAPI, Linux Secret Service).

[Google popis veřejných desktop klientů](https://developers.google.com/identity/protocols/oauth2#installed)

## Nasazení a omezení Google

Scope `drive.metadata.readonly` patří mezi restricted: aplikace potřebuje
najít ID složek v již existujícím archivu, proto jí samotné drive.file
nestačí. Produkční režim neznamená ověření Googlem. Pro odstranění upozornění
a rozšíření nad limit neověřené aplikace je potřeba vyřešit ověření značky
a požadovaného scope podle pravidel Google; MIT licence tento proces nenahrazuje.

V External / Testing Google omezuje autorizaci na test users a obnovovací
tokeny typicky vyprší po 7 dnech. Archiv přitom funguje dál; jen se
přestanou doplňovat Google ID, dokud se uživatel znovu nepřihlásí.
Nové přihlášení v produkčním režimu tomuto sedmidennímu omezení Testing
nepodléhá. Starší přihlášení po přepnutí projektu může být potřeba jednou
obnovit. Ani produkční token nemá zaručenou neomezenou životnost: přístup
může odvolat uživatel či správce a token může vypršet z dalších důvodů.
[Pravidla životnosti tokenů](https://developers.google.com/identity/protocols/oauth2#expiration)

Při odmítnutém přihlášení zkontrolujte typ klienta, zapnutí Drive API, Audience,
vyčerpání limitu a případné omezení Google Workspace. Test users řešte pouze
u vlastního klienta v Testing. Na Linuxu musí být odemčená klíčenka a dostupné
`secret-tool`; Flatpak obsahuje nástroj, používá klíčenku hostitele.

Zdroje: [OAuth pro desktop aplikace](https://developers.google.com/identity/protocols/oauth2/native-app),
[Drive scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth),
[životnost tokenů](https://developers.google.com/identity/protocols/oauth2#expiration).
