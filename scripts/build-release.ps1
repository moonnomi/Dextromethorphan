param(
    [ValidateSet('win-x64','win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "artifacts\publish\$Runtime"
$archive = Join-Path $root "artifacts\Dextromethorphan-$Runtime.zip"

dotnet restore (Join-Path $root 'Dextromethorphan.slnx')
dotnet test (Join-Path $root 'Dextromethorphan.slnx') -c Release --no-restore
dotnet publish (Join-Path $root 'src\Dextromethorphan.App\Dextromethorphan.App.csproj') -c Release -r $Runtime --self-contained:$($SelfContained.IsPresent.ToString().ToLowerInvariant()) -p:PublishReadyToRun=true -o $publish

if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Release archive: $archive"

if ($Installer) {
    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if (-not $iscc) { throw 'Inno Setup 6 is required for -Installer (ISCC.exe was not found).' }
    & $iscc.Source "/DRuntime=$Runtime" (Join-Path $root 'installer\Dextromethorphan.iss')
}
