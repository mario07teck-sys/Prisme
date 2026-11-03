; Installeur de Prisme — compilé par build.ps1 (Inno Setup 6)
#define AppName "Prisme"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6E3B7A51-4C2D-4F8E-9B61-5A7C3D2E1F90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Mario Teck
AppComments=Convertisseur de fichiers et téléchargeur de vidéos, sans publicité
DefaultDirName={localappdata}\Programs\Prisme
DefaultGroupName=Prisme
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Prisme-Setup-{#AppVersion}
SetupIconFile=..\src\Assets\prisme.ico
UninstallDisplayIcon={app}\Prisme.exe
UninstallDisplayName=Prisme
WizardStyle=modern
WizardSizePercent=110
WizardImageFile=wizard-1.bmp,wizard-2.bmp
WizardSmallImageFile=small-1.bmp,small-2.bmp
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "menu";      Description: "Ajouter « Convertir avec Prisme » au clic droit"; GroupDescription: "Intégration à Windows :"
Name: "autostart"; Description: "Démarrer Prisme avec Windows (dans la zone de notification)"; GroupDescription: "Intégration à Windows :"; Flags: unchecked
Name: "desktop";   Description: "Créer une icône sur le Bureau"; GroupDescription: "Raccourcis :"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; anciens moteurs remplacés à chaque mise à jour
Type: filesandordirs; Name: "{app}\engines\office"

[Icons]
Name: "{userprograms}\Prisme"; Filename: "{app}\Prisme.exe"; Comment: "Convertir des fichiers et télécharger des vidéos"
Name: "{userdesktop}\Prisme"; Filename: "{app}\Prisme.exe"; Tasks: desktop

[Run]
; les cases ne s'appliquent qu'à la première installation : une mise à jour garde les réglages faits dans Prisme
Filename: "{app}\Prisme.exe"; Parameters: "--register-shell"; Tasks: menu; Check: IsFreshInstall; Flags: runhidden waituntilterminated
Filename: "{app}\Prisme.exe"; Parameters: "--autostart on"; Tasks: autostart; Check: IsFreshInstall; Flags: runhidden waituntilterminated
Filename: "{app}\Prisme.exe"; Description: "Lancer Prisme"; Flags: nowait postinstall skipifsilent
; mise à jour automatique (installeur lancé par Prisme en silence) : on le relance comme il était, ouvert ou rangé (/relaunch=show|tray|none)
Filename: "{app}\Prisme.exe"; Parameters: "--{param:relaunch|none}"; Flags: nowait; Check: SilentRelaunch

[Registry]
Root: HKCU; Subkey: "Software\Prisme"; Flags: uninsdeletekey dontcreatekey
; filet de sécurité si « Prisme.exe --unregister » n'a pas pu tourner
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Prisme"; Flags: uninsdeletevalue dontcreatekey

[UninstallRun]
Filename: "{app}\Prisme.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "PrismeUnregister"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\engines"
Type: filesandordirs; Name: "{userappdata}\Prisme\office-profile"

[Code]
var FreshInstall: Boolean;

function InitializeSetup(): Boolean;
begin
  FreshInstall := not RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E3B7A51-4C2D-4F8E-9B61-5A7C3D2E1F90}_is1');
  Result := True;
end;

function IsFreshInstall(): Boolean;
begin
  Result := FreshInstall;
end;

function SilentRelaunch(): Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:relaunch|none}') <> 'none');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer;
begin
  // première installation sans la tâche « menu » : on désactive le menu dans les réglages
  if (CurStep = ssPostInstall) and FreshInstall and not WizardIsTaskSelected('menu') then
    Exec(ExpandConstant('{app}\Prisme.exe'), '--unregister-menu', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function InitializeUninstall(): Boolean;
var Code: Integer;
begin
  // ferme Prisme et ses moteurs en cours (ffmpeg, LibreOffice…) pour pouvoir tout supprimer
  Exec('taskkill.exe', '/IM Prisme.exe /T /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := True;
end;
