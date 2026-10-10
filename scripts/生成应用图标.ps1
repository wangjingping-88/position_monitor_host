$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\PositionMonitor.App\Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$bitmap = New-Object System.Drawing.Bitmap 256, 256
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$shape = New-Object System.Drawing.Drawing2D.GraphicsPath
$shape.AddArc(8, 8, 80, 80, 180, 90)
$shape.AddArc(168, 8, 80, 80, 270, 90)
$shape.AddArc(168, 168, 80, 80, 0, 90)
$shape.AddArc(8, 168, 80, 80, 90, 90)
$shape.CloseFigure()
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(256, 256)), ([System.Drawing.Color]::FromArgb(35, 177, 160)), ([System.Drawing.Color]::FromArgb(6, 108, 114))
$graphics.FillPath($brush, $shape)
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 17
$pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($pen, 66, 181, 194, 181)
$graphics.DrawLine($pen, 66, 181, 157, 77)
$arcPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(170, 211, 245, 238)), 7
$graphics.DrawArc($arcPen, 26, 141, 80, 80, 310, 50)
$graphics.FillEllipse([System.Drawing.Brushes]::White, 172, 57, 25, 25)
$bitmap.Save((Join-Path $assets 'PositionMonitor.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $small = New-Object System.Drawing.Bitmap $size, $size
    $canvas = [System.Drawing.Graphics]::FromImage($small)
    $canvas.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $canvas.DrawImage($bitmap, 0, 0, $size, $size)
    $stream = New-Object System.IO.MemoryStream
    $small.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose(); $canvas.Dispose(); $small.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $assets 'PositionMonitor.ico'))
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
$writer.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $shape.Dispose(); $brush.Dispose(); $pen.Dispose(); $arcPen.Dispose()
Write-Host "已生成应用图标：$assets\PositionMonitor.ico"
