$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$config = Get-Content (Join-Path $root 'src-tauri/tauri.conf.json') -Raw | ConvertFrom-Json
$version = $config.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected three-part launcher release version' }
$bundles = @(Get-ChildItem (Join-Path $root 'src-tauri/target/release/bundle/nsis') -Filter '*_x64-setup.exe' | Where-Object Name -Like "*_$($version)_*")
if ($bundles.Count -ne 1) { throw 'Expected one installer for the configured version' }
$bundle = $bundles[0]
$signatureFile = $bundle.FullName + '.sig'
if (-not (Test-Path -LiteralPath $signatureFile)) { throw 'Signed updater artifact is missing' }
$releaseDir = Join-Path $root 'release'
New-Item -ItemType Directory -Force $releaseDir | Out-Null
$assetName = "TPF2-Multiplayer-Launcher-$version-Setup.exe"
$target = Join-Path $releaseDir $assetName
Copy-Item -LiteralPath $bundle.FullName -Destination $target -Force
Copy-Item -LiteralPath $signatureFile -Destination ($target + '.sig') -Force
# The Linux AppImage built by release.yml's linux job (release-linux/), when present.
$linux = @(Get-ChildItem (Join-Path $root 'release-linux') -Filter '*.AppImage' -ErrorAction SilentlyContinue | Where-Object Name -Like "*_$($version)_*")
if ($linux.Count -gt 1) { throw 'Expected at most one AppImage for the configured version' }
$linuxName = "TPF2-Multiplayer-Launcher-$version-x86_64.AppImage"
if ($linux.Count -eq 1) {
    if (-not (Test-Path -LiteralPath ($linux[0].FullName + '.sig'))) { throw 'Signed Linux updater artifact is missing' }
    Copy-Item -LiteralPath $linux[0].FullName -Destination (Join-Path $releaseDir $linuxName) -Force
    Copy-Item -LiteralPath ($linux[0].FullName + '.sig') -Destination (Join-Path $releaseDir ($linuxName + '.sig')) -Force
}
$notes = Get-Content (Join-Path $root "docs/releases/$version.md") -Raw
$metadata = [ordered]@{
    version = $version
    notes = $notes
    pub_date = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    platforms = [ordered]@{ 'windows-x86_64' = @{ url = "https://github.com/tearded/tpf-multiplayer-launcher/releases/download/v$version/$assetName"; signature = (Get-Content -LiteralPath $signatureFile -Raw).Trim() } }
}
if ($linux.Count -eq 1) {
    $metadata.platforms['linux-x86_64'] = @{ url = "https://github.com/tearded/tpf-multiplayer-launcher/releases/download/v$version/$linuxName"; signature = (Get-Content -LiteralPath ($linux[0].FullName + '.sig') -Raw).Trim() }
}
[IO.File]::WriteAllText((Join-Path $releaseDir 'latest.json'), ($metadata | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$hash = (Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant()
$sums = "$hash  $assetName`n"
if ($linux.Count -eq 1) { $sums += (Get-FileHash -LiteralPath (Join-Path $releaseDir $linuxName)).Hash.ToLowerInvariant() + "  $linuxName`n" }
[IO.File]::WriteAllText((Join-Path $releaseDir 'SHA256SUMS.txt'), $sums, [Text.UTF8Encoding]::new($false))
Write-Output "Prepared launcher $version ($($bundle.Length) bytes), SHA256 $hash"
