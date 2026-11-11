param(
    [switch]$SkipPackage,
    [switch]$Start,
    [string]$Configuration = "Release"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Stop-ToolDockProcesses {
    Get-Process ToolDock -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process ToolDock.Agent -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction Stop }
        catch { Write-Output ("agent " + $_.Id + " needs elevation") }
    }
    Start-Sleep -Milliseconds 400
    $remaining = Get-Process ToolDock.Agent -ErrorAction SilentlyContinue
    if ($remaining) {
        Write-Output "requesting elevation to stop remaining agent processes"
        Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\taskkill.exe') -ArgumentList '/IM ToolDock.Agent.exe /F' -Verb RunAs -Wait
        Start-Sleep -Milliseconds 600
    }
}

Write-Output "== stop running instances =="
Stop-ToolDockProcesses

Write-Output "== publish agent (NativeAOT) =="
dotnet publish src/ToolDock.Agent/ToolDock.Agent.csproj -c $Configuration -r win-x64 -o publish
if ($LASTEXITCODE -ne 0) { throw "agent publish failed" }

Write-Output "== publish app (WPF) =="
dotnet publish src/ToolDock.App/ToolDock.App.csproj -c $Configuration -o publish/app
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

$required = @(
    'publish/ToolDock.Agent.exe',
    'publish/app/ToolDock.exe',
    'publish/app/tools/mas/MAS_AIO.cmd',
    'publish/tools/altsnap/altsnap_host.dll'
)
foreach ($relative in $required) {
    if (-not (Test-Path $relative)) { throw ("missing " + $relative) }
}
Write-Output "== artifacts =="
foreach ($relative in $required) {
    $item = Get-Item $relative
    $hash = (Get-FileHash $item.FullName -Algorithm SHA256).Hash
    Write-Output ("{0}  {1:N0} bytes  {2}" -f $item.Name, $item.Length, $hash)
}

if (-not $SkipPackage) {
    Write-Output "== package zip =="
    python scripts/package-win-x64.py
    if ($LASTEXITCODE -ne 0) { throw "package failed" }
}

if ($Start) {
    Write-Output "== start =="
    Start-Process (Join-Path $root 'publish/ToolDock.Agent.exe')
    Start-Sleep -Seconds 3
    Start-Process (Join-Path $root 'publish/app/ToolDock.exe') -ArgumentList '--background'
}

Write-Output "build-release: done"
