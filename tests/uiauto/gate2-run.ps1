# 关2「能不能用」界面自动化清单（主班补位版 v1，2026-11-22）
# 口径：真 Windows 桌面会话内启动服务+壳层，UIA 逐项点通，每步截图留证。
# 严格项失败 → 退出码非零（关2 不通过）；INFO 项只记账不判红。
# 用法: pwsh -File gate2-run.ps1 -PublishDir <publish目录> -EvidenceDir <证据目录>
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$EvidenceDir
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
$results = New-Object System.Collections.Generic.List[string]
$strictFails = 0

function Log([string]$m) { Write-Host $m }
function Check([string]$id, [string]$name, [bool]$ok, [bool]$strict = $true) {
    $verdict = if ($ok) { 'PASS' } elseif ($strict) { 'FAIL' } else { 'INFO' }
    if (-not $ok -and $strict) { $script:strictFails++ }
    $line = "[$verdict] $id $name"
    $results.Add($line); Log $line
}
function Screenshot([string]$name) {
    try {
        Start-Sleep -Milliseconds 600
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bmp.Save("$EvidenceDir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Log "  截图: $name.png"
    } catch { Log "  截图失败 $name : $_" }
}

# ---- 测试语料：小卷目录（避免 runner 全盘慢路径扫描拖死作业） ----
$testVolume = "$EvidenceDir\volume"
New-Item -ItemType Directory -Force -Path "$testVolume\docs" | Out-Null
Set-Content "$testVolume\readme.txt" 'TaoSearch gate2 smoke volume'
Set-Content "$testVolume\docs\发票明细.txt" '测试中文文件名与内容'
Set-Content "$testVolume\docs\notes.md" '# notes gate2'

# ---- 启动服务（指向小卷；数据目录注入临时路径防污染） ----
Log '== 启动服务 =='
$svcLog = "$EvidenceDir\service.log"
$svc = Start-Process -FilePath "$PublishDir\TaoSearch.Service.exe" -ArgumentList "`"$testVolume`"" `
    -WorkingDirectory $PublishDir -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput $svcLog -RedirectStandardError "$EvidenceDir\service.err.log"
$pipeUp = $false
foreach ($i in 1..30) {
    Start-Sleep -Seconds 1
    if ($svc.HasExited) { break }
    try {
        $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'taosearch.svc.1', [System.IO.Pipes.PipeDirection]::InOut)
        $p.ConnectAsync(1000).Wait(1000) | Out-Null
        $p.Dispose(); $pipeUp = $true; break
    } catch { }
}
Check 'G2-00' '服务启动且管道就绪' $pipeUp $true
if (-not $pipeUp) {
    if (Test-Path $svcLog) { Get-Content $svcLog | ForEach-Object { Log "  svc| $_" } }
    if (Test-Path "$EvidenceDir\service.err.log") { Get-Content "$EvidenceDir\service.err.log" | ForEach-Object { Log "  err| $_" } }
}

