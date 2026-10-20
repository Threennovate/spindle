param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$Output = '',
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "artifacts/$Runtime" }
$Output = [System.IO.Path]::GetFullPath($Output)
dotnet publish (Join-Path $root 'Spindle.Windows/Spindle.Windows.csproj') -c Release -r $Runtime --self-contained true --disable-build-servers -m:1 -p:PublishSingleFile=false -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Copy-Item (Join-Path $PSScriptRoot 'package-README.txt') (Join-Path $Output 'README.txt')
foreach ($required in @('Spindle.exe', 'coreclr.dll', 'hostfxr.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll')) {
    if (-not (Test-Path (Join-Path $Output $required))) { throw "Missing Windows runtime file: $required" }
}
if ($Zip) {
    $architecture = if ($Runtime -eq 'win-x64') { 'x64' } else { 'arm64' }
    $archive = Join-Path (Split-Path -Parent $Output) "Spindle-Windows-$architecture.zip"
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $archive -Force
    Write-Host "Created $archive"
}
Write-Host "Published Spindle to $Output. Run Spindle.exe."
