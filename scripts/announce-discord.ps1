[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$webhook = $env:DISCORD_WEBHOOK_URL
if (-not $webhook) { Write-Output 'No Discord webhook configured; skipping announcement'; return }
$config = Get-Content src-tauri/tauri.conf.json -Raw | ConvertFrom-Json
$version = $config.version
$url = "https://github.com/tearded/tpf-multiplayer-launcher/releases/tag/v$version"
$notes = (Get-Content "docs/releases/$version.md" -Raw -Encoding utf8) -replace '^\s*# .*\r?\n', ''
$notes = $notes.Trim()
if ($notes.Length -gt 4000) { $notes = $notes.Substring(0, 4000).TrimEnd() + "`n..." }
$payload = @{
    username = 'TPF2 Multiplayer Launcher'
    allowed_mentions = @{ parse = @() }
    embeds = @(@{
        title = "Version $version released"
        url = $url
        description = $notes
        color = 0x2F80ED
    })
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Uri $webhook -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload)) | Out-Null
Write-Output "Announced $version on Discord"
