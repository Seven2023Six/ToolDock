# 管道链路自测：hello → welcome → setTheme → getSettings（最终还原为 Light）
$ErrorActionPreference = 'Stop'
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'tooldock-agent', [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
$pipe.Connect(3000)

function SendMsg($obj) {
    $json = [System.Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Compress))
    $frame = New-Object byte[] (4 + $json.Length)
    [Array]::Copy([BitConverter]::GetBytes([int]$json.Length), $frame, 4)
    [Array]::Copy($json, 0, $frame, 4, $json.Length)
    $pipe.Write($frame, 0, $frame.Length)
    $pipe.Flush()
}
function RecvMsg {
    $lenBuf = New-Object byte[] 4
    $read = 0
    while ($read -lt 4) {
        $n = $pipe.Read($lenBuf, $read, 4 - $read)
        if ($n -le 0) { throw 'pipe closed' }
        $read += $n
    }
    $len = [BitConverter]::ToInt32($lenBuf, 0)
    $buf = New-Object byte[] $len
    $read = 0
    while ($read -lt $len) {
        $n = $pipe.Read($buf, $read, $len - $read)
        if ($n -le 0) { throw 'pipe closed' }
        $read += $n
    }
    [System.Text.Encoding]::UTF8.GetString($buf) | ConvertFrom-Json
}

SendMsg @{ t = 'hello'; ver = 1 }
$w = RecvMsg
Write-Host ("welcome: duplicate={0} theme={1}" -f $w.duplicate, $w.theme)

SendMsg @{ t = 'setTheme'; theme = 'Dark' }
Start-Sleep -Milliseconds 600
SendMsg @{ t = 'getSettings' }
$s1 = RecvMsg
Write-Host ("after setTheme Dark -> agent replies theme={0}" -f $s1.theme)

SendMsg @{ t = 'setTheme'; theme = 'Light' }
Start-Sleep -Milliseconds 600

$pipe.Dispose()
$settings = Get-Content "$env:APPDATA\ToolDock\settings.json" | ConvertFrom-Json
Write-Host ("settings.json on disk: theme={0}" -f $settings.theme)
Write-Host 'PIPE TEST OK'
