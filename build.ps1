#Requires -Version 5.1
<#
  TomatoFocus 构建脚本
  ---------------------------------------------------------------
  用 .NET SDK 自带的 Roslyn (csc.dll) 直接编译到 .NET Framework 4.8，
  产出单个零依赖 exe（Win10/11 自带 4.8 运行时），不联网、无 NuGet、无第三方库。
  应用图标由程序自身程序化绘制生成（--make-icon），仓库里不需要任何图片资源。

  目录约定：
    dist\          交付目录 —— 只有 TomatoFocus.exe（+ app.ico、assets、说明）
    build\dev\     开发产物 —— 测试 exe、界面快照、演示图

  用法:
    powershell -File build.ps1            编译（产出 dist\TomatoFocus.exe）
    powershell -File build.ps1 -Run       编译并启动
    powershell -File build.ps1 -Test      编译并运行测试
    powershell -File build.ps1 -Sheet     编译并渲染界面快照
    powershell -File build.ps1 -Clean     清理 dist 与 build
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Test,
    [switch]$Sheet,
    [switch]$Clean,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$srcDir    = Join-Path $root 'src'
$testDir   = Join-Path $root 'tests'
$langDir   = Join-Path $root 'Lang'
$assetsDir = Join-Path $root 'assets'
$distDir   = Join-Path $root 'dist'
$devDir    = Join-Path $root 'build\dev'
$exePath   = Join-Path $distDir 'TomatoFocus.exe'
$iconPath  = Join-Path $distDir 'app.ico'
$testExe   = Join-Path $devDir 'TomatoFocus.Tests.exe'
$sheetDir  = Join-Path $devDir 'snapshots'
$manifest  = Join-Path $root 'app.manifest'

if ($Clean) {
    foreach ($d in @($distDir, (Join-Path $root 'build'))) {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    }
    Write-Host '已清理 dist 与 build 目录。' -ForegroundColor DarkGray
    if (-not ($Run -or $Test -or $Sheet)) { return }
}
foreach ($d in @($distDir, $devDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# --- 定位工具链 -------------------------------------------------------------
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) { throw '未找到 dotnet。请安装 .NET SDK。' }
$dotnet     = $dotnetCmd.Source
$dotnetRoot = Split-Path $dotnet -Parent
$sdkRoot    = Join-Path $dotnetRoot 'sdk'
$sdk = Get-ChildItem $sdkRoot -Directory -ErrorAction SilentlyContinue |
       Sort-Object { [version]($_.Name -replace '[^0-9\.].*$','') } -Descending |
       Select-Object -First 1
if (-not $sdk) { throw "未在 $sdkRoot 找到 .NET SDK。" }
$csc = Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll'
if (-not (Test-Path $csc)) { throw "未找到 Roslyn 编译器: $csc" }

$fwDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path $fwDir)) { throw "未找到 .NET Framework 4.x: $fwDir" }

# --- 引用程序集（全部来自系统内置 .NET Framework） --------------------------
$refNames = @(
    'mscorlib.dll',
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Xml.dll'
)
$refs = @()
foreach ($n in $refNames) {
    $p = Join-Path $fwDir $n
    if (-not (Test-Path $p)) { throw "缺少引用程序集: $p" }
    $refs += "-r:$p"
}

# --- 嵌入式资源（语言包） ---------------------------------------------------
$resources = @()
if (Test-Path $langDir) {
    foreach ($f in Get-ChildItem $langDir -Filter *.json) {
        $resources += "-resource:$($f.FullName),Lang.$($f.Name)"
    }
}

$common = @(
    '-nologo', '-nostdlib+', '-langversion:latest', '-platform:anycpu', '-optimize+', '-debug-',
    '-deterministic+', '-utf8output', '-codepage:65001', '-nowarn:0169,0649,0414,1591'
)

function Invoke-Csc {
    param([string[]]$Arguments, [string]$What)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $out = & $dotnet $csc @Arguments 2>&1
    $sw.Stop()
    $errors = $out | Where-Object { $_ -match ':\s*(error|错误)\s+[A-Z]+\d+' }
    $warns  = $out | Where-Object { $_ -match ':\s*(warning|警告)\s+[A-Z]+\d+' }
    if ($warns.Count -gt 0 -and -not $Quiet) {
        Write-Host "  警告 $($warns.Count) 条：" -ForegroundColor DarkYellow
        $warns | Select-Object -First 12 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }
    }
    if ($errors.Count -gt 0) {
        Write-Host "  $What 编译失败：" -ForegroundColor Red
        $errors | Select-Object -First 25 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        throw "$What 编译失败（$($errors.Count) 个错误）。"
    }
    return $sw.ElapsedMilliseconds
}

