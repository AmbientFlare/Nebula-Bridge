from __future__ import annotations

import base64
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec


SIGNATURE_ALGORITHM = "ecdsa-p256-sha256"


class ManifestSigner:
    def __init__(self, private_key_path: str | Path) -> None:
        self.private_key_path = Path(private_key_path)

    def _private_key(self):
        if not self.private_key_path.is_file():
            raise ValueError("manifest signing key is not configured")
        key = serialization.load_pem_private_key(
            self.private_key_path.read_bytes(), password=None
        )
        if not isinstance(key, ec.EllipticCurvePrivateKey) or not isinstance(
            key.curve, ec.SECP256R1
        ):
            raise ValueError("manifest key must be an ECDSA P-256 private key")
        return key

    def sign(self, content: bytes) -> str:
        signature = self._private_key().sign(content, ec.ECDSA(hashes.SHA256()))
        return base64.b64encode(signature).decode("ascii")

    def public_key_base64(self) -> str:
        public = self._private_key().public_key().public_bytes(
            serialization.Encoding.DER,
            serialization.PublicFormat.SubjectPublicKeyInfo,
        )
        return base64.b64encode(public).decode("ascii")
