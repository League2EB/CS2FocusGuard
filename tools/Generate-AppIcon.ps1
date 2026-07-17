param(
    [string]$SourceImagePath = (
        Join-Path $PSScriptRoot '..\assets\source\app-icon\ios26-glass.png'
    ),
    [string]$OutputDirectory = (
        Join-Path $PSScriptRoot '..\src\CS2FocusGuard.App\Assets'
    ),
    [double]$CornerRadiusFraction = 0.18
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$SourceImagePath = [System.IO.Path]::GetFullPath($SourceImagePath)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not (Test-Path -LiteralPath $SourceImagePath -PathType Leaf)) {
    throw "Source image not found: $SourceImagePath"
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function New-AppIconBitmap {
    param(
        [System.Drawing.Image]$SourceImage,
        [int]$Size,
        [double]$CornerRadiusFraction
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $cornerRadius = [Math]::Max(1, [Math]::Round($Size * $CornerRadiusFraction))
    $diameter = $cornerRadius * 2
    $clipPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $graphics.SmoothingMode =
        [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.InterpolationMode =
        [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.CompositingQuality =
        [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.PixelOffsetMode =
        [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $scale = [Math]::Max(
        $Size / [double]$SourceImage.Width,
        $Size / [double]$SourceImage.Height)
    $width = $SourceImage.Width * $scale
    $height = $SourceImage.Height * $scale
    $bounds = [System.Drawing.Rectangle]::new(
        [Math]::Round(($Size - $width) / 2),
        [Math]::Round(($Size - $height) / 2),
        [Math]::Round($width),
        [Math]::Round($height))

    $clipPath.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $clipPath.AddArc($Size - $diameter, 0, $diameter, $diameter, 270, 90)
    $clipPath.AddArc($Size - $diameter, $Size - $diameter, $diameter, $diameter, 0, 90)
    $clipPath.AddArc(0, $Size - $diameter, $diameter, $diameter, 90, 90)
    $clipPath.CloseFigure()
    $graphics.SetClip($clipPath)
    $graphics.DrawImage(
        $SourceImage,
        $bounds,
        0,
        0,
        $SourceImage.Width,
        $SourceImage.Height,
        [System.Drawing.GraphicsUnit]::Pixel)
    $clipPath.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function ConvertTo-IconDib {
    param([System.Drawing.Bitmap]$Bitmap)

    $size = $Bitmap.Width
    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([uint32]40)
    $writer.Write([int32]$size)
    $writer.Write([int32]($size * 2))
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]0)
    $writer.Write([uint32]($size * $size * 4))
    $writer.Write([int32]0)
    $writer.Write([int32]0)
    $writer.Write([uint32]0)
    $writer.Write([uint32]0)

    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $pixel = $Bitmap.GetPixel($x, $y)
            $writer.Write([byte]$pixel.B)
            $writer.Write([byte]$pixel.G)
            $writer.Write([byte]$pixel.R)
            $writer.Write([byte]$pixel.A)
        }
    }

    $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
    $mask = [byte[]]::new($maskStride)
    for ($y = 0; $y -lt $size; $y++) {
        $writer.Write($mask)
    }

    $writer.Dispose()
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return $bytes
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$sourceImage = [System.Drawing.Image]::FromFile($SourceImagePath)
try {
    $sourceSize = [Math]::Min($sourceImage.Width, $sourceImage.Height)
    $sourceBitmap = New-AppIconBitmap $sourceImage $sourceSize $CornerRadiusFraction
    try {
        $sourceBitmap.Save(
            (Join-Path $OutputDirectory 'AppIconSource.png'),
            [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $sourceBitmap.Dispose()
    }

    $images = foreach ($size in $sizes) {
        $bitmap = New-AppIconBitmap $sourceImage $size $CornerRadiusFraction
        if ($size -le 48) {
            $bytes = ConvertTo-IconDib $bitmap
        }
        else {
            $stream = [System.IO.MemoryStream]::new()
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $stream.ToArray()
            $stream.Dispose()
        }

        if ($size -eq 256) {
            $bitmap.Save(
                (Join-Path $OutputDirectory 'AppIcon.png'),
                [System.Drawing.Imaging.ImageFormat]::Png)
        }

        $bitmap.Dispose()
        [pscustomobject]@{
            Size = $size
            Bytes = $bytes
        }
    }
}
finally {
    $sourceImage.Dispose()
}

$iconPath = Join-Path $OutputDirectory 'AppIcon.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($image in $images) {
    $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$image.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}

foreach ($image in $images) {
    $writer.Write([byte[]]$image.Bytes)
}

$writer.Dispose()
$file.Dispose()
Write-Output $iconPath
