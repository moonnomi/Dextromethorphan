param(
    [string]$Output = (
        Join-Path $PSScriptRoot '..\tests\Fixtures\AudioFormats'),
    [string]$ProbeOutput = (
        Join-Path $PSScriptRoot '..\src\Dextromethorphan.Infrastructure\Audio\Probes')
)

$ErrorActionPreference = 'Stop'
$ffmpeg = Get-Command ffmpeg -ErrorAction Stop
$Output = [IO.Path]::GetFullPath($Output)
$ProbeOutput = [IO.Path]::GetFullPath($ProbeOutput)
New-Item -ItemType Directory -Force -Path $Output | Out-Null
New-Item -ItemType Directory -Force -Path $ProbeOutput | Out-Null

function Invoke-Ffmpeg([string[]]$Arguments) {
    & $ffmpeg.Source -hide_banner -loglevel error -y @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed with exit code ${LASTEXITCODE}: $Arguments"
    }
}

$reference = Join-Path $Output 'reference.wav'
Invoke-Ffmpeg -Arguments @(
    '-f', 'lavfi',
    '-i', 'sine=frequency=997:sample_rate=48000:duration=2',
    '-ac', '2',
    '-c:a', 'pcm_s16le',
    $reference
)

$encodings = @(
    @{ File = 'reference.flac'; Args = @('-c:a', 'flac') },
    @{ File = 'reference.mp3'; Args = @('-c:a', 'libmp3lame', '-q:a', '4') },
    @{ File = 'reference.aiff'; Args = @('-c:a', 'pcm_s16be') },
    @{ File = 'reference.ogg'; Args = @('-c:a', 'libvorbis', '-q:a', '5') },
    @{ File = 'reference.opus'; Args = @('-c:a', 'libopus', '-b:a', '128k') },
    @{ File = 'aac.m4a'; Args = @('-c:a', 'aac', '-b:a', '160k') },
    @{ File = 'aac.aac'; Args = @('-c:a', 'aac', '-b:a', '160k', '-f', 'adts') },
    @{ File = 'alac.m4a'; Args = @('-c:a', 'alac') },
    @{ File = 'reference.wma'; Args = @('-c:a', 'wmav2', '-b:a', '160k') }
)

foreach ($encoding in $encodings) {
    $arguments = @(
        '-i', $reference,
        '-map_metadata', '-1',
        '-metadata', 'title=Generated 997 Hz reference',
        '-metadata', 'artist=Dextromethorphan test corpus'
    )
    $arguments += [string[]]$encoding.Args
    $arguments += (Join-Path $Output $encoding.File)
    Invoke-Ffmpeg -Arguments $arguments
}

$unicodeName =
    'unicode-' + [char]0x97F3 + [char]0x697D + '-alac.m4a'
Get-ChildItem -LiteralPath $Output -File -Filter 'unicode-*-alac.m4a' |
    Where-Object Name -ne $unicodeName |
    ForEach-Object {
        [IO.File]::Delete($_.FullName)
    }
Copy-Item -LiteralPath (
    Join-Path $Output 'alac.m4a'
) -Destination (
    Join-Path $Output $unicodeName
) -Force

$cover = Join-Path $Output 'generated-cover.bmp'
$coverSize = 64
$rowBytes = $coverSize * 3
$pixelBytes = $rowBytes * $coverSize
$stream = [IO.File]::Create($cover)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([Text.Encoding]::ASCII.GetBytes('BM'))
    $writer.Write([int](54 + $pixelBytes))
    $writer.Write([int]0)
    $writer.Write([int]54)
    $writer.Write([int]40)
    $writer.Write([int]$coverSize)
    $writer.Write([int]$coverSize)
    $writer.Write([int16]1)
    $writer.Write([int16]24)
    $writer.Write([int]0)
    $writer.Write([int]$pixelBytes)
    $writer.Write([int]2835)
    $writer.Write([int]2835)
    $writer.Write([int]0)
    $writer.Write([int]0)
    for ($y = 0; $y -lt $coverSize; $y++) {
        for ($x = 0; $x -lt $coverSize; $x++) {
            $writer.Write([byte](40 + (($x * 3) % 180)))
            $writer.Write([byte](30 + (($y * 3) % 180)))
            $writer.Write([byte](100 + ((($x + $y) * 2) % 150)))
        }
    }
}
finally {
    $writer.Dispose()
}

$largeComment = 'Dextromethorphan metadata stress fixture. ' + ('x' * 12000)
Invoke-Ffmpeg -Arguments @(
    '-i', (Join-Path $Output 'reference.flac'),
    '-i', $cover,
    '-map', '0:a:0',
    '-map', '1:v:0',
    '-c:a', 'copy',
    '-c:v', 'copy',
    '-disposition:v:0', 'attached_pic',
    '-metadata', ('title=' + [char]0x97F3 + [char]0x697D + ' metadata fixture'),
    '-metadata', 'artist=Alpha; Beta / Gamma',
    '-metadata', 'album=Unusual metadata blocks',
    '-metadata', ('comment=' + $largeComment),
    (Join-Path $Output 'metadata-heavy.flac')
)

