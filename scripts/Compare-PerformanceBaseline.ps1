param(
    [Parameter(Mandatory)]
    [string]$Summary,
    [string]$Policy,
    [string]$Baseline,
    [string]$Output,
    [switch]$AllowMachineMismatch,
    [switch]$ReportOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$summaryPath = [System.IO.Path]::GetFullPath($Summary)
if (-not (Test-Path -LiteralPath $summaryPath)) {
    throw "Performance summary not found: $summaryPath"
}
if ([string]::IsNullOrWhiteSpace($Policy)) {
    $Policy = Join-Path $root 'docs\performance\regression-policy.json'
}
$policyPath = [System.IO.Path]::GetFullPath($Policy)
if (-not (Test-Path -LiteralPath $policyPath)) {
    throw "Regression policy not found: $policyPath"
}

$summaryData = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$policyData = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
if ($summaryData.schemaVersion -ne 1) {
    throw "Unsupported performance summary schema: $($summaryData.schemaVersion)"
}
if ($policyData.schemaVersion -ne 1) {
    throw "Unsupported regression-policy schema: $($policyData.schemaVersion)"
}

$trackKey = ([int]$summaryData.fixture.tracks).ToString()
if ([string]::IsNullOrWhiteSpace($Baseline)) {
    $baselineProperty = $policyData.baselines.PSObject.Properties[$trackKey]
    if ($null -eq $baselineProperty) {
        throw "No stored baseline is configured for $trackKey tracks."
    }
    $Baseline = Join-Path (Split-Path -Parent $policyPath) $baselineProperty.Value
}
$baselinePath = [System.IO.Path]::GetFullPath($Baseline)
if (-not (Test-Path -LiteralPath $baselinePath)) {
    throw "Stored performance baseline not found: $baselinePath"
}
$baselineData = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
if ($baselineData.schemaVersion -ne 1) {
    throw "Unsupported stored-baseline schema: $($baselineData.schemaVersion)"
}
if ([int]$summaryData.fixture.tracks -ne [int]$baselineData.fixture.tracks) {
    throw "Fixture size mismatch: current has $($summaryData.fixture.tracks) tracks, baseline has $($baselineData.fixture.tracks)."
}
if ($summaryData.fixture.contentSha256 -ne $baselineData.fixture.contentSha256) {
    throw 'Fixture content hash differs from the stored baseline. Regenerate the canonical fixture or select the matching baseline.'
}
if ([int]$summaryData.runs -lt [int]$policyData.minimumRuns) {
    throw "Current summary contains $($summaryData.runs) runs; regression comparison requires at least $($policyData.minimumRuns)."
}
if ([int]$baselineData.runs -lt [int]$policyData.minimumRuns) {
    throw "Stored baseline contains fewer than $($policyData.minimumRuns) runs."
}

$machineDifferences = [System.Collections.Generic.List[string]]::new()
foreach ($property in @('architecture', 'processor', 'logicalProcessors')) {
    if ($summaryData.machine.$property -ne $baselineData.machine.$property) {
        $machineDifferences.Add("${property}: '$($summaryData.machine.$property)' versus '$($baselineData.machine.$property)'")
    }
}
if ($machineDifferences.Count -gt 0 -and -not $AllowMachineMismatch) {
    throw "The current and reference machines are not comparable. $($machineDifferences -join '; '). Pass -AllowMachineMismatch only for an intentionally non-gating investigation."
}
if ($machineDifferences.Count -gt 0) {
    Write-Warning "Comparing different machines: $($machineDifferences -join '; ')"
}

function Get-NestedNumber {
    param([object]$Source, [string]$Path)
    $value = $Source
    foreach ($segment in $Path.Split('.')) {
        $property = $value.PSObject.Properties[$segment]
        if ($null -eq $property -or $null -eq $property.Value) {
            throw "Metric '$Path' is missing from a performance summary."
        }
        $value = $property.Value
    }
    return [double]$value
}

function Format-MetricValue {
    param([double]$Value, [string]$Unit)
    if ($Unit -eq 'bytes') {
        return "$([math]::Round($Value / 1MB, 1)) MB"
    }
    return "$([math]::Round($Value, 3)) $Unit".Trim()
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($metric in $policyData.metrics) {
    $reference = Get-NestedNumber $baselineData $metric.path
    $actual = Get-NestedNumber $summaryData $metric.path
    $relativeTolerance = [math]::Abs($reference) * ([double]$metric.relativeTolerancePercent / 100)
    $tolerance = [math]::Max([double]$metric.absoluteTolerance, $relativeTolerance)
    if ($metric.direction -eq 'lower') {
        $limit = $reference + $tolerance
        $passed = $actual -le $limit
    }
    elseif ($metric.direction -eq 'higher') {
        $limit = $reference - $tolerance
        $passed = $actual -ge $limit
    }
    else {
        throw "Unsupported direction '$($metric.direction)' for '$($metric.path)'."
    }
    $delta = $actual - $reference
    $deltaPercent = if ([math]::Abs($reference) -lt 0.000001) { $null } else { ($delta / $reference) * 100 }
    $results.Add([pscustomobject]@{
        path = $metric.path
        name = $metric.name
        direction = $metric.direction
        unit = $metric.unit
        baseline = [math]::Round($reference, 3)
        actual = [math]::Round($actual, 3)
        tolerance = [math]::Round($tolerance, 3)
        limit = [math]::Round($limit, 3)
        delta = [math]::Round($delta, 3)
        deltaPercent = $(if ($null -eq $deltaPercent) { $null } else { [math]::Round($deltaPercent, 2) })
        passed = $passed
        BaselineDisplay = Format-MetricValue $reference $metric.unit
        ActualDisplay = Format-MetricValue $actual $metric.unit
        LimitDisplay = Format-MetricValue $limit $metric.unit
        Result = $(if ($passed) { 'PASS' } else { 'REGRESSION' })
    })
}

$failed = @($results | Where-Object { -not $_.passed })
$comparison = [ordered]@{
    schemaVersion = 1
    comparedAt = (Get-Date).ToUniversalTime().ToString('o')
    passed = $failed.Count -eq 0
    currentSummary = $summaryPath
    baseline = $baselinePath
    fixture = $summaryData.fixture
    currentMachine = $summaryData.machine
    baselineMachine = $baselineData.machine
    machineMismatchAllowed = [bool]$AllowMachineMismatch
    results = @($results | Select-Object path,name,direction,unit,baseline,actual,tolerance,limit,delta,deltaPercent,passed)
}

if (-not [string]::IsNullOrWhiteSpace($Output)) {
    $outputPath = [System.IO.Path]::GetFullPath($Output)
    $outputDirectory = Split-Path -Parent $outputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $comparison | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputPath -Encoding utf8
}

Write-Host ''
Write-Host "Performance regression comparison"
Write-Host "Current:  $summaryPath"
Write-Host "Baseline: $baselinePath"
$results | Select-Object @{Name='Metric';Expression={$_.name}},BaselineDisplay,ActualDisplay,LimitDisplay,Result | Format-Table -AutoSize
Write-Host "Result: $($results.Count - $failed.Count)/$($results.Count) metrics within tolerance."

if ($failed.Count -gt 0 -and -not $ReportOnly) {
    exit 1
}
