# TaoSearch 应用启动诊断: 内嵌清单提取 + (可选) sxstrace 采集启动 + 宽事件日志
# 用途: 定位 ERROR_SXS_CANT_GEN_ACTCTX (0x800736B1) 的确切缺失依赖
# 用法: powershell -File diag-app.ps1 -ExePath <exe路径> [-WorkingDir <工作目录>] [-Start]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$WorkingDir = "",
    [switch]$Start
)
$ErrorActionPreference = 'Continue'
function Log([string]$m) { Write-Host $m }

if (-not (Test-Path $ExePath)) { Log "诊断目标不存在: $ExePath"; exit 1 }
Log ("诊断目标: {0} ({1:N0} 字节)" -f $ExePath, (Get-Item $ExePath).Length)

# ---- 内嵌清单 (RT_MANIFEST) 全文: SxS 失败时核对清单引用的依赖程序集 ----
$manifestReader = @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ManifestReader {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibraryEx(string name, IntPtr h, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr FindResource(IntPtr h, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr LoadResource(IntPtr h, IntPtr res);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr LockResource(IntPtr res);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern uint SizeofResource(IntPtr h, IntPtr res);
    public static string Read(string path) {
        IntPtr lib = LoadLibraryEx(path, IntPtr.Zero, 0x2);
        if (lib == IntPtr.Zero) return "LoadLibraryEx failed: " + Marshal.GetLastWin32Error();
        try {
            IntPtr res = FindResource(lib, (IntPtr)1, (IntPtr)24);
            if (res == IntPtr.Zero) return "(no RT_MANIFEST)";
            IntPtr ptr = LockResource(LoadResource(lib, res));
            uint size = SizeofResource(lib, res);
            byte[] buf = new byte[size];
            Marshal.Copy(ptr, buf, 0, (int)size);
            return Encoding.UTF8.GetString(buf);
        } finally { FreeLibrary(lib); }
    }
}
"@
Add-Type -TypeDefinition $manifestReader
Log "内嵌清单(RT_MANIFEST)全文:"
[ManifestReader]::Read($ExePath) -split "`r?`n" | ForEach-Object { if ($_.Trim()) { Log ("  | " + $_.Trim()) } }

if (-not $Start) { exit 0 }

# ---- sxstrace 采集: 拿激活上下文失败的确切依赖项 ----
$etl = Join-Path (Get-Location) "sxs-trace.etl"
$txt = Join-Path (Get-Location) "sxs-trace.txt"
$procName = [System.IO.Path]::GetFileNameWithoutExtension($ExePath)
$traceProc = $null
try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $env:ComSpec
    $psi.Arguments = "/c sxstrace trace -logfile:`"$etl`""
    $psi.RedirectStandardInput = $true
    $psi.UseShellExecute = $false
    $traceProc = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds 2
    Log "sxstrace 采集中"
} catch {
    Log ("sxstrace 启动失败(不影响验证): " + $_.Exception.Message)
}

# ---- 启动尝试 ----
$started = $false
$proc = $null
try {
    $wd = if ($WorkingDir) { $WorkingDir } else { Split-Path $ExePath }
    $proc = Start-Process -FilePath $ExePath -WorkingDirectory $wd -PassThru -ErrorAction Stop
    $started = $true
    Log ("进程已创建, Id=" + $proc.Id)
} catch {
    Log ("启动异常: " + $_.Exception.Message)
}
Start-Sleep -Seconds 8
$alive = Get-Process -Name $procName -ErrorAction SilentlyContinue
if ($alive) {
    Log "进程 8 秒后仍存活 (核心页可达)"
    Stop-Process -Name $procName -Force -ErrorAction SilentlyContinue
} elseif ($started) {
    try { Log ("进程已创建但 8 秒内退出, 退出码: " + $proc.ExitCode) } catch { Log "进程已创建但 8 秒内退出" }
    Log "(GUI 在无人会话可能无法存活, 需真机复核)"
} else {
    Log "进程未能创建 (SxS 错误实锤)"
}

# ---- 停止采集并解析 ----
if ($traceProc -and -not $traceProc.HasExited) {
    try {
        $traceProc.StandardInput.WriteLine()
        $traceProc.WaitForExit(10000) | Out-Null
    } catch { Log ("sxstrace 停止异常: " + $_.Exception.Message) }
}
if (Test-Path $etl) {
    cmd /c "sxstrace parse -logfile:`"$etl`" -outfile:`"$txt`"" 2>&1 | ForEach-Object { Log ("sxstrace parse: $_") }
    if (Test-Path $txt) {
        Log "sxstrace 解析结果:"
        Get-Content $txt | ForEach-Object { Log ("  | " + $_) }
    } else {
        Log "sxstrace 解析结果文件未生成"
    }
} else {
    Log "sxstrace 采集文件未生成"
}

# ---- 宽事件日志: 最近 20 条 Application 事件 (含 SxS/应用错误详情) ----
Log "Application 事件日志最近 20 条:"
Get-WinEvent -LogName Application -MaxEvents 20 -ErrorAction SilentlyContinue | ForEach-Object {
    $msg = if ($_.Message) { ($_.Message -replace "\s+", " ") } else { "(无正文)" }
    if ($msg.Length -gt 300) { $msg = $msg.Substring(0, 300) }
    Log ("  [" + $_.TimeCreated.ToString("HH:mm:ss") + "][" + $_.ProviderName + "] " + $msg)
}
