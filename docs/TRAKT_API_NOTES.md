# Trakt API integration notes

Last reviewed: 2026-08-23

These notes capture the Trakt requirements that affect Nebula Bridge. They are based on Trakt's current [getting-started guide](https://docs.trakt.tv/docs/getting-started), public guide, and endpoint reference. No client secret, access token, or refresh token belongs in this repository.

## Application registration

- Application name: `Nebula Bridge`.
- Registered redirect URI: `urn:ietf:wg:oauth:2.0:oob`.
- JavaScript CORS origins are blank because Jellyfin's server performs all Trakt requests; browser JavaScript never calls Trakt directly.
- Runtime settings require an application client ID, application client secret, and the exact registered redirect URI.
- Prefer `NEBULA_BRIDGE_TRAKT_CLIENT_ID`, `NEBULA_BRIDGE_TRAKT_CLIENT_SECRET`, and `NEBULA_BRIDGE_TRAKT_REDIRECT_URI` through container secret/environment injection. The elevated plugin page is the fallback.

## Hosts and required headers

- General API calls always use TLS at `https://api.trakt.tv`.
- OAuth and device authorization use `https://auth.trakt.tv`.
- API calls send `Content-Type: application/json`, a versioned `User-Agent`, `trakt-api-key: <client_id>`, and `trakt-api-version: 2`.
- Authenticated calls additionally send `Authorization: Bearer <access_token>`.

References: [API URL](https://docs.trakt.tv/docs/api-url), [required headers](https://docs.trakt.tv/docs/required-headers).

## Device authorization flow

Nebula Bridge uses the media-center/device flow, not the browser redirect flow.

1. `POST https://auth.trakt.tv/oauth/device/code` with `client_id`.
2. Display the returned `user_code` and `verification_url`. The UI also renders a server-generated QR code whose activation URL includes `?code=<user_code>` for best-effort prefill. Trakt officially documents the activation page but not the query parameter, so the visible code remains the compatibility fallback.
3. Poll `POST https://auth.trakt.tv/oauth/device/token` with `code` (the returned `device_code`), `client_id`, and `client_secret`.
4. Respect the returned polling `interval` and stop after `expires_in`.
5. Handle statuses: `200` success, `400` pending, `404` invalid code, `409` already used, `410` expired, `418` denied, and `429` polling too quickly.
6. On success, persist the access token, refresh token, creation time, and lifetime, then retrieve `/users/settings` to identify the connected account.

References: [authentication overview](https://docs.trakt.tv/reference/auth), [generate device codes](https://docs.trakt.tv/reference/postoauthdevicecode), [poll for an access token](https://docs.trakt.tv/reference/postoauthdevicetoken).

## Token refresh

- Access tokens currently last seven days; use the response's timestamps instead of hard-coding that lifetime.
- Refresh through `POST https://auth.trakt.tv/oauth/token` with `refresh_token`, `client_id`, `client_secret`, the exact registered `redirect_uri`, and `grant_type=refresh_token`.
- Refresh tokens are single-use. A successful response must atomically replace both tokens before another refresh can start.
- A legacy refresh token can return `invalid_grant`/`session not found`; in that case the user must complete device authorization once more.
- The plugin serializes refresh attempts with a lock and persists both replacement tokens together.

Reference: [exchange/refresh a token](https://docs.trakt.tv/reference/postoauthtoken).

## Jellyfin Trakt plugin inheritance

- Nebula Bridge first looks for an authorized account in Jellyfin's official Trakt plugin. When present, it uses that plugin's access token together with the matching application identity that issued it. Mixing an inherited bearer token with Nebula Bridge's application client ID is intentionally avoided.
- Discovery is server-side and optional: the official plugin is loaded through a reflection bridge so Nebula Bridge does not acquire a hard binary dependency on it.
- The first authorized Jellyfin-linked Trakt account, ordered deterministically by Jellyfin user ID, is used for the server-wide catalog importer. This matches Nebula Bridge's current global catalog model.
- Refresh replacements are written back to the official Trakt plugin's configuration. Tokens are not copied to browser JavaScript or duplicated in Nebula Bridge's configuration.
- Disconnecting Nebula Bridge never deauthorizes an inherited Jellyfin Trakt account. That account remains managed from Jellyfin's Trakt configuration page.
- If the official plugin is absent, Nebula Bridge keeps its independent application-credential and QR device flow as a fallback.
- The Nebula Bridge configuration page can install the official Trakt package through Jellyfin's authenticated package API after disclosing the source and restart requirement. This is a setup action, not a silent startup install.

## Pagination, limits, and caching

- Paginated methods accept `page` and `limit`; default page size is generally 10. Pagination response headers include current page, applied limit, page count, and item count.
- Nebula Bridge requests pages of 50 and stops when the catalog limit is satisfied or Trakt returns an empty page.
- Current documented GET limits are 500 calls per five minutes for both authenticated users and unauthenticated applications. Authenticated writes are limited to one call per second.
- On `429`, read `Retry-After`, wait at least that long, and avoid immediate loops. Cache and deduplicate requests where possible.
- `extended=full` returns extended metadata and images. Trakt-hosted images must be cached and must not be hotlinked. Nebula Bridge currently relies on Jellyfin's metadata providers and external IDs for artwork instead of hotlinking Trakt images.

References: [pagination](https://docs.trakt.tv/docs/pagination), [rate limits](https://docs.trakt.tv/docs/rate-limiting), [image rules](https://docs.trakt.tv/docs/images).

### Account collection limits

- HTTP `420` is Trakt's account-limit response, distinct from rate-limit HTTP `429`.
- The official Jellyfin Trakt plugin currently logs a failed queued batch when a free-tier collection is already at its item cap; this behavior is tracked upstream in [jellyfin-plugin-trakt issue #289](https://github.com/jellyfin/jellyfin-plugin-trakt/issues/289).
- Nebula Bridge does not retry or bypass that account limit. Its internal alternate source rows are virtual, and playback probing does not write item metadata or create official-plugin queue events.

## Catalog mapping

Public catalogs:

- Movies: trending, popular, anticipated.
- TV shows: trending, popular, anticipated.

Connected-account catalogs:

- Watchlist, collection, recommendations, history, ratings, and in-progress playback for movies and TV shows.
- TV catalog items are expanded through `/shows/{id}/seasons?extended=full,episodes` so Jellyfin receives series, seasons, and episodes without a legacy Stremio metadata dependency.

The catalog client maps Trakt, IMDb, TMDB, and TVDB identifiers into Jellyfin provider IDs. Public calls require the application client ID; account catalogs additionally require a valid bearer token.

## Secret-handling rules

- Never commit or document actual application secrets or OAuth tokens.
- Never put secrets in URLs or logs.
- Use a dedicated HTTP client without default request logging for authorization headers and token payloads.
- The normal user interface is code-first. Application values remain in an advanced elevated-admin section.
- Disconnect clears access and refresh tokens but retains app registration unless the administrator explicitly clears it.