function New-AppExe {
    param([switch]$WithIcon)
    $a = @($common) + @('-target:winexe', "-out:$exePath")
    if (Test-Path $manifest) { $a += "-win32manifest:$manifest" }
    if ($WithIcon -and (Test-Path $iconPath)) { $a += "-win32icon:$iconPath" }
    $a += $refs + $resources + @("-recurse:$srcDir\*.cs")
    return Invoke-Csc -Arguments $a -What '主程序'
}

# --- 第一遍：无图标编译，用来生成程序化图标 ---------------------------------
if (-not (Test-Path $iconPath)) {
    $null = New-AppExe
    if (Test-Path $exePath) {
        $p = Start-Process -FilePath $exePath -ArgumentList '--make-icon', $iconPath -Wait -PassThru
        if (Test-Path $iconPath) { Write-Host '已生成程序化应用图标。' -ForegroundColor DarkGray }
    }
}

# --- 第二遍：带图标编译 -----------------------------------------------------
$ms = New-AppExe -WithIcon

# --- 交付目录里放上可选的资源接口与说明 -------------------------------------
if (Test-Path $assetsDir) {
    $dstAssets = Join-Path $distDir 'assets'
    if (-not (Test-Path $dstAssets)) { New-Item -ItemType Directory -Path $dstAssets -Force | Out-Null }
    Copy-Item (Join-Path $assetsDir '*') $dstAssets -Recurse -Force
}
$readme = Join-Path $distDir '使用说明.txt'
@"
番茄专注 TomatoFocus
========================================
本目录只需要 TomatoFocus.exe 即可运行：
  - 单文件，无需安装，无需额外运行时（Win10/11 自带 .NET Framework 4.8）
  - 双击启动；关闭窗口默认最小化到托盘
  - 托盘图标右键菜单提供全部功能（含极简模式、开机自启）

数据位置：%APPDATA%\TomatoFocus\data.json
可选美化：把 PNG 放进 assets\ 目录即可替换程序化绘制的图标（删掉即回退）
语言包覆盖：在 exe 同目录建 Lang\zh-CN.json 可覆盖内置文案

更多说明见项目根目录 README.md
"@ | Set-Content -Path $readme -Encoding UTF8

if (-not $Quiet) {
    $size = (Get-Item $exePath).Length
    Write-Host ("编译完成  dist\TomatoFocus.exe  {0:N0} 字节  {1} ms" -f $size, $ms) -ForegroundColor Green
}

# --- 编译并运行测试 ---------------------------------------------------------
if ($Test) {
    $testArgs = @($common) + @(
        '-target:exe', "-out:$testExe", '-main:TomatoFocus.Tests.TestMain', '-define:TOMATO_TESTS'
    )
    $testArgs += $refs + $resources + @("-recurse:$srcDir\*.cs", "-recurse:$testDir\*.cs")
    $null = Invoke-Csc -Arguments $testArgs -What '测试'
    Write-Host '运行测试...' -ForegroundColor Cyan
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "测试失败（退出码 $LASTEXITCODE）。" }
}

# --- 渲染 UI 快照 -----------------------------------------------------------
if ($Sheet) {
    if (-not (Test-Path $sheetDir)) { New-Item -ItemType Directory -Path $sheetDir -Force | Out-Null }
    Write-Host '渲染 UI 快照...' -ForegroundColor Cyan
    # GUI 子系统的 exe 必须用 Start-Process -Wait 等待，否则 PowerShell 会立刻返回
    $p = Start-Process -FilePath $exePath -ArgumentList '--sheet', $sheetDir -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "快照渲染失败（退出码 $($p.ExitCode)）。" }
    Write-Host "快照目录: $sheetDir" -ForegroundColor Green
}

if ($Run) {
    Write-Host '启动 TomatoFocus...' -ForegroundColor Cyan
    Start-Process -FilePath $exePath
}
