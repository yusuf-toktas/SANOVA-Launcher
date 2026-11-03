<#
.SYNOPSIS
  Regenerates the SANOVA application icon (src/Sanova.Launcher/Assets/Branding/sanova.ico).

.DESCRIPTION
  Draws the SANOVA mark — a sun setting behind a horizon with three road lines, on a dusk
  tile — at every standard Windows icon size and packs the PNGs into a single .ico file.
  The same geometry is drawn at runtime by Controls/LogoMark.cs.
  Requires Windows PowerShell 5.1 (System.Drawing / GDI+).
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\src\Sanova.Launcher\Assets\Branding\sanova.ico'),
    [string]$PngOutput = (Join-Path $PSScriptRoot '..\src\Sanova.Launcher\Assets\Branding\sanova-256.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Render-Mark([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $inset = [Math]::Max(0.5, $s * 0.02)
    $tile = New-RoundedRect $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ($s * 0.22)
    $tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(255, 0x34, 0x18, 0x40)), ([System.Drawing.Color]::FromArgb(255, 0x14, 0x0E, 0x1B))
    $g.FillPath($tileBrush, $tile)

    # Sun, clipped at the horizon.
    $horizon = $s * 0.60
    $radius = $s * 0.25
    $cx = $s * 0.5
    $cy = $horizon
    $g.SetClip((New-Object System.Drawing.RectangleF 0, 0, $s, $horizon))
    $sunRect = New-Object System.Drawing.RectangleF ($cx - $radius), ($cy - $radius), ($radius * 2), ($radius * 2)
    $sunBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $sunRect, ([System.Drawing.Color]::FromArgb(255, 0xFF, 0xD9, 0x86)), ([System.Drawing.Color]::FromArgb(255, 0xFF, 0x7E, 0x4A)), ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillEllipse($sunBrush, $sunRect)
    $g.ResetClip()

    # Road lines below the horizon, narrowing toward the viewer's eye.
    $amber = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xFF, 0xAA, 0x4D))
    $lineHeight = [Math]::Max(1.0, $s * 0.045)
    $widths = @(0.56, 0.38, 0.20)
    for ($i = 0; $i -lt 3; $i++) {
        $w = $s * $widths[$i]
        $y = $horizon + $s * 0.075 + $i * $s * 0.095
        $line = New-RoundedRect ($cx - $w / 2) $y $w $lineHeight ([Math]::Min($lineHeight / 2, $w / 2))
        $g.FillPath($amber, $line)
    }

    $g.Dispose()
    return $bitmap
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($size in $sizes) {
    $bmp = Render-Mark $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save($PngOutput, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $images += , @($size, $stream.ToArray())
}

# ICO container: header, directory entries, PNG payloads.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $size = $image[0]; $data = $image[1]
    $dimension = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$data.Length); $writer.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image[1]) }
$writer.Flush()
New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
[System.IO.File]::WriteAllBytes($Output, $out.ToArray())
Write-Host "Wrote $Output ($($images.Count) sizes)"