# ---- 启动壳层 ----
Log '== 启动壳层 =='
# 预置本地设置：跳过首启引导（引导窗标题非 TaoSearch，会干扰 G2-01 定位）
$settingsDir = "$env:LOCALAPPDATA\TaoSearch"
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
Set-Content "$settingsDir\settings.json" '{"OnboardingCompleted":true,"ShowQuickOnStartup":false}' -Encoding UTF8
$appLog = "$EvidenceDir\app.log"
$app = Start-Process -FilePath "$PublishDir\TaoSearch.App.exe" -WorkingDirectory $PublishDir -PassThru `
    -RedirectStandardOutput "$EvidenceDir\app.out.log" -RedirectStandardError $appLog
Start-Sleep -Seconds 6
if ($app.HasExited) { Log "  壳层退出码: $($app.ExitCode)"; if (Test-Path $appLog) { Get-Content $appLog | ForEach-Object { Log "  app| $_" } } }
Check 'G2-00b' '壳层进程存活（启动 6 秒未退出）' (-not $app.HasExited) $true

# ---- UIA 检查 ----
# WinUI3 顶层窗在 UIA 树的挂载点不一定是 RootElement 直接子级：优先进程主窗句柄定位，Name 搜索兜底
$win = $null
foreach ($i in 1..15) {
    $app.Refresh()
    if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
        try { $win = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle) } catch { }
        if ($win) { break }
    }
    Start-Sleep -Seconds 2
}
if (-not $win) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch')
    $win = $root.FindFirst([System.Windows.Automation.AutomationElement]::Descendants, $cond)
}
Check 'G2-01' '主窗口可被 UIA 发现（句柄定位或 Name=TaoSearch）' ($null -ne $win) $true
if ($win) {
    Screenshot 'G2-01-mainwindow'

    # 搜索框
    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edit = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCond)
    Check 'G2-02' '搜索框（Edit 控件）存在' ($null -ne $edit) $true

    if ($edit) {
        try {
            ($edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('readme')
            Start-Sleep -Seconds 2
            $val = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            Check 'G2-03' "输入查询词生效（回读=$val）" ($val -eq 'readme') $true
            Screenshot 'G2-03-query-typed'
        } catch { Check 'G2-03' "输入查询词异常: $_" $false $true }
    }

    # 状态行：有非空文本（INFO——状态文案口径关1差距清单在改）
    try {
        $textCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)
        $texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)
        $names = @($texts | ForEach-Object { $_.Current.Name }) | Where-Object { $_ } | Select-Object -First 12
        Log "  窗口文本元素: $($names -join ' | ')"
        Check 'G2-05' '窗口存在可见文本元素' ($names.Count -gt 0) $false
    } catch { Log "  文本枚举异常: $_" }

    # 范围切换（ToggleSwitch，INFO——控件类型映射待真机校准）
    try {
        $toggle = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)))
        if ($toggle) {
            try {
                ($toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
                Start-Sleep -Milliseconds 800
                ($toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle() # 还原
                Check 'G2-06' '范围切换可操作' $true $false
            } catch { Check 'G2-06' "范围切换模式不支持: $_" $false $false }
        } else { Check 'G2-06' '未定位到范围切换控件' $false $false }
    } catch { Check 'G2-06' "范围切换异常: $_" $false $false }

    # 导航到设置页
    $navOk = $false
    try {
        $navCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '设置')
        $navItem = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $navCond)
        if ($navItem) {
            try {
                ($navItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                $navOk = $true
            } catch {
                try { ($navItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); $navOk = $true }
                catch { Log "  设置导航模式不支持: $_" }
            }
        }
    } catch { Log "  设置导航异常: $_" }
    Check 'G2-07' '导航到设置页' $navOk $true
    if ($navOk) {
        Start-Sleep -Seconds 2
        Screenshot 'G2-07-settings'
        $settingsTexts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text)))
        $snames = @($settingsTexts | ForEach-Object { $_.Current.Name }) | Where-Object { $_ }
        Log "  设置页文本: $($snames -join ' | ')"
        Check 'G2-08' '设置页渲染出索引状态等内容' ($snames.Count -gt 0) $false
    }

    Screenshot 'G2-99-final'
}
else {
    Screenshot 'G2-01-window-not-found'
    try {
        $topCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)
        $tops = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $topCond)
        $topNames = @($tops | ForEach-Object { $_.Current.Name }) | Where-Object { $_ }
        Log "  顶层窗口: $($topNames -join ' | ')"
        $results.Add("[INFO] 顶层窗口: $($topNames -join ' | ')")
    } catch { Log "  顶层窗口枚举失败: $_" }
}

# ---- 收尾 ----
Check 'G2-10' '检查全程壳层未崩溃' (-not $app.HasExited) $true
try { $app.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $app.HasExited) { $app.Kill() } } catch { }
try { if (-not $svc.HasExited) { $svc.Kill() } } catch { }

$results | Set-Content "$EvidenceDir\gate2-results.txt"
Log "== 关2 结果: 严格失败 $strictFails 项 =="
exit $(if ($strictFails -gt 0) { 1 } else { 0 })
