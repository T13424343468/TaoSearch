# 生成 TaoSearch 的 Windows 安装器：产品 MSI（微信风格自绘单页）+ 引导安装器（运行时按 2026-11-21 新标准联网装）
# 制度依据: tao-knowledge-hub 部署交付篇 D-1（MSI 唯一交付 + 微信风格三步）+ tao-build-pitfalls 九（安装器风格全系统一）
# 运行时标准: 安装包不内置运行时; 装时全盘检测, 有则零下载, 无则联网下载官方运行时并显示进度; 无网给重试指引
# 用法: powershell -File package.ps1 -PublishDir <publish目录> -OutDir <out目录> [-WixExe <wix.exe路径>]
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [string]$OutDir = "out",
    [string]$WixExe = "wix",
    [string]$BalExtPath = "",
    [string]$UtilExtPath = "",
    [string]$RuntimeDir = ""
)
$ScriptVersion = "msi-v1-1121"
Write-Host "打包脚本版本: $ScriptVersion (若日志无此行, 说明运行的是旧脚本, 请全新目录重试)"
$ErrorActionPreference = 'Stop'
$ProductVersion = "0.1.0.8"
$UpgradeCode = "9A3E68D7-3E6A-49BF-B003-86C7A28EC6D5"
$BundleUpgradeCode = "E65F0B4C-5C59-4CB8-95AE-565C781111ED"
$publishRoot = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $publishRoot 'TaoSearch.App.exe'))) {
    throw "发布目录里没找到 TaoSearch.App.exe: $PublishDir"
}
if (-not (Test-Path (Join-Path $publishRoot 'TaoSearch.Service.exe'))) {
    throw "发布目录里没找到 TaoSearch.Service.exe: $PublishDir"
}

# ---- 枚举发布文件: 目录树(支持多级) + 组件清单 ----
$allFiles = Get-ChildItem -Path $publishRoot -Recurse -File
$script:dirId = 0
$script:dirRefs = @{}
$tree = [ordered]@{}
foreach ($f in $allFiles) {
    $rel = $f.FullName.Substring($publishRoot.Length).TrimStart('\')
    $parts = $rel -split '\\'
    $node = $tree
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        $seg = $parts[$i]
        if (-not $node.Contains($seg)) { $node[$seg] = [ordered]@{} }
        $node = $node[$seg]
    }
}
function Get-DirXml($node, [string]$prefix) {
    $xml = ""
    foreach ($seg in $node.Keys) {
        $script:dirId++
        $id = "SUBDIR$script:dirId"
        $childPath = if ($prefix) { "$prefix\$seg" } else { $seg }
        $script:dirRefs[$childPath] = $id
        $inner = Get-DirXml $node[$seg] $childPath
        $xml += "<Directory Id=`"$id`" Name=`"$seg`">$inner</Directory>"
    }
    return $xml
}
$dirDefs = Get-DirXml $tree ""

# ---- 用户级旧右键键清理: 旧版安装器/应用运行时写的 HKCU 同名键, 卸载清不到会残留并遮蔽系统级新键 ----
# 装时清 (removeOnInstall) + 卸载时清 (removeOnUninstall); 同一键的装/卸两条登记必须各立其名 (真机 WIX0091 教训)
$hkcuStaleKeys = @(
    "Software\Classes\Directory\shell\TaoSearch"
    "Software\Classes\Directory\Background\shell\TaoSearch"
    "Software\Classes\*\shell\TaoSearch"
)
$hkcuCleanup = ""
$i = 0
foreach ($k in $hkcuStaleKeys) {
    $i++
    $hkcuCleanup += "<RemoveRegistryKey Id=`"HkcuCleanInstall$i`" Root=`"HKCU`" Key=`"$k`" Action=`"removeOnInstall`" />"
    $hkcuCleanup += "<RemoveRegistryKey Id=`"HkcuCleanUninstall$i`" Root=`"HKCU`" Key=`"$k`" Action=`"removeOnUninstall`" />"
}

