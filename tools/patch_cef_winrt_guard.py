#!/usr/bin/env python3
"""Apply the source-equivalent WinRT DCHECK removal to one exact CEF 154 binary.

This is a release bridge for the existing CEF build. It only writes a separate
output copy, refuses unknown binaries, verifies the PE RVA/instructions, and
updates the MBrowser custom-CEF hash manifest.
"""
import argparse
import hashlib
import json
import shutil
import struct
from pathlib import Path

SOURCE_SHA256 = "D3F51E3651ECEDBE247E3F8F0FA1194F4872E74999C4B0FCC35BD40FE3BD4B8A"
PATCHED_SHA256 = "FDCEBC3CF42555CD02D9BD20CD06B84F259BFF73D6586C5B526B75993E7380F7"
TARGET_RVA = 0x06ED82A4
EXPECTED = bytes.fromhex("741d488d4c24284c8db4")
REPLACEMENT = bytes.fromhex("eb1d488d4c24284c8db4")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def rva_to_offset(path: Path, rva: int) -> tuple[int, str]:
    with path.open("rb") as stream:
        header = stream.read(8192)
    pe = struct.unpack_from("<I", header, 0x3C)[0]
    if header[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Not a PE image")
    section_count = struct.unpack_from("<H", header, pe + 6)[0]
    optional_size = struct.unpack_from("<H", header, pe + 20)[0]
    section_table = pe + 24 + optional_size
    for index in range(section_count):
        pos = section_table + 40 * index
        name, virtual_size, virtual_address, raw_size, raw_address = struct.unpack_from(
            "<8sIIII", header, pos
        )
        if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
            return raw_address + rva - virtual_address, name.split(b"\0")[0].decode()
    raise ValueError("RVA is not mapped by a PE section")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path, help="Unmodified CEF 154 libcef.dll")
    parser.add_argument("target", type=Path, help="Separate output libcef.dll")
    parser.add_argument("--manifest", type=Path, help="CUSTOM_LIBCEF_SHA256.txt")
    args = parser.parse_args()

    source = args.source.resolve()
    target = args.target.resolve()
    if source == target:
        raise ValueError("Refusing in-place modification")

    current_target_hash = sha256(target) if target.exists() else None
    if current_target_hash == PATCHED_SHA256:
        result_hash = current_target_hash
        already_patched = True
        offset, section = rva_to_offset(target, TARGET_RVA)
        with target.open("rb") as stream:
            stream.seek(offset)
            if stream.read(len(REPLACEMENT)) != REPLACEMENT:
                raise ValueError("Patched hash matched but instruction bytes did not")
    else:
        original_hash = sha256(source)
        if original_hash != SOURCE_SHA256:
            raise ValueError(f"Refusing unknown CEF binary: SHA256={original_hash}")
        offset, section = rva_to_offset(source, TARGET_RVA)
        with source.open("rb") as stream:
            stream.seek(offset)
            if stream.read(len(EXPECTED)) != EXPECTED:
                raise ValueError("Unexpected instructions at the guarded RVA")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        with target.open("r+b") as stream:
            stream.seek(offset)
            if stream.read(len(EXPECTED)) != EXPECTED:
                raise ValueError("Target copy changed before patch")
            stream.seek(offset)
            stream.write(REPLACEMENT)
            stream.flush()
        result_hash = sha256(target)
        if result_hash != PATCHED_SHA256:
            raise ValueError(f"Patched output hash mismatch: {result_hash}")
        already_patched = False

    if args.manifest:
        args.manifest.parent.mkdir(parents=True, exist_ok=True)
        args.manifest.write_text(result_hash + "\n", encoding="ascii")

    print(json.dumps({
        "target": str(target),
        "already_patched": already_patched,
        "rva": f"0x{TARGET_RVA:X}",
        "file_offset": f"0x{offset:X}",
        "section": section,
        "before": EXPECTED.hex(" "),
        "after": REPLACEMENT.hex(" "),
        "sha256": result_hash,
    }, indent=2))


if __name__ == "__main__":
    main()
