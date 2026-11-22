# 关2「能不能用」界面自动化清单（主班补位版 v2，2026-11-22 全面覆盖令）
# 口径：真 Windows 桌面会话内启动服务+壳层，UIA 逐项点通，每步截图留证。
# v4d（2026-11-22）：G2-27 段先关遮挡主窗的快速窗再滚轮；G2-26c 改轻量轮询，不饿死 UI 线程。
# v4（2026-11-22 老板令「必须全面覆盖无死角」）：对账补齐 G2-22~G2-27（内容页签全文搜索/快速窗输入即搜/设置页按钮群/Alt+Q 真实按键/单实例转发/托盘退出）；v3 托盘命令链路 G2-20（关窗进托盘→右键回调→打开主窗复现）、
#   托盘「快速搜索」拉起快速窗并截图（G2-21）；v2：引导全流程（G2-11~15）、热键开关切换（G2-16）、托盘 UIA 探针（G2-17）、自启开关试切（G2-18）、引导跳过路径（G2-19）。
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

function Click-UiaElement([System.Windows.Automation.AutomationElement]$el) {
    # Select 不可用时按可点坐标真实点一下（托管 UIA 无 LegacyIAccessiblePattern）
    try {
        if (-not ('G2Click.Native' -as [type])) {
            Add-Type -Namespace G2Click -Name Native -MemberDefinition '
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);'
        }
        $pt = $el.GetClickablePoint()
        [G2Click.Native]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
        Start-Sleep -Milliseconds 200
        [G2Click.Native]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        [G2Click.Native]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        return $true
    } catch { Log "  鼠标点击失败: $_"; return $false }
}

function Scroll-WheelOver([System.Windows.Automation.AutomationElement]$el, [int]$notches) {
    # 主题卡在首屏外（截图实证）：光标移到元素区中央，用鼠标滚轮下滚（负 delta）数格
    try {
        if (-not ('G2Wheel.Native' -as [type])) {
            Add-Type -Namespace G2Wheel -Name Native -MemberDefinition '
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);'
        }
        $r = $el.Current.BoundingRectangle
        if ($r.IsEmpty) { return $false }
        [G2Wheel.Native]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
        Start-Sleep -Milliseconds 200
        foreach ($n in 1..$notches) {
            [G2Wheel.Native]::mouse_event(0x0800, 0, 0, -120, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 150
        }
        return $true
    } catch { Log "  滚轮滚动失败: $_"; return $false }
}

function Close-UiaDialog([System.Windows.Automation.AutomationElement]$w) {
    # 关闭可能打开的 ContentDialog（关闭按钮文案「好」），模拟真实用户先关弹窗再操作
    try {
        $okBtn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, '好')))
        if ($okBtn) {
            try { ($okBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch { }
            Start-Sleep -Milliseconds 800
        }
    } catch { }
}

function Invoke-UiaButton([System.Windows.Automation.AutomationElement]$w, [string]$label, [string]$id, [bool]$strict) {
    try {
        $btnCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $label)
        $btn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
        if (-not $btn) { Check $id "引导按钮「$label」定位失败" $false $strict; return $false }
        try { ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
        catch {
            try { ($btn.GetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern)).DoDefaultAction() }
            catch { Check $id "引导按钮「$label」不可调用: $_" $false $strict; return $false }
        }
        return $true
    } catch { Check $id "引导按钮「$label」异常: $_" $false $strict; return $false }
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

