"""Synthetic game API for container protocol checks; never shipped in production."""

import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import time

import msgpack

TOKEN = json.loads(Path("/app/appsettings.json").read_text())["LuxonApiOptions"][
    "Token"
]
RECORDS = {}
PREPARATIONS = 0
PREFIX = "/2.19.0_20220714193707/"


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def reply(self, status, body, content_type="application/json"):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/preparations":
            self.reply(200, str(PREPARATIONS).encode())
        elif self.path.startswith("/records/"):
            self.reply(
                200,
                json.dumps(RECORDS.get(int(self.path.rsplit("/", 1)[1]), 0)).encode(),
            )
        else:
            self.reply(200, b"{}")

    def do_POST(self):
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            chunks = []
            while True:
                size = int(self.rfile.readline().split(b";", 1)[0], 16)
                if size == 0:
                    self.rfile.readline()
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)
            body = b"".join(chunks)
        else:
            body = self.rfile.read(int(self.headers["Content-Length"]))
        if self.path == PREFIX + "heroparam/batch":
            global PREPARATIONS
            PREPARATIONS += 1
            actors = json.loads(body)
            if any(actor["viewerId"] == 999 for actor in actors):
                time.sleep(0.15)
                self.reply(503, b"{}")
                return
            heroes = [
                dict(
                    actorNr=a["actorNr"],
                    viewerId=a["viewerId"],
                    heroParamLists=[
                        [
                            dict(
                                characterId=10100101 + position,
                                level=80,
                                hp=1000,
                                attack=2000,
                                relativeHp=1.25,
                                position=position,
                                exAbilityLv=5,
                                weaponPassiveAbilityIds=[101, 102],
                            )
                            for position in range(1, 5)
                        ]
                        for _ in a["partySlots"]
                    ],
                )
                for a in actors
            ]
            self.reply(200, json.dumps(heroes).encode())
        elif self.path == PREFIX + "dungeon_record/record_multi":
            if self.headers.get("Authorization") != "Bearer " + TOKEN:
                self.reply(401, b"{}")
                return
            viewer = int(self.headers["Auth-ViewerId"])
            RECORDS[viewer] = RECORDS.get(viewer, 0) + 1
            if viewer == 998:
                self.reply(503, b"{}")
                return
            record = msgpack.unpackb(body, raw=False)
            record["test_api_viewer"] = viewer
            self.reply(
                200,
                msgpack.packb(record, use_bin_type=True),
                "application/octet-stream",
            )
        else:
            self.reply(404, b"{}")


ThreadingHTTPServer(("0.0.0.0", 8080), Handler).serve_forever()
