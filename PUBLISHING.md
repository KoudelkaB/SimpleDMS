# Vydávání SimpleDMS

Pipeline odpovídá [DPH-Asistent](https://github.com/KoudelkaB/DPH-Asistent):
.NET 10, MinVer 7, tagy `vX.Y.Z`, Inno Setup, Flatpak, přenosné archivy,
SHA256SUMS.txt a draft GitHub Release. Prerelease tagy např. `v0.1.0-alpha.1`
jsou podporované. Flatpak používá freedesktop runtime 26.08 a přibaluje
libsecret/secret-tool pro OS klíčenku.

## Jednorázově

Nastavte [Google OAuth klienta](docs/google-setup.md), repository variable
GOOGLE_CLIENT_ID a secret GOOGLE_CLIENT_SECRET. Bez nich build projde,
balíček nabídne import klientského JSON v Nastavení. Soukromé tokeny ani
archivní soubory nikdy do repozitáře nebo CI nevkládejte.

## Ověření bez vydání

GitHub Actions → Release → Run workflow → main a verze (např. 0.1.0-alpha.1).
Testy musí projít, poté vzniknou Windows a Linux artefakty. Ruční spuštění
nevytvoří Release. Artefakty lze stáhnout ze stránky běhu workflow.

## Nová verze

```sh
git tag v0.1.0
git push origin v0.1.0
```

Po úspěchu obou buildů vznikne draft s EXE, ZIP, Flatpak, tar.gz a SHA256SUMS.
Zkontrolujte změny, nainstalujte balíček a proveďte zkušební Google login,
práci v testovacím archivu a tisk. Potom draft publikujte v Releases.
Windows instalátor nemá zřízený podpisový certifikát. Winget/Flathub
registrace jsou samostatné kroky, workflow je nepublikuje automaticky.

Pro místní sestavení použijte `dotnet publish src/SimpleDms.App -c Release
-r linux-x64 --self-contained true -o publish/linux-x64` (nebo win-x64).
Flatpak očekává staging podle release workflow. Inno Setup používá unikátní
AppId SimpleDMS a instalaci pro aktuálního uživatele.
