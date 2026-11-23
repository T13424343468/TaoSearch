# TaoSearch 零用例盲测（v3.22 十一.3 零用例盲测令，2026-11-23 老板令落制）
# 口径：不给代码、不给用例，只拿安装包按六类场景当真实用户摸索；三路画像并行（小白/熟练/搞破坏）；
#       9.8 机械遍历（乱试找崩溃）并入「搞破坏」画像路执行。全程截图+日志取证，录屏由 workflow 层 ffmpeg 承担。
# 产出：问题清单骨架（findings.md）+ 逐档截图 + 全部日志，随对账表一并交付（六.9 日志归档口径）。
# 用法：powershell -File blind-run.ps1 -PublishDir <publish> -EvidenceDir <evidence> [-MirrorRoot C:\taosearch-mirror]
param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [Parameter(Mandatory=$true)][string]$EvidenceDir,
    [string]$MirrorRoot = "C:\taosearch-mirror"
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null

function Log([string]$m) { Write-Host "[blind] $m" }
function Shot([string]$name) {
    try {
        Start-Sleep -Milliseconds 400
        $b = New-Object System.Drawing.Bitmap([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
        $g = [System.Drawing.Graphics]::FromImage($b)
        $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
        $b.Save((Join-Path $EvidenceDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $b.Dispose()
        Log "shot $name"
    } catch { Log "shot fail $name : $_" }
}
function Get-AppWindow([string]$titlePart) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    # 先按精确名找窗口（对齐 gate2 口径），再退化为包含名；只认 ControlType.Window，防误拿任务栏按钮
    $exact = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $titlePart)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
    $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $exact)
    if ($w) { return $w }
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($el in $all) {
        try {
            if ($el.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and $el.Current.Name -like "*$titlePart*") { return $el }
        } catch { }
    }
    return $null
}
function Invoke-ButtonByName([System.Windows.Automation.AutomationElement]$w, [string]$label) {
    # 对齐 gate2 Invoke-UiaButton：只按 Name 找（不加 ControlType 过滤），GetCurrentPattern 双模式兜底
    $btn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $label)))
    if (-not $btn) { Log "btn[$label]: 元素未找到"; return $false }
    try { ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); return $true }
    catch {
        try { ($btn.GetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern)).DoDefaultAction(); return $true }
        catch { Log "btn[$label]: 不可调用 $_"; return $false }
    }
}
function Get-OnboardingWindow {
    # 对齐 gate2 G2-11：按精确窗口名「TaoSearch 首次使用引导」找，重试 30s（B-03 v3：
    # v2 用 Get-AppWindow 'TaoSearch' 模糊回退疑似误拿后台主窗，导致在主窗里找「下一步」落空）
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    foreach ($i in 1..15) {
        try {
            $ob = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'TaoSearch 首次使用引导')))
            if ($ob) { return $ob }
        } catch { }
        Start-Sleep -Seconds 2
    }
    return $null
}
# 引导页逐页推进（B-03 修复 v3：引导窗按精确名独立定位、逐页重找防元素失效；
# 索引就绪等待放宽到 6 分钟（镜像 27 万文件口径，引导文案「视文件数量需几分钟」））
function Complete-Onboarding([string]$tag) {
    $w = Get-OnboardingWindow
    if (-not $w) { Log "onboarding: 引导窗未找到（TaoSearch 首次使用引导）"; return }
    if (-not (Invoke-ButtonByName $w '下一步')) { Log "onboarding: 下一步未点通"; return }
    Start-Sleep -Seconds 2; Shot "$tag-step2"
    $w = Get-OnboardingWindow; if (-not $w) { $w = Get-AppWindow 'TaoSearch' }
    if (-not $w -or -not (Invoke-ButtonByName $w '开始建立索引')) { Log "onboarding: 开始建立索引未点通"; return }
    Start-Sleep -Seconds 2; Shot "$tag-step3"
    $done = $false
    foreach ($i in 1..180) {  # 6 分钟：真实数据规模下索引就绪
        Start-Sleep -Seconds 2
        $w = Get-OnboardingWindow; if (-not $w) { $w = Get-AppWindow 'TaoSearch' }
        if ($w -and (Invoke-ButtonByName $w '完成，进入 TaoSearch')) { $done = $true; break }
    }
    Shot "$tag-done-$done"
    if ($done) { Start-Sleep -Seconds 3; Shot "$tag-main" } else { Log "onboarding: 6 分钟内未就绪（镜像规模口径，记录实际耗时）" }
}
function Send-Text([string]$text) {
    [System.Windows.Forms.SendKeys]::SendWait($text)
}
$findings = New-Object System.Collections.Generic.List[string]

