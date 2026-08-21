; EQdps installer — EverQuest Legends session tracker widget
#define AppName "EQdps"
; Overridden by scripts\release.ps1 via /DAppVersion=<csproj Version>
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#define AppPublisher "David Edwards"
#define AppExe "EQdps.exe"

[Setup]
AppId={{9C4F2B71-5A8E-4D03-B662-EQDPS0100000}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\EQdps
DefaultGroupName=EQdps
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=EQdpsSetup
SetupIconFile=..\src\EQBuddy\Assets\EQBuddy.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
; Stamp the setup exe with the app version so the in-app updater can read it.
VersionInfoVersion={#AppVersion}
; Let silent self-updates close the running widget and relaunch it after.
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\dist\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; MIT requires the copyright and permission notice to travel with copies.
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\EQdps"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\EQdps"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; No skipifsilent: silent self-updates must relaunch the widget when done.
Filename: "{app}\{#AppExe}"; Description: "Launch EQdps now"; Flags: nowait postinstall
