#!/usr/bin/env python3
"""Local Stremio-compatible and byte-range server for isolated acceptance tests."""

from __future__ import annotations

import argparse
import json
import mimetypes
import os
import re
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlsplit


MEDIA_FILES = (
    "fixture-h264-aac.mp4",
    "fixture-h264-aac.mkv",
    "fixture-vp9-opus.webm",
    "fixture-h264-aac.ts",
)
OPEN_MOVIE_META = {
    "id": "tt1727587",
    "type": "movie",
    "name": "Sintel",
    "description": "Blender Foundation open movie, licensed CC BY 3.0.",
    "releaseInfo": "2010",
    "released": "2010-09-27T00:00:00.000Z",
    "year": 2010,
    "runtime": "15 min",
    "genres": ["Animation", "Fantasy"],
    "imdb_id": "tt1727587",
}


class FixtureHandler(BaseHTTPRequestHandler):
    server_version = "NebulaBridgeFixture/1.0"

    def do_HEAD(self) -> None:
        self._dispatch(send_body=False)

    def do_GET(self) -> None:
        self._dispatch(send_body=True)

    def log_message(self, format: str, *args: object) -> None:
        print(f"fixture: {self.address_string()} {format % args}", flush=True)

    def _dispatch(self, send_body: bool) -> None:
        path = unquote(urlsplit(self.path).path)
        if path == "/health":
            self._json({"status": "ok"}, send_body)
            return
        if path == "/manifest.json":
            self._json(
                {
                    "id": "org.nebulabridge.fixture",
                    "version": "1.0.0",
                    "name": "Nebula Bridge playback fixture",
                    "description": "Isolated acceptance-test source",
                    "types": ["movie", "series"],
                    "resources": ["catalog", "meta", "stream"],
                    "catalogs": [
                        {
                            "type": "movie",
                            "id": "nebula-open-movies",
                            "name": "Nebula Open Movies",
                        }
                    ],
                },
                send_body,
            )
            return
        if path == "/catalog/movie/nebula-open-movies.json":
            self._json({"metas": [OPEN_MOVIE_META]}, send_body)
            return
        if path == "/meta/movie/tt1727587.json":
            self._json({"meta": OPEN_MOVIE_META}, send_body)
            return
        if re.fullmatch(r"/stream/(movie|series)/[^/]+\.json", path):
            streams = []
            for index, filename in enumerate(MEDIA_FILES, start=1):
                file_path = self.server.media_root / filename  # type: ignore[attr-defined]
                if file_path.is_file():
                    streams.append(
                        {
                            "url": f"{self.server.public_base}/media/{filename}",  # type: ignore[attr-defined]
                            "name": f"Nebula Bridge fixture {index}",
                            "title": filename,
                            "description": "Locally generated acceptance-test media",
                            "behaviorHints": {
                                "filename": filename,
                                "videoSize": file_path.stat().st_size,
                            },
                        }
                    )
            self._json({"streams": streams}, send_body)
            return
        if path.startswith("/subtitles/"):
            self._json({"subtitles": []}, send_body)
            return
        if path.startswith("/media/"):
            filename = Path(path.removeprefix("/media/")).name
            if filename not in MEDIA_FILES:
                self.send_error(HTTPStatus.NOT_FOUND)
                return
            self._file(self.server.media_root / filename, send_body)  # type: ignore[attr-defined]
            return
        self.send_error(HTTPStatus.NOT_FOUND)

    def _json(self, value: object, send_body: bool) -> None:
        payload = json.dumps(value, separators=(",", ":")).encode()
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        if send_body:
            self.wfile.write(payload)

    def _file(self, path: Path, send_body: bool) -> None:
        if not path.is_file():
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        size = path.stat().st_size
        start, end = 0, size - 1
        range_header = self.headers.get("Range")
        status = HTTPStatus.OK
        if range_header:
            match = re.fullmatch(r"bytes=(\d*)-(\d*)", range_header.strip())
            if not match:
                self.send_error(HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE)
                return
            first, last = match.groups()
            if first:
                start = int(first)
                end = int(last) if last else end
            elif last:
                suffix = int(last)
                start = max(0, size - suffix)
            if start >= size or end < start:
                self.send_response(HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE)
                self.send_header("Content-Range", f"bytes */{size}")
                self.end_headers()
                return
            end = min(end, size - 1)
            status = HTTPStatus.PARTIAL_CONTENT

        length = end - start + 1
        mime, _ = mimetypes.guess_type(path.name)
        self.send_response(status)
        self.send_header("Content-Type", mime or "application/octet-stream")
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        if status == HTTPStatus.PARTIAL_CONTENT:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        if not send_body:
            return
        with path.open("rb") as source:
            source.seek(start)
            remaining = length
            while remaining:
                chunk = source.read(min(64 * 1024, remaining))
                if not chunk:
                    break
                self.wfile.write(chunk)
                remaining -= len(chunk)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=18100)
    parser.add_argument("--media-root", type=Path, required=True)
    parser.add_argument("--public-base")
    args = parser.parse_args()
    media_root = args.media_root.resolve(strict=True)
    missing = [name for name in MEDIA_FILES if not (media_root / name).is_file()]
    if missing:
        raise SystemExit(f"missing fixture files: {', '.join(missing)}")
    server = ThreadingHTTPServer((args.bind, args.port), FixtureHandler)
    server.media_root = media_root  # type: ignore[attr-defined]
    server.public_base = args.public_base or f"http://{args.bind}:{args.port}"  # type: ignore[attr-defined]
    print(f"Nebula Bridge fixture listening on {server.public_base}", flush=True)  # type: ignore[attr-defined]
    server.serve_forever()


if __name__ == "__main__":
    main()
