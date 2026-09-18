; SystemGuard — установщик (Inno Setup 6+).
; Сборка:
;   1. dotnet publish ..\SystemGuard.Desktop\SystemGuard.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:Version=2.1.1 -o ..\publish\win-x64
;   2. ISCC.exe SystemGuard.iss   (запускать из папки SystemGuard.Installer)
; Источник файлов — ..\publish\win-x64 (single-file: один exe; wildcard на будущее,
; если публикация станет многофайловой). PDB/XML в установщик не пакуются.

#define MyAppName "SystemGuard"
#define MyAppVersion "2.1.1"
#define MyAppPublisher "SystemGuard"
#define MyAppURL "https://t.me/mattrix_solution"
#define MyAppExeName "SystemGuard.exe"

[Setup]
AppId={{B7E4C9A1-5F2D-4B8E-9A3C-6D1E7F0A2B5C}
AppName={#MyAppName}
AppVerName={#MyAppName} {#MyAppVersion}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=LICENSE.txt
SetupIconFile=..\SystemGuard.Desktop\Assets\logo.ico
WizardImageFile=wizard.bmp
WizardSmallImageFile=wizardsmall.bmp
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=Output
OutputBaseFilename=SystemGuard-Setup-{#MyAppVersion}
UsePreviousAppDir=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "launchstartup"; Description: "Запускать SystemGuard при входе в Windows / Start with Windows"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Весь publish-каталог (на практике — один single-file exe). Символы и xml отчёты не тащим.
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{commonstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--minimized"; Tasks: launchstartup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
