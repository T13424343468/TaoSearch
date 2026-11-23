# 录屏包装进程（2026-11-24 首轮录屏损坏教训：Stop-Process -Force 杀 ffmpeg 丢 moov atom）
# 职责：启动 ffmpeg 录屏 → 监视停止旗标 → 出现后向 ffmpeg stdin 写 q 优雅收尾，超时才强杀。
param(
    [Parameter(Mandatory=$true)][string]$OutPath,
    [Parameter(Mandatory=$true)][string]$StopFlag
)
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'ffmpeg'
$psi.Arguments = "-y -f gdigrab -framerate 2 -i desktop -c:v libx264 -preset ultrafast -pix_fmt yuv420p `"$OutPath`""
$psi.RedirectStandardInput = $true
$psi.UseShellExecute = $false
$proc = [System.Diagnostics.Process]::Start($psi)
$proc.Id | Out-File "$StopFlag.pid" -Encoding ascii
while (-not (Test-Path $StopFlag)) { Start-Sleep -Milliseconds 500 }
try { $proc.StandardInput.Write('q') } catch { }
if (-not $proc.WaitForExit(20000)) { try { $proc.Kill() } catch { } }
