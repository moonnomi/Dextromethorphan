param(
    [Parameter(Mandatory = $true)]
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
if (-not (Test-Path -LiteralPath $ReportPath)) {
    throw "Audio soak report was not found: $ReportPath"
}

$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$failures = [System.Collections.Generic.List[string]]::new()
function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $failures.Add($Message) }
}

Require ($report.SchemaVersion -ge 3) 'Report schema must be at least 3.'
Require ($report.ReportState -eq 'Completed') 'Report state must be Completed.'
Require ([bool]$report.Completed) 'Runner did not complete.'
Require (-not [bool]$report.Cancelled) 'Runner was cancelled.'
Require ([bool]$report.RunPassed) 'Requested soak run did not pass.'
Require ([bool]$report.Qualified) 'Eight-hour milestone qualification is false.'
Require ([double]$report.RequestedDurationSeconds -ge 28800) 'Requested duration was below eight hours.'
Require ([double]$report.ObservedPlayingSeconds -ge 28800) 'Observed Playing time was below eight hours.'
Require (@($report.Faults).Count -eq 0) 'Faults were recorded.'
Require ([int]$report.PrematurePlaybackEnds -eq 0) 'Playback ended prematurely.'
Require ([long]$report.FinalDiagnostics.Underruns -eq 0) 'Callback deadline misses were recorded.'
Require ([int]$report.FinalDiagnostics.RecoveryAttempts -eq 0) 'Endpoint recovery attempts were recorded.'
Require ([bool]$report.EndpointVolume.Unchanged) 'Endpoint volume was not invariant.'
Require (-not [bool]$report.EndpointVolume.ObservedChanged) 'Endpoint volume changed during a checkpoint.'
Require ([long]$report.Memory.PeakGrowthBytes -le 134217728) 'Peak working-set growth exceeded 128 MiB.'

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine(
        "Audio soak qualification failed:`n- " +
        ($failures -join "`n- "))
    exit 2
}

Write-Host 'Audio soak qualification PASSED.'
Write-Host "Observed playing: $([TimeSpan]::FromSeconds([double]$report.ObservedPlayingSeconds))"
Write-Host "Transitions: $($report.Transitions)"
Write-Host "Peak working-set growth: $([Math]::Round([double]$report.Memory.PeakGrowthBytes / 1MB, 1)) MiB"
exit 0
