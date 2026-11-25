# TaoSearch 安装器模拟安装验证 (制度 D-4: 出包前模拟安装+启动调试, 四步留痕)
# 四步: 静默安装 → 验文件与注册表 → 启动核心页 → 卸载干净
# 用法: powershell -File verify-install.ps1 -InstallerExe <引导安装器exe> [-InstallDir <目录>]
param(
    [Parameter(Mandatory = $true)][string]$InstallerExe,
    [string]$InstallDir = "C:\Program Files\TaoSearch",
    [string]$PublishDir = ""
)
$ErrorActionPreference = 'Stop'
$log = Join-Path (Get-Location) "verify-install.log"
function Log([string]$m) {
    $line = "{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m
    Write-Host $line
    Add-Content -Path $log -Value $line
}
function Assert([bool]$cond, [string]$name) {
    if (-not $cond) { Log "FAIL: $name"; throw "验证失败: $name" }
    Log "PASS: $name"
}
$exeName = "TaoSearch.App.exe"
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
$ctxKey = "HKLM:\SOFTWARE\Classes\Directory\shell\TaoSearch"
$EvidenceDir = Join-Path (Get-Location) "verify-evidence"
New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null

# ---- 第 1 步: 静默安装 (引导安装器自动联网补运行时) ----
Log "STEP1 静默安装开始: $InstallerExe"
$burnLog = Join-Path (Get-Location) "burn-install.log"
$p = Start-Process -FilePath $InstallerExe -ArgumentList "/quiet", "InstallFolder=`"$InstallDir`"", "/norestart", "/log `"$burnLog`"" -Wait -PassThru
Log ("STEP1 安装器退出码: " + $p.ExitCode)
Assert ($p.ExitCode -eq 0) "静默安装退出码为 0"

# ---- 第 2 步: 验证文件与注册表 ----
Log "STEP2 验证文件与注册表"
# 诊断留痕: 核对目标目录与两个默认目录, 并抽取 Burn 日志里的目录变量
foreach ($probe in @($InstallDir, "C:\Program Files\TaoSearch", "D:\Program Files\TaoSearch")) {
    Log ("STEP2 探测 {0}: {1}" -f $probe, (Test-Path (Join-Path $probe $exeName)))
}
# 诊断留痕: 枚举常见落盘点 TaoSearch 相关目录与嵌套内容, 定位文件实际去向
foreach ($root in @("C:\Program Files", "C:\Program Files (x86)", "D:\Program Files", "C:\")) {
    $dirs = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*Tao*" }
    foreach ($d in $dirs) {
        Log ("STEP2 发现目录: " + $d.FullName)
        Get-ChildItem $d.FullName -Recurse -ErrorAction SilentlyContinue | Select-Object -First 15 | ForEach-Object { Log ("  - " + $_.FullName) }
    }
}
if (Test-Path $burnLog) {
    $varLines = Select-String -Path $burnLog -Pattern "InstallFolder" -ErrorAction SilentlyContinue | Select-Object -First 6
    foreach ($v in $varLines) { Log ("STEP2 Burn日志: " + $v.Line.Trim()) }
    # 诊断留痕: 打印 MSI 执行段全文 (从首个 Applying execute package 到日志末尾), 看 MSI 退出码与错误
    $allLines = Get-Content $burnLog
    $applyIdx = ($allLines | Select-String -Pattern "Applying execute package" | Select-Object -First 1).LineNumber
    if ($applyIdx) {
        Log "STEP2 Burn日志 MSI执行段:"
        $allLines[($applyIdx - 1)..($allLines.Count - 1)] | Select-Object -First 120 | ForEach-Object { Log ("  | " + $_.Trim()) }
    }
} else {
    Log "STEP2 Burn日志不存在"
}
Assert (Test-Path (Join-Path $InstallDir $exeName)) "主程序已安装到目标目录"
# 诊断留痕: 已装文件数与发布产物文件数比对 (发现部分安装/载荷缺失)
if ($PublishDir -and (Test-Path $PublishDir)) {
    $pubCount = (Get-ChildItem $PublishDir -Recurse -File | Measure-Object).Count
    $instCount = (Get-ChildItem $InstallDir -Recurse -File | Measure-Object).Count
    Log ("STEP2 文件数: 发布 {0} / 已装 {1}" -f $pubCount, $instCount)
    Assert ($instCount -eq $pubCount) "安装文件数与发布产物一致"
}
$arp = Get-ChildItem $runKey -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -eq "TaoSearch" }
Assert ($null -ne $arp) "卸载注册项已登记"
# 诊断留痕: 逐条列出 TaoSearch 卸载项的键名与 DisplayIcon (定位图标缺失来源)
foreach ($k in @($arp)) {
    $p = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
    Log ("STEP2 卸载项: 键={0} DisplayName={1} DisplayIcon={2}" -f $k.PSChildName, $p.DisplayName, ($p.DisplayIcon ?? '<空>'))
}
# 卸载列表图标: DisplayIcon 必须指向产品图标 (真机反馈: 卸载列表白板)
$arpDisplayIcons = @($arp | ForEach-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayIcon } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
Assert ($arpDisplayIcons.Count -gt 0) "卸载注册项含 DisplayIcon 图标"
Log ("STEP2 卸载列表图标: " + ($arpDisplayIcons -join ' | '))
Assert (Test-Path $ctxKey) "右键菜单已注册"
$svc = Get-Service -Name 'TaoSearchService' -ErrorAction SilentlyContinue
Assert ($null -ne $svc) "TaoSearchService 服务已注册"
Assert ($svc.Status -eq 'Running') ("TaoSearchService 运行中，实际: " + $svc.Status)

