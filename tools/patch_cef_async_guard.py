#!/usr/bin/env python3
"""Inspect or copy-patch one precisely identified Chromium 154 DCHECK branch.

No native rebuild. Original source is never modified.
This merely bypasses a DCHECK; it does not cure the mismatched WinRT operation.
"""
import argparse
import hashlib
import json
import shutil
import struct
from pathlib import Path

EXPECTED_SHA256 = "D3F51E3651ECEDBE247E3F8F0FA1194F4872E74999C4B0FCC35BD40FE3BD4B8A"
TARGET_RVA = 0x06ED82A4
# cmp qword ptr [rdx], r9; je +0x1d; ... DCHECK failure code
EXPECTED_BYTES = bytes.fromhex("741d488d4c24284c8db4")
PATCHED_BYTES = bytes.fromhex("eb1d488d4c24284c8db4")


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(8 * 1024 * 1024), b""):
            h.update(b)
    return h.hexdigest().upper()


def rva_to_offset(path, rva):
    with open(path, "rb") as f:
        header = f.read(8192)
    pe = struct.unpack_from("<I", header, 0x3c)[0]
    if header[pe:pe+4] != b"PE\0\0":
        raise ValueError("Not a PE image")
    sections = struct.unpack_from("<H", header, pe+6)[0]
    optional_size = struct.unpack_from("<H", header, pe+20)[0]
    start = pe+24+optional_size
    for i in range(sections):
        p = start+40*i
        name, virt_size, virt_address, raw_size, raw_address = struct.unpack_from("<8sIIII", header, p)
        if virt_address <= rva < virt_address+max(virt_size, raw_size):
            return raw_address + (rva-virt_address), name.split(b"\0")[0].decode()
    raise ValueError("RVA outside mapped sections")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("source", type=Path)
    p.add_argument("--target", type=Path)
    a = p.parse_args()
    original_sha = sha256_file(a.source)
    if original_sha != EXPECTED_SHA256:
        raise ValueError(f"Wrong libcef.dll hash: {original_sha}")
    off, section = rva_to_offset(a.source, TARGET_RVA)
    with a.source.open("rb") as f:
        f.seek(off - 24)
        context = f.read(72)
    if context[24:24+len(EXPECTED_BYTES)] != EXPECTED_BYTES:
        raise ValueError(f"Unexpected native instructions at RVA: offset=0x{off:X} actual={context[24:24+len(EXPECTED_BYTES)].hex(' ')} surrounding={context.hex(' ')}")
    result = dict(source=str(a.source), source_sha256=original_sha,
                  target_rva=f"0x{TARGET_RVA:X}", file_offset=f"0x{off:X}",
                  section=section, before=context.hex(" "),
                  expected=EXPECTED_BYTES.hex(" "),
                  replacement=PATCHED_BYTES.hex(" "))
    if a.target:
        if a.target.resolve() == a.source.resolve():
            raise ValueError("Refuse in-place mutation")
        a.target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(a.source, a.target)
        with a.target.open("r+b") as f:
            f.seek(off)
            assert f.read(len(EXPECTED_BYTES)) == EXPECTED_BYTES
            f.seek(off)
            f.write(PATCHED_BYTES)
            f.flush()
        with a.target.open("rb") as f:
            f.seek(off)
            assert f.read(len(PATCHED_BYTES)) == PATCHED_BYTES
        result["patched"] = str(a.target)
        result["patched_sha256"] = sha256_file(a.target)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
