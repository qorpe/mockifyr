#!/usr/bin/env python3
"""Local demo runner — serves the live demo page + concept docs, executes whitelisted steps.

Security: binds 127.0.0.1 only; runs ONLY the fixed step names below (no arbitrary commands);
serves ONLY files from demo/docs (no path traversal).
Start:   python3 demo/runner.py     then open  http://localhost:7788
"""
import json
import os
import re
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qs

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
PORT = 7788

DEMO_STEPS = {
    "openapi-import", "payments-create", "payments-get", "payments-list", "key-quota",
    "order-ok", "order-bad", "near-miss", "webhook", "sms", "otp", "email",
    "grpc-descriptor", "grpc", "graphql", "graphql-messy", "ws", "scenario",
    "record-start", "record-drive", "record-snapshot", "record-import",
    "drift", "record-verify", "record-stop",
    "token", "clock-freeze", "clock-reset", "chaos-on", "chaos-probe", "chaos-off",
    "verify-stubs", "verify-traffic", "wipe", "rehearse",
}
DOC_NAME = re.compile(r"^[a-z0-9-]+(\.tr)?$")


def command_for(name: str):
    if name == "seed":
        return ["./demo/seed.sh"]
    if name in DEMO_STEPS:
        return ["./demo/demo.sh", name]
    return None


class Handler(BaseHTTPRequestHandler):
    def _send(self, status, body, ctype="application/json; charset=utf-8"):
        data = body if isinstance(body, bytes) else body.encode()
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        url = urlparse(self.path)
        if url.path in ("/", "/index.html"):
            page = (HERE / "demo-live.html").read_text()
            engine = os.environ.get("PAGE_ENGINE_URL")
            if engine:  # docker mode: the engine dot must probe the HOST-visible address
                page = page.replace("http://localhost:8080/__admin/health", engine)
            self._send(200, page.encode(), "text/html; charset=utf-8")
        elif url.path == "/doc":
            self._send(200, (HERE / "doc-viewer.html").read_bytes(), "text/html; charset=utf-8")
        elif url.path.startswith("/docs/"):
            name = url.path[len("/docs/"):].removesuffix(".md")
            if DOC_NAME.match(name) and (HERE / "docs" / f"{name}.md").is_file():
                self._send(200, (HERE / "docs" / f"{name}.md").read_bytes(),
                           "text/markdown; charset=utf-8")
            else:
                self._send(404, json.dumps({"error": "no such doc"}))
        elif url.path == "/health":
            self._send(200, json.dumps({"ok": True}))
        else:
            self._send(404, json.dumps({"error": "not found"}))

    def do_POST(self):
        if self.path == "/shutdown":
            self._send(200, json.dumps({"ok": True, "bye": True}))
            threading.Timer(0.5, lambda: subprocess.Popen(
                ["./demo/stop.sh"], cwd=REPO)).start()
            return
        if self.path != "/run":
            self._send(404, json.dumps({"error": "not found"}))
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(length) or b"{}")
            step = str(payload.get("step", ""))
        except Exception:
            self._send(400, json.dumps({"error": "bad request"}))
            return

        cmd = command_for(step)
        if cmd is None:
            self._send(400, json.dumps({"error": f"unknown step: {step}"}))
            return

        started = time.time()
        try:
            proc = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, timeout=300)
            out = proc.stdout + (("\n" + proc.stderr) if proc.stderr.strip() else "")
            self._send(200, json.dumps({
                "step": step, "exit": proc.returncode,
                "ms": int((time.time() - started) * 1000),
                "output": out[-40000:],
            }))
        except subprocess.TimeoutExpired:
            self._send(200, json.dumps({
                "step": step, "exit": -1,
                "ms": int((time.time() - started) * 1000),
                "output": "(timeout — 300 s)"}))

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    print(f"Mockifyr demo runner → http://localhost:{PORT}   (Ctrl+C ile durdur)")
    ThreadingHTTPServer((os.environ.get("RUNNER_BIND", "127.0.0.1"), PORT), Handler).serve_forever()
