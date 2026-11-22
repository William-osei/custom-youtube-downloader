[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$exePath = Join-Path $outDir 'Custom YouTube Downloader.exe'
$app = & (Join-Path $PSScriptRoot 'build.ps1') -OutputPath $exePath
$version = $app.VersionInfo.ProductVersion
$archive = Join-Path $outDir "Custom-YouTube-Downloader-$version-Windows.zip"
$staging = Join-Path $outDir ("release-" + [Guid]::NewGuid().ToString('N') + '.zip')

try {
    $files = @(
        $exePath
        "$exePath.config"
        (Join-Path $PSScriptRoot 'README.md')
        (Join-Path $PSScriptRoot 'setup-engines.ps1')
        (Join-Path $PSScriptRoot 'setup-translation.ps1')
    )
    Compress-Archive -LiteralPath $files -DestinationPath $staging
    Move-Item -LiteralPath $staging -Destination $archive -Force
} finally {
    if (Test-Path -LiteralPath $staging -PathType Leaf) { Remove-Item -LiteralPath $staging }
}
Get-Item -LiteralPath $archive
