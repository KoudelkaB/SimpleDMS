; Inno Setup script pro SimpleDMS.
;
; Očekává self-contained .NET publish výstup pro win-x64 v adresáři zadaném
; přes /DSourceDir (výchozí: ..\..\publish\win-x64 relativně k tomuto skriptu).
; Verzi předejte přes /DAppVersion=0.1.0.
;
; Příklad sestavení:
;   iscc /DAppVersion=0.1.0 /DSourceDir="C:\path\to\publish\win-x64" packaging\windows\SimpleDMS.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\..\publish\win-x64"
#endif

#define AppName "SimpleDMS"
#define AppPublisher "Bohdan Koudelka"
#define AppExeName "SimpleDMS.exe"
#define AppUrl "https://github.com/KoudelkaB/DPH-Asistent"

[Setup]
AppId={{74965774-83D2-4C17-9FD4-B28E50407704}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\SimpleDMS
DefaultGroupName=SimpleDMS
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir=..\..\dist
OutputBaseFilename=SimpleDMS-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
LicenseFile=..\..\LICENSE
SetupIconFile=..\icons\SimpleDMS.ico

[Languages]
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SimpleDMS"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,SimpleDMS}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SimpleDMS"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,SimpleDMS}"; Flags: nowait postinstall skipifsilent

