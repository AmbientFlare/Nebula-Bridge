# Nebula Bridge Roadmap

**Status:** Working product/engineering roadmap
**Date:** 2026-09-09
**Companion document:** `Nebula Bridge — Discovery and Capability Audit` / systemic frontier-model review
**Primary implementation baseline reviewed:** Nebula Bridge `0.4.1.1`, .NET 9, Jellyfin `10.11.11`

---

## 1. Purpose

Nebula Bridge is evolving into a **Jellyfin-native remote-content bridge with managed discovery, playback, acquisition, and lifecycle management**.

Its purpose is not to make Jellyfin dependent on Astra, nor to become an Astra-only backend. The core product should remain useful through ordinary Jellyfin clients and APIs.

The intended lifecycle is:

```text
DISCOVER
   ↓
REPRESENT AS NORMAL JELLYFIN CONTENT
   ↓
REMOTE PLAYBACK THROUGH A DEBRID PROVIDER
   ↓
OPTIONALLY KEEP / CACHE LOCALLY
   ↓
ACQUIRE THROUGH A DEBRID PROVIDER
   ↓
ORGANIZE INTO A REAL LOCAL LIBRARY
   ↓
NORMAL JELLYFIN PLAYBACK
   ↓
OPTIONAL LONG-TERM PRUNING
```

Astra is the first client expected to make deep use of Nebula-specific capabilities, but Nebula should preserve normal Jellyfin behavior wherever practical.

---

## 2. Product Principles

### 2.1 Jellyfin-native first

Prefer representing content as ordinary Jellyfin objects:

- Movies
- Series
- Seasons
- Episodes
- MediaSources
- watched/resume/favorite state
- artwork
- media segments
- ordinary libraries

Do not create a parallel client-only content model when the server can represent the same information natively.

### 2.2 Progressive enhancement

A standard Jellyfin client should continue to work normally.

A Nebula-aware client may gain:

- hierarchy prefetch
- richer source selection
- catalog home rows
- availability/status hints
- enhanced discovery
- acquisition controls

Nebula absence must never make basic Jellyfin functionality fail.

### 2.3 Server-side work should provide a concrete advantage

Move work server-side when it reduces:

- client request fan-out
- repeated metadata correlation
- constrained-TV computation
- credential exposure
- repeated remote bandwidth
- duplicate work across clients

Do not create new APIs merely because an operation can be moved server-side.

### 2.4 No local P2P in Nebula

Nebula Bridge must **not join BitTorrent swarms or perform local torrent/P2P acquisition**.

Remote debrid providers may perform torrent acquisition on their infrastructure.

Nebula may then retrieve the completed file over ordinary HTTP/HTTPS.

Potential future fallback to an administrator-configured external BitTorrent client is acceptable, but Nebula itself should not embed a local torrent engine.

### 2.5 Debrid providers are interchangeable infrastructure

TorBox is the first active provider, not the permanent architecture.

The acquisition and playback design must support additional providers such as:

- Real-Debrid
- Premiumize
- future debrid/cache providers

Provider-specific behavior should live behind capability-aware interfaces.

### 2.6 Sonarr/Radarr are optional, not required

Nebula must be capable of acquiring and organizing local files without requiring:

- Sonarr
- Radarr
- RDTClient
- qBittorrent
- Jellyseerr

Existing *arr installations may later be supported as optional integration/post-processing paths.

### 2.7 Do not solve household parenting policy

Nebula should not attempt to become a parental-control product.

In the real deployment model, multiple household members may share one Jellyfin user account. Per-user content filtering therefore does not represent the actual household boundary.

Security and authorization still matter. Content censorship and parental classification are separate concerns.

---

## 3. Current Foundation Already Built

The systemic review found that Nebula already contains substantial working foundations.

### 3.1 Discovery and persistence

Already implemented:

- remote movie/TV search
- `local:` search mode
- `raw:` release search
- materialization of search results into persistent Jellyfin objects
- promotion of useful discoveries into Nebula Bridge — Movies / Shows (2026-09-11: the separate Saved Movies/Shows libraries were folded into these)
- stable managed-library identities

### 3.2 Progressive hierarchy

Already implemented:

- series hydration
- season hydration
- authenticated hydration endpoints
- persisted seasons and episodes
- single-flight behavior
- freshness tracking
- capability discovery

### 3.3 Playback and source handling

Already implemented:

- normal Jellyfin `MediaSources`
- mixed local/remote sources
- source pinning
- deferred source opening
- remote media probing
- native stream proxy
- TorBox/direct resolution
- exact release association
- cached-debrid checks
- file matching

### 3.4 Metadata and catalogs

Already implemented:

- TMDB enrichment
- optional legacy Stremio metadata compatibility
- Trakt catalogs
- Trakt watched-history import
- Next Episodes
- managed catalog libraries
- state-aware catalog pruning
- digital-release filtering
- local-series completion

