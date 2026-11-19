<#
.SYNOPSIS
Builds a release on this machine and publishes it as a GitHub Release.

.DESCRIPTION
The same steps as .github/workflows/release.yml, for when the release is not
built by GitHub Actions: tests, the previous release fetched for the delta
package, the Windows installer packed with the release's version, the tag
pushed, and the GitHub Release created with the files the apps' updater reads.

The release stays a draft until its files and notes are on it, so neither the
updater nor the /releases/latest download link ever sees half of one.

Needs: the .NET 9 SDK, the vpk tool (dotnet tool install -g vpk --version
1.2.161) and gh, signed in to an account that may write to the repository.

Files from other machines (a Mac build, an APK) are added to the same release
afterwards with: gh release upload v<version> <file>

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
$project = Join-Path $win 'src\AiUsage\AiUsage.csproj'
$releases = Join-Path $win 'build\releases'

function Invoke-Step([string]$What, [scriptblock]$Do) {
    Write-Host "`n== $What" -ForegroundColor Cyan
    & $Do
    if ($LASTEXITCODE -ne 0) { throw "$What failed." }
}

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$Version = "$Version".Trim() -replace '^v', ''
$semver = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-((0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?$'
if ($Version -cnotmatch $semver) { throw "'$Version' is not a version like 1.2.0 or 1.2.1-beta.1." }
$tag = "v$Version"
$beta = $Version.Contains('-')
$pre = if ($beta) { @('--pre') } else { @() }
Write-Host "usagetop $Version$(if ($beta) { ' (pre-release)' })"

# What is released is exactly a commit that is on GitHub: nothing uncommitted,
# nothing unpushed.
Push-Location $PSScriptRoot
try {
    if (git status --porcelain) { throw 'There are uncommitted changes. Commit or stash them first.' }
    $sha = git rev-parse HEAD
    $where = gh api "repos/$repo/compare/main...$sha" --jq .status 2>$null
    if ($where -notin 'identical', 'behind') { throw "HEAD ($($sha.Substring(0, 7))) is not on GitHub's main branch. Push it first." }
    gh release view $tag -R $repo *> $null
    if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists." }
    $global:LASTEXITCODE = 0

    $token = gh auth token
    if (-not $token) { throw 'gh is not signed in.' }

    Invoke-Step 'Tests' {
        dotnet test (Join-Path $win 'tests\AiUsage.Core.Tests') -c Release --nologo -v q
        if ($LASTEXITCODE -eq 0) { dotnet test (Join-Path $PSScriptRoot 'my-usage-server\tests\AiUsage.Server.Tests') -c Release --nologo -v q }
    }

    # vpk pack builds a delta against the newest full package in the output
    # folder, so that folder holds the previous release and nothing else. A
    # release builds on the last release; a beta on the last one of either kind.
    # With no earlier release vpk says so and pack makes no delta.
    Invoke-Step 'Fetch the previous release' {
        if (Test-Path $releases) { Get-ChildItem $releases | Remove-Item -Recurse -Force }
        vpk download github --skip-updates --repoUrl $url --channel win --outputDir $releases --token $token @pre
    }

    Invoke-Step 'Pack' { & (Join-Path $win 'pack.ps1') -Version $Version }

    foreach ($file in 'usagetop-win-Setup.exe', "usagetop-$Version-full.nupkg", 'assets.win.json') {
        if (-not (Test-Path (Join-Path $releases $file))) { throw "pack.ps1 did not produce $file." }
    }
    $product = (Get-Item (Join-Path $win 'build\publish\AiUsage.exe')).VersionInfo.ProductVersion
    if ($product -ne $Version) { throw "AiUsage.exe says $product, expected $Version." }

    Invoke-Step 'Tag' {
        if (-not (git tag --list $tag)) { git tag $tag $sha }
        elseif ((git rev-parse "$tag^{commit}") -ne $sha) { throw "Tag $tag exists and points at another commit." }
        git push "$url.git" $tag
    }

    # vpk creates the release as a draft and uploads the files this build made,
    # then a releases.win.json listing only this release's packages: that feed
    # is what the apps read. The one pack wrote locally also lists the previous
    # release's package, which is not on this release.
    Invoke-Step 'Upload' {
        vpk upload github --skip-updates --repoUrl $url --channel win --outputDir $releases --token $token `
            --tag $tag --releaseName "usagetop $Version" @pre
    }

    Invoke-Step 'Notes' {
        $filter = if ($beta) { @() } else { @('--exclude-pre-releases') }
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
