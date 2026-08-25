from __future__ import annotations

import base64
import hashlib
import io
import json
import os
import tarfile
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import yaml
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec

import app as registry_app
from catalog import CatalogStore
from signing import ManifestSigner


FIXTURE_ROOT = Path(__file__).resolve().parent.parent / "indexers"


def archive_for(definitions: dict[str, bytes]) -> bytes:
    output = io.BytesIO()
    files = {"schema.json": (FIXTURE_ROOT / "schema.json").read_bytes(), **definitions}
    with tarfile.open(fileobj=output, mode="w:gz") as package:
        for name, content in files.items():
            info = tarfile.TarInfo(f"Prowlarr-master/definitions/v11/{name}")
            info.size = len(content)
            package.addfile(info, io.BytesIO(content))
    return output.getvalue()


def fixture(name: str) -> bytes:
    return (FIXTURE_ROOT / name).read_bytes()


def basic_auth() -> dict[str, str]:
    token = base64.b64encode(b"catalog-admin:test-password").decode("ascii")
    return {"Authorization": f"Basic {token}"}


class CatalogAppTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        self.store = CatalogStore(root / "data")
        self.key_path = root / "manifest-key.pem"
        key = ec.generate_private_key(ec.SECP256R1())
        self.key_path.write_bytes(
            key.private_bytes(
                serialization.Encoding.PEM,
                serialization.PrivateFormat.PKCS8,
                serialization.NoEncryption(),
            )
        )
        self.public_key = key.public_key()
        self.original_store = registry_app.store
        self.original_signer = registry_app.signer
        registry_app.store = self.store
        registry_app.signer = ManifestSigner(self.key_path)
        self.addCleanup(self._restore_globals)
        self.environment = patch.dict(
            os.environ,
            {
                "NEBULA_ADMIN_USERNAME": "catalog-admin",
                "NEBULA_ADMIN_PASSWORD": "test-password",
                "NEBULA_BOOTSTRAP_APPROVED_IDS": "",
            },
            clear=False,
        )
        self.environment.start()
        self.addCleanup(self.environment.stop)
        registry_app.app.config.update(TESTING=True)
        self.client = registry_app.app.test_client()

    def _restore_globals(self) -> None:
        registry_app.store = self.original_store
        registry_app.signer = self.original_signer

    def sync(self, definitions: dict[str, bytes], revision: str = "a" * 40):
        return self.store.install_upstream_archive(archive_for(definitions), revision)

    def test_sync_discovers_valid_definitions_as_unpublished(self) -> None:
        result = self.sync(
            {
                "showrss.yml": fixture("showrss.yml"),
                "internetarchive.yml": fixture("internetarchive.yml"),
            }
        )
        self.assertEqual(2, result["valid"])
        self.assertEqual(2, result["new"])
        items = self.store.list_admin()["indexers"]
        self.assertEqual({"new"}, {item["upstreamStatus"] for item in items})
        self.assertTrue(all(not item["published"] for item in items))
        self.assertIn("compatible", {item["compatibilityStatus"] for item in items})

    def test_publication_survives_changes_and_removed_state_is_retained(self) -> None:
        showrss = fixture("showrss.yml")
        internet_archive = fixture("internetarchive.yml")
        self.sync({"showrss.yml": showrss, "internetarchive.yml": internet_archive})
        self.store.set_published("showrss-yml", True)
        changed = showrss + b"\n# upstream changed\n"
        result = self.sync({"showrss.yml": changed}, "b" * 40)
        self.assertEqual(1, result["updated"])
        self.assertEqual(1, result["removed"])
        by_id = {item["id"]: item for item in self.store.list_admin()["indexers"]}
        self.assertTrue(by_id["showrss-yml"]["published"])
        self.assertEqual("removed", by_id["internetarchive"]["upstreamStatus"])
        self.assertFalse(by_id["internetarchive"]["published"])

    def test_failed_staged_sync_retains_last_known_good_catalog(self) -> None:
        self.sync({"showrss.yml": fixture("showrss.yml")})
        self.store.set_published("showrss-yml", True)
        previous_manifest = self.store.manifest()
        previous_yaml = self.store.published_definition("showrss-yml")["yaml"]
        with self.assertRaises(ValueError):
            self.store.install_upstream_archive(b"not an archive", "b" * 40)
        self.assertEqual(previous_manifest["catalog_version"], self.store.manifest()["catalog_version"])
        self.assertEqual(previous_yaml, self.store.published_definition("showrss-yml")["yaml"])

    def test_unsupported_definition_is_visible_but_cannot_be_published(self) -> None:
        document = yaml.safe_load(fixture("showrss.yml"))
        document["search"]["rows"]["filters"].append({"name": "strdump"})
        unsupported = yaml.safe_dump(document, sort_keys=False).encode()
        self.sync({"unsupported.yml": unsupported})
        item = self.store.list_admin()["indexers"][0]
        self.assertEqual("unsupported", item["compatibilityStatus"])
        self.assertIn("strdump", " ".join(item["compatibilityNotes"]))
        with self.assertRaises(ValueError):
            self.store.set_published("showrss-yml", True)

    def test_admin_requires_authentication_and_can_publish(self) -> None:
        self.sync({"showrss.yml": fixture("showrss.yml")})
        denied = self.client.get("/api/v1/admin/indexers")
        self.assertEqual(401, denied.status_code)
        self.assertIn("Basic", denied.headers["WWW-Authenticate"])
        listing = self.client.get("/api/v1/admin/indexers", headers=basic_auth())
        self.assertEqual(200, listing.status_code)
        changed = self.client.patch(
            "/api/v1/admin/indexers/showrss-yml",
            headers=basic_auth(),
            json={"published": True},
        )
        self.assertEqual(200, changed.status_code)
        self.assertTrue(changed.get_json()["published"])

    def test_signed_manifest_contains_only_published_and_yaml_is_hash_identical(self) -> None:
        self.sync(
            {
                "showrss.yml": fixture("showrss.yml"),
                "internetarchive.yml": fixture("internetarchive.yml"),
            }
        )
        self.store.set_published("showrss-yml", True)
        response = self.client.get("/api/v1/indexers/manifest")
        self.assertEqual(200, response.status_code)
        signature = base64.b64decode(response.headers["X-Nebula-Signature"])
        self.public_key.verify(signature, response.data, ec.ECDSA(hashes.SHA256()))
        manifest = json.loads(response.data)
        self.assertEqual(["showrss-yml"], [item["id"] for item in manifest["indexers"]])
        definition = self.client.get("/api/v1/indexers/showrss-yml")
        self.assertEqual(200, definition.status_code)
        self.assertEqual(
            manifest["indexers"][0]["sha256"],
            hashlib.sha256(definition.data).hexdigest(),
        )
        self.assertEqual(404, self.client.get("/api/v1/indexers/internetarchive").status_code)
        self.assertEqual(404, self.client.get("/api/v1/indexers/..%2Fstate.json").status_code)

    def test_unpublished_change_does_not_change_catalog_version(self) -> None:
        original = fixture("showrss.yml")
        self.sync({"showrss.yml": original})
        before = self.store.manifest()["catalog_version"]
        self.sync({"showrss.yml": original + b"\n# unpublished change\n"}, "b" * 40)
        self.assertEqual(before, self.store.manifest()["catalog_version"])
        self.store.set_published("showrss-yml", True)
        self.assertNotEqual(before, self.store.manifest()["catalog_version"])

    def test_manual_sync_route_uses_shared_sync_service(self) -> None:
        result = {"success": True, "valid": 3, "invalid": 0, "new": 3, "updated": 0, "removed": 0}
        with patch.object(self.store, "fetch_upstream", return_value=result) as sync:
            response = self.client.post("/api/v1/admin/sync", headers=basic_auth())
        self.assertEqual(200, response.status_code)
        self.assertEqual(3, response.get_json()["valid"])
        sync.assert_called_once_with()


if __name__ == "__main__":
    unittest.main()
