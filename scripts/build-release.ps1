param(
    [ValidateSet('win-x64','win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$publish = [System.IO.Path]::GetFullPath(
    (Join-Path $root "artifacts\publish\$Runtime"))
$archive = [System.IO.Path]::GetFullPath(
    (Join-Path $root "artifacts\Dextromethorphan-$Runtime.zip"))
$latest = [System.IO.Path]::GetFullPath(
    (Join-Path $root 'src\Dextromethorphan.App\bin\latest'))

function Assert-WorkspaceChild([string]$Path) {
    $prefix = $root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith(
        $prefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the workspace: $Path"
    }
}

Assert-WorkspaceChild $publish
Assert-WorkspaceChild $archive
Assert-WorkspaceChild $latest

dotnet restore (Join-Path $root 'Dextromethorphan.slnx')
dotnet test (Join-Path $root 'Dextromethorphan.slnx') -c Release --no-restore
if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}
dotnet publish (Join-Path $root 'src\Dextromethorphan.App\Dextromethorphan.App.csproj') -c Release -r $Runtime --self-contained:$($SelfContained.IsPresent.ToString().ToLowerInvariant()) -p:PublishReadyToRun=true -o $publish

if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Release archive: $archive"

if (Test-Path -LiteralPath $latest) {
    Remove-Item -LiteralPath $latest -Recurse -Force
}
New-Item -ItemType Directory -Path $latest -Force | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $latest -Recurse -Force
Write-Host "Latest runnable build: $latest"

if ($Installer) {
    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if (-not $iscc) { throw 'Inno Setup 6 is required for -Installer (ISCC.exe was not found).' }
    & $iscc.Source "/DRuntime=$Runtime" (Join-Path $root 'installer\Dextromethorphan.iss')
}
