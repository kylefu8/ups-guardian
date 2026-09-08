<#!
.SYNOPSIS
    Build the UPS Guardian brand assets from the canonical vector geometry.

.DESCRIPTION
    This script intentionally has no network or image dependencies. It renders the
    SVG geometry through System.Drawing at 8x and downsamples it for clean edges,
    then writes PNG-backed Windows ICO files for the shell and notification area.
#>

[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$WorkDirectory
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\assets'
}
if ([string]::IsNullOrWhiteSpace($WorkDirectory)) {
    $WorkDirectory = Join-Path $PSScriptRoot '..\artifacts\icon-work'
}

Add-Type -AssemblyName System.Drawing

$script:Palette = @{
    Navy       = [System.Drawing.Color]::FromArgb(255, 11, 23, 38)       # #0B1726
    NavyBottom = [System.Drawing.Color]::FromArgb(255, 16, 38, 58)       # subtle depth
    Teal       = [System.Drawing.Color]::FromArgb(255, 20, 184, 166)      # #14B8A6
    TealTop    = [System.Drawing.Color]::FromArgb(255, 30, 201, 176)
    TealEdge   = [System.Drawing.Color]::FromArgb(255, 8, 126, 119)
    White      = [System.Drawing.Color]::FromArgb(255, 244, 247, 251)     # #F4F7FB
    LightPanel = [System.Drawing.Color]::FromArgb(255, 246, 249, 252)
    DarkPanel  = [System.Drawing.Color]::FromArgb(255, 7, 15, 26)
}

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

$OutputDirectory = Resolve-FullPath $OutputDirectory
$WorkDirectory = Resolve-FullPath $WorkDirectory
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null

function New-RoundedRectanglePath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = [float]($Radius * 2)
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconGeometry {
    param([float]$Scale = 1)

    $geometry = [PSCustomObject]@{
        Scale = $Scale
        TilePath = New-RoundedRectanglePath (12 * $Scale) (12 * $Scale) (232 * $Scale) (232 * $Scale) (54 * $Scale)
        ShieldPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        BoltPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    }

    $shield = $geometry.ShieldPath
    $shield.StartFigure()
    $shield.AddBezier((128 * $Scale), (39 * $Scale), (104 * $Scale), (54 * $Scale), (82 * $Scale), (61 * $Scale), (58 * $Scale), (66 * $Scale))
    $shield.AddLine((58 * $Scale), (66 * $Scale), (58 * $Scale), (119 * $Scale))
    $shield.AddBezier((58 * $Scale), (119 * $Scale), (58 * $Scale), (163 * $Scale), (82 * $Scale), (194 * $Scale), (128 * $Scale), (214 * $Scale))
    $shield.AddBezier((128 * $Scale), (214 * $Scale), (174 * $Scale), (194 * $Scale), (198 * $Scale), (163 * $Scale), (198 * $Scale), (119 * $Scale))
    $shield.AddLine((198 * $Scale), (119 * $Scale), (198 * $Scale), (66 * $Scale))
    $shield.AddBezier((198 * $Scale), (66 * $Scale), (174 * $Scale), (61 * $Scale), (152 * $Scale), (54 * $Scale), (128 * $Scale), (39 * $Scale))
    $shield.CloseFigure()

    $bolt = $geometry.BoltPath
    $bolt.StartFigure()
    $bolt.AddLine((144 * $Scale), (70 * $Scale), (102 * $Scale), (132 * $Scale))
    $bolt.AddLine((102 * $Scale), (132 * $Scale), (126 * $Scale), (132 * $Scale))
    $bolt.AddLine((126 * $Scale), (132 * $Scale), (113 * $Scale), (184 * $Scale))
    $bolt.AddLine((113 * $Scale), (184 * $Scale), (160 * $Scale), (118 * $Scale))
    $bolt.AddLine((160 * $Scale), (118 * $Scale), (137 * $Scale), (118 * $Scale))
    $bolt.AddLine((137 * $Scale), (118 * $Scale), (144 * $Scale), (70 * $Scale))
    $bolt.CloseFigure()

    return $geometry
}

