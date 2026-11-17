# Builds the single-file release exe into dist\ with its SHA-256 next to it (the updater verifies it).
# Framework-dependent: needs the .NET 10 Desktop Runtime, which winget installs as a dependency.
param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
$dist = Join-Path $root "dist"
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $root "src\BFM\BFM.csproj") -c Release -r $Runtime --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $dist "JuiceBar.exe"
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Set-Content (Join-Path $dist "JuiceBar.exe.sha256") "$hash  JuiceBar.exe" -NoNewline -Encoding ascii
Get-ChildItem $dist | Format-Table Name, Length -AutoSize
"SHA256 $hash"
