param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference = 'Stop'
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path $csc)) { throw 'csc.exe not found; install .NET Framework 4.x' }
$src = Join-Path $PSScriptRoot 'src'
$sources = @('Program.cs','NativeDesktopHost.cs','DesktopFolderForm.cs','AppController.cs','Model.cs','Dialogs.cs','HomeForm.cs','PanelForm.cs','GlassMotion.cs') |
    ForEach-Object { Join-Path $src $_ }
$exe = Join-Path $OutputDirectory 'MacDeskNative.exe'
& $csc /nologo /langversion:4 /codepage:65001 /warnaserror+ /target:winexe `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll `
    ('/win32manifest:' + (Join-Path $src 'Native.manifest')) ('/out:' + $exe) $sources
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
Write-Output $exe
