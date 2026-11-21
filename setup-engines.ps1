[CmdletBinding()]
param(
    [switch]$Refresh
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$engineFolder = Join-Path $PSScriptRoot 'tools'
New-Item -ItemType Directory -Path $engineFolder -Force | Out-Null

function Get-VerifiedFile {
    param(
        [string]$Uri,
        [string]$Destination,
        [string]$Sha256
    )
    & curl.exe --fail --silent --show-error --location --proto '=https' --retry 2 --connect-timeout 15 --max-time 240 --output $Destination $Uri
    if ($LASTEXITCODE -ne 0) {
        throw "Download failed: $Uri"
    }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Sha256) {
        throw "SHA-256 mismatch: $Uri"
    }
}

$ytDlp = Join-Path $engineFolder 'yt-dlp.exe'
$ffmpeg = Join-Path $engineFolder 'ffmpeg.exe'
$ffprobe = Join-Path $engineFolder 'ffprobe.exe'
$staging = [Guid]::NewGuid().ToString('N')
$ytDlpTemp = Join-Path $engineFolder "$staging-yt-dlp.tmp"
$ffmpegZip = Join-Path $engineFolder "$staging-ffmpeg.zip"
$ffmpegTemp = Join-Path $engineFolder "$staging-ffmpeg.tmp"
$ffprobeTemp = Join-Path $engineFolder "$staging-ffprobe.tmp"
try {
    if ($Refresh -or !(Test-Path -LiteralPath $ytDlp -PathType Leaf)) {
        Write-Host 'Downloading yt-dlp...'
        Get-VerifiedFile -Uri 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe' -Destination $ytDlpTemp -Sha256 'f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d'
        Move-Item -LiteralPath $ytDlpTemp -Destination $ytDlp -Force
    }
    if ($Refresh -or !(Test-Path -LiteralPath $ffmpeg -PathType Leaf) -or !(Test-Path -LiteralPath $ffprobe -PathType Leaf)) {
        Write-Host 'Downloading FFmpeg and FFprobe...'
        Get-VerifiedFile -Uri 'https://github.com/GyanD/codexffmpeg/releases/download/9.1/ffmpeg-9.1-essentials_build.zip' -Destination $ffmpegZip -Sha256 'b9bbbb3acece000cbeec994f856649354e52c773807841e78908374b24d658fd'

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($ffmpegZip)
        try {
            $binaries = @{ 'ffmpeg.exe' = $ffmpegTemp; 'ffprobe.exe' = $ffprobeTemp }
            foreach ($name in $binaries.Keys) {
                $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith("/bin/$name", [StringComparison]::OrdinalIgnoreCase) })
                if ($entries.Count -ne 1) {
                    throw "Expected exactly one $name in the FFmpeg archive."
                }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $binaries[$name], $false)
            }
        } finally {
            $archive.Dispose()
        }
        Move-Item -LiteralPath $ffmpegTemp -Destination $ffmpeg -Force
        Move-Item -LiteralPath $ffprobeTemp -Destination $ffprobe -Force
    }
} finally {
    foreach ($tempFile in @($ytDlpTemp, $ffmpegZip, $ffmpegTemp, $ffprobeTemp)) {
        if (Test-Path -LiteralPath $tempFile -PathType Leaf) {
            Remove-Item -LiteralPath $tempFile
        }
    }
}

& $ytDlp --ignore-config --version
if ($LASTEXITCODE -ne 0) { throw 'yt-dlp could not start.' }
& $ffmpeg -version | Select-Object -First 1
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg could not start.' }
& $ffprobe -version | Select-Object -First 1
if ($LASTEXITCODE -ne 0) { throw 'FFprobe could not start.' }

$node = Get-Command node.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($node) {
    $nodeVersion = & $node.Source --version
    if ($LASTEXITCODE -eq 0 -and $nodeVersion -match '^v(\d+)\.' -and [int]$Matches[1] -ge 22) {
        Write-Host "Node.js $nodeVersion is ready."
        return
    }
}
Write-Warning 'Install Node.js 22 or later for YouTube support. https://nodejs.org/en/download/'
