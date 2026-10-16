<#
.SYNOPSIS
  Dawnshard 私服 Windows 一键部署脚本（Phase 0 + Phase 1）
  - 在 E 盘创建工作区
  - 通过 winget 安装 Git / .NET SDK 10 / Docker Desktop（已装则跳过）
  - 克隆 Dawnshard 仓库
  - 临时关闭 Website（避免 Node.js 依赖，可用 -WithWebsite 保留）
  - 可选：自动启动 Aspire AppHost（-Launch）

.DESCRIPTION
  用管理员 PowerShell 运行：
    powershell -ExecutionPolicy Bypass -File .\setup-dawnshard-windows.ps1 -Launch

  脚本可重复运行（幂等），中断后直接重跑即可。
  国内网络 clone 慢时：-RepoUrl 参数可换成你常用的 GitHub 镜像地址。

.NOTES
  - Git / .NET SDK / Docker Desktop 是系统级组件，Windows 要求安装到 C 盘（系统盘），
    E 盘工作区用于存放仓库源码和运行数据。
  - Docker Desktop 首次安装后可能需要重启电脑完成 WSL2 初始化。
#>

param(
    # E 盘工作区根目录
    [string]$WorkspaceRoot = "E:\DawnshardWorkspace",
    # 仓库地址（国内可换成镜像）
    [string]$RepoUrl = "https://github.com/SapiensAnatis/Dawnshard.git",
    # 保留 Website 启用（需要 Node.js + pnpm，默认关闭）
    [switch]$WithWebsite,
    # 完成后自动在新窗口启动 Aspire AppHost
    [switch]$Launch
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Refresh-Path {
    $env:Path =
        [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
        [System.Environment]::GetEnvironmentVariable("Path", "User")
}

function Get-DotnetExe {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $programFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    if (Test-Path $programFiles) { return $programFiles }

    return $null
}

function Invoke-WingetInstall {
    param([string]$Id)

    Write-Host "winget 安装: $Id ..." -ForegroundColor Yellow
    winget install --id $Id -e --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget 安装 $Id 失败（exit code $LASTEXITCODE）。可手动安装后重跑本脚本。"
    }
    Refresh-Path
}

# ============================================================
Write-Step "0/6 检查工作盘与权限"
# ============================================================
$driveLetter = $WorkspaceRoot.Substring(0, 3)
if (-not (Test-Path $driveLetter)) {
    throw "找不到磁盘 $driveLetter ，请确认 E 盘存在，或用 -WorkspaceRoot 指定其他位置。"
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Warning "当前不是管理员权限。安装 Docker Desktop / .NET SDK 时可能弹 UAC 确认框，建议用管理员 PowerShell 重跑。"
}

New-Item -ItemType Directory -Force -Path $WorkspaceRoot | Out-Null
Write-Host "工作区: $WorkspaceRoot"

# ============================================================
Write-Step "1/6 检查 winget"
# ============================================================
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw "未找到 winget。请先在 Microsoft Store 安装『应用安装程序 (App Installer)』，重开 PowerShell 后再运行本脚本。"
}

# ============================================================
Write-Step "2/6 安装 Git / .NET SDK 10 / Docker Desktop"
# ============================================================
# --- Git ---
if (Get-Command git -ErrorAction SilentlyContinue) {
    Write-Host "Git 已安装，跳过"
} else {
    Invoke-WingetInstall -Id "Git.Git"
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Git 安装后仍不可用。请关闭本窗口，重开一个管理员 PowerShell 再运行本脚本。"
}

# --- .NET SDK 10 ---
$dotnet = Get-DotnetExe
$hasSdk10 = $false
if ($dotnet) {
    $sdks = (& $dotnet --list-sdks 2>$null) -join "`n"
    $hasSdk10 = $sdks -match "(?m)^10\.\d+\.\d+"
}
if ($hasSdk10) {
    Write-Host ".NET 10 SDK 已安装，跳过"
} else {
    Invoke-WingetInstall -Id "Microsoft.DotNet.SDK.10"
}
$dotnet = Get-DotnetExe
if (-not $dotnet) { throw "未找到 dotnet，请重开 PowerShell 后重跑本脚本。" }

$sdkLine = (& $dotnet --list-sdks) -join "; "
Write-Host "已安装 SDK: $sdkLine"
if ($sdkLine -notmatch "(?m)^10\.") {
    throw "未检测到 .NET 10 SDK（仓库 global.json 要求 >= 10.0.100）。请手动安装后重跑。"
}

# --- Docker Desktop ---
$dockerExe = $null
if (Get-Command docker -ErrorAction SilentlyContinue) {
    $dockerExe = (Get-Command docker).Source
} elseif (Test-Path "$env:ProgramFiles\Docker\Docker\resources\bin\docker.exe") {
    $dockerExe = "$env:ProgramFiles\Docker\Docker\resources\bin\docker.exe"
}

