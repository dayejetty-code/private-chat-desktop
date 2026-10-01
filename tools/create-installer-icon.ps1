$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Split-Path -Parent $PSScriptRoot
$images=[Collections.Generic.List[byte[]]]::new()
$sizes=@(16,32,48,64,128,256)
foreach ($size in $sizes) {
    $bitmap=[Drawing.Bitmap]::new($size,$size)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size/256.0,$size/256.0)
    $fill=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(0,47,167))
    $shape=[Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddPolygon([Drawing.PointF[]]@([Drawing.PointF]::new(24,26),[Drawing.PointF]::new(232,26),[Drawing.PointF]::new(232,188),[Drawing.PointF]::new(95,188),[Drawing.PointF]::new(44,230),[Drawing.PointF]::new(44,188),[Drawing.PointF]::new(24,188)))
    $graphics.FillPath($fill,$shape)
    $pen=[Drawing.Pen]::new([Drawing.Color]::White,13)
    $graphics.DrawLine($pen,67,91,189,91)
    $graphics.DrawLine($pen,67,132,151,132)
    $stream=[IO.MemoryStream]::new()
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $images.Add($stream.ToArray())
    $stream.Dispose();$pen.Dispose();$shape.Dispose();$fill.Dispose();$graphics.Dispose();$bitmap.Dispose()
}
$path=Join-Path $root 'installer\private-chat.ico'
$writer=[IO.BinaryWriter]::new([IO.File]::Create($path))
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for ($i=0;$i -lt $sizes.Count;$i++) {
        $dim=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dim);$writer.Write([byte]$dim);$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset)
        $offset+=$images[$i].Length
    }
    foreach ($bytes in $images) {$writer.Write($bytes)}
} finally {$writer.Dispose()}
Write-Output $path
