# Jellyfin 12 compatibility

Assessment date: 2026-09-10
Verified against: Jellyfin 10.11.11 and Jellyfin 12.0 (`lscr.io/linuxserver/jellyfin:12.0ubu2604-ls48`, reports `12.0.0`).

## Supported server lines

| Server line | Framework | Jellyfin packages | `targetAbi` | Plugin version stream |
| --- | --- | --- | --- | --- |
| Jellyfin 10.11.x | `net9.0` | `10.11.11` | `10.11.11.0` | `0.4.1.1` (upstream version) |
| Jellyfin 12.x | `net10.0` | `12.0.0` | `12.0.0.0` | `12.4.1.1` (derived, see below) |

Both are produced from this one source tree, one commit, one release process.

## Root cause of the Jellyfin 12 failure

The 10.11 artifact does **not** fail an assembly-binding or ABI check. Jellyfin's
plugin `AssemblyLoadContext` redirects `MediaBrowser.*` references to the host
assemblies, so a `net9.0` build carrying `10.11.11.0` references loads far enough
to begin type resolution. It then throws:

```
System.TypeLoadException: Method '<member>' in type '<NebulaBridge decorator>'
  ... does not have an implementation.
```

and the plugin is disabled **before any DI registration runs**. Nebula Bridge
implements several large Jellyfin interfaces directly (decorators over
`IItemRepository`, `IDtoService`, `ICollectionManager`, `IPlaylistManager`,
`IProviderManager`, `IMediaSourceManager`, plus an `IMediaSegmentProvider`), so
every added interface member is a hard load failure.

The runtime error list is necessarily **incomplete**: .NET reports only members
the interface requires and the type lacks. It can never report members the type
carries that the interface no longer declares. Compiling against the real stable
`12.0.0` packages was required to see the full delta.

## API delta that affected Nebula Bridge

Added members (load-time failures):

- `ICollectionManager.GetCollectionsContainingItem(User, Guid)`
- `IItemRepository.GetMediaStreamLanguages(InternalItemsQuery, MediaStreamType)`
- `IItemRepository.GetQueryFiltersLegacy(InternalItemsQuery)`
- `IProviderManager.GetMetadataProviders<T>(BaseItem, LibraryOptions, bool includeDisabled)`
- `IMediaSegmentProvider.CleanupExtractedData(Guid, CancellationToken)`

Signature changes:

- `IDtoService.GetBaseItemDtos(..., bool skipVisibilityCheck = false)`
- `IPlaylistManager.AddItemToPlaylistAsync(Guid, IReadOnlyCollection<Guid>, int? position, Guid userId)`

Interface split (invisible to the runtime error list). `IItemRepository` lost
`DeleteItem`, `SaveItems`, `SaveImages`, `GetNextUpSeriesKeys`,
`UpdateInheritedValues`, `GetCount`, `GetItemCounts`, `FindArtists` and
`ReattachUserDataAsync`; that work moved to `IItemPersistenceService`,
`IItemCountService`, `INextUpService` and `ILinkedChildrenService`.

Type and semantic changes:

- `Video.PrimaryVersionId`: `string` -> `Guid?`
- `LinkedChild.Create` links by `ItemId` always; `LinkedChild.Path` and
  `LinkedChild.LibraryItemId` are now `[Obsolete]`

## Compatibility architecture

One repository, one shared implementation, two compile targets. `NebulaBridge.csproj`
multi-targets `net9.0;net10.0`; the `net10.0` target resolves the `12.0.0` Jellyfin
packages and defines `JELLYFIN_12`. Everything except the seams below compiles
identically for both lines.

Compatibility seams (all compile-time; no reflection, no runtime version probing):

- `Decorators/ItemRepositoryDecorator.cs` — conditional class header; on Jellyfin 12
  the removed members are dropped and `SaveItems` forwards to `IItemPersistenceService`.
  The extra constructor dependency resolves automatically, because the decorator is
  built with `ActivatorUtilities.CreateInstance`.
- `Decorators/DtoServiceDecorator.cs` — two signatures over one shared
  `GetBaseItemDtosCore` body.
- `Decorators/PlaylistManagerDecorator.cs`, `CollectionManagerDecorator.cs`,
  `ProviderManagerDecorator.cs`, `MediaSourceManagerDecorator.cs` — added or
  changed forwards.