### 3.5 Supporting features

Already implemented:

- IntroDB media segments
- lazy artwork recovery
- subtitles
- signed indexer definition updates
- Cardigann-compatible public-indexer execution
- FlareSolverr support
- playlist/collection compatibility
- download/delete compatibility
- scheduled maintenance

### 3.6 Capability discovery

Nebula already exposes an authenticated capability document:

```http
GET /nebulabridge/capabilities
```

Current v1 behavior includes:

```json
{
  "apiVersion": 1,
  "features": {
    "hierarchyPrefetch": true,
    "seriesHydration": true,
    "seasonHydration": true,
    "playbackPrefetch": false
  }
}
```

Do not replace this contract. Extend it additively.

---

## 4. Immediate Product Decisions

These decisions supersede uncertainty raised in the systemic review.

### 4.1 Astra integration state

Assume current Astra uses only functionality available to an ordinary Jellyfin client unless current Astra source proves otherwise.

A previous experimental Nebula/Astra integration branch may have been lost during rollback to a stable Astra branch.

Before implementing new Astra integration:

1. inspect current Astra source
2. inventory any existing Nebula-specific calls
3. avoid reconstructing obsolete experimental work from memory
4. then integrate only the currently useful server capabilities

### 4.2 Trakt identity

Desired behavior is **per-Jellyfin-user Trakt association where possible**.

Nebula should not conceptually treat one Trakt identity as the only household identity if Jellyfin/official Trakt configuration can associate separate Trakt accounts with Jellyfin users.

Goals:

- resolve Trakt state for the relevant Jellyfin user
- reuse official Jellyfin Trakt associations when practical
- keep Nebula's own authorization path as a fallback
- avoid accidental cross-user exposure of personal history/watchlists
- do not make multi-account support a prerequisite for unrelated Nebula functionality

This needs repository-level investigation before implementation because the current review found one effective account in the active Nebula path.

### 4.3 Raw search is intentionally unfiltered

`raw:` exists specifically as an advanced search path for release/indexer content that may not fit ordinary metadata catalogs or rating systems.

Product behavior:

- no parental/rating classification layer will be added to raw search
- raw results may contain adult or otherwise unclassified content
- do not claim Jellyfin parental-rating filters apply to raw results
- raw search should remain an advanced capability rather than a headline beginner feature
- administrators should be informed in documentation that raw search is intentionally unfiltered

The current security issue is not that adult results exist. The issue is that search identity/authorization must be correct and documentation must not make false policy guarantees.

### 4.4 Local library effects are intentional, but metadata corruption is not

Nebula is allowed to improve the ordinary Jellyfin TV experience across local and remote content.

Examples:

- reduce redundant home rows
- reorder Next Up
- hide unreleased content
- improve unified presentation

However:

- do not repurpose unrelated Jellyfin metadata fields as hidden Nebula state when avoidable
- do not globally mutate local-library metadata merely as an implementation shortcut
- presentation/query policy and persistent metadata ownership must be clearly separated

The current `EndDate` overloading should be corrected.

### 4.5 Ephemeral discovery must age out

Discovery content is not intended to accumulate forever.

There are multiple lifecycle classes:

#### Catalog-driven discovery

Examples:

- Trending
- Popular
- Anticipated
- Trakt-generated rows

These should be **reconciled**, not blindly destroyed and rebuilt.

Suggested model:

```text
02:00 daily
   ↓
fetch current source catalog
   ↓
compare with existing managed catalog
   ↓
keep unchanged items
add new items
remove stale untouched items
protect meaningful user state
```

Avoid unnecessary metadata/artwork churn.

#### Search-generated discovery

Ordinary search materializations should receive an expiration policy.

Suggested initial range:

- 24–72 hours for untouched ordinary search discoveries

Exact default remains configurable/product-tunable.

#### Raw-search discoveries

Raw releases should remain more aggressively ephemeral.

Existing raw TTL infrastructure can be retained, but purge must protect content that has meaningful retained state.

### 4.6 Meaningful interaction promotes content

An item should stop being disposable when meaningful user state exists.

Candidate promotion signals:

- favorite
- explicit Save / Keep
- meaningful playback progress
- watched status
- episode interaction that promotes its series
- local acquisition in progress/completed

A five-minute playback threshold is a reasonable candidate for "meaningful watch," but should remain a configurable or deliberately chosen policy rather than being accidentally embedded in unrelated code.

---

## 5. Remove or Retire Legacy Baggage

### 5.1 Legacy local MonoTorrent/P2P path

**Decision: retire or make unreachable.**

Nebula should never locally participate in a torrent swarm.

Required work:

- identify all callers
- verify whether any modern path depends on the legacy endpoint
- remove or disable the MonoTorrent path
- remove obsolete P2P configuration where safe
- ensure no supposedly-disabled path remains externally reachable
- preserve modern indexer/debrid behavior

