<#
.SYNOPSIS
Builds a release of every app on this machine and publishes it as a GitHub Release.

.DESCRIPTION
One version, one GitHub Release, carrying:

  usagetop-win-Setup.exe and the Velopack packages   the Windows app and its updates
  usagetop-android.apk                               the Android app, signed
  usagetop-linux-x64.tar.gz, -arm64.tar.gz           the Linux app, with its runtime;
                                                     the app updates itself from them
  usagetop-server-linux-x64.tar.gz, -arm64.tar.gz    the server, with its runtime

It runs the tests, fetches the previous release so the Windows update can be a
delta, builds all of the above with the release's version, pushes the tag and
creates the release. The release stays a draft until its files and notes are on
it, so neither the apps' updaters nor the /releases/latest download links ever
see half of one.

The Mac app and the server's Docker image are built on a Mac and added
afterwards:
  ./release-mac.sh

Needs:
  - the .NET 9 SDK, and the vpk tool: dotnet tool install -g vpk --version 1.2.161
  - gh, signed in to an account that may write to the repository
  - a JDK 17+ and the Android release key. Both are named in a properties file
    outside the repository, at USAGETOP_SIGNING or ~/.usagetop/signing.properties:
        storeFile=C:/path/to/usagetop-release.jks
        storePassword=...
        keyAlias=usagetop
        keyPassword=...
        javaHome=C:/path/to/jdk        (optional; JAVA_HOME is used otherwise)
    Every release must be signed with the same key, or phones refuse the update.

.PARAMETER Version
The version to release, e.g. 1.2.0, or 1.2.1-beta.1 for a pre-release, which
only installs that opted into beta versions are offered. Defaults to <Version>
in AiUsage.csproj.

.PARAMETER Draft
Stops before publishing: the release is left as a draft to look over, and
published by hand on GitHub or with: gh release edit v<version> --draft=false
#>
param([string]$Version, [switch]$Draft)

$ErrorActionPreference = 'Stop'
$repo = 'rzukic/usagetop'
$url = "https://github.com/$repo"
$win = Join-Path $PSScriptRoot 'my-usage-win'
$android = Join-Path $PSScriptRoot 'my-usage-android'
$server = Join-Path $PSScriptRoot 'my-usage-server'
$linux = Join-Path $PSScriptRoot 'my-usage-linux'
$project = Join-Path $win 'src\AiUsage\AiUsage.csproj'
$releases = Join-Path $win 'build\releases'
# The files that are not Velopack's: vpk uploads its own folder, these go on after.
$extra = Join-Path $PSScriptRoot 'build\release'

function Invoke-Step([string]$What, [scriptblock]$Do) {
    Write-Host "`n== $What" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Do
    if ($LASTEXITCODE -ne 0) { throw "$What failed." }
}

