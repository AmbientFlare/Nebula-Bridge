from __future__ import annotations

import fcntl
import hashlib
import io
import json
import logging
import os
import re
import shutil
import tarfile
import tempfile
from contextlib import contextmanager
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Iterator
from urllib.request import Request, urlopen

import yaml
from jsonschema import FormatChecker
from jsonschema.validators import validator_for

from compatibility import classify


CARDIGANN_SCHEMA_VERSION = 11
MANIFEST_VERSION = 1
MAX_ARCHIVE_BYTES = 64 * 1024 * 1024
MAX_DEFINITION_BYTES = 512 * 1024
MAX_DEFINITIONS = 1000
UPSTREAM_ARCHIVE_URL = os.environ.get(
    "NEBULA_PROWLARR_ARCHIVE_URL",
    "https://codeload.github.com/Prowlarr/Indexers/tar.gz/refs/heads/master",
)
UPSTREAM_COMMIT_URL = os.environ.get(
    "NEBULA_PROWLARR_COMMIT_URL",
    "https://api.github.com/repos/Prowlarr/Indexers/commits/master",
)
VALID_ID = re.compile(r"^[a-z0-9][a-z0-9-]{1,127}$")
LOGGER = logging.getLogger("nebula.indexer_catalog")


def utc_now() -> str:
    return datetime.now(UTC).isoformat().replace("+00:00", "Z")