This does **not** remove:

- raw search
- torrent/hash discovery
- cached availability checks
- TorBox
- direct HTTP sources
- future Real-Debrid/Premiumize providers
- remote debrid acquisition

### 5.2 Palco / Anfiteatro compatibility

The Palco/Anfiteatro subsystem is likely inherited legacy compatibility from earlier Gelato-derived code.

Evidence includes:

- legacy `GELATO_` configuration fallbacks (removed 2026-09-11 together with the `gelato://` identity scheme and `/gelato` route aliases)
- `/Palco` APIs
- `anfiteatro-registration` namespace
- SQLite cache
- registration/email functionality
- no current Nebula UI consumer

**Decision:** treat this as legacy baggage until a supported current consumer is proven.

Required work:

1. trace all internal/external callers
2. identify whether anything current depends on it
3. if no required consumer exists:
   - disable/remove public endpoints
   - remove startup dependency on the Palco SQLite database
   - remove unnecessary configuration/code
4. if a real consumer exists:
   - explicitly scope it as compatibility functionality
   - repair authentication/intake/schema problems before release

Do not expand Palco into a new general Nebula API.

### 5.3 Legacy Stremio compatibility

Do not automatically delete every Stremio-derived component.

Separate:

- useful compatibility/fallback behavior
- obsolete Gelato-era assumptions
- legacy local-P2P behavior
- DTOs that should not become the future public API

Retire only paths with no current value or with unacceptable maintenance/security cost.

Stremio *stream addons* are not legacy: since 2026-09-21 the native pipeline asks the configured
addons (Torrentio, Comet…) for a title's hashes ahead of the indexer sweep
(`docs/design/AGGREGATOR_SOURCES.md`). What remains legacy is the catalog-manifest-bound
`cfg.Url` lookup in `NebulaBridgeStremioProvider`.

---

## 6. Release-Critical Correctness and Security

These items precede major public expansion or deep Astra integration.

### 6.1 Fix principal/request identity handling

Confirmed issue: intercepted search may use a bindable `userId` argument before authenticated principal identity.

Required:

- one principal-based identity resolver
- explicit, authorized admin impersonation only where intentionally supported
- regression tests for mismatched requested user vs authenticated user
- ensure short-circuit filters preserve required host authorization semantics

### 6.2 Fix deferred-source target authorization

Confirmed issue: deferred open tokens encode an item ID but do not prove authorized issuance and the target is retrieved without sufficient user scoping.

Required:

- validate target item against authenticated user
- bind request/item/source identity correctly
- reject mismatched/stale/forged source references
- never silently switch to another source when a requested source ID is invalid unless explicitly intended

### 6.3 Preserve source URL/credential isolation

Audit every public/client-facing output:

- PlaybackInfo GET
- PlaybackInfo POST
- deferred-open responses
- MediaSource DTOs
- legacy paths
- error/logging paths

Provider credentials or signed provider URLs should not leak to ordinary clients when a server-side proxy/handle can be used.

### 6.4 Correct local-library release-date handling

Replace current broad `EndDate` mutation strategy.

Requirements:

- clearly identify Nebula-owned data
- avoid changing unrelated local media metadata
- preserve intentional release filtering behavior
- prefer plugin-owned state/query logic where appropriate

### 6.5 Correct global search-policy behavior

Fix configuration precedence so per-user overrides cannot accidentally re-enable globally disabled functionality.

### 6.6 Folder-policy integrity

Avoid restoring stale permission snapshots over later administrator changes.

Add transition tests.

---

## 7. Stabilization and Architecture Work

Do not perform a wholesale rewrite.

### 7.1 Preserve the native source subsystem boundaries

The review found the current native source subsystem generally coherent:

- definitions
- parsing
- templating
- coordinator
- normalization
- aggregator
- debrid abstraction
- file selection
- prepared streams
- proxy transport

Do not refactor merely to hit file-length targets.

### 7.2 Reduce `NebulaBridgeManager` responsibility incrementally

The manager currently owns too many lifecycle concerns.

Potential extraction areas:

- hierarchy persistence
- stream/source lifecycle
- discovery lifecycle
- local acquisition/import
- promotion

Extract only when touching those responsibilities for concrete work.

### 7.3 Centralize hierarchy writes

All hierarchy mutations should share:

- per-series serialization
- cancellation semantics
- consistent persistence ordering
- recovery behavior

### 7.4 Normalize background work

Prefer:

- Jellyfin scheduled tasks
- bounded background queues
- cancellation-aware hosted services

Avoid:

- untracked `Task.Run`
- operations that outlive shutdown/configuration change
- overlapping catalog import/prune jobs

### 7.5 Avoid mutable shared response/cache objects

Cache immutable facts/snapshots instead of response objects that are later sanitized or mutated.

### 7.6 Configuration lifecycle

Centralize:

- validation
- save behavior
- cache invalidation
- runtime reconfiguration
- restart-required semantics

