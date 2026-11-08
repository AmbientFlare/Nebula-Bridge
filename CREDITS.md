# Credits

Nebula Bridge is a full fork of [Gelato](https://github.com/lostb1t/Gelato). The original authors and contributors established the Jellyfin virtual-library, Stremio integration, metadata, and playback foundations from which this project was forked.

Nebula Bridge's native-source implementation was also informed by projects that have spent years making media discovery systems more understandable and reliable:

- **Cardigann and [Prowlarr Indexers](https://github.com/Prowlarr/Indexers)** provide the v11 definition contract used by
  the native-source subsystem. Nebula Bridge's C# execution engine is implemented
  in this fork; the exact v11 `schema.json` and the Internet Archive, LinuxTracker,
  and showRSS proof definitions are imported from the GPL-3.0 Prowlarr Indexers
  repository and retained under this fork's GPL-3.0 license.
- **AIOStreams** demonstrated useful ways to normalize, deduplicate, rank, and limit results from several sources before presenting them to a player. Nebula Bridge implements only a small native aggregation core and does not port AIOStreams.
- **Net.Codecrete.QrCodeGenerator** provides the MIT-licensed, server-side QR encoder used for Trakt device activation. No external QR service receives activation data.

Thank you to their maintainers and contributors for making their work available to study.
