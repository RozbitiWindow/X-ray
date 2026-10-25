# Generates the X-RAY application icon (multi-resolution .ico with PNG frames) and a 256 px PNG.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\XRay.App\Assets'))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded charcoal tile
    $r = [Math]::Max(2, [int]($size * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $m = [Math]::Max(0, [int]($size * 0.03))
    $w = $size - 2 * $m
    $path.AddArc($m, $m, $d, $d, 180, 90)
    $path.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $path.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90)
    $path.AddArc($m, $m + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(0, $size)), ([System.Drawing.Color]::FromArgb(255, 30, 34, 41)), ([System.Drawing.Color]::FromArgb(255, 17, 19, 23))
    $g.FillPath($bg, $path)
    if ($size -ge 32) {
        $border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 52, 58, 68)), ([Math]::Max(1, $size / 64))
        $g.DrawPath($border, $path)
    }

    # Cyan X
    $cyan = [System.Drawing.Color]::FromArgb(255, 34, 195, 230)
    $stroke = [Math]::Max(2, $size * 0.13)
    $pen = New-Object System.Drawing.Pen $cyan, $stroke
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $a = $size * 0.30; $b = $size * 0.70
    $g.DrawLine($pen, $a, $a, $b, $b)
    $g.DrawLine($pen, $b, $a, $a, $b)

    # Scan line
    if ($size -ge 32) {
        $scan = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 210, 245, 255)), ([Math]::Max(1, $size / 48))
        $g.DrawLine($scan, $size * 0.16, $size * 0.5, $size * 0.84, $size * 0.5)
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $OutDir 'xray-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $frames += , @($s, $ms.ToArray())
}

$out = New-Object System.IO.MemoryStream
$wr = New-Object System.IO.BinaryWriter $out
$wr.Write([UInt16]0); $wr.Write([UInt16]1); $wr.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $data = $f[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $wr.Write([byte]$dim); $wr.Write([byte]$dim); $wr.Write([byte]0); $wr.Write([byte]0)
    $wr.Write([UInt16]1); $wr.Write([UInt16]32)
    $wr.Write([UInt32]$data.Length); $wr.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $wr.Write([byte[]]$f[1]) }
$wr.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'xray.ico'), $out.ToArray())
Write-Output "Icon written to $OutDir"
