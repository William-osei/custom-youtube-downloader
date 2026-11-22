#!/bin/sh
# Install the release's multilingual whisper.cpp model for the current user.
set -eu
case "$(uname -s)" in
    Darwin) model_dir="$HOME/Library/Application Support/Custom YouTube Downloader/models" ;;
    Linux) model_dir="${XDG_CONFIG_HOME:-"$HOME/.config"}/custom-youtube-downloader/models" ;;
    *) printf '%s\n' "This installer runs on Linux and macOS." >&2; exit 1 ;;
esac
name=ggml-medium-q5_0.bin
sha=19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f
url="https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/$name"
mkdir -p "$model_dir"
target="$model_dir/$name"

digest() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum <"$1" | cut -d ' ' -f 1
    else
        shasum -a 256 <"$1" | cut -d ' ' -f 1
    fi
}
if [ -f "$target" ] && [ "$(digest "$target")" = "$sha" ]; then
    printf 'Whisper medium is installed at %s\n' "$target"
    exit 0
fi
tmp=$(mktemp "$model_dir/.cyd-model.XXXXXX")
trap 'rm -f "$tmp"' EXIT
printf '%s\n' 'Downloading the 539 MB Whisper medium model…'
curl --fail --location --proto '=https' --proto-redir '=https' --retry 2 --output "$tmp" "$url"
if [ "$(digest "$tmp")" != "$sha" ]; then
    printf '%s\n' 'The downloaded model failed its checksum check. Please retry.' >&2
    exit 1
fi
mv -f "$tmp" "$target"
printf 'Verified model saved to %s\n' "$target"