---

## 8. Capability Negotiation

Keep the existing v1 endpoint.

Future capability discovery should distinguish:

1. plugin/package version
2. API envelope version
3. individual capability versions
4. enabled/disabled state
5. dependency availability
6. caller permission

Example conceptual direction:

```json
{
  "apiVersion": 1,
  "features": {
    "hierarchyPrefetch": true,
    "seriesHydration": true,
    "seasonHydration": true,
    "playbackPrefetch": false
  },
  "capabilities": {
    "hierarchy": {
      "versions": [1],
      "status": "available",
      "allowed": true
    },
    "sourceSelection": {
      "versions": [1],
      "status": "available",
      "allowed": true
    },
    "localAcquisition": {
      "versions": [1],
      "status": "configured",
      "allowed": true
    }
  }
}
```

Rules:

- preserve existing v1 fields
- additive evolution where possible
- do not make Astra compare Nebula package versions
- cache capabilities per server **and user**
- 404/absence means normal Jellyfin fallback
- do not add WebSockets or universal RPC infrastructure without measured need

---

## 9. Astra Integration Roadmap

### 9.1 Verify current client first

Before coding:

- inspect current Astra main/release branch
- identify any Nebula-specific calls
- assume the old experimental branch is gone unless found
- document current playback/source behavior

### 9.2 Early Astra opportunities using existing backend

High-value, already-paid-for capabilities:

- capability probe
- bounded series/season hydration
- standard `MediaSources` source picker
- IntroDB via normal Jellyfin media-segment API
- managed catalog libraries
- catalog home descriptors
- Next Up
- local/raw search UX where appropriate

### 9.3 Astra must not require Nebula

Behavior:

```text
Nebula present and capability allowed
    → enhanced path

Nebula absent / old / disabled
    → ordinary Jellyfin path
```

No blocking error banners for missing optional Nebula functionality.

### 9.4 Do not expose admin/provider infrastructure to Astra

Astra should not need:

- TorBox API keys
- Real-Debrid API keys
- indexer credentials
- filesystem paths
- provider administration
- raw native diagnostic results

Those remain server-side.

---

## 10. Multi-Debrid Provider Architecture

### 10.1 Goal

Nebula should support multiple remote providers for:

- cached availability
- stream resolution
- remote acquisition
- completed-file retrieval

### 10.2 Provider abstraction

Do not create TorBox-specific acquisition architecture.

Conceptual interface:

```text
IDebridProvider
    ProviderId
    DisplayName
    IsConfigured()

    GetCapabilities()
    CheckAvailability(release)
    ResolveStream(release, file)
    AddRelease(release)
    GetJobStatus(job)
    GetFiles(job)
    GetDownloadUrl(job, file)
    DeleteRemoteJob(job)
```

Possible capability flags:

```text
CachedAvailability
MagnetSubmission
TorrentSubmission
RemoteAcquisition
DirectDownload
FileSelection
JobDeletion
InstantAvailability
```

Providers may not implement every operation.

### 10.3 Provider selection policy

Initial policy can remain simple.

Example:

```text
exact selected release
      ↓
check configured providers
      ↓
preferred provider cached?
      ├─ yes → use
      └─ no
          ↓
another provider cached?
      ├─ yes → use according to policy
      └─ no
          ↓
optional remote acquisition
```

Do not overbuild scoring until multiple providers exist.

### 10.4 Implementation order

1. stabilize/canonicalize current TorBox implementation
2. define provider contract from actual TorBox needs
3. add provider contract tests
4. add one additional verified provider
5. refine abstraction only when the second implementation demonstrates real differences

Avoid designing a fictional universal provider interface before a second provider is implemented.

---

## 11. Local Acquisition: Remote Content → Real Files

This is a major future Nebula capability.

### 11.1 Goal

Allow a remote Nebula item or exact remote release to become ordinary local Jellyfin media.

Primary motivations:

- avoid repeatedly streaming popular content through debrid
- reduce external bandwidth usage
- make popular content available to all normal Jellyfin users
- turn successful discovery into permanent/local library content
- preserve exact release already selected during playback/discovery

### 11.2 Core workflow

```text
Nebula item / exact release
      ↓
Keep Locally / Cache Locally
      ↓
Acquisition Coordinator
      ↓
select debrid provider
      ↓
cached?
  ├─ yes → obtain completed-file URL
  └─ no  → optionally ask provider to acquire remotely
      ↓
download via HTTP/HTTPS to staging
      ↓
verify completion
      ↓
identify media using known Nebula metadata
      ↓
organize into configured local library
      ↓
refresh Jellyfin
      ↓
ordinary local Jellyfin playback
```

Nebula never joins the torrent swarm.

### 11.3 Download ownership

Nebula should own its own HTTP download lifecycle.

Required concerns:

