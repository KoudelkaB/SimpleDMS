# Jednorázové Google nastavení pro správce

Běžný uživatel nemusí vytvářet Google Cloud projekt. Nastavte jednoho desktop
OAuth klienta pro distribuovanou aplikaci; uživatel pak vloží adresu archivu,
přihlásí se svým Google účtem a potvrdí přístup. Práva k souborům určuje sdílení
na Drive, nikoli znalost OAuth klienta.

1. V [Google Cloud Console](https://console.cloud.google.com/) vytvořte nebo
   vyberte projekt a zapněte **Google Drive API**.
2. V **Google Auth Platform** nastavte Branding (název SimpleDMS, kontaktní
   e-mail), Audience a Data Access. Pro několik vlastních účtů lze začít
   režimem External / Testing a přidat je do Test users. Internal je
   dostupné jen pro příslušnou Google Workspace organizaci.
3. Požadované scopes jsou `openid`, `email` a
   `https://www.googleapis.com/auth/drive`. Čtenářský režim používá
   `https://www.googleapis.com/auth/drive.readonly`.
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

Scopes drive i drive.readonly jsou restricted: aplikace potřebuje projít
již existující archiv, proto jí samotné drive.file nestačí. Před distribucí
většímu okruhu uživatelů vyřešte Google požadavky na ověření / výjimku podle
Audience a způsobu nasazení. Není správné slibovat, že MIT licence nebo malý
počet uživatelů automaticky ověření nahrazuje.

V External / Testing Google omezuje autorizaci na test users a obnovovací
tokeny pro tyto scopes typicky vyprší po 7 dnech. Je to vhodné pro první
zkoušku, ne pro dlouhodobé přihlášení klientů. Přepnutí na In production
a případné ověření provádí správce projektu.

Při odmítnutém přihlášení zkontrolujte typ klienta, zapnutí Drive API, Audience
a Test users. Při chybě zápisu zkontrolujte Google Drive sdílení registru
i složky dokumentů. Na Linuxu musí být odemčená klíčenka a dostupné
`secret-tool`; Flatpak obsahuje nástroj, používá klíčenku hostitele.

Zdroje: [OAuth pro desktop aplikace](https://developers.google.com/identity/protocols/oauth2/native-app),
[Drive scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth),
[životnost tokenů](https://developers.google.com/identity/protocols/oauth2#expiration).
