<#
.SYNOPSIS
Builds the Windows installer and update packages for the version in AiUsage.csproj.

.DESCRIPTION
Publishes the app (framework-dependent, win-x64) and packs it with Velopack:
build\releases\usagetop-win-Setup.exe installs it per user without admin rights,
with a Start menu entry and an uninstaller, and fetches the .NET 9 desktop runtime
first if the machine lacks it. The .nupkg files and releases.win.json beside it
are what the app's updater reads, so a later version can ship only what changed.

Keep build\releases between versions: vpk makes the delta packages against the
previous release it finds there.

Needs the vpk tool at the Velopack package's version:
    dotnet tool install -g vpk --version 1.2.161

.PARAMETER Version
Overrides the version in AiUsage.csproj: the release workflow passes the tag's
version (1.2.0, or 1.2.1-beta.1 for a beta), and it is also handy for a test
build. A leading "v" is dropped. The binaries carry it too: the file and
assembly version get the numeric part (1.2.1.0), the product version all of it.
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\AiUsage\AiUsage.csproj'
$publish = Join-Path $root 'build\publish'
$releases = Join-Path $root 'build\releases'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw 'No <Version> in AiUsage.csproj and none given.' }
$Version = $Version.Trim() -replace '^v', ''
# SemVer 2.0 without build metadata, which Velopack would ignore when ordering.
$semver = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-((0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?$'
if ($Version -cnotmatch $semver) { throw "'$Version' is not a version like 1.2.0 or 1.2.1-beta.1." }
Write-Host "usagetop $Version"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# -p:Version sets the file, assembly and product versions of every project built,
# AiUsage.Core included; the second property keeps the commit hash out of the
# product version, which the SDK would otherwise append ("1.2.0+3f96fb9...").
dotnet publish $project -c Release -r win-x64 --self-contained false `
    -p:Version=$Version -p:IncludeSourceRevisionInInformationalVersion=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

vpk pack `
    --packId usagetop `
    --packVersion $Version `
    --runtime win-x64 `
    --packDir $publish `
    --mainExe AiUsage.exe `
    --packTitle 'usagetop' `
    --packAuthors 'usagetop' `
    --icon (Join-Path $root 'src\AiUsage\Assets\app.ico') `
    --framework net9-x64-desktop `
    --shortcuts StartMenuRoot `
    --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

Get-ChildItem $releases | Sort-Object LastWriteTime | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
