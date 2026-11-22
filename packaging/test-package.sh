#!/bin/sh
# Exercise the public package format on a native Linux/macOS desktop or test runner.
set -eu
archive=${1:?Pass the packaged tar.gz or zip}
if [ ! -f "$archive" ]; then
    printf 'Package not found: %s\n' "$archive" >&2
    exit 1
fi
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
case "$(uname -s)" in
    Linux)
        tar -xzf "$archive" -C "$scratch"
        folder="$scratch/Custom-YouTube-Downloader"
        app="$folder/CustomYouTubeDownloader"
        # The menu entry must still point to this extracted app when the data path contains spaces.
        export XDG_DATA_HOME="$scratch/desktop data"
        "$folder/install-menu.sh"
        desktop-file-validate "$XDG_DATA_HOME/applications/io.github.william-osei.custom-youtube-downloader.desktop"
        test "$(readlink "$XDG_DATA_HOME/custom-youtube-downloader-launcher/CustomYouTubeDownloader")" = "$app"
        xvfb-run -a "$app" --smoke-test
        ;;
    Darwin)
        ditto -x -k "$archive" "$scratch"
        bundle="$scratch/Custom YouTube Downloader.app"
        app="$bundle/Contents/MacOS/CustomYouTubeDownloader"
        plutil -lint "$bundle/Contents/Info.plist"
        codesign --verify --deep --strict "$bundle"
        "$app" --smoke-test
        # LaunchServices can return success even when the app's smoke test fails.
        : >"$scratch/finder.stdout"
        : >"$scratch/finder.stderr"
        /usr/bin/open -W -n --stdout "$scratch/finder.stdout" --stderr "$scratch/finder.stderr" "$bundle" --args --smoke-test
        if ! grep -Fxq 'Opened native window and verified Download, Library, Discover, Watchlist and History.' "$scratch/finder.stdout"; then
            cat "$scratch/finder.stdout" "$scratch/finder.stderr" >&2
            printf '%s\n' "The Finder launch did not complete all five workspaces." >&2
            exit 1
        fi
        ;;
    *) printf '%s\n' "Run this check on Linux or macOS." >&2; exit 1 ;;
esac
version=$("$app" --version)
printf 'App version %s\n' "$version"
if [ -n "${2:-}" ] && [ "$version" != "$2" ]; then
    printf 'Expected version %s, got %s\n' "$2" "$version" >&2
    exit 1
fi
"$app" --self-test
