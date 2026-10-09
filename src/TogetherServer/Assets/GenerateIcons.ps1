# Regenerate the checked-in ICO assets without an app, window, font or new dependency.
# The production shape follows ui/public/favicon.svg's rounded tile and T path.
# Development keeps the existing orange square / black D distinction.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

function New-IconBitmap([int] $size, [bool] $staging) {
    $large = [System.Drawing.Bitmap]::new($size * 4, $size * 4)
    $graphics = [System.Drawing.Graphics]::FromImage($large)
    $orange = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 138, 31))
    $ink = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(26, 14, 5))
    $tile = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $glyph = [System.Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($large.Width / 40.0, $large.Height / 40.0)
        if ($staging) {
            $graphics.FillRectangle($orange, 2.0, 2.0, 36.0, 36.0)
            $glyph.StartFigure()
            $glyph.AddLine(12.0, 10.0, 20.0, 10.0)
            $glyph.AddBezier(20.0, 10.0, 35.0, 10.0, 35.0, 30.0, 20.0, 30.0)
            $glyph.AddLine(20.0, 30.0, 12.0, 30.0)
            $glyph.CloseFigure()
            $glyph.StartFigure()
            $glyph.AddLine(17.0, 15.0, 20.0, 15.0)
            $glyph.AddBezier(20.0, 15.0, 28.0, 15.0, 28.0, 25.0, 20.0, 25.0)
            $glyph.AddLine(20.0, 25.0, 17.0, 25.0)
            $glyph.CloseFigure()
            $graphics.FillPath($ink, $glyph)
        } else {
            $tile.AddArc(0.0, 0.0, 20.0, 20.0, 180.0, 90.0)
            $tile.AddArc(20.0, 0.0, 20.0, 20.0, 270.0, 90.0)
            $tile.AddArc(20.0, 20.0, 20.0, 20.0, 0.0, 90.0)
            $tile.AddArc(0.0, 20.0, 20.0, 20.0, 90.0, 90.0)
            $tile.CloseFigure()
            $graphics.FillPath($orange, $tile)
            # M12 10h16v4h-6v16h-4V14h-6z, in the existing 40x40 vector.
            $graphics.FillRectangle($ink, 12.0, 10.0, 16.0, 4.0)
            $graphics.FillRectangle($ink, 18.0, 14.0, 4.0, 16.0)
        }
    } finally {
        $glyph.Dispose()
        $tile.Dispose()
        $ink.Dispose()
        $orange.Dispose()
        $graphics.Dispose()
    }
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $scaled = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $scaled.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $scaled.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scaled.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scaled.DrawImage($large, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
    } finally {
        $scaled.Dispose()
        $large.Dispose()
    }
    return $bitmap
}

function Get-IconFrame([System.Drawing.Bitmap] $bitmap) {
    $size = $bitmap.Width
    $maskStride = [int]([Math]::Floor(($size + 31) / 32.0) * 4)
    $frame = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($frame)
    try {
        $writer.Write([int]40) # BITMAPINFOHEADER; ICO height includes XOR and AND planes.
        $writer.Write([int]$size)
        $writer.Write([int]($size * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([int]0)
        $writer.Write([int]($size * $size * 4))
        1..4 | ForEach-Object { $writer.Write([int]0) }
        for ($row = $size - 1; $row -ge 0; $row--) {
            for ($column = 0; $column -lt $size; $column++) {
                $pixel = $bitmap.GetPixel($column, $row)
                $writer.Write([byte]$pixel.B)
                $writer.Write([byte]$pixel.G)
                $writer.Write([byte]$pixel.R)
                $writer.Write([byte]$pixel.A)
            }
        }
        for ($row = $size - 1; $row -ge 0; $row--) {
            $mask = [byte[]]::new($maskStride)
            for ($column = 0; $column -lt $size; $column++) {
                if ($bitmap.GetPixel($column, $row).A -eq 0) {
                    $index = [int][Math]::Floor($column / 8.0)
                    $mask[$index] = [byte]($mask[$index] -bor (1 -shl (7 - ($column % 8))))
                }
            }
            $writer.Write($mask)
        }
        $writer.Flush()
        return ,$frame.ToArray()
    } finally {
        $writer.Dispose()
        $frame.Dispose()
    }
}

foreach ($staging in @($false, $true)) {
    $frames = foreach ($size in $sizes) {
        $bitmap = New-IconBitmap $size $staging
        try { ,(Get-IconFrame $bitmap) } finally { $bitmap.Dispose() }
    }
    $name = if ($staging) { 'TogetherServer.Development.ico' } else { 'TogetherServer.ico' }
    $target = [System.IO.File]::Create([System.IO.Path]::Combine($PSScriptRoot, $name))
    $writer = [System.IO.BinaryWriter]::new($target)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $edge = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$edge)
            $writer.Write([byte]$edge)
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([int]$frames[$index].Length)
            $writer.Write([int]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally {
        $writer.Dispose()
        $target.Dispose()
    }
    Write-Output "Generated $name ($($sizes -join ', ') pixels)."
}
