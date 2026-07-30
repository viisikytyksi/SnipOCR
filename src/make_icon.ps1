# Generates app.ico for SnipOCR.
# Simple mark: rounded blue square (capture frame) + white "A" + corner brackets.
# Writes a multi-size ICO (16/32/48/256) using uncompressed DIB entries
# for maximum compatibility with Explorer, the taskbar and System.Drawing.Icon.

Add-Type -AssemblyName System.Drawing

# このスクリプトと同じ場所へ出力する（環境に依存しないように）
$OutPath = Join-Path $PSScriptRoot 'app.ico'
$sizes   = @(256, 48, 32, 16)

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded square background (accent blue, matches the selection overlay)
    $pad    = [Math]::Max(1, [int]($s * 0.04))
    $rect   = New-Object System.Drawing.Rectangle($pad, $pad, ($s - 2*$pad), ($s - 2*$pad))
    $radius = [Math]::Max(2, [int]($s * 0.20))
    $path   = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc(($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
    $path.AddArc(($rect.Right - $d), ($rect.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($rect.X, ($rect.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 132, 200))
    $g.FillPath($bg, $path)

    # corner brackets (capture hint) - only legible at larger sizes
    if ($s -ge 32) {
        $t = [Math]::Max(1, [int]($s * 0.055))          # stroke thickness
        $L = [int]($s * 0.20)                            # bracket arm length
        $m = [int]($s * 0.16)                            # margin from edge
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255,255,255,255)), $t
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $x0 = $m; $y0 = $m; $x1 = $s - $m; $y1 = $s - $m
        # top-left
        $g.DrawLine($pen, $x0, $y0, ($x0 + $L), $y0)
        $g.DrawLine($pen, $x0, $y0, $x0, ($y0 + $L))
        # top-right
        $g.DrawLine($pen, $x1, $y0, ($x1 - $L), $y0)
        $g.DrawLine($pen, $x1, $y0, $x1, ($y0 + $L))
        # bottom-left
        $g.DrawLine($pen, $x0, $y1, ($x0 + $L), $y1)
        $g.DrawLine($pen, $x0, $y1, $x0, ($y1 - $L))
        # bottom-right
        $g.DrawLine($pen, $x1, $y1, ($x1 - $L), $y1)
        $g.DrawLine($pen, $x1, $y1, $x1, ($y1 - $L))
        $pen.Dispose()
    }

    # the letter A (the "text" being recognised)
    $fontSize = if ($s -ge 32) { $s * 0.46 } else { $s * 0.72 }
    $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $box = New-Object System.Drawing.RectangleF(0, ($s * 0.02), $s, $s)
    $g.DrawString('A', $font, $white, $box, $fmt)

    $font.Dispose(); $white.Dispose(); $bg.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

# ---- build ICO with DIB (BITMAPINFOHEADER + BGRA + AND mask) entries ----
$images = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s

    # The 256x256 frame is stored PNG-compressed (standard since Vista) to keep the
    # file small; the small frames stay DIB for maximum compatibility.
    if ($s -ge 256) {
        $pms = New-Object System.IO.MemoryStream
        $bmp.Save($pms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += ,@{ Size = $s; Data = $pms.ToArray() }
        $pms.Dispose(); $bmp.Dispose()
        continue
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER
    $bw.Write([int]40)          # biSize
    $bw.Write([int]$s)          # biWidth
    $bw.Write([int]($s * 2))    # biHeight = XOR + AND
    $bw.Write([int16]1)         # biPlanes
    $bw.Write([int16]32)        # biBitCount
    $bw.Write([int]0)           # biCompression = BI_RGB
    $bw.Write([int]($s * $s * 4))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)

    # XOR: BGRA, bottom-up
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    # AND mask: all zero (alpha channel governs), rows padded to 4 bytes
    $maskRow = [int][Math]::Floor((($s + 31) / 32)) * 4
    $zeros = New-Object byte[] ($maskRow * $s)
    $bw.Write($zeros)

    $bw.Flush()
    $images += ,@{ Size = $s; Data = $ms.ToArray() }
    $bw.Dispose(); $ms.Dispose(); $bmp.Dispose()
}

$fs = New-Object System.IO.FileStream($OutPath, [System.IO.FileMode]::Create)
$w  = New-Object System.IO.BinaryWriter($fs)
$w.Write([int16]0)                  # reserved
$w.Write([int16]1)                  # type = icon
$w.Write([int16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    $s = $img.Size
    $w.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))   # width  (0 == 256)
    $w.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))   # height
    $w.Write([byte]0)               # palette
    $w.Write([byte]0)               # reserved
    $w.Write([int16]1)              # planes
    $w.Write([int16]32)             # bpp
    $w.Write([int]$img.Data.Length)
    $w.Write([int]$offset)
    $offset += $img.Data.Length
}
foreach ($img in $images) { $w.Write($img.Data) }
$w.Flush(); $w.Dispose(); $fs.Dispose()

Write-Output ("app.ico written: {0} bytes, sizes: {1}" -f (Get-Item $OutPath).Length, ($sizes -join ','))
