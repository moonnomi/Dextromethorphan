param(
    [ValidateSet(10000, 50000)]
    [int]$Tracks = 10000,
    [int]$Seed = 20260725,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Output,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) {
    $label = if ($Tracks -eq 10000) { '10k' } else { '50k' }
    $Output = Join-Path $root "performance-fixtures\library-$label-seed-$Seed"
}
$Output = [System.IO.Path]::GetFullPath($Output)
$project = Join-Path $root 'tools\Dextromethorphan.PerformanceFixtures\Dextromethorphan.PerformanceFixtures.csproj'
$arguments = @(
    'run',
    '--project', $project,
    '-c', $Configuration,
    '--no-restore',
    '--no-build',
    '--',
    '--tracks', $Tracks,
    '--seed', $Seed,
    '--output', $Output
)
if ($Force) { $arguments += '--force' }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed with exit code $LASTEXITCODE." }

Write-Host ''
Write-Host 'Launch this isolated fixture with:'
Write-Host "  .\scripts\Start-PerformanceFixture.ps1 -Output `"$Output`""
