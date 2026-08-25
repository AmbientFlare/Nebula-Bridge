from __future__ import annotations

import hashlib
import hmac
import json
import os
import sys
from base64 import b64decode
from functools import wraps
from pathlib import Path

from flask import Flask, Response, jsonify, request

from catalog import CatalogStore
from signing import ManifestSigner, SIGNATURE_ALGORITHM


app = Flask(__name__)
store = CatalogStore(os.environ.get("NEBULA_CATALOG_DATA", "/data"))
signer = ManifestSigner(
    os.environ.get(
        "NEBULA_MANIFEST_SIGNING_KEY",
        "/run/secrets/manifest-signing-key.pem",
    )
)


MANAGEMENT_PAGE = r"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Nebula Bridge Indexer Catalog</title><style>
:root{color-scheme:dark}body{margin:0;background:#080b1a;color:#eef4ff;font:16px system-ui,sans-serif}main{max-width:86rem;margin:2rem auto;padding:2rem;border:1px solid #39447a;border-radius:1rem;background:#11162d}h1{color:#88e5ff}.tools{display:flex;gap:.7rem;flex-wrap:wrap;align-items:center;margin:1rem 0}button,input,select{padding:.65rem .8rem;border:1px solid #566197;border-radius:.4rem;background:#080b1a;color:#fff}button{background:#4b75e8;cursor:pointer}.danger{background:#9f3f54}.status{min-height:1.5rem;color:#8fffc1}.muted{color:#aeb9d8;font-size:.9rem}.item{display:grid;grid-template-columns:minmax(18rem,1fr) minmax(15rem,1fr) auto;gap:1rem;align-items:center;padding:.85rem 0;border-bottom:1px solid #303858}.bad{color:#ff9bad}.good{color:#8fffc1}#search{min-width:18rem;flex:1}a{color:#9fc5ff}code{background:#070a16;padding:.2rem .4rem;border-radius:.25rem}@media(max-width:760px){.item{grid-template-columns:1fr}}
</style></head><body><main><h1>Nebula Bridge Cardigann v11 Catalog</h1>
<p>This service distributes approved definitions. Torrent searches always execute inside each Jellyfin installation.</p>
<div class="tools"><button id="sync">Sync Upstream Now</button><input id="upload" type="file" accept=".yml,.yaml,application/yaml"><label><input id="publishUpload" type="checkbox"> Publish upload</label><button id="uploadButton">Upload custom YAML</button></div>
<div class="tools"><input id="search" type="search" placeholder="Search name, ID, or description"><select id="filter"><option value="all">All</option><option value="published">Published</option><option value="unpublished">Unpublished</option><option value="compatible">Compatible</option><option value="unsupported">Unsupported / invalid</option></select><button id="refresh">Refresh</button></div>
<p id="status" class="status"></p><div id="summary" class="muted"></div><div id="items"></div>
<p class="muted">Public manifest: <a href="/api/v1/indexers/manifest"><code>/api/v1/indexers/manifest</code></a> · Health: <a href="/healthz"><code>/healthz</code></a></p>
</main><script>
const status=document.querySelector('#status'),items=document.querySelector('#items'),search=document.querySelector('#search'),filter=document.querySelector('#filter'),summary=document.querySelector('#summary');let catalog=[];
function visible(i,q,f){if(q&&!`${i.id} ${i.name} ${i.description}`.toLowerCase().includes(q))return false;if(f==='published')return i.published;if(f==='unpublished')return !i.published;if(f==='compatible')return i.compatible;if(f==='unsupported')return !i.compatible;return true}
function draw(){const q=search.value.trim().toLowerCase(),f=filter.value;items.replaceChildren();let count=0;for(const i of catalog){if(!visible(i,q,f))continue;count++;const row=document.createElement('div');row.className='item';const identity=document.createElement('div');const title=document.createElement('strong');title.textContent=i.name||i.id;const meta=document.createElement('div');meta.className='muted';meta.textContent=`${i.id} · ${i.type||'unknown'} · ${i.language||'unknown'} · ${i.source||'unknown'} · v${i.definitionSchemaVersion||11}`;identity.append(title,meta);const diagnostic=document.createElement('div');const state=document.createElement('strong');state.className=i.compatible?'good':'bad';state.textContent=i.compatibilityStatus||'invalid';const change=document.createElement('div');change.className='muted';change.textContent=`Upstream: ${i.upstreamStatus||'unknown'}${i.lastChangedUtc?' · '+new Date(i.lastChangedUtc).toLocaleString():''}`;const notes=document.createElement('div');notes.className='muted';notes.textContent=(i.compatibilityNotes||[]).join(' · ');diagnostic.append(state,change,notes);const publish=document.createElement('label');const box=document.createElement('input');box.type='checkbox';box.checked=!!i.published;box.disabled=!i.compatible||i.upstreamStatus==='removed';publish.append(box,' Publish');box.onchange=async()=>{try{const response=await fetch(`/api/v1/admin/indexers/${encodeURIComponent(i.id)}`,{method:'PATCH',headers:{'Content-Type':'application/json'},body:JSON.stringify({published:box.checked})});const body=await response.json();if(!response.ok)throw new Error(body.message||'Publication update failed.');await load()}catch(error){status.textContent=error.message;box.checked=!box.checked}};row.append(identity,diagnostic,publish);items.append(row)}summary.textContent=`${count} shown · ${catalog.length} catalog records.`}
async function load(){status.textContent='Loading catalog…';try{const response=await fetch('/api/v1/admin/indexers',{cache:'no-store'}),body=await response.json();if(!response.ok)throw new Error(body.message||'Catalog unavailable.');catalog=body.indexers||[];status.textContent=`Upstream ${body.upstreamRevision||'not synchronized'}${body.lastSyncUtc?' · synced '+new Date(body.lastSyncUtc).toLocaleString():''}`;draw()}catch(error){status.textContent=error.message}}
document.querySelector('#sync').onclick=async()=>{status.textContent='Downloading, staging, and validating Prowlarr v11…';try{const response=await fetch('/api/v1/admin/sync',{method:'POST'}),body=await response.json();if(!response.ok)throw new Error(body.message||'Sync failed.');status.textContent=`Synchronized ${body.valid} valid definitions; ${body.invalid} invalid; ${body.new} new; ${body.updated} updated; ${body.removed} removed.`;await load()}catch(error){status.textContent=error.message}};
document.querySelector('#uploadButton').onclick=async()=>{const file=document.querySelector('#upload').files[0];if(!file){status.textContent='Choose a YAML file first.';return}const form=new FormData();form.append('definition',file);form.append('published',document.querySelector('#publishUpload').checked?'true':'false');try{const response=await fetch('/api/v1/admin/indexers/upload',{method:'POST',body:form}),body=await response.json();if(!response.ok)throw new Error(body.message||'Upload failed.');status.textContent=`Uploaded ${body.id}.`;await load()}catch(error){status.textContent=error.message}};
document.querySelector('#refresh').onclick=load;search.oninput=draw;filter.onchange=draw;load();
</script></body></html>"""


def json_error(message: str, status: int = 400):
    return jsonify({"success": False, "message": message}), status


def _authorized() -> bool:
    expected_user = os.environ.get("NEBULA_ADMIN_USERNAME")
    expected_password = os.environ.get("NEBULA_ADMIN_PASSWORD")
    password_file = os.environ.get("NEBULA_ADMIN_PASSWORD_FILE")
    if not expected_password and password_file:
        try:
            password_path = Path(password_file)
            if password_path.stat().st_size > 1024:
                return False
            expected_password = password_path.read_text(encoding="utf-8").strip()
        except OSError:
            expected_password = None
    supplied = request.authorization
    return bool(
        expected_user
        and expected_password
        and supplied
        and hmac.compare_digest(supplied.username or "", expected_user)
        and hmac.compare_digest(supplied.password or "", expected_password)
    )


def admin_required(function):
    @wraps(function)
    def protected(*args, **kwargs):
        if not _authorized():
            response = json_error("Administrator authentication required.", 401)
            response[0].headers["WWW-Authenticate"] = 'Basic realm="Nebula Indexer Catalog"'
            return response
        return function(*args, **kwargs)

    return protected


def _manifest_bytes() -> bytes:
    return json.dumps(
        store.manifest(),
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")


@app.get("/")
@app.get("/manage")
@admin_required
def home():
    return Response(MANAGEMENT_PAGE, content_type="text/html; charset=utf-8")


@app.get("/healthz")
def healthz():
    try:
        summary = store.health_summary()
        public_key = b64decode(signer.public_key_base64())
        return jsonify(
            {
                "status": "ok",
                "apiVersion": 1,
                "cardigannSchemaVersion": 11,
                **summary,
                "manifestSigningKeySha256": hashlib.sha256(public_key).hexdigest(),
            }
        )
    except (OSError, UnicodeError, ValueError) as error:
        return jsonify({"status": "error", "message": str(error)}), 503


@app.get("/api/v1/indexers")
@app.get("/api/v1/indexers/manifest")
def manifest():
    try:
        payload = _manifest_bytes()
        response = Response(payload, content_type="application/json")
        response.headers["X-Nebula-Signature"] = signer.sign(payload)
        response.headers["X-Nebula-Signature-Algorithm"] = SIGNATURE_ALGORITHM
        response.set_etag(hashlib.sha256(payload).hexdigest())
        response.cache_control.public = True
        response.cache_control.max_age = 300
        return response.make_conditional(request)
    except (OSError, UnicodeError, ValueError) as error:
        return json_error(f"Catalog unavailable: {error}", 503)


@app.get("/api/v1/indexers/<indexer_id>")
def definition(indexer_id: str):
    try:
        item = store.published_definition(indexer_id)
        response = Response(item["yaml"], content_type="application/yaml; charset=utf-8")
        response.set_etag(item["sha256"])
        response.cache_control.public = True
        response.cache_control.max_age = 300
        return response.make_conditional(request)
    except KeyError:
        return json_error("Definition not found.", 404)
    except (OSError, UnicodeError, ValueError) as error:
        return json_error(f"Catalog unavailable: {error}", 503)


@app.get("/api/v1/admin/indexers")
@admin_required
def admin_indexers():
    try:
        return jsonify(store.list_admin())
    except (OSError, UnicodeError, ValueError) as error:
        return json_error(str(error), 503)


@app.patch("/api/v1/admin/indexers/<indexer_id>")
@admin_required
def update_indexer(indexer_id: str):
    body = request.get_json(silent=True)
    if not isinstance(body, dict) or not isinstance(body.get("published"), bool):
        return json_error("Supply a boolean published value.")
    try:
        return jsonify({"success": True, **store.set_published(indexer_id, body["published"])})
    except KeyError:
        return json_error("Indexer not found.", 404)
    except (OSError, UnicodeError, ValueError) as error:
        return json_error(str(error))


@app.delete("/api/v1/admin/indexers/<indexer_id>")
@admin_required
def remove_indexer(indexer_id: str):
    try:
        store.remove_custom(indexer_id)
        return jsonify({"success": True, "message": "Custom definition removed."})
    except KeyError:
        return json_error("Only custom definitions can be removed.", 404)
    except (OSError, UnicodeError, ValueError) as error:
        return json_error(str(error))


@app.post("/api/v1/admin/indexers/upload")
@admin_required
def upload_definition():
    uploaded = request.files.get("definition")
    if uploaded is None:
        return json_error("Upload a YAML definition file.")
    content = uploaded.stream.read(512 * 1024 + 1)
    published = request.form.get("published", "false").casefold() == "true"
    try:
        return jsonify({"success": True, **store.upload_custom(content, published)})
    except (OSError, UnicodeError, ValueError) as error:
        return json_error(str(error))


@app.post("/api/v1/admin/sync")
@admin_required
def sync_upstream():
    try:
        return jsonify(store.fetch_upstream())
    except Exception as error:
        app.logger.exception("Prowlarr v11 synchronization failed")
        return json_error(f"Upstream synchronization failed: {error}", 502)


def main() -> int:
    if len(sys.argv) == 2 and sys.argv[1] == "sync-upstream":
        print(json.dumps(store.fetch_upstream(), sort_keys=True))
        return 0
    app.run(host="0.0.0.0", port=5050)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
