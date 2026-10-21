param([string]$OutDir)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Фон: скруглённый квадрат с диагональным градиентом (зелёный Android → бирюзовый)
    $m = [Math]::Max(0.5, $s * 0.03)
    $bg = RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 61, 220, 132)), ([System.Drawing.Color]::FromArgb(255, 0, 137, 123))
    $g.FillPath($grad, $bg)

    # Телефон: белый корпус и тёмный экран
    $pw = $s * 0.46; $ph = $s * 0.70
    $px = ($s - $pw) / 2; $py = ($s - $ph) / 2
    $g.FillPath([System.Drawing.Brushes]::White, (RoundRect $px $py $pw $ph ($s * 0.08)))
    $b = [Math]::Max(1, $s * 0.045)
    $sx = $px + $b; $sy = $py + $b * 1.6; $sw = $pw - 2 * $b; $sh = $ph - $b * 3.2
    $screen = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 18, 64, 58))
    $g.FillPath($screen, (RoundRect $sx $sy $sw $sh ([Math]::Max(0.5, $s * 0.035))))

    # Стрелка «установить» на экране
    $cx = $s / 2
    $arrow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 61, 220, 132))
    $stemW = $sw * 0.26
    $headW = $sw * 0.72
    $top = $sy + $sh * 0.16
    $headTop = $sy + $sh * 0.46
    $tip = $sy + $sh * 0.74
    $g.FillRectangle($arrow, $cx - $stemW / 2, $top, $stemW, $headTop - $top + 0.5)
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($cx - $headW / 2), $headTop),
        (New-Object System.Drawing.PointF ($cx + $headW / 2), $headTop),
        (New-Object System.Drawing.PointF $cx, $tip))
    $g.FillPolygon($arrow, $pts)
    if ($s -ge 32) {
        # Полочка под стрелкой
        $lineH = [Math]::Max(1, $sh * 0.06)
        $g.FillRectangle($arrow, $cx - $headW / 2, $sy + $sh * 0.82, $headW, $lineH)
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = Draw $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @($s, $ms.ToArray())
    if ($s -eq 256) { $bmp.Save((Join-Path $OutDir 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($s -eq 64) { $bmp.Save((Join-Path $OutDir 'AppIcon-64.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO-контейнер с PNG-кадрами
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'AppIcon.ico'), $out.ToArray())
"ok"
