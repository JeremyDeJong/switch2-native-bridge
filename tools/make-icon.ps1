# Draws the app icon (a rounded blue tile with "S2") at every size Windows asks for
# and writes them as PNG frames into assets\app.ico.
#
# Usage: powershell -NoProfile -File tools\make-icon.ps1

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "assets\app.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$tile = [System.Drawing.Color]::FromArgb(255, 15, 108, 189)   # Windows blue
$edge = [System.Drawing.Color]::FromArgb(255, 11, 84, 150)

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $inset = [Math]::Max(0.5, $size * 0.03)
    $w = $size - 2 * $inset
    $r = $w * 0.24
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($inset, $inset, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($inset + $w - 2 * $r, $inset, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($inset + $w - 2 * $r, $inset + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($inset, $inset + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $tile), $path)
    if ($size -ge 32) {
        $g.DrawPath((New-Object System.Drawing.Pen $edge, ([Math]::Max(1, $size / 64))), $path)
    }

    $fontSize = $size * 0.46
    if ($size -le 24) {
        # Tiny sizes: bigger, pixel-snapped letters stay readable in the tray and Explorer.
        $fontSize = $size * 0.56
    }
    $font = New-Object System.Drawing.Font "Segoe UI Variable Display", $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    if ($font.Name -ne "Segoe UI Variable Display") {
        $font = New-Object System.Drawing.Font "Segoe UI", $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    }
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF 0, ($size * 0.02), $size, $size
    $g.DrawString("S2", $font, [System.Drawing.Brushes]::White, $rect, $format)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # Large frames as PNG, as Windows expects.
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small frames as a 32-bit DIB: header with doubled height, BGRA rows
        # bottom-up, then an all-zero AND mask (alpha does the masking).
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$size); $bw.Write([Int32]($size * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $bw.Write([UInt32]0); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [Math]::Ceiling($size / 32) * 4
        $bw.Write((New-Object byte[] ($maskRow * $size)))
        $bw.Flush()
    }
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$frames = foreach ($s in $sizes) { ,(New-Frame $s) }

# ICO container: header, one directory entry per frame, then the PNG data.
$stream = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $stream
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$frames[$i].Length); $w.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[System.IO.File]::WriteAllBytes($out, $stream.ToArray())
Write-Output "Wrote $out ($($sizes.Count) sizes)"
