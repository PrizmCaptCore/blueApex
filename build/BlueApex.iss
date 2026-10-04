; BlueApex installer (Inno Setup 6). Built by build\publish.ps1, which passes:
;   /DAppVersion=0.1.0  /DAppDir=<published app folder>  /DOutDir=<where the setup exe goes>
;
; What it takes care of beyond copying files:
;  - stops a running BlueApex cleanly (BlueApex.exe --exit) so hidden icons come back and files unlock
;  - installs the Edge WebView2 runtime if the PC has none (needed for the Steam sign-in window)
;  - lets the current user hide Public Desktop shortcuts (icacls), the one thing that needs admin
;  - optional autostart; uninstall stops the app and asks whether to keep settings

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppDir
  #define AppDir "out\app"
#endif
#ifndef OutDir
  #define OutDir "out"
#endif

[Setup]
AppId={{B5A0C1E2-7D3F-4C7A-9E1B-3C2A1F0E9D8C}
AppName=BlueApex
AppVersion={#AppVersion}
AppVerName=BlueApex {#AppVersion}
AppPublisher=prizmcaptcore
AppPublisherURL=https://github.com/PrizmCaptCore/blueApex
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\BlueApex
DefaultGroupName=BlueApex
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=BlueApex-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=BlueApex
LicenseFile={#AppDir}\LICENSE
SetupIconFile=..\src\BlueApex\Assets\blueapex.ico
UninstallDisplayIcon={app}\BlueApex.exe
; A silent update started by the app passes /LAUNCH=1 so it is restarted afterwards.

[Languages]
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ko.Autostart=Windows 시작 시 BlueApex 자동 실행
ko.PublicDesktop=공용 바탕화면 바로가기(모든 사용자용)도 서랍에 넣을 수 있게 권한 설정
ko.Launch=BlueApex 실행
ko.KeepSettings=설정과 캐시(%AppData%\BlueApex: 구역, 위젯, 로그인 세션)를 지울까요?%n%n"아니요"를 누르면 다음에 다시 설치할 때 그대로 돌아옵니다.
ko.WebView2=Microsoft Edge WebView2 런타임 설치 중...
en.Autostart=Start BlueApex when Windows starts
en.PublicDesktop=Allow hiding Public Desktop shortcuts (all users) in the drawer
en.Launch=Launch BlueApex
en.KeepSettings=Delete settings and caches (%AppData%\BlueApex: zones, widgets, sign-in session)?%n%nChoose No to keep them for a later install.
en.WebView2=Installing the Microsoft Edge WebView2 runtime...

[Tasks]
Name: "autostart"; Description: "{cm:Autostart}"; Flags: unchecked
Name: "publicdesktop"; Description: "{cm:PublicDesktop}"

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\BlueApex"; Filename: "{app}\BlueApex.exe"
Name: "{group}\BlueApex 종료 (아이콘 복원)"; Filename: "{app}\BlueApex.exe"; Parameters: "--exit"

[Run]
; Run-at-login is a Task Scheduler logon task for the real (non-elevated) user; the app creates it itself.
Filename: "{app}\BlueApex.exe"; Parameters: "--enable-autostart"; Tasks: autostart; Flags: runhidden runasoriginaluser waituntilterminated
; S-1-5-32-545 = BUILTIN\Users: every user may then set the hidden attribute on public shortcuts.
Filename: "icacls.exe"; Parameters: """{commondesktop}"" /grant *S-1-5-32-545:(OI)(CI)(WA,RA)"; Tasks: publicdesktop; Flags: runhidden
Filename: "{app}\BlueApex.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\BlueApex.exe"; Flags: nowait runasoriginaluser; Check: WantLaunchAfterSilent

[Code]
// (Uninstall stops the app in InitializeUninstall below; see StopRunningApp.)
const
  MutexName = 'Local\BlueApex';
  WebView2Key = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

function WantLaunchAfterSilent: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:LAUNCH|0}') = '1');
end;

// Asks a running BlueApex to quit (which restores hidden icons) and waits for it.
procedure StopRunningApp;
var
  Exe: String;
  ResultCode, Waited: Integer;
begin
  if not CheckForMutexes(MutexName) then exit;
  Exe := ExpandConstant('{app}\BlueApex.exe');
  if FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Waited := 0;
  while CheckForMutexes(MutexName) and (Waited < 15000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp;
  Result := '';
end;

function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(HKLM, WebView2Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Bootstrapper: String;
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and not WebView2Installed then
  begin
    Bootstrapper := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
    try
      DownloadTemporaryFile(WebView2Url, 'MicrosoftEdgeWebview2Setup.exe', '', nil);
      Exec(Bootstrapper, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    except
      Log('WebView2 runtime install skipped: ' + GetExceptionMessage);
    end;
  end;
end;

function InitializeUninstall: Boolean;
var
  ResultCode: Integer;
begin
  StopRunningApp;
  // Drop the logon task (and any old Run-key entry) before the exe goes away.
  Exec(ExpandConstant('{app}\BlueApex.exe'), '--disable-autostart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\BlueApex');
    if DirExists(DataDir) and (MsgBox(CustomMessage('KeepSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(DataDir, True, True, True);
  end;
end;
