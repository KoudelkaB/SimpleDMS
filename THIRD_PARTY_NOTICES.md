# Závislosti SimpleDMS

Zdrojový kód SimpleDMS je pod MIT. Přibalené knihovny a systémové komponenty
mají vlastní licence; MIT licence aplikace tyto licence nenahrazuje.

- Avalonia a Avalonia.Headless: MIT, https://github.com/AvaloniaUI/Avalonia
- Inter font: SIL Open Font License 1.1, https://github.com/rsms/inter
- SkiaSharp: MIT, https://github.com/mono/SkiaSharp
- Skia a jeho nativní závislosti: BSD a další licence uvedené v distribuci
  SkiaSharp, https://github.com/google/skia
- QRCoder: MIT, https://github.com/codebude/QRCoder
- .NET runtime a System.Security.Cryptography.ProtectedData: MIT,
  https://github.com/dotnet/runtime
- MinVer: Apache-2.0, https://github.com/adamralph/minver
- Linux Flatpak přibaluje libsecret/secret-tool pod LGPL-2.1-or-later.
  Zdrojový archiv se SHA256 je uveden v manifestu; lze z něj sestavit
  a nahradit knihovnu. https://gitlab.gnome.org/GNOME/libsecret

Při distribuci uchovejte licenční soubory dodané nativními knihovnami a .NET
runtime. Testovací balíčky xUnit a Microsoft.NET.Test.Sdk nejsou součástí aplikace.
