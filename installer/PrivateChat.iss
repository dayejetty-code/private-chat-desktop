#ifndef PayloadDir
  #error PayloadDir must point to the verified distribution staging directory.
#endif
#ifndef ReleaseVersion
  #define ReleaseVersion "0.8.1"
#endif
#ifndef OutputPath
  #define OutputPath "..\artifacts\release-0.8.1"
#endif

[Setup]
AppId={{8B1CA174-E752-4DDC-92D7-D93A84052B7E}
AppName=Private Chat
AppVersion={#ReleaseVersion}
AppVerName=Private Chat {#ReleaseVersion} 测试版
AppPublisher=Private Chat 项目
DefaultDirName={localappdata}\Programs\Private Chat
DefaultGroupName=Private Chat
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir={#OutputPath}
OutputBaseFilename=PrivateChat-{#ReleaseVersion}-Windows-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
WizardResizable=no
SetupIconFile=private-chat.ico
UninstallDisplayIcon={app}\private-chat.ico
SetupMutex=PrivateChat.Installer
CloseApplications=no
RestartApplications=no
AlwaysRestart=no
AllowCancelDuringInstall=no
LicenseFile=..\LICENSE
InfoBeforeFile=before-install.txt
UninstallDisplayName=Private Chat {#ReleaseVersion} 测试版
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "private-chat.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Private Chat"; Filename: "{app}\PrivateChat.exe"; WorkingDir: "{app}"; IconFilename: "{app}\private-chat.ico"
Name: "{autodesktop}\Private Chat"; Filename: "{app}\PrivateChat.exe"; WorkingDir: "{app}"; IconFilename: "{app}\private-chat.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\PrivateChat.exe"; Description: "打开 Private Chat"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function OpenExistingFile(FileName: String; Access, Share, Security, Creation, Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseFileHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function ApplicationFilesAvailable: Boolean;
var Handle: THandle; FileName: String;
begin
  FileName := ExpandConstant('{app}\PrivateChat.exe');
  Result := True;
  if not FileExists(FileName) then Exit;
  Handle := OpenExistingFile(FileName, $C0000000, 0, 0, 3, 0, 0);
  Result := Handle <> THandle(-1);
  if Result then CloseFileHandle(Handle);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Target, DataRoot: String;
begin
  Result := '';
  Target := Lowercase(AddBackslash(ExpandFileName(ExpandConstant('{app}'))));
  DataRoot := Lowercase(AddBackslash(ExpandConstant('{localappdata}\PrivateChatDesktop')));
  if Pos(DataRoot, Target) = 1 then
    Result := '请勿把程序安装到聊天资料库目录。请使用默认安装位置。'
  else if not ApplicationFilesAvailable then
    Result := '请先退出 Private Chat 的所有窗口，再继续安装。安装程序不会强行关闭聊天或要求重启电脑。';
end;

function InitializeUninstall: Boolean;
begin
  Result := ApplicationFilesAvailable;
  if not Result then begin
    Log('Application files are in use; uninstall stopped.');
    if not UninstallSilent then
      MsgBox('请先退出 Private Chat 的所有窗口，再卸载。聊天资料库会保留。', mbInformation, MB_OK);
  end;
end;

// Deliberately no UninstallDelete rules: unknown files and the separate encrypted
// profile in LocalAppData\PrivateChatDesktop must never be removed by setup.
