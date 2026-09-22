$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'native/out/UnitTests.exe'
New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
$sources = @('Updater.cs','Profiles.cs','Setup.cs','Bridge.cs','UnitTests.cs') | ForEach-Object { Join-Path $root "native/$_" }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /codepage:65001 /target:exe /main:UnitTests /platform:x64 /r:Microsoft.CSharp.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/out:$output" @sources
if ($LASTEXITCODE -ne 0) { throw 'Unit test build failed' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed' }
