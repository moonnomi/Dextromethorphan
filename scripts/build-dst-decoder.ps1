param()

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$manifest = Join-Path $root 'native\Dextromethorphan.DstDecoder\Cargo.toml'
$source = Join-Path $root 'native\Dextromethorphan.DstDecoder\target\release\dextromethorphan_dst.dll'
$destination = Join-Path $root 'src\Dextromethorphan.Infrastructure\runtimes\win-x64\native\dextromethorphan_dst.dll'

$hostLine = (& rustc -vV | Select-String '^host:').Line
if ($LASTEXITCODE -ne 0 -or $hostLine -ne 'host: x86_64-pc-windows-msvc') {
    throw "The DST shim requires the x86_64-pc-windows-msvc Rust toolchain; found '$hostLine'."
}

& cargo build --manifest-path $manifest --release --locked
if ($LASTEXITCODE -ne 0) {
    throw "Native DST decoder build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $source)) {
    throw "Native DST decoder output was not created: $source"
}

New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $destination -Force
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
Write-Host "Native DST decoder: $destination"
Write-Host "SHA-256: $hash"
