param(
    [string]$DataRoot,
    [string]$DiagnosticRoot,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = if ($env:DEXTROMETHORPHAN_DATA_ROOT) {
        $env:DEXTROMETHORPHAN_DATA_ROOT
    } else {
        Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Dextromethorphan'
    }
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
if ([string]::IsNullOrWhiteSpace($DiagnosticRoot)) {
    $DiagnosticRoot = Join-Path $root 'artifacts\diagnostics\sessions'
}
$DiagnosticRoot = [System.IO.Path]::GetFullPath($DiagnosticRoot)

$bundleRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts\diagnostics'))
New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $bundleRoot ("Dextromethorphan-Diagnostics-{0}.zip" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$Output = [System.IO.Path]::GetFullPath($Output)
$staging = [System.IO.Path]::GetFullPath((Join-Path $bundleRoot ("staging-{0}" -f [guid]::NewGuid().ToString('N'))))
if (-not $staging.StartsWith($bundleRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe staging path: $staging"
}

try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $logsTarget = Join-Path $staging 'logs'
    New-Item -ItemType Directory -Path $logsTarget -Force | Out-Null
    $appLogs = Join-Path $DataRoot 'logs'
    if (Test-Path -LiteralPath $appLogs) {
        Get-ChildItem -LiteralPath $appLogs -File |
            Where-Object Extension -in @('.log', '.json', '.jsonl') |
            Copy-Item -Destination $logsTarget
    }
    if (Test-Path -LiteralPath $DiagnosticRoot) {
        $sessionTarget = Join-Path $staging 'diagnostic-sessions'
        New-Item -ItemType Directory -Path $sessionTarget -Force | Out-Null
        Get-ChildItem -LiteralPath $DiagnosticRoot -Recurse -File |
            Where-Object Extension -in @('.json', '.jsonl') |
            Copy-Item -Destination $sessionTarget
    }

    $settingsPath = Join-Path $DataRoot 'settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw
        $settings = $settings -replace '(?i)("(?:[^"])*(?:key|token|secret|password)(?:[^"])*"\s*:\s*)"[^"]*"', '$1"<redacted>"'
        $settings | Set-Content -LiteralPath (Join-Path $staging 'settings-redacted.json') -Encoding utf8
    }

    $system = [ordered]@{
        capturedAt = (Get-Date).ToUniversalTime().ToString('o')
        os = [Environment]::OSVersion.VersionString
        framework = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        processors = [Environment]::ProcessorCount
        processorIdentifier = $env:PROCESSOR_IDENTIFIER
        workingDirectory = $root
        gitCommit = (& git -C $root rev-parse HEAD 2>$null)
        gitStatus = @(& git -C $root status --short 2>$null)
    }
    try {
        $system['videoControllers'] = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, AdapterRAM)
        $system['operatingSystem'] = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, TotalVisibleMemorySize, FreePhysicalMemory
    } catch {
        $system['cimError'] = $_.Exception.Message
    }
    $system | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $staging 'system.json') -Encoding utf8
    (& dotnet --info 2>&1) | Set-Content -LiteralPath (Join-Path $staging 'dotnet-info.txt') -Encoding utf8

    $performanceRoot = Join-Path $root 'performance-results'
    if (Test-Path -LiteralPath $performanceRoot) {
        $performanceTarget = Join-Path $staging 'performance'
        New-Item -ItemType Directory -Path $performanceTarget -Force | Out-Null
        Get-ChildItem -LiteralPath $performanceRoot -Recurse -File |
            Where-Object Name -in @('summary.json', 'summary.md') |
            ForEach-Object {
                $prefix = Split-Path $_.DirectoryName -Leaf
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $performanceTarget "$prefix-$($_.Name)") -Force
            }
    }

    $outputDirectory = Split-Path -Parent $Output
    if ($outputDirectory) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
    if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $Output -CompressionLevel Optimal
    Write-Host "Diagnostic bundle: $Output"
    Write-Host 'Review the bundle before sharing it; logs can contain local media paths and exception details.'
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
