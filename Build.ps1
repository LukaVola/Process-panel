param([string]$OutputPath = (Join-Path $PSScriptRoot 'ProcessPanel.exe'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Building Process Panel requires .NET Framework 4.8 on 64-bit Windows.' }
$sources = (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File).FullName
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /langversion:5 "/out:$OutputPath" "/win32icon:$PSScriptRoot\panel.ico" "/win32manifest:$PSScriptRoot\app.manifest" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Xml.dll $sources
exit $LASTEXITCODE