if ($dockerExe) {
    Write-Host "Docker 已安装，跳过"
} else {
    Invoke-WingetInstall -Id "Docker.DockerDesktop"
    $dockerExe = "$env:ProgramFiles\Docker\Docker\resources\bin\docker.exe"
    if (-not (Test-Path $dockerExe)) { $dockerExe = $null }
}

# ============================================================
Write-Step "3/6 确保 Docker 引擎就绪"
# ============================================================
$dockerReady = $false
if ($dockerExe) {
    # 拉起 Docker Desktop（若未运行）
    $dockerDesktop = "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe"
    $ddRunning = Get-Process -Name "Docker Desktop" -ErrorAction SilentlyContinue
    if ((Test-Path $dockerDesktop) -and (-not $ddRunning)) {
        Write-Host "启动 Docker Desktop ..."
        Start-Process $dockerDesktop | Out-Null
    }

    Write-Host "等待 Docker 引擎就绪（最多 5 分钟）..."
    for ($i = 0; $i -lt 60; $i++) {
        & $dockerExe info *> $null
        if ($LASTEXITCODE -eq 0) { $dockerReady = $true; break }
        Start-Sleep -Seconds 5
    }
}

if ($dockerReady) {
    Write-Host "Docker 引擎已就绪" -ForegroundColor Green
} else {
    Write-Warning "Docker 引擎暂未就绪。常见原因："
    Write-Warning "  1) 首次安装 Docker Desktop 需要重启电脑完成 WSL2 初始化 -> 重启后重跑本脚本"
    Write-Warning "  2) 需要先手动打开 Docker Desktop 接受服务条款 -> 打开后重跑本脚本"
    Write-Warning "可以先继续 clone 源码，稍后再启动服务。"
}

# ============================================================
Write-Step "4/6 获取 Dawnshard 源码"
# ============================================================
$repoDir = Join-Path $WorkspaceRoot "Dawnshard"
if (Test-Path (Join-Path $repoDir ".git")) {
    Write-Host "仓库已存在，跳过 clone: $repoDir"
} elseif (Test-Path $repoDir) {
    throw "$repoDir 已存在但不是 git 仓库，请手动删除或改名后重跑。"
} else {
    Write-Host "克隆 $RepoUrl ..."
    git clone $RepoUrl $repoDir
    if ($LASTEXITCODE -ne 0) {
        throw "git clone 失败。国内网络慢时可用 -RepoUrl 换镜像地址，或给 git 配置代理后重跑。"
    }
}

# ============================================================
Write-Step "5/6 配置 AppHost"
# ============================================================
$appsettings = Join-Path $repoDir "Aspire\Dawnshard.AppHost\appsettings.json"
if (-not (Test-Path $appsettings)) { throw "找不到 $appsettings" }

if ($WithWebsite) {
    Write-Host "保留 Website 启用（需要已安装 Node.js + pnpm）"
} else {
    $raw = Get-Content $appsettings -Raw
    if ($raw -match '"EnableWebsite":\s*true') {
        $raw = $raw -replace '"EnableWebsite":\s*true', '"EnableWebsite": false'
        Set-Content -Path $appsettings -Value $raw -Encoding UTF8
        Write-Host "已临时关闭 Website（避免 Node.js 依赖）。以后装好 Node/pnpm 可用 -WithWebsite 重跑改回。"
    } else {
        Write-Host "Website 已是关闭状态"
    }
}

# ============================================================
Write-Step "6/6 完成"
# ============================================================
$launchHint = "cd `"$repoDir\Aspire\Dawnshard.AppHost`"; dotnet run --launch-profile http"
Write-Host ""
Write-Host "工作区位置 : $WorkspaceRoot"
Write-Host "仓库位置   : $repoDir"
Write-Host "启动命令   : $launchHint" -ForegroundColor Green
Write-Host ""
Write-Host "首次 dotnet run 需要还原 NuGet 包并构建，大约几分钟，请耐心等待。"
Write-Host "启动成功后浏览器会打开 Aspire 仪表盘，确认 dragalia-api / postgres / redis 都是 Running。"
Write-Host "记下仪表盘里 dragalia-api 的 http 端点（手机 Dragalipatch 要填它，localhost 换成本机局域网 IP）。"

if ($Launch) {
    if ($dockerReady) {
        Write-Host ""
        Write-Host "正在新窗口启动 AppHost ..." -ForegroundColor Yellow
        Start-Process powershell -ArgumentList "-NoExit", "-Command", $launchHint
    } else {
        Write-Warning "Docker 未就绪，跳过自动启动。Docker 就绪后手动运行上面的启动命令。"
    }
}
