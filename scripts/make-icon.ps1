# Generates src/VirtualStreamDeck/Assets/app.ico: a dark rounded tile holding a 3x3 key grid
# with one accent key. PNG-compressed entries at 16..256 px. ASCII only (PowerShell 5.1).
param([string]$Out = "")
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\VirtualStreamDeck\Assets\app.ico' }
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(0.5, $r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = [Math]::Max(0.5, $s * 0.04)
    $tile = New-RoundRect $pad $pad ($s - 2 * $pad) ($s - 2 * $pad) ($s * 0.2)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(255, 36, 40, 48)), ([System.Drawing.Color]::FromArgb(255, 20, 22, 27))
    $g.FillPath($bg, $tile)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 58, 63, 75)), ([Math]::Max(1, $s / 64))
    $g.DrawPath($pen, $tile)

    $n = 3
    $inner = $s * 0.66
    $gap = $inner * 0.09
    $k = ($inner - $gap * ($n - 1)) / $n
    $ox = ($s - $inner) / 2
    for ($r = 0; $r -lt $n; $r++) {
        for ($c = 0; $c -lt $n; $c++) {
            $x = $ox + $c * ($k + $gap)
            $y = $ox + $r * ($k + $gap)
            $key = New-RoundRect $x $y $k $k ($k * 0.24)
            if ($r -eq 0 -and $c -eq 0) { $col = [System.Drawing.Color]::FromArgb(255, 59, 130, 246) }
            elseif ($r -eq 1 -and $c -eq 2) { $col = [System.Drawing.Color]::FromArgb(255, 239, 68, 68) }
            else { $col = [System.Drawing.Color]::FromArgb(255, 70, 76, 90) }
            $b = New-Object System.Drawing.SolidBrush $col
            $g.FillPath($b, $key)
            $b.Dispose(); $key.Dispose()
        }
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($s in $sizes) { $images += ,(New-IconPng $s) }

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    if ($s -ge 256) { $bw.Write([byte]0); $bw.Write([byte]0) } else { $bw.Write([byte]$s); $bw.Write([byte]$s) }
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "Wrote $Out"
