[CmdletBinding()]
param([switch]$Refresh)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$engineFolder = Join-Path $PSScriptRoot 'tools\whisper'
New-Item -ItemType Directory -Path $engineFolder -Force | Out-Null
$archiveUri = 'https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip'
$archiveHash = '6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb'
$modelUri = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin?download=true'
$modelHash = '60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe'
$modelPath = Join-Path $engineFolder 'ggml-base.bin'
$cliPath = Join-Path $engineFolder 'whisper-cli.exe'
$binaryNames = @(
    'whisper-cli.exe', 'whisper.dll', 'ggml.dll', 'ggml-base.dll',
    'ggml-cpu-x64.dll', 'ggml-cpu-sse42.dll', 'ggml-cpu-sandybridge.dll',
    'ggml-cpu-haswell.dll', 'ggml-cpu-skylakex.dll', 'ggml-cpu-cascadelake.dll',
    'ggml-cpu-cannonlake.dll', 'ggml-cpu-icelake.dll', 'ggml-cpu-alderlake.dll'
)

function Test-Checksum([string]$Path, [string]$Hash) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Hash)
}

function Get-VerifiedDownload([string]$Uri, [string]$Path, [string]$Hash) {
    if (Test-Checksum $Path $Hash) {
        return
    }
    & curl.exe --fail --silent --show-error --location --proto '=https' --retry 2 --connect-timeout 15 --max-time 240 --output $Path $Uri
    if ($LASTEXITCODE -ne 0 -or !(Test-Checksum $Path $Hash)) {
        throw "Could not download and verify $Uri."
    }
}

$lockPath = Join-Path $engineFolder 'setup.lock'
$setupLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $missingBinary = $binaryNames | Where-Object { !(Test-Path -LiteralPath (Join-Path $engineFolder $_) -PathType Leaf) }
    if ($Refresh -or $missingBinary) {
        $archivePath = Join-Path $engineFolder 'whisper-b5454.zip.part'
        Write-Host 'Installing the whisper.cpp CPU translation engine...'
        Get-VerifiedDownload $archiveUri $archivePath $archiveHash
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($name in $binaryNames) {
                $entry = $archive.GetEntry("Release/$name")
                if ($null -eq $entry) {
                    throw "The pinned whisper.cpp archive is missing $name."
                }
                $stagedPath = Join-Path $engineFolder "$name.part"
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $stagedPath, $true)
            }
        } finally {
            $archive.Dispose()
        }
        foreach ($name in $binaryNames) {
            Move-Item -LiteralPath (Join-Path $engineFolder "$name.part") -Destination (Join-Path $engineFolder $name) -Force
        }
        Remove-Item -LiteralPath $archivePath
    }

    if ($Refresh -or !(Test-Checksum $modelPath $modelHash)) {
        $stagedModel = Join-Path $engineFolder 'ggml-base.bin.part'
        Write-Host 'Installing the multilingual audio model (148 MB)...'
        Get-VerifiedDownload $modelUri $stagedModel $modelHash
        Move-Item -LiteralPath $stagedModel -Destination $modelPath -Force
    }

    Get-VerifiedDownload 'https://raw.githubusercontent.com/ggml-org/whisper.cpp/d1be6fd/LICENSE' (Join-Path $engineFolder 'whisper.cpp-LICENSE.txt') '94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d'
    Get-VerifiedDownload 'https://raw.githubusercontent.com/openai/whisper/v20250625/LICENSE' (Join-Path $engineFolder 'whisper-model-LICENSE.txt') 'b5d65a59060e68c4ff940e1eddfa6f94b2d68fdf58ed7f4dd57721c997e35e9d'

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $cliPath
    $start.Arguments = '--help'
    $start.WorkingDirectory = $engineFolder
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $helpText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0 -or !$helpText.Contains('--translate')) {
            throw 'The translation engine could not start. Run this setup again with -Refresh.'
        }
    } finally {
        $process.Dispose()
    }

    Write-Host 'Offline subtitle translation is ready.'
} finally {
    $setupLock.Dispose()
}
