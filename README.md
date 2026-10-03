# SimpleDMS

Desktopová aplikace pro sdílený archiv dokumentů s XLSX evidencí.
.NET 10 + Avalonia, Windows a Linux, MIT licence.

Archiv je místní složka, kterou na pozadí synchronizuje Google Drive for
desktop (na Linuxu Insync nebo rclone). Uživatel vybere složku s dvojicí
stejně pojmenovaného XLSX a složky dokumentů. Všechny operace jsou okamžité
souborové operace a fungují i offline; nahrání do cloudu obstará synchronizační klient.

- Hledání bez diakritiky, filtry podle kategorií s názvy z listu „Kódování dokumentů“.
- Stabilní šestimístné číslo: dvě číslice kategorie a čtyři pořadí v kategorii.
- Přidání souborů/složek, doplnění skenů k původnímu číslu.
- Volitelné propojení s Google účtem (jen čtení názvů a ID) doplní Q (Drive ID),
  takže odkaz v M a QR kód na štítku vedou na Google Drive.
- Rozpracovanost včetně kompatibility s oranžovým celým řádkem původního XLSX.
- Zálohy XLSX a ochrana proti zápisu do registru otevřeného v Excelu.
- Štítky s QR, vlastní grid a kalibrace; pokračování na použitých arších,
  ruční pozice, výběr tiskárny a potvrzení skutečného výsledku tisku.

[Používání](docs/usage.md) · [Google konfigurace správce](docs/google-setup.md) ·
[Vydávání balíčků](PUBLISHING.md) · [Licence závislostí](THIRD_PARTY_NOTICES.md)

## Instalace

Balíčky nabízí [Releases](https://github.com/KoudelkaB/SimpleDMS/releases):
Windows Inno Setup EXE / portable ZIP a Linux Flatpak / portable tar.gz.
Obsahují .NET runtime. Pro práci s archivem stačí synchronizační klient;
Google OAuth Desktop klient je potřeba jen pro volitelné doplňování Google ID.

Linux portable potřebuje X11/XWayland, fontconfig a pro přímý tisk CUPS;
pro volitelné Google ID libsecret (secret-tool) s odemčenou klíčenkou.
Flatpak tisk předává PDF prohlížeči.

## Vývoj

Ve VS Code otevřete kořenovou složku projektu a stiskněte F5 (konfigurace
`SimpleDMS`). Je potřeba .NET 10 SDK a rozšíření Microsoft C#.
F5 aplikaci nejprve sestaví v režimu Debug. Pro volitelné Google přihlášení se
použije místní `oauth-client.json` v kořeni projektu, který se nekopíruje do Gitu.

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
musí mít list Databáze, A–F jako N1–N6 a G jako Název; R je explicitní stav,
Q je Drive ID.

Před prvním ostrým použitím ověřte zarovnání štítků s konkrétní tiskárnou.
Automatické testy neprovádějí přihlášení skutečného Google uživatele ani fyzický tisk.
