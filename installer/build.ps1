$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskPayload = Join-Path $taskRepo 'bin\Release\net6.0\FlyoutMouseAim.dll'
if (-not (Test-Path -LiteralPath $taskPayload)) { throw 'Build FlyoutMouseAim.csproj in Release mode first.' }
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$taskOutputDir = Join-Path $taskRepo 'bin\installer'
New-Item -ItemType Directory -Path $taskOutputDir -Force | Out-Null
$taskOutput = Join-Path $taskOutputDir '(optional)-War-Thunder-Style-Mouse-Controls-v1-Installer.exe'
& $taskCompiler /nologo /target:winexe /platform:anycpu /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll "/win32manifest:$PSScriptRoot\installer.manifest" "/resource:$taskPayload,FlyoutMouseAim.dll" "/out:$taskOutput" "$PSScriptRoot\OptionalInstaller.cs"
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
Write-Output $taskOutput