# A .tar.gz of a folder under one top-level name. Written here rather than with
# tar because Windows has no executable bit to carry over: the server's
# launcher must be marked executable inside the archive.
function Write-TarGz([string]$Folder, [string]$Archive, [string]$Top, [string[]]$Executable) {
    $file = [IO.File]::Create($Archive)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
    $tar = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
    try {
        foreach ($item in Get-ChildItem $Folder -Recurse -File | Sort-Object FullName) {
            $name = [IO.Path]::GetRelativePath($Folder, $item.FullName).Replace('\', '/')
            $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, "$Top/$name")
            # 0755 for what is run, 0644 for the rest.
            $entry.Mode = [IO.UnixFileMode]$(if ($Executable -contains $name) { 493 } else { 420 })
            $entry.ModificationTime = $item.LastWriteTimeUtc
            $data = [IO.File]::OpenRead($item.FullName)
            try { $entry.DataStream = $data; $tar.WriteEntry($entry) } finally { $data.Dispose() }
        }
    }
    finally { $tar.Dispose(); $gzip.Dispose(); $file.Dispose() }
}

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$Version = "$Version".Trim() -replace '^v', ''
$semver = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-((0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?$'
if ($Version -cnotmatch $semver) { throw "'$Version' is not a version like 1.2.0 or 1.2.1-beta.1." }
$tag = "v$Version"
$beta = $Version.Contains('-')
[string[]]$pre = if ($beta) { '--pre' } else { @() }
Write-Host "usagetop $Version$(if ($beta) { ' (pre-release)' })"

Push-Location $PSScriptRoot
try {
    # What is released is exactly a commit that is on GitHub: nothing
    # uncommitted, nothing unpushed.
    if (git status --porcelain) { throw 'There are uncommitted changes. Commit or stash them first.' }
    $sha = git rev-parse HEAD
    $where = gh api "repos/$repo/compare/main...$sha" --jq .status 2>$null
    if ($where -notin 'identical', 'behind') { throw "HEAD ($($sha.Substring(0, 7))) is not on GitHub's main branch. Push it first." }
    gh release view $tag -R $repo *> $null
    if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists." }

    $token = gh auth token
    if (-not $token) { throw 'gh is not signed in.' }

    # The Android key, checked before anything is built: a release without the
    # APK, or with one signed by another key, is not a release.
    $signingFile = if ($env:USAGETOP_SIGNING) { $env:USAGETOP_SIGNING } else { Join-Path $HOME '.usagetop\signing.properties' }
    if (-not (Test-Path $signingFile)) { throw "No Android signing properties at $signingFile (see this script's header)." }
    $signing = @{}
    foreach ($line in Get-Content $signingFile) {
        if ($line -match '^\s*([A-Za-z]+)\s*=\s*(.*?)\s*$') { $signing[$Matches[1]] = $Matches[2] }
    }
    if (-not $signing.storeFile -or -not (Test-Path $signing.storeFile)) { throw "The keystore named in $signingFile does not exist." }
    if ($signing.javaHome) { $env:JAVA_HOME = $signing.javaHome }
    if (-not $env:JAVA_HOME) { throw 'No JDK: set JAVA_HOME, or javaHome in the signing properties.' }

    Invoke-Step 'Tests' {
        dotnet test (Join-Path $win 'tests\AiUsage.Core.Tests') -c Release --nologo -v q
        if ($LASTEXITCODE -eq 0) { dotnet test (Join-Path $server 'tests\AiUsage.Server.Tests') -c Release --nologo -v q }
    }

    if (Test-Path $extra) { Get-ChildItem $extra | Remove-Item -Recurse -Force }
    New-Item -ItemType Directory -Force $extra | Out-Null

    # vpk pack builds a delta against the newest full package in the output
    # folder, so that folder holds the previous release and nothing else. A
    # release builds on the last release; a beta on the last one of either kind.
    # With no earlier release vpk says so and pack makes no delta.
    Invoke-Step 'Windows: fetch the previous release' {
        if (Test-Path $releases) { Get-ChildItem $releases | Remove-Item -Recurse -Force }
        vpk download github --skip-updates --repoUrl $url --channel win --outputDir $releases --token $token @pre
    }

    Invoke-Step 'Windows: pack' { & (Join-Path $win 'pack.ps1') -Version $Version }

    foreach ($file in 'usagetop-win-Setup.exe', "usagetop-$Version-full.nupkg", 'assets.win.json') {
        if (-not (Test-Path (Join-Path $releases $file))) { throw "pack.ps1 did not produce $file." }
    }
    $product = (Get-Item (Join-Path $win 'build\publish\AiUsage.exe')).VersionInfo.ProductVersion
    if ($product -ne $Version) { throw "AiUsage.exe says $product, expected $Version." }

    # Its unit tests, then the release build. Gradle names the file
    # app-release.apk only when it signed it; unsigned it would be
    # app-release-unsigned.apk, and the check below fails.
    Invoke-Step 'Android: test and build' {
        Push-Location $android
        try { & .\gradlew.bat --no-daemon -q testDebugUnitTest assembleRelease "-PreleaseVersion=$Version" }
        finally { Pop-Location }
    }
    $apk = Join-Path $android 'app\build\outputs\apk\release\app-release.apk'
    if (-not (Test-Path $apk)) { throw 'The Android build produced no signed app-release.apk.' }
    Copy-Item $apk (Join-Path $extra 'usagetop-android.apk')

    # Self-contained, so a machine needs no .NET to run it: unpack and start
    # ./usagetop-server/aiusage-server.
    foreach ($rid in 'linux-x64', 'linux-arm64') {
        Invoke-Step "Server: $rid" {
            $out = Join-Path $server "build\publish-$rid"
            if (Test-Path $out) { Remove-Item $out -Recurse -Force }
            dotnet publish (Join-Path $server 'src\AiUsage.Server') -c Release -r $rid --self-contained true --nologo -v q `
                -p:Version=$Version -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none -o $out
            if ($LASTEXITCODE -eq 0) {
                Write-TarGz $out (Join-Path $extra "usagetop-server-$rid.tar.gz") 'usagetop-server' @('aiusage-server')
            }
        }
    }

    # The same for the Linux app, under the names its updater looks for. The
    # folder is what install.sh installs from: ./usagetop/install.sh puts it in
    # ~/.local/share/usagetop/app, and from there it updates itself.
    foreach ($rid in 'linux-x64', 'linux-arm64') {
        Invoke-Step "Linux: $rid" {
            $out = Join-Path $linux "build\publish-$rid"
            if (Test-Path $out) { Remove-Item $out -Recurse -Force }
            dotnet publish (Join-Path $linux 'src\AiUsage.Linux') -c Release -r $rid --self-contained true --nologo -v q `
                -p:Version=$Version -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none -o $out
            if ($LASTEXITCODE -eq 0) {
                Copy-Item (Join-Path $linux 'install.sh') $out
                Copy-Item (Join-Path $linux 'gnome-extension') $out -Recurse
                Copy-Item (Join-Path $linux 'plasma-widget') $out -Recurse
                New-Item -ItemType Directory -Force (Join-Path $out 'fonts') | Out-Null
                Copy-Item (Join-Path $linux 'fonts\usagetop-marks.otf') (Join-Path $out 'fonts')
                Write-TarGz $out (Join-Path $extra "usagetop-linux-$rid.tar.gz") 'usagetop' @('usagetop', 'install.sh')
            }
        }
    }

    Invoke-Step 'Tag' {
        if (-not (git tag --list $tag)) { git tag $tag $sha }
        elseif ((git rev-parse "$tag^{commit}") -ne $sha) { throw "Tag $tag exists and points at another commit." }
        git push "$url.git" $tag
    }

    # vpk creates the release as a draft and uploads the files this build made,
    # then a releases.win.json listing only this release's packages: that feed
    # is what the Windows app reads. The one pack wrote locally also lists the
    # previous release's package, which is not on this release.
    Invoke-Step 'Upload: Windows' {
        vpk upload github --skip-updates --repoUrl $url --channel win --outputDir $releases --token $token `
            --tag $tag --releaseName "usagetop $Version" @pre
    }

    Invoke-Step 'Upload: Android, Linux and server' {
        gh release upload $tag -R $repo @(Get-ChildItem $extra -File | ForEach-Object FullName)
    }

    Invoke-Step 'Notes' {
        [string[]]$filter = if ($beta) { @() } else { '--exclude-pre-releases' }
        $previous = gh release list -R $repo --exclude-drafts @filter --limit 1 --json tagName --jq '.[0].tagName // empty'
        $fields = @('-f', "tag_name=$tag")
        if ($previous) { $fields += @('-f', "previous_tag_name=$previous") }
        $notes = Join-Path ([IO.Path]::GetTempPath()) "usagetop-$Version-notes.md"
        gh api -X POST "repos/$repo/releases/generate-notes" @fields --jq .body | Set-Content $notes -Encoding utf8
        gh release edit $tag -R $repo --notes-file $notes --prerelease=$($beta.ToString().ToLower()) | Out-Null
    }

    if ($Draft) {
        Write-Host "`nLeft as a draft: $url/releases" -ForegroundColor Yellow
        return
    }
    Invoke-Step 'Publish' {
        gh release edit $tag -R $repo --draft=false --latest=$((-not $beta).ToString().ToLower()) | Out-Null
    }
    Write-Host "`nPublished $url/releases/tag/$tag" -ForegroundColor Green
}
finally {
    Pop-Location
}
