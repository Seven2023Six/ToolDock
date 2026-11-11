param(
    [string]$AgentPath = "",
    [switch]$SkipStart
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrEmpty($AgentPath)) { $AgentPath = Join-Path $root 'publish\ToolDock.Agent.exe' }

function Write-Frame($pipe, $json) {
    $payload = [System.Text.Encoding]::UTF8.GetBytes($json)
    $length = [BitConverter]::GetBytes([int]$payload.Length)
    $pipe.Write($length, 0, 4)
    $pipe.Write($payload, 0, $payload.Length)
    $pipe.Flush()
}

function Read-Frame($pipe) {
    $header = New-Object byte[] 4
    $read = 0
    while ($read -lt 4) {
        $n = $pipe.Read($header, $read, 4 - $read)
        if ($n -le 0) { return $null }
        $read += $n
    }
    $len = [BitConverter]::ToInt32($header, 0)
    if ($len -lt 1 -or $len -gt 1000000) { return $null }
    $body = New-Object byte[] $len
    $read = 0
    while ($read -lt $len) {
        $n = $pipe.Read($body, $read, $len - $read)
        if ($n -le 0) { return $null }
        $read += $n
    }
    return [System.Text.Encoding]::UTF8.GetString($body)
}

function Test-Field($json, $name) {
    $obj = $json | ConvertFrom-Json
    if ($obj.PSObject.Properties.Name -contains $name) { return $true }
    return $false
}

$agentRunning = $null -ne (Get-Process ToolDock.Agent -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $agentRunning) {
    if ($SkipStart) { Write-Output 'FAIL: agent not running (and -SkipStart)'; exit 1 }
    if (-not (Test-Path $AgentPath)) { Write-Output ("FAIL: agent not found: " + $AgentPath); exit 1 }
    Write-Output ("starting agent: " + $AgentPath)
    Start-Process $AgentPath
    Start-Sleep -Seconds 4
}

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'tooldock-agent', [System.IO.Pipes.PipeDirection]::InOut)
try { $pipe.Connect(3000) } catch { Write-Output 'FAIL: cannot connect to pipe'; exit 1 }

$failed = $false
try {
    Write-Frame $pipe '{"t":"hello"}'
    $welcome = Read-Frame $pipe
    if ($null -eq $welcome) { Write-Output 'FAIL: no welcome frame'; exit 1 }
    $w = $welcome | ConvertFrom-Json
    if ($w.t -ne 'welcome') { Write-Output ('FAIL: unexpected welcome type ' + $w.t); exit 1 }
    if ($w.duplicate) {
        Write-Output 'SKIP: another ToolDock window is attached; close it and rerun'
        exit 2
    }
    Write-Output ('PASS: handshake (ver=' + $w.ver + ', mask=0x' + ('{0:X}' -f [int]$w.mask) + ')')

    Write-Frame $pipe '{"t":"getSettings"}'
    $settings = Read-Frame $pipe
    if ($null -eq $settings) { Write-Output 'FAIL: no settings reply'; $failed = $true }
    else {
        $s = $settings | ConvertFrom-Json
        if ($s.t -ne 'settings') { Write-Output ('FAIL: settings type ' + $s.t); $failed = $true }
        else { Write-Output ('PASS: getSettings (threshold=' + $s.threshold + ', autoClean=' + $s.autoClean + ')') }
    }

    Write-Frame $pipe '{"t":"getAltSnap"}'
    $alt = Read-Frame $pipe
    if ($null -eq $alt) { Write-Output 'FAIL: no altSnap reply'; $failed = $true }
    elseif (-not (Test-Field $alt 'altSnapEnabled')) { Write-Output 'FAIL: altSnap field missing'; $failed = $true }
    else { Write-Output 'PASS: getAltSnap' }

    Write-Frame $pipe '{"t":"getInputTip"}'
    $tip = Read-Frame $pipe
    if ($null -eq $tip) { Write-Output 'FAIL: no inputTip reply'; $failed = $true }
    elseif (-not (Test-Field $tip 'inputTipEnabled')) { Write-Output 'FAIL: inputTip field missing'; $failed = $true }
    else { Write-Output 'PASS: getInputTip' }
}
finally {
    $pipe.Dispose()
}

if ($failed) { Write-Output 'SMOKE: FAILED'; exit 1 }
Write-Output 'SMOKE: OK'
exit 0
