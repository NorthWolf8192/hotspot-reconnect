param([string]$OutputPath, [switch]$ConsoleBuild)
$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkPath 'csc.exe'))) {
    $frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'The .NET Framework C# compiler was not found.' }
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot 'dist\HotspotReconnect.exe' }
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$targetKind = if ($ConsoleBuild) { 'exe' } else { 'winexe' }
$compilerArgs = @('/nologo', "/target:$targetKind", '/platform:anycpu', '/optimize+', "/out:$OutputPath",
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Xaml.dll',
    "/reference:$(Join-Path $frameworkPath 'WPF\WindowsBase.dll')",
    "/reference:$(Join-Path $frameworkPath 'WPF\PresentationCore.dll')",
    "/reference:$(Join-Path $frameworkPath 'WPF\PresentationFramework.dll')",
    "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')",
    "/win32icon:$(Join-Path $PSScriptRoot 'AppIcon.ico')",
    "/resource:$(Join-Path $PSScriptRoot 'AppIcon.ico'),HotspotReconnect.AppIcon.ico",
    "/resource:$(Join-Path $PSScriptRoot 'AppIcon.png'),HotspotReconnect.AppIcon.png",
    "/resource:$(Join-Path $PSScriptRoot 'TrayMenu.xaml'),HotspotReconnect.TrayMenu.xaml",
    "/resource:$(Join-Path $PSScriptRoot 'MainWindow.xaml'),HotspotReconnect.MainWindow.xaml",
    (Join-Path $PSScriptRoot 'HotspotReconnect.cs'), (Join-Path $PSScriptRoot 'App.cs'), (Join-Path $PSScriptRoot 'TrayMenu.cs'), (Join-Path $PSScriptRoot 'Tests.cs'))
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $OutputPath"
