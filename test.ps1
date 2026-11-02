$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\build.ps1"
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$sources = @(Get-ChildItem "$PSScriptRoot\src\*.cs" | ForEach-Object { $_.FullName })
& $compiler /nologo /target:exe /main:Tests /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/out:$PSScriptRoot\dist\Tests.exe" $sources "$PSScriptRoot\tests\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& "$PSScriptRoot\dist\Tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
