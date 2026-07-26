param(
    [ValidateSet(10000, 50000)]
    [int]$Tracks = 10000,
    [int]$Seed = 20260725,
    [ValidateRange(1, 10)]
    [int]$WarmRuns = 3,
    [ValidateRange(100, 10000)]
    [int]$ScanFiles = 1000,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Fixture,
    [string]$Output,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$label = if ($Tracks -eq 10000) { '10k' } else { '50k' }
if ([string]::IsNullOrWhiteSpace($Fixture)) {
    $Fixture = Join-Path $root "performance-fixtures\library-$label-seed-$Seed"
}
$Fixture = [System.IO.Path]::GetFullPath($Fixture)
if (-not (Test-Path -LiteralPath (Join-Path $Fixture 'fixture.json'))) {
    throw "Performance fixture not found at $Fixture. Run New-PerformanceFixture.ps1 first."
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $Output = Join-Path $root "performance-results\$stamp-$label"
}
$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null

$project = Join-Path $root 'src\Dextromethorphan.App\Dextromethorphan.App.csproj'
if (-not $NoBuild) {
    & dotnet build $project -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
}

$targetFramework = 'net10.0-windows10.0.19041.0'
$executable = Join-Path $root "src\Dextromethorphan.App\bin\$Configuration\$targetFramework\Dextromethorphan.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Application executable not found: $executable"
}

function Get-Median([double[]]$Values) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $middle = [math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return [math]::Round($sorted[$middle], 3) }
    return [math]::Round(($sorted[$middle - 1] + $sorted[$middle]) / 2, 3)
}

$previousRoot = $env:DEXTROMETHORPHAN_DATA_ROOT
$previousCommit = $env:DEXTROMETHORPHAN_BENCHMARK_COMMIT
$env:DEXTROMETHORPHAN_DATA_ROOT = $Fixture
$env:DEXTROMETHORPHAN_BENCHMARK_COMMIT = (& git -C $root rev-parse --short HEAD 2>$null)
if ([string]::IsNullOrWhiteSpace($env:DEXTROMETHORPHAN_BENCHMARK_COMMIT)) {
    $env:DEXTROMETHORPHAN_BENCHMARK_COMMIT = 'working-tree'
}

