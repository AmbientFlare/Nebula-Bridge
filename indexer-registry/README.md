# Nebula Bridge indexer catalog

This Flask service synchronizes Cardigann v11 definitions from
`Prowlarr/Indexers`, records compatibility metadata, and distributes only the
definitions explicitly published by an administrator. It never performs torrent
searches; searches execute locally in the Jellyfin add-on.

## Setup

1. Copy `.env.example` to `.env` and replace the administrator password.
2. Generate an ECDSA P-256 private key outside source control:

   ```sh
   mkdir -p secrets data
   openssl ecparam -name prime256v1 -genkey -noout \
     -out secrets/manifest-signing-key.pem
   openssl rand -base64 -out secrets/admin-password 36
   chmod 600 secrets/manifest-signing-key.pem
   chmod 600 secrets/admin-password
   ```

3. Start and synchronize the service:

   ```sh
   docker compose up -d --build
   docker compose exec -T indexer-registry python app.py sync-upstream
   ```

Gunicorn and the Docker host port listen on `0.0.0.0:5050`. Put the public
client API behind HTTPS. Open `/manage` with the Basic-auth credentials from
`.env` to synchronize, inspect compatibility, and publish definitions.

## API

- `GET /healthz` — health and non-sensitive catalog status
- `GET /api/v1/indexers/manifest` — signed manifest of published definitions
- `GET /api/v1/indexers/{id}` — exact published YAML definition
- `GET /api/v1/admin/indexers` — authenticated administrative catalog
- `PATCH /api/v1/admin/indexers/{id}` — authenticated publication change
- `POST /api/v1/admin/sync` — authenticated manual upstream sync
- `POST /api/v1/admin/indexers/upload` — authenticated custom v11 upload

The manifest signature is returned in `X-Nebula-Signature` with algorithm
`ecdsa-p256-sha256`. Clients embed only the corresponding public key and verify
the raw response bytes before parsing the manifest.

## Scheduling

The supplied `nebula-indexer-registry-sync.service` calls the same sync code path
as the admin button. Install it and `nebula-indexer-registry-sync.timer` under
`/etc/systemd/system`, then enable the timer:

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now nebula-indexer-registry-sync.timer
```

The timer runs weekly with a randomized delay and catches up after downtime.
Failed downloads, extraction, schema validation, or state installation retain
the prior upstream snapshot and published catalog.
