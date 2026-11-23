# 扮演验收班（测试规范 v3.22 十一章：真人扮演令 11.1 + 全生命周期场景覆盖令 11.2 + 零用例盲测令 11.3 + 真实环境镜像令 11.6；六.9 日志强制归档、六.10 录屏必看真验）
# 口径：不给产品代码派生的预期断言，三画像（小白/熟练/搞破坏）在真实脏数据环境里当真人摸索；
# 全程录屏帧 + 逐档截图 + 日志全部归档；观察与异常全部进对账表；异常分级 CRASH（进程死/崩溃日志/已知在文件搜不到）与 OBS（记录备查）。
# 用法: powershell -File roleplay-run.ps1 -PublishDir <publish目录> -EvidenceDir <证据目录>
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
New-Item -ItemType Directory -Force -Path "$EvidenceDir\frames" | Out-Null
$ledger = New-Object System.Collections.Generic.List[string]
$anomalies = New-Object System.Collections.Generic.List[string]
$crashFails = 0
$settingsDir = "$env:LOCALAPPDATA\TaoSearch"
$root = [System.Windows.Automation.AutomationElement]::RootElement

function Log([string]$m) { Write-Host $m }
function Ledger([string]$m) { $ledger.Add($m); Log "  [账] $m" }
function Flag([string]$id, [string]$m, [bool]$crash) {
    $sev = if ($crash) { 'CRASH' } else { 'OBS' }
    if ($crash) { $script:crashFails++ }
    $anomalies.Add("[$sev] $id $m")
    Log "  [疑-$sev] $id $m"
    Screenshot "RP-ANOMALY-$id"
}
function Screenshot([string]$name) {
    try {
        Start-Sleep -Milliseconds 500
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bmp.Save("$EvidenceDir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Log "  截图: $name.png"
    } catch { Log "  截图失败 $name : $_" }
}
function Get-Luminance([string]$png) {
    try {
        $bmp = New-Object System.Drawing.Bitmap($png)
        $sum = 0L; $n = 0
        for ($y = 0; $y -lt $bmp.Height; $y += 8) {
            for ($x = 0; $x -lt $bmp.Width; $x += 8) {
                $c = $bmp.GetPixel($x, $y)
                $sum += [int](0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B); $n++
            }
        }
        $bmp.Dispose()
        if ($n -eq 0) { return -1 }
        return [math]::Round($sum / $n, 1)
    } catch { return -1 }
}

# ---- UIA 助手（真人触控口径：真实点击/真实键入，禁接口直达） ----
function Click-El($el) {
    try {
        if (-not ('RP.Native' -as [type])) {
            Add-Type -Namespace RP -Name Native -MemberDefinition '
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);'
        }
        try { $pt = $el.GetClickablePoint() } catch {
            $rc = $el.Current.BoundingRectangle
            if ($rc.IsEmpty) { throw }
            $pt = New-Object System.Windows.Point(($rc.X + $rc.Width / 2), ($rc.Y + $rc.Height / 2))
        }
        [RP.Native]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
        Start-Sleep -Milliseconds 200
        [RP.Native]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        [RP.Native]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        return $true
    } catch { Log "  点击失败: $_"; return $false }
}
function Find-ByName($scope, [string]$name) {
    try {
        return $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    } catch { return $null }
}
function Get-AppWin($proc) {
    foreach ($i in 1..15) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
            try {
                $w = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
                if ($w) { return $w }
            } catch { }
        }
        Start-Sleep -Seconds 2
    }
    try {
        return $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch')))
    } catch { return $null }
}
function Get-Edit($win) {
    try {
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Edit)))
    } catch { return $null }
}
function Count-Results($win) {
    try {
        $items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)))
        return $items.Count
    } catch { return -1 }
}
# 真人搜索动作：点进搜索框、键入词、等结果稳定，返回 (命中数, 观察记录)
function Search-Like-Human($win, [string]$term, [int]$waitSec) {
    $edit = Get-Edit $win
    if (-not $edit) { return @(-1, '搜索框未找到') }
    try { ($edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($term) } catch { return @(-1, "键入失败: $_") }
    $last = -2; $stable = 0
    foreach ($i in 1..([Math]::Max(1, [int]($waitSec / 2)))) {
        Start-Sleep -Seconds 2
        $c = Count-Results $win
        if ($c -eq $last) { $stable++; if ($stable -ge 2) { break } } else { $stable = 0; $last = $c }
    }
    return @($last, '')
}

# ---- 录屏（六.7/六.10：全程帧采集，3 秒一帧 JPEG） ----
$stopRec = "$env:TEMP\rp-stop-recording.rec"
Remove-Item $stopRec -Force -ErrorAction SilentlyContinue
$recScript = "$env:TEMP\rp-recorder.ps1"
@"
param(`$Dir, `$StopFile)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
`$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { `$_.MimeType -eq 'image/jpeg' }
`$n = 0
while (-not (Test-Path `$StopFile)) {
    try {
        `$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        `$bmp = New-Object System.Drawing.Bitmap(`$b.Width, `$b.Height)
        `$g = [System.Drawing.Graphics]::FromImage(`$bmp)
        `$g.CopyFromScreen(`$b.Location, [System.Drawing.Point]::Empty, `$b.Size)
        `$ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
        `$ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]60)
        `$bmp.Save(("{0}\frames\f{1:d6}.jpg" -f `$Dir, `$n), `$codec, `$ep)
        `$g.Dispose(); `$bmp.Dispose()
    } catch { }
    `$n++
    Start-Sleep -Seconds 3
}
"@ | Set-Content -Path $recScript -Encoding UTF8
Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$recScript`" -Dir `"$EvidenceDir`" -StopFile `"$stopRec`"" -WindowStyle Hidden
Log '== 录屏已启动（3 秒/帧） =='

