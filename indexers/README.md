# Local Cardigann v11 definitions

This development directory contains a small proof set copied from the official
[Prowlarr Indexers](https://github.com/Prowlarr/Indexers) `definitions/v11`
catalog on 2026-08-23:

- `internetarchive.yml` — JSON response parsing
- `linuxtracker.yml` — HTML/CSS response parsing
- `showrss.yml` — XML response parsing

At runtime Nebula Bridge reads top-level `.yml` and `.yaml` files from:

`<Jellyfin data directory>/nebulabridge/indexers`

Set `NEBULA_BRIDGE_INDEXER_DEFINITIONS` to use a different local directory.
Definitions are validated against the embedded `schema.json`; newly discovered
indexers start disabled. Use **Update Indexers** on the plugin settings page after
adding or changing files.

This directory is development input only. It is not a remote catalog and Nebula
Bridge does not proxy searches through a distribution service.
