# Nebula Bridge

A Jellyfin plugin that creates self-updating movie & TV libraries from Trakt and TMDB, searches public indexers natively, and resolves playback through debrid caching — TorBox today, with more providers on the way. Family-friendly access controls included. Free and open source under GPL-3.0.

## What you get

- **Self-updating libraries** — Trending, Popular, Anticipated, and a personal **Trakt Next Episodes** library that always shows the next episode of every show you follow.
- **Search-and-watch discovery** — search for any movie or show right inside Jellyfin. Watch something once and it's promoted into your real library; ignore it and it cleans itself up.
- **Instant playback through your debrid account** — only already-cached torrents are used, streams play through Jellyfin's normal pipeline, and your credentials never touch a client device.
- **Per-user access control** — decide exactly who can see Nebula Bridge libraries and who can search beyond your local media using standard Jellyfin user settings plus a simple per-user toggle.

## Requirements

- [Jellyfin](https://jellyfin.org) **10.11.x**
- A [TorBox](https://torbox.app) account (playback only — browsing, search, and metadata work without it)
- Optional: a Trakt account for personal catalogs and next-episode tracking

## Install

1. Open your Jellyfin Dashboard → **Plugins** → **Repositories**
2. Click **+** and add this URL:

   ```
   https://raw.githubusercontent.com/AmbientFlare/Nebula-Bridge/main/manifest.json
   ```

3. Go to **Catalog**, find **Nebula Bridge**, and click **Install**
4. Restart Jellyfin when prompted

Updates are automatic after that — when a new version is released, Jellyfin offers it on the Plugins page.

## Setup (one time, as the server administrator)

Open **Dashboard → Plugins → Nebula Bridge**:

1. **Connect Trakt** *(optional)* — easiest path is *Use Jellyfin Trakt account* if you already run the official Trakt plugin; otherwise use the on-screen device code or QR code.
2. **Add your TorBox API token** — from your TorBox account settings. You can paste it into the plugin settings or inject `NEBULA_BRIDGE_TORBOX_API_TOKEN` as an environment variable.
3. **Enable what you want** — turn on the virtual libraries (Trending, Next Episodes, …) and, under Native Sources, enable the indexer pipeline and TorBox resolver.
4. Point the configured movie/series paths at matching Jellyfin libraries and scan.

Your family and friends don't need to do anything — they just see new libraries appear. To restrict someone, use the standard Dashboard → Users library-access settings, plus the per-user Nebula Bridge toggle in the plugin configuration.

## Good to know

- Playback uses only torrents already cached by your debrid service; Nebula Bridge never starts an uncached download.
- Trending refreshes daily, Popular/Anticipated weekly, and Trakt Next Episodes twice a day (1 AM / 1 PM server-local time).
- Stale items are pruned automatically unless you've watched, favorited, or started them.
- Searches go directly from your server to each indexer's public site. Private trackers, logins, CAPTCHAs, and uncached downloads are deliberately unsupported.

## Problems?

Check the [issue tracker](https://github.com/AmbientFlare/Nebula-Bridge/issues) — include your Jellyfin version and the plugin version from the Plugins page.

## Building from source

```bash
dotnet build NebulaBridge.csproj -c Release
dotnet test NebulaBridge.Tests/NebulaBridge.Tests.csproj -c Release
```

Requires the .NET 9 SDK and targets Jellyfin 10.11.11. Releases are built automatically from the tagged sources in this repository.

## License and ancestry

Nebula Bridge is GPL-3.0. It is forked from [Gelato](https://github.com/lostb1t/Gelato); see [CREDITS.md](CREDITS.md) for upstream and dependency acknowledgements. Trakt integration notes are in [docs/TRAKT_API_NOTES.md](docs/TRAKT_API_NOTES.md).
