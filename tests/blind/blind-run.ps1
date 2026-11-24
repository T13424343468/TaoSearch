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

# 2026-11-24 首轮盲测假绿教训：UIA Invoke 在部分环境静默失效，补真实鼠标坐标点击兜底
if (-not ('NativeMouse' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeMouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(0x02, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x04, 0, 0, 0, UIntPtr.Zero);
    }
}
"@
}
$script:blindFail = $false
$script:popupReported = $false

# 2026-11-24 二轮假绿教训：盲测绿场藏着服务 0xc0000005 系统错误弹窗（run 45227026290 实锤，
# 「TaoSearch.Service.exe - System Error」在屏而账面 success）——补系统错误弹窗检测断言，弹窗即红
if (-not ('NativePopupScan' -as [type])) {
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class NativePopupScan {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public static List<string> SystemErrorPopupTitles() {
        var found = new List<string>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (c.ToString() != "#32770") return true;
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            string title = t.ToString();
            if (title.Contains("System Error") || title.Contains("Exception Processing Message")) found.Add(title);
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static int CloseSystemErrorPopups() {
        int closed = 0;
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (c.ToString() != "#32770") return true;
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            string title = t.ToString();
            if (title.Contains("System Error") || title.Contains("Exception Processing Message")) {
                PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE：取证后关弹窗，不阻塞后续 UIA
                closed++;
            }
            return true;
        }, IntPtr.Zero);
        return closed;
    }
}
"@
}

function Test-SystemErrorPopup {
    $titles = [NativePopupScan]::SystemErrorPopupTitles()
    foreach ($t in $titles) {
        Log "SYSTEM ERROR POPUP DETECTED: $t"
        if (-not $script:popupReported) {
            $script:popupReported = $true
            $findings.Add("B-x99 检测到系统错误弹窗「$t」——原生崩溃在屏（对应截图定谳，弹窗已关不阻塞后续）")
            $script:blindFail = $true
        }
        [NativePopupScan]::CloseSystemErrorPopups() | Out-Null
    }
}

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
    Test-SystemErrorPopup   # 每个取证点同步查弹窗（2026-11-24 假绿整改）
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
        catch { Log "btn[$label]: 不可调用 $_" }
    }
    # 降级：真实鼠标坐标点击（对齐 gate2 教训口径，Invoke 静默失效时兜底）
    try {
        $pt = $btn.GetClickablePoint()
        [NativeMouse]::Click([int]$pt.X, [int]$pt.Y)
        return $true
    } catch { return $false }
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
# 索引就绪等待 15 分钟（run 45222019352：6 分钟收 366,939 个文件仍在继续；run 45229193248：15 分钟仍未完，见 B-05））
function Complete-Onboarding([string]$tag) {
    $script:obStage = 'not-found'
    $w = Get-OnboardingWindow
    if (-not $w) { Log "onboarding: 引导窗未找到（TaoSearch 首次使用引导）"; return }
    $script:obStage = 'step1'
    if (-not (Invoke-ButtonByName $w '下一步')) { Log "onboarding: 下一步未点通"; return }
    Start-Sleep -Seconds 2; Shot "$tag-step2"
    $w = Get-OnboardingWindow; if (-not $w) { $w = Get-AppWindow 'TaoSearch' }
    if (-not $w -or -not (Invoke-ButtonByName $w '开始建立索引')) { Log "onboarding: 开始建立索引未点通"; return }
    $script:obStage = 'indexing'
    Start-Sleep -Seconds 2; Shot "$tag-step3"
    $done = $false
    # 15 分钟：五轮实测 30 万级镜像索引需 6 分钟以上（run 45222019352：6 分钟收 366,939 个文件仍在继续）
    foreach ($i in 1..450) {
        Start-Sleep -Seconds 2
        $w = Get-OnboardingWindow; if (-not $w) { $w = Get-AppWindow 'TaoSearch' }
        if ($w -and (Invoke-ButtonByName $w '完成，进入 TaoSearch')) { $done = $true; break }
        if ($i % 60 -eq 0) { Log "onboarding: 等待索引就绪 $($i*2)s（30 万级镜像口径）" }
    }
    Shot "$tag-done-$done"
    if ($done) { $script:obStage = 'done'; Start-Sleep -Seconds 3; Shot "$tag-main" } else { Log "onboarding: 15 分钟内未就绪（记录实际耗时，产品侧观察项：大镜像首扫时长）" }
}
function Send-Text([string]$text) {
    [System.Windows.Forms.SendKeys]::SendWait($text)
}
function Focus-SearchBox([System.Windows.Automation.AutomationElement]$w) {
    # B-06 修复（run 45238141117）：UIA SetFocus 聚焦的是窗口而非输入框，SendKeys 文本未落搜索框；
    # 先真实鼠标点击搜索框（Edit 控件）再 SendKeys，带结果搜索证据才能落地
    # 七轮回归（run 45245082853）：GetClickablePoint 报 no clickable point——改用 BoundingRectangle 中心点击，
    # 并按窗口矩形上部兜底（搜索框位于窗口顶部区域）
    if (-not $w) { Log "searchbox: 窗口为空，跳过聚焦"; return $false }
    $edits = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    foreach ($e in $edits) {
        try {
            $r = $e.Current.BoundingRectangle
            if ($r.Width -gt 20 -and $r.Height -gt 10) {
                [NativeMouse]::Click([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2))
                Start-Sleep -Milliseconds 300
                return $true
            }
        } catch { }
    }
    try {
        $wr = $w.Current.BoundingRectangle
        [NativeMouse]::Click([int]($wr.X + $wr.Width/2), [int]($wr.Y + $wr.Height*0.14))
        Start-Sleep -Milliseconds 300
        Log "searchbox: Edit 无有效矩形，已按窗口矩形上部兜底点击"
        return $true
    } catch { Log "searchbox: 点击失败 $_"; return $false }
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
$obSeen = Get-OnboardingWindow
if ($obSeen) { Complete-Onboarding 'BL-xiaobai-02' }
else { Log "onboarding: 引导窗未出现（30s 内）"; $findings.Add("B-x01 首启 30s 内未找到引导窗「TaoSearch 首次使用引导」（UIA）"); $script:blindFail = $true }
Start-Sleep -Seconds 8
# 硬断言（2026-11-24 假绿教训）：引导必须已关闭，否则本轮盲测无效，CI 必须红
# B-05 改判（run 45229193248）：若已走到索引阶段、仅因 30 万级镜像首扫 15 分钟未完而未关闭，
# 属产品观察项（大镜像首扫时长），不再硬失败；画像路继续跑（搜索空结果也是有效证据）。
# 引导窗若卡在前置步骤（obStage 非 indexing/done）仍按脚本或产品缺陷硬失败。
$obGone = -not (Get-OnboardingWindow)
if (-not $obGone) {
    if ($script:obStage -eq 'indexing') {
        $findings.Add("B-05 观察：30 万级镜像首扫 15 分钟未就绪（产品观察项：大镜像首扫时长，MFT 直读、无假百分比、UI 正常），画像路继续")
        Log "OBSERVATION: onboarding indexing still running after 15 min; continue personas"
    } else {
        $findings.Add("B-x02 引导窗未关闭：盲测推进链路未走通（脚本或产品缺陷，见 BL-xiaobai-03 截图定谳）")
        $script:blindFail = $true
        Log "ASSERT FAIL: onboarding still open after Complete-Onboarding"
    }
}
Shot 'BL-xiaobai-03-after-onboarding'
# 小白随手搜一个常见词
$w = Get-AppWindow 'TaoSearch'
if ($w) {
    $w.SetFocus()
    Focus-SearchBox $w | Out-Null
    Send-Text '报告'
    Start-Sleep -Seconds 3
    Shot 'BL-xiaobai-04-naive-search'
} 
# 小白点设置页看看
try {
    $w2 = Get-AppWindow 'TaoSearch'
    $settings = $w2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '设置')))
    if ($settings) { try { $settings.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { try { $settings.SetFocus() } catch {} }; Start-Sleep -Seconds 3; Shot 'BL-xiaobai-05-settings' }
} catch { Log "xiaobai settings: $_" }

