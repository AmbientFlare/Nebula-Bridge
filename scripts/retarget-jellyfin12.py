#!/usr/bin/env python3
"""Rewrite the checked-in 10.11 build metadata into the Jellyfin 12 release line.

Nebula Bridge ships one source tree as two artifacts: a net9.0 build against the
Jellyfin 10.11.11 packages and a net10.0 build against the Jellyfin 12.0.0
packages. Both are advertised from the SAME public manifest, so which one a
server installs has to be decided by Jellyfin itself rather than by the user.

Jellyfin's InstallationManager.GetCompatibleVersions keeps only the entries
whose targetAbi is <= the running server version and then takes the highest
version number. That is the whole mechanism, and it is identical in 10.11.11
and 12.0. So:

  * a 10.11 server never sees a targetAbi 12.0.0.0 entry at all, and
  * a 12 server sees both lines and must therefore find the 12 entry numerically
    higher, always.

This script encodes that invariant. The 10.11 line keeps its natural numbering
(0.x today) and the 12 line is derived from it as

    12.(major * 100 + minor).patch.revision

which is monotonic with the 10.11 line and can never collide with it: the 10.11
line would have to reach major version 12 to do so. Deriving it instead of
maintaining a second number by hand is deliberate -- a manifest version that
disagrees with the assembly version makes Jellyfin re-offer the update forever.

Run from the repository root, in a throwaway checkout (it edits in place):

    scripts/retarget-jellyfin12.py            # rewrite build.yaml + csproj
    scripts/retarget-jellyfin12.py --print    # just print the mapped version
"""

import argparse
import re
import sys
from pathlib import Path

TARGET_ABI = "12.0.0.0"
FRAMEWORK = "net10.0"


def map_version(version: str) -> str:
    """0.4.1.1 -> 12.4.1.1;  1.0.0.0 -> 12.100.0.0."""
    parts = version.strip().split(".")
    if len(parts) != 4 or not all(p.isdigit() for p in parts):
        raise SystemExit(f"unexpected version shape: {version!r} (want major.minor.patch.rev)")
    major, minor, patch, rev = (int(p) for p in parts)
    stream_minor = major * 100 + minor
    if stream_minor > 65535:
        raise SystemExit(f"mapped minor {stream_minor} exceeds the 16-bit version field")
    return f"12.{stream_minor}.{patch}.{rev}"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".")
    parser.add_argument("--print", dest="print_only", action="store_true")
    args = parser.parse_args()
    root = Path(args.root)

    build_yaml = root / "build.yaml"
    csproj = root / "NebulaBridge.csproj"
    text = build_yaml.read_text(encoding="utf-8")

    match = re.search(r'^version:\s*"([^"]+)"', text, re.MULTILINE)
    if not match:
        raise SystemExit("no version: line in build.yaml")
    source_version = match.group(1)
    mapped = map_version(source_version)

    if args.print_only:
        print(mapped)
        return

    text = re.sub(r'^version:\s*"[^"]+"', f'version: "{mapped}"', text, count=1, flags=re.MULTILINE)
    text = re.sub(r'^targetAbi:\s*"[^"]+"', f'targetAbi: "{TARGET_ABI}"', text, count=1, flags=re.MULTILINE)
    text = re.sub(r'^framework:\s*"[^"]+"', f'framework: "{FRAMEWORK}"', text, count=1, flags=re.MULTILINE)
    build_yaml.write_text(text, encoding="utf-8")

    # The assembly version must match the manifest version or Jellyfin re-offers
    # the update after every install.
    proj = csproj.read_text(encoding="utf-8")
    proj, count = re.subn(
        r"<Version>[^<]+</Version>", f"<Version>{mapped}</Version>", proj, count=1
    )
    if count != 1:
        raise SystemExit("no <Version> element in NebulaBridge.csproj")
    csproj.write_text(proj, encoding="utf-8")

    print(f"Retargeted Jellyfin 12 line: {source_version} -> {mapped} (abi {TARGET_ABI}, {FRAMEWORK})", file=sys.stderr)
    print(mapped)


if __name__ == "__main__":
    main()