- temporary `.partial` files
- resumability where provider supports Range requests
- cancellation
- retry/backoff
- disk-space validation
- provider URL expiration/refresh
- checksum or size verification where available
- safe atomic finalization
- crash/restart recovery
- bounded concurrent downloads
- cleanup of abandoned staging jobs
- progress reporting through Jellyfin tasks/admin UI

Do not reuse the legacy MonoTorrent implementation.

### 11.4 Preserve exact release identity

For "Keep Locally" from an active remote item, prefer the exact release/file already selected.

Store/reuse durable identifiers such as:

- release hash
- source identity
- provider-independent release metadata
- filename
- file index/identity
- movie/series identity
- season/episode identity

Do **not** store an expiring provider download URL as the durable identity.

### 11.5 Local media organizer

Jellyfin is good at identifying media when files are placed into predictable folders with useful names. Nebula should therefore perform a small, deterministic organization step.

Conceptual movie layout:

```text
Movies/
  Movie Name (2026) [tmdbid-12345]/
    Movie Name (2026) [tmdbid-12345].mkv
```

Conceptual TV layout:

```text
Shows/
  Show Name (2026) [tvdbid-12345]/
    Season 01/
      Show Name S01E03 - Episode Name.mkv
```

Nebula already knows much of this identity before acquisition.

Requirements:

- configurable local movie root
- configurable local series root
- safe filename/path normalization
- collision handling
- multi-episode handling
- extras/samples rejection
- multi-file torrent selection
- preserve or generate metadata-provider IDs in folder/file naming where useful
- trigger Jellyfin refresh after successful import
- transition/remove the corresponding virtual item without losing user state

### 11.6 Local item state transfer

When remote content becomes local:

- preserve watched state
- preserve resume state
- preserve favorite state
- preserve relevant provider IDs
- preserve series hierarchy
- avoid duplicate visible copies where practical
- local source should become preferred for normal playback

The transition should feel like the item became local, not like the user received a second unrelated copy.

---

## 12. Optional *arr Integration

Nebula must not require Sonarr/Radarr.

However, optional integration may be useful for users who already run them.

Possible optional modes:

### Native Nebula organizer — default

```text
debrid → Nebula staging → Nebula organizes → Jellyfin
```

### *arr post-processing — optional

```text
debrid → Nebula staging → Sonarr/Radarr import → Jellyfin
```

Potential advantages of optional *arr integration:

- established rename policies
- quality profiles
- monitoring future episodes
- collection management
- existing user workflows

Potential future "Keep Series" behavior:

- acquire current exact episode through Nebula
- optionally ensure show exists in Sonarr
- optionally enable monitoring for future episodes
- let existing user's Sonarr/torrent-client/VPN workflow handle future releases

Do not make this part of the first local-acquisition implementation.

---

## 13. Future External Torrent-Client Fallback

Nebula itself will not do local P2P.

A future administrator-configured fallback may send a release to an **external** download client if no debrid provider can satisfy it.

Conceptual:

```text
Nebula release
    ↓
debrid providers unavailable/failed
    ↓
optional external-download fallback enabled?
    ├─ no → report unavailable
    └─ yes
         ↓
      qBittorrent/other client API
         ↓
      user's VPN/network policy
         ↓
      completed file
         ↓
      Nebula or *arr import
```

This is later work.

Do not couple initial Nebula local acquisition to qBittorrent.

---

## 14. Discovery and Storage Lifecycle

### 14.1 Catalog reconciliation

Daily reconciliation should:

- pull newest Trakt/other catalog membership
- keep unchanged records
- add new records
- remove stale disposable records
- protect items with meaningful user state
- distinguish provider failure from a legitimate empty catalog

Avoid purge/recreate churn.

### 14.2 Ordinary search result retention

Implement an age sweep for materialized ordinary search discoveries.

Protect:

- favorites
- meaningful playback/resume state
- saved/promoted items
- active acquisition
- local copies

### 14.3 Raw result retention

Keep raw results short-lived unless explicitly retained.

Fix existing behavior so age purge does not blindly remove:

- promoted items
- favorited items
- active playback
- meaningful resume state
- local-acquisition jobs

### 14.4 Artwork and metadata cleanup

When ephemeral items are removed, also clean owned:

- image sidecars
- cached artwork
- stale remote-source records
- unnecessary metadata files
- expired proxy registrations

The goal is not merely database cleanliness; it is reducing unnecessary long-term RAM/disk/cache pressure.

### 14.5 Local media pruning — later

Once local acquisition exists, add a separate **local retention** concept.

Possible future signals:

- never watched by anyone for N months
- manually marked Keep
- administrator pinned
- recently watched
- currently monitored by Sonarr/Radarr
- storage pressure

Do not automatically delete real local media in the first acquisition release.

Start with reporting/suggestions or explicit administrator actions.

---

## 15. Home Screen and Unified Presentation

The purpose of global presentation changes is to make TV browsing simpler.