# ---- 启动壳层（v2：干净环境先走引导全流程，再进主流程） ----
Log '== 启动壳层（引导全流程段） =='
$settingsDir = "$env:LOCALAPPDATA\TaoSearch"
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
Remove-Item "$settingsDir\settings.json" -Force -ErrorAction SilentlyContinue  # 干净环境：引导窗必须出现
$appLog = "$EvidenceDir\app.log"
$app = Start-Process -FilePath "$PublishDir\TaoSearch.App.exe" -WorkingDirectory $PublishDir -PassThru `
    -RedirectStandardOutput "$EvidenceDir\app.out.log" -RedirectStandardError $appLog
Start-Sleep -Seconds 6
if ($app.HasExited) { Log "  壳层退出码: $($app.ExitCode)"; if (Test-Path $appLog) { Get-Content $appLog | ForEach-Object { Log "  app| $_" } } }
Check 'G2-00b' '壳层进程存活（启动 6 秒未退出）' (-not $app.HasExited) $true


# ---- 引导全流程（G2-11~G2-15，2026-11-22 全面覆盖令新增） ----
$root = [System.Windows.Automation.AutomationElement]::RootElement
$ob = $null
foreach ($i in 1..15) {
    try {
        $obCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch 首次使用引导')
        $ob = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $obCond)
        if ($ob) { break }
    } catch { }
    Start-Sleep -Seconds 2
}
Check 'G2-11' '首启引导窗出现（干净环境）' ($null -ne $ob) $true
if ($ob) {
    Screenshot 'G2-11-onboarding-step1'
    $ok1 = Invoke-UiaButton $ob '下一步' 'G2-12' $true
    Check 'G2-12' '引导第 1 步「下一步」可点通' $ok1 $true
    if ($ok1) {
        Start-Sleep -Seconds 1
        Screenshot 'G2-12-onboarding-step2'
        $ok2 = Invoke-UiaButton $ob '开始建立索引' 'G2-13' $true
        Check 'G2-13' '引导第 2 步「开始建立索引」可点通' $ok2 $true
        if ($ok2) {
            Start-Sleep -Seconds 2
            Screenshot 'G2-13-onboarding-step3'
            $done = $false
            foreach ($i in 1..90) {  # 180 秒：小卷应秒级就绪，留足余量
                Start-Sleep -Seconds 2
                try {
                    $finCond = New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, '完成，进入 TaoSearch')
                    $fin = $ob.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $finCond)
                    if ($fin -and -not $fin.Current.IsOffscreen) { $done = $true; break }
                } catch { }
            }
            try {
                $pc = $ob.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ProgressCount')))
                if ($pc) { Log "  引导进度文案: $($pc.Current.Name)" }
            } catch { }
            Check 'G2-14' '引导第 3 步索引就绪出「完成」按钮（180 秒内）' $done $true
            Screenshot 'G2-14-onboarding-done'
            if ($done) {
                $ok3 = Invoke-UiaButton $ob '完成，进入 TaoSearch' 'G2-15' $true
                Start-Sleep -Seconds 2
                Check 'G2-15' '引导「完成」进入主窗口' $ok3 $true
                Screenshot 'G2-15-main-after-onboarding'
            }
        }
    }
}

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
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
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

    # ---- 热键开关实际切换（G2-16，全面覆盖令：按 AutomationId 定位开关本体，模式降级链切换 + 设置文件回读） ----
    try {
        $hk = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'HotkeyToggle')))
        if ($hk) {
            function Read-Qh {
                try { return [bool]((Get-Content "$settingsDir\settings.json" -Raw | ConvertFrom-Json).QuickHotkeyEnabled) } catch { return $null }
            }
            function Flip-Toggle($el) {
                try { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return $true } catch { }
                try { $el.GetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern).DoDefaultAction(); return $true } catch { }
                try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
                return $false
            }
            $qh0 = Read-Qh
            $f1 = Flip-Toggle $hk
            Start-Sleep -Milliseconds 900
            $qh1 = Read-Qh
            $f2 = Flip-Toggle $hk
            Start-Sleep -Milliseconds 900
            $qh2 = Read-Qh
            # 断言以设置文件回读为准（UIA Toggle 方法可能报错但实际已生效，G2-16 第三轮实证）
            Check 'G2-16' "热键开关切换生效（$qh0→$qh1→$qh2）" (($qh0 -ne $qh1) -and ($qh1 -ne $qh2) -and ($qh0 -eq $qh2)) $true
            Screenshot 'G2-16-hotkey-toggle'
        } else { Check 'G2-16' '未定位到热键开关控件' $false $true }
    } catch { Check 'G2-16' "热键开关异常: $_" $false $true }

    # ---- 热键状态提示文案（G2-16b，Alt+Q 失效显式反馈） ----
    try {
        $note = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'HotkeyNote')))
        $noteText = if ($note) { $note.Current.Name } else { '' }
        Log "  热键提示文案: $noteText"
        Check 'G2-16b' '热键状态提示文案存在（Alt+Q 失效显式反馈）' ($noteText.Length -gt 0) $false
    } catch { Log "  热键提示读取异常: $_" }

    # ---- 托盘图标探针（G2-17，先求证：UIA 枚举任务栏通知区，首场定级） ----
    try {
        $trayFound = $false
        $taskbar = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')))
        if ($taskbar) {
            # 收窄到通知区 TrayNotifyWnd，排除主窗任务栏按钮误匹配（v3 修正）
            $notify = $taskbar.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'TrayNotifyWnd')))
            $scopeEl = if ($notify) { $notify } else { $taskbar }
            if (-not $notify) { Log '  未定位到 TrayNotifyWnd，退回全任务栏搜索' }
            $btnCond2 = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)
            $btns = $scopeEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond2)
            foreach ($b in $btns) {
                if ("$($b.Current.Name)" -like '*TaoSearch*') { $trayFound = $true; break }
            }
            if (-not $trayFound) {
                $tnames = @($btns | ForEach-Object { $_.Current.Name }) | Where-Object { $_ }
                Log "  任务栏可见按钮: $($tnames -join ' | ')"
            }
        } else { Log '  未找到任务栏窗口 Shell_TrayWnd' }
        Check 'G2-17' '托盘图标在通知区可见（UIA）' $trayFound $false
        Screenshot 'G2-17-tray'
    } catch { Check 'G2-17' "托盘枚举异常: $_" $false $false }

    # ---- 开机自启开关试切（G2-18，MSIX StartupTask：非打包环境试切留痕，生效性真机项） ----
    try {
        $st = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'StartupToggle')))
        if ($st) {
            try {
                ($st.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
                Start-Sleep -Milliseconds 800
                Check 'G2-18' '自启开关可操作（MSIX StartupTask 生效性真机项）' $true $false
            } catch { Check 'G2-18' "自启开关切换受限: $_" $false $false }
        } else { Check 'G2-18' '未定位到自启开关' $false $false }
    } catch { Log "  自启开关异常: $_" }

    # ---- 托盘命令链路（G2-20/G2-21，老板令：托盘交互与视觉上云）----
    # 链路：关主窗进托盘（进程不退）→ 托盘隐藏窗收右键回调 → WM_COMMAND「打开主窗」→ 主窗复现
    Log '== 托盘命令链路 =='
    try {
        if (-not ('G2Tray.Native' -as [type])) {
            Add-Type -Namespace G2Tray -Name Native -MemberDefinition '
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder sb, int max);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
public static System.Collections.Generic.List<string> ListWindows(uint pid) {
    var list = new System.Collections.Generic.List<string>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
        var sb = new System.Text.StringBuilder(256);
        GetClassNameW(h, sb, 256);
        uint p; GetWindowThreadProcessId(h, out p);
        if (p == pid) list.Add(sb.ToString() + "|" + h.ToString());
        return true;
    }, IntPtr.Zero);
    return list;
}'
        }
        $wins = [G2Tray.Native]::ListWindows([uint32]$app.Id)
        Log ("  壳层顶层窗口类: " + ($wins -join ' , '))
        $trayHwnd = [IntPtr]::Zero
        foreach ($w in $wins) {
            if ($w.StartsWith('TaoSearchTrayWnd|')) { $trayHwnd = [IntPtr][long]$w.Split('|')[1] }
        }
        Check 'G2-20' '托盘隐藏消息窗存在（TaoSearchTrayWnd）' ($trayHwnd -ne [IntPtr]::Zero) $true
        if ($trayHwnd -ne [IntPtr]::Zero) {
            # 第一步：关主窗 → 最小化进托盘（进程必须存活）
            try { $app.CloseMainWindow() | Out-Null } catch { }
            Start-Sleep -Seconds 3
            $app.Refresh()
            $hiddenOk = (-not $app.HasExited) -and ($app.MainWindowHandle -eq [IntPtr]::Zero)
            Check 'G2-20b' '关主窗后进程存活且主窗隐藏（进托盘）' $hiddenOk $true
            # 第二步：右键回调 + 托盘「打开主窗」命令 → 主窗复现
            [G2Tray.Native]::PostMessageW($trayHwnd, 0x5554, [IntPtr]::Zero, [IntPtr]0x0205) | Out-Null
            Start-Sleep -Milliseconds 1200
            [G2Tray.Native]::PostMessageW($trayHwnd, 0x0111, [IntPtr]1, [IntPtr]::Zero) | Out-Null
            $restored = $false
            foreach ($i in 1..10) {
                Start-Sleep -Seconds 2
                try {
                    $w2 = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch')))
                    if ($w2) { $restored = $true; break }
                } catch { }
            }
            if (-not $restored) {
                # 菜单可能模态停留：先 ESC 关菜单再补发一次命令
                [G2Tray.Native]::PostMessageW($trayHwnd, 0x0100, [IntPtr]0x1B, [IntPtr]::Zero) | Out-Null
                [G2Tray.Native]::PostMessageW($trayHwnd, 0x0101, [IntPtr]0x1B, [IntPtr]::Zero) | Out-Null
                Start-Sleep -Milliseconds 800
                [G2Tray.Native]::PostMessageW($trayHwnd, 0x0111, [IntPtr]1, [IntPtr]::Zero) | Out-Null
                foreach ($i in 1..10) {
                    Start-Sleep -Seconds 2
                    try {
                        $w2 = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
                            (New-Object System.Windows.Automation.PropertyCondition(
                                [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch')))
                        if ($w2) { $restored = $true; break }
                    } catch { }
                }
            }
            Check 'G2-20c' '托盘「打开主窗」命令后主窗复现' $restored $true
            Screenshot 'G2-20-tray-restore'
            # 第三步：托盘「快速搜索」→ 快速搜索窗出现（视觉证据）
            [G2Tray.Native]::PostMessageW($trayHwnd, 0x0111, [IntPtr]2, [IntPtr]::Zero) | Out-Null
            $quick = $null
            foreach ($i in 1..10) {
                Start-Sleep -Seconds 2
                try {
                    $quick = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch 快速搜索')))
                    if ($quick) { break }
                } catch { }
            }
            Check 'G2-21' '托盘「快速搜索」命令拉起快速搜索窗' ($null -ne $quick) $true
            if ($quick) { Screenshot 'G2-21-quick-window' }
        }
    } catch { Check 'G2-20' "托盘命令链路异常: $_" $false $true }

    # ---- 内容页签全文搜索（G2-22，全面覆盖令：核心差异点 UI 链路）----
    Log '== 内容页签全文搜索 =='
    try {
        $tabOk = $false
        try {
            $tabC = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '内容')))
            if ($tabC) {
                try { ($tabC.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle() | Out-Null; $tabOk = $true }
                catch { try { ($tabC.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); $tabOk = $true } catch { Log "  内容页签切换模式不支持: $_" } }
            }
        } catch { Log "  内容页签定位异常: $_" }
        Check 'G2-22a' '切换到内容页签' $tabOk $true
        if ($tabOk) {
            Start-Sleep -Seconds 1
            $chipOk = $false
            try {
                $chip = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, '全部')))
                $chipOk = ($null -ne $chip)
            } catch { }
            Check 'G2-22b' '内容页签筛选 chips 出现' $chipOk $true
            $contentHit = $false
            try {
                $editC = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                        [System.Windows.Automation.ControlType]::Edit)))
                if ($editC) {
                    ($editC.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('smoke')
                    foreach ($i in 1..15) {
                        Start-Sleep -Seconds 2
                        $items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                            (New-Object System.Windows.Automation.PropertyCondition(
                                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                                [System.Windows.Automation.ControlType]::ListItem)))
                        if ($items.Count -gt 0) { $contentHit = $true; break }
                    }
                }
            } catch { Log "  内容搜索异常: $_" }
            Check 'G2-22c' '全文关键词 smoke 命中结果列表' $contentHit $true
            Screenshot 'G2-22-content-search'
        }
    } catch { Check 'G2-22' "内容页签异常: $_" $false $true }

    # ---- 快速窗输入即搜（G2-23，全面覆盖令）----
    Log '== 快速窗输入即搜 =='
    try {
        $quickHit = $false
        if ($quick) {
            try {
                $qEdit = $quick.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                        [System.Windows.Automation.ControlType]::Edit)))
                if ($qEdit) {
                    ($qEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('readme')
                    foreach ($i in 1..15) {
                        Start-Sleep -Seconds 2
                        $qItems = $quick.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                            (New-Object System.Windows.Automation.PropertyCondition(
                                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                                [System.Windows.Automation.ControlType]::ListItem)))
                        if ($qItems.Count -gt 0) { $quickHit = $true; break }
                    }
                }
            } catch { Log "  快速窗搜索异常: $_" }
        }
        Check 'G2-23' '快速窗输入 readme 出结果' $quickHit $true
        if ($quick) { Screenshot 'G2-23-quick-search' }
    } catch { Check 'G2-23' "快速窗异常: $_" $false $true }

    # ---- 设置页操作按钮群（G2-27，全面覆盖令）----
    Log '== 设置页操作按钮群 =='
    try {
        # 快速窗若仍开着会盖住主窗中央，滚轮/点击会落到快速窗上（截图实证）：先关掉
        try {
            $qw = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch 快速搜索')))
            if ($qw) { ($qw.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close(); Log '  已关快速窗（避免遮挡主窗滚轮）'; Start-Sleep -Seconds 1 }
        } catch { Log "  关快速窗失败（继续）: $_" }
        $nav2 = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, '设置')))
        if ($nav2) { try { ($nav2.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() } catch { try { ($nav2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch { } } }
        Start-Sleep -Seconds 2
        $pauseOk = $false
        try {
            $pb = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '暂停索引')))
            if ($pb) {
                ($pb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                Start-Sleep -Seconds 2
                Close-UiaDialog $win
                $rb = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, '恢复索引')))
                if ($rb) {
                    ($rb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                    Start-Sleep -Seconds 2
                    Close-UiaDialog $win
                    $pb2 = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::NameProperty, '暂停索引')))
                    $pauseOk = ($null -ne $pb2)
                }
            }
        } catch { Log "  暂停恢复异常: $_" }
        Check 'G2-27a' '暂停/恢复索引切换生效（文案往返）' $pauseOk $true
        $themeOk = $false
        try {
            # 主题卡在首屏外（截图实证）：先在设置页区域滚轮下滚数格，再定位单选
            Scroll-WheelOver $win 6 | Out-Null
            Start-Sleep -Milliseconds 500
            $rbCond = New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '墨白（深色）')),
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::RadioButton)))
            $mobai = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rbCond)
            if (-not $mobai) { Scroll-WheelOver $win 6 | Out-Null; Start-Sleep -Milliseconds 500; $mobai = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rbCond) }
            if ($mobai) {
                # 主题卡在首屏外（G2-27 截图实证）：先滚动入视再选
                try { ($mobai.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)).ScrollIntoView(); Start-Sleep -Milliseconds 800 } catch { }
                try { ($mobai.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
                catch { if (-not (Click-UiaElement $mobai)) { throw "墨白单选不可选: $_" } }
                Start-Sleep -Seconds 2
                Screenshot 'G2-27-theme-dark'
                Scroll-WheelOver $win 3 | Out-Null
                Start-Sleep -Milliseconds 400
                $rbCond2 = New-Object System.Windows.Automation.AndCondition(
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, '水墨（浅色）')),
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                        [System.Windows.Automation.ControlType]::RadioButton)))
                $shuimo = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rbCond2)
                if ($shuimo) {
                    try { ($shuimo.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)).ScrollIntoView(); Start-Sleep -Milliseconds 800 } catch { }
                    try { ($shuimo.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
                    catch { if (-not (Click-UiaElement $shuimo)) { throw "水墨单选不可选: $_" } }
                    Start-Sleep -Seconds 1
                }
                $themeOk = $true
            }
        } catch { Log "  主题切换异常: $_" }
        Check 'G2-27b' '主题切换可操作（深色截图留证）' $themeOk $true
        $locOk = $false
        try {
            $locBtn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '更改位置…')))
            $locOk = ($null -ne $locBtn)
        } catch { }
        Check 'G2-27c' '索引库位置按钮存在' $locOk $true
        $rebuildOk = $false
        try {
            $rbtn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '重建索引')))
            if ($rbtn) {
                ($rbtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                Start-Sleep -Seconds 2
                Close-UiaDialog $win
                foreach ($i in 1..30) {
                    Start-Sleep -Seconds 2
                    $texts2 = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                            [System.Windows.Automation.ControlType]::Text)))
                    $joined = (@($texts2 | ForEach-Object { $_.Current.Name }) -join ' ')
                    if ($joined -match '就绪') { $rebuildOk = $true; break }
                }
            }
        } catch { Log "  重建索引异常: $_" }
        Check 'G2-27d' '重建索引后状态回就绪' $rebuildOk $true
        Screenshot 'G2-27-settings-actions'
    } catch { Check 'G2-27' "设置页按钮群异常: $_" $false $true }

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

# ---- 引导「稍后再说」跳过路径（G2-19，全面覆盖令：第二出口） ----
Log '== 引导跳过路径 =='
try { $app.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $app.HasExited) { $app.Kill() } } catch { }
Start-Sleep -Seconds 2
Remove-Item "$settingsDir\settings.json" -Force -ErrorAction SilentlyContinue
$app2 = Start-Process -FilePath "$PublishDir\TaoSearch.App.exe" -WorkingDirectory $PublishDir -PassThru `
    -RedirectStandardOutput "$EvidenceDir\app2.out.log" -RedirectStandardError "$EvidenceDir\app2.log"
