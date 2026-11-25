"""Exercise the published plugin-enabled light client against local Bee responses."""

import argparse
import contextlib
import http.server
import json
import os
import socket
import socketserver
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path


LIMIT = 1024 * 1024


class Bee(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        reference = self.path.rstrip("/").split("/")[-1]
        if self.path == f"/bee/bytes/{'a' * 64}":
            self.reply(200, b"\x00\x11\xff")
        elif self.path == f"/bee/bzz/{'a' * 64}/":
            self.reply(200, b"file")
        elif reference == "b" * 64:
            self.reply(404, b"missing")
        elif reference == "c" * 64:
            self.send_response(302)
            self.send_header("Location", f"/bee/bytes/{'a' * 64}")
            self.end_headers()
        elif reference == "d" * 64:
            self.reply(200, b"d" * LIMIT)
        elif reference == "e" * 64:
            self.send_response(200)
            self.send_header("Content-Length", str(LIMIT + 1))
            self.end_headers()
        elif reference == "f" * 64:
            self.send_response(200)
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
            with contextlib.suppress(ConnectionError):
                self.wfile.write(f"{LIMIT + 1:x}\r\n".encode() + b"f" * (LIMIT + 1) + b"\r\n0\r\n\r\n")
        else:
            self.reply(500, b"unexpected route")

    def reply(self, status, body):
        self.send_response(status)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class PendingCheckpoint(socketserver.BaseRequestHandler):
    def handle(self):
        # Leave TLS pending so every RPC in this smoke test runs before Ethereum sync.
        self.request.settimeout(60)
        with contextlib.suppress(ConnectionError, TimeoutError):
            while self.request.recv(4096):
                pass


@contextlib.contextmanager
def serve(handler):
    with socketserver.ThreadingTCPServer(("127.0.0.1", 0), handler) as server:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            yield server.server_address[1]
        finally:
            server.shutdown()


def main(assembly, scratch):
    with serve(Bee) as bee, serve(PendingCheckpoint) as checkpoint, tempfile.TemporaryDirectory(dir=scratch) as directory:
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            rpc_port = listener.getsockname()[1]
        url = f"http://127.0.0.1:{rpc_port}"
        log = Path(directory) / "client.log"
        environment = os.environ | {"Swarm__BeeUrl": f"http://127.0.0.1:{bee}/bee"}
        with log.open("w") as output, subprocess.Popen(
            ["dotnet", str(assembly), "--urls", url, "--data-dir", directory,
             "--checkpoint-url", f"https://127.0.0.1:{checkpoint}"],
            stdout=output, stderr=subprocess.STDOUT, env=environment,
        ) as process:
            try:
                check_rpc(url, process)
            except Exception:
                print(log.read_text(encoding="utf-8", errors="replace"))
                raise
            finally:
                if process.poll() is None:
                    process.terminate()
    print("Swarm RPC smoke checks passed.")


def check_rpc(url, process):
    deadline = time.monotonic() + 15
    while True:
        try:
            with urllib.request.urlopen(f"{url}/status", timeout=1) as response:
                assert not json.load(response)["rpcInitialized"]
                break
        except (urllib.error.URLError, TimeoutError):
            if process.poll() is not None or time.monotonic() >= deadline:
                raise
            time.sleep(0.1)

    def call(method, params):
        request = urllib.request.Request(
            url, json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode(),
            {"Content-Type": "application/json"},
        )
        with urllib.request.urlopen(request, timeout=5) as response:
            return json.load(response)

    assert call("eth_chainId", [])["result"] == "0x1"
    assert call("swarm_getBytes", ["0x" + "a" * 64])["result"] == "0x0011ff"
    assert call("swarm_getFile", ["a" * 64])["result"] == "0x66696c65"
    assert call("swarm_getBytes", ["d" * 64])["result"] == "0x" + "64" * LIMIT
    for reference, code in [("https://example.com", -32602), ("b" * 64, -32001),
                            ("c" * 64, -32002), ("e" * 64, -32005), ("f" * 64, -32005)]:
        response = call("swarm_getBytes", [reference])
        assert response["error"]["code"] == code, response


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assembly", type=Path)
    parser.add_argument("--scratch", type=Path)
    options = parser.parse_args()
    main(options.assembly.resolve(), options.scratch)