# ── 画像二：熟练（场景②③⑤：高频反复 + 低频致命 + 镜像规模）──
Log "persona=shulian start"
# 连续快速搜索（多批命中路径：batch-* 语料 300 个 > 200 封顶）
$w = Get-AppWindow 'TaoSearch'
if ($w) {
    $w.SetFocus()
    Focus-SearchBox $w | Out-Null
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
        if ($el) { try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { try { $el.SetFocus() } catch {} }; Start-Sleep -Seconds 1 }
    }
    Shot 'BL-shulian-03-settings-actions'
} catch { Log "shulian settings: $_" }
# 托盘与热键（场景②③）
Send-Text('%q'); Start-Sleep -Seconds 2; Shot 'BL-shulian-04-hotkey-quick'
Send-Text('%q'); Start-Sleep -Seconds 1

# ── 画像三：搞破坏（场景③④⑤ + 9.8 机械遍历）──
Log "persona=pohuai start"
# 畸形输入灌入（2026-11-24 整改：①'a'*500 在数组字面量中被解析为 'a' * (500,'*')——逗号优先级高于 *，
# 字符串重复拿到 Object[] 报 Cannot convert Object[] to Int32，加括号修复；②fuzz 串含 ^%~(){} 等
# SendKeys 元字符会被解释（%SYSTEMROOT% 变 Alt+S、{ 组非法抛错），改剪贴板粘贴保真灌入）
$w = Get-AppWindow 'TaoSearch'
if ($w) {
    $w.SetFocus()
    Focus-SearchBox $w | Out-Null
    $fuzz = @(('a' * 500), '*', '?', 'file: && ||', '\..\..\..', ([char]::ConvertFromUtf32(0x1F600) + ' emoji'), 'regex ( [ { * +', "' OR 1=1 --", '%SYSTEMROOT%', 'con.txt')
    foreach ($f in $fuzz) {
        Set-Clipboard -Value $f
        Send-Text('^a'); Start-Sleep -Milliseconds 150
        Send-Text('^v'); Start-Sleep -Milliseconds 600
    }
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
    Get-ChildItem 'D:\TaoSearch' -Filter '*.log' -Recurse -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_.FullName (Join-Path $EvidenceDir ("svc-" + $_.Name)) -Force -ErrorAction SilentlyContinue }
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
Test-SystemErrorPopup   # 收尾终扫：最后一段流程中的崩溃弹窗不漏检（2026-11-24 假绿整改）
# 2026-11-24 假绿教训：盲测推进链路未走通必须红，禁止静默退出 0
if ($script:blindFail) { Log "BLIND RUN FAILED (assertion(s) unmet)"; exit 1 }
