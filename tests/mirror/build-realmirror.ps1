# TaoSearch 真实环境镜像生成器（v3.22 十一.6 真实环境镜像令，2026-11-23 老板令落制）
# 用途：在云真机/测试机一次造好「几十万级真实文件 + 真实脏数据」标准环境，存为模板全线复用；
#       作为 11.2⑤「真实数据规模」的固定落地方式。禁用干净实验室环境充当真实环境。
# 脏数据项按本地搜索工具实际场景取 applicable：超长路径、奇怪字符、损坏文件、只读权限、
# 深嵌套、零字节、隐藏/系统属性、同名多目录、双分隔符口径语料、大小写混淆。
# 幂等：已存在 manifest.json 则跳过重建（传 -Force 强制重建）。
# 用法：powershell -File build-realmirror.ps1 -Root C:\taosearch-mirror -FileCount 300000
param(
    [string]$Root = "C:\taosearch-mirror",
    [int]$FileCount = 300000,
    [switch]$Force
)

$ErrorActionPreference = 'Continue'
$manifest = Join-Path $Root 'manifest.json'
if ((Test-Path $manifest) -and -not $Force) {
    Write-Host "[mirror] 已存在 $manifest，跳过重建（-Force 强制重建）"
    exit 0
}
if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
New-Item -ItemType Directory -Path $Root | Out-Null

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$stats = [ordered]@{ normal = 0; longpath = 0; weird = 0; corrupt = 0; zero = 0; readonly = 0; hidden = 0; samename = 0; batch = 0 }
$exts = @('.txt', '.md', '.docx', '.pdf', '.jpg', '.png', '.mp3', '.mp4', '.cs', '.json', '.log', '.xlsx', '.pptx', '.html')
$words = @('报告', '总结', '会议', '纪要', '合同', '发票', '照片', '设计', '备份', '草稿', 'report', 'notes', 'draft', 'final', 'v1', 'v2', 'archive', 'temp')

$dirCache = @{}
function New-File([string]$path, [int]$sizeBytes, [switch]$Corrupt, [switch]$Zero) {
    $dir = Split-Path $path -Parent
    if (-not $dirCache.ContainsKey($dir)) {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $dirCache[$dir] = $true
    }
    if ($Zero) { New-Item -ItemType File -Path $path -Force | Out-Null; return }
    $bytes = New-Object byte[] $sizeBytes
    if (-not $Corrupt) {
        # 可搜索的伪文本内容（全文索引语料）：词 + 随机后缀
        $text = "TaoSearch mirror corpus file. 关键词样本：搜索 测试 索引 内容检索。seq=$([guid]::NewGuid().ToString('N'))"
        $body = [System.Text.Encoding]::UTF8.GetBytes($text)
        for ($i = 0; $i -lt $bytes.Length; $i += $body.Length) { [Array]::Copy($body, 0, $bytes, $i, [Math]::Min($body.Length, $bytes.Length - $i)) }
    } else {
        (New-Object System.Random($path.GetHashCode())).NextBytes($bytes) # 损坏文件：随机字节冒充真实扩展名
    }
    [System.IO.File]::WriteAllBytes($path, $bytes)
}

