# Draws the BFM icon (a battery on a dark rounded tile) and writes a multi-size .ico with PNG entries.
param([string]$Out = "$PSScriptRoot\..\src\BFM\BFM.ico")
Add-Type -AssemblyName System.Drawing

function RoundRect([System.Drawing.Drawing2D.GraphicsPath]$p, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
}

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    RoundRect $tile 0 0 ($s - 1) ($s - 1) ($s * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(36, 36, 36))), $tile)

    # Battery body, terminal and fill.
    $bx = $s * 0.16; $by = $s * 0.32; $bw = $s * 0.60; $bh = $s * 0.36
    $stroke = [Math]::Max(1.0, $s * 0.055)
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    RoundRect $body $bx $by $bw $bh ($s * 0.07)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(240, 240, 240)), $stroke), $body)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 240, 240))),
        [float]($bx + $bw + $stroke * 0.4), [float]($by + $bh * 0.3), [float]($s * 0.06), [float]($bh * 0.4))
    $pad = $stroke * 1.6
    $fill = New-Object System.Drawing.Drawing2D.GraphicsPath
    RoundRect $fill ($bx + $pad) ($by + $pad) (($bw - 2 * $pad) * 0.7) ($bh - 2 * $pad) ([Math]::Max(0.5, $s * 0.03))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(108, 203, 95))), $fill)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) { $images.Add([byte[]](Draw $s)) }
$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out) | ForEach-Object { Join-Path $_ (Split-Path $Out -Leaf) }))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Dispose()
"wrote $Out"
