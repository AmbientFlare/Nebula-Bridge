# Nebula Bridge capabilities API

`GET /nebulabridge/capabilities` requires Jellyfin authentication and returns the client-facing Nebula Bridge contract.

Version 1 preserves the existing `apiVersion` and `features` fields. `supportedVersions` lists compatible contract versions. `availability` is caller-specific: a user excluded through **No Nebula Bridge** receives `available: false` and `hierarchyPrefetchAllowed: false`.

The response intentionally excludes provider credentials, debrid status, indexer configuration, and administrator-only settings. Clients must continue to work through ordinary Jellyfin APIs when the endpoint is unavailable or reports unavailable.
