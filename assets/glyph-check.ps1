# 渲染侧边栏字形候选，输出 PNG 供人工检查
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$glyphs = @(
    @{ code = 0xE80F; label = 'E80F home?' },
    @{ code = 0xE702; label = 'E702 battery?' },
    @{ code = 0xE74D; label = 'E74D clean?' },
    @{ code = 0xE713; label = 'E713 settings?' },
    @{ code = 0xE946; label = 'E946 info?' },
    @{ code = 0xEA8A; label = 'EA8A home-alt?' },
    @{ code = 0xE10F; label = 'E10F section?' }
)
$fonts = @('Segoe Fluent Icons', 'Segoe MDL2 Assets')

$cell = 72
$bmp = New-Object System.Drawing.Bitmap(($cell * $glyphs.Count), ($cell * $fonts.Count + 30))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

for ($fi = 0; $fi -lt $fonts.Count; $fi++) {
    $font = New-Object System.Drawing.Font($fonts[$fi], 34)
    $g.DrawString($fonts[$fi], (New-Object System.Drawing.Font('Segoe UI', 9)), [System.Drawing.Brushes]::Gray, 2, ($cell * $fi + 2))
    for ($gi = 0; $gi -lt $glyphs.Count; $gi++) {
        $code = $glyphs[$gi].code
        $charStr = [char]$code
        $g.DrawString($charStr, $font, [System.Drawing.Brushes]::Black, ($cell * $gi + 18), ($cell * $fi + 16))
        $g.DrawString($glyphs[$gi].label, (New-Object System.Drawing.Font('Segoe UI', 7)), [System.Drawing.Brushes]::DarkGray, ($cell * $gi + 2), ($cell * ($fi + 1) - 14))
    }
    $font.Dispose()
}
$g.Dispose()
$out = "D:\WorkSpace\Agent\Projects\ToolDock\assets\glyph-check.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved: $out"
