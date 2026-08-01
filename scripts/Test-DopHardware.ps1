param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Run')]
    [switch]$ConfirmCompatibleDac,
    [Parameter(Mandatory = $true, ParameterSetName = 'Run')]
    [ValidateNotNullOrEmpty()]
    [string]$DeviceId,
    [Parameter(Mandatory = $true, ParameterSetName = 'Run')]
    [ValidateNotNullOrEmpty()]
    [string]$DacModel,
    [Parameter(Mandatory = $true, ParameterSetName = 'Run')]
    [ValidateNotNullOrEmpty()]
    [string]$DriverVersion,
    [Parameter(Mandatory = $true, ParameterSetName = 'Run')]
    [ValidateNotNullOrEmpty()]
    [string]$Connection,
    [Parameter(Mandatory = $true, ParameterSetName = 'List')]
    [switch]$ListDevices,
    [ValidateSet('Unknown', 'Pass', 'Fail')]
    [string]$Dsd64Indication = 'Unknown',
    [ValidateSet('Unknown', 'Pass', 'Fail')]
    [string]$Dsd128Indication = 'Unknown',
    [ValidateRange(2, 1000)]
    [int]$BufferMilliseconds = 100,
    [string]$OperatorNotes = '',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($ListDevices) {
    $project = Join-Path $root 'tools/Dextromethorphan.DopQualification/Dextromethorphan.DopQualification.csproj'
    & dotnet run --project $project -c Release -- --list-devices
    exit $LASTEXITCODE
}
if ([string]::IsNullOrWhiteSpace($Output)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $Output = Join-Path $root "artifacts/audio-qualification/dop-$stamp.json"
}

Write-Warning 'This test sends exclusive DoP carriers. A non-DSD PCM device may produce noise.'
$project = Join-Path $root 'tools/Dextromethorphan.DopQualification/Dextromethorphan.DopQualification.csproj'
$arguments = @(
    'run', '--project', $project, '-c', 'Release', '--',
    '--confirm-compatible-dac',
    '--device-id', $DeviceId,
    '--dac-model', $DacModel,
    '--driver-version', $DriverVersion,
    '--connection', $Connection,
    '--buffer', $BufferMilliseconds,
    '--dsd64-indication', $Dsd64Indication,
    '--dsd128-indication', $Dsd128Indication,
    '--output', ([System.IO.Path]::GetFullPath($Output))
)
if (-not [string]::IsNullOrWhiteSpace($OperatorNotes)) {
    $arguments += @('--operator-notes', $OperatorNotes)
}
& dotnet @arguments
exit $LASTEXITCODE
