[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$config = Get-Content src-tauri/tauri.conf.json -Raw | ConvertFrom-Json
$version = $config.version
$tag = "v$version"
$repo = 'tearded/tpf-multiplayer-launcher'
$assetName = "TPF2-Multiplayer-Launcher-$version-Setup.exe"
if ((git remote get-url origin) -notmatch '^https://github.com/tearded/tpf-multiplayer-launcher(\.git)?$') { throw 'Wrong publication repository' }
git rev-parse --verify "refs/tags/$tag" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Expected a local release tag' }
$releases = gh api "repos/$repo/releases?per_page=100" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing releases' }
if (@($releases | Where-Object tag_name -EQ $tag).Count -ne 0) { throw 'Release already exists; existing assets must not be overwritten' }
foreach ($entry in @($releases | Where-Object { -not $_.draft -and -not $_.prerelease -and $_.tag_name -match '^v\d+\.\d+\.\d+$' })) {
    if ([version]$entry.tag_name.Substring(1) -ge [version]$version) { throw 'New version must be newer than existing releases' }
}
$assets = @("release/$assetName", "release/$assetName.sig", 'release/latest.json', 'release/SHA256SUMS.txt')
foreach ($asset in $assets) { if (-not (Test-Path -LiteralPath $asset)) { throw "Missing release asset $asset" } }
gh release create $tag --repo $repo --verify-tag --draft --title "$version" --notes-file "docs/releases/$version.md" @assets
if ($LASTEXITCODE -ne 0) { throw 'Could not create draft release' }
$verifyDir = Join-Path $env:TEMP ('tpf2-launcher-release-' + [Guid]::NewGuid().ToString('N'))
gh release download $tag --repo $repo --dir $verifyDir
if ($LASTEXITCODE -ne 0) { throw 'Draft download verification failed' }
foreach ($asset in $assets) {
    $downloaded = Join-Path $verifyDir (Split-Path $asset -Leaf)
    if ((Get-FileHash -LiteralPath $asset).Hash -ne (Get-FileHash -LiteralPath $downloaded).Hash) { throw 'Draft download hash mismatch' }
}
gh release edit $tag --repo $repo --draft=false --latest
if ($LASTEXITCODE -ne 0) { throw 'Could not publish verified draft' }
$latest = gh api "repos/$repo/releases/latest" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $latest.tag_name -ne $tag) { throw 'Published feed verification failed' }
Write-Output $latest.html_url