function Draw-IconGeometry {
    param(
        [Parameter(Mandatory)][System.Drawing.Graphics]$Graphics,
        [Parameter(Mandatory)][int]$Size
    )

    $scale = [float]($Size / 256.0)
    $geometry = New-IconGeometry -Scale $scale

    $tileRectangle = New-Object -TypeName System.Drawing.RectangleF -ArgumentList @([float]0, [float]0, [float]$Size, [float]$Size)
    $tileBrush = New-Object -TypeName System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList @($tileRectangle, $script:Palette.Navy, $script:Palette.NavyBottom, [float]90)
    $Graphics.FillPath($tileBrush, $geometry.TilePath)

    # A quiet inner edge helps the tile survive dark taskbars while staying clean
    # at 16 px. It is deliberately below the shield and never competes with it.
    $edgePen = New-Object -TypeName System.Drawing.Pen -ArgumentList @($script:Palette.TealEdge, [float](1.25 * $scale))
    $edgePath = New-RoundedRectanglePath (13 * $scale) (13 * $scale) (230 * $scale) (230 * $scale) (53 * $scale)
    $Graphics.DrawPath($edgePen, $edgePath)

    $shieldRectangle = New-Object -TypeName System.Drawing.RectangleF -ArgumentList @([float]0, [float](39 * $scale), [float]$Size, [float](175 * $scale))
    $shieldBrush = New-Object -TypeName System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList @($shieldRectangle, $script:Palette.TealTop, $script:Palette.Teal, [float]90)
    $Graphics.FillPath($shieldBrush, $geometry.ShieldPath)

    $shieldPen = New-Object -TypeName System.Drawing.Pen -ArgumentList @($script:Palette.TealEdge, [float](1.5 * $scale))
    $Graphics.DrawPath($shieldPen, $geometry.ShieldPath)

    $boltBrush = New-Object -TypeName System.Drawing.SolidBrush -ArgumentList @($script:Palette.White)
    $Graphics.FillPath($boltBrush, $geometry.BoltPath)

    $boltBrush.Dispose()
    $shieldPen.Dispose()
    $shieldBrush.Dispose()
    $edgePath.Dispose()
    $edgePen.Dispose()
    $tileBrush.Dispose()
    $geometry.TilePath.Dispose()
    $geometry.ShieldPath.Dispose()
    $geometry.BoltPath.Dispose()
}

function New-RenderedIcon {
    param(
        [Parameter(Mandatory)][int]$Size,
        [int]$RenderScale = 8
    )

    $hiSize = [int]($Size * $RenderScale)
    $hi = New-Object System.Drawing.Bitmap($hiSize, $hiSize, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $hiGraphics = [System.Drawing.Graphics]::FromImage($hi)
    $hiGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $hiGraphics.Clear([System.Drawing.Color]::Transparent)
    $hiGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $hiGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $hiGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    Draw-IconGeometry -Graphics $hiGraphics -Size $hiSize
    $hiGraphics.Dispose()

    $final = New-Object System.Drawing.Bitmap($Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $finalGraphics = [System.Drawing.Graphics]::FromImage($final)
    $finalGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $finalGraphics.Clear([System.Drawing.Color]::Transparent)
    $finalGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $finalGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $finalGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $finalGraphics.DrawImage($hi, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)), 0, 0, $hiSize, $hiSize, [System.Drawing.GraphicsUnit]::Pixel)
    $finalGraphics.Dispose()
    $hi.Dispose()
    return $final
}

function Convert-BitmapToPngBytes {
    param([Parameter(Mandatory)][System.Drawing.Bitmap]$Bitmap)
    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    # Unary comma keeps the byte array as one pipeline object in Windows PowerShell.
    return ,$bytes
}

