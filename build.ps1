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
$batchSource = Join-Path $PSScriptRoot 'DownloadBatchRunner.cs'
$dubSource = Join-Path $PSScriptRoot 'EnglishDubWorker.cs'
$speech = Join-Path (Split-Path $compiler) 'WPF\System.Speech.dll'
$captionSource = Join-Path $PSScriptRoot 'CaptionWorker.cs'
$librarySource = Join-Path $PSScriptRoot 'VideoLibrary.cs'
$playlistSource = Join-Path $PSScriptRoot 'PlaylistWorker.cs'
$catalogSource = Join-Path $PSScriptRoot 'MediaCatalog.cs'
$discoverySource = Join-Path $PSScriptRoot 'DiscoveryPanel.cs'
$preferencesSource = Join-Path $PSScriptRoot 'AppPreferences.cs'
$watchlistSource = Join-Path $PSScriptRoot 'Watchlist.cs'
$historySource = Join-Path $PSScriptRoot 'DownloadHistory.cs'
$historyPanelSource = Join-Path $PSScriptRoot 'HistoryPanel.cs'
$identitySource = Join-Path $PSScriptRoot 'AppIdentity.cs'
$studioSources = @('StudioTheme.cs', 'StudioShell.cs', 'DownloadWorkspace.cs', 'CollectionView.cs', 'EnglishTranscript.cs', 'EnglishTranscriptEditor.cs', 'Portable\PlatformTools.cs') |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
$film = Join-Path $PSScriptRoot 'assets\film-reel.jpg'
$icon = Join-Path $PSScriptRoot 'assets\app-icon.ico'
$manifest = Join-Path $PSScriptRoot 'app.manifest'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$output" "/win32icon:$icon" "/win32manifest:$manifest" "/resource:$icon,CustomYouTubeDownloader.AppIcon.ico" "/resource:$film,CustomYouTubeDownloader.Film.jpg" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/r:$speech" $mainSource $workerSource $batchSource $dubSource $captionSource $librarySource $playlistSource $catalogSource $discoverySource $preferencesSource $watchlistSource $historySource $historyPanelSource $identitySource @studioSources
if ($LASTEXITCODE -ne 0) { throw 'The application could not be built.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'App.config') -Destination "$output.config" -Force
Get-Item -LiteralPath $output
