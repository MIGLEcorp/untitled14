#define AppVersion "4.0.0"
[Setup]
AppId={{B4E6167D-3A9C-41E6-A0A2-9176BEDDAC12}
AppName=MBrowser
AppMutex={code:GetAppMutex}
AppVersion={#AppVersion}
AppPublisher=MIGLEcorp
AppPublisherURL=https://github.com/MIGLEcorp/untitled14
DefaultDirName={localappdata}\Programs\MBrowser
DefaultGroupName=MBrowser
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=MBrowser-{#AppVersion}-Setup-x64
Compression=lzma2
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
Source: "..\bin\tools\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
[Icons]
Name: "{group}\MBrowser"; Filename: "{app}\MBrowser.exe"; AppUserModelID: "MIGLEcorp.MBrowser"; Check: not IsTestInstall
Name: "{autodesktop}\MBrowser"; Filename: "{app}\MBrowser.exe"; AppUserModelID: "MIGLEcorp.MBrowser"; Tasks: desktopicon; Check: not IsTestInstall
[InstallDelete]
Type: files; Name: "{app}\Feather.exe"
Type: files; Name: "{app}\Feather.dll"
Type: files; Name: "{app}\Feather.deps.json"
Type: files; Name: "{app}\Feather.runtimeconfig.json"
Type: files; Name: "{app}\Modules\Feather.Features.dll"
Type: files; Name: "{app}\Features\WebKit\Feather.WebKit.exe"
Type: files; Name: "{app}\Features\WebKit\Feather.WebKit.dll"
Type: files; Name: "{app}\Features\WebKit\Feather.WebKit.deps.json"
Type: files; Name: "{app}\Features\WebKit\Feather.WebKit.runtimeconfig.json"
Type: files; Name: "{userprograms}\Feather\Feather.lnk"; Check: not IsTestInstall
Type: files; Name: "{autodesktop}\Feather.lnk"; Check: not IsTestInstall
[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Установка Microsoft WebView2 Runtime…"; Check: NeedsWebView
Filename: "{app}\MBrowser.exe"; Description: "Открыть MBrowser"; Flags: nowait postinstall skipifsilent unchecked; Check: not IsTestInstall
[Code]
function IsTestInstall: Boolean;
begin
  Result := ExpandConstant('{param:TESTMODE|0}') = '1';
end;
function NeedsWebView: Boolean;
var Version: String;
begin
  if IsTestInstall then begin Result := False; exit; end;
  Result := not ((RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')));
end;
function GetAppMutex(Param: String): String;
begin
  if IsTestInstall then Result := '' else Result := 'Local\FeatherBrowser';
end;
