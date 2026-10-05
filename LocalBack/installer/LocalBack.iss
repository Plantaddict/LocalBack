; LocalBack installer (Inno Setup 6). Per-user install, no admin rights.
; Build: iscc installer\LocalBack.iss /DAppVersion=0.1.0 /DSourceDir=..\publish\LocalBack

#define AppName "LocalBack"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\LocalBack"
#endif

[Setup]
AppId={{8F3C2B1A-6D4E-4F7A-9C2B-1E5D7A9B3C4F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=LocalBack
DefaultDirName={localappdata}\Programs\LocalBack
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=LocalBack-Setup-{#AppVersion}
SetupIconFile=..\src\LocalBack.App\Assets\LocalBack.ico
UninstallDisplayIcon={app}\LocalBack.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Let setup close a running LocalBack before replacing it, and start it again afterwards.
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#SourceDir}\LocalBack.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\localback-cli.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\LocalBack"; Filename: "{app}\LocalBack.exe"

[Run]
Filename: "{app}\LocalBack.exe"; Description: "Start LocalBack"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM LocalBack.exe"; Flags: runhidden; RunOnceId: "StopLocalBack"

[Registry]
; The app writes these itself (Settings → autostart, Explorer menu); remove them on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "LocalBack"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Classes\*\shell\LocalBack"; ValueType: none; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\LocalBack"; ValueType: none; Flags: uninsdeletekey dontcreatekey

; Settings and the index in %LOCALAPPDATA%\LocalBack are kept, so a reinstall picks up where it left off.
; Backups on the drives are never touched.
