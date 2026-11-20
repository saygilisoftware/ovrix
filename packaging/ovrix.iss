#ifndef StageDir
  #error StageDir must point to the published OVRIX folder
#endif
#ifndef OutputFolder
  #error OutputFolder must point to the artifact folder
#endif
[Setup]
AppId={{C7E10D8A-89D1-4A19-9487-9D4186771923}
AppName=OVRIX
AppVersion=1.0.0
AppPublisher=OVRIX Contributors
DefaultDirName={localappdata}\Programs\OVRIX
DefaultGroupName=OVRIX
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputFolder}
OutputBaseFilename=OVRIX-1.0.0-win-x64-setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\apps\desktop\ovrix.ico
WizardImageFile=wizard-side.bmp
WizardSmallImageFile=wizard-header.bmp
UninstallDisplayIcon={app}\OVRIX.exe
VersionInfoVersion=1.0.0

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\OVRIX"; Filename: "{app}\OVRIX.exe"

[Run]
Filename: "{app}\OVRIX.exe"; Description: "{cm:LaunchProgram,OVRIX}"; Flags: nowait postinstall skipifsilent
