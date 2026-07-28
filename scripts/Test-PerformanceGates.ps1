param(
    [Parameter(Mandatory)]
    [string]$Summary,
    [string]$Gates,
    [switch]$ReportOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$summaryPath = [System.IO.Path]::GetFullPath($Summary)
if (-not (Test-Path -LiteralPath $summaryPath)) {
    throw "Performance summary not found: $summaryPath"
}
if ([string]::IsNullOrWhiteSpace($Gates)) {
    $Gates = Join-Path $root 'docs\performance\release-gates.json'
}
$gatesPath = [System.IO.Path]::GetFullPath($Gates)
if (-not (Test-Path -LiteralPath $gatesPath)) {
    throw "Release-gate configuration not found: $gatesPath"
}

$summaryData = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$gateData = Get-Content -LiteralPath $gatesPath -Raw | ConvertFrom-Json
if ($summaryData.schemaVersion -ne 1) {
    throw "Unsupported performance summary schema: $($summaryData.schemaVersion)"
}
if ($gateData.schemaVersion -ne 1) {
    throw "Unsupported release-gate schema: $($gateData.schemaVersion)"
}
if ($null -eq $summaryData.fixture.tracks) {
    throw 'The performance summary does not contain fixture track count.'
}

$checks = [System.Collections.Generic.List[object]]::new()
function Add-MaximumCheck {
    param(
        [string]$Name,
        [double]$Actual,
        [double]$Maximum,
        [string]$Unit,
        [bool]$Strict = $false
    )
    $passed = if ($Strict) { $Actual -lt $Maximum } else { $Actual -le $Maximum }
    $checks.Add([pscustomobject]@{
        Gate = $Name
        Actual = [math]::Round($Actual, 3)
        Limit = $(if ($Strict) { "< $Maximum $Unit" } else { "<= $Maximum $Unit" })
        Result = $(if ($passed) { 'PASS' } else { 'FAIL' })
        Passed = $passed
    })
}

$tracks = [int]$summaryData.fixture.tracks
if ($tracks -eq 10000) {
    Add-MaximumCheck 'Cold start to interactive' ([double]$summaryData.coldStartInteractiveMs) ([double]$gateData.coldStart10kMaximumMs) 'ms' $true
}
$cachedTabGateValue = if ($null -ne $summaryData.cachedTabSwitchWorstViewMedianMs) {
    [double]$summaryData.cachedTabSwitchWorstViewMedianMs
} else {
    [double]$summaryData.cachedTabSwitchMaximumMs
}
Add-MaximumCheck 'Cached tab switch worst per-view median' $cachedTabGateValue ([double]$gateData.cachedTabSwitchMaximumMs) 'ms' $true
if ($null -ne $summaryData.navigationHistoryPassed) {
    $historyPassed = [bool]$summaryData.navigationHistoryPassed
    $checks.Add([pscustomobject]@{
        Gate = 'Navigation history state restoration'
        Actual = $historyPassed
        Limit = 'True'
        Result = $(if ($historyPassed) { 'PASS' } else { 'FAIL' })
        Passed = $historyPassed
    })
}
if ($null -ne $summaryData.hiddenViewReleasePassed) {
    $hiddenPassed = [bool]$summaryData.hiddenViewReleasePassed
    $checks.Add([pscustomobject]@{
        Gate = 'Hidden view artwork release'
        Actual = $hiddenPassed
        Limit = 'True'
        Result = $(if ($hiddenPassed) { 'PASS' } else { 'FAIL' })
        Passed = $hiddenPassed
    })
}
if ($null -ne $summaryData.pagedSongsPassed) {
    $songsPassed = [bool]$summaryData.pagedSongsPassed
    $checks.Add([pscustomobject]@{
        Gate = 'Paged Songs presentation'
        Actual = $songsPassed
        Limit = 'True'
        Result = $(if ($songsPassed) { 'PASS' } else { 'FAIL' })
        Passed = $songsPassed
    })
}
if ($null -ne $summaryData.concurrentWorkloadPassed) {
    $concurrentPassed = [bool]$summaryData.concurrentWorkloadPassed
    $checks.Add([pscustomobject]@{
        Gate = 'Concurrent scan, playback, and navigation'
        Actual = $concurrentPassed
        Limit = 'True'
        Result = $(if ($concurrentPassed) { 'PASS' } else { 'FAIL' })
        Passed = $concurrentPassed
    })
}
Add-MaximumCheck 'Album scroll p95 frame' ([double]$summaryData.scrollP95FrameMedianMs) ([double]$gateData.scrollP95FrameMaximumMs) 'ms'
Add-MaximumCheck 'Album scroll worst frame' ([double]$summaryData.scrollMaximumFrameMs) ([double]$gateData.scrollFrameMaximumMs) 'ms'
Add-MaximumCheck 'Album scroll frames over 50 ms' ([double]$summaryData.scrollFramesOver50Ms) ([double]$gateData.scrollFramesOver50Maximum) 'frames'
Add-MaximumCheck 'Idle CPU' ([double]$summaryData.idleCpuMedianPercent) ([double]$gateData.idleCpuMaximumPercent) '%' $true
if ($tracks -eq 50000) {
    Add-MaximumCheck 'Settled working set at 50k' ([double]$summaryData.maximumWorkingSetBytes) ([double]$gateData.workingSet50kMaximumBytes) 'bytes' $true
}

$failed = @($checks | Where-Object { -not $_.Passed })
Write-Host ''
Write-Host "Performance release gates: $summaryPath"
$checks | Select-Object Gate,Actual,Limit,Result | Format-Table -AutoSize
Write-Host "Result: $($checks.Count - $failed.Count)/$($checks.Count) gates passed."

if ($failed.Count -gt 0 -and -not $ReportOnly) {
    exit 1
}
