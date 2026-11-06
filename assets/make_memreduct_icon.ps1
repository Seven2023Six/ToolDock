# Build memreduct icon (multi-size .ico) from a source PNG.
# Usage: powershell -File make_memreduct_icon.ps1 [-Source <png>] [-Out <ico>]
#   -Source omitted -> draw a placeholder memory-chip icon procedurally.
# NOTE: keep this file ASCII-only (PowerShell 5.1 mis-parses BOM-less UTF-8 CJK comments).
param(
    [string]$Source = "",
    [string]$Out = "D:\WorkSpace\Agent\Projects\ToolDock\Tools\memreduct\src\res\100.ico"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$canvas = 256
$master = New-Object System.Drawing.Bitmap($canvas, $canvas)

if ($Source -ne "") {
    $img = [System.Drawing.Image]::FromFile($Source)
    $g = [System.Drawing.Graphics]::FromImage($master)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $scale = [Math]::Min($canvas / $img.Width, $canvas / $img.Height)
    $w = [int][Math]::Round($img.Width * $scale)
    $h = [int][Math]::Round($img.Height * $scale)
    $g.DrawImage($img, [int](($canvas - $w) / 2), [int](($canvas - $h) / 2), $w, $h)
    $g.Dispose()
    $img.Dispose()
}
else {
    # Placeholder: memory chip outline + cleaning sparkle
    $ink = [System.Drawing.Color]::FromArgb(255, 32, 32, 32)
    $accent = [System.Drawing.Color]::FromArgb(255, 0, 120, 212)
    $g = [System.Drawing.Graphics]::FromImage($master)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    $bodyPen = New-Object System.Drawing.Pen($ink, 18)
    $bodyPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $chip = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 26; $x = 56; $y = 56; $w = 144; $h = 144; $d = $r * 2
    $chip.AddArc($x, $y, $d, $d, 180, 90)
    $chip.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $chip.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $chip.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $chip.CloseFigure()
    $g.DrawPath($bodyPen, $chip)

    $pinPen = New-Object System.Drawing.Pen($ink, 14)
    $pinPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pinPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($p in 92, 128, 164) {
        $g.DrawLine($pinPen, $p, 34, $p, 56)      # top
        $g.DrawLine($pinPen, $p, 200, $p, 222)    # bottom
        $g.DrawLine($pinPen, 34, $p, 56, $p)      # left
        $g.DrawLine($pinPen, 200, $p, 222, $p)    # right
    }

    # sparkle (cleaning) at upper-right
    $sparkle = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cx = 196; $cy = 60; $ro = 22; $ri = 6
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx, ($cy - $ro)),
        [System.Drawing.PointF]::new(($cx + $ri), ($cy - $ri)),
        [System.Drawing.PointF]::new(($cx + $ro), $cy),
        [System.Drawing.PointF]::new(($cx + $ri), ($cy + $ri)),
        [System.Drawing.PointF]::new($cx, ($cy + $ro)),
        [System.Drawing.PointF]::new(($cx - $ri), ($cy + $ri)),
        [System.Drawing.PointF]::new(($cx - $ro), $cy),
        [System.Drawing.PointF]::new(($cx - $ri), ($cy - $ri))
    )
    $sparkle.AddPolygon($pts)
    $sparkleBrush = New-Object System.Drawing.SolidBrush($accent)
    $g.FillPath($sparkleBrush, $sparkle)

    $g.Dispose()
    $bodyPen.Dispose(); $pinPen.Dispose()
}

# ---- write multi-size ICO (PNG-compressed entries) ----
$sizes = 16, 24, 32, 48, 256
$entries = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($master, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $entries += , @($s, $ms.ToArray())
}
$master.Dispose()

$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $s = $e[0]; $data = $e[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($e in $entries) { $bw.Write($e[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Out, $ico.ToArray())
Write-Host ("wrote {0} ({1} bytes, {2} sizes)" -f $Out, $ico.Length, $entries.Count)
