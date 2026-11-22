# Custom YouTube Downloader

A Windows desktop app for finding movies and series, saving YouTube videos you have permission to download, and searching your offline library.

## Install or update on Windows

Download the Windows ZIP from [GitHub Releases](https://github.com/William-osei/custom-youtube-downloader/releases/latest) and extract it to a permanent folder. On a first install, use 64-bit Windows with .NET Framework 4.8 and Node.js 22 or later. Run `.\setup-engines.ps1` from PowerShell in the extracted folder. For offline speech translation, also run `.\setup-translation.ps1`.

To update an existing copy, close the app and extract the new ZIP into the same folder, replacing the matching files. Keep the executable, its `.exe.config` file, and the `tools` folder together. The title bar shows the installed version. Choose **Releases** in the app to find updates.

## Use the app

1. Open **Custom YouTube Downloader** from your desktop shortcut or run the executable in this project folder.
2. Paste a YouTube video, Shorts, or playlist link. For a batch, paste one link per line. Choose a download folder or use **Downloads\Offline Videos**.
3. Choose the video quality, file format, and subtitle option. Enter a category and series to choose where to group the batch, or leave them blank for automatic folders.
4. Click **Start downloads**. Open **Library** to search, play a saved video, or find its files. **Details** shows the result of each job.

Maximize the window to use the desktop work area, or press **F11** for full screen. Press **Esc** or **F11** to return to your previous window size. On short displays, scroll inside the tab to reach the remaining controls.

## Full playlists

To download a full playlist, copy the link from the playlist page or its **Share** button. A link to a video inside a playlist still downloads the selected video. You can mix playlists and individual videos in the same batch. The app saves available playlist entries in a folder named after the playlist and numbers their files in playlist order. A custom series name replaces the playlist title. Unavailable entries are counted in **Details**.

## Download history and retries

**History** tracks recent videos that started downloading. Filter to **Saved videos** or **Needs retry**, then select a row to play the file, open its folder, or copy its YouTube link. **Retry video** tries one failed, cancelled, or interrupted video using its original quality, format, subtitle choice, folder, and playlist number. If the downloader still has the matching partial file, it can resume from that file.

History stays on this computer and keeps the most recent 200 finished entries. Videos saved before this feature still appear in **Library**. A saved video stays available even if its subtitle job is interrupted.

## Find movies and series

Open **Find Movies & Series**, type a title, pick **Movies**, **TV series**, or both, and select a country. In-app results come from [Wikidata](https://www.wikidata.org/), [TVmaze](https://www.tvmaze.com/api), [the Apple storefront](https://developer.apple.com/library/archive/documentation/AudioVideo/Conceptual/iTuneSearchAPI/), [Internet Archive](https://archive.org/), and YouTube where relevant. Internet Archive and YouTube listings can be clips, trailers, or other related material. A catalog result identifies a title and does not establish streaming availability.

Select a result to open its source or official site. The **Open search** button checks sites such as JustWatch, Netflix, Prime Video, Apple TV, and Plex in your browser, even if the catalogs return nothing. **Web · where to watch** searches the wider web using your country. Some streaming services require you to sign in to see their results.

For a YouTube result, **Add YouTube link** puts that upload in the Download tab so you can check your settings before starting.

## Watchlist and preferences

Select a finder result and click **Save to watchlist**. In **Watchlist**, use **Find this title** to refresh its results or **Open saved link** to revisit the original source. Saved titles, your download folder, quality, format, subtitle choice, and search country are kept locally in your Windows app data and survive closing the app.

## Subtitles

Choose **Original YouTube captions**, **English subtitles**, or **Original + English**. When YouTube has English captions, the app can use those, including automatic translations YouTube offers. If it can't fetch English captions, the app generates timed English subtitles from the audio on this laptop. In **Library**, select an existing video and click **Generate English subtitles** to do this without downloading it again.

Subtitles are saved as `.srt` files alongside the video. When VLC is installed, **Play video** loads the English subtitle file if one is present. You can also open the video in your preferred player and load the SRT manually. Local speech translation supports many languages, and the result depends on audio clarity and the language. Long videos may take time to process. The video audio does not change.

## Library folders

New videos go under **download folder → category → series**. Automatic grouping looks at YouTube's category and can use an identified series, a playlist name from a video link, or an episode title. It uses the channel when a series can't be identified. Other videos already in the chosen folder still appear in Library.

**MKV** keeps the best available video and audio streams without re-encoding. **MP4** selects H.264 video and AAC audio for wider compatibility. The actual resolution depends on the source and the selected format. VLC can play MKV files.

## Build from a clone

Use 64-bit Windows with .NET Framework 4.8 and Node.js 22 or later. From PowerShell in the clone, run:

```powershell
.\setup-engines.ps1
.\setup-translation.ps1
.\build.ps1
```

The first script installs the local yt-dlp, FFmpeg, and FFprobe tools. The second adds the optional offline speech model (about 148 MB). It is needed for generating English subtitles from audio. Run **Custom YouTube Downloader.exe** after building and keep `tools` beside it. The repository excludes binaries, downloaded media, and local app settings, including history and watchlist files.

Run `.\test.ps1` to check caption selection and the Windows layout. Run `.\package.ps1` to build the versioned release ZIP under `bin`. Published releases link the Windows download to its tagged source commit.

Private playlists, rentals, and other restricted or unavailable videos may not download. [YouTube terms](https://www.youtube.com/t/terms)

## App icon

The built-in image generator created `assets/app-icon.png`. The Windows icon in `assets/app-icon.ico` includes twelve sizes from 16 to 256 pixels, including 20, 28, 40 and 56 pixels for fractional display scales.

> Use case: logo-brand. Create a distinctive Windows desktop app icon for Custom YouTube Downloader. A compact rounded midnight-navy tile holds one bold mint-green downward arrow that ends in a coral-orange film tray. The tray has two simple negative-space perforations to suggest film. Centered symmetrical silhouette, flat vector-like edges with subtle depth, high contrast, fills most of square with a transparent margin. Readable at 16 pixels. No text, no letters, no branding, no YouTube logo, no extra ornaments.
