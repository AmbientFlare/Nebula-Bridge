# Jellyfin 12 compatibility assessment

Assessment date: 2026-08-23

## Supported baseline

The plugin targets .NET 9 and Jellyfin packages `10.11.11`, matching the lab's current stable Jellyfin container. The Release build and test suite complete with zero warnings and zero errors on that baseline.

## Upcoming release

Jellyfin's current next-generation packages are `12.0.0-rcrc3` and target .NET 10. A compile spike was run with:

```sh
dotnet build NebulaBridge.csproj --configuration Release \
  -p:NebulaBridgeTargetFramework=net10.0 \
  -p:JellyfinVersion=12.0.0-rcrc3
```

It fails with seven missing interface members:

- `ICollectionManager.GetCollectionsContainingItem(User, Guid)`
- `IDtoService.GetBaseItemDtos(IReadOnlyList<BaseItem>, DtoOptions, User?, BaseItem?, bool)`
- `IPlaylistManager.AddItemToPlaylistAsync(Guid, IReadOnlyCollection<Guid>, int?, Guid)`
- `IItemRepository.GetMediaStreamLanguages(InternalItemsQuery, MediaStreamType)`
- `IItemRepository.GetQueryFiltersLegacy(InternalItemsQuery)`
- `IProviderManager.GetMetadataProviders<T>(BaseItem, LibraryOptions, bool)`
- `IMediaSegmentProvider.CleanupExtractedData(Guid, CancellationToken)`

These errors confirm that NebulaBridge cannot be loaded on Jellyfin 12 unchanged. The embedded `IHasWebPages` configuration mechanism itself remains available, but NebulaBridge's extensive service decorators make the migration high-risk. A dedicated Jellyfin 12 branch should add forwarding implementations, then exercise item filtering, search insertion, playlists, collections, playback source selection, metadata providers, and segment cleanup in a real Jellyfin 12 test server.

The non-blocking weekly compatibility workflow intentionally reports this compile failure without changing the stable artifact.
