"""Loopback-only server for the macOS launcher.

Serves the standalone app from 127.0.0.1 so every browser treats the page
as a secure context (camera access works, including in Safari), and adds a
small save bridge: a native folder picker and writing finished PDFs into the
folder the user picked.

The bridge is locked down:
- requests must carry the per-launch token that is injected into the page,
  arrive with Host 127.0.0.1:<port> (defeats DNS rebinding) and, when the
  browser sends them, a same-origin Origin / Sec-Fetch-Site;
- files are written only into folders chosen through the native picker (or
  one procedure-type subfolder level inside them), only as new `.pdf` files
  (never overwriting), and only with PDF content.

The server exits on its own after a period without requests; an open page
sends a heartbeat so saving keeps working while it is in use.
"""
import http.server
import json
import os
import re
import secrets
import subprocess
import sys
import threading
import time
import unicodedata
import urllib.parse

PING_PATH = "/.document-scanner-ping"
PING_BODY = b"document-scanner"
IDLE_SECONDS = 15 * 60
MAX_PDF_BYTES = 300 * 1024 * 1024
# Kept from the app's old name (Document Scanner) so saved Save-to folders survive the rename to Life-scanner.
CONFIG_DIR = os.path.expanduser("~/Library/Application Support/Document Scanner")
CONFIG_PATH = os.path.join(CONFIG_DIR, "bridge.json")

app_dir = os.path.abspath(sys.argv[1])
port = int(sys.argv[2])
origin = f"http://127.0.0.1:{port}"
host = f"127.0.0.1:{port}"
token = secrets.token_urlsafe(32)
last_request = time.monotonic()
lock = threading.Lock()
saved_files = set()


def load_config():
    try:
        with open(CONFIG_PATH, encoding="utf-8") as f:
            data = json.load(f)
        folders = [p for p in data.get("folders", []) if isinstance(p, str)]
        last = data.get("last") if isinstance(data.get("last"), str) else None
        return {"folders": folders, "last": last}
    except (OSError, ValueError):
        return {"folders": [], "last": None}


def save_config(cfg):
    os.makedirs(CONFIG_DIR, exist_ok=True)
    tmp = CONFIG_PATH + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    os.replace(tmp, CONFIG_PATH)


def folder_info(path):
    return {"path": path, "name": os.path.basename(path.rstrip("/")) or path}


def allowed_folder(path):
    if not path:
        return None
    real = os.path.realpath(path)
    cfg = load_config()
    if real in cfg["folders"] and os.path.isdir(real):
        return real
    return None


def choose_folder():
    cfg = load_config()
    script = [
        "on run argv",
        "tell me to activate",
        "set prompt_text to \"Choose where to save scanned PDFs\"",
        "if (count of argv) > 0 then",
        "set f to choose folder with prompt prompt_text default location (POSIX file (item 1 of argv))",
        "else",
        "set f to choose folder with prompt prompt_text",
        "end if",
        "return POSIX path of f",
        "end run",
    ]
    args = ["/usr/bin/osascript"]
    for line in script:
        args += ["-e", line]
    if cfg["last"] and os.path.isdir(cfg["last"]):
        args.append(cfg["last"])
    result = subprocess.run(args, capture_output=True, text=True, timeout=600)
    if result.returncode != 0:
        return None
    real = os.path.realpath(result.stdout.strip())
    if not os.path.isdir(real):
        return None
    with lock:
        cfg = load_config()
        if real not in cfg["folders"]:
            cfg["folders"].append(real)
        cfg["last"] = real
        save_config(cfg)
    return real


def safe_pdf_name(name):
    name = unicodedata.normalize("NFC", os.path.basename(name or "")).strip()
    name = "".join("_" if c in '/\\:*?"<>|' or ord(c) < 32 else c for c in name)
    name = name.lstrip(".").rstrip(". ")
    if not name.lower().endswith(".pdf"):
        name += ".pdf"
    if name.lower() == ".pdf":
        name = "document.pdf"
    return name[:120]


def safe_dir_name(name):
    name = unicodedata.normalize("NFC", name or "")
    name = "".join("_" if c in '/\\:*?"<>|' or ord(c) < 32 else c for c in name)
    return name.strip(". \t")[:80]


def procedure_folder(folder, sub):
    """One subfolder level inside an allowed folder: reuse a case-insensitive match or create it."""
    name = safe_dir_name(sub)
    if not name:
        return folder
    wanted = name.casefold()
    for entry in os.scandir(folder):
        if entry.is_dir(follow_symlinks=False) and unicodedata.normalize("NFC", entry.name).casefold() == wanted:
            return entry.path
    target = os.path.join(folder, name)
    os.mkdir(target)
    return target


def create_new(target, data):
    """Write data to target only if it does not exist yet; False when the name is taken."""
    try:
        fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o644)
    except FileExistsError:
        return False
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
    except BaseException:
        os.unlink(target)
        raise
    return True


