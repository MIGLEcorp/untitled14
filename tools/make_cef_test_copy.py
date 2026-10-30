from pathlib import Path
import os
import sys
source = Path(sys.argv[1])
target = Path(sys.argv[2])
total = 0
for root, dirs, files in os.walk(source):
    src_dir = Path(root)
    dst_dir = target / src_dir.relative_to(source)
    dst_dir.mkdir(parents=True, exist_ok=True)
    for filename in files:
        if src_dir == source and filename.lower() == "libcef.dll":
            continue
        src = src_dir / filename
        dst = dst_dir / filename
        if dst.exists():
            continue
        os.link(src, dst)
        total += 1
print(f"Hard-linked {total} matching CEF runtime files in {target}. Patched libcef.dll is independent.")
