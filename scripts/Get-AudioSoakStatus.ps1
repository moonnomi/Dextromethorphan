param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$TaskName = 'Dextromethorphan-AudioSoak',
    [switch]$CleanupCompletedTask
)

$ErrorActionPreference = 'Stop'
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
$taskInfo = if ($task) {
    Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
}

if ($task) {
    Write-Host "Task: $TaskName ($($task.State))"
    if ($taskInfo) {
        Write-Host "Last result: $($taskInfo.LastTaskResult); next run: $($taskInfo.NextRunTime)"
    }
}
else {
    Write-Host "Task: $TaskName (not registered)"
}

if (-not (Test-Path -LiteralPath $OutputPath)) {
    Write-Host "Report has not been checkpointed yet: $OutputPath"
    exit $(if ($task -and $task.State -eq 'Running') { 3 } else { 4 })
}

$report = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
$updated = (Get-Item -LiteralPath $OutputPath).LastWriteTime
$observed = [TimeSpan]::FromSeconds([double]$report.ObservedPlayingSeconds)
$requested = [TimeSpan]::FromSeconds([double]$report.RequestedDurationSeconds)
$peakGrowthMiB = [double]$report.Memory.PeakGrowthBytes / 1MB

Write-Host "Report: $OutputPath"
Write-Host "Updated: $updated"
Write-Host "State: $($report.ReportState); process: $($report.RunnerProcessId)"
Write-Host "Observed/requested: $observed / $requested"
Write-Host "Transitions: $($report.Transitions); deadline misses: $($report.FinalDiagnostics.Underruns); recoveries: $($report.FinalDiagnostics.RecoveryAttempts)"
Write-Host ("Peak memory growth: {0:N1} MiB; endpoint volume unchanged: {1}" -f $peakGrowthMiB, $report.EndpointVolume.Unchanged)
Write-Host "Run passed: $($report.RunPassed); eight-hour qualified: $($report.Qualified)"

if ($CleanupCompletedTask
    -and $task
    -and $task.State -ne 'Running'
    -and $report.ReportState -ne 'Running') {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Removed completed scheduled task: $TaskName"
}

if ($report.Qualified) { exit 0 }
if ($report.ReportState -eq 'Running') { exit 3 }
exit 2