function Save-BitmapPng {
    param(
        [Parameter(Mandatory)][System.Drawing.Bitmap]$Bitmap,
        [Parameter(Mandatory)][string]$Path
    )
    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Save-PngIco {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int[]]$Sizes
    )

    $frames = @()
    try {
        foreach ($size in $Sizes) {
            $bitmap = New-RenderedIcon -Size $size
            try {
                $pngBytes = Convert-BitmapToPngBytes -Bitmap $bitmap
                $frames += [PSCustomObject]@{ Size = $size; Bytes = [byte[]]$pngBytes }
            }
            finally {
                $bitmap.Dispose()
            }
        }

        $stream = New-Object System.IO.MemoryStream
        $writer = New-Object System.IO.BinaryWriter($stream)
        $writer.Write([uint16]0) # reserved
        $writer.Write([uint16]1) # icon type
        $writer.Write([uint16]$frames.Count)

        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -ge 256) { [byte]0 } else { [byte]$frame.Size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0) # palette colors
            $writer.Write([byte]0) # reserved
            $writer.Write([uint16]1) # planes
            $writer.Write([uint16]32) # bit depth
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) {
            $writer.Write([byte[]]$frame.Bytes)
        }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
        $writer.Dispose()
        $stream.Dispose()
    }
    finally {
        $frames = $null
    }
}

function Draw-ContactSheetIcon {
    param(
        [Parameter(Mandatory)][System.Drawing.Graphics]$Graphics,
        [Parameter(Mandatory)][System.Drawing.Bitmap]$Bitmap,
        [Parameter(Mandatory)][int]$X,
        [Parameter(Mandatory)][int]$Y,
        [Parameter(Mandatory)][int]$DisplaySize,
        [Parameter(Mandatory)][int]$NativeSize,
        [Parameter(Mandatory)][System.Drawing.Color]$LabelColor
    )

    $largeRect = New-Object System.Drawing.Rectangle($X, $Y, $DisplaySize, $DisplaySize)
    $Graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $Graphics.DrawImage($Bitmap, $largeRect)

    $font = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $label = "$NativeSize px"
    $labelBrush = New-Object -TypeName System.Drawing.SolidBrush -ArgumentList @($LabelColor)
    $Graphics.DrawString($label, $font, $labelBrush, ($X + 2), ($Y + $DisplaySize + 7))
    $labelBrush.Dispose()
    $font.Dispose()
}

function New-ContactSheet {
    param([string]$Path)

    $width = 700
    $height = 398
    $sheet = New-Object System.Drawing.Bitmap($width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $graphics = [System.Drawing.Graphics]::FromImage($sheet)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $graphics.Clear([System.Drawing.Color]::White)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $headerFont = New-Object System.Drawing.Font('Segoe UI Semibold', 12, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $smallFont = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $sizes = @(16, 24, 32, 64, 256)
    $bitmaps = @{}
    foreach ($size in $sizes) {
        $bitmaps[$size] = New-RenderedIcon -Size $size
    }

    try {
        $graphics.FillRectangle((New-Object System.Drawing.SolidBrush($script:Palette.LightPanel)), 0, 0, $width, 198)
        $graphics.FillRectangle((New-Object System.Drawing.SolidBrush($script:Palette.DarkPanel)), 0, 198, $width, 200)
        $graphics.DrawString('UPS Guardian  /  light surface', $headerFont, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 22, 38, 56))), 20, 12)
        $graphics.DrawString('UPS Guardian  /  dark surface', $headerFont, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 221, 232, 242))), 20, 210)

        $cardWidth = 132
        $displaySize = 78
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $x = 18 + ($i * $cardWidth)
            Draw-ContactSheetIcon -Graphics $graphics -Bitmap $bitmaps[$sizes[$i]] -X $x -Y 54 -DisplaySize $displaySize -NativeSize $sizes[$i] -LabelColor ([System.Drawing.Color]::FromArgb(255, 38, 54, 70))
            Draw-ContactSheetIcon -Graphics $graphics -Bitmap $bitmaps[$sizes[$i]] -X $x -Y 252 -DisplaySize $displaySize -NativeSize $sizes[$i] -LabelColor ([System.Drawing.Color]::FromArgb(255, 220, 232, 243))
        }

        Save-BitmapPng -Bitmap $sheet -Path $Path
    }
    finally {
        foreach ($bitmap in $bitmaps.Values) { $bitmap.Dispose() }
        $headerFont.Dispose()
        $smallFont.Dispose()
        $graphics.Dispose()
        $sheet.Dispose()
    }
}

