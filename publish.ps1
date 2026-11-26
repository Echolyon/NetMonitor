# NetMonitor yayin paketi olusturucu
# Kullanim (repo kok klasorunde):
#   powershell -ExecutionPolicy Bypass -File .\publish.ps1
#   powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Version 1.0.1
param([string]$Version = "1.0.0")

$ErrorActionPreference = "Stop"
$root  = $PSScriptRoot
$out   = Join-Path $root "publish"
$rel   = Join-Path $root "release"
$name  = "NetMonitor-v$Version-win-x64"
$stage = Join-Path $rel $name
$zip   = Join-Path $rel "$name.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }

Write-Host "==> Derleniyor (tek dosya, self-contained, win-x64)..."
dotnet publish "$root\NetMonitor.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish basarisiz oldu." }

Write-Host "==> Paket hazirlaniyor..."
New-Item -ItemType Directory -Path "$stage\GeoIP" -Force | Out-Null
Copy-Item "$out\NetMonitor.exe" $stage
Copy-Item "$root\GeoIP\README.txt" "$stage\GeoIP"
foreach ($f in @("LICENSE", "LICENSE.txt", "README.md", "README.tr.md")) {
    $p = Join-Path $root $f
    if (Test-Path $p) { Copy-Item $p $stage }
}

Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force

$exe  = Get-Item "$stage\NetMonitor.exe"
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host ""
Write-Host "Tamam."
Write-Host ("  EXE boyutu : {0:N1} MB" -f ($exe.Length / 1MB))
Write-Host ("  ZIP        : {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host "  SHA-256    : $hash"
