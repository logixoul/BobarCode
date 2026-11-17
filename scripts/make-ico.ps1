<#
.SYNOPSIS
  Regenerates the app icon (src\Bobar.App\Assets\icon.ico) from icon.png.

.DESCRIPTION
  Run this after replacing src\Bobar.App\Assets\icon.png. It writes a
  multi-size .ico (16-256 px, PNG-compressed entries), with the image centred
  and not stretched if it isn't square. bobar.exe embeds that .ico
  (ApplicationIcon) and both windows use it, so rebuild afterwards.
#>
param(
    [string]$Src = (Join-Path $PSScriptRoot "..\src\Bobar.App\Assets\icon.png"),
    [string]$Dst = (Join-Path $PSScriptRoot "..\src\Bobar.App\Assets\icon.ico")
)
$Src = (Resolve-Path $Src).Path
$Dst = [System.IO.Path]::GetFullPath($Dst)
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($Src)
$side = [Math]::Max($img.Width, $img.Height)
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.SmoothingMode = 'HighQuality'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    # fit inside the square, centred, keeping aspect ratio
    $w = $img.Width * $s / $side; $h = $img.Height * $s / $side
    $g.DrawImage($img, [single](($s - $w) / 2), [single](($s - $h) / 2), [single]$w, [single]$h)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$img.Dispose()
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Dst, $out.ToArray())
Write-Host "Wrote $Dst"
