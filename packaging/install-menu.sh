#!/bin/sh
# Register the extracted app for this account. Keep the extracted directory in place.
set -eu
appdir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
exe="$appdir/CustomYouTubeDownloader"
if [ ! -x "$exe" ]; then
    printf '%s\n' "Extract the complete Linux package before registering it." >&2
    exit 1
fi

data_home=${XDG_DATA_HOME:-"$HOME/.local/share"}
apps="$data_home/applications"
icons="$data_home/icons/hicolor/256x256/apps"
id=io.github.william-osei.custom-youtube-downloader
mkdir -p "$apps" "$icons"

# A symlink gives the launcher a stable executable name, including for app folders containing '='.
links="$data_home/custom-youtube-downloader-launcher"
mkdir -p "$links"
link="$links/CustomYouTubeDownloader"
if [ -e "$link" ] && [ ! -L "$link" ]; then
    printf 'A file already exists at %s. Choose a different XDG_DATA_HOME.\n' "$link" >&2
    exit 1
fi
case "$link" in
    *=*|*'
'*) printf '%s\n' "The applications menu needs a data directory without an equals sign or a newline." >&2; exit 1 ;;
esac
ln -sfnT -- "$exe" "$link"

# Escape the Exec argument for the desktop entry's two decoding steps.
exec_path=$(printf '%s' "$link" | sed 's/[\\"`$]/\\&/g; s/\\/\\\\/g; s/%/%%/g')
entry=$(mktemp "$apps/.custom-youtube-downloader.XXXXXX")
trap 'rm -f -- "$entry"' EXIT
cat >"$entry" <<EOF
[Desktop Entry]
Type=Application
Name=Custom YouTube Downloader
Comment=Save videos and playlists for offline playback
Exec="$exec_path"
Icon=$id
Terminal=false
Categories=AudioVideo;Video;
EOF
install -m 644 "$appdir/app-icon.png" "$icons/$id.png"
chmod 644 "$entry"
mv -f -- "$entry" "$apps/$id.desktop"
printf '%s\n' "Custom YouTube Downloader is now in your applications menu."
