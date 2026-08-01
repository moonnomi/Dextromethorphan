param(
    [TimeSpan]$Duration = [TimeSpan]::FromHours(8),
    [int]$TrackSeconds = 8,
    [double]$CrossfadeSeconds = 0.5,
    [int]$BufferMilliseconds = 100,
    [TimeSpan]$SampleInterval = [TimeSpan]::FromSeconds(30),
    [string]$DeviceId = 'default',
    [string]$OutputPath = '',
    [switch]$Detached,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$TaskName = 'Dextromethorphan-AudioSoak'
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputPath = Join-Path $root "artifacts/audio-soak/audio-soak-$stamp.json"
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$runnerArguments = @(
    '--duration', $Duration.ToString('c'),
    '--track-seconds', $TrackSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--crossfade', $CrossfadeSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--buffer', $BufferMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--sample-interval', $SampleInterval.ToString('c'),
    '--device-id', $DeviceId,
    '--output', $OutputPath
)
$project = Join-Path $root 'tools/Dextromethorphan.AudioSoak/Dextromethorphan.AudioSoak.csproj'

if (-not $Detached) {
    $arguments = @('run', '--project', $project, '--configuration', 'Release', '--') + $runnerArguments
    & dotnet @arguments
    exit $LASTEXITCODE
}

if ($env:OS -ne 'Windows_NT') {
    throw 'Detached audio soak execution requires Windows Task Scheduler.'
}

$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existingTask -and $existingTask.State -eq 'Running') {
    throw "Scheduled task '$TaskName' is already running."
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runnerDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $outputDirectory "runner-$stamp"))
$workspacePrefix = $root.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $runnerDirectory.StartsWith(
    $workspacePrefix,
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish a runner outside the workspace: $runnerDirectory"
}

dotnet publish $project -c Release --self-contained false -o $runnerDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Audio soak runner publish failed with exit code $LASTEXITCODE."
}
$runner = Join-Path $runnerDirectory 'Dextromethorphan.AudioSoak.exe'
if (-not (Test-Path -LiteralPath $runner)) {
    throw "Published audio soak runner was not found: $runner"
}

function ConvertTo-SingleQuotedLiteral([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

$wrapperPath = "$OutputPath.launch.ps1"
$logPath = "$OutputPath.log"
$exitPath = "$OutputPath.exitcode"
$argumentLiterals = ($runnerArguments | ForEach-Object {
    '    ' + (ConvertTo-SingleQuotedLiteral ([string]$_))
}) -join ",`r`n"
$runnerLiteral = ConvertTo-SingleQuotedLiteral $runner
$logLiteral = ConvertTo-SingleQuotedLiteral $logPath
$exitLiteral = ConvertTo-SingleQuotedLiteral $exitPath
$wrapper = @"
`$ErrorActionPreference = 'Stop'
`$runnerArguments = @(
$argumentLiterals
)
`$exitCode = 1
try {
    & $runnerLiteral @runnerArguments *>> $logLiteral
    `$exitCode = `$LASTEXITCODE
}
catch {
    `$_ | Out-String | Add-Content -LiteralPath $logLiteral
}
finally {
    [System.IO.File]::WriteAllText($exitLiteral, `$exitCode.ToString())
}
exit `$exitCode
"@
[System.IO.File]::WriteAllText(
    $wrapperPath,
    $wrapper,
    [System.Text.UTF8Encoding]::new($false))

$powershell = (Get-Process -Id $PID).Path
$action = New-ScheduledTaskAction `
    -Execute $powershell `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$wrapperPath`"" `
    -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1)
$principal = New-ScheduledTaskPrincipal `
    -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) `
    -LogonType Interactive `
    -RunLevel Limited
$executionLimit = [TimeSpan]::FromHours(
    [Math]::Max(12, [Math]::Ceiling($Duration.TotalHours + 4)))
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -WakeToRun `
    -ExecutionTimeLimit $executionLimit
Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description 'Dextromethorphan generated-audio Milestone 3 soak qualification' `
    -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName

Write-Host "Detached audio soak task: $TaskName"
Write-Host "Checkpoint/final report: $OutputPath"
Write-Host "Runner log: $logPath"
Write-Host "Use scripts/Get-AudioSoakStatus.ps1 -OutputPath '$OutputPath' -TaskName '$TaskName'"
