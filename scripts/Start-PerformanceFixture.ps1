param(
    [ValidateSet(10000, 50000, 100000)]
    [int]$Tracks = 10000,
    [int]$Seed = 20260725,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) {
    $label = switch ($Tracks) {
        10000 { '10k' }
        50000 { '50k' }
        100000 { '100k' }
    }
    $Output = Join-Path $root "performance-fixtures\library-$label-seed-$Seed"
}
$Output = [System.IO.Path]::GetFullPath($Output)
$manifest = Join-Path $Output 'fixture.json'
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "Performance fixture not found: $manifest. Run New-PerformanceFixture.ps1 first."
}

$env:DEXTROMETHORPHAN_DATA_ROOT = $Output
Write-Host "Using isolated app data: $Output"
Write-Host 'Synthetic media paths are metadata-only; use the fixture for browsing performance, not playback tests.'
& dotnet run --project (Join-Path $root 'src\Dextromethorphan.App\Dextromethorphan.App.csproj') -c Release --no-restore --no-build
if ($LASTEXITCODE -ne 0) { throw "Dextromethorphan exited with code $LASTEXITCODE." }