def write_unique(folder, name, data):
    base = name[:-4]
    for i in range(1, 1000):
        target = os.path.join(folder, name if i == 1 else f"{base} ({i}).pdf")
        if create_new(target, data):
            return target
    raise OSError("no free file name")


def write_sequenced(folder, base, data):
    """Save as <base>_<n>.pdf, continuing after the highest number already in the folder."""
    pattern = re.compile(re.escape(base.casefold()) + r"_(\d+)\.pdf")
    highest = 0
    for entry in os.scandir(folder):
        match = pattern.fullmatch(unicodedata.normalize("NFC", entry.name).casefold())
        if match:
            highest = max(highest, int(match.group(1)))
    for n in range(highest + 1, highest + 1000):
        target = os.path.join(folder, f"{base}_{n}.pdf")
        if create_new(target, data):
            return target
    raise OSError("no free file name")


def page_html():
    with open(os.path.join(app_dir, "index.html"), "rb") as f:
        html = f.read().decode("utf-8")
    csp_old = "connect-src 'none'"
    if html.count(csp_old) != 1 or html.count("<head>") != 1:
        return html.encode("utf-8")
    # Same-origin fetches only, so the page can reach this bridge and nothing else.
    html = html.replace(csp_old, "connect-src 'self'")
    html = html.replace("<head>", f'<head>\n  <meta name="scanner-bridge" content="{token}">', 1)
    return html.encode("utf-8")


class Handler(http.server.BaseHTTPRequestHandler):
    def send_body(self, code, body, content_type):
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(body)

    def send_json(self, code, obj):
        self.send_body(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8")

    def touch(self):
        global last_request
        last_request = time.monotonic()

    def do_GET(self):
        self.touch()
        if self.headers.get("Host") != host:
            self.send_error(403)
            return
        path = self.path.split("?", 1)[0]
        if path == PING_PATH:
            self.send_body(200, PING_BODY, "text/plain")
        elif path in ("/", "/index.html"):
            self.send_body(200, page_html(), "text/html; charset=utf-8")
        else:
            self.send_error(404)

    def authorized(self):
        if self.headers.get("Host") != host:
            return False
        if not secrets.compare_digest(self.headers.get("X-Scanner-Token", ""), token):
            return False
        if self.headers.get("Origin") not in (None, origin):
            return False
        if self.headers.get("Sec-Fetch-Site") not in (None, "same-origin"):
            return False
        return True

    def do_POST(self):
        self.touch()
        if not self.authorized():
            self.send_json(403, {"error": "forbidden"})
            return
        url = urllib.parse.urlsplit(self.path)
        params = dict(urllib.parse.parse_qsl(url.query))
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = -1
        if length < 0 or length > MAX_PDF_BYTES:
            self.send_json(413, {"error": "too large"})
            return
        body = self.rfile.read(length) if length else b""
        try:
            if url.path == "/api/state":
                cfg = load_config()
                last = allowed_folder(cfg["last"])
                self.send_json(200, {"folder": folder_info(last) if last else None})
            elif url.path == "/api/choose-folder":
                folder = choose_folder()
                self.send_json(200, {"folder": folder_info(folder) if folder else None})
            elif url.path == "/api/clear-folder":
                with lock:
                    cfg = load_config()
                    cfg["last"] = None
                    save_config(cfg)
                self.send_json(200, {"ok": True})
            elif url.path == "/api/save":
                folder = allowed_folder(params.get("folder"))
                if not folder:
                    self.send_json(400, {"error": "folder not allowed"})
                    return
                if not body.startswith(b"%PDF-"):
                    self.send_json(400, {"error": "not a pdf"})
                    return
                folder = procedure_folder(folder, params.get("sub"))
                if "auto" in params:
                    base = safe_pdf_name(params["auto"])[:-4][:100] or "document"
                    target = write_sequenced(folder, base, body)
                else:
                    target = write_unique(folder, safe_pdf_name(params.get("name")), body)
                saved_files.add(target)
                self.send_json(200, {"path": target, "name": os.path.basename(target)})
            elif url.path == "/api/reveal":
                target = params.get("path")
                if target not in saved_files or not os.path.exists(target):
                    self.send_json(404, {"error": "unknown file"})
                    return
                subprocess.run(["/usr/bin/open", "-R", target], timeout=20)
                self.send_json(200, {"ok": True})
            else:
                self.send_json(404, {"error": "not found"})
        except (OSError, subprocess.SubprocessError) as e:
            self.send_json(500, {"error": str(e)})

    def log_message(self, *args):
        pass


server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)


def stop_when_idle():
    while time.monotonic() - last_request < IDLE_SECONDS:
        time.sleep(30)
    server.shutdown()


threading.Thread(target=stop_when_idle, daemon=True).start()
server.serve_forever()
