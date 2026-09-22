$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'src-tauri/resources/TPF2Launcher.Native.exe'
New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
$sources = @('Updater.cs','Profiles.cs','Setup.cs','Bridge.cs') | ForEach-Object { Join-Path $root "native/$_" }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /codepage:65001 /target:exe /main:NativeBridge /platform:x64 /optimize+ /r:Microsoft.CSharp.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/out:$output" @sources
if ($LASTEXITCODE -ne 0) { throw 'Native launcher build failed' }
