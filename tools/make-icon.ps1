# Renders assets\juicebar.svg (32 px and up) and assets\juicebar-small.svg (16-24 px) with headless Edge
# and packs them into a multi-size .ico with PNG entries, plus a 256 px PNG preview.
param(
    [string]$Out = "$PSScriptRoot\..\src\BFM\JuiceBar.ico",
    [string]$Preview = "$PSScriptRoot\..\assets\juicebar-256.png"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge is needed to render the SVG." }

$assets = (Resolve-Path "$PSScriptRoot\..\assets").Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("juicebar-icon-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory $work | Out-Null

function Render([string]$svg, [int]$size) {
    $html = Join-Path $work "$size.html"
    $png = Join-Path $work "$size.png"
    $src = ([Uri](Join-Path $assets $svg)).AbsoluteUri
    Set-Content $html "<html><body style='margin:0;background:transparent'><img src='$src' width='$size' height='$size' style='display:block'></body></html>"
    # Tiny windows get clamped to a minimum size, so render into a fixed viewport and crop.
    $p = Start-Process $edge -PassThru -WindowStyle Hidden -ArgumentList @(
        "--headless=new", "--disable-gpu", "--force-device-scale-factor=1", "--hide-scrollbars",
        "--default-background-color=00000000", "--user-data-dir=`"$work\profile`"", "--window-size=400,400",
        "--screenshot=`"$png`"", ([Uri]$html).AbsoluteUri)
    $p.WaitForExit(30000) | Out-Null
    $img = [System.Drawing.Bitmap]::FromFile($png)
    $crop = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($crop)
    $g.DrawImage($img, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), (New-Object System.Drawing.Rectangle 0, 0, $size, $size), 'Pixel')
    $g.Dispose(); $img.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $crop.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) { $images.Add((Render ($(if ($s -le 24) { "juicebar-small.svg" } else { "juicebar.svg" })) $s)) }

$fs = [IO.File]::Create($Out)
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Dispose()
[IO.File]::WriteAllBytes($Preview, $images[$images.Count - 1])
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
"wrote $Out ($($sizes -join ', ') px) and $Preview"
