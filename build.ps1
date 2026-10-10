# Builds LiveEarth.exe with the C# compiler that ships with Windows (.NET Framework 4.x) - no SDK needed.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /platform:x64 /out:"$here\LiveEarth.exe" `
    /r:System.Drawing.dll /r:System.Windows.Forms.dll "$here\src\LiveEarth.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Output "built $here\LiveEarth.exe"
