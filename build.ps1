$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
New-Item -ItemType Directory -Path "$PSScriptRoot\dist" -Force | Out-Null
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/out:$PSScriptRoot\dist\RegionChangeClicker.exe" "$PSScriptRoot\src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host 'Built dist\RegionChangeClicker.exe'
