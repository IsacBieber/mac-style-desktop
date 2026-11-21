param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'), [switch]$NoDeploy)
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
# Layout: src\Core = backend (no UI), src\UI = frontend, src\Host = entry point + desktop hosting.
$ErrorActionPreference = 'Stop'
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path $csc)) { throw 'csc.exe not found; install .NET Framework 4.x' }
$src = Join-Path $PSScriptRoot 'src'
$core = @(Get-ChildItem (Join-Path $src 'Core') -Filter *.cs | Sort-Object Name | ForEach-Object FullName)
$ui = @(Get-ChildItem (Join-Path $src 'UI'), (Join-Path $src 'Host') -Filter *.cs -Recurse | Sort-Object FullName | ForEach-Object FullName)
$common = @('/nologo', '/langversion:4', '/codepage:65001', '/optimize+', '/warnaserror+')

# 1) Backend boundary check: Core must compile on its own, WITHOUT System.Windows.Forms.
#    If anyone makes the backend depend on UI code, the build stops here.
$coreDll = Join-Path $OutputDirectory 'MacDesk.Core.dll'
& $csc @common /target:library /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll ('/out:' + $coreDll) $core
if ($LASTEXITCODE -ne 0) { throw 'backend (src\Core) failed to build on its own - it must not reference UI code' }

# 2) The app: one self-contained exe (backend + frontend compiled together).
$exe = Join-Path $OutputDirectory 'MacDesk.exe'
$icon = Join-Path $PSScriptRoot 'assets\app.ico'
$iconArg = if (Test-Path $icon) { '/win32icon:' + $icon } else { $null }
& $csc @common /target:winexe `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll `
    ('/win32manifest:' + (Join-Path $src 'Native.manifest')) $iconArg ('/out:' + $exe) ($core + $ui)
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

# 3) Deploy to a Medium-integrity folder (the repo folder is Low-IL; see README).
if (-not $NoDeploy) {
 $docDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'MacDesk'
 [void][IO.Directory]::CreateDirectory($docDir)
 [IO.File]::WriteAllBytes((Join-Path $docDir 'MacDesk.exe'), [IO.File]::ReadAllBytes($exe))
}
Write-Output $exe
