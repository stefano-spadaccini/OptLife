# Draws OptLife's icon (a white ring on black, the same mark as the tray icon and the panel's title)
# and writes it as src\OptLife.ico, in the sizes Windows asks for. Run it again only if the mark changes.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Black)
    $stroke = [Math]::Max(1.6, $size * 0.13)
    $inset = $size * 0.22
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
    $g.DrawEllipse($pen, [single]$inset, [single]$inset, [single]($size - 2 * $inset), [single]($size - 2 * $inset))
    $pen.Dispose(); $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # The large one is stored as PNG, as Windows expects for this size.
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # The small ones are stored the old way, which every program that reads icons understands:
        # a header, the pixels from the bottom row up, then a (here empty) one-bit mask.
        $bw = New-Object System.IO.BinaryWriter $stream
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]($size * $size * 4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bitmap.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $size)))
        $bw.Flush()
    }
    $bitmap.Dispose()
    , $stream.ToArray()
}

# An .ico file is a small table of contents followed by the pictures, here stored as PNG.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dimension); $w.Write([byte]$dimension); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $w.Write($image) }
$w.Flush()
$target = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\OptLife.ico'
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"Scritta $target ($($out.Length) byte, $($sizes.Count) grandezze)"