$skipOk = $false
foreach ($i in 1..15) {
    Start-Sleep -Seconds 2
    try {
        $ob2 = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch 首次使用引导')))
        if ($ob2) {
            if (Invoke-UiaButton $ob2 '稍后再说' 'G2-19a' $true) {
                Start-Sleep -Seconds 3
                $app2.Refresh()
                $skipOk = ($app2.MainWindowHandle -ne [IntPtr]::Zero)
            }
            break
        }
    } catch { }
}
Check 'G2-19' '引导「稍后再说」跳过直达主窗口' $skipOk $true
Screenshot 'G2-19-skip-main'

# ---- 单实例转发（G2-26，全面覆盖令）----
Log '== 单实例转发 =='
try {
    $app3 = Start-Process -FilePath "$PublishDir\TaoSearch.App.exe" -ArgumentList '--search','readme' -WorkingDirectory $PublishDir -PassThru `
        -RedirectStandardOutput "$EvidenceDir\app3.out.log" -RedirectStandardError "$EvidenceDir\app3.log"
    Start-Sleep -Seconds 6
    $app3.Refresh()
    Check 'G2-26a' '第二启动实例转发后自行退出' $app3.HasExited $true
    $app2.Refresh()
    Check 'G2-26b' '原实例仍存活（未退出）' (-not $app2.HasExited) $true
    $val2 = ''
    $fwd = $false
    try {
        $win2 = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch')))
        Log "  转发回读: 主窗$(if ($win2) { '已找到' } else { '未找到' })"
        if ($win2) {
            # G2-27 停在设置页：先切回「搜索」页，保证读到的是搜索框
            $navHome = $win2.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '搜索')))
            if ($navHome) {
                try { ($navHome.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
                catch { try { ($navHome.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch { } }
                Start-Sleep -Seconds 1
            }
            $resultsHit = $false
            # 轻量轮询：每轮只按 AutomationId 精读 SearchBox；整树 FindAll 改每 5 轮一次。
            # 重轮询会持续占用应用 UI 线程，转发回调被饿到轮询结束后才执行（三轮截图均在停止后 1 秒内出现内容）。
            foreach ($i in 1..60) {
                try {
                    $sbCond = New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SearchBox')
                    $sb = $win2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $sbCond)
                    if ($sb) {
                        $val2 = ($sb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Value
                        if ($i -le 5 -or $i % 10 -eq 0) { Log ("  转发回读 第{0}轮 SearchBox={1}" -f $i, $val2) }
                    }
                } catch { }
                if ($i % 5 -eq 0) {
                    # 断言主体：结果列表出现即转发链路真实生效；框回读为辅
                    try {
                        $rItems = $win2.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                            (New-Object System.Windows.Automation.PropertyCondition(
                                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                                [System.Windows.Automation.ControlType]::ListItem)))
                        if ($rItems.Count -gt 0) { $resultsHit = $true; Log ("  转发回读 第{0}轮 结果列表出现（{1} 项）" -f $i, $rItems.Count) }
                    } catch { }
                }
                if ($resultsHit -or ($val2 -eq 'readme')) { break }
                Start-Sleep -Seconds 1
            }
            Log ("  转发回读: launch-args 文件存在=" + (Test-Path "$env:TEMP\TaoSearch\launch-args.json"))
            $fwd = ($val2 -eq 'readme') -or $resultsHit
            Screenshot 'G2-26-forward-readback'
        }
    } catch { Log "  转发回读异常: $_" }
    Check 'G2-26c' "转发生效（框回读=$val2，结果命中=$resultsHit）" $fwd $true
} catch { Check 'G2-26' "单实例转发异常: $_" $false $true }

# ---- Alt+Q 真实按键（G2-25，全面覆盖令：全局热键实测）----
Log '== Alt+Q 真实按键 =='
try {
    # 快速窗若仍开着（G2-23 遗留），Alt+Q 是开关切换：先关再开，验证完整切换语义
    $pre = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch 快速搜索')))
    if ($pre) {
        [System.Windows.Forms.SendKeys]::SendWait('%q')
        Start-Sleep -Seconds 2
    }
    [System.Windows.Forms.SendKeys]::SendWait('%q')
    $quickHot = $null
    foreach ($i in 1..10) {
        Start-Sleep -Seconds 1
        try {
            $quickHot = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch 快速搜索')))
            if ($quickHot) { break }
        } catch { }
    }
    Check 'G2-25' 'Alt+Q 真实按键唤起快速搜索窗' ($null -ne $quickHot) $true
    Screenshot 'G2-25-hotkey-quick'
    if (-not $quickHot) { Log "  Alt+Q 诊断: 预检快速窗$(if ($pre) { '开着' } else { '已关' })，按键后 10 秒未发现快速窗" }
} catch { Check 'G2-25' "Alt+Q 按键异常: $_" $false $true }

# ---- 托盘「退出」命令（G2-24，全面覆盖令：退出链路实测）----
Log '== 托盘退出 =='
try {
    if (-not ('G2Tray.Native' -as [type])) {
        Add-Type -Namespace G2Tray -Name Native -MemberDefinition '
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder sb, int max);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
public static System.Collections.Generic.List<string> ListWindows(uint pid) {
    var list = new System.Collections.Generic.List<string>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
        var sb = new System.Text.StringBuilder(256);
        GetClassNameW(h, sb, 256);
        uint p; GetWindowThreadProcessId(h, out p);
        if (p == pid) list.Add(sb.ToString() + "|" + h.ToString());
        return true;
    }, IntPtr.Zero);
    return list;
}'
    }
    $wins2 = [G2Tray.Native]::ListWindows([uint32]$app2.Id)
    $tray2 = [IntPtr]::Zero
    foreach ($w in $wins2) { if ($w.StartsWith('TaoSearchTrayWnd|')) { $tray2 = [IntPtr][long]$w.Split('|')[1] } }
    Check 'G2-24a' '第二实例托盘隐藏窗存在' ($tray2 -ne [IntPtr]::Zero) $true
    if ($tray2 -ne [IntPtr]::Zero) {
        [G2Tray.Native]::PostMessageW($tray2, 0x0111, [IntPtr]4, [IntPtr]::Zero) | Out-Null
        $exited = $false
        foreach ($i in 1..15) {
            Start-Sleep -Seconds 1
            $app2.Refresh()
            if ($app2.HasExited) { $exited = $true; break }
        }
        Check 'G2-24' '托盘「退出」命令后进程结束' $exited $true
    }
} catch { Check 'G2-24' "托盘退出异常: $_" $false $true }
Screenshot 'G2-19-skip-main'
try { $app2.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $app2.HasExited) { $app2.Kill() } } catch { }

# ---- 崩溃取证（0.1.0.9）：壳层若崩溃，从事件日志与 crash.log 取堆栈 ----
Log '== 崩溃取证 =='
try {
    $since = (Get-Date).AddMinutes(-20)
    $evts = Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=$since } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in '.NET Runtime','Application Error','Windows Error Reporting' } |
        Select-Object -First 8
    foreach ($evt in $evts) {
        Log "  evt| $($evt.TimeCreated) [$($evt.ProviderName)]"
        ($evt.Message -split "`n" | Select-Object -First 25) | ForEach-Object { Log "  evt| $_" }
    }
    if (-not $evts) { Log '  evt| 近 20 分钟无 .NET Runtime / Application Error / WER 事件' }
} catch { Log "  事件日志读取失败: $_" }
$crashLog = "$settingsDir\crash.log"
if (Test-Path $crashLog) {
    Log '== 壳层 crash.log =='
    Get-Content $crashLog | ForEach-Object { Log "  crash| $_" }
    Copy-Item $crashLog "$EvidenceDir\crash.log" -Force
} else {
    Log '  crash| 无 crash.log（壳层未产生托管未处理异常记录）'
}
try { $app.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $app.HasExited) { $app.Kill() } } catch { }
try { if (-not $svc.HasExited) { $svc.Kill() } } catch { }

$results | Set-Content "$EvidenceDir\gate2-results.txt"
Log "== 关2 结果: 严格失败 $strictFails 项 =="
exit $(if ($strictFails -gt 0) { 1 } else { 0 })