- `Providers/IntroDbSegmentProvider.cs` — `CleanupExtractedData`.
- `Common.cs` — `CreateLinkedChild`, `HasPrimaryVersion`, `GetPrimaryVersionId`
  absorb the `PrimaryVersionId` type change and the `LinkedChild` semantics change.

No Phase 7 code (acquisition jobs, sparse cache, retention, import, reconciliation,
user-state transfer) required a seam. `Services/AcquisitionReconciliationService.cs`
contains no conditional compilation at all.

## Packaging and upgrade path

One manifest advertises both streams. Jellyfin's
`InstallationManager.GetCompatibleVersions` filters on
`Version.Parse(targetAbi) <= serverVersion` and then takes the highest version
number, so a plugin version that sorts *above* every 10.11-line version but is
gated behind `targetAbi 12.0.0.0` is offered only to Jellyfin 12 servers and is
invisible to 10.11 servers.

`scripts/retarget-jellyfin12.py` derives the Jellyfin 12 version from the upstream
one as `12.(major*100 + minor).patch.rev` (`0.4.1.1` -> `12.4.1.1`,
`1.2.3.4` -> `12.102.3.4`) and rewrites `build.yaml` (`version`, `targetAbi`,
`framework`) and the csproj `<Version>` in the CI checkout only. The derivation is
mechanical on purpose: a manifest version that disagrees with the assembly version
makes Jellyfin re-offer the same update forever.

Release flow (`.github/workflows/publish.yml`): the shared Jellyfin reusable
workflow builds the `net9.0` package; an inline `build-jf12` job retargets and
builds the `net10.0` package; `upload` attaches both. `mirror-to-public.yml` waits
for **two** zips, classifies each by version, stages it under its own
`dist/<version>/`, and calls `generate-manifest.py` once per stream with the
matching `--target-abi`. `generate-manifest.py` retains 25 versions **per
`targetAbi` stream**, so a busy stream can never evict the other line's only entry.

Simulated against Jellyfin's own selection logic: server `10.11.11` -> `0.4.1.1`
(abi `10.11.11.0`); server `12.0.0` -> `12.4.1.1` (abi `12.0.0.0`).

### Upgrading a server from 10.11 to 12

Jellyfin does not re-evaluate installed plugins on a server upgrade, so the
10.11-line DLL stays on disk and fails to load exactly as described above. After
upgrading the server, update Nebula Bridge from the catalogue once; Jellyfin will
then offer the `12.x` stream. No manual DLL selection is ever required, and users
never see an artifact for the wrong server line.

### Configuration and state

The plugin configuration format is unchanged across both lines. A
`Nebula Bridge.xml` written by the 10.11 build deserializes unchanged on Jellyfin 12
— verified on the isolated Jellyfin 12 test server by cloning the 10.11 config
directory. No configuration migration is required. Acquisition cache state,
retention flags and job records likewise carry over unchanged.

## Hosted observations (2026-09-20)

- The Real-Debrid and multi-provider hosted scenarios recorded in `IMPLEMENTATION_STATUS.md`
  behaved identically on 10.11.11 and 12.0.0, with one 12-only finding: the acquisition replay
  bypass returned an unservable source after a provider was disabled (fixed; see the Phase 6B
  defect list).
- Memory: the Jellyfin 12 process idles at ~1.5 GB in the 3 GB lab VM (10.11: ~2.0 GB) and stayed
  flat (+30 MB) through a full discovery and playback run. During one in-process restart while the
  Proxmox host was memory-constrained it grew to 2.6 GB RSS and was OOM-killed by the guest kernel;
  several other restarts that day were fine. Not attributed to the plugin yet; worth a 4 GB VM or a
  heap profile if it recurs.
- Jellyfin 12 rejects `X-Emby-Token` for API keys; scripts must send
  `Authorization: MediaBrowser Token="…"`.

## Continuous verification

- `.github/workflows/main.yml` and `pr-build.yml` build the `net9.0` package, build
  the retargeted `net10.0` package, and run the test suite against **both** target
  frameworks.
- `.github/workflows/jellyfin-12-compatibility.yml` builds and tests `net10.0`
  against the stable `12.0.0` packages (blocking), plus a separate allowed-to-fail
  job that tracks the newest published `Jellyfin.Controller` prerelease.
