"""Build a distributable Unix desktop package from dotnet publish output."""

import argparse
import mmap
import os
import plistlib
import re
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
NAME = "Custom YouTube Downloader"
EXE = "CustomYouTubeDownloader"
MAC_MAGICS = {bytes.fromhex(h) for h in ("cffaedfe", "cefaedfe", "feedfacf", "feedface", "cafebabe", "bebafeca")}
DOTNET_BUNDLE_MARKER = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")


def check_macos_bundle(exe):
    # The apphost points at the bundled managed assemblies. Verify it still does after signing.
    with exe.open("rb") as file, mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ) as binary:
        marker = binary.find(DOTNET_BUNDLE_MARKER)
        offset = struct.unpack_from("<Q", binary, marker - 8)[0] if marker >= 8 else 0
        if not 0 < offset < len(binary) or binary.find((EXE + ".runtimeconfig.json").encode(), offset) < 0:
            raise ValueError("Publish macOS with -p:PublishSingleFile=true so the apphost contains its managed code.")


def check_publish(folder, rid):
    exe = folder / EXE
    with exe.open("rb") as binary:
        prefix = binary.read(64)
    arm = rid.endswith("arm64")
    if rid.startswith("linux"):
        valid = prefix[:6] == b"\x7fELF\x02\x01" and struct.unpack_from("<H", prefix, 18)[0] == (183 if arm else 62)
        valid = valid and (folder / "libcoreclr.so").is_file() and (folder / (EXE + ".runtimeconfig.json")).is_file()
    else:
        valid = prefix[:4] == bytes.fromhex("cffaedfe") and struct.unpack_from("<I", prefix, 4)[0] == (0x100000c if arm else 0x1000007)
        check_macos_bundle(exe)
    if not valid:
        raise ValueError(f"Expected a self-contained {rid} publish in {folder}")


def is_native(path):
    with path.open("rb") as file:
        magic = file.read(4)
    return magic in MAC_MAGICS or magic == b"\x7fELF"


def unix_mode(path):
    if path.is_symlink():
        return stat.S_IFLNK | 0o777
    if path.is_dir():
        return stat.S_IFDIR | 0o755
    return stat.S_IFREG | (0o755 if path.suffix == ".sh" or is_native(path) else 0o644)


def sign_bundle(bundle, rcodesign):
    if rcodesign:
        subprocess.run([rcodesign, "sign", str(bundle)], check=True)
    elif sys.platform == "darwin":
        main = bundle / "Contents" / "MacOS" / EXE
        for path in main.parent.rglob("*"):
            if path.is_file() and not path.is_symlink() and path != main and is_native(path):
                subprocess.run(["codesign", "--force", "--sign", "-", str(path)], check=True)
        subprocess.run(["codesign", "--force", "--sign", "-", str(bundle)], check=True)
    else:
        raise ValueError("Packaging macOS on another host requires --rcodesign PATH for ad hoc signing.")
    if sys.platform == "darwin":
        subprocess.run(["codesign", "--verify", "--deep", "--strict", str(bundle)], check=True)


def make_zip(folder, destination):
    # Archive Utility reads Unix mode bits in the central directory, including on cross-builds.
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(folder.rglob("*")):
            info = zipfile.ZipInfo.from_file(path, path.relative_to(folder).as_posix())
            info.create_system = 3
            info.external_attr = unix_mode(path) << 16
            if path.is_symlink():
                info.filename = info.filename.rstrip("/")
                archive.writestr(info, os.readlink(path))
            elif path.is_dir():
                archive.writestr(info, b"")
            else:
                info.compress_type = zipfile.ZIP_DEFLATED
                with path.open("rb") as source, archive.open(info, "w") as output:
                    shutil.copyfileobj(source, output)


def make_tar(folder, destination):
    with tarfile.open(destination, "w:gz", compresslevel=6, dereference=False) as archive:
        for path in sorted(folder.rglob("*")):
            info = archive.gettarinfo(str(path), path.relative_to(folder).as_posix())
            info.mode = stat.S_IMODE(unix_mode(path))
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            if info.isfile():
                with path.open("rb") as content:
                    archive.addfile(info, content)
            else:
                archive.addfile(info)


def package(args):
    check_publish(args.publish, args.rid)
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.version):
        raise ValueError("Expected a three-part release version.")
    mac = args.rid.startswith("osx")
    args.output.mkdir(parents=True, exist_ok=True)
    extension = ".zip" if mac else ".tar.gz"
    target = args.output / f"Custom-YouTube-Downloader-{args.version}-{args.rid}{extension}"
    with tempfile.TemporaryDirectory(dir=args.output) as work:
        stage = Path(work) / "stage"
        folder = stage / (NAME + ".app" if mac else "Custom-YouTube-Downloader")
        binaries = folder / "Contents/MacOS" if mac else folder
        shutil.copytree(args.publish, binaries, symlinks=True, ignore=shutil.ignore_patterns("*.pdb"))
        # chmod is also needed before codesign and running the staged binary on a Unix host.
        for path in binaries.rglob("*"):
            if path.is_file() and not path.is_symlink() and is_native(path):
                path.chmod(0o755)
        with Image.open(ROOT / "assets/app-icon.png") as icon:
            if mac:
                resources = folder / "Contents/Resources"
                resources.mkdir()
                icon.resize((1024, 1024), Image.Resampling.LANCZOS).save(resources / "app-icon.icns")
            else:
                icon.resize((256, 256), Image.Resampling.LANCZOS).save(folder / "app-icon.png")
        if mac:
            plist = {"CFBundlePackageType": "APPL", "CFBundleIdentifier": "io.github.william-osei.custom-youtube-downloader",
                     "CFBundleExecutable": EXE, "CFBundleName": NAME, "CFBundleDisplayName": NAME,
                     "CFBundleVersion": args.version, "CFBundleShortVersionString": args.version,
                     "CFBundleIconFile": "app-icon.icns", "CFBundleInfoDictionaryVersion": "6.0",
                     "NSHighResolutionCapable": True, "LSApplicationCategoryType": "public.app-category.video"}
            (folder / "Contents/Info.plist").write_bytes(plistlib.dumps(plist))
            sign_bundle(folder, args.rcodesign)
            check_macos_bundle(binaries / EXE)
        guide_dir = stage if mac else folder
        scripts = ["setup-translation.sh"] if mac else ["install-menu.sh", "setup-translation.sh"]
        for name in scripts:
            # A Windows checkout may have CRLF even though the package runs under /bin/sh.
            script = guide_dir / name
            script.write_bytes((ROOT / "packaging" / name).read_bytes().replace(b"\r\n", b"\n"))
            script.chmod(0o755)
        shutil.copy2(ROOT / "packaging/GETTING-STARTED.md", guide_dir / "GETTING-STARTED.md")
        pending = Path(work) / target.name
        (make_zip if mac else make_tar)(stage, pending)
        os.replace(pending, target)
    print(target.resolve())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", type=Path, required=True, help="Self-contained publish directory")
    parser.add_argument("--rid", choices=("linux-x64", "linux-arm64", "osx-x64", "osx-arm64"), required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "bin")
    parser.add_argument("--rcodesign", help="Path to rcodesign for cross-packaging macOS")
    package(parser.parse_args())
