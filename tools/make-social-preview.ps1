Add-Type -AssemblyName System.Drawing
# Картинка для превью ссылок (GitHub Settings → Social preview, og:image сайта): docs/social-preview.png, 1280×640.
# Запуск: powershell -File tools/make-social-preview.ps1
$repo = Split-Path $PSScriptRoot -Parent
$W = 1280; $H = 640
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'

$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $W, $H),
    ([System.Drawing.Color]::FromArgb(255, 18, 22, 34)), ([System.Drawing.Color]::FromArgb(255, 34, 22, 48))
$g.FillRectangle($bg, 0, 0, $W, $H)

# Скриншот справа, со скруглённой рамкой
$shot = [System.Drawing.Image]::FromFile("$repo\docs\images\screenshot.png")
$sw = 640; $sh = [int]($shot.Height * $sw / $shot.Width)
$sx = $W - $sw - 40; $sy = [int](($H - $sh) / 2)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 18
$path.AddArc($sx, $sy, $r, $r, 180, 90); $path.AddArc($sx + $sw - $r, $sy, $r, $r, 270, 90)
$path.AddArc($sx + $sw - $r, $sy + $sh - $r, $r, $r, 0, 90); $path.AddArc($sx, $sy + $sh - $r, $r, $r, 90, 90); $path.CloseFigure()
$g.SetClip($path); $g.DrawImage($shot, $sx, $sy, $sw, $sh); $g.ResetClip()
$g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), 2), $path)

# Иконка и текст слева
$icon = [System.Drawing.Image]::FromFile("$repo\src\AdbManager\Assets\AppIcon.png")
$g.DrawImage($icon, 60, 110, 120, 120)
$white = [System.Drawing.Brushes]::White
$soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 205, 210, 225))
$accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 222, 160, 240))
$g.DrawString("ADB Manager", (New-Object System.Drawing.Font "Segoe UI Semibold", 46), $white, 52, 250)
$fmt = New-Object System.Drawing.StringFormat
$g.DrawString("Убрать рекламу и лишние приложения с Android TV, Google TV и смартфона — без root", (New-Object System.Drawing.Font "Segoe UI", 19), $soft, (New-Object System.Drawing.RectangleF 58, 335, 500, 100), $fmt)
$g.DrawString("Debloat Android TV & phones from Windows", (New-Object System.Drawing.Font "Segoe UI", 16), $accent, 58, 440)
$g.DrawString("Windows 10/11 · ADB GUI · open source · MIT", (New-Object System.Drawing.Font "Segoe UI", 14), $soft, 58, 500)

$bmp.Save("$repo\docs\social-preview.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $shot.Dispose(); $icon.Dispose()
"ok"
