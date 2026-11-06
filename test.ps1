param([switch]$Desktop)
$ErrorActionPreference = 'Stop'
$testDirectory = Join-Path $PSScriptRoot '.test-output'
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
$testExecutable = Join-Path $testDirectory 'HotspotReconnect.Tests.exe'
& (Join-Path $PSScriptRoot 'build.ps1') -ConsoleBuild -OutputPath $testExecutable
if ($Desktop) {
    & $testExecutable --self-test (Join-Path $testDirectory 'window-light.png')
} else {
    & $testExecutable --self-test-core
}
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
