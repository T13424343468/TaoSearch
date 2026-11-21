# TaoSearch MSIX 打包脚本（M1 前置试跑版）
# 用法：pwsh scripts/pack-msix.ps1 -ProjectRoot <仓库根> -OutputDir <输出目录>
# 说明：
#   - 本脚本在 Windows 环境运行（需 Windows SDK 的 makeappx 或单项目 MSIX 打包）；
#     沙箱 Linux 只做语法校验，真实打包在云端 runner / 真机验证（见 docs/待真机验证清单.md）。
#   - 零第三方依赖令：仅用 Windows SDK 自带工具与 .NET SDK，不引第三方打包库。
#   - 签名：M1 阶段产出未签名 MSIX（自用安装走开发者模式侧载）；正式签名属敏感操作，另行授权。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = (Join-Path $ProjectRoot "artifacts\msix"),

    [Parameter(Mandatory = $false)]
    [string]$MakeAppxPath = ""
)

$ErrorActionPreference = "Stop"

# ---- 前置检查 ----
$serviceProject = Join-Path $ProjectRoot "src\TaoSearch.Service\TaoSearch.Service.csproj"
if (-not (Test-Path $serviceProject)) {
    throw "找不到服务工程：$serviceProject"
}

# makeappx 探测：优先用外部传入路径（CI 按需取用），否则找 Windows SDK
$makeappx = $null
if ($MakeAppxPath -and (Test-Path $MakeAppxPath)) { $makeappx = $MakeAppxPath }
if (-not $makeappx) {
    $makeappx = Get-Command makeappx.exe -ErrorAction SilentlyContinue
    if ($makeappx) { $makeappx = $makeappx.Source }
}
if (-not $makeappx) {
    $sdkRoots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin") |
        Where-Object { $_ -and (Test-Path $_) }
    if ($sdkRoots) {
        $candidate = Get-ChildItem $sdkRoots[0] -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\makeappx.exe" } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($candidate) { $makeappx = $candidate }
    }
}
if (-not $makeappx) {
    throw "未找到 makeappx.exe（需 Windows SDK）。真机/云端 runner 安装 Windows SDK 后重试。"
}

# ---- 发布服务工程（框架依赖 win-x64：体积优先，第一优先级） ----
$publishDir = Join-Path $OutputDir "publish"
dotnet publish $serviceProject -c Release -r win-x64 --self-contained false `
    -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }

# ---- 安装器运行时新标准（2026-11-21 老板令）：打包产物附运行时保障脚本，安装期执行 ----
# 口径：包不内置运行时；装时全盘检测（注册表+统一目录），无则联网下载官方运行时装进
# 系统统一 .NET 目录；无网给说人话重试指引。详见 scripts/EnsureRuntime.ps1。
$ensureRuntime = Join-Path $PSScriptRoot "EnsureRuntime.ps1"
if (-not (Test-Path $ensureRuntime)) { throw "找不到运行时保障脚本：$ensureRuntime" }
Copy-Item -Path $ensureRuntime -Destination (Join-Path $publishDir "EnsureRuntime.ps1") -Force

# ---- AppxManifest（未签名侧载用；包名与版本号对齐版本台账） ----
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
  <Identity Name="Tao.TaoSearch.Service"
            Publisher="CN=Tao"
            Version="0.1.0.4" />
  <Properties>
    <DisplayName>TaoSearch Service</DisplayName>
    <PublisherDisplayName>Tao</PublisherDisplayName>
    <Logo>assets\logo.png</Logo>
  </Properties>
  <Resources>
    <Resource Language="zh-cn" />
  </Resources>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
  <Applications>
    <Application Id="TaoSearchService"
                 Executable="TaoSearch.Service.exe"
                 EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="TaoSearch Service"
                          Square150x150Logo="assets\logo.png"
                          Square44x44Logo="assets\logo.png"
                          Description="TaoSearch 本地搜索服务"
                          BackgroundColor="transparent" />
    </Application>
  </Applications>
</Package>
'@

$staging = Join-Path $OutputDir "staging"
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Set-Content -Path (Join-Path $staging "AppxManifest.xml") -Value $manifest -Encoding UTF8
# assets 必须位于 staging\assets\ 子目录（清单引用 assets\logo.png）
New-Item -ItemType Directory -Force -Path (Join-Path $staging "assets") | Out-Null
Copy-Item -Path (Join-Path $ProjectRoot "artifacts\msix\assets\*") -Destination (Join-Path $staging "assets") -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $publishDir "*") -Destination $staging -Recurse -Force

# ---- 打包前诊断：staging 内容与关键文件存在性 ----
Write-Host "---- staging 内容 ----"
Get-ChildItem $staging -Recurse | ForEach-Object { Write-Host $_.FullName.Substring($staging.Length) }
Write-Host "TaoSearch.Service.exe 存在：$(Test-Path (Join-Path $staging 'TaoSearch.Service.exe'))"
Write-Host "assets\logo.png 存在：$(Test-Path (Join-Path $staging 'assets\logo.png'))"

# ---- 打包 MSIX（未签名） ----
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$msixPath = Join-Path $OutputDir "TaoSearch.Service-0.1.0.4-unsigned.msix"
& $makeappx pack /d $staging /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack 失败（exit $LASTEXITCODE）" }

$size = (Get-Item $msixPath).Length
Write-Host "MSIX 打包完成：$msixPath（$size 字节）"
Write-Host "注意：未签名包需开发者模式侧载；签名与安装验证见待真机清单。"
