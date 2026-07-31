param(
    [TimeSpan]$Duration = [TimeSpan]::FromHours(8),
    [int]$TrackSeconds = 8,
    [double]$CrossfadeSeconds = 0.5,
    [int]$BufferMilliseconds = 100,
    [TimeSpan]$SampleInterval = [TimeSpan]::FromSeconds(30),
    [string]$DeviceId = "default",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = Join-Path $root "artifacts/audio-soak/audio-soak-$stamp.json"
}

$arguments = @(
    "run",
    "--project", (Join-Path $root "tools/Dextromethorphan.AudioSoak/Dextromethorphan.AudioSoak.csproj"),
    "--configuration", "Release",
    "--",
    "--duration", $Duration.ToString("c"),
    "--track-seconds", $TrackSeconds,
    "--crossfade", $CrossfadeSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--buffer", $BufferMilliseconds,
    "--sample-interval", $SampleInterval.ToString("c"),
    "--device-id", $DeviceId,
    "--output", $OutputPath
)

& dotnet @arguments
exit $LASTEXITCODE