# ① 常规真实规模：嵌套 3~6 层、真实扩展名与命名习惯
$normalCount = [int]($FileCount * 0.90)
$rand = New-Object System.Random(20261123)
for ($i = 0; $i -lt $normalCount; $i++) {
    $depth = 3 + $rand.Next(4)
    $segs = @()
    for ($d = 0; $d -lt $depth; $d++) { $segs += "dir{0:d3}" -f $rand.Next(200) }
    $name = "{0}-{1}-{2:d6}{3}" -f ($words[$rand.Next($words.Count)]), ($words[$rand.Next($words.Count)]), $i, ($exts[$rand.Next($exts.Count)])
    New-File -path (Join-Path $Root ($segs -join '\')) -sizeBytes (64 + $rand.Next(16384))
    $stats.normal++
    if ($i % 20000 -eq 0) { Write-Host ("[mirror] normal {0}/{1} elapsed {2:n0}s" -f $i, $normalCount, $sw.Elapsed.TotalSeconds) }
}

# ② 超长路径：逼近/超过 260 字符（LongPathsEnabled 与否都造，探路径解析边界）
$longBase = Join-Path $Root 'longpath'
$deep = (1..40 | ForEach-Object { 'level-with-a-rather-long-directory-name-segment-xx' }) -join '\'
foreach ($n in 1..50) {
    New-File -path (Join-Path $longBase "$deep\超长路径测试文件-$n-这是一段刻意拉长的文件名用来逼近MAX-PATH边界-$(('x')*80).txt") -sizeBytes 512
    $stats.longpath++
}

# ③ 奇怪字符：Unicode/emoji/空格收尾/#%&引号/制表符/连续点/大小写混淆
$weirdDir = Join-Path $Root 'weird'
$weirdNames = @(
    'emoji 😀🎉 文件名.txt', '中文＋全角符号＄％.txt', 'trailing space .txt',
    'hash#percent%amp&quote".txt', 'tab`tchar.txt', 'dots...txt',
    'UPPER.TXT', 'upper.txt', '大小写混淆.TXT', '连续空格   名.txt',
    '日本語のファイル.txt', '한국어파일명.txt', 'РусскоеИмяФайла.txt', 'café-résumé.txt'
)
foreach ($n in $weirdNames) { New-File -path (Join-Path $weirdDir $n) -sizeBytes 256; $stats.weird++ }

# ④ 损坏文件：真实扩展名 + 随机字节（PDF/DOCX 头损坏、截断）
$corruptDir = Join-Path $Root 'corrupt'
foreach ($n in 1..100) {
    New-File -path (Join-Path $corruptDir "损坏文档-$n.pdf") -sizeBytes (1024 + $n) -Corrupt
    New-File -path (Join-Path $corruptDir "损坏文档-$n.docx") -sizeBytes (2048 + $n) -Corrupt
    $stats.corrupt += 2
}

# ⑤ 零字节文件
$zeroDir = Join-Path $Root 'zerobyte'
foreach ($n in 1..200) { New-File -path (Join-Path $zeroDir "空文件-$n.txt") -Zero; $stats.zero++ }

# ⑥ 只读权限：只读属性文件 + 只读目录
$roDir = Join-Path $Root 'readonly'
New-Item -ItemType Directory -Path $roDir -Force | Out-Null
foreach ($n in 1..100) {
    $p = Join-Path $roDir "只读文件-$n.txt"
    New-File -path $p -sizeBytes 256
    Set-ItemProperty -Path $p -Name IsReadOnly -Value $true
    $stats.readonly++
}
$roSub = Join-Path $roDir '只读目录'
New-Item -ItemType Directory -Path $roSub -Force | Out-Null
New-File -path (Join-Path $roSub '目录级只读.txt') -sizeBytes 256
Set-ItemProperty -Path $roSub -Name IsReadOnly -Value $true

# ⑦ 隐藏/系统属性
$hiddenDir = Join-Path $Root 'hidden'
foreach ($n in 1..50) {
    $p = Join-Path $hiddenDir "隐藏文件-$n.txt"
    New-File -path $p -sizeBytes 128
    Set-ItemProperty -Path $p -Name Attributes -Value ([System.IO.FileAttributes]::Hidden)
    $stats.hidden++
}

# ⑧ 同名多目录（同名不同内容，探命中聚合与路径区分）
foreach ($d in 1..20) {
    $dir = Join-Path $Root "samename\branch$('{0:d2}' -f $d)"
    New-File -path (Join-Path $dir '同名文件.txt') -sizeBytes (128 + $d)
    $stats.samename++
}

# ⑨ 多批搜索语料（G2-32 同构：命中超 BatchSize=200 逼流式多帧路径）
$batchDir = Join-Path $Root 'batch'
foreach ($n in 1..300) { New-File -path (Join-Path $batchDir "batch-$n.txt") -sizeBytes 64; $stats.batch++ }

# ⑩ 双分隔符口径语料：文件名内含反斜杠形近字符（Windows 不允许真实反斜杠，用全角＼与连续下划线近似探解析）
$sepDir = Join-Path $Root 'separators'
foreach ($n in @('a__b.txt', 'a＼b.txt', 'a___b.txt', 'a＼＼b.txt')) {
    New-File -path (Join-Path $sepDir $n) -sizeBytes 64
}

$sw.Stop()
$total = ($stats.Values | Measure-Object -Sum).Sum
$manifestBody = @{
    root = $Root
    created = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    elapsedSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    fileCountTarget = $FileCount
    stats = $stats
    note = 'v3.22 十一.6 真实环境镜像模板：几十万级真实文件+脏数据（超长路径/奇怪字符/损坏/只读/隐藏/同名/多批语料/双分隔符口径）'
} | ConvertTo-Json
$manifestBody | Out-File -FilePath $manifest -Encoding utf8
Write-Host "[mirror] 完成：$total 个文件，耗时 $([math]::Round($sw.Elapsed.TotalSeconds,0))s，manifest=$manifest"
