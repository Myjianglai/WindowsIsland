"""Publish self-contained macOS .app ZIPs with Unix executable permissions."""

import argparse
import hashlib
import plistlib
import stat
import struct
import subprocess
import zipfile
from pathlib import Path
from xml.etree import ElementTree


def package(sdk: str, project: Path, output: Path, version: str, rid: str) -> Path:
    publish = output / f"publish-{rid}"
    subprocess.run(
        [sdk, "publish", str(project), "-c", "Release", "-r", rid,
         "--self-contained", "true", "-p:DebugType=None", "-o", str(publish)],
        check=True,
    )
    app = "Island.app/Contents"
    manifest = plistlib.dumps({
        "CFBundleDevelopmentRegion": "zh-Hans",
        "CFBundleDisplayName": "灵动岛",
        "CFBundleExecutable": "IslandMac",
        "CFBundleIdentifier": "com.myjianglai.windowsisland.mac",
        "CFBundleIconFile": "Island.icns",
        "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleName": "Island",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": version,
        "CFBundleVersion": version,
        "LSMinimumSystemVersion": "14.0",
        "NSHighResolutionCapable": True,
        "NSAppleEventsUsageDescription": "灵动岛需要读取和控制 Apple Music 或 Spotify 的播放状态。",
    })
    executable = publish / "IslandMac"
    with executable.open("rb") as handle:
        magic, cpu = struct.unpack("<II", handle.read(8))
    expected_cpu = {"osx-arm64": 0x0100000C, "osx-x64": 0x01000007}[rid]
    if magic != 0xFEEDFACF or cpu != expected_cpu:
        raise ValueError(f"Unexpected Mach-O architecture: {rid}")

    png = (project.parent / "Island.png").read_bytes()
    icns = struct.pack(">4sI4sI", b"icns", len(png) + 16, b"icp5", len(png) + 8) + png
    archive = output / f"Island-Mac-{version}-{rid}.zip"
    temp = archive.with_suffix(".zip.tmp")
    with zipfile.ZipFile(temp, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        bundle.writestr(f"{app}/Info.plist", manifest)
        bundle.writestr(f"{app}/Resources/Island.icns", icns)
        for file in sorted(publish.rglob("*")):
            if not file.is_file() or file.suffix == ".pdb":
                continue
            name = f"{app}/MacOS/{file.relative_to(publish).as_posix()}"
            info = zipfile.ZipInfo.from_file(file, name)
            info.create_system = 3
            mode = 0o755 if file == executable or file.suffix == ".dylib" else 0o644
            info.external_attr = (stat.S_IFREG | mode) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            with bundle.open(info, "w") as target, file.open("rb") as source:
                while chunk := source.read(1024 * 1024):
                    target.write(chunk)
    with zipfile.ZipFile(temp) as bundle:
        if bundle.testzip() is not None or not (bundle.getinfo(f"{app}/MacOS/IslandMac").external_attr >> 16) & 0o111:
            raise ValueError(f"Archive validation failed: {rid}")
        if plistlib.loads(bundle.read(f"{app}/Info.plist"))["CFBundleExecutable"] != "IslandMac":
            raise ValueError("Invalid application entry point")
    temp.replace(archive)
    return archive


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--rid", choices=("osx-arm64", "osx-x64"), action="append")
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[1] / "src" / "Island.Mac" / "Island.Mac.csproj"
    version = ElementTree.parse(project).findtext(".//Version")
    output = (args.output or project.parent / "bin" / "macos").resolve()
    output.mkdir(parents=True, exist_ok=True)
    artifacts = [package(args.dotnet, project, output, version, rid)
                 for rid in (args.rid or ["osx-arm64", "osx-x64"])]
    lines = []
    for artifact in artifacts:
        with artifact.open("rb") as handle:
            digest = hashlib.file_digest(handle, "sha256").hexdigest()
        lines.append(f"{digest}  {artifact.name}")
        print(artifact)
    (output / "SHA256SUMS.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
