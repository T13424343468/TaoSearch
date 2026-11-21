# TaoSearch 一键侧载安装（自用）
# 用法：右键"以管理员身份运行"本脚本，或在管理员 PowerShell 中执行：
#   pwsh -ExecutionPolicy Bypass -File .\install-sideload.ps1
# 做什么：导入随包证书 → 安装 MSIX → 冒烟确认；全程中文提示，失败给通俗指引。
[CmdletBinding()]
param(
    [string]$MsixPath = "",
    [string]$CerPath = ""
)
$ErrorActionPreference = "Stop"

function Say($msg) { Write-Host "[TaoSearch] $msg" -ForegroundColor Cyan }

# ---- 管理员检查 ----
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Say "需要管理员权限（装证书要用）。请右键本脚本 →『以管理员身份运行』，或在管理员 PowerShell 里重跑。"
    exit 1
}

# ---- 定位文件：优先参数，其次脚本同目录，再次同目录安装包子目录 ----
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $MsixPath) {
    $cand = @(Get-ChildItem $here -Filter *.msix -ErrorAction SilentlyContinue) +
             @(Get-ChildItem (Join-Path $here "安装包") -Filter *.msix -ErrorAction SilentlyContinue)
    $MsixPath = ($cand | Select-Object -First 1).FullName
}
if (-not $CerPath) {
    $cand = @(Get-ChildItem $here -Filter *.cer -ErrorAction SilentlyContinue) +
             @(Get-ChildItem (Join-Path $here "安装包") -Filter *.cer -ErrorAction SilentlyContinue)
    $CerPath = ($cand | Select-Object -First 1).FullName
}
if (-not ($MsixPath -and (Test-Path $MsixPath))) {
    Say "没找到 MSIX 安装包。请把本脚本放在安装包（.msix）旁边再运行，或用 -MsixPath 指定路径。"
    exit 1
}
Say "安装包：$MsixPath"

# ---- 导入证书到本机受信任人员 ----
if ($CerPath -and (Test-Path $CerPath)) {
    try {
        Import-Certificate -FilePath $CerPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
        Say "证书已导入本机受信任列表。"
    } catch {
        Say "证书导入失败：$($_.Exception.Message)"
        Say "如果之前导入过同名证书可忽略本条；否则请把错误截图发我。"
    }
} else {
    Say "未找到 .cer 证书文件，将尝试直接安装（若报 0x800B010A，请确认 .cer 与 .msix 在同一目录）。"
}

# ---- 安装 ----
try {
    Add-AppxPackage -Path $MsixPath -ErrorAction Stop
    Say "安装完成！可在开始菜单搜『TaoSearch』启动。"
} catch {
    $msg = $_.Exception.Message
    Say "安装失败：$msg"
    if ($msg -match "0x800B010A") {
        Say "这是证书没装上导致的。请确认 .cer 文件在安装包旁边，然后重跑本脚本。"
    } elseif ($msg -match "0x80073D06|已安装|already") {
        Say "系统里已有同包名的新版本，先卸载旧版（设置→应用→搜 TaoSearch）再重跑。"
    } else {
        Say "把上面的错误信息截图发我，我来查。"
    }
    exit 1
}

# ---- 冒烟：确认包在册 ----
$pkg = Get-AppxPackage -Name "Tao.TaoSearch.Service" -ErrorAction SilentlyContinue
if ($pkg) { Say "确认在册：版本 $($pkg.Version)，安装位置 $($pkg.InstallLocation)" }
