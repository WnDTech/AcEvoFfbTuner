#define AppName "AC Evo FFB Tuner"
#define AppPublisher "WnDTech"
#define AppURL "https://github.com/WnDTech/AcEvoFfbTuner"
#define AppExeName "AcEvoFfbTuner.exe"
#define AppVersion GetVersionNumbersString("..\publish-test\AcEvoFfbTuner.exe")

[Setup]
AppId={{A7E3D2F1-5B8C-4E6A-9F0D-1C2B3A4E5F6D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\README.md
OutputDir=..\installer-output
OutputBaseFilename=AcEvoFfbTuner-Setup-v{#AppVersion}
;SetupIconFile=..\src\AcEvoFfbTuner\Resources\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=lowest
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish-test\AcEvoFfbTuner.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish-test\Moza_API_C.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish-test\Moza_SDK.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish-test\*.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#AppName}"