# ── 环境准备：真实数据规模场景（11.2⑤）优先挂镜像，无镜像退回 C 盘真实盘并在清单注明 ──
$volume = 'C:\'
$mirrorNote = 'no-mirror'
if (Test-Path (Join-Path $MirrorRoot 'manifest.json')) {
    $volume = $MirrorRoot
    $mirrorNote = 'mirror'
}
Log "volume=$volume mirror=$mirrorNote"

# 干净环境口径：清掉上次残留设置与索引（盲测当全新用户）
$taoData = Join-Path $env:LOCALAPPDATA 'TaoSearch'
if (Test-Path $taoData) { Remove-Item -Recurse -Force $taoData -ErrorAction SilentlyContinue }
Get-Process TaoSearch* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# ── 画像一：小白（场景①②：装完第一分钟的真实路径，凭直觉摸索）──
Log "persona=xiaobai start"
$svc = Start-Process -FilePath "$PublishDir\TaoSearch.Service.exe" -ArgumentList "`"$volume`"" -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 3
$app = Start-Process -FilePath "$PublishDir\TaoSearch.App.exe" -WorkingDirectory $PublishDir -PassThru
Start-Sleep -Seconds 8
Shot 'BL-xiaobai-01-first-launch' # B-01 复核口径：首启截图延后至渲染稳定（8s），若仍空白升级为缺陷
# 小白不看说明：引导页逐页下一步（B-03 v3：引导窗按精确名独立定位）
Complete-Onboarding 'BL-xiaobai-02'
Start-Sleep -Seconds 8
Shot 'BL-xiaobai-03-after-onboarding'
# 小白随手搜一个常见词
$w = Get-AppWindow 'TaoSearch'
if ($w) {
    $w.SetFocus()
    Send-Text '报告'
    Start-Sleep -Seconds 3
    Shot 'BL-xiaobai-04-naive-search'
} 
# 小白点设置页看看
try {
    $w2 = Get-AppWindow 'TaoSearch'
    $settings = $w2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '设置')))
    if ($settings) { $settings.GetPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds 3; Shot 'BL-xiaobai-05-settings' }
} catch { Log "xiaobai settings: $_" }

# ── 画像二：熟练（场景②③⑤：高频反复 + 低频致命 + 镜像规模）──
Log "persona=shulian start"
# 连续快速搜索（多批命中路径：batch-* 语料 300 个 > 200 封顶）
$w = Get-AppWindow 'TaoSearch'
if ($w) {
    $w.SetFocus()
    foreach ($q in @('batch', '报告', 'batch', 'dir', 'batch-1', '搜索')) {
        Send-Text('^a'); Send-Text($q); Start-Sleep -Milliseconds 900
    }
    Shot 'BL-shulian-01-rapid-search'
    # 正则与高级语法
    Send-Text('^a'); Send-Text('batch-\d+'); Start-Sleep -Seconds 2; Shot 'BL-shulian-02-regex'
}
# 低频致命：改设置（重建索引/暂停恢复/换主题）
try {
    $w2 = Get-AppWindow 'TaoSearch'
    foreach ($label in @('暂停索引', '恢复索引', '墨白', '水墨')) {
        $el = $w2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $label)))
        if ($el) { try { $el.GetPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { try { $el.SetFocus() } catch {} }; Start-Sleep -Seconds 1 }
    }
    Shot 'BL-shulian-03-settings-actions'
} catch { Log "shulian settings: $_" }
# 托盘与热键（场景②③）
Send-Text('%q'); Start-Sleep -Seconds 2; Shot 'BL-shulian-04-hotkey-quick'
Send-Text('%q'); Start-Sleep -Seconds 1

# ── 画像三：搞破坏（场景③④⑤ + 9.8 机械遍历）──
Log "persona=pohuai start"
# 畸形输入灌入
$w = Get-AppWindow 'TaoSearch'
if ($w) {
    $w.SetFocus()
    $fuzz = @('a' * 500, '*', '?', 'file: && ||', '\..\..\..', $([char]::ConvertFromUtf32(0x1F600) + ' emoji'), "regex ( [ { * +", "' OR 1=1 --", '%SYSTEMROOT%', 'con.txt')
    foreach ($f in $fuzz) { Send-Text('^a'); Send-Text($f); Start-Sleep -Milliseconds 600 }
    Shot 'BL-pohuai-01-fuzz-input'
}
# 并发同发：快速窗 + 主窗同时搜
Start-Process -FilePath "$PublishDir\TaoSearch.App.exe" -ArgumentList '--quick' -WorkingDirectory $PublishDir
Start-Sleep -Seconds 3
Shot 'BL-pohuai-02-quick-and-main'
# 搜索中杀服务（中断恢复）
Get-Process TaoSearch.Service -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Shot 'BL-pohuai-03-service-killed'
# 重启服务（壳层自动拉起路径）
$svc2 = Start-Process -FilePath "$PublishDir\TaoSearch.Service.exe" -ArgumentList "`"$volume`"" -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 5
Shot 'BL-pohuai-04-service-restart'
# 改坏设置文件（损坏 JSON）
try {
    $settingsFile = Get-ChildItem $taoData -Filter '*.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($settingsFile) { Set-Content -Path $settingsFile.FullName -Value '{ broken json !!!' -Encoding utf8 }
} catch { Log "corrupt settings: $_" }
Get-Process TaoSearch.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process -FilePath "$PublishDir\TaoSearch.App.exe" -WorkingDirectory $PublishDir
Start-Sleep -Seconds 6
Shot 'BL-pohuai-05-corrupt-settings-restart'
# 删索引库文件（数据目录破坏）
try {
    $idxDir = Get-ChildItem 'D:\TaoSearch' -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($idxDir) { Get-ChildItem $idxDir.FullName -File -ErrorAction SilentlyContinue | Select-Object -First 3 | Remove-Item -Force -ErrorAction SilentlyContinue }
} catch { Log "delete index: $_" }
Start-Sleep -Seconds 3
Shot 'BL-pohuai-06-index-files-deleted'

# ── 日志归档（六.9）：安装/运行/崩溃日志全部落盘随证据交 ──
foreach ($log in @('crash.log', 'service-crash.log', 'hotkey.log', 'app.log')) {
    $src = Join-Path $taoData $log
    if (Test-Path $src) { Copy-Item $src (Join-Path $EvidenceDir "collected-$log") -Force }
}
if (Test-Path 'D:\TaoSearch') {
    Get-ChildItem 'D:\TaoSearch' -Filter '*.log' -Recurse -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_.FullName (Join-Path $EvidenceDir "svc-" + $_.Name) -Force -ErrorAction SilentlyContinue }
}
# 服务事件日志（服务模式崩溃线索）
try { Get-EventLog -LogName Application -Newest 50 -Source 'TaoSearch*' -ErrorAction SilentlyContinue | Out-File (Join-Path $EvidenceDir 'eventlog-taosearch.txt') -Encoding utf8 } catch {}

# ── 问题清单骨架（分身看后记录时逐条实锤回填）──
@"
# TaoSearch 零用例盲测问题清单（首轮，$(Get-Date -Format 'yyyy-MM-dd HH:mm')）
> 口径：不给代码不给用例，三路画像按六类场景摸索；镜像=$mirrorNote volume=$volume
> 本文件由分身亲看证据（截图+录屏+日志）后逐条实锤回填；零发现也要写明「逐屏看过、未见异常」。

## 自动探得线索（脚本取证，待亲看定谳）
$($findings -join "`n")

## 画像路发现（看后回填）
| 编号 | 画像 | 场景 | 描述 | 严重级 | 证据 |
|---|---|---|---|---|---|
| 待回填 | | | | | |
"@ | Out-File (Join-Path $EvidenceDir 'findings.md') -Encoding utf8

Log "blind run complete, evidence=$EvidenceDir"