$components = ""
foreach ($f in $allFiles) {
    $rel = $f.FullName.Substring($publishRoot.Length).TrimStart('\')
    $parts = $rel -split '\\'
    $parent = if ($parts.Length -gt 1) { ($parts[0..($parts.Length-2)] -join '\') } else { "" }
    $dirRef = if ($parent) { $script:dirRefs[$parent] } else { "INSTALLFOLDER" }
    $src = ($publishRoot + '\' + $rel).Replace('\', '/')
    $esc = [System.Security.SecurityElement]::Escape($src)
    $components += "<Component Directory=`"$dirRef`"><File Source=`"$esc`" /></Component>"
}

# ---- 产品 MSI: 微信风格自绘单页 (许可→默认 D:\Program Files\TaoSearch→安装), 快捷方式自动创建, 卸载干净 ----
$wxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Codepage="936" Name="TaoSearch" Manufacturer="Tao" Version="$ProductVersion"
           UpgradeCode="$UpgradeCode" Scope="perMachine" Compressed="yes">
    <MajorUpgrade Schedule="afterInstallInitialize" DowngradeErrorMessage="已安装了更新版本的 TaoSearch，如需重装请先在“应用”中卸载。" AllowSameVersionUpgrades="yes" />
    <!-- 默认装 D 盘 (D:\Program Files\TaoSearch, 界面可改): 制度 D-1/九-3 默认目录要求;
         目录同名属性覆盖: PathEdit 控件绑定 INSTALLFOLDER 属性, Burn 经 MsiProperty 传入同名属性;
         Secure=yes: Burn 提升权限安装时命令行属性须列入 SecureCustomProperties 才能传到服务端, 否则被忽略 -->
    <Property Id="INSTALLFOLDER" Value="D:\Program Files\TaoSearch" Secure="yes" />
    <!-- 装/卸/升级前先结束运行中的 TaoSearch: 覆盖安装时旧进程占用文件会致安装失败;
         Return=ignore: 程序本来没在运行时报错不影响安装 -->
    <CustomAction Id="SetKillCmd" Property="KILLCMD" Value="[SystemFolder]taskkill.exe" />
    <CustomAction Id="KillRunningApp" Property="KILLCMD"
                  ExeCommand="/f /im TaoSearch.App.exe"
                  Execute="immediate" Return="ignore" />
    <CustomAction Id="KillRunningService" Property="KILLCMD"
                  ExeCommand="/f /im TaoSearch.Service.exe"
                  Execute="immediate" Return="ignore" />
    <InstallExecuteSequence>
      <Custom Action="SetKillCmd" After="InstallValidate" />
      <Custom Action="KillRunningApp" After="SetKillCmd" />
      <Custom Action="KillRunningService" After="KillRunningApp" />
    </InstallExecuteSequence>
    <MediaTemplate EmbedCab="yes" />

    <UI Id="TaoSearchInstallUI">
      <TextStyle Id="DlgFont" FaceName="微软雅黑" Size="9" />
      <TextStyle Id="TitleFont" FaceName="微软雅黑" Size="13" Bold="yes" />
      <Property Id="DefaultUIFont" Value="DlgFont" />

      <Dialog Id="MainDlg" Width="440" Height="344" Title="[ProductName] 安装程序">
        <Control Id="Title" Type="Text" X="20" Y="12" Width="400" Height="22" Text="{\TitleFont}安装 TaoSearch" />
        <Control Id="LicenseText" Type="Text" X="20" Y="42" Width="400" Height="92" Text="TaoSearch $ProductVersion，免费使用。本软件按“现状”提供：请仅将它用于合法合规的本地文件搜索用途，作者不对使用本软件产生的任何损失承担责任。" />
        <Control Id="AcceptCheck" Type="CheckBox" X="20" Y="140" Width="400" Height="16" Property="LICENSE_ACCEPTED" CheckBoxValue="1" Text="我已阅读并同意以上说明" />
        <Control Id="SepLine" Type="Line" X="20" Y="164" Width="400" Height="1" />
        <Control Id="DirLabel" Type="Text" X="20" Y="174" Width="400" Height="14" Text="安装目录（可直接输入，不存在的文件夹会自动创建）：" />
        <Control Id="DirEdit" Type="PathEdit" X="20" Y="192" Width="310" Height="17" Property="INSTALLFOLDER" />
        <Control Id="InstallNow" Type="PushButton" X="340" Y="282" Width="80" Height="24" Default="yes" Text="立即安装">
          <Publish Event="SpawnDialog" Value="AcceptWarnDlg" Condition="NOT LICENSE_ACCEPTED" Order="1" />
          <Publish Event="NewDialog" Value="VerifyReadyDlg" Condition="LICENSE_ACCEPTED" Order="2" />
        </Control>
        <Control Id="CancelBtn" Type="PushButton" X="340" Y="310" Width="80" Height="18" Cancel="yes" Text="取消">
          <Publish Event="EndDialog" Value="Exit" />
        </Control>
      </Dialog>

      <Dialog Id="VerifyReadyDlg" Width="440" Height="130" Title="[ProductName] 安装程序">
        <Control Id="ReadyText" Type="Text" X="20" Y="30" Width="400" Height="24" Text="准备安装 TaoSearch，点击“安装”开始。首次安装如缺运行组件会联网下载官方组件（只需一次）。" />
        <Control Id="BackBtn" Type="PushButton" X="270" Y="95" Width="70" Height="22" Text="返回">
          <Publish Event="NewDialog" Value="MainDlg" />
        </Control>
        <Control Id="InstallBtn" Type="PushButton" X="350" Y="95" Width="70" Height="22" Default="yes" Text="安装">
          <Publish Event="EndDialog" Value="Return" />
        </Control>
      </Dialog>

      <Dialog Id="ProgressDlg" Width="440" Height="110" Title="[ProductName] 安装程序" Modeless="yes">
        <Control Id="ProgressText" Type="Text" X="20" Y="24" Width="400" Height="14" Text="正在安装，请稍候……" />
        <Control Id="ProgressBar" Type="ProgressBar" X="20" Y="48" Width="400" Height="14" Text="正在安装" />
        <Control Id="CancelBtn" Type="PushButton" X="350" Y="78" Width="70" Height="20" Cancel="yes" Text="取消">
          <Publish Event="SpawnDialog" Value="CancelDlg" />
        </Control>
      </Dialog>

      <Dialog Id="AcceptWarnDlg" Width="280" Height="90" Title="[ProductName] 安装程序">
        <Control Id="WarnText" Type="Text" X="20" Y="18" Width="240" Height="18" Text="请先勾选“我已阅读并同意以上说明”。" />
        <Control Id="OkBtn" Type="PushButton" X="110" Y="55" Width="60" Height="20" Default="yes" Text="确定">
          <Publish Event="EndDialog" Value="Return" />
        </Control>
      </Dialog>
      <Dialog Id="CancelDlg" Width="280" Height="90" Title="[ProductName] 安装程序">
        <Control Id="CancelText" Type="Text" X="20" Y="18" Width="240" Height="18" Text="确定要取消安装吗？" />
        <Control Id="YesBtn" Type="PushButton" X="100" Y="55" Width="60" Height="20" Default="yes" Text="是">
          <Publish Event="EndDialog" Value="Exit" />
        </Control>
        <Control Id="NoBtn" Type="PushButton" X="170" Y="55" Width="60" Height="20" Text="否">
          <Publish Event="EndDialog" Value="Return" />
        </Control>
      </Dialog>

      <Dialog Id="ExitDialog" Width="440" Height="130" Title="[ProductName] 安装程序">
        <Control Id="DoneText" Type="Text" X="20" Y="30" Width="400" Height="24" Text="TaoSearch 安装完成，双击桌面图标即可开始搜索。" />
        <Control Id="FinishBtn" Type="PushButton" X="350" Y="95" Width="70" Height="22" Default="yes" Text="完成">
          <Publish Event="EndDialog" Value="Return" />
        </Control>
      </Dialog>
      <Dialog Id="UserExitDlg" Width="440" Height="130" Title="[ProductName] 安装程序">
        <Control Id="UserExitText" Type="Text" X="20" Y="30" Width="400" Height="24" Text="安装已取消，本机未做任何更改。" />
        <Control Id="FinishBtn" Type="PushButton" X="350" Y="95" Width="70" Height="22" Default="yes" Text="完成">
          <Publish Event="EndDialog" Value="Return" />
        </Control>
      </Dialog>
      <Dialog Id="FatalErrorDlg" Width="440" Height="130" Title="[ProductName] 安装程序">
        <Control Id="ErrorText" Type="Text" X="20" Y="30" Width="400" Height="24" Text="安装未完成，请重试或联系开发者。" />
        <Control Id="FinishBtn" Type="PushButton" X="350" Y="95" Width="70" Height="22" Default="yes" Text="完成">
          <Publish Event="EndDialog" Value="Return" />
        </Control>
      </Dialog>

      <InstallUISequence>
        <!-- 显式 CostFinalize (950): 先把默认目录算好再弹窗, 目录框不会空着 -->
        <CostFinalize Sequence="950" />
        <Show Dialog="MainDlg" Sequence="1250" />
        <Show Dialog="ProgressDlg" Before="ExecuteAction" />
        <Show Dialog="ExitDialog" OnExit="success" />
        <Show Dialog="UserExitDlg" OnExit="cancel" />
        <Show Dialog="FatalErrorDlg" OnExit="error" />
      </InstallUISequence>
    </UI>

    <StandardDirectory Id="DesktopFolder" />
    <StandardDirectory Id="ProgramFiles6432Folder">
      <Directory Id="INSTALLFOLDER" Name="TaoSearch">$dirDefs</Directory>
    </StandardDirectory>
    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="ApplicationProgramsFolder" />
    </StandardDirectory>

    <!-- 单一核心功能: 快捷方式自动创建 (制度: 不做勾选项), 右键菜单默认注册 -->
    <Feature Id="Core" Title="TaoSearch 主程序" Description="搜索服务 + 界面 + 右键菜单（必装）" Level="1" AllowAbsent="no">
      $components
      <Component Directory="ApplicationProgramsFolder">
        <Shortcut Id="MenuLnk" Name="TaoSearch" Target="[INSTALLFOLDER]TaoSearch.App.exe" WorkingDirectory="INSTALLFOLDER">
          <!-- AUMID 与壳层 JumpListService.AppId 一致，任务栏跳转列表据此挂接 -->
          <ShortcutProperty Key="System.AppUserModel.ID" Value="Tao.TaoSearch" />
        </Shortcut>
        <RemoveFolder Id="RemoveMenuDir" On="uninstall" />
        <RegistryValue Root="HKCU" Key="Software\Tao\TaoSearch" Name="MenuShortcut" Type="integer" Value="1" KeyPath="yes" />
      </Component>
      <Component Directory="DesktopFolder">
        <Shortcut Id="DesktopLnk" Name="TaoSearch" Target="[INSTALLFOLDER]TaoSearch.App.exe" WorkingDirectory="INSTALLFOLDER">
          <ShortcutProperty Key="System.AppUserModel.ID" Value="Tao.TaoSearch" />
        </Shortcut>
        <RegistryValue Root="HKCU" Key="Software\Tao\TaoSearch" Name="DesktopShortcut" Type="integer" Value="1" KeyPath="yes" />
      </Component>
      <!-- 用户级旧右键键清理: 装时清掉残留 (新键不被旧键遮蔽), 卸载时清掉应用运行时写的键 (卸载即干净) -->
      <Component Directory="INSTALLFOLDER">
        <RegistryValue Root="HKCU" Key="Software\Tao\TaoSearch" Name="HkcuAssocCleanup" Type="integer" Value="1" KeyPath="yes" />
        $hkcuCleanup
      </Component>
      <!-- 右键菜单: 文件夹 / 文件夹内空白处 / 所有文件, 系统级注册, 卸载即清 -->
      <Component Directory="INSTALLFOLDER">
        <RegistryValue Root="HKLM" Key="Software\Tao\TaoSearch" Name="ContextMenu" Type="integer" Value="1" KeyPath="yes" />
        <RemoveRegistryKey Root="HKLM" Key="Software\Classes\Directory\shell\TaoSearch" Action="removeOnUninstall" />
        <RegistryKey Root="HKLM" Key="Software\Classes\Directory\shell\TaoSearch">
          <RegistryValue Type="string" Value="用 TaoSearch 搜索" />
          <RegistryValue Name="Icon" Type="string" Value="&quot;[INSTALLFOLDER]TaoSearch.App.exe&quot;,0" />
          <RegistryKey Key="command"><RegistryValue Type="string" Value="&quot;[INSTALLFOLDER]TaoSearch.App.exe&quot; &quot;%1&quot;" /></RegistryKey>
        </RegistryKey>
        <RemoveRegistryKey Root="HKLM" Key="Software\Classes\Directory\Background\shell\TaoSearch" Action="removeOnUninstall" />
        <RegistryKey Root="HKLM" Key="Software\Classes\Directory\Background\shell\TaoSearch">
          <RegistryValue Type="string" Value="用 TaoSearch 搜索" />
          <RegistryValue Name="Icon" Type="string" Value="&quot;[INSTALLFOLDER]TaoSearch.App.exe&quot;,0" />
          <RegistryKey Key="command"><RegistryValue Type="string" Value="&quot;[INSTALLFOLDER]TaoSearch.App.exe&quot; &quot;%V&quot;" /></RegistryKey>
        </RegistryKey>
        <RemoveRegistryKey Root="HKLM" Key="Software\Classes\*\shell\TaoSearch" Action="removeOnUninstall" />
        <RegistryKey Root="HKLM" Key="Software\Classes\*\shell\TaoSearch">
          <RegistryValue Type="string" Value="用 TaoSearch 搜索" />
          <RegistryValue Name="Icon" Type="string" Value="&quot;[INSTALLFOLDER]TaoSearch.App.exe&quot;,0" />
          <RegistryKey Key="command"><RegistryValue Type="string" Value="&quot;[INSTALLFOLDER]TaoSearch.App.exe&quot; &quot;%1&quot;" /></RegistryKey>
        </RegistryKey>
      </Component>
    </Feature>
  </Package>
</Wix>
"@

$tmpDir = if ($env:TEMP) { $env:TEMP } else { [System.IO.Path]::GetTempPath() }
$wxsPath = Join-Path $tmpDir "TaoSearch.Package.wxs"
[System.IO.File]::WriteAllText($wxsPath, $wxs, (New-Object System.Text.UTF8Encoding($true)))

# ---- 引导安装器 (Burn): 运行时按新标准联网装, 不内置任何运行时 ----
# .NET 10 桌面运行时: 注册表精确版本探测, 已装则零下载; 未装则联网下载官方包静默装 (Burn 自带进度);
#   1638=已装更新版(视为成功), 3010=需重启(视为成功)
# Windows App SDK 1.8 运行时: MSIX 形态无安装器侧可靠注册表探测, 官方安装器自带"已装即跳过",
#   0x80073D06=已装更新版(视为成功)
$bundleWxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"
     xmlns:bal="http://wixtoolset.org/schemas/v4/wxs/bal"
     xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util">
  <Bundle Name="TaoSearch" Manufacturer="Tao" Version="$ProductVersion"
          UpgradeCode="$BundleUpgradeCode" Compressed="yes">
    <BootstrapperApplication>
      <bal:WixStandardBootstrapperApplication Theme="rtfLargeLicense" LicenseFile="$([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'license.rtf')))" ShowVersion="yes" />
    </BootstrapperApplication>
    <Variable Name="InstallFolder" Value="D:\Program Files\TaoSearch" bal:Overridable="yes" />
    <util:RegistrySearch Id="Net10DesktopSearch" Variable="Net10Desktop1015" Root="HKLM" Bitness="always64"
                         Key="SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
                         Value="10.0.15" Result="exists" />
    <Chain>
      <ExePackage Id="Net10DesktopRuntime" DisplayName=".NET 桌面运行时（官方组件）"
                  Name="windowsdesktop-runtime-win-x64.exe"
                  DownloadUrl="https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
                  DetectCondition="Net10Desktop1015"
                  InstallArguments="/install /quiet /norestart"
                  RepairArguments="/repair /quiet /norestart"
                  Permanent="yes"
                  PerMachine="yes"
                  Compressed="no">
        <ExitCode Value="1638" Behavior="success" />
        <ExitCode Value="3010" Behavior="success" />
      </ExePackage>
      <!-- WinAppSDK 运行时已随应用自包含部署(WindowsAppSDKSelfContained), 不再依赖系统运行时, 从安装链移除 -->
      <MsiPackage Id="TaoSearchMsi" SourceFile="$OutDir\TaoSearch-$ProductVersion-x64.msi" Vital="yes">
        <MsiProperty Name="INSTALLFOLDER" Value="[InstallFolder]" />
      </MsiPackage>
    </Chain>
  </Bundle>
</Wix>
"@

$bundlePath = Join-Path $tmpDir "TaoSearch.Bundle.wxs"
[System.IO.File]::WriteAllText($bundlePath, $bundleWxs, (New-Object System.Text.UTF8Encoding($true)))

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$outFull = (Resolve-Path $OutDir).Path

# ---- 构建 MSI ----
$msi = Join-Path $outFull "TaoSearch-$ProductVersion-x64.msi"
if (Test-Path $msi) { Remove-Item $msi -Force }
Write-Host "构建产品 MSI..."
& $WixExe build $wxsPath -arch x64 -pdbtype none -o $msi 2>&1 | Tee-Object -FilePath (Join-Path $outFull "msi-build.log")
if ($LASTEXITCODE -ne 0) {
    Write-Host "打包配置已保存: $wxsPath (排错用, 请把此文件与上方完整报错一并发出)"
    throw "WiX 构建 msi 失败"
}
Write-Host "MSI 已生成: $msi"

# ---- 构建引导安装器 (需 Bal 扩展) ----
$exe = Join-Path $outFull "TaoSearch-$ProductVersion-x64.exe"
if (Test-Path $exe) { Remove-Item $exe -Force }
Write-Host "构建引导安装器..."
# WinAppSDK 运行时为 MSIX 形态, 无注册表探测键 (微软官方以 PackageManager 检测), 官方安装器自检"已装即跳过";
#   无 DetectCondition 的 WIX1161 建议性警告按已知限制抑制 (-sw1161), 不影响安装正确性
# 扩展引用: 传了全路径就用全路径 (CI 直接解包引用, 不依赖 wix extension add 缓存), 否则按扩展名走缓存
$balArg = if ($BalExtPath) { $BalExtPath } else { "WixToolset.Bal.wixext" }
$utilArg = if ($UtilExtPath) { $UtilExtPath } else { "WixToolset.Util.wixext" }
$bindArgs = @()
if ($RuntimeDir) { $bindArgs = @("-b", $RuntimeDir) }
& $WixExe build $bundlePath -pdbtype none @bindArgs -ext $balArg -ext $utilArg -sw1161 -o $exe 2>&1 | Tee-Object -FilePath (Join-Path $outFull "bundle-build.log")
if ($LASTEXITCODE -ne 0) {
    Write-Host "打包配置已保存: $bundlePath (排错用, 请把此文件与上方完整报错一并发出)"
    throw "WiX 构建引导安装器失败"
}
Write-Host "引导安装器已生成: $exe"

# ---- 出包自检: out 最终只允许 1 个 msi + 1 个引导 exe (制度: 项数自检) ----
$msiCount = (Get-ChildItem $outFull -Filter *.msi | Measure-Object).Count
$exeCount = (Get-ChildItem $outFull -Filter "TaoSearch-*.exe" | Measure-Object).Count
if ($msiCount -ne 1) { throw "出包自检失败: msi 数量应为 1, 实际 $msiCount" }
if ($exeCount -ne 1) { throw "出包自检失败: 引导安装器数量应为 1, 实际 $exeCount" }
Write-Host "出包自检通过: 1 msi + 1 引导安装器"
Get-ChildItem $outFull | ForEach-Object { Write-Host ("  {0}  {1:N0} 字节" -f $_.Name, $_.Length) }
