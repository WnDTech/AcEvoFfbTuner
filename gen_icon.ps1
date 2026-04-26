Add-Type -AssemblyName System.Drawing

$pngPath = 'C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\src\AcEvoFfbTuner\Resources\wheel_nobg.png'
$icoPath = 'C:\Users\paul_\OneDrive\Documents\APP\ACEVO - Telemetry FFB\src\AcEvoFfbTuner\Resources\wheel.ico'

$sizes = @(16, 32, 48, 64, 128, 256)
$bmp = New-Object System.Drawing.Bitmap($pngPath)

$iconStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($iconStream)

$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)

$dataOffset = 6 + ($sizes.Count * 16)
$entries = @()

foreach ($size in $sizes) {
    $resized = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($resized)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($bmp, 0, 0, $size, $size)
    $g.Dispose()

    $pngMemStream = New-Object System.IO.MemoryStream
    $resized.Save($pngMemStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $resized.Dispose()
    $bytes = $pngMemStream.ToArray()
    $pngMemStream.Dispose()

    $w = if ($size -ge 256) { [byte]0 } else { [byte]$size }
    $h = if ($size -ge 256) { [byte]0 } else { [byte]$size }

    $entries += @{ w = $w; h = $h; size = $bytes.Length; data = $bytes }
}

$currentOffset = $dataOffset
foreach ($entry in $entries) {
    $writer.Write([byte]$entry.w)
    $writer.Write([byte]$entry.h)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$entry.size)
    $writer.Write([uint32]$currentOffset)
    $currentOffset += $entry.size
}

foreach ($entry in $entries) {
    $writer.Write($entry.data)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $iconStream.ToArray())
$writer.Dispose()
$iconStream.Dispose()
$bmp.Dispose()

Write-Host "ICO created at: $icoPath"
Write-Host "Size: $((Get-Item $icoPath).Length) bytes"