Log "STEP2 文件与注册表验证通过"

# ---- 第 3 步: 启动核心页 ----
Log "STEP3 启动主程序"
# 诊断留痕: 主程序大小 + 已装 WinAppSDK 运行时包 + SideBySide 事件日志 (启动失败时指明缺失组件)
$exePath = Join-Path $InstallDir $exeName
Log ("STEP3 主程序大小: " + (Get-Item $exePath).Length + " 字节")
Get-AppxPackage *WindowsAppRuntime* -ErrorAction SilentlyContinue | ForEach-Object { Log ("STEP3 运行时包: " + $_.Name + " " + $_.Version) }
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='SideBySide'} -MaxEvents 3 -ErrorAction SilentlyContinue | ForEach-Object { Log ("STEP3 SideBySide事件: " + ($_.Message -replace "\s+", " ")) }
# 诊断留痕: 内嵌清单全文 (SxS 失败时核对清单引用的依赖程序集)
& (Join-Path $PSScriptRoot "diag-app.ps1") -ExePath $exePath
try {
    Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
} catch {
    Log ("STEP3 启动异常: " + $_.Exception.Message)
    # 诊断留痕: 宽事件日志 (最近 20 条 Application 事件, 含 SxS/应用错误详情)
    Get-WinEvent -LogName Application -MaxEvents 20 -ErrorAction SilentlyContinue | ForEach-Object {
        $msg = if ($_.Message) { ($_.Message -replace "\s+", " ") } else { "(无正文)" }
        if ($msg.Length -gt 300) { $msg = $msg.Substring(0, 300) }
        Log ("STEP3 事件[" + $_.TimeCreated.ToString("HH:mm:ss") + "][" + $_.ProviderName + "]: " + $msg)
    }
    throw
}
Start-Sleep -Seconds 8
$proc = Get-Process -Name "TaoSearch.App" -ErrorAction SilentlyContinue
Assert ($null -ne $proc) "主程序进程存活（核心页可达）"
# ---- 界面自动化验证 (制度六.7 视觉出图强制: 无图不算绿) ----
# 1) 窗口真实创建: 主窗口句柄非零 + 标题匹配
$winProc = Get-Process -Name "TaoSearch.App" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
if (-not $winProc) { Start-Sleep -Seconds 5; $winProc = Get-Process -Name "TaoSearch.App" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } }
Assert ($null -ne $winProc) "主窗口已创建（窗口句柄非零）"
$winTitle = [string]$winProc.MainWindowTitle
Log ("STEP3 主窗口标题: " + $winTitle)
# 断言收紧: 标题必须精确等于应用窗口标题(主窗或首次使用引导窗, 关7持久化修复后全新安装正确进入引导页);
# 运行时缺失时错误弹框标题含 exe 名, 不得假绿 (0.1.0.6 教训)
Assert ($winTitle -eq "TaoSearch" -or $winTitle -eq "TaoSearch 首次使用引导") "主窗口标题精确匹配（排除运行时错误弹框）"
# 2) 屏幕截图留证: 全屏截图落盘, 启动失败或窗口空白可肉眼核对
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$shot = Join-Path $EvidenceDir "step3-window-screenshot.png"
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$g.Dispose()
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Log ("STEP3 窗口截图已存: " + $shot + " (" + (Get-Item $shot).Length + " 字节)")
Assert ((Get-Item $shot).Length -gt 5000) "窗口截图非空白（文件体量合理）"
Stop-Process -Name "TaoSearch.App" -Force -ErrorAction SilentlyContinue
Log "STEP3 启动验证通过: 进程存活 + 窗口句柄 + 标题匹配 + 截图留证"