$reports = @()
try {
    $totalRuns = 1 + $WarmRuns
    for ($index = 0; $index -lt $totalRuns; $index++) {
        $kind = if ($index -eq 0) { 'cold' } else { 'warm' }
        $reportPath = Join-Path $Output ("run-{0:D2}-{1}.json" -f ($index + 1), $kind)
        $arguments = @(
            '--performance-benchmark', "`"$reportPath`"",
            '--benchmark-kind', $kind,
            '--benchmark-scan-files', $ScanFiles
        )
        if ($index -eq 0) { $arguments += '--benchmark-workloads' }

        Write-Host "Running $kind benchmark $($index + 1)/$totalRuns. Keep the benchmark window visible and unobstructed."
        $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -Wait
        if (-not (Test-Path -LiteralPath $reportPath)) {
            throw "Benchmark did not produce $reportPath (exit code $($process.ExitCode))."
        }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if ($report.error) { throw "Benchmark failed: $($report.error)" }
        $reports += $report
    }
}
finally {
    $env:DEXTROMETHORPHAN_DATA_ROOT = $previousRoot
    $env:DEXTROMETHORPHAN_BENCHMARK_COMMIT = $previousCommit
}

$cold = $reports[0]
$warm = @($reports | Where-Object runKind -eq 'warm')
$cachedTabs = @($reports | ForEach-Object { $_.tabSwitches } | Where-Object pass -eq 'cached')
$tabViews = @('Albums', 'Artists', 'Genres', 'Songs', 'Folders', 'Playlists')
$cachedTabByView = [ordered]@{}
foreach ($view in $tabViews) {
    $values = @($cachedTabs | Where-Object view -eq $view | ForEach-Object { [double]$_.latencyMs })
    $cachedTabByView[$view] = [ordered]@{
        medianMs = Get-Median $values
        maximumMs = [math]::Round(($values | Measure-Object -Maximum).Maximum, 3)
    }
}
$summary = [ordered]@{
    schemaVersion = 1
    capturedAt = (Get-Date).ToUniversalTime().ToString('o')
    fixture = $cold.fixture
    machine = $cold.machine
    runs = $reports.Count
    coldStartInteractiveMs = $cold.startup.processToInteractiveMs
    warmStartInteractiveMedianMs = Get-Median @($warm | ForEach-Object { [double]$_.startup.processToInteractiveMs })
    firstArtworkMedianMs = Get-Median @($reports | ForEach-Object { [double]$_.startup.processToFirstArtworkMs })
    cachedTabSwitchMedianMs = Get-Median @($cachedTabs | ForEach-Object { [double]$_.latencyMs })
    cachedTabSwitchMaximumMs = [math]::Round(($cachedTabs | Measure-Object latencyMs -Maximum).Maximum, 3)
    cachedTabByView = $cachedTabByView
    scrollP95FrameMedianMs = Get-Median @($reports | ForEach-Object { [double]$_.albumScroll.p95Ms })
    scrollMaximumFrameMs = [math]::Round(($reports | ForEach-Object { $_.albumScroll.maximumMs } | Measure-Object -Maximum).Maximum, 3)
    scrollFramesOver50Ms = ($reports | ForEach-Object { $_.albumScroll.over50Ms } | Measure-Object -Sum).Sum
    maximumWorkingSetBytes = ($reports | ForEach-Object { $_.resources.peakWorkingSetBytes } | Measure-Object -Maximum).Maximum
    idleCpuMedianPercent = Get-Median @($reports | ForEach-Object { [double]$_.cpu.idlePercent })
    playbackCpuPercent = $cold.cpu.playbackPercent
    playbackStatus = $cold.cpu.playbackStatus
    scanFilesPerSecond = $cold.scan.filesPerSecond
    scanImported = $cold.scan.imported
    scanFailed = $cold.scan.failed
}
$summaryPath = Join-Path $Output 'summary.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8

$workingSetMb = [math]::Round($summary.maximumWorkingSetBytes / 1MB, 1)
$markdown = @"
# Dextromethorphan performance baseline

Captured: $($summary.capturedAt)

| Metric | Result |
|---|---:|
| Cold process to interactive | $($summary.coldStartInteractiveMs) ms |
| Warm process to interactive, median | $($summary.warmStartInteractiveMedianMs) ms |
| First artwork, median | $($summary.firstArtworkMedianMs) ms |
| Cached tab switch, median | $($summary.cachedTabSwitchMedianMs) ms |
| Cached tab switch, maximum | $($summary.cachedTabSwitchMaximumMs) ms |
| Album scroll p95 frame, median | $($summary.scrollP95FrameMedianMs) ms |
| Album scroll worst frame | $($summary.scrollMaximumFrameMs) ms |
| Album scroll frames over 50 ms | $($summary.scrollFramesOver50Ms) |
| Peak working set | $workingSetMb MB |
| Idle CPU, median | $($summary.idleCpuMedianPercent)% |
| Playback CPU | $($summary.playbackCpuPercent)% |
| Scan throughput | $($summary.scanFilesPerSecond) files/s |

Cold means the first fresh process in this run. The script does not purge the Windows standby list; perform the first run after reboot when a true filesystem-cold number is required.
"@
$markdown | Set-Content -LiteralPath (Join-Path $Output 'summary.md') -Encoding utf8

Write-Host ''
Write-Host "Baseline complete: $summaryPath"
Write-Host "Cold interactive: $($summary.coldStartInteractiveMs) ms"
Write-Host "Cached tab median/max: $($summary.cachedTabSwitchMedianMs) / $($summary.cachedTabSwitchMaximumMs) ms"
Write-Host "Scroll p95/worst: $($summary.scrollP95FrameMedianMs) / $($summary.scrollMaximumFrameMs) ms"
