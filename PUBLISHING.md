# Vydávání SimpleDMS

Pipeline odpovídá [DPH-Asistent](https://github.com/KoudelkaB/DPH-Asistent):
.NET 10, MinVer 7, tagy `vX.Y.Z`, Inno Setup, Flatpak, přenosné archivy,
SHA256SUMS.txt a draft GitHub Release. Prerelease tagy např. `v0.1.0-beta.2`
jsou podporované. Flatpak používá freedesktop runtime 26.08 a přibaluje
libsecret/secret-tool pro OS klíčenku.

## Vydaná veřejná beta

[SimpleDMS 0.1.0-beta.1](https://github.com/KoudelkaB/SimpleDMS/releases/tag/v0.1.0-beta.1)
vyšla 5. 10. 2026 z commitu `2781f1971e5562e887fb1f8dc1f7588731165991`.
[Release workflow](https://github.com/KoudelkaB/SimpleDMS/actions/runs/37372882496)
úspěšně provedl testy a sestavil oba systémy. Vydání obsahuje čtyři balíčky
a `SHA256SUMS.txt`; dostupnost vydání byla ověřena bez přihlášení.
Přihlášení dalším Google účtem a fyzický tisk nebyly při tomto vydání ručně
ověřené. Tyto kontroly automatické testy nenahrazují.

Oficiální klient je **External / In production**, zatím bez ověření Googlem
a s limitem 100 uživatelů. Nastavení, oprávnění a zásady ochrany osobních
údajů popisuje [Google konfigurace](docs/google-setup.md).

## Jednorázově

Nastavte [Google OAuth klienta](docs/google-setup.md), repository variable
GOOGLE_CLIENT_ID a secret GOOGLE_CLIENT_SECRET. Bez nich build projde,
balíček nabídne import klientského JSON v Nastavení. Soukromé tokeny ani
archivní soubory nikdy do repozitáře nebo CI nevkládejte.

## Ověření bez vydání

GitHub Actions → Release → Run workflow → main a verze (např. 0.1.0-beta.2).
Testy musí projít, poté vzniknou Windows a Linux artefakty. Ruční spuštění
nevytvoří Release. Artefakty lze stáhnout ze stránky běhu workflow.

## Nová verze

```sh
git tag v0.1.0-beta.2
git push origin v0.1.0-beta.2
```

Po úspěchu obou buildů vznikne draft s EXE, ZIP, Flatpak, tar.gz a SHA256SUMS.
Zkontrolujte změny, nainstalujte balíček a proveďte zkušební Google login
také účtem mimo původní seznam testerů, práci v testovacím archivu a tisk.
Pro beta tag v editoru Releases vyberte **Pre-release**; workflow sám toto
označení nenastavuje. Potom draft publikujte v Releases a ověřte, že je
dostupný i bez přihlášení a obsahuje všechny balíčky a kontrolní součty.
Již vydané tagy nepřesouvejte; další změny aplikace vydávejte s novou verzí.
Windows instalátor nemá zřízený podpisový certifikát. Winget/Flathub
registrace jsou samostatné kroky, workflow je nepublikuje automaticky.

Pro místní sestavení použijte `dotnet publish src/SimpleDms.App -c Release
-r linux-x64 --self-contained true -o publish/linux-x64` (nebo win-x64).
Flatpak očekává staging podle release workflow. Inno Setup používá unikátní
AppId SimpleDMS a instalaci pro aktuálního uživatele.