function Write-BigEndian(
    [IO.BinaryWriter]$Writer,
    [byte[]]$Bytes
) {
    [Array]::Reverse($Bytes)
    $Writer.Write($Bytes)
}

$dsf = Join-Path $Output 'reference.dsf'
$stream = [IO.File]::Create($dsf)
$writer = [IO.BinaryWriter]::new(
    $stream,
    [Text.Encoding]::ASCII,
    $false)
try {
    $writer.Write([Text.Encoding]::ASCII.GetBytes('DSD '))
    $writer.Write([long]28)
    $writer.Write([long]100)
    $writer.Write([long]0)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('fmt '))
    $writer.Write([long]52)
    $writer.Write([int]1)
    $writer.Write([int]0)
    $writer.Write([int]2)
    $writer.Write([int]2)
    $writer.Write([int]2822400)
    $writer.Write([int]1)
    $writer.Write([long]32)
    $writer.Write([int]4)
    $writer.Write([int]0)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
    $writer.Write([long]20)
    $writer.Write([byte[]](1,2,3,4,5,6,7,8))
}
finally {
    $writer.Dispose()
}

$dff = Join-Path $Output 'reference.dff'
$properties = [IO.MemoryStream]::new()
$propertyWriter = [IO.BinaryWriter]::new(
    $properties,
    [Text.Encoding]::ASCII,
    $true)
try {
    $propertyWriter.Write([Text.Encoding]::ASCII.GetBytes('SND '))
    $propertyWriter.Write([Text.Encoding]::ASCII.GetBytes('FS  '))
    Write-BigEndian $propertyWriter ([BitConverter]::GetBytes([uint64]4))
    Write-BigEndian $propertyWriter ([BitConverter]::GetBytes([uint32]2822400))
    $propertyWriter.Write([Text.Encoding]::ASCII.GetBytes('CHNL'))
    Write-BigEndian $propertyWriter ([BitConverter]::GetBytes([uint64]10))
    Write-BigEndian $propertyWriter ([BitConverter]::GetBytes([uint16]2))
    $propertyWriter.Write([Text.Encoding]::ASCII.GetBytes('SLFTSRGT'))
    $propertyWriter.Write([Text.Encoding]::ASCII.GetBytes('CMPR'))
    Write-BigEndian $propertyWriter ([BitConverter]::GetBytes([uint64]5))
    $propertyWriter.Write([Text.Encoding]::ASCII.GetBytes('DSD '))
    $propertyWriter.Write([byte]0)
    $propertyWriter.Write([byte]0)
}
finally {
    $propertyWriter.Dispose()
}

$stream = [IO.File]::Create($dff)
$writer = [IO.BinaryWriter]::new(
    $stream,
    [Text.Encoding]::ASCII,
    $false)
try {
    $writer.Write([Text.Encoding]::ASCII.GetBytes('FRM8'))
    Write-BigEndian $writer (
        [BitConverter]::GetBytes(
            [uint64](4 + 12 + $properties.Length + 12 + 8)))
    $writer.Write([Text.Encoding]::ASCII.GetBytes('DSD '))
    $writer.Write([Text.Encoding]::ASCII.GetBytes('PROP'))
    Write-BigEndian $writer (
        [BitConverter]::GetBytes([uint64]$properties.Length))
    $properties.Position = 0
    $properties.CopyTo($stream)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('DSD '))
    Write-BigEndian $writer ([BitConverter]::GetBytes([uint64]8))
    $writer.Write([byte[]](1,2,3,4,5,6,7,8))
}
finally {
    $writer.Dispose()
    $properties.Dispose()
}

[IO.File]::WriteAllBytes(
    (Join-Path $Output 'malformed-header.flac'),
    [Text.Encoding]::UTF8.GetBytes('not a FLAC stream'))
$mp3 = [IO.File]::ReadAllBytes(
    (Join-Path $Output 'reference.mp3'))
[IO.File]::WriteAllBytes(
    (Join-Path $Output 'truncated.mp3'),
    $mp3[0..([Math]::Min(95, $mp3.Length - 1))])

$files = Get-ChildItem -LiteralPath $Output -File |
    Where-Object Name -ne 'manifest.json' |
    Sort-Object Name |
    ForEach-Object {
        [ordered]@{
            file = $_.Name
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
[ordered]@{
    schemaVersion = 1
    generator = 'scripts/New-AudioFormatCorpus.ps1'
    source = 'synthetic 997 Hz sine wave; no copyrighted audio'
    generatedWith = (& $ffmpeg.Source -version | Select-Object -First 1)
    files = @($files)
} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (
        Join-Path $Output 'manifest.json') -Encoding utf8

@(
    'reference.mp3',
    'aac.aac',
    'aac.m4a',
    'alac.m4a',
    'reference.wma'
) | ForEach-Object {
    Copy-Item -LiteralPath (Join-Path $Output $_) `
        -Destination (Join-Path $ProbeOutput $_) -Force
}

Write-Host "Generated $($files.Count) legal audio fixtures in $Output"
Write-Host "Updated clean-install decoder probes in $ProbeOutput"