# ---- 第 4 步: 卸载干净 ----
Log "STEP4 卸载"
# 卸载优先走 Burn 引导包登记项 (其 UninstallString 指向 Package Cache 里的引导 exe);
# 若只匹配到 MSI 登记项 (MsiExec.exe /I{guid}), 则改用 msiexec /x 卸载
$arpEntries = Get-ChildItem $runKey -ErrorAction SilentlyContinue | ForEach-Object {
    $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    if ($props.DisplayName -eq "TaoSearch" -and $props.UninstallString -like "*exe*") {
        [pscustomobject]@{ Key = $_; Unstr = [string]$props.UninstallString }
    }
}
$bundleEntry = $arpEntries | Where-Object { $_.Unstr -notmatch '(?i)msiexec\.exe' } | Select-Object -First 1
if ($bundleEntry) {
    $unstr = $bundleEntry.Unstr
    Log ("STEP4 卸载命令: " + $unstr)
    Assert (-not [string]::IsNullOrWhiteSpace($unstr)) "卸载登记项含 UninstallString"
    if ($unstr -match '^"([^"]+)"') { $unExe = $Matches[1] } else { $unExe = ($unstr -split '\s+')[0] }
    Assert ($unExe -and (Test-Path $unExe)) "卸载命令解析出有效路径"
    $up = Start-Process -FilePath $unExe -ArgumentList "/uninstall", "/quiet", "/norestart" -Wait -PassThru
    Log ("STEP4 卸载退出码: " + $up.ExitCode)
    Assert ($up.ExitCode -eq 0) "卸载退出码为 0"
} elseif ($arpEntries) {
    $unstr = $arpEntries[0].Unstr
    Log ("STEP4 卸载命令(MSI): " + $unstr)
    Assert ($unstr -match '\{([0-9A-Fa-f\-]+)\}') "MSI 卸载登记项含产品码"
    $up = Start-Process msiexec.exe -ArgumentList "/x {$($Matches[1])}", "/quiet", "/norestart" -Wait -PassThru
    Log ("STEP4 卸载退出码: " + $up.ExitCode)
    Assert ($up.ExitCode -eq 0) "卸载退出码为 0"
} else {
    throw "未找到卸载登记项"
}
Start-Sleep -Seconds 3
Assert (-not (Test-Path (Join-Path $InstallDir $exeName))) "主程序文件已清除"
$arpAfter = Get-ChildItem $runKey -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -eq "TaoSearch" }
Assert ($null -eq $arpAfter) "卸载注册项已清除"
Assert (-not (Test-Path $ctxKey)) "右键菜单已清除"
$svcU = Get-Service -Name 'TaoSearchService' -ErrorAction SilentlyContinue
Assert ($null -eq $svcU) "TaoSearchService 服务已随卸载移除"

Log "STEP4 卸载干净验证通过"
Log "四步模拟安装验证全部通过"