Desired outcome:

- fewer redundant rows
- less overlap between local and remote discovery
- useful Next Up ordering
- remote catalogs integrated cleanly
- local media remains first-class

Nebula may intentionally influence ordinary local-library presentation.

However:

- behavior must be explicitly scoped
- global decorators must forward untouched calls when Nebula adds no value
- do not mutate unrelated metadata merely to achieve UI filtering
- measure home-screen request fan-out before building custom batching APIs

---

## 16. Trakt Roadmap

### Phase A — clarify current behavior

- trace official Trakt plugin reflection bridge
- determine exact current user/account mapping
- test multiple Jellyfin users
- test direct Nebula Trakt auth vs inherited official plugin auth
- document shared-library exposure

### Phase B — correct ownership

Goal:

```text
Jellyfin User A → Trakt Account A
Jellyfin User B → Trakt Account B
```

where configuration supports it.

Avoid exposing one user's personal:

- watchlist
- history
- recommendations
- Next Episodes

to unrelated users unless the administrator intentionally shares that library.

### Phase C — preserve server-side advantage

Continue doing history correlation and Jellyfin user-data import server-side.

Do not push complete Trakt-history joins onto Astra.

---

## 17. Testing Strategy

The review found 184 reported .NET tests passing plus 10 registry tests, but no full reproducible hosted Jellyfin suite.

### 17.1 First priority: boundary regression tests

Add tests for:

- authenticated principal vs request `userId`
- deferred item/source authorization
- stale/mismatched MediaSource IDs
- public output URL redaction
- folder-policy behavior
- global DisableSearch precedence
- local-library ownership
- raw lifecycle protection

If Palco is removed, do not spend extensive effort hardening code that is being retired.

### 17.2 Disposable Jellyfin integration fixture

Create one small real-host fixture with:

- supported Jellyfin version
- two users
- different folder/access policies
- one local movie
- one partial local series
- fake metadata/indexer/debrid HTTP endpoints

Cover:

```text
search
→ materialize
→ hydrate
→ PlaybackInfo
→ source selection
→ stream
→ watched/resume promotion
→ restart
```

### 17.3 Acquisition tests

When local acquisition is added:

- cached debrid item
- uncached remote acquisition
- provider failure
- URL expiration and refresh
- interrupted download/resume
- insufficient disk space
- duplicate acquisition
- simultaneous requests for same media
- multi-file release
- episode file selection
- movie file selection
- crash/restart recovery
- final move/rename
- Jellyfin refresh
- state transfer from virtual to local

### 17.4 Provider contract fixtures

Every debrid provider must have deterministic fixtures for:

- success
- not cached
- acquisition pending
- acquisition failed
- malformed response
- auth failure
- rate limiting
- multi-file result
- URL expiration
- deletion where supported

### 17.5 Release gate

Before public release:

- clean Release build
- package dependency verification
- install on fresh supported Jellyfin
- restart
- configuration round-trip
- restricted-user test
- discovery/hydration test
- real playback smoke test
- disabled/uninstall behavior
- documented supported Jellyfin versions

---

## 18. Performance Work

Optimize measured hot paths after correctness.

Priority candidates from the review:

### High

- remove synchronous network resolution from media-source lookup
- keep expensive source work async/deferred

### Medium-high

- fetch only requested season metadata instead of full series tree
- avoid whole-library Next Up fetch/sort where possible

### Medium

- paginate before expensive metadata DTO work
- single-flight identical raw searches
- bound source/proxy registries
- add explicit size budgets to high-cardinality caches
- reduce repeated retention scans
- avoid repeated enrichment of immutable release data
- clean image sidecars with item lifecycle

Do not add broad caching without measured need.

---

## 19. Documentation Roadmap

Before formal public release, document:

### What Nebula is

A Jellyfin-native remote-content bridge that:

- discovers remote content
- represents it as normal Jellyfin content
- resolves remote sources
- preserves user state
- can progressively materialize hierarchy
- can eventually promote remote media into local files

### What it is not

- not an Astra dependency
- not an Astra-only backend
- not a local torrent client
- not a parental-control product
- not a replacement for Sonarr/Radarr
- not a promise to support every Cardigann definition

### Advanced/raw search

Explicitly state:

- `raw:` is advanced
- it searches release/indexer data
- it may contain adult/unclassified content
- ordinary Jellyfin rating metadata may not exist
- server administrators decide who receives Nebula access

### Client API

Publish supported client subset:

- capabilities
- hierarchy hydration
- catalog descriptors
- any future item-scoped acquisition/status endpoints

Mark clearly:

- client APIs
- admin APIs
- internal/loopback APIs
- legacy compatibility APIs

### Compatibility

Publish exact tested Jellyfin versions.

Do not imply "10.11.x" or future-major compatibility solely from compilation.

---

## 20. Public Release Blockers

The following should be addressed before calling Nebula broadly release-ready.

