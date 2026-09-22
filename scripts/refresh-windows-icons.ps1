$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$installDir = Join-Path $env:LOCALAPPDATA 'TPF2 Multiplayer Launcher'
$exe = Join-Path $installDir 'tpf2-launcher.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Launcher installation not found.' }

# Give Explorer a new cache key when the icon artwork changes.
$sourceIcon = Join-Path $root 'src-tauri/icons/icon.ico'
$iconHash = (Get-FileHash -LiteralPath $sourceIcon -Algorithm SHA256).Hash.Substring(0, 12)
$iconPath = Join-Path $installDir "launcher-$iconHash.ico"
Copy-Item -LiteralPath $sourceIcon -Destination $iconPath -Force
$shell = New-Object -ComObject WScript.Shell
$shortcutRoots = @(
    [Environment]::GetFolderPath('Desktop'),
    (Join-Path $env:APPDATA 'Microsoft/Windows/Start Menu/Programs'),
    (Join-Path $env:APPDATA 'Microsoft/Internet Explorer/Quick Launch/User Pinned/TaskBar')
)
foreach ($shortcutRoot in $shortcutRoots) {
    if (-not (Test-Path -LiteralPath $shortcutRoot)) { continue }
    Get-ChildItem -LiteralPath $shortcutRoot -Filter '*.lnk' -Recurse | ForEach-Object {
        $shortcut = $shell.CreateShortcut($_.FullName)
        if ($shortcut.TargetPath -eq $exe) {
            $shortcut.IconLocation = "$iconPath,0"
            $shortcut.Save()
            Write-Output "Updated icon: $($_.FullName)"
        }
    }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LauncherIconRefresh {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
'@
[LauncherIconRefresh]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
