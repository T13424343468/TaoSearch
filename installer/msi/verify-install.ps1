# TaoSearch 安装器模拟安装验证 (制度 D-4: 出包前模拟安装+启动调试, 四步留痕)
# 四步: 静默安装 → 验文件与注册表 → 启动核心页 → 卸载干净
# 用法: powershell -File verify-install.ps1 -InstallerExe <引导安装器exe> [-InstallDir <目录>]
param(
    [Parameter(Mandatory = $true)][string]$InstallerExe,
    [string]$InstallDir = "C:\Program Files\TaoSearch"
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

# ---- 第 1 步: 静默安装 (引导安装器自动联网补运行时) ----
Log "STEP1 静默安装开始: $InstallerExe"
$burnLog = Join-Path (Get-Location) "burn-install.log"
$p = Start-Process -FilePath $InstallerExe -ArgumentList "/quiet", "InstallFolder=`"$InstallDir`"", "/norestart", "/log `"$burnLog`"" -Wait -PassThru
Log ("STEP1 安装器退出码: " + $p.ExitCode)
Assert ($p.ExitCode -eq 0) "静默安装退出码为 0"

# ---- 第 2 步: 验证文件与注册表 ----
Log "STEP2 验证文件与注册表"
# 诊断留痕: 分别核对 C 盘指定目录与 D 盘默认目录, 并抽取 Burn 日志里的目录变量
foreach ($probe in @("C:\Program Files\TaoSearch", "D:\Program Files\TaoSearch")) {
    Log ("STEP2 探测 {0}: {1}" -f $probe, (Test-Path (Join-Path $probe $exeName)))
}
# 诊断留痕: 枚举各 Program Files 下 TaoSearch 相关目录与嵌套内容, 定位文件实际去向
foreach ($root in @("C:\Program Files", "C:\Program Files (x86)", "D:\Program Files")) {
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
$arp = Get-ChildItem $runKey -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -eq "TaoSearch" }
Assert ($null -ne $arp) "卸载注册项已登记"
Assert (Test-Path $ctxKey) "右键菜单已注册"
Log "STEP2 文件与注册表验证通过"

# ---- 第 3 步: 启动核心页 ----
Log "STEP3 启动主程序"
Start-Process -FilePath (Join-Path $InstallDir $exeName)
Start-Sleep -Seconds 8
$proc = Get-Process -Name "TaoSearch.App" -ErrorAction SilentlyContinue
Assert ($null -ne $proc) "主程序进程存活（核心页可达）"
Stop-Process -Name "TaoSearch.App" -Force -ErrorAction SilentlyContinue
Log "STEP3 启动验证通过, 进程已结束"

# ---- 第 4 步: 卸载干净 ----
Log "STEP4 卸载"
$bundleArp = Get-ChildItem $runKey -ErrorAction SilentlyContinue | Where-Object {
    $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    $props.DisplayName -eq "TaoSearch" -and $props.UninstallString -like "*exe*"
} | Select-Object -First 1
if ($bundleArp) {
    $unstr = $bundleArp.UninstallString
    Log ("STEP4 卸载命令: " + $unstr)
    $un = $unstr -replace '^"([^"]+)"\s*', '$1 '
    $unExe = $unstr.Split('"')[1]
    $up = Start-Process -FilePath $unExe -ArgumentList "/uninstall", "/quiet", "/norestart" -Wait -PassThru
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
Log "STEP4 卸载干净验证通过"
Log "四步模拟安装验证全部通过"