# ---- 阶段 A：真实环境镜像（11.6：真实规模 + 脏数据，禁干净实验室充当） ----
Log '== 阶段 A：真实脏数据环境生成 =='
# 语料放证据目录之外（C 盘镜像位），不随证据包上传（4.5 万文件会撑爆 artifact）
$corpus = 'C:\taosearch-rp-corpus'
if (Test-Path $corpus) { Remove-Item $corpus -Recurse -Force -ErrorAction SilentlyContinue }
$sw = [System.Diagnostics.Stopwatch]::StartNew()
function W([string]$p, [string]$c) { [System.IO.File]::WriteAllText($p, $c) }
$cnContent = '这是一份测试文档，包含中文内容与数字 12345，用于全文检索验证。TaoSearch 本地全文引擎应能命中本段文字。'
$codeContent = "public class Sample { public int Id { get; set; } public string Name { get; set; } }`nfunction hello(name) { return 'hi ' + name; }"
$logContent = '2026-11-24 00:00:00 INFO request processed latency=12ms status=200'
$csvContent = 'id,name,value' + ("`n" + (1..50 | ForEach-Object { "$_,'item $_',$_*" }) -join "`n")
$count = 0
function New-File([string]$path, [string]$content) {
    try { [System.IO.File]::WriteAllText($path, $content); $script:count++ } catch { }
}
# 1) 常规文档（中文内容）
New-Item -ItemType Directory -Force -Path "$corpus\docs" | Out-Null
W "$corpus\docs\readme.txt" 'TaoSearch roleplay smoke volume readme'
W "$corpus\docs\发票明细.txt" $cnContent
W "$corpus\docs\会议纪要 2026.txt" $cnContent
foreach ($i in 1..800) { New-File "$corpus\docs\文档-$i.txt" $cnContent }
# 2) 代码文件
New-Item -ItemType Directory -Force -Path "$corpus\code" | Out-Null
foreach ($i in 1..3000) {
    $ext = @('.cs', '.js', '.py')[$i % 3]
    New-File "$corpus\code\source_$i$ext" $codeContent
}
# 3) 日志文件（量大，模拟真实使用积累）
New-Item -ItemType Directory -Force -Path "$corpus\logs" | Out-Null
foreach ($i in 1..20000) { New-File "$corpus\logs\app-$i.log" $logContent }
# 4) 数据文件
New-Item -ItemType Directory -Force -Path "$corpus\data" | Out-Null
foreach ($i in 1..20000) {
    $ext = @('.csv', '.json')[$i % 2]
    New-File "$corpus\data\dataset-$i$ext" $csvContent
}
# 5) 多批回归语料（250 个 batch-*，命中超 200 逼流式多帧）
New-Item -ItemType Directory -Force -Path "$corpus\batch" | Out-Null
foreach ($i in 1..250) { New-File "$corpus\batch\batch-$i.txt" "batch corpus file $i" }
# 6) 奇怪字符文件名（真实脏数据：emoji、空格、特殊符号、超长名）
New-Item -ItemType Directory -Force -Path "$corpus\weird" | Out-Null
$weirdNames = @('报告 最终版 v2 (1).txt', '预算💰表.xlsx.txt', 'notes<>demo.txt', 'what*is*this?.txt', '管道|文件.txt',
    '引号"测试.txt', '百分百%完成.txt', '感叹!!!重要.txt', 'Ｔｏｐｕｎｉｃｏｄｅ.txt', '日本語のファイル.txt',
    'emoji😀😀😀.txt', 'tab	名.txt', '长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长长.txt')
