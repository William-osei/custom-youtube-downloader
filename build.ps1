[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'Custom YouTube Downloader.exe')
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The Windows .NET Framework C# compiler is required.'
}

$output = [IO.Path]::GetFullPath($OutputPath)
$mainSource = Join-Path $PSScriptRoot 'Program.cs'
$workerSource = Join-Path $PSScriptRoot 'DownloadWorker.cs'
$captionSource = Join-Path $PSScriptRoot 'CaptionWorker.cs'
$librarySource = Join-Path $PSScriptRoot 'VideoLibrary.cs'
$playlistSource = Join-Path $PSScriptRoot 'PlaylistWorker.cs'
$catalogSource = Join-Path $PSScriptRoot 'MediaCatalog.cs'
$discoverySource = Join-Path $PSScriptRoot 'DiscoveryPanel.cs'
$preferencesSource = Join-Path $PSScriptRoot 'AppPreferences.cs'
$watchlistSource = Join-Path $PSScriptRoot 'Watchlist.cs'
$icon = Join-Path $PSScriptRoot 'assets\app-icon.ico'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$output" "/win32icon:$icon" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll $mainSource $workerSource $captionSource $librarySource $playlistSource $catalogSource $discoverySource $preferencesSource $watchlistSource
if ($LASTEXITCODE -ne 0) { throw 'The application could not be built.' }
Get-Item -LiteralPath $output
