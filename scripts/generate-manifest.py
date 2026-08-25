#!/usr/bin/env python3
"""Merge a plugin version entry into the public Jellyfin repository manifest.

Keeps existing version entries, replaces any entry with the same version, and
sorts newest-first (Jellyfin reads the first entry as current).

Usage:
  generate-manifest.py --manifest PATH --version V --checksum MD5 --url URL \
      --target-abi X.Y.Z.0 [--source-url URL] [--changelog-file PATH] \
      [--name NAME] [--guid GUID] [--owner OWNER]
"""

import argparse
import json
import os
import time

DEFAULT_NAME = "Nebula Bridge"
DEFAULT_GUID = "e9d7c793-aee0-49b6-82c1-8ad583453663"  # MUST match NebulaBridgePlugin.Id
DEFAULT_OWNER = "AmbientFlare"
DEFAULT_CATEGORY = "General"
DESCRIPTION = (
    "A Jellyfin plugin that creates self-updating libraries from Trakt & TMDB, "
    "searches public indexers natively, and resolves playback through debrid "
    "caching - TorBox today, with Real-Debrid, AllDebrid, Premiumize and more in "
    "progress. Family-friendly access controls included. GPL-3.0."
)
OVERVIEW = "Virtual debrid-backed libraries for Jellyfin."


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--checksum", required=True)
    parser.add_argument("--url", required=True)
    parser.add_argument("--target-abi", default="10.11.11.0")
    parser.add_argument("--source-url", default="")
    parser.add_argument("--changelog-file", default="")
    parser.add_argument("--name", default=DEFAULT_NAME)
    parser.add_argument("--guid", default=DEFAULT_GUID)
    parser.add_argument("--owner", default=DEFAULT_OWNER)
    args = parser.parse_args()

    changelog = ""
    if args.changelog_file and os.path.exists(args.changelog_file):
        with open(args.changelog_file, encoding="utf-8") as handle:
            changelog = handle.read().strip()
    else:
        changelog = f"Nebula Bridge {args.version}."

    manifest = []
    if os.path.exists(args.manifest):
        with open(args.manifest, encoding="utf-8") as handle:
            content = handle.read().strip()
        if content:
            parsed = json.loads(content)
            if isinstance(parsed, dict):  # single-plugin manifest shape
                parsed = [parsed]
            manifest = parsed

    if not manifest:
        manifest = [
            {
                "guid": args.guid,
                "name": args.name,
                "description": DESCRIPTION,
                "overview": OVERVIEW,
                "owner": args.owner,
                "category": DEFAULT_CATEGORY,
                "versions": [],
            }
        ]

    plugin = next(
        (entry for entry in manifest if entry.get("guid", "").lower() == args.guid.lower()),
        None,
    )
    if plugin is None:
        plugin = dict(manifest[0])
        plugin["guid"] = args.guid
        plugin["name"] = args.name
        manifest.append(plugin)

    versions = [
        v for v in plugin.get("versions", []) if v.get("version") != args.version
    ]
    versions.insert(
        0,
        {
            # Jellyfin 10.11 VersionInfo has NO url field: sourceUrl is the
            # package download location. repositoryUrl is informational.
            "version": args.version,
            "changelog": changelog,
            "targetAbi": args.target_abi,
            "sourceUrl": args.url,
            # Jellyfin compares case-sensitively against its own UPPERCASE hex,
            # with NO algorithm prefix — bare digest only.
            "checksum": args.checksum.split(":")[-1].upper(),
            "repositoryUrl": args.source_url,
            "repositoryName": args.owner,
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        },
    )
    plugin["versions"] = versions[:25]

    with open(args.manifest, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=2)
        handle.write("\n")
    print(f"Manifest updated: {args.name} {args.version}")


if __name__ == "__main__":
    main()