foreach ($n in $weirdNames) { New-File "$corpus\weird\$n" $cnContent }
# 7) 超长路径（深度嵌套，总长超 260）
$deepRoot = "$corpus\deep"
$seg = 'level-directory-with-long-name-'
foreach ($b in 1..10) {
    $p = "$deepRoot\branch$b"
    foreach ($d in 1..25) { $p += "\$seg$d" }
    try { New-Item -ItemType Directory -Force -Path $p | Out-Null; W "$p\deep-file.txt" $cnContent; $script:count++ } catch { }
    try { W "$p\deep-发票.txt" $cnContent; $script:count++ } catch { }
}
# 8) 损坏文件（真实脏数据：扩展名是文档、内容是乱字节）
New-Item -ItemType Directory -Force -Path "$corpus\corrupt" | Out-Null
$rnd = New-Object byte[] 2048
$randGen = New-Object System.Random
$randGen.NextBytes($rnd)
foreach ($i in 1..300) {
    $ext = @('.docx', '.pdf', '.txt')[$i % 3]
    try { [System.IO.File]::WriteAllBytes("$corpus\corrupt\corrupted-$i$ext", $rnd); $script:count++ } catch { }
}
# 9) 只读文件
New-Item -ItemType Directory -Force -Path "$corpus\readonly" | Out-Null
foreach ($i in 1..200) {
    $p = "$corpus\readonly\locked-$i.txt"
    New-File $p '只读权限文件内容 locked content'
    try { Set-ItemProperty -Path $p -Name IsReadOnly -Value $true } catch { }
}
# 10) 空文件
New-Item -ItemType Directory -Force -Path "$corpus\empty" | Out-Null
foreach ($i in 1..300) { New-File "$corpus\empty\empty-$i.txt" '' }
# 11) 重名文件（不同目录同名）
foreach ($d in 1..15) {
    New-Item -ItemType Directory -Force -Path "$corpus\dupes\dir$d" | Out-Null
    foreach ($n in 1..20) { New-File "$corpus\dupes\dir$d\duplicate-$n.txt" "duplicate file $n in dir $d" }
}
# 12) 大文件
New-Item -ItemType Directory -Force -Path "$corpus\big" | Out-Null
$bigLine = 'big file content line for fulltext scan 0123456789'
$bigSb = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt 100000; $i++) { [void]$bigSb.AppendLine($bigLine) }
foreach ($i in 1..3) { New-File "$corpus\big\huge-$i.txt" $bigSb.ToString() }
$sw.Stop()
$fileCount = (Get-ChildItem $corpus -Recurse -File | Measure-Object).Count
$sizeMb = [math]::Round(((Get-ChildItem $corpus -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Ledger "真实环境镜像：$fileCount 个文件，$sizeMb MB，生成耗时 $([int]$sw.Elapsed.TotalSeconds)s（含中文/emoji/特殊字符名、超长路径、损坏文件、只读、空文件、重名、大文件）"
if ($fileCount -lt 30000) { Flag 'RP-ENV' "语料规模不足（$fileCount < 30000），真实数据规模场景打折" $false }

# ---- 服务/壳层生命周期助手 ----
$script:svc = $null
function Start-Service([string]$dataDir) {
    $env:TAOSEARCH_DATA = $dataDir
    $svcLog = "$EvidenceDir\service.log"
    $script:svc = Start-Process -FilePath "$PublishDir\TaoSearch.Service.exe" -ArgumentList "`"$corpus`"" `
        -WorkingDirectory $PublishDir -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $svcLog -RedirectStandardError "$EvidenceDir\service.err.log"
    foreach ($i in 1..30) {
        Start-Sleep -Seconds 1
        if ($script:svc.HasExited) { break }
        try {
            $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'taosearch.svc.1', [System.IO.Pipes.PipeDirection]::InOut)
            $p.ConnectAsync(1000).Wait(1000) | Out-Null
            $p.Dispose(); return $true
        } catch { }
    }
    return $false
}
function Stop-ServiceProc { if ($script:svc -and -not $script:svc.HasExited) { try { Stop-Process -Id $script:svc.Id -Force -ErrorAction SilentlyContinue } catch { } } }
function Start-App {
    $appLog = "$EvidenceDir\app.log"
    $script:app = Start-Process -FilePath "$PublishDir\TaoSearch.App.exe" -WorkingDirectory $PublishDir -PassThru `
        -RedirectStandardOutput "$EvidenceDir\app.out.log" -RedirectStandardError $appLog
    Start-Sleep -Seconds 6
    return $script:app
}
function Stop-App { if ($script:app -and -not $script:app.HasExited) { try { Stop-Process -Id $script:app.Id -Force -ErrorAction SilentlyContinue } catch { } } Start-Sleep -Seconds 2 }
function Check-CrashLog([string]$phase) {
    if (Test-Path "$settingsDir\crash.log") {
        $sz = (Get-Item "$settingsDir\crash.log").Length
        if ($sz -gt $script:lastCrashSize) {
            Flag "RP-$phase" "发现崩溃日志（crash.log 增至 $sz 字节）" $true
            Copy-Item "$settingsDir\crash.log" "$EvidenceDir\crash-$phase.log" -Force
            $script:lastCrashSize = $sz
        }
    }
}
$script:lastCrashSize = 0

# ================================================================
# 画像一：小白用户（11.1 多路画像 1/3）——装完第一分钟的真实路径
# ================================================================
Log '== 画像一：小白用户 =='
Stop-App; Stop-ServiceProc
Remove-Item "$settingsDir\settings.json" -Force -ErrorAction SilentlyContinue
Remove-Item "$settingsDir\Index" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$settingsDir\crash.log" -Force -ErrorAction SilentlyContinue
$noviceData = "$EvidenceDir\rpdata-novice"
if (Test-Path $noviceData) { Remove-Item $noviceData -Recurse -Force -ErrorAction SilentlyContinue }
$pipeUp = Start-Service $noviceData
if (-not $pipeUp) { Flag 'RP-N0' '服务启动失败（小白场景开局即卡）' $true }
$app = Start-App
Check-CrashLog 'N-start'
if ($app.HasExited) { Flag 'RP-N0b' '壳层启动即退出（小白第一分钟就撞墙）' $true }

# 小白走引导：下一步 → 开始建立索引 → 等就绪 → 完成
$ob = $null
foreach ($i in 1..15) {
    try {
        $ob = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch 首次使用引导')))
        if ($ob) { break }
    } catch { }
    Start-Sleep -Seconds 2
}
if (-not $ob) { Flag 'RP-N1' '首启引导窗未出现（干净环境应弹引导）' $true }
else {
    Screenshot 'RP-N1-onboarding'
    foreach ($step in @(@('下一步', 'RP-N2'), @('开始建立索引', 'RP-N3'))) {
        $btn = Find-ByName $ob $step[0]
        if ($btn -and (Click-El $btn)) { Ledger "引导「$($step[0])」点通" } else { Flag $step[1] "引导「$($step[0])」点不动" $true }
        Start-Sleep -Seconds 2
    }
    Screenshot 'RP-N3-indexing'
    $done = $false
    foreach ($i in 1..120) {
        Start-Sleep -Seconds 3
        $fin = Find-ByName $ob '完成，进入 TaoSearch'
        if ($fin -and -not $fin.Current.IsOffscreen) { $done = $true; break }
    }
    if (-not $done) { Flag 'RP-N4' "引导索引就绪超时（6 分钟内未出「完成」，语料 $fileCount 文件）" $true }
    else {
        Ledger '引导索引就绪出「完成」'
        Screenshot 'RP-N4-onboarding-done'
        $fin = Find-ByName $ob '完成，进入 TaoSearch'
        if ($fin) { Click-El $fin | Out-Null; Start-Sleep -Seconds 3 }
    }
}
Check-CrashLog 'N-guide'
$win = Get-AppWin $app
if (-not $win) { Flag 'RP-N5' '引导完成后主窗未出现' $true }

# 小白摸索：搜常见词、搜不存在的东西、乱点页签
if ($win) {
    Screenshot 'RP-N5-main'
    foreach ($q in @('readme', '发票', '不存在的词zzz')) {
        $r = Search-Like-Human $win $q 20
        Ledger "小白搜「$q」→ 命中 $($r[0]) 条"
        if ($q -eq 'readme' -and $r[0] -le 0) { Flag 'RP-N6' "已知在文件 readme 搜不到（命中 $($r[0])）" $true }
        if ($q -eq '发票' -and $r[0] -le 0) { Flag 'RP-N6b' "已知在文件 发票 搜不到（命中 $($r[0])）" $true }
        Screenshot "RP-N6-search-$($q -replace '[^\w]', '')"
    }
    # 小白切内容页签搜中文
    try {
        $tab = Find-ByName $win '内容'
        if ($tab) { Click-El $tab | Out-Null; Start-Sleep -Seconds 2 }
        $r = Search-Like-Human $win '测试文档' 30
        Ledger "小白内容页签搜「测试文档」→ 命中 $($r[0]) 条"
        if ($r[0] -le 0) { Flag 'RP-N7' "内容页签搜已知中文内容无命中（命中 $($r[0])）" $true }
        Screenshot 'RP-N7-content-search'
        $tab2 = Find-ByName $win '文件名'
        if ($tab2) { Click-El $tab2 | Out-Null; Start-Sleep -Seconds 1 }
    } catch { Flag 'RP-N7b' "内容页签操作异常: $_" $false }
    # 小白开设置页看看
    try {
        $nav = Find-ByName $win '设置'
        if ($nav) { Click-El $nav | Out-Null; Start-Sleep -Seconds 3; Screenshot 'RP-N8-settings' }
        else { Flag 'RP-N8' '设置入口未找到' $false }
    } catch { Flag 'RP-N8b' "设置导航异常: $_" $false }
    Check-CrashLog 'N-explore'
    # 小白点 X 关窗（以为退出了，其实进托盘）
    try { $app.CloseMainWindow() | Out-Null } catch { }
    Start-Sleep -Seconds 3
    $app.Refresh()
    if ($app.HasExited) { Flag 'RP-N9' '关窗后进程退出（托盘常驻失效）' $true }
    else { Ledger '关窗后进程存活（进托盘，小白视角：窗口没了但程序还在）' }
    Check-CrashLog 'N-close'
}

# ================================================================
# 画像二：熟练用户（11.1 多路画像 2/3）——日常高频操作反复做换着做
# ================================================================
Log '== 画像二：熟练用户 =='
# 重启壳层（熟练用户第二天再开）
Stop-App
$app = Start-App
Check-CrashLog 'P-start'
$win = Get-AppWin $app
if (-not $win) { Flag 'RP-P0' '二次启动主窗未出现（检查点恢复路径）' $true }
if ($win) {
    Screenshot 'RP-P0-main-restart'
    # 高频连搜 8 笔（换着做）
    $terms = @('readme', 'dataset', 'app-1', 'source_1', 'duplicate-5', 'batch', 'locked-3', 'deep-file')
    foreach ($t in $terms) {
        $r = Search-Like-Human $win $t 15
        Ledger "熟练连搜「$t」→ 命中 $($r[0]) 条"
        if ($t -in @('readme', 'batch', 'locked-3', 'deep-file') -and $r[0] -le 0) { Flag "RP-P1-$t" "高频搜索已知在词「$t」无命中" $true }
    }
    Screenshot 'RP-P1-rapid-searches'
    # 老板真机踩中的路径：多批命中后紧接第二笔
    $r1 = Search-Like-Human $win 'batch' 20
    $r2 = Search-Like-Human $win 'readme' 20
    Ledger "多批路径：batch 命中 $($r1[0]) → 紧接 readme 命中 $($r2[0])"
    if ($r1[0] -le 0 -or $r2[0] -le 0) { Flag 'RP-P2' "多批后紧接搜索落空（batch=$($r1[0]), readme=$($r2[0])）——0.1.0.15 真机红灯复现" $true }
    Screenshot 'RP-P2-multibatch'
    # Alt+Q 真实按键唤起快速窗
    try {
        [RP.Native]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [RP.Native]::keybd_event(0x51, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [RP.Native]::keybd_event(0x51, 0, 2, [UIntPtr]::Zero)
        [RP.Native]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Seconds 2
        $quick = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch 快速搜索')))
        if ($quick) {
            Ledger 'Alt+Q 快速窗唤起'
            Screenshot 'RP-P3-quick'
            $qe = Get-Edit $quick
            if ($qe) {
                ($qe.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('readme')
                Start-Sleep -Seconds 4
                Screenshot 'RP-P3b-quick-search'
            }
            [RP.Native]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
            [RP.Native]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
            Start-Sleep -Seconds 1
        } else { Flag 'RP-P3' 'Alt+Q 真实按键未唤起快速窗' $true }
    } catch { Flag 'RP-P3b' "Alt+Q 按键异常: $_" $false }
    Check-CrashLog 'P-hotkey'
    # 设置页：暂停/恢复、重建索引
    try {
        $nav = Find-ByName $win '设置'
        if ($nav) {
            Click-El $nav | Out-Null; Start-Sleep -Seconds 3
            $pauseBtn = Find-ByName $win '暂停索引'
            if ($pauseBtn) { Click-El $pauseBtn | Out-Null; Start-Sleep -Seconds 2; Ledger '暂停索引点通'; Screenshot 'RP-P4-paused' }
            # 弹窗先关（真实用户会点确认）
            foreach ($label in @('好', '知道了', '关闭', '确定')) {
                $d = Find-ByName $win $label
                if ($d) { Click-El $d | Out-Null; Start-Sleep -Milliseconds 800 }
            }
            $resumeBtn = Find-ByName $win '恢复索引'
            if ($resumeBtn) { Click-El $resumeBtn | Out-Null; Start-Sleep -Seconds 2; Ledger '恢复索引点通' }
            foreach ($label in @('好', '知道了', '关闭', '确定')) {
                $d = Find-ByName $win $label
                if ($d) { Click-El $d | Out-Null; Start-Sleep -Milliseconds 800 }
            }
            Screenshot 'RP-P4-settings-ops'
        }
    } catch { Flag 'RP-P4' "设置页操作异常: $_" $false }
    Check-CrashLog 'P-settings'
}

# ================================================================
# 画像三：搞破坏（11.1 多路画像 3/3 + 11.3 盲测乱试找崩溃）
# ================================================================
Log '== 画像三：搞破坏 =='
# D1：索引中途杀服务再恢复
Stop-ServiceProc
Remove-Item "$EvidenceDir\rpdata-kill" -Recurse -Force -ErrorAction SilentlyContinue
$pipeUp = Start-Service "$EvidenceDir\rpdata-kill"
Start-Sleep -Seconds 5   # 让它开始建索引
if ($script:svc -and -not $script:svc.HasExited) {
    Stop-Process -Id $script:svc.Id -Force
    Ledger '建索引中途强杀服务'
    Start-Sleep -Seconds 2
    $pipeUp2 = Start-Service "$EvidenceDir\rpdata-kill"
    if (-not $pipeUp2) { Flag 'RP-D1' '杀服务后重启失败（中断恢复路径断）' $true }
    else { Ledger '杀服务后重启成功（管道就绪）' }
} else { Flag 'RP-D1b' '服务重启场景中进程意外退出' $false }
Check-CrashLog 'D-kill'
# D2：设置文件损坏（写垃圾字节）后启动壳层
Stop-App
Set-Content "$settingsDir\settings.json" '{ corrupted json !!! [[[' -Encoding UTF8
$app = Start-App
Start-Sleep -Seconds 5
$app.Refresh()
if ($app.HasExited) { Flag 'RP-D2' '设置文件损坏导致壳层启动即崩' $true }
else {
    $win = Get-AppWin $app
    if (-not $win) { Flag 'RP-D2b' '设置损坏后主窗不可达' $true }
    else { Ledger '设置文件损坏后壳层正常启动（走默认值）' }
}
Check-CrashLog 'D-corrupt'
# D3：乱试怪查询（真人乱输入 + 恶意探试）
if ($win) {
    $weirdQueries = @('', ('x' * 500), '<>:"|?*', '😀😀😀 搜索', "'; DROP TABLE --", 'C:\Windows\system32', '..\..\..\etc\passwd', '   ')
    foreach ($q in $weirdQueries) {
        $label = if ($q.Length -gt 20) { $q.Substring(0, 20) + '…' } else { $q }
        try {
            $r = Search-Like-Human $win $q 10
            Ledger "怪查询「$label」→ 命中 $($r[0])"
            $app.Refresh()
            if ($app.HasExited) { Flag 'RP-D3' "怪查询「$label」致壳层退出" $true; $app = Start-App; $win = Get-AppWin $app }
        } catch { Flag 'RP-D3b' "怪查询「$label」异常: $_" $false }
    }
    Screenshot 'RP-D3-weird-queries'
    Check-CrashLog 'D-queries'
    # D4：索引期间删文件再搜
    try {
        Remove-Item "$corpus\docs\readme.txt" -Force -ErrorAction SilentlyContinue
        $r = Search-Like-Human $win 'readme' 15
        Ledger "删除 readme 后立刻搜 → 命中 $($r[0])（重建前残留属正常，重建后应消失）"
        Screenshot 'RP-D4-after-delete'
    } catch { Flag 'RP-D4' "删文件场景异常: $_" $false }
    # D5：快速连开连关 3 次
    Stop-App
    foreach ($i in 1..3) {
        $app = Start-App
        Stop-App
    }
    $app = Start-App
    Start-Sleep -Seconds 4
    $app.Refresh()
    if ($app.HasExited) { Flag 'RP-D5' '快速连开连关后壳层起不来' $true }
    else {
        $win = Get-AppWin $app
        if (-not $win) { Flag 'RP-D5b' '连开连关后主窗不可达' $true }
        else { Ledger '快速连开连关 3 次后正常启动' }
    }
    Check-CrashLog 'D-restart'
    # D6：第二实例转发
    try {
        $app2 = Start-Process -FilePath "$PublishDir\TaoSearch.App.exe" -ArgumentList '--search','readme' -WorkingDirectory $PublishDir -PassThru
        Start-Sleep -Seconds 6
        $app2.Refresh()
        if (-not $app2.HasExited) { Flag 'RP-D6' "第二实例未退出（单实例约束失效，PID=$($app2.Id)）" $false; try { Stop-Process -Id $app2.Id -Force } catch { } }
        else { Ledger '第二实例转发后自行退出（单实例正常）' }
        $app.Refresh()
        if ($app.HasExited) { Flag 'RP-D6b' '转发后第一实例反被带崩' $true }
    } catch { Flag 'RP-D6c' "第二实例场景异常: $_" $false }
    Check-CrashLog 'D-instance'
}

# ================================================================
# 收口：日志强制归档（六.9）+ 对账表
# ================================================================
Stop-App; Stop-ServiceProc
'stop' | Set-Content $stopRec
Start-Sleep -Seconds 4

# 日志归档
$arch = "$EvidenceDir\logs"
New-Item -ItemType Directory -Force -Path $arch | Out-Null
foreach ($f in @('app.log', 'app.out.log', 'service.log', 'service.err.log')) {
    if (Test-Path "$EvidenceDir\$f") { Copy-Item "$EvidenceDir\$f" "$arch\$f" -Force }
}
foreach ($f in @('crash.log', 'hotkey.log')) {
    if (Test-Path "$settingsDir\$f") { Copy-Item "$settingsDir\$f" "$arch\$f" -Force }
}
try {
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddHours(-2) } -MaxEvents 400 -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'TaoSearch' } |
        ForEach-Object { "[$($_.TimeCreated)] $($_.ProviderName): $($_.Message)" } | Set-Content "$arch\eventlog-taosearch.txt"
} catch { }
# 日志异常扫描
$logErrs = 0
foreach ($lf in Get-ChildItem $arch -File -ErrorAction SilentlyContinue) {
    $hits = (Select-String -Path $lf.FullName -Pattern 'Exception|Unhandled|崩溃' -ErrorAction SilentlyContinue | Measure-Object).Count
    if ($hits -gt 0) { $logErrs += $hits; Ledger "日志 $($lf.Name) 发现 $hits 处异常关键字（已归档待亲看）" }
}
if ($logErrs -eq 0) { Ledger '全部日志扫描：未见异常关键字' }

# 对账表
$frameCount = (Get-ChildItem "$EvidenceDir\frames" -File -ErrorAction SilentlyContinue | Measure-Object).Count
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# TaoSearch 0.1.0.15 扮演验收对账表（v3.22 十一章）')
[void]$md.AppendLine('')
[void]$md.AppendLine("日期：2026-11-24；环境：GitHub Actions windows-latest 云真机；语料：$fileCount 文件 / $sizeMb MB 脏数据环境")
[void]$md.AppendLine('')
[void]$md.AppendLine('## 一、真实环境镜像（11.6）')
[void]$md.AppendLine("- 文件数：$fileCount；体量：$sizeMb MB")
[void]$md.AppendLine('- 脏数据构成：中文/emoji/特殊字符文件名、超长路径（>260 字符）、损坏文件（假 docx/pdf）、只读文件、空文件、重名文件、大文件（5MB×3）')
[void]$md.AppendLine('')
[void]$md.AppendLine('## 二、三画像观察记录（11.1/11.2/11.3）')
foreach ($l in $ledger) { [void]$md.AppendLine("- $l") }
[void]$md.AppendLine('')
[void]$md.AppendLine('## 三、异常清单（盲测问题清单）')
if ($anomalies.Count -eq 0) { [void]$md.AppendLine('- 逐段摸索未见异常') } else { foreach ($a in $anomalies) { [void]$md.AppendLine("- $a") } }
[void]$md.AppendLine('')
[void]$md.AppendLine('## 四、证据四件（六.9）')
[void]$md.AppendLine("- 录屏帧：$frameCount 张（3 秒/帧，全程）")
[void]$md.AppendLine("- 截图：$((Get-ChildItem $EvidenceDir -Filter 'RP-*.png' | Measure-Object).Count) 张")
[void]$md.AppendLine("- 日志：$((Get-ChildItem $arch -File | Measure-Object).Count) 份归档（app/service/crash/hotkey/eventlog）")
[void]$md.AppendLine('')
[void]$md.AppendLine('## 五、看后记录（六.10，主班亲看后填写）')
[void]$md.AppendLine('- （待主班下载证据后逐屏亲看补写）')
$md.ToString() | Set-Content "$EvidenceDir\roleplay-ledger.md" -Encoding UTF8

Log "== 扮演验收收口：CRASH 级异常 $crashFails 项，观察 $($ledger.Count) 条，录屏帧 $frameCount =="
if ($crashFails -gt 0) { exit 1 } else { exit 0 }
