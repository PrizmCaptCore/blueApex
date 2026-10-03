# Draws the BlueApex icon (a blue mountain peak) at every size Windows asks for and packs
# the PNGs into .ico files. No image editor or third-party asset involved, so nothing to license.
#   .\build\make-icon.ps1   ->  src\BlueApex\Assets\blueapex.ico  (app, installer, Start menu: white peak on a blue tile)
#                               src\BlueApex\Assets\tray.ico      (tray: blue peak on transparent)
#                               build\out\icon-preview-*.png      (to look at)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root "src\BlueApex\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null
New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot "out") | Out-Null

$blue = [System.Drawing.Color]::FromArgb(255, 0x2D, 0x7B, 0xE5)
$blueDark = [System.Drawing.Color]::FromArgb(255, 0x1F, 0x5F, 0xBF)
$white = [System.Drawing.Color]::White

# The mountain: a tall main peak with a lower shoulder to its right, as fractions of the size.
function PeakPoints([double]$s, [double]$inset) {
    $pts = @(
        @(0.08, 0.84), @(0.40, 0.20), @(0.55, 0.50), @(0.66, 0.36), @(0.92, 0.84)
    )
    $pts | ForEach-Object {
        # inset squeezes the shape towards the centre so it sits inside the tile's padding
        $x = 0.5 + ($_[0] - 0.5) * (1 - $inset); $y = 0.5 + ($_[1] - 0.5) * (1 - $inset)
        New-Object System.Drawing.PointF ($x * $s), ($y * $s)
    }
}

function RoundedRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

function Draw([int]$size, [bool]$tile) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.PixelOffsetMode = "HighQuality"
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($tile) {
        $r = $size * 0.22
        $rect = RoundedRect 0 0 $size $size $r
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, $size), $blue, $blueDark
        $g.FillPath($brush, $rect)
        $g.FillPolygon((New-Object System.Drawing.SolidBrush $white), [System.Drawing.PointF[]](PeakPoints $size 0.18))
    } else {
        # No tile: the peak itself is the icon, in blue, filling the box.
        $g.FillPolygon((New-Object System.Drawing.SolidBrush $blue), [System.Drawing.PointF[]](PeakPoints $size 0.0))
    }
    $g.Dispose()
    $bmp
}

function PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , [byte[]]$ms.ToArray()   # the comma keeps PowerShell from unrolling the byte array
}

# ICO container: header, one directory entry per image, then the PNG payloads (PNG-in-ICO is fine on Vista+).
function WriteIco([string]$path, [int[]]$sizes, [bool]$tile) {
    $images = foreach ($s in $sizes) { $bmp = Draw $s $tile; [byte[]]$png = PngBytes $bmp; $bmp.Dispose(); @{ Size = $s; Png = $png } }
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$img.Png.Length); $w.Write([uint32]$offset)
        $offset += $img.Png.Length
    }
    foreach ($img in $images) { $w.Write([byte[]]$img.Png) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    "$path ($($ms.Length) bytes, sizes $($sizes -join ','))"
}

WriteIco (Join-Path $assets "blueapex.ico") @(16, 20, 24, 32, 40, 48, 64, 128, 256) $true
WriteIco (Join-Path $assets "tray.ico") @(16, 20, 24, 32, 48, 64) $false

# Previews to look at.
foreach ($s in 16, 48, 256) {
    $b = Draw $s $true; $b.Save((Join-Path $PSScriptRoot "out\icon-preview-tile-$s.png")); $b.Dispose()
    $b = Draw $s $false; $b.Save((Join-Path $PSScriptRoot "out\icon-preview-tray-$s.png")); $b.Dispose()
}
"previews in build\out"
