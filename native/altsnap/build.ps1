param(
    [string]$AltSnapSource = "Tools/AltSnap",
    [string]$Output = "assets/tools/altsnap"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Join-Path $root $AltSnapSource
$build = Join-Path $PSScriptRoot "build"
$out = Join-Path $root $Output

if (-not (Test-Path (Join-Path $source "hooks.c"))) {
    throw "找不到 AltSnap 源码：$source（先 clone RamonUnch/AltSnap v1.68）"
}

if (Test-Path $build) { Remove-Item -Recurse -Force $build }
New-Item -ItemType Directory -Path $build | Out-Null
Copy-Item (Join-Path $source "*") $build -Recurse -Force
Copy-Item -Path (Join-Path $PSScriptRoot "altsnap_host.c") -Destination (Join-Path $build "altsnap.c") -Force
Copy-Item -Path (Join-Path $PSScriptRoot "languages_host.c") -Destination (Join-Path $build "languages.c") -Force

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "找不到 vswhere.exe，请安装 Visual Studio Build Tools" }
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "找不到 MSVC x64 工具链" }
$devCmd = Join-Path $vsPath "Common7\Tools\VsDevCmd.bat"

$bat = @"
@echo off
call "$devCmd" -arch=x64 -host_arch=x64
cd /d "$build"
rc altsnap.rc
cl /c altsnap.c /nologo /O2 /Oi /Os /Gy /utf-8 /MT /DUNICODE /D_UNICODE /DTOOLDOCK_HOST
link /DLL altsnap.obj altsnap.res /entry:DllMain /out:altsnap_host.dll kernel32.lib user32.lib shell32.lib advapi32.lib gdi32.lib comctl32.lib ole32.lib oleaut32.lib uuid.lib shlwapi.lib winmm.lib libcmt.lib libucrt.lib libvcruntime.lib
rc hooks.rc
cl /c hooks.c /nologo /O2 /Oi /Os /Gy /GS- /LD /utf-8 /MT /DUNICODE /D_UNICODE
link hooks.obj hooks.res /DLL /subsystem:windows /entry:DllMain /out:hooks.dll kernel32.lib user32.lib shell32.lib gdi32.lib ole32.lib oleaut32.lib uuid.lib libcmt.lib libucrt.lib libvcruntime.lib
"@
$batPath = Join-Path $build "build.bat"
Set-Content -Path $batPath -Value $bat -Encoding ASCII
& $batPath
if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }

New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item (Join-Path $build "altsnap_host.dll") $out -Force
Copy-Item (Join-Path $build "hooks.dll") $out -Force
Copy-Item (Join-Path $source "License.txt") $out -Force
New-Item -ItemType Directory -Path (Join-Path $out "Lang") -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot "Lang\zh_CN.ini") -Destination (Join-Path $out "Lang\zh_CN.ini") -Force
Write-Host "输出：$out"
