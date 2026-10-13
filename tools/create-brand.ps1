$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$talkBrandRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\package\Manifest'))
New-Item -ItemType Directory -Force -Path $talkBrandRoot | Out-Null
function Draw-TalkBrand([int]$width, [int]$height, [string]$name) {
    $bitmap = New-Object Drawing.Bitmap($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $background = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(21, 28, 36))
    $accent = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(48, 182, 186))
    $white = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(230, 239, 243))
    $muted = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(142, 163, 173))
    $font = New-Object Drawing.Font('Segoe UI', 52, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
    $titleFont = New-Object Drawing.Font('Segoe UI', 34, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
    $subtitleFont = New-Object Drawing.Font('Segoe UI', 18, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    $format = New-Object Drawing.StringFormat
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear($background.Color)
        $graphics.FillRectangle($accent, 0, 0, $width, 8)
        $x = if ($width -eq $height) { 44 } else { 52 }
        $y = if ($width -eq $height) { 62 } else { 98 }
        $graphics.FillRectangle($accent, $x, $y, 168, 118)
        $points = [Drawing.Point[]]@([Drawing.Point]::new($x + 26, $y + 116), [Drawing.Point]::new($x + 26, $y + 140), [Drawing.Point]::new($x + 52, $y + 116))
        $graphics.FillPolygon($accent, $points)
        $format.Alignment = [Drawing.StringAlignment]::Center
        $format.LineAlignment = [Drawing.StringAlignment]::Center
        $graphics.DrawString('FT', $font, $background, [Drawing.RectangleF]::new($x, $y, 168, 118), $format)
        if ($width -ne $height) {
            $graphics.DrawString('FreeWorldTalk', $titleFont, $white, [Drawing.PointF]::new(250, 104))
            $graphics.DrawString('GROUND DIALOGUE', $subtitleFont, $muted, [Drawing.PointF]::new(252, 158))
            $graphics.DrawString('PLAYER / CHAT / MEMORY', $subtitleFont, $muted, [Drawing.PointF]::new(252, 190))
            $graphics.DrawString('HARMONY MOD + DEEPSEEK', $subtitleFont, $white, [Drawing.PointF]::new(52, 292))
        }
        $bitmap.Save((Join-Path $talkBrandRoot $name), [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $format.Dispose(); $subtitleFont.Dispose(); $titleFont.Dispose(); $font.Dispose()
        $muted.Dispose(); $white.Dispose(); $accent.Dispose(); $background.Dispose()
        $graphics.Dispose(); $bitmap.Dispose()
    }
}
Draw-TalkBrand 256 256 'icon.png'
Draw-TalkBrand 640 360 'preview.png'
