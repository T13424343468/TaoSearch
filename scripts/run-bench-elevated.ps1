# 管理员权限真卷基准（方案 A 云端验证：计划任务 RunLevel Highest 免 UAC 提权）
# 由 benchmark.yml 以计划任务方式调用；输出 TRX 与运行日志到 ResultsDir。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DotNetExe,
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$ResultsDir
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

$log = Join-Path $ResultsDir "bench-elevated.log"
& $DotNetExe test TaoSearch.Kernel.slnx -c Release --no-build --nologo `
    --filter "Category=Benchmark" `
    --logger "trx;LogFileName=taosearch-bench.trx" `
    --results-directory $ResultsDir *>&1 | Tee-Object -FilePath $log
exit $LASTEXITCODE
