#define AppVersion "4.4.0"
[Setup]
AppId={{B4E6167D-3A9C-41E6-A0A2-9176BEDDAC12}
AppName=MBrowser
MinVersion=10.0.19041
AppMutex={code:GetAppMutex}
AppVersion={#AppVersion}
AppVerName=MBrowser 4.4.0 RC5 — Windows Hello и passkeys
AppPublisher=MIGLEcorp
AppPublisherURL=https://github.com/MIGLEcorp/untitled14
DefaultDirName={localappdata}\Programs\MBrowser
DefaultGroupName=MBrowser
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=MBrowser-{#AppVersion}-rc.5-Setup-x64
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\mbrowser.ico
UninstallDisplayIcon={app}\MBrowser.exe
Uninstallable=not IsTestInstall
CreateUninstallRegKey=not IsTestInstall
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked
[Files]
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Dirs]
Name: "{app}\_MBrowser_TestProfileOnly"; Check: IsTestInstall
[Icons]
Name: "{group}\MBrowser"; Filename: "{app}\MBrowser.exe"; AppUserModelID: "MIGLEcorp.MBrowser"; Check: not IsTestInstall
Name: "{autodesktop}\MBrowser"; Filename: "{app}\MBrowser.exe"; AppUserModelID: "MIGLEcorp.MBrowser"; Tasks: desktopicon; Check: not IsTestInstall
[InstallDelete]
Type: files; Name: "{app}\assets\kawaii-home.jpg"
Type: files; Name: "{app}\assets\kawaii-menu.jpg"
Type: files; Name: "{app}\assets\kawaii-chrome.jpg"
Type: files; Name: "{app}\Feather.exe"
Type: files; Name: "{app}\Feather.dll"
Type: files; Name: "{app}\Feather.deps.json"
Type: files; Name: "{app}\Feather.runtimeconfig.json"
Type: dirifempty; Name: "{app}\Feather"
Type: files; Name: "{app}\Modules\Feather.Features.dll"
Type: files; Name: "{app}\Features\WebKit\Feather.WebKit.exe"
Type: files; Name: "{app}\Features\WebKit\Feather.WebKit.dll"
Type: files; Name: "{app}\Features\WebKit\Feather.WebKit.deps.json"
Type: files; Name: "{app}\Features\WebKit\Feather.WebKit.runtimeconfig.json"
Type: files; Name: "{userprograms}\Feather\Feather.lnk"; Check: not IsTestInstall
Type: dirifempty; Name: "{userprograms}\Feather"; Check: not IsTestInstall
Type: files; Name: "{autodesktop}\Feather.lnk"; Check: not IsTestInstall
[Run]
Filename: "{app}\MBrowser.exe"; Parameters: "--register-browser"; Flags: runhidden waituntilterminated; Check: not IsTestInstall
Filename: "{app}\MBrowser.exe"; Description: "Открыть MBrowser"; Flags: nowait postinstall skipifsilent unchecked; Check: not IsTestInstall
[UninstallRun]
Filename: "{app}\MBrowser.exe"; Parameters: "--unregister-browser"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "UnregisterBrowser"
[Code]
function IsTestInstall: Boolean;
begin
  Result := ExpandConstant('{param:TESTMODE|0}') = '1';
end;
function GetAppMutex(Param: String): String;
begin
  if IsTestInstall then Result := '' else Result := 'Local\FeatherBrowser';
end;
