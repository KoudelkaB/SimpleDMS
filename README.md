# SimpleDMS

Desktopová aplikace pro sdílený archiv dokumentů s XLSX evidencí.
.NET 10 + Avalonia, Windows a Linux, MIT licence.

Archiv je místní složka, kterou na pozadí synchronizuje Google Drive for
desktop (na Linuxu Insync nebo rclone). Uživatel vybere složku s dvojicí
stejně pojmenovaného XLSX a složky dokumentů. Všechny operace jsou okamžité
souborové operace a fungují i offline; nahrání do cloudu obstará synchronizační klient.

- Hledání bez diakritiky, filtry podle kategorií s názvy z listu „Kódování dokumentů“.
- Stabilní šestimístné číslo: dvě číslice kategorie a čtyři pořadí v kategorii.
- Dokument jako soubor `číslo.přípona` nebo složka `číslo` jako v původní evidenci; přílohy mohou být soubory i složky, doplnění k původnímu číslu.
- Volitelné propojení s Google účtem (jen čtení názvů a ID) doplní Q (Drive ID),
  takže odkaz v M a QR kód na štítku vedou na Google Drive.
- Rozpracovanost jako oranžový celý řádek, stejně jako v původním XLSX.
- Zálohy XLSX a ochrana proti zápisu do registru otevřeného v Excelu.
- Štítky s QR, vlastní grid a kalibrace; pokračování na použitých arších,
  ruční pozice, výběr tiskárny a potvrzení skutečného výsledku tisku.

[Používání](docs/usage.md) · [Google konfigurace správce](docs/google-setup.md) ·
[Vydávání balíčků](PUBLISHING.md) · [Licence závislostí](THIRD_PARTY_NOTICES.md) ·
[Ochrana osobních údajů](https://github.com/KoudelkaB/SimpleDMS/wiki/Privacy-Policy)

## Instalace

První veřejná beta [0.1.0-beta.1](https://github.com/KoudelkaB/SimpleDMS/releases/tag/v0.1.0-beta.1)
je dostupná pro Windows x64 (Inno Setup EXE / portable ZIP) a Linux x64
(Flatpak / portable tar.gz). Další verze najdete v
[Releases](https://github.com/KoudelkaB/SimpleDMS/releases); u balíčků je také
soubor `SHA256SUMS.txt` s kontrolními součty. Balíčky obsahují .NET runtime.
Betu nejprve vyzkoušejte na kopii archivu.

Oficiální balíčky už obsahují konfiguraci Google OAuth Desktop klienta.
Pro volitelné doplňování Google ID stačí na kartě Archiv zadat odkaz na
root archivu a přihlásit se svým Google účtem; není potřeba vlastní Google
Cloud projekt ani import klientského JSON. Přihlášení synchronizačního
klienta a přihlášení SimpleDMS jsou samostatná.

Oficiální OAuth aplikace je od 5. 10. 2026 v režimu **External / In production**.
Účty není nutné přidávat mezi testery. Aplikace zatím není ověřená Googlem:
při autorizaci zobrazí upozornění a platí limit **100 uživatelů celkem za
dobu projektu**. Správce Google Workspace může přístup omezit.
Podrobnosti a požadovaná oprávnění uvádí [Google konfigurace](docs/google-setup.md).
Pro místní práci s archivem Google přihlášení v SimpleDMS není nutné.

Linux portable potřebuje X11/XWayland, fontconfig a pro přímý tisk CUPS;
pro volitelné Google ID libsecret (secret-tool) s odemčenou klíčenkou.
Flatpak tisk předává PDF prohlížeči.

## Vývoj

Ve VS Code otevřete kořenovou složku projektu a stiskněte F5 (konfigurace
`SimpleDMS`). Je potřeba .NET 10 SDK a rozšíření Microsoft C#.
F5 aplikaci nejprve sestaví v režimu Debug. Pro volitelné Google přihlášení se
použije místní `oauth-client.json` v kořeni projektu, který se nekopíruje do Gitu.
Nový klon ani fork tento soubor a GitHub Actions secrets neobsahuje; pro
vlastní sestavení připravte desktop klienta podle [návodu](docs/google-setup.md).

```sh
dotnet restore SimpleDMS.slnx
dotnet build SimpleDMS.slnx -c Release
dotnet test SimpleDMS.slnx -c Release
dotnet run --project src/SimpleDms.App
```

Testy používají syntetická data, dočasné složky a falešné Drive API. Volitelný
test skutečného legacy registru přijímá cestu v `SIMPLEDMS_REFERENCE_WORKBOOK`;
pracuje na kopii v paměti. `SIMPLEDMS_QA_DIR` uloží snímky všech karet UI a
zkušební PDF štítků. Žádné archivní dokumenty, uživatelské tokeny ani soukromé
registry se nepublikují s implementací.

## Provozní model

Jeden správce zapisuje, ostatní nahlížejí. XLSX zůstává zdrojem metadat.
Zápis probíhá na místě pod výhradním zámkem souboru: je-li registr otevřený
v Excelu, aplikace zápis odmítne místo přepsání cizí změny. Když synchronizační
klient registr aktualizuje, aplikace jej do 15 sekund znovu načte. Registry
musí mít list Databáze, A–F jako N1–N6 a G jako Název. Q je Drive ID souboru
nebo složky. Rozpracovanost určuje oranžová výplň celého řádku A–Q
(`FFC000`); samostatný stavový sloupec R není potřeba.

Před prvním ostrým použitím ověřte zarovnání štítků s konkrétní tiskárnou.
Automatické testy neprovádějí přihlášení skutečného Google uživatele ani fyzický tisk.