def atomic_write(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        dir=path.parent, prefix=f".{path.name}.", suffix=".tmp"
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def download_limited(url: str, limit: int, content_type: str | None = None) -> bytes:
    request = Request(
        url,
        headers={
            "Accept": content_type or "application/octet-stream",
            "User-Agent": "NebulaBridge-CardigannCatalog/2.0",
        },
    )
    with urlopen(request, timeout=45) as response:
        declared = response.headers.get("Content-Length")
        if declared and int(declared) > limit:
            raise ValueError(f"upstream response exceeds {limit} bytes")
        content = response.read(limit + 1)
    if len(content) > limit:
        raise ValueError(f"upstream response exceeds {limit} bytes")
    return content


def json_compatible(value: Any) -> Any:
    """Mirror YAML-to-JSON schema validation without mutating execution data."""
    if isinstance(value, dict):
        return {str(key): json_compatible(child) for key, child in value.items()}
    if isinstance(value, list):
        return [json_compatible(child) for child in value]
    return value


class CatalogStore:
    def __init__(self, data_directory: str | Path) -> None:
        self.data_directory = Path(data_directory).resolve()
        self.upstream_directory = (
            self.data_directory / "upstream" / "prowlarr" / "definitions" / "v11"
        )
        self.custom_directory = self.data_directory / "custom" / "definitions" / "v11"
        self.state_path = self.data_directory / "state.json"
        self.lock_path = self.data_directory / ".catalog.lock"

    @contextmanager
    def locked(self) -> Iterator[None]:
        self.data_directory.mkdir(parents=True, exist_ok=True)
        with self.lock_path.open("a+b") as lock:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
            try:
                yield
            finally:
                fcntl.flock(lock.fileno(), fcntl.LOCK_UN)

    def load_state(self) -> dict[str, Any]:
        defaults: dict[str, Any] = {
            "manifestVersion": MANIFEST_VERSION,
            "cardigannSchemaVersion": CARDIGANN_SCHEMA_VERSION,
            "indexers": {},
            "lastChangedUtc": None,
            "lastSyncUtc": None,
            "upstreamRevision": None,
        }
        if not self.state_path.exists():
            return defaults
        try:
            loaded = json.loads(self.state_path.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError) as error:
            raise ValueError(f"catalog state is invalid: {error}") from error
        if not isinstance(loaded, dict) or not isinstance(loaded.get("indexers", {}), dict):
            raise ValueError("catalog state must be an object with indexers metadata")
        return {**defaults, **loaded}

    def save_state(self, state: dict[str, Any]) -> None:
        state["manifestVersion"] = MANIFEST_VERSION
        state["cardigannSchemaVersion"] = CARDIGANN_SCHEMA_VERSION
        atomic_write(
            self.state_path,
            (json.dumps(state, indent=2, sort_keys=True) + "\n").encode("utf-8"),
        )

    def fetch_upstream(self) -> dict[str, Any]:
        LOGGER.info("Prowlarr sync started")
        commit_document = json.loads(
            download_limited(
                UPSTREAM_COMMIT_URL, 1024 * 1024, "application/vnd.github+json"
            )
        )
        revision = commit_document.get("sha")
        if not isinstance(revision, str) or not re.fullmatch(r"[0-9a-f]{40}", revision):
            raise ValueError("upstream commit response did not contain a valid SHA")
        archive = download_limited(UPSTREAM_ARCHIVE_URL, MAX_ARCHIVE_BYTES)
        result = self.install_upstream_archive(archive, revision)
        LOGGER.info(
            "Prowlarr sync completed: %s valid, %s invalid, %s new, %s updated, %s removed",
            result["valid"],
            result["invalid"],
            result["new"],
            result["updated"],
            result["removed"],
        )
        return result

    def install_upstream_archive(self, archive: bytes, revision: str) -> dict[str, Any]:
        if len(archive) > MAX_ARCHIVE_BYTES:
            raise ValueError("upstream archive exceeds 64 MiB")
        if not re.fullmatch(r"[0-9a-f]{7,64}", revision):
            raise ValueError("upstream revision is invalid")

        self.data_directory.mkdir(parents=True, exist_ok=True)
        staging_root = Path(
            tempfile.mkdtemp(prefix=".upstream-v11.", dir=self.data_directory)
        )
        staging = staging_root / "v11"
        staging.mkdir()
        try:
            try:
                extracted = self._extract_v11(archive, staging)
            except (tarfile.TarError, EOFError, OSError) as error:
                raise ValueError(f"upstream archive is invalid: {error}") from error
            schema = self._load_schema(staging / "schema.json")
            valid_definitions: dict[str, dict[str, Any]] = {}
            invalid: list[dict[str, str]] = []
            for path in sorted(staging.glob("*.yml"), key=lambda item: item.name.casefold()):
                try:
                    definition = self._load_definition(path, schema)
                    if definition["id"] in valid_definitions:
                        raise ValueError(f"duplicate indexer id: {definition['id']}")
                    definition["upstreamPath"] = f"definitions/v11/{path.name}"
                    valid_definitions[definition["id"]] = definition
                except (OSError, UnicodeError, ValueError, yaml.YAMLError) as error:
                    invalid.append(
                        {
                            "id": self._best_effort_id(path),
                            "file": path.name,
                            "error": str(error),
                        }
                    )
                    path.unlink(missing_ok=True)
            if not valid_definitions:
                raise ValueError("upstream archive contained no valid v11 definitions")

            with self.locked():
                state_existed = self.state_path.exists()
                previous_state = self.state_path.read_bytes() if state_existed else None
                state = self.load_state()
                now = utc_now()
                seen_ids = set(valid_definitions)
                seen_ids.update(item["id"] for item in invalid)
                for indexer_id, definition in valid_definitions.items():
                    existing = dict(state["indexers"].get(indexer_id, {}))
                    old_hash = existing.get("definitionHash")
                    upstream_status = (
                        "new"
                        if existing.get("firstSeenUtc") is None
                        else "updated"
                        if old_hash != definition["sha256"]
                        else "unchanged"
                    )
                    state["indexers"][indexer_id] = {
                        **existing,
                        "id": indexer_id,
                        "name": definition["name"],
                        "description": definition["description"],
                        "upstreamSource": "Prowlarr/Indexers",
                        "upstreamPath": definition["upstreamPath"],
                        "definitionSchemaVersion": CARDIGANN_SCHEMA_VERSION,
                        "definitionHash": definition["sha256"],
                        "firstSeenUtc": existing.get("firstSeenUtc") or now,
                        "lastSeenUtc": now,
                        "lastChangedUtc": now
                        if upstream_status in ("new", "updated")
                        else existing.get("lastChangedUtc") or now,
                        "type": definition["type"],
                        "language": definition["language"],
                        "sizeBytes": definition["sizeBytes"],
                        "requiresAuthentication": definition["requiresAuthentication"],
                        "requiresConfiguration": definition["requiresConfiguration"],
                        "compatible": definition["compatible"],
                        "compatibilityStatus": definition["compatibilityStatus"],
                        "compatibilityNotes": definition["compatibilityNotes"],
                        "upstreamStatus": upstream_status,
                        "source": "upstream",
                        "published": existing.get("published") is True,
                    }
                    if upstream_status == "new":
                        LOGGER.info("New indexer discovered: %s", indexer_id)
                    elif upstream_status == "updated":
                        LOGGER.info("Definition updated: %s", indexer_id)
                for invalid_item in invalid:
                    indexer_id = invalid_item["id"]
                    existing = dict(state["indexers"].get(indexer_id, {}))
                    state["indexers"][indexer_id] = {
                        **existing,
                        "id": indexer_id,
                        "name": existing.get("name") or indexer_id,
                        "description": existing.get("description") or "",
                        "upstreamSource": "Prowlarr/Indexers",
                        "upstreamPath": f"definitions/v11/{invalid_item['file']}",
                        "definitionSchemaVersion": CARDIGANN_SCHEMA_VERSION,
                        "firstSeenUtc": existing.get("firstSeenUtc") or now,
                        "lastSeenUtc": now,
                        "lastChangedUtc": now,
                        "compatible": False,
                        "compatibilityStatus": "invalid",
                        "compatibilityNotes": [invalid_item["error"]],
                        "upstreamStatus": "invalid",
                        "source": "upstream",
                        "published": False,
                    }
                    LOGGER.warning(
                        "Definition validation failed: %s — %s",
                        indexer_id,
                        invalid_item["error"],
                    )
                for indexer_id, existing in list(state["indexers"].items()):
                    if existing.get("source") == "upstream" and indexer_id not in seen_ids:
                        state["indexers"][indexer_id] = {
                            **existing,
                            "compatible": False,
                            "compatibilityStatus": "removed",
                            "compatibilityNotes": ["Definition was removed upstream."],
                            "upstreamStatus": "removed",
                        }
                state["lastSyncUtc"] = now
                state["lastChangedUtc"] = now
                state["upstreamRevision"] = revision
                state["lastSyncInvalid"] = invalid[:100]
                self._apply_bootstrap_approvals(state)

                self.upstream_directory.parent.mkdir(parents=True, exist_ok=True)
                previous = self.upstream_directory.with_name(".v11.previous")
                shutil.rmtree(previous, ignore_errors=True)
                if self.upstream_directory.exists():
                    os.replace(self.upstream_directory, previous)
                try:
                    os.replace(staging, self.upstream_directory)
                    self.save_state(state)
                except Exception:
                    shutil.rmtree(self.upstream_directory, ignore_errors=True)
                    if previous.exists():
                        os.replace(previous, self.upstream_directory)
                    if previous_state is not None:
                        atomic_write(self.state_path, previous_state)
                    elif not state_existed:
                        self.state_path.unlink(missing_ok=True)
                    raise
                shutil.rmtree(previous, ignore_errors=True)
            return {
                "success": True,
                "revision": revision,
                "discovered": extracted,
                "valid": len(valid_definitions),
                "invalid": len(invalid),
                "invalidDefinitions": invalid[:20],
                "new": sum(
                    item.get("upstreamStatus") == "new"
                    for item in state["indexers"].values()
                ),
                "updated": sum(
                    item.get("upstreamStatus") == "updated"
                    for item in state["indexers"].values()
                ),
                "removed": sum(
                    item.get("upstreamStatus") == "removed"
                    for item in state["indexers"].values()
                ),
            }
        finally:
            shutil.rmtree(staging_root, ignore_errors=True)

    def _extract_v11(self, archive: bytes, output: Path) -> int:
        discovered = 0
        schema_found = False
        with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as package:
            for member in package.getmembers():
                normalized = member.name.replace("\\", "/")
                marker = "/definitions/v11/"
                if marker not in normalized or not member.isfile():
                    continue
                relative = normalized.split(marker, 1)[1]
                if "/" in relative or relative in ("", ".", ".."):
                    continue
                if relative != "schema.json" and not relative.endswith((".yml", ".yaml")):
                    continue
                if member.size > MAX_DEFINITION_BYTES:
                    continue
                stream = package.extractfile(member)
                if stream is None:
                    continue
                content = stream.read(MAX_DEFINITION_BYTES + 1)
                if len(content) > MAX_DEFINITION_BYTES:
                    continue
                target_name = "schema.json" if relative == "schema.json" else Path(relative).stem + ".yml"
                atomic_write(output / target_name, content)
                if relative == "schema.json":
                    schema_found = True
                else:
                    discovered += 1
                    if discovered > MAX_DEFINITIONS:
                        raise ValueError("upstream archive exceeds the definition limit")
        if not schema_found:
            raise ValueError("upstream archive did not contain definitions/v11/schema.json")
        return discovered

    @staticmethod
    def _best_effort_id(path: Path) -> str:
        try:
            document = yaml.safe_load(path.read_text(encoding="utf-8"))
            indexer_id = document.get("id") if isinstance(document, dict) else None
            if isinstance(indexer_id, str) and VALID_ID.fullmatch(indexer_id):
                return indexer_id
        except (OSError, UnicodeError, yaml.YAMLError):
            pass
        fallback = re.sub(r"[^a-z0-9-]+", "-", path.stem.casefold()).strip("-")
        return fallback if VALID_ID.fullmatch(fallback) else f"invalid-{hashlib.sha256(path.name.encode()).hexdigest()[:12]}"

    def _load_schema(self, path: Path) -> Any:
        if not path.exists() or path.stat().st_size > MAX_DEFINITION_BYTES:
            raise ValueError("Cardigann v11 schema is missing or too large")
        try:
            schema = json.loads(path.read_text(encoding="utf-8"))
            validator_type = validator_for(schema)
            validator_type.check_schema(schema)
            return validator_type(schema, format_checker=FormatChecker())
        except (OSError, UnicodeError, json.JSONDecodeError) as error:
            raise ValueError(f"Cardigann v11 schema is invalid: {error}") from error

    def _load_definition(self, path: Path, schema: Any) -> dict[str, Any]:
        if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_DEFINITION_BYTES:
            raise ValueError(f"{path.name} is not a safe definition file")
        text = path.read_text(encoding="utf-8")
        document = yaml.safe_load(text)
        if not isinstance(document, dict):
            raise ValueError(f"{path.name} must contain a YAML object")
        errors = sorted(
            schema.iter_errors(json_compatible(document)),
            key=lambda error: list(error.path),
        )
        if errors:
            first = errors[0]
            location = ".".join(str(part) for part in first.path) or "root"
            raise ValueError(f"{path.name} failed v11 schema at {location}: {first.message}")
        indexer_id = document.get("id")
        if not isinstance(indexer_id, str) or not VALID_ID.fullmatch(indexer_id):
            raise ValueError(f"{path.name} has an invalid indexer id")
        raw = text.encode("utf-8")
        compatibility = classify(document)
        return {
            "id": indexer_id,
            "name": str(document.get("name") or indexer_id),
            "description": str(document.get("description") or ""),
            "language": str(document.get("language") or ""),
            "type": str(document.get("type") or ""),
            "sha256": hashlib.sha256(raw).hexdigest(),
            "sizeBytes": len(raw),
            "yaml": text,
            "path": path,
            "document": document,
            **compatibility,
        }

    def load_available(self) -> dict[str, dict[str, Any]]:
        schema_path = self.upstream_directory / "schema.json"
        if not schema_path.exists():
            raise ValueError("no synchronized Cardigann v11 schema is available")
        schema = self._load_schema(schema_path)
        definitions: dict[str, dict[str, Any]] = {}
        for source, directory in (
            ("upstream", self.upstream_directory),
            ("custom", self.custom_directory),
        ):
            if not directory.exists():
                continue
            for path in sorted(directory.glob("*.yml"), key=lambda item: item.name.casefold()):
                definition = self._load_definition(path, schema)
                definition["source"] = source
                definitions[definition["id"]] = definition
        return definitions

    def list_admin(self) -> dict[str, Any]:
        state = self.load_state()
        items = []
        for indexer_id, persisted in state["indexers"].items():
            metadata = dict(persisted)
            metadata.setdefault("id", indexer_id)
            metadata.setdefault("name", indexer_id)
            metadata.setdefault("description", "")
            metadata.setdefault("language", "")
            metadata.setdefault("type", "unknown")
            metadata.setdefault("compatible", False)
            metadata.setdefault("compatibilityStatus", "invalid")
            metadata.setdefault("compatibilityNotes", [])
            metadata["published"] = metadata.get("published") is True
            items.append(metadata)
        items.sort(key=lambda item: (str(item["name"]).casefold(), item["id"]))
        return {
            "manifestVersion": MANIFEST_VERSION,
            "cardigannSchemaVersion": CARDIGANN_SCHEMA_VERSION,
            "lastSyncUtc": state.get("lastSyncUtc"),
            "upstreamRevision": state.get("upstreamRevision"),
            "indexers": items,
        }

    def health_summary(self) -> dict[str, Any]:
        state = self.load_state()
        items = list(state["indexers"].values())
        return {
            "available": sum(item.get("upstreamStatus") != "removed" for item in items),
            "compatible": sum(item.get("compatible") is True for item in items),
            "published": sum(
                item.get("published") is True and item.get("compatible") is True
                for item in items
            ),
            "lastSyncUtc": state.get("lastSyncUtc"),
            "upstreamRevision": state.get("upstreamRevision"),
        }

    def set_published(self, indexer_id: str, published: bool) -> dict[str, Any]:
        with self.locked():
            state = self.load_state()
            metadata = state["indexers"].get(indexer_id)
            if metadata is None:
                raise KeyError(indexer_id)
            if published:
                if metadata.get("compatible") is not True:
                    raise ValueError("only available, compatible definitions can be published")
                definition = self._load_available_definition(indexer_id, metadata)
                if not definition["compatible"]:
                    raise ValueError("only available, compatible definitions can be published")
            metadata["published"] = bool(published)
            state["indexers"][indexer_id] = metadata
            state["lastChangedUtc"] = utc_now()
            self.save_state(state)
            LOGGER.info(
                "Indexer %s: %s",
                "published" if published else "unpublished",
                indexer_id,
            )
            return {"id": indexer_id, "published": metadata["published"]}

    def upload_custom(self, content: bytes, publish: bool = False) -> dict[str, Any]:
        if len(content) > MAX_DEFINITION_BYTES:
            raise ValueError("definition exceeds 512 KiB")
        schema = self._load_schema(self.upstream_directory / "schema.json")
        self.custom_directory.mkdir(parents=True, exist_ok=True)
        descriptor, name = tempfile.mkstemp(
            dir=self.custom_directory, prefix=".upload.", suffix=".yml"
        )
        temporary = Path(name)
        try:
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(content)
            definition = self._load_definition(temporary, schema)
            if publish and not definition["compatible"]:
                raise ValueError("only compatible definitions can be published")
            target = self.custom_directory / f"{definition['id']}.yml"
            with self.locked():
                os.replace(temporary, target)
                state = self.load_state()
                now = utc_now()
                existing = dict(state["indexers"].get(definition["id"], {}))
                state["indexers"][definition["id"]] = {
                    **existing,
                    "id": definition["id"],
                    "name": definition["name"],
                    "description": definition["description"],
                    "upstreamSource": "custom-upload",
                    "upstreamPath": f"custom/definitions/v11/{target.name}",
                    "definitionSchemaVersion": CARDIGANN_SCHEMA_VERSION,
                    "definitionHash": definition["sha256"],
                    "firstSeenUtc": existing.get("firstSeenUtc") or now,
                    "lastSeenUtc": now,
                    "lastChangedUtc": now,
                    "type": definition["type"],
                    "language": definition["language"],
                    "sizeBytes": definition["sizeBytes"],
                    "requiresAuthentication": definition["requiresAuthentication"],
                    "requiresConfiguration": definition["requiresConfiguration"],
                    "compatible": definition["compatible"],
                    "compatibilityStatus": definition["compatibilityStatus"],
                    "compatibilityNotes": definition["compatibilityNotes"],
                    "upstreamStatus": "custom",
                    "source": "custom",
                    "published": publish,
                }
                state["lastChangedUtc"] = now
                self.save_state(state)
            return {"id": definition["id"], "published": publish}
        finally:
            temporary.unlink(missing_ok=True)

    def remove_custom(self, indexer_id: str) -> None:
        with self.locked():
            target = self.custom_directory / f"{indexer_id}.yml"
            if not target.exists():
                raise KeyError(indexer_id)
            target.unlink()
            state = self.load_state()
            preference = state["indexers"].setdefault(indexer_id, {})
            preference["published"] = False
            preference["compatible"] = False
            preference["compatibilityStatus"] = "removed"
            preference["compatibilityNotes"] = ["Custom definition was removed."]
            preference["upstreamStatus"] = "removed"
            state["lastChangedUtc"] = utc_now()
            self.save_state(state)

    def manifest(self) -> dict[str, Any]:
        state = self.load_state()
        published = []
        for indexer_id, preference in sorted(state["indexers"].items()):
            if (
                preference.get("published") is not True
                or preference.get("compatible") is not True
            ):
                continue
            definition = self._load_available_definition(indexer_id, preference)
            if not definition["compatible"]:
                raise ValueError(
                    f"published definition {indexer_id} is no longer client-compatible"
                )
            published.append(
                {
                    "id": indexer_id,
                    "name": definition["name"],
                    "description": definition["description"],
                    "language": definition["language"],
                    "type": definition["type"],
                    "definition_version": CARDIGANN_SCHEMA_VERSION,
                    "sha256": definition["sha256"],
                    "size_bytes": definition["sizeBytes"],
                    "definition_url": f"/api/v1/indexers/{indexer_id}",
                }
            )
        revision_input = "\n".join(
            f"{item['id']}:{item['sha256']}" for item in published
        ).encode("utf-8")
        result = {
            "api_version": MANIFEST_VERSION,
            "catalog_version": hashlib.sha256(revision_input).hexdigest(),
            "cardigann_schema": CARDIGANN_SCHEMA_VERSION,
            "minimum_client_version": "1.0.0",
            "generated_at": state.get("lastChangedUtc") or state.get("lastSyncUtc"),
            "upstream_revision": state.get("upstreamRevision"),
            "indexers": published,
        }
        LOGGER.info(
            "Catalog generated: %s (%s definitions)",
            result["catalog_version"],
            len(published),
        )
        return result

    def published_definition(self, indexer_id: str) -> dict[str, Any]:
        if not VALID_ID.fullmatch(indexer_id):
            raise KeyError(indexer_id)
        state = self.load_state()
        metadata = state["indexers"].get(indexer_id)
        if (
            metadata is None
            or metadata.get("published") is not True
            or metadata.get("compatible") is not True
        ):
            raise KeyError(indexer_id)
        definition = self._load_available_definition(indexer_id, metadata)
        if not definition["compatible"]:
            raise KeyError(indexer_id)
        return definition

    def _load_available_definition(
        self, indexer_id: str, metadata: dict[str, Any]
    ) -> dict[str, Any]:
        if not VALID_ID.fullmatch(indexer_id):
            raise KeyError(indexer_id)
        source = metadata.get("source")
        if source == "custom":
            path = self.custom_directory / f"{indexer_id}.yml"
        elif source == "upstream":
            upstream_path = metadata.get("upstreamPath")
            if not isinstance(upstream_path, str):
                raise KeyError(indexer_id)
            file_name = Path(upstream_path).name
            if file_name not in (f"{indexer_id}.yml", f"{indexer_id}.yaml"):
                # Prowlarr filenames may vary in case, but IDs never select a path directly.
                candidates = [
                    item
                    for item in self.upstream_directory.glob("*.yml")
                    if item.name.casefold() == file_name.casefold()
                ]
                if len(candidates) != 1:
                    raise KeyError(indexer_id)
                path = candidates[0]
            else:
                path = self.upstream_directory / file_name
        else:
            raise KeyError(indexer_id)
        schema = self._load_schema(self.upstream_directory / "schema.json")
        definition = self._load_definition(path, schema)
        if definition["id"] != indexer_id:
            raise ValueError(f"definition identity mismatch for {indexer_id}")
        definition["source"] = source
        return definition

    def _apply_bootstrap_approvals(self, state: dict[str, Any]) -> None:
        if any(item.get("published") is True for item in state["indexers"].values()):
            return
        configured = os.environ.get("NEBULA_BOOTSTRAP_APPROVED_IDS", "")
        for indexer_id in configured.split(","):
            normalized = indexer_id.strip()
            if normalized and state["indexers"].get(normalized, {}).get("compatible") is True:
                state["indexers"].setdefault(normalized, {})["published"] = True
