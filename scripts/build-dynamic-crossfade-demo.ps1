param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destination = Join-Path $root 'src\Dextromethorphan.App\bin\dynamic-crossfade-demo'
$buildArtifacts = Join-Path $root 'artifacts\build\dynamic-crossfade-demo'
$running = Get-Process -Name Dextromethorphan -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw 'Close the dynamic crossfade demo before rebuilding it.' }
dotnet test (Join-Path $root 'Dextromethorphan.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
dotnet publish (Join-Path $root 'src\Dextromethorphan.App\Dextromethorphan.App.csproj') -c Release -r win-x64 --self-contained false --artifacts-path $buildArtifacts -o $destination -p:VersionSuffix=dynamic-crossfade-demo
if ($LASTEXITCODE -ne 0) { throw 'Demo publish failed.' }
Write-Host "Demo executable: $destination\Dextromethorphan.exe"
Write-Host 'Stable bin/latest was not changed.'
