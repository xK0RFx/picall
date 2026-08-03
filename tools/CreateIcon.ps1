$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assetDirectory = Join-Path $PSScriptRoot '..\Assets'
$pngPath = Join-Path $assetDirectory 'picall.png'
$iconPath = Join-Path $assetDirectory 'picall.ico'
$bitmap = [System.Drawing.Bitmap]::new(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

function Add-RoundedRect([System.Drawing.Drawing2D.GraphicsPath]$path, [float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
}

$outerPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
Add-RoundedRect $outerPath 0 0 256 256 60
$outerBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 17, 20, 26))
$graphics.FillPath($outerBrush, $outerPath)

$innerPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
Add-RoundedRect $innerPath 25 25 206 206 50
$innerBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.PointF]::new(30, 20), [System.Drawing.PointF]::new(226, 236),
    [System.Drawing.Color]::FromArgb(255, 167, 139, 250), [System.Drawing.Color]::FromArgb(255, 109, 59, 234))
$graphics.FillPath($innerBrush, $innerPath)

$whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
$softBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 221, 214, 254))
$graphics.FillEllipse($whiteBrush, 56, 56, 70, 70)
$graphics.FillEllipse($softBrush, 147, 143, 48, 48)
$mountain = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(55, 181), [System.Drawing.PointF]::new(97, 137),
    [System.Drawing.PointF]::new(128, 167), [System.Drawing.PointF]::new(155, 138),
    [System.Drawing.PointF]::new(202, 187), [System.Drawing.PointF]::new(202, 203),
    [System.Drawing.PointF]::new(55, 203))
$graphics.FillPolygon($whiteBrush, $mountain)

$bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$handle = $bitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($handle)
$stream = [System.IO.File]::Create($iconPath)
$icon.Save($stream)
$stream.Dispose()
$icon.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
$outerBrush.Dispose()
$innerBrush.Dispose()
$whiteBrush.Dispose()
$softBrush.Dispose()
$outerPath.Dispose()
$innerPath.Dispose()
