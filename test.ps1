[CmdletBinding()]
param([switch]$Media)

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
        /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
        (Join-Path $PSScriptRoot 'tests\WindowLayoutTests.cs') (Join-Path $PSScriptRoot 'tests\BatchDownloadTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Window test compilation failed.' }
    Copy-Item -LiteralPath "$application.config" -Destination "$layout.config"
    if ($Media) {
        $fixtureTools = New-Item -ItemType Directory -Path (Join-Path $run 'tools')
        foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
            $engine = Join-Path $PSScriptRoot "tools\$name"
            if (!(Test-Path -LiteralPath $engine -PathType Leaf)) { throw 'Run setup-engines.ps1 before testing real media.' }
            $target = Join-Path $fixtureTools.FullName $name
            try { New-Item -ItemType HardLink -Path $target -Target $engine -ErrorAction Stop | Out-Null }
            catch { Copy-Item -LiteralPath $engine -Destination $target }
        }
        & (Join-Path $fixtureTools.FullName 'ffmpeg.exe') -v error -nostdin -f lavfi -i 'color=s=160x90:r=10:d=3' `
            -f lavfi -i 'sine=frequency=440:duration=3' -c:v mpeg4 -c:a aac (Join-Path $fixtureTools.FullName 'sample.mkv')
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the media test input.' }
    }
    & $layout $application (Join-Path $run 'profile')
    if ($LASTEXITCODE -ne 0) { throw 'Window layout tests failed.' }

    if ($Media) {
        $audioTest = Join-Path $run 'EnglishAudioTests.exe'
        & $compiler /nologo /target:exe /codepage:65001 "/out:$audioTest" /r:System.Web.Extensions.dll `
            (Join-Path $PSScriptRoot 'tests\EnglishAudioTests.cs')
        if ($LASTEXITCODE -ne 0) { throw 'English audio test compilation failed.' }
        & $audioTest $application (Join-Path $PSScriptRoot 'tools') (Join-Path $run 'media')
        if ($LASTEXITCODE -ne 0) { throw 'English audio verification failed.' }
    }
}
finally {
    $directory = Get-Item -LiteralPath $run
    if ($directory.Parent.FullName -eq $tempRoot -and $directory.Name -match '^cyd-tests-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
}