### Required

- [ ] principal/request identity boundary fixed
- [ ] deferred-source target authorization fixed
- [ ] source URL/credential exits audited
- [ ] local-library `EndDate`/release mutation corrected
- [ ] legacy local P2P endpoint retired/unreachable
- [ ] Palco/Anfiteatro retired or explicitly justified and repaired
- [ ] global search-policy precedence corrected
- [ ] fresh packaged install tested
- [ ] restricted-user behavior tested
- [ ] discovery → playback smoke test passes
- [ ] exact supported Jellyfin versions documented

### Strongly recommended

- [ ] hierarchy writers share locking/serialization
- [ ] manual catalog imports serialized
- [x] background work cancellation normalized (2026-09-20, `BackgroundWork`; see IMPLEMENTATION_STATUS)
- [ ] provider image response gaps fixed or scoped
- [ ] source/probe cache ownership corrected
- [ ] ordinary discovery age sweep implemented
- [ ] raw purge protects retained state
- [ ] capability API documented
- [ ] live tests report skipped rather than false-green
- [ ] stale folder-policy restoration corrected

---

## 21. Proposed Development Phases

### Phase 0 — Preserve the Map

**Goal:** keep the frontier audit as evidence and avoid losing architectural decisions.

- [ ] store systemic review in repository docs/reference area
- [ ] store this roadmap beside it
- [ ] do not overwrite the systemic review with implementation notes
- [ ] reference review findings from future issue/task prompts

---

### Phase 1 — Release Boundary Repair

**Goal:** make existing functionality safe to build on.

- [ ] add failing tests for principal mismatch
- [ ] fix request identity
- [ ] add failing tests for deferred target/source mismatch
- [ ] fix deferred authorization
- [ ] audit source URL redaction
- [ ] correct global DisableSearch logic
- [ ] correct local release-date ownership
- [ ] decide and remove/disable legacy MonoTorrent path
- [ ] trace Palco/Anfiteatro callers
- [ ] remove/disable Palco if unused
- [ ] add fresh Jellyfin integration fixture

**Do not add new features during this phase unless needed to complete a fix.**

---

### Phase 2 — Lifecycle Correctness

**Goal:** make discovery reliably self-cleaning without losing meaningful state.

- [x] define ordinary discovery TTL
- [ ] reconcile catalogs daily
- [x] preserve favorite/resume/watched/saved state
- [x] protect raw items during active/meaningful use
- [x] correct raw promotion vs raw purge conflict
- [ ] clean owned image/metadata/cache artifacts
- [x] serialize catalog reconciliation
- [x] distinguish failed/incomplete snapshots from authoritative empty catalogs

---

### Phase 3 — Stabilize Existing High-Value Capabilities

**Goal:** make current features dependable before extending them.

- [ ] shared hierarchy write locking
- [x] hydration persistence ordering/recovery
- [ ] async/deferred source resolution
- [x] immutable probe/source snapshots
- [x] bounded proxy/source registries
- [ ] fix/narrow image-provider response paths
- [x] normalize background task ownership (2026-09-20)
- [x] normalize configuration lifecycle
- [ ] selected-season metadata fetch optimization where justified

---

### Phase 4 — Formalize Client Capability Contract

**Goal:** make optional client integration maintainable.

- [x] preserve `/capabilities` v1
- [x] add capability versions
- [x] add caller-specific availability/status
- [x] add caller-specific allowed state
- [x] publish supported client API
- [x] add compatibility tests
- [x] keep provider/admin details server-side

---

### Phase 5 — Astra Integration

**Goal:** consume already-built server advantages.

**Current Astra audit:** no Nebula-specific playback changes are required. Astra already uses
standard Jellyfin `PlaybackInfo` and `MediaSources`; Phase 5 adds capability negotiation and
selective prefetch/UI enhancements around that existing path. Do not rebuild source selection or
playback mechanics unless standard Jellyfin DTOs prove insufficient for a specific enhancement.

- [x] audit current Astra source
- [x] confirm existing Nebula integration, if any (none on the stable branch)
- [x] add a lightweight per-server/user capability probe with a short cache; 404, unavailable,
      or disabled responses silently retain ordinary Jellyfin behavior
- [x] add bounded series/season hydration only when the capability advertises it
- [x] retain the existing standard MediaSources source picker and playback path
- [x] consume standard Jellyfin media segments for IntroDB/Skip Intro
- [x] consume accessible managed catalog/home descriptors where useful (ordinary user views/home rows)
- [x] consume Next Up normally
- [x] expose Nebula-specific source/status UX only where standard Jellyfin DTOs lack needed information
- [x] preserve full fallback to ordinary Jellyfin

---

### Phase 6 — Multi-Debrid Foundation

**Goal:** make TorBox one provider, not the architecture.

