; Installeur Windows de TeamsCompanion (Inno Setup 6), par utilisateur : aucun droit administrateur demandé.
; Compiler après "dotnet publish ... -o publish" à la racine du dépôt (voir .github/workflows/app.yml) :
;   iscc /DAppVersion=1.2.3 TeamsCompanion\installer.iss   ->   publish-installer\OpenCompanion-Setup.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; AppId fixe : c'est lui qui fait reconnaître une installation existante (mise à jour en place). Ne jamais le changer.
AppId={{F864111C-DE56-4B0B-B670-12BA03EEB532}
AppName=OpenCompanion
AppVersion={#AppVersion}
AppPublisher=AnthoDingo
AppPublisherURL=https://github.com/AnthoDingo/OpenCompanion
PrivilegesRequired=lowest
DefaultDirName={autopf}\OpenCompanion
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; même nom que le Mutex de App.xaml.cs : l'installeur demande de quitter l'appli avant de remplacer ses fichiers
AppMutex=OpenCompanion.TeamsCompanion
SetupIconFile=app.ico
UninstallDisplayIcon={app}\TeamsCompanion.exe
OutputDir=..\publish-installer
OutputBaseFilename=OpenCompanion-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
fr.Startup=Lancer OpenCompanion au démarrage de Windows
en.Startup=Start OpenCompanion when Windows starts

[Tasks]
Name: "startup"; Description: "{cm:Startup}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\OpenCompanion"; Filename: "{app}\TeamsCompanion.exe"
Name: "{autodesktop}\OpenCompanion"; Filename: "{app}\TeamsCompanion.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "OpenCompanion"; ValueData: """{app}\TeamsCompanion.exe"""; Flags: uninsdeletevalue; Tasks: startup
; mise à jour avec la case décochée : retirer le lancement auto posé par une installation précédente
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "OpenCompanion"; Flags: deletevalue; Tasks: not startup
; réglages de l'appli (langue, compagnon) : supprimés à la désinstallation
Root: HKCU; Subkey: "Software\OpenCompanion"; Flags: uninsdeletekey

[Run]
Filename: "{app}\TeamsCompanion.exe"; Description: "{cm:LaunchProgram,OpenCompanion}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; jeton Microsoft Graph (DPAPI), esptool et firmware téléchargés : rien ne doit rester après désinstallation
Type: filesandordirs; Name: "{localappdata}\OpenCompanion"
