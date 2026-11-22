# Custom YouTube Downloader

A Windows desktop app for finding movies and series, saving YouTube videos you have permission to download, and searching your offline library.

## Install or update on Windows

Download the Windows ZIP from [GitHub Releases](https://github.com/William-osei/custom-youtube-downloader/releases/latest) and extract it to a permanent folder. On a first install, use 64-bit Windows with .NET Framework 4.8 and Node.js 22 or later. Run `.\setup-engines.ps1` from PowerShell in the extracted folder. For offline speech translation, also run `.\setup-translation.ps1`.

To update an existing copy, close the app and extract the new ZIP into the same folder, replacing the matching files. Keep the executable, its `.exe.config` file, and the `tools` folder together. The title bar shows the installed version. Choose **Releases** in the app to find updates.

## Use the app

1. Open **Custom YouTube Downloader** from your desktop shortcut or run the executable in this project folder.
2. In **Download**, paste a YouTube video, Shorts, or playlist link, or choose **Paste links** to append links from the clipboard. For a batch, use one link per line. Choose a download folder or use **Downloads\Offline Videos**.
3. Choose the video quality, file format, and subtitle option. Turn on **Add English audio (synthetic voice)** if you want a dubbed copy. Enter a category and series to choose where to group the batch, or leave them blank for automatic folders.
4. Click **Start downloads**. Open **Library** to search, play a saved video, or find its files. **Details** shows the result of each job.

Use the sidebar or **Ctrl+1** through **Ctrl+5** to switch between Download, Library, Discover, Watchlist, and History. **Ctrl+Tab** moves to the next workspace and **Ctrl+Shift+Tab** moves back. The sidebar switches to icons in narrow windows, and the settings stack vertically.

Maximize the window to use the desktop work area, or press **F11** for full screen. Press **Esc** or **F11** to return to your previous window size. On short displays, scroll inside the workspace to reach the remaining controls.

## Simultaneous downloads and playlists

**Simultaneous downloads** accepts 1 through 4, with 3 selected by default. Paste multiple links or a playlist, then select **Start downloads** once. The app fills available download slots as videos finish. The status area shows how many items have finished and which are downloading or processing. Subtitle and voice processing run one video at a time alongside the transfers. **Cancel** stops the batch and leaves finished files in the library.

To download a full playlist, copy the link from the playlist page or its **Share** button. A link to a video inside a playlist still downloads the selected video. You can mix playlists and individual videos in the same batch. The app saves available playlist entries in a folder named after the playlist and numbers their files in playlist order. A custom series name replaces the playlist title. Unavailable entries are counted in **Details**.

## Download history and retries

**History** tracks recent videos that started downloading. Filter to **Saved videos** or **Needs retry**, then select a row to play the file, open its folder, or copy its YouTube link. **Retry video** tries one failed, cancelled, or interrupted video using its original quality, format, subtitle choice, folder, and playlist number. If the downloader still has the matching partial file, it can resume from that file.

History stays on this computer and keeps the most recent 200 finished entries. Videos saved before this feature still appear in **Library**. A saved video stays available even if its subtitle job is interrupted.

If English audio is interrupted, open **History → Needs retry**. Choose **Play original** to watch the saved video or **Retry English audio** to finish the dub. The app can reuse the original from its download folder.

## English audio

Enable **Add English audio (synthetic voice)** in Download, or select an original MP4 or MKV in **Library**. Choose an **Audio style** before starting the download or selecting **Create English audio**.

| Audio style | What you hear |
| --- | --- |
| Faithful translation | A Windows English voice reads the timed translation, including names and numbers as written. This is the default. |
| Serious storyteller | The same translated dialogue in a lower, more measured voice. |
| Sarcastic storyteller | The dialogue plus an occasional spoken “Narrator’s aside.” The app recognizes particular story situations and adds a remark only when there is room before the next line. |

Each style creates a separate video. The original soundtrack remains selectable in the player, and the English subtitles contain the translated dialogue. An installed English Windows desktop speech voice is required. Changing a transcript and creating the same style again produces a numbered copy so you can compare versions.

### Review a translation

In **Library**, select the original video and choose **Edit English text**. If it has no English transcript yet, choose **Generate English subtitles** first. Select a line, click **Listen to original** to hear that part of the source, and correct the wording. **Save corrections** (Ctrl+S) preserves the cue times and backs up the previous SRT. Choose **Create English audio** to hear the revised words.

To revisit an older automatic translation, use **Retranslate from audio** in the editor. Leave the source language on **Automatic**, or choose the language being spoken if detection got it wrong. The app runs the installed model on the original dialogue and keeps the previous SRT beside the video. A normal download reuses an existing English SRT, including your edits.

## Find movies and series

Open **Discover**, type a title, pick **Movies**, **TV series**, or both, and select a country. In-app results come from [Wikidata](https://www.wikidata.org/), [TVmaze](https://www.tvmaze.com/api), [the Apple storefront](https://developer.apple.com/library/archive/documentation/AudioVideo/Conceptual/iTuneSearchAPI/), [Internet Archive](https://archive.org/), and YouTube where relevant. Internet Archive and YouTube listings can be clips, trailers, or other related material. A catalog result identifies a title and does not establish streaming availability.

Select a result to open its source or official site. The **Open search** button checks sites such as JustWatch, Netflix, Prime Video, Apple TV, and Plex in your browser, even if the catalogs return nothing. **Web · where to watch** searches the wider web using your country. Some streaming services require you to sign in to see their results.

For a YouTube result, **Add YouTube link** puts that upload in the Download tab so you can check your settings before starting.

## Watchlist and preferences

Select a finder result and click **Save to watchlist**. In **Watchlist**, use **Find this title** to refresh its results or **Open saved link** to revisit the original source. Saved titles, your download folder, quality, format, subtitle choice, and search country are kept locally in your Windows app data and survive closing the app.

## Subtitles

Choose **Original YouTube captions**, **English subtitles**, or **Original + English**. The app can use creator-provided English captions or extract a full English text subtitle track embedded in the video. When translation is needed, it uses local Whisper and looks for the original dialogue track and its language. With the upgraded model installed, a YouTube machine translation can serve as a fallback. In **Library**, select a saved video and click **Generate English subtitles** to work offline.

Subtitles are saved as `.srt` files alongside the video. When VLC is installed, **Play video** loads the English subtitle file if one is present. You can also open the video in your preferred player and load the SRT manually. Local speech translation supports many languages and can take time on long videos.

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

The first script installs the local yt-dlp, FFmpeg, and FFprobe tools. The second downloads the multilingual Whisper medium Q5 model (539 MB) for local speech translation. Run the translation setup again when upgrading from a base-model installation to enable medium. Older installs with only the base model still work. Medium uses more CPU time and can run considerably slower than playback on a laptop. Check uncertain words, especially proper names, against the original audio.

Run **Custom YouTube Downloader.exe** after building and keep `tools` beside it. The repository excludes binaries, downloaded media, and local app settings, including history and watchlist files.

Run `.\test.ps1` to check caption selection, transcript editing, keyboard navigation, layouts, overlapping downloads, cancellation and retries. With the media engines and an English voice installed, `.\test.ps1 -Media` also checks real narration, subtitle timing, audio previews and preservation of the original streams. Commit and push each tested update to GitHub. For a new version, set `AppIdentity.Version`, tag the release commit, and publish the Windows ZIP built from that tag with `.\package.ps1`.

Private playlists, rentals, and other restricted or unavailable videos may not download. [YouTube terms](https://www.youtube.com/t/terms)

## App artwork

The Download studio header uses [film-reel.jpg](assets/film-reel.jpg), generated with the built-in ImageGen tool.

<details>
<summary>Film-reel artwork prompt</summary>

Use case: photorealistic editorial still life. Asset: widescreen photographic print for the masthead of an elegant film library desktop application. Panoramic 3:1 horizontal image, approximately 1536x512. On a deep petroleum-green matte studio tabletop sits a dramatic large vintage brushed-silver 35mm cinema film reel with broad circular cutouts, wound dark amber translucent acetate film unspooling and curling to the right into a single graceful loop. Top down three-quarter close-up with sharp tactile grain and warm illuminated edges, analog cinema journal art direction. Put ALL of the reel on the far right half of the image, artistically cropped at the high right corner, with film in the middle right. Left 55% of the image is dark deep evergreen empty subtle gradient / tabletop, calm room for overlay text. Rich black-green and warm copper, high contrast, sophisticated. No text, letters, logos, graphic frames, badges, people or watermarks.

</details>

The built-in image generator created `assets/app-icon.png`. The Windows icon in `assets/app-icon.ico` includes twelve sizes from 16 to 256 pixels, including 20, 28, 40 and 56 pixels for fractional display scales.

> Use case: logo-brand. Create a distinctive Windows desktop app icon for Custom YouTube Downloader. A compact rounded midnight-navy tile holds one bold mint-green downward arrow that ends in a coral-orange film tray. The tray has two simple negative-space perforations to suggest film. Centered symmetrical silhouette, flat vector-like edges with subtle depth, high contrast, fills most of square with a transparent margin. Readable at 16 pixels. No text, no letters, no branding, no YouTube logo, no extra ornaments.
