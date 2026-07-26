param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Output,
    [string]$Session = 'interactive',
    [switch]$VerboseTrace,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $Output = Join-Path $root "artifacts\diagnostics\sessions\$stamp"
}
$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null

$project = Join-Path $root 'src\Dextromethorphan.App\Dextromethorphan.App.csproj'
if (-not $NoBuild) {
    & dotnet build $project -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Application build failed with exit code $LASTEXITCODE." }
}

$targetFramework = 'net10.0-windows10.0.19041.0'
$executable = Join-Path $root "src\Dextromethorphan.App\bin\$Configuration\$targetFramework\Dextromethorphan.exe"
if (-not (Test-Path -LiteralPath $executable)) { throw "Application executable not found: $executable" }

$arguments = @('--diagnostics', '--diagnostics-output', "`"$Output`"", '--diagnostics-session', "`"$Session`"")
if ($VerboseTrace) { $arguments += '--diagnostics-verbose' }

Write-Host "Starting diagnostic session. Logs will be written to $Output"
Write-Host 'Use the app normally and reproduce the lag or error, then close it to finalize the summary.'
$process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -Wait

$summary = Get-ChildItem -LiteralPath $Output -File -Filter '*-summary.json' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($summary) { Write-Host "Diagnostic summary: $($summary.FullName)" }
if ($process.ExitCode -ne 0) { throw "Dextromethorphan exited with code $($process.ExitCode)." }