- [x] define minimal provider capability interface from current TorBox behavior
- [x] move/canonicalize TorBox behind it
- [x] add deterministic provider tests
- [x] review excluded multi-debrid candidate for reusable concepts; retain it excluded until each API flow is verified
- [x] implement one additional provider (Real-Debrid, account-scoped; hosted validation 2026-09-20)
- [x] revise abstraction based on real differences (provider-neutral file identity, torrent-scoped
  vs provider-scoped failure classification, archive-bundle and blocked-file reasons)

---

### Phase 7 — Native Local Acquisition

**Goal:** allow Nebula remote content to become real local Jellyfin media.

#### 7A — Acquisition engine

- [x] acquisition job model
- [x] staging directory
- [x] bounded downloader
- [x] progress/status
- [x] cancellation (2026-09-20: owned completion runs, `POST acquisition-cache/{id}/cancel`, shutdown stop + recovery)
- [x] retry/backoff
- [x] URL refresh
- [x] disk-space checks
- [x] restart recovery
- [x] remote-job cleanup

#### 7B — Media organization

- [ ] classify movie vs episode
- [ ] preserve external IDs
- [ ] deterministic movie naming
- [ ] deterministic series/season/episode naming
- [ ] multi-file release selection
- [ ] collision behavior
- [ ] atomic move into configured roots
- [ ] Jellyfin refresh

#### 7C — State transition

- [ ] transfer watched/resume/favorite state
- [ ] merge/replace virtual representation
- [ ] prefer local MediaSource
- [ ] prevent duplicate visible entries
- [ ] preserve series hierarchy

#### 7D — User-facing operation

Potential initial operation:

```text
Keep Locally
```

Future variants may include:

```text
Keep Movie
Keep Episode
Keep Series
Cache Locally
```

Do not overbuild UI semantics until the backend transition is proven.

---

### Phase 8 — Optional Ecosystem Integrations

Only after native local acquisition works.

Possible:

- [ ] Sonarr import adapter
- [ ] Radarr import adapter
- [ ] Jellyseerr-aware status
- [ ] external qBittorrent fallback
- [ ] Sonarr "monitor future episodes" option after Keep Series
- [ ] alternative download-client adapters

These must remain optional.

---

### Phase 9 — Intelligent Local Caching and Retention

Later optimization/product work.

Possible triggers:

- manual Keep
- administrator watched > threshold
- repeated remote plays by multiple users
- popular-series policy
- explicitly pinned series
- storage-pressure policy

Possible cleanup:

- report large local media unused for N months
- administrator review queue
- optional automatic policy only after confidence/testing

Do not introduce automatic deletion of real media casually.

---

## 22. Explicit Non-Goals

For the foreseeable roadmap, do **not** spend time on:

- making Nebula a local BitTorrent client
- building parental classification for raw search
- forcing every household member to have a separate Jellyfin account
- requiring Sonarr/Radarr
- requiring RDTClient
- replacing Jellyfin's entire API with Nebula endpoints
- creating a universal RPC framework
- adding WebSockets merely for architectural purity
- rewriting the whole plugin into microservices
- refactoring solely because a file is large
- implementing every debrid provider simultaneously
- implementing every Cardigann feature
- supporting future Jellyfin majors before current release boundaries are stable
- making every internal service a public client API
- turning Astra into the only supported client

---

## 23. Recommended Near-Term Order

If work resumes after this planning session:

1. **Keep the systemic review and this roadmap together.**
2. **Do not begin with new features.**
3. Add targeted failing tests for the confirmed security/correctness boundaries.
4. Fix request identity and deferred target authorization.
5. Retire local MonoTorrent/P2P.
6. Trace and likely retire Palco/Anfiteatro.
7. Correct local-library release-date mutation.
8. Implement ordinary discovery aging/reconciliation.
9. Add the small hosted Jellyfin fixture.
10. Stabilize hierarchy/background/source ownership.
11. Formalize capability negotiation.
12. Audit current Astra and integrate existing paid-for capabilities.
13. Build the multi-debrid provider foundation.
14. Build native debrid → local-file acquisition.
15. Add optional ecosystem integrations only after native acquisition is reliable.

---

## 24. Product North Star

Nebula Bridge should make remote content feel progressively more native to Jellyfin.

A user should be able to:

```text
find something
→ see it like normal Jellyfin media
→ play it remotely
→ continue watching normally
→ decide it is worth keeping
→ acquire it once through a remote debrid provider
→ turn it into an ordinary local library item
→ let every normal Jellyfin client use the local copy
```

The server should manage the complexity:

- provider credentials
- source discovery
- debrid availability
- exact release identity
- metadata correlation
- lifecycle state
- file acquisition
- local organization
- cleanup

Astra and other clients should consume the result through normal Jellyfin behavior wherever possible, using Nebula-specific APIs only where they provide a clear enhancement.

That is the direction that turns Nebula Bridge from a collection of powerful experiments into a coherent Jellyfin platform component.
