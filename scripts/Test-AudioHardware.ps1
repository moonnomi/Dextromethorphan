param(
    [string]$DeviceId = 'default',
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $Output = Join-Path $root "artifacts\audio-qualification\$stamp.json"
}
$Output = [System.IO.Path]::GetFullPath($Output)
$project = Join-Path $root 'tests\Dextromethorphan.Tests\Dextromethorphan.Tests.csproj'

$previousRun = $env:DEXTROMETHORPHAN_RUN_AUDIO_HARDWARE_TESTS
$previousDevice = $env:DEXTROMETHORPHAN_AUDIO_DEVICE_ID
$previousReport = $env:DEXTROMETHORPHAN_AUDIO_REPORT
try {
    $env:DEXTROMETHORPHAN_RUN_AUDIO_HARDWARE_TESTS = '1'
    $env:DEXTROMETHORPHAN_AUDIO_DEVICE_ID = $DeviceId
    $env:DEXTROMETHORPHAN_AUDIO_REPORT = $Output
    & dotnet test $project -c Release --filter 'FullyQualifiedName~AudioHardwareQualificationTests'
    if ($LASTEXITCODE -ne 0) {
        throw "Audio hardware qualification failed with exit code $LASTEXITCODE. The report was retained at $Output."
    }
}
finally {
    $env:DEXTROMETHORPHAN_RUN_AUDIO_HARDWARE_TESTS = $previousRun
    $env:DEXTROMETHORPHAN_AUDIO_DEVICE_ID = $previousDevice
    $env:DEXTROMETHORPHAN_AUDIO_REPORT = $previousReport
}

Write-Host "Audio hardware qualification report: $Output"
