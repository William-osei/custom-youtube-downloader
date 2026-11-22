# Custom YouTube Downloader on Linux and macOS

Choose the release that matches your computer. In a terminal, `uname -m` prints `x86_64` for x64 or `arm64`/`aarch64` for ARM64. The .NET runtime is included in each package.

## Linux

1. Extract the matching `.tar.gz` archive into a permanent folder in your home directory. Keep all the extracted files together.
2. Open the extracted `Custom-YouTube-Downloader` folder and launch `CustomYouTubeDownloader`. From a terminal in that folder, run `./CustomYouTubeDownloader`.
3. To add the icon to your applications menu, run `./install-menu.sh` from the same folder. Run it again if you move the app or install a new version.

Use a glibc-based Linux desktop with X11 or XWayland. On Ubuntu, these commands install the X11 libraries, media tools, English narration and yt-dlp.

```sh
sudo apt install libx11-6 libice6 libsm6 libfontconfig1 ffmpeg espeak-ng pipx
pipx install 'yt-dlp[default]'
```

Install Node.js 22 or newer and confirm the version with `node --version`. The app searches your PATH and `~/.local/bin`. If your node version manager sets PATH in a shell profile, you can launch the app from that terminal. [Node.js downloads](https://nodejs.org/en/download) · [yt-dlp JavaScript runtime requirements](https://github.com/yt-dlp/yt-dlp/wiki/EJS)

## macOS

1. Choose `osx-arm64` for Apple silicon or `osx-x64` for an Intel Mac.
2. Unzip the archive in Finder, drag **Custom YouTube Downloader.app** to Applications and double-click it.
3. Install the download tools. If you use Homebrew, run this command.

```sh
brew install yt-dlp ffmpeg node
```

The app searches the standard Intel and Apple silicon Homebrew locations. English narration uses the Mac's built-in `say` command and an installed English voice.

The community macOS build uses an ad hoc signature and has no Apple notarization. macOS may ask you to approve the individual app. Apple explains its prompts and options for software from a developer you trust. [Apple's guide to opening downloaded apps](https://support.apple.com/en-us/102445)

## Optional speech translation

The app can use available English subtitles to create an English voice track. To generate English subtitles directly from speech, install `whisper-cli` from whisper.cpp. On a Mac with Homebrew, use `brew install whisper.cpp`. On Linux, use the package for your distribution or follow the project's build instructions. [whisper.cpp installation](https://github.com/ggml-org/whisper.cpp#quick-start) · [Homebrew whisper.cpp](https://formulae.brew.sh/formula/whisper.cpp)

Run `sh setup-translation.sh` in the extracted folder containing this guide. It downloads the multilingual medium Q5 model (539 MB), verifies its SHA-256 checksum, and stores it in your user settings. If you already have a compatible multilingual whisper.cpp model, you can set `CYD_WHISPER_MODEL` to its absolute path and start the app from that environment.

For tools installed in a custom directory, set `CYD_TOOLS_DIR` to its absolute path. Video downloads use yt-dlp, Node.js, FFmpeg and FFprobe. You can open the app and browse your saved library before installing the download tools.
