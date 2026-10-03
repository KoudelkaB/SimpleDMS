# Jednorázové Google nastavení pro správce

Google přihlášení je volitelné. Archiv funguje přes složku synchronizovanou
Google Drive for desktop (Insync, rclone) bez jakéhokoli OAuth klienta.
Klient je potřeba jen pro doplňování Google ID do sloupce Q, díky kterému
odkaz v M a QR kód na štítku vedou přímo na Google Drive.

Běžný uživatel nemusí vytvářet Google Cloud projekt. Nastavte jednoho desktop
OAuth klienta pro distribuovanou aplikaci; uživatel pak na kartě Archiv vloží
adresu složky s registrem, přihlásí se svým Google účtem a potvrdí přístup.

Aplikace žádá pouze scope `drive.metadata.readonly`: čte názvy a ID položek,
nikdy obsah souborů a nic nezapisuje. Soubory přenáší synchronizační klient.
Google nevynucuje omezení na zadanou složku; SimpleDMS je vynucuje ve vrstvě
`ArchiveDriveClient`: prochází jen root archivu a jeho složku dokumentů,
neznámá ID nehledá načítáním cizích položek a před každým čtením kontroluje
řetězec rodičů.

Oficiální balíčky stahujte z repozitáře `KoudelkaB/SimpleDMS`. Stejný OAuth
Client ID v jiném programu nepotvrzuje jeho původ ani dodržování této ochrany.

1. V [Google Cloud Console](https://console.cloud.google.com/) vytvořte nebo
   vyberte projekt a zapněte **Google Drive API**.
2. V **Google Auth Platform** nastavte Branding (název SimpleDMS, kontaktní
   e-mail), Audience a Data Access. Pro několik vlastních účtů lze začít
   režimem External / Testing a přidat je do Test users. Internal je
   dostupné jen pro příslušnou Google Workspace organizaci.
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
tajemství je v distribuované aplikaci dostupné a není heslem archivu.
Obnovovací token uživatele se ukládá odděleně do OS klíčenky
(Windows DPAPI, Linux Secret Service).

## Nasazení a omezení Google

Scope `drive.metadata.readonly` patří mezi restricted: aplikace potřebuje
najít ID složek v již existujícím archivu, proto jí samotné drive.file
nestačí. Před distribucí většímu okruhu uživatelů vyřešte Google požadavky
na ověření / výjimku podle Audience a způsobu nasazení. Není správné slibovat,
že MIT licence nebo malý počet uživatelů automaticky ověření nahrazuje.

V External / Testing Google omezuje autorizaci na test users a obnovovací
tokeny typicky vyprší po 7 dnech. Archiv přitom funguje dál; jen se
přestanou doplňovat Google ID, dokud se uživatel znovu nepřihlásí.
Přepnutí na In production a případné ověření provádí správce projektu.

Při odmítnutém přihlášení zkontrolujte typ klienta, zapnutí Drive API, Audience
a Test users. Na Linuxu musí být odemčená klíčenka a dostupné `secret-tool`;
Flatpak obsahuje nástroj, používá klíčenku hostitele.

Zdroje: [OAuth pro desktop aplikace](https://developers.google.com/identity/protocols/oauth2/native-app),
[Drive scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth),
[životnost tokenů](https://developers.google.com/identity/protocols/oauth2#expiration).
