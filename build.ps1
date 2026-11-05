param([string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows x64 and .NET Framework 4.8 are required.' }
$outputDir = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $taskRoot $OutputDirectory }
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# Generate a small code-drawn application icon; no downloaded assets or packages.
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = 'AntiAlias'
$graphics.Clear([System.Drawing.Color]::FromArgb(18,23,32))
$body = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(75,95,105))
$top = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(35,48,57))
$accent = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(108,232,210)),5
$graphics.FillRectangle($body,17,18,30,28)
$graphics.FillEllipse($body,17,35,30,16)
$graphics.FillEllipse($top,17,12,30,15)
$graphics.DrawArc($accent,19,35,26,13,0,180)
$iconHandle = $bitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($iconHandle)
$iconPath = Join-Path $outputDir 'SoundcoreDesk.ico'
$file = [System.IO.File]::Create($iconPath)
try { $icon.Save($file) } finally { $file.Dispose(); $icon.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $body.Dispose(); $top.Dispose(); $accent.Dispose() }

$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:' + (Join-Path $outputDir 'SoundcoreDesk.exe')),('/win32manifest:' + (Join-Path $taskRoot 'src\app.manifest')),('/win32icon:' + $iconPath),('/resource:' + $iconPath + ',SoundcoreDesk.ico'),('/resource:' + (Join-Path $taskRoot 'src\MainWindow.xaml') + ',SoundcoreDesk.MainWindow.xaml'),('/resource:' + (Join-Path $taskRoot 'src\Theme.xaml') + ',SoundcoreDesk.Theme.xaml'),('/resource:' + (Join-Path $taskRoot 'src\SyncTestWindow.xaml') + ',SoundcoreDesk.SyncTestWindow.xaml'),('/resource:' + (Join-Path $taskRoot 'src\SurroundWindow.xaml') + ',SoundcoreDesk.SurroundWindow.xaml'),('/resource:' + (Join-Path $taskRoot 'src\SpeakerCard.xaml') + ',SoundcoreDesk.SpeakerCard.xaml'))
$compilerArgs += $references | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$compilerArgs += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host ('Built: ' + (Join-Path $outputDir 'SoundcoreDesk.exe'))
