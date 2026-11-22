[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$run = Join-Path $tempRoot ('cyd-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run
try {
    $application = Join-Path $run 'Custom YouTube Downloader.exe'
    $null = & (Join-Path $PSScriptRoot 'build.ps1') -OutputPath $application
    $captions = Join-Path $run 'CaptionSelectionTests.exe'
    & $compiler /nologo /target:exe /codepage:65001 "/out:$captions" /r:System.Web.Extensions.dll `
        (Join-Path $PSScriptRoot 'tests\CaptionSelectionTests.cs') `
        (Join-Path $PSScriptRoot 'CaptionWorker.cs') (Join-Path $PSScriptRoot 'DownloadWorker.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Caption test compilation failed.' }
    & $captions
    if ($LASTEXITCODE -ne 0) { throw 'Caption selection tests failed.' }

    $layout = Join-Path $run 'WindowLayoutTests.exe'
    $manifest = Join-Path $PSScriptRoot 'app.manifest'
    & $compiler /nologo /target:exe /codepage:65001 "/out:$layout" "/win32manifest:$manifest" `
        /r:System.Drawing.dll /r:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'tests\WindowLayoutTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Window test compilation failed.' }
    Copy-Item -LiteralPath "$application.config" -Destination "$layout.config"
    & $layout $application (Join-Path $run 'profile')
    if ($LASTEXITCODE -ne 0) { throw 'Window layout tests failed.' }
}
finally {
    $directory = Get-Item -LiteralPath $run
    if ($directory.Parent.FullName -eq $tempRoot -and $directory.Name -match '^cyd-tests-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
}
