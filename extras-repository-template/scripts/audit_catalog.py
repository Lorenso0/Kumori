#!/usr/bin/env python3
import hashlib
import json
import os
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = os.environ.get("GITHUB_REPOSITORY", "Lorenso0/Kumori-Extras")


def asset_url(asset: dict) -> str:
    return (
        f"https://github.com/{REPOSITORY}/releases/download/"
        f"{asset['releaseTag']}/{asset['assetName']}"
    )


def verify(asset: dict, entries: list[dict], download: bool) -> None:
    request = urllib.request.Request(
        asset_url(asset),
        method="GET" if download else "HEAD",
        headers={"User-Agent": "Kumori-Extras-integrity"},
    )
    temporary = None
    with urllib.request.urlopen(request, timeout=120) as response:
        if response.status >= 400:
            raise RuntimeError(f"{response.status}: {asset_url(asset)}")
        if not download:
            return
        digest = hashlib.sha256()
        size = 0
        temporary = tempfile.NamedTemporaryFile(delete=False)
        try:
            while chunk := response.read(1024 * 1024):
                digest.update(chunk)
                size += len(chunk)
                temporary.write(chunk)
        finally:
            temporary.close()
        if size != asset["downloadBytes"]:
            raise RuntimeError(f"size mismatch: {asset['assetName']}")
        if digest.hexdigest() != asset["sha256"]:
            raise RuntimeError(f"SHA-256 mismatch: {asset['assetName']}")
    try:
        if entries:
            with zipfile.ZipFile(temporary.name) as archive:
                names = archive.namelist()
                if len(names) != len(set(names)):
                    raise RuntimeError(f"duplicate bundle entries: {asset['assetName']}")
                for entry in entries:
                    name = entry["entryName"]
                    if name not in names:
                        raise RuntimeError(f"missing bundle entry: {name}")
                    entry_hash = hashlib.sha256()
                    entry_size = 0
                    with archive.open(name) as stream:
                        while chunk := stream.read(1024 * 1024):
                            entry_hash.update(chunk)
                            entry_size += len(chunk)
                    if entry_size != entry["entryDownloadBytes"]:
                        raise RuntimeError(f"bundle-entry size mismatch: {name}")
                    if entry_hash.hexdigest() != entry["entrySha256"]:
                        raise RuntimeError(f"bundle-entry SHA-256 mismatch: {name}")
    finally:
        if temporary is not None:
            os.unlink(temporary.name)


def main() -> None:
    download = "--download" in sys.argv
    catalog = json.loads((ROOT / "catalog-v1.json").read_text(encoding="utf-8"))
    containers: dict[tuple[str, str], tuple[dict, list[dict]]] = {}
    entry_count = 0
    for pack in catalog["packs"]:
        for asset in (pack["package"], pack.get("preview")):
            if asset is None:
                continue
            key = (asset["releaseTag"], asset["assetName"])
            if key not in containers:
                containers[key] = (asset, [])
            elif (
                containers[key][0]["sha256"] != asset["sha256"]
                or containers[key][0]["downloadBytes"] != asset["downloadBytes"]
            ):
                raise RuntimeError(f"inconsistent bundle metadata: {asset['assetName']}")
            if asset.get("entryName"):
                containers[key][1].append(asset)
                entry_count += 1
    for asset, entries in containers.values():
        verify(asset, entries, download)
    print(
        f"verified {len(containers)} public release assets and {entry_count} bundle entries "
        f"({'full hashes' if download else 'availability'})"
    )


if __name__ == "__main__":
    main()
