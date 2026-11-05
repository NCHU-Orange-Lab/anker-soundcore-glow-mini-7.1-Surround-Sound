param([string]$AppDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $AppDirectory) { $AppDirectory = Join-Path $taskRoot 'dist' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
# Build the test runner next to a copy of the app so test binaries never land in the shipped folder.
$testBin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $testBin | Out-Null
$app = Join-Path $testBin 'SoundcoreDesk.exe'
Copy-Item -LiteralPath (Join-Path $AppDirectory 'SoundcoreDesk.exe') -Destination $app -Force
$executable = Join-Path $testBin 'DeviceIdentityTests.exe'
& $compiler /nologo /target:exe /platform:x64 ('/out:' + $executable) ('/reference:' + $app) (Join-Path $PSScriptRoot 'DeviceIdentityTests.cs') (Join-Path $PSScriptRoot 'ProtocolTests.cs') (Join-Path $PSScriptRoot 'PartyCastRecordTests.cs') (Join-Path $PSScriptRoot 'SyncTestTests.cs') (Join-Path $PSScriptRoot 'SurroundTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $executable
if ($LASTEXITCODE -ne 0) { throw 'Device identity verification failed.' }
