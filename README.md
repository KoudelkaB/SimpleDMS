# SimpleDMS

Desktopová aplikace pro sdílený archiv na Google Drive s XLSX evidencí.
.NET 10 + Avalonia, Windows a Linux, MIT licence.

Uživatel vloží odkaz na archiv a přihlásí se Google účtem. Aplikace otevře
nebo založí dvojici stejně pojmenovaného XLSX a složky dokumentů.

- Hledání bez diakritiky, filtry a offline čtení evidence i připravených příloh.
- Stabilní šestimístné číslo: dvě číslice kategorie a čtyři pořadí v kategorii.
- Přidání souborů/složek, doplnění skenů k původnímu číslu, automatické Q (Drive ID).
- Rozpracovanost včetně kompatibility s oranžovým celým řádkem původního XLSX.
- Zálohy XLSX, kontrola souběžné změny a obnova přerušeného přidání.
- Štítky s QR, vlastní grid a kalibrace; pokračování na použitých arších,
  ruční pozice a potvrzení skutečného výsledku tisku.

[Používání](docs/usage.md) · [Google konfigurace správce](docs/google-setup.md) ·
[Vydávání balíčků](PUBLISHING.md) · [Licence závislostí](THIRD_PARTY_NOTICES.md)

## Instalace

Balíčky nabízí [Releases](https://github.com/KoudelkaB/SimpleDMS/releases):
Windows Inno Setup EXE / portable ZIP a Linux Flatpak / portable tar.gz.
Obsahují .NET runtime. Google OAuth Desktop klient musí být jednou nastaven
vydavatelem nebo správcem; potom jej klienti nastavovat nemusí.

Linux portable potřebuje X11/XWayland, fontconfig, libsecret (secret-tool),
odemčenou Secret Service klíčenku a pro přímý tisk CUPS. Flatpak obsahuje
secret-tool a tisk předává PDF prohlížeči. Google přihlášení a přidávání
vyžadují internet; offline je dostupné čtení připravené kopie.

## Vývoj

```sh
dotnet restore SimpleDMS.slnx
dotnet build SimpleDMS.slnx -c Release
dotnet test SimpleDMS.slnx -c Release
dotnet run --project src/SimpleDms.App
```

Testy používají syntetická data a falešné Drive API. Volitelný test skutečného
legacy registru přijímá cestu v `SIMPLEDMS_REFERENCE_WORKBOOK`; pracuje na
kopii v paměti. `SIMPLEDMS_QA_DIR` uloží náhled UI a zkušební PDF štítků.
Žádné archivní dokumenty, uživatelské tokeny ani soukromé registry se
nepublikují s implementací.

## Provozní model

Jeden správce zapisuje online, ostatní nahlížejí. XLSX zůstává zdrojem
metadat; místní cache, deník a offline mapa jsou oddělené pro účet a archiv.
Aktuální implementace kontroluje změny pravidelným čtením Drive, nepoužívá
server ani webhooky. Registry musí mít list Databáze, A–F jako N1–N6 a G
jako Název; R je explicitní stav, Q je Drive ID.

Před prvním ostrým použitím ověřte přihlášení s vlastním OAuth klientem a
testovací složkou a zarovnání štítků s konkrétní tiskárnou. Automatické testy
neprovádějí přihlášení skutečného Google uživatele ani fyzický tisk.
