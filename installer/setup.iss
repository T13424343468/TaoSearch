; TaoSearch 安装器（Inno Setup 6）
; 按安装器运行时新标准：不内置 .NET 运行时；安装时先全盘检测，无则联网下载官方运行时并显示进度；无网给通俗重试指引。
; 集成功能勾选：桌面快捷方式 / 开机自启 / 右键菜单，用户在"选择组件"页自由勾选。

#define MyAppName "TaoSearch"
#define MyAppVersion "0.1.0.4"
#define MyAppPublisher "Tao"
#define MyAppExeName "TaoSearch.App.exe"
#define DotNetVersion "10.0.15"
#define DotNetUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.15/windowsdesktop-runtime-10.0.15-win-x64.exe"
#define WinAppSdkUrl "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe"

[Setup]
AppId={{400FD9CB-5592-4075-A9A2-BCC34605B354}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\TaoSearch
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=TaoSearch-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Types]
Name: "full"; Description: "完整安装（全部集成功能）"
Name: "compact"; Description: "精简安装（仅核心）"
Name: "custom"; Description: "自定义（手动勾选集成功能）"; Flags: iscustom

[Components]
Name: "core"; Description: "TaoSearch 核心程序（搜索服务 + 界面，必装）"; Types: full compact custom; Flags: fixed
Name: "desktopicon"; Description: "桌面快捷方式"; Types: full custom
Name: "autostart"; Description: "开机自动启动"; Types: full custom
Name: "contextmenu"; Description: "右键菜单（文件与文件夹『用 TaoSearch 搜索』）"; Types: full custom

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Components: desktopicon

[Registry]
; 开机自启（当前用户，卸载自动清除）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TaoSearch"; ValueData: """{app}\{#MyAppExeName}"""; Components: autostart; Flags: uninsdeletevalue
; 右键菜单：文件夹
Root: HKCU; Subkey: "Software\Classes\Directory\shell\TaoSearch"; ValueType: string; ValueName: ""; ValueData: "用 TaoSearch 搜索"; Components: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\TaoSearch"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName}"; Components: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\TaoSearch\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Components: contextmenu; Flags: uninsdeletekey
; 右键菜单：文件夹内空白处
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\TaoSearch"; ValueType: string; ValueName: ""; ValueData: "用 TaoSearch 搜索"; Components: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\TaoSearch"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName}"; Components: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\TaoSearch\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%V"""; Components: contextmenu; Flags: uninsdeletekey
; 右键菜单：所有文件
Root: HKCU; Subkey: "Software\Classes\*\shell\TaoSearch"; ValueType: string; ValueName: ""; ValueData: "用 TaoSearch 搜索"; Components: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\TaoSearch"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName}"; Components: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\TaoSearch\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Components: contextmenu; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// ---- 运行时检测（按安装器运行时新标准：先检测，有则不下载，无则联网装官方运行时，无网给通俗重试指引） ----

// .NET 10 桌面运行时：统一目录 + 注册表双查
function DotNet10DesktopInstalled(): Boolean;
var
  dir, sub: String;
  sr: TFindRec;
  found: Boolean;
begin
  Result := False;
  dir := ExpandConstant('{autoprogramfiles}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(dir) then
  begin
    found := False;
    if FindFirst(dir + '\*', sr) then
    begin
      try
        repeat
          if (sr.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            if Pos('10.', sr.Name) = 1 then found := True;
        until (not FindNext(sr)) or found;
      finally
        FindClose(sr);
      end;
    end;
    if found then begin Result := True; Exit; end;
  end;
  sub := '';
  if RegQueryStringValue(HKLM, 'SOFTWARE\dotnet\Setup', 'InstallSuccess', sub) then
    if sub <> '' then Result := True;
end;

// Windows App SDK 运行时（WinUI3 界面依赖）：查已装运行时包
function WinAppSdkInstalled(): Boolean;
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -Command "if (Get-AppxPackage -Name ''Microsoft.WindowsAppRuntime.1.*'') { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, rc);
  Result := (rc = 0);
end;

var
  DownloadPage: TDownloadWizardPage;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('正在准备', '正在下载官方运行时（只需一次）', nil);
end;

// 下载并静默安装一个官方运行时；失败时给通俗指引，返回是否继续安装
function FetchAndInstallRuntime(const Url, SaveName, Desc, SizeHint: String): Boolean;
var
  runtimeFile: String;
  resultCode: Integer;
  retry: Integer;
  done: Boolean;
begin
  Result := True;
  DownloadPage.Clear;
  DownloadPage.Add(Url, SaveName, '');
  retry := 0;
  done := False;
  while (not done) and (retry < 2) do
  begin
    DownloadPage.Show;
    try
      try
        DownloadPage.Download; // 下载到 {tmp}
        done := True;
      except
        retry := retry + 1;
        if retry >= 2 then
        begin
          if MsgBox('下载官方 ' + Desc + ' 失败：当前可能没有联网。' #13#10 #13#10 '这台电脑还缺 ' + Desc + '，缺它 TaoSearch 就启动不了。' #13#10 '请连上网后点【重试】；或点【取消】退出，联网后重新运行本安装程序。', mbError, MB_RETRYCANCEL) = IDRETRY then
            retry := 0;
        end;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
  if not done then
  begin
    Result := False;
    Exit;
  end;

  runtimeFile := ExpandConstant('{tmp}\') + SaveName;
  Exec(runtimeFile, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, resultCode);
  if resultCode <> 0 then
  begin
    MsgBox(Desc + ' 安装未完成（代码 ' + IntToStr(resultCode) + '）。' #13#10 '如果刚才弹过"重启"提示，重启电脑后再运行本安装程序即可。', mbError, MB_OK);
    Result := False;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;

  // 有则不下载、不重复安装
  if not DotNet10DesktopInstalled() then
  begin
    if not FetchAndInstallRuntime('{#DotNetUrl}', 'windowsdesktop-runtime.exe', '.NET 运行时', '约 55 MB') then
    begin
      Result := False;
      Exit;
    end;
  end;

  if not WinAppSdkInstalled() then
  begin
    if not FetchAndInstallRuntime('{#WinAppSdkUrl}', 'windowsappruntimeinstall-x64.exe', 'Windows 应用运行时', '约 70 MB') then
      Result := False;
  end;
end;
