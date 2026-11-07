# Draws the MeetWise Studio icon and writes:
#   src\MeetWise.App\meetwise.ico  (16-256 px, PNG entries)
#   docs\img\icon.png              (512 px, for the product page)
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = [double]$size; $k = $s / 256.0
    # rounded navy tile
    $r = 52 * $k
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = 8 * $k; $y = 8 * $k; $w = 240 * $k; $h = 240 * $k; $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90); $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(0, $s)),
        [System.Drawing.Color]::FromArgb(255, 36, 52, 98), [System.Drawing.Color]::FromArgb(255, 14, 20, 40))
    $g.FillPath($grad, $path)
    # record ring (red) with a solid dot
    $ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 235, 64, 72)), (22 * $k)
    $g.DrawEllipse($ringPen, 42 * $k, 42 * $k, 110 * $k, 110 * $k)
    $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 235, 64, 72))
    $g.FillEllipse($dot, 84 * $k, 84 * $k, 46 * $k, 46 * $k)
    # three subtitle bars (cyan, white, cyan) across the bottom
    $cyan = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 94, 201, 255))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 240, 244, 252))
    $g.FillRectangle($cyan, 46 * $k, 176 * $k, 122 * $k, 16 * $k)
    $g.FillRectangle($white, 46 * $k, 200 * $k, 164 * $k, 16 * $k)
    $g.FillRectangle($cyan, 46 * $k, 224 * $k, 90 * $k, 16 * $k)
    $g.Dispose()
    return $bmp
}

function Get-Png($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

# ICO file with PNG-compressed entries
$sizes = 16, 24, 32, 48, 64, 128, 256
$entries = foreach ($s in $sizes) { $b = New-IconBitmap $s; [pscustomobject]@{ Size = $s; Data = (Get-Png $b) }; $b.Dispose() }
$ico = Join-Path $root "src\MeetWise.App\meetwise.ico"
$fs = [System.IO.File]::Create($ico)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$e.Data.Length); $bw.Write([uint32]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $bw.Write($e.Data) }
$bw.Close(); $fs.Close()

$big = New-IconBitmap 512
$big.Save((Join-Path $root "docs\img\icon.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
"wrote $ico and docs\img\icon.png"
