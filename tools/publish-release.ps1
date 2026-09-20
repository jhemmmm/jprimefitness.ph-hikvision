<#
.SYNOPSIS
  Builds the Hikvision helper release zip that the JPrime installer downloads from GitHub releases.

.DESCRIPTION
  The HCNetSDK native libraries (bin\lib) are not in git, so releases cannot be built by GitHub Actions;
  run this on a machine that has the SDK. Output: dist\hikvision-v<Version>.zip and a .sha256 next to it.

  Self-contained by default (no ASP.NET Core runtime needed on the gym PC; the installer detects hostfxr.dll
  and skips the runtime download). appsettings.json in the zip is a placeholder: the installer always writes
  the real device credentials, so no secret from this machine ends up in the release.

.EXAMPLE
  .\tools\publish-release.ps1 -Version 1.0.1
  .\tools\publish-release.ps1 -Version 1.0.1 -Publish      # also creates the GitHub release (needs the gh CLI)
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Lib = (Join-Path $PSScriptRoot '..\bin\lib'),
    [switch]$FrameworkDependent,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tag = "v$Version"
$publishDir = Join-Path $root 'dist\publish'
$zipName = "hikvision-$tag.zip"
$zip = Join-Path $root "dist\$zipName"

if (-not (Test-Path (Join-Path $Lib 'HCNetSDK.dll'))) {
    throw "HCNetSDK.dll not found in $Lib. The SDK is not in git; pass -Lib <folder> with the Hikvision SDK libraries."
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $root 'dist') | Out-Null

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
Write-Host "Publishing HikVision $tag (self-contained: $selfContained)"
dotnet publish (Join-Path $root 'HikVision\HikVision.csproj') -c Release -r win-x64 --self-contained $selfContained -o $publishDir -p:Version=$Version -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Copy-Item $Lib (Join-Path $publishDir 'lib') -Recurse
Get-ChildItem $publishDir -Filter *.pdb | Remove-Item

# Exactly the five keys the helper accepts; any other key is fatal at startup.
$placeholder = @{
    HikVision = [ordered]@{
        Username     = 'admin'
        Password     = ''
        ForwardUrl   = 'http://127.0.0.1:8001/api/biometric/hikvision/callback'
        ForwardToken = ''
        Debug        = $false
    }
}
$placeholder | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $publishDir 'appsettings.json') -Encoding utf8

if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# ZipFile (not Compress-Archive) so entry names use forward slashes, which the installer's extractor expects.
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $zipName" | Set-Content "$zip.sha256" -Encoding ascii

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Built $zip ($size MB)"
Write-Host "sha256 $hash"

if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh CLI not found; upload the zip manually (see below).' }
    gh release create $tag $zip "$zip.sha256" --repo jhemmmm/jprimefitness.ph-hikvision --title $tag --notes "Hikvision helper $tag"
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE" }
    Write-Host "Published https://github.com/jhemmmm/jprimefitness.ph-hikvision/releases/tag/$tag"
}
else {
    Write-Host ''
    Write-Host 'Next: https://github.com/jhemmmm/jprimefitness.ph-hikvision/releases/new'
    Write-Host "  tag $tag (create on publish), attach $zipName and $zipName.sha256, Publish release."
    Write-Host '  The installer picks the first .zip asset of releases/latest; do not mark it as a pre-release.'
}
