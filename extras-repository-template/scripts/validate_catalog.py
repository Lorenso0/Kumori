#!/usr/bin/env python3
import json
import re
import sys
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SHA = re.compile(r"^[a-f0-9]{64}$")
TAG = re.compile(r"^catalog-\d{4}\.\d{2}\.\d{2}\.\d+(?:-part-\d{3})?$")


def fail(message: str) -> None:
    raise ValueError(message)


def validate_asset(asset: dict, fingerprint: str, suffix: str) -> None:
    if not TAG.fullmatch(asset.get("releaseTag", "")):
        fail(f"invalid release tag for {fingerprint}")
    expected = fingerprint + suffix
    entry_name = asset.get("entryName")
    if entry_name is None:
        if asset.get("assetName") != expected:
            fail(f"asset name must be {expected}")
    else:
        prefix = "packages/" if suffix == ".kextra" else "previews/"
        if entry_name != prefix + expected:
            fail(f"bundle entry name must be {prefix + expected}")
        if not re.fullmatch(r"catalog-bundle-\d{3}\.zip", asset.get("assetName", "")):
            fail(f"invalid bundle name for {expected}")
        if not SHA.fullmatch(asset.get("entrySha256", "")):
            fail(f"invalid bundle-entry SHA-256 for {expected}")
        entry_size = asset.get("entryDownloadBytes")
        if not isinstance(entry_size, int) or not 0 < entry_size <= 256 * 1024 * 1024:
            fail(f"invalid bundle-entry size for {expected}")
    if not SHA.fullmatch(asset.get("sha256", "")):
        fail(f"invalid release-asset SHA-256 for {expected}")
    size = asset.get("downloadBytes")
    if not isinstance(size, int) or not 0 < size <= 256 * 1024 * 1024:
        fail(f"invalid compressed size for {expected}")
    expanded = asset.get("expandedBytes")
    if expanded is not None and (not isinstance(expanded, int) or not 0 < expanded <= 512 * 1024 * 1024):
        fail(f"invalid expanded size for {expected}")
    entries = asset.get("entryCount")
    if entries is not None and (not isinstance(entries, int) or not 0 < entries <= 4096):
        fail(f"invalid entry count for {expected}")


def main() -> None:
    catalog = json.loads((ROOT / "catalog-v1.json").read_text(encoding="utf-8"))
    state = json.loads((ROOT / "state/catalog-state.json").read_text(encoding="utf-8"))
    if catalog.get("schemaVersion") != 1 or state.get("schemaVersion") != 1:
        fail("unsupported schema version")
    if catalog.get("signingKeyId") != "kumori-extras-2026-01":
        fail("unexpected signing key")

    ids: set[str] = set()
    fingerprints: set[str] = set()
    active = catalog.get("packs", [])
    for pack in active:
        pack_id = str(uuid.UUID(pack["packId"]))
        if pack_id in ids:
            fail(f"duplicate packId {pack_id}")
        ids.add(pack_id)
        fingerprint = pack.get("contentFingerprint", "")
        if not SHA.fullmatch(fingerprint):
            fail(f"invalid fingerprint for {pack_id}")
        if fingerprint in fingerprints:
            fail(f"duplicate active fingerprint {fingerprint}")
        fingerprints.add(fingerprint)
        if not isinstance(pack.get("revision"), int) or pack["revision"] < 1:
            fail(f"invalid revision for {pack_id}")
        validate_asset(pack["package"], fingerprint, ".kextra")
        if pack.get("preview") is not None:
            validate_asset(pack["preview"], fingerprint, ".preview.png")

    withdrawn_ids: set[str] = set()
    for withdrawal in catalog.get("withdrawals", []):
        pack_id = str(uuid.UUID(withdrawal["packId"]))
        if pack_id in ids:
            fail(f"pack {pack_id} is both active and withdrawn")
        if pack_id in withdrawn_ids:
            fail(f"duplicate withdrawal {pack_id}")
        withdrawn_ids.add(pack_id)
        if not SHA.fullmatch(withdrawal.get("contentFingerprint", "")):
            fail(f"invalid withdrawn fingerprint for {pack_id}")

    state_packs = state.get("packs", [])
    state_active = {
        str(uuid.UUID(item["pack"]["packId"]))
        for item in state_packs
        if item.get("withdrawnAtUtc") is None
    }
    if state_active != ids:
        fail("catalog active identities do not match publisher state")
    state_withdrawn = {
        str(uuid.UUID(item["pack"]["packId"]))
        for item in state_packs
        if item.get("withdrawnAtUtc") is not None
    }
    if state_withdrawn != withdrawn_ids:
        fail("catalog withdrawals do not match publisher state")
    print(f"validated {len(active)} active packs and {len(withdrawn_ids)} withdrawals")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"catalog validation failed: {error}", file=sys.stderr)
        raise
