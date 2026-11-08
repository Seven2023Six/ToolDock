# 从 box.png 生成 ToolDock 全套图标资产：
#   assets/app.ico        多尺寸(16/24/32/48/256, PNG 压缩条目) —— exe 图标 + WPF 窗口图标
#   assets/icon16.bgra    16x16 原始 BGRA —— Agent 托盘
#   assets/icon32.bgra    32x32 原始 BGRA —— Agent 托盘(高 DPI)
param(
    # 源图：任意方形 PNG（示例：assets/box.png）
    [Parameter(Mandatory = $true)][string]$Source,
    # 输出目录，默认脚本所在目录（assets/）
    [string]$OutDir = $PSScriptRoot
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile($Source)
Write-Host ("源图: {0}x{1}" -f $src.Width, $src.Height)

function Resize-To([System.Drawing.Image]$srcImg, [int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $scale = [Math]::Min($s / $srcImg.Width, $s / $srcImg.Height)
    $w = [int][Math]::Round($srcImg.Width * $scale)
    $h = [int][Math]::Round($srcImg.Height * $scale)
    $x = [int](($s - $w) / 2)
    $y = [int](($s - $h) / 2)
    $g.DrawImage($srcImg, $x, $y, $w, $h)
    $g.Dispose()
    return $bmp
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ── 1) 多尺寸 .ico（PNG 压缩条目，Vista+ 支持）──
$sizes = 16, 24, 32, 48, 256
$entries = @()
foreach ($s in $sizes) {
    $bmp = Resize-To $src $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $entries += , @($s, $ms.ToArray())
}
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)
$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type = icon
$bw.Write([uint16]$entries.Count)    # count
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $s = $e[0]; $data = $e[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)            # width
    $bw.Write([byte]$dim)            # height
    $bw.Write([byte]0)               # palette
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # planes
    $bw.Write([uint16]32)            # bpp
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($e in $entries) { $bw.Write($e[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "app.ico"), $ico.ToArray())
Write-Host ("app.ico: {0} bytes, {1} 尺寸" -f $ico.Length, $entries.Count)

# ── 2) 托盘用原始 BGRA（顶行在下 → 与负高 DIB 一致）──
foreach ($s in 16, 32) {
    $bmp = Resize-To $src $s
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $bd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($s * $s * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($bd)
    $bmp.Dispose()
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir ("icon{0}.bgra" -f $s)), $bytes)
    Write-Host ("icon{0}.bgra: {1} bytes" -f $s, $bytes.Length)
}
$src.Dispose()
Write-Host "完成"
