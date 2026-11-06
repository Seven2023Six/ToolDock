# Capture the ToolDock main window - pure managed (no Add-Type P/Invoke, AV blocks csc temp DLLs).
# NOTE: keep this file ASCII-only - PowerShell 5.1 mis-parses BOM-less UTF-8 Chinese comments as GBK.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName Microsoft.VisualBasic

$p = Get-Process ToolDock -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "no ToolDock window" }

[Microsoft.VisualBasic.Interaction]::AppActivate($p.Id)
Start-Sleep -Milliseconds 400

$ae = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$rect = $ae.Current.BoundingRectangle
Write-Host ("window rect: {0},{1} {2}x{3}" -f [int]$rect.X, [int]$rect.Y, [int]$rect.Width, [int]$rect.Height)

$bmp = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bmp.Size)
$g.Dispose()
$out = "D:\WorkSpace\Agent\Projects\ToolDock\assets\window-capture.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved: $out"