function Write-Svg {
    param([Parameter(Mandatory)][string]$Path)

    $svg = @'
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256" role="img" aria-labelledby="title desc">
  <title id="title">UPS Guardian</title>
  <desc id="desc">A teal shield with a white lightning bolt on a deep navy rounded square.</desc>
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#0B1726"/>
      <stop offset="1" stop-color="#10263A"/>
    </linearGradient>
    <linearGradient id="shield" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#1EC9B0"/>
      <stop offset="1" stop-color="#14B8A6"/>
    </linearGradient>
  </defs>
  <path d="M66 12h124c29.823 0 54 24.177 54 54v124c0 29.823-24.177 54-54 54H66c-29.823 0-54-24.177-54-54V66c0-29.823 24.177-54 54-54Z" fill="url(#tile)"/>
  <path d="M66 13.25h124c29.133 0 52.75 23.617 52.75 52.75v124c0 29.133-23.617 52.75-52.75 52.75H66c-29.133 0-52.75-23.617-52.75-52.75V66c0-29.133 23.617-52.75 52.75-52.75Z" fill="none" stroke="#087E77" stroke-width="1.25"/>
  <path d="M128 39c-24 15-46 22-70 27v53c0 44 24 75 70 95 46-20 70-51 70-95V66c-24-5-46-12-70-27Z" fill="url(#shield)" stroke="#087E77" stroke-width="1.5" stroke-linejoin="round"/>
  <path d="m144 70-42 62h24l-13 52 47-66h-23l7-48Z" fill="#F4F7FB" stroke="#F4F7FB" stroke-linejoin="round"/>
</svg>
'@
    [System.IO.File]::WriteAllText($Path, $svg, (New-Object System.Text.UTF8Encoding($false)))
}

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$appPng = Join-Path $OutputDirectory 'app-icon.png'
$appSvg = Join-Path $OutputDirectory 'app-icon.svg'
$appIco = Join-Path $OutputDirectory 'app.ico'
$trayIco = Join-Path $OutputDirectory 'tray.ico'
$contactSheet = Join-Path $WorkDirectory 'contact-sheet.png'

Write-Svg -Path $appSvg
$mainBitmap = New-RenderedIcon -Size 256
try {
    Save-BitmapPng -Bitmap $mainBitmap -Path $appPng
}
finally {
    $mainBitmap.Dispose()
}

Save-PngIco -Path $appIco -Sizes $sizes
# Keep the same complete PNG frame set for NotifyIcon. Windows' legacy Icon
# reader is more reliable when the ICO also carries a 256 px frame.
Save-PngIco -Path $trayIco -Sizes $sizes
New-ContactSheet -Path $contactSheet

Write-Output "Generated UPS Guardian brand assets in $OutputDirectory"
Write-Output "  app-icon.svg (canonical vector)"
Write-Output "  app-icon.png (256x256 RGBA)"
Write-Output "  app.ico (8 PNG frames: 16,20,24,32,48,64,128,256)"
Write-Output "  tray.ico (8 PNG frames: 16,20,24,32,48,64,128,256)"
Write-Output "QA contact sheet: $contactSheet"
