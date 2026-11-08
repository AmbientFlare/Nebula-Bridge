# Debrid providers

Nebula Bridge resolves remote playback through one or more debrid accounts. As of
Phase 6B two providers are implemented and tested: **TorBox** and **Real-Debrid**.
No other provider is available; the settings page only lists providers that are
compiled into the plugin.

## The rule that never changes

Pressing Play never starts a download on a provider. Nebula only offers a release
when a provider already holds it, and it only ever serves the exact file that was
chosen when the release was ranked. Everything below (priority, duplicate
collapsing, failover) is a choice of *transport*, never a choice of *bytes*.

## Setting up providers

Open **Dashboard → Plugins → Nebula Bridge → Native Sources → Debrid providers**.
Each provider has its own card:

| Control | Meaning |
| --- | --- |
| **Enabled** | Whether Nebula may call this provider at all. Turn one off to test the other, or to take a misbehaving account out of rotation without deleting its token. |
| **Priority** | 1–100, lower is preferred. When two providers both hold the same file, the lowest number serves it. |
| **Status** | `No token`, `Disabled`, or the live health state (`Healthy`, `Temporarily unavailable`, `Rate limited`, `Auth failed`, `Premium unavailable`) with the last failure reason. |
| **Test** | Calls the provider's account endpoint with the stored token and reports the health result. Never displays the token. |
| **API token** | Write-only. Paste once and save; the page only ever shows "Token stored". Clearing the field removes the token. |

Tokens can also be injected as environment variables, which take precedence over
the stored value; the card then says the saved value is ignored:

| Provider | Environment variable | Where to get the token |
| --- | --- | --- |
| TorBox | `NEBULA_BRIDGE_TORBOX_API_TOKEN` | torbox.app → Settings → API |
| Real-Debrid | `NEBULA_BRIDGE_REALDEBRID_API_TOKEN` | real-debrid.com → My Account → API token (a **premium** account is required) |

Tokens are never written to the plugin's ordinary configuration JSON, never
returned by any API, never included in acquisition jobs, and are redacted from
the dedicated log together with `Authorization` headers and signed download URLs.

## How a release becomes a stream

1. The indexer search produces release candidates with infohashes.
2. The orchestrator asks every **enabled, configured, healthy** provider whether it
   holds each infohash. Queries run concurrently with a per-provider timeout; a
   provider that fails, times out, or is rate limited never hides another
   provider's answer.
3. Each release is normalised to one entry in the source list. Providers are not
   shown as separate rows; the provider is a diagnostic detail, not a choice the
   viewer makes.
4. Release ranking chooses **which release** to play (cached count, seeders, size).
5. For the chosen release, the highest-priority healthy provider that holds the
   exact file becomes the transport. Other providers holding the *same* file
   (same infohash, same path, same length) are kept as alternates. A provider
   that holds the torrent but reports a different file is not an alternate.
6. The stream is served through Nebula's proxy. The proxy key is provider-neutral,
   so a later refresh can switch provider without changing the client URL.

### Availability answers

Every provider query is normalised to one of: **Cached**, **Not cached**,
**Unknown**, **Provider error**, **Rate limited**. Definitive answers are cached
briefly so scrolling a library does not hammer the provider; error and
rate-limited answers are not cached as "not cached".

### Failover

- **Before playback** — if the preferred provider fails to produce a URL, each
  alternate is tried in priority order. An alternate is used only when the file
  it returns has the same content identity as the pinned file. Different bytes
  are refused and the attempt is logged as `different_file`.
- **During playback** — signed URLs expire. When the proxy refreshes a stream it
  re-resolves the pinned file, first on the current provider and then on the
  alternates, again under the same identity check. The proxy key and the expected
  length never change.
- **Not supported** — switching providers mid-download inside an acquisition job.
  A job that is downloading stays with the provider that produced its stream
  until the next refresh boundary; cross-provider recovery is only applied when a
  new URL is being obtained.

### Health and back-off

Failures are classified into a health state per provider and the provider is
skipped for a bounded window:

| State | Trigger | Back-off |
| --- | --- | --- |
| Temporarily unavailable | 5xx, timeout, unexpected API error | 30 s doubling to 10 min |
| Rate limited | 429 / provider "slow down" codes | 60 s doubling to 5 min |
| Auth failed | 401 / bad token | 5 min |
| Premium unavailable | account not premium, locked, fair-use limited | 15 min |

Nothing is permanently disabled by a transient failure, and one unhealthy provider
never blocks the other. "Not cached" and "wrong file" answers are not health
failures.

## Capability differences

| Capability | TorBox | Real-Debrid |
| --- | --- | --- |
| Cached availability | Global cache check by infohash | **Account-scoped**: only torrents already in *your* account and fully downloaded count as cached |
| Magnet submission | Yes (used only for explicit acquisition, never on Play) | Not exposed — Real-Debrid disabled its non-mutating instant-availability endpoint, and adding a magnet on their side always starts a download |
| File selection | Yes | Yes (among the files you selected when adding the torrent) |
| Direct stream URL | Yes | Yes, via `unrestrict/link`; links are per-account and expire |
| Completed-file source | Yes | Yes |

The practical consequence for Real-Debrid: a release shows as playable only if
you have already added that torrent to your Real-Debrid account (through their
website or any other client) and it finished downloading there. Nebula does not
add torrents to Real-Debrid on your behalf.

## Jobs and imported media

Acquisition jobs identify their content by infohash + file path + length, not by
provider. A job created while TorBox served the file, later refreshed from
Real-Debrid, is still the same job; alternates are stored alongside the owning
handle. Disabling a provider does not invalidate media that was already imported,
and jobs written by earlier releases (TorBox-only handles) continue to load and
are upgraded in place the first time the same file is resolved again.

## When a provider is down

- Search still returns releases held by the remaining providers.
- Releases held only by the unavailable provider are not offered.
- Playback in progress continues until its URL expires; the refresh then tries the
  alternates.
- The Status column and the dedicated log (`debrid-providers`,
  `debrid-availability`, `debrid-cache-check`, `debrid-route-selected`,
  `debrid-failover`, `debrid-health`) show what happened. No event ever contains a token,
  refresh token, `Authorization` header, or signed URL.
