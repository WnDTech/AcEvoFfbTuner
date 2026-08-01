#!/usr/bin/env python3
"""AC EVO FFB Tuner - Discord feedback relay.

Bridges the app's one-way Discord webhook to two-way communication:

  - Reads replies in per-report Discord threads (via a bot token)
  - Serves them to the app over HTTP for polling
  - Posts user replies from the app back into the thread (via the stored webhook)

All endpoints require the shared secret in the `X-Relay-Token` header.

Run:
    set DISCORD_BOT_TOKEN=your-bot-token
    set RELAY_SECRET=your-shared-secret
    python discord_feedback_relay.py

Optional env vars:
    RELAY_HOST (default 0.0.0.0)
    RELAY_PORT (default 8090)
    POLL_INTERVAL_SECONDS (default 20)
"""

import collections
import datetime
import json
import os
import re
import sqlite3
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

BOT_TOKEN = os.environ.get("DISCORD_BOT_TOKEN", "")
RELAY_SECRET = os.environ.get("RELAY_SECRET", "")
RELAY_HOST = os.environ.get("RELAY_HOST", "127.0.0.1")
RELAY_PORT = int(os.environ.get("RELAY_PORT", "8090"))
POLL_INTERVAL = int(os.environ.get("POLL_INTERVAL_SECONDS", "20"))
ALLOW_REMOTE = os.environ.get("RELAY_ALLOW_REMOTE", "").lower() in ("1", "true", "yes")
LOOPBACK_ADDRESSES = ("127.0.0.1", "::1")
DB_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "relay.db")
DISCORD_API = "https://discord.com/api/v10"
DISCORD_USER_AGENT = "DiscordBot (https://github.com/ACEVO-FFB-Tuner/AcEvoFfbTuner, 1.0.0)"
FIX_PREFIX = re.compile(r"^\s*\[FIX\]", re.IGNORECASE)

MAX_BODY_BYTES = 64 * 1024
RATE_WINDOW_SECONDS = 10.0
RATE_MAX_PER_WINDOW = 30
MAX_POLL_PAGES = 20
PAGE_SIZE = 100

_db_lock = threading.Lock()
_rate = collections.defaultdict(list)
_rate_lock = threading.Lock()
_db = None


def db():
    return _db


def init_db():
    global _db
    _db = sqlite3.connect(DB_PATH, timeout=10, check_same_thread=False)
    with _db_lock:
        _db.execute(
            "CREATE TABLE IF NOT EXISTS reports ("
            " report_id TEXT PRIMARY KEY, thread_id TEXT NOT NULL, channel_id TEXT NOT NULL,"
            " webhook_url TEXT NOT NULL, created_at TEXT NOT NULL)")
        _db.execute(
            "CREATE TABLE IF NOT EXISTS messages ("
            " id TEXT PRIMARY KEY, report_id TEXT NOT NULL, author TEXT NOT NULL,"
            " content TEXT NOT NULL, at TEXT NOT NULL, fix INTEGER NOT NULL DEFAULT 0,"
            " created_at TEXT NOT NULL, updated_at TEXT NOT NULL)")
        _db.commit()


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def discord_get(url):
    req = urllib.request.Request(url, headers={
        "Authorization": f"Bot {BOT_TOKEN}",
        "User-Agent": DISCORD_USER_AGENT})
    with urllib.request.urlopen(req, timeout=15) as resp:
        return json.loads(resp.read().decode("utf-8"))


def post_json(url, payload, headers=None):
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers={
        "Content-Type": "application/json",
        "User-Agent": DISCORD_USER_AGENT,
        **({} if headers is None else headers)})
    with urllib.request.urlopen(req, timeout=15) as resp:
        body = resp.read().decode("utf-8")
        return resp.status, json.loads(body) if body else {}


def store_messages(report_id, messages):
    now = utc_now()
    with _db_lock:
        for msg in messages:
            if "webhook_id" in msg:
                continue
            mid = str(msg.get("id", ""))
            if not mid:
                continue
            author = (msg.get("author") or {}).get("username", "Unknown")
            content = msg.get("content", "") or ""
            at = msg.get("timestamp", "") or ""
            fix = 1 if FIX_PREFIX.match(content) else 0
            row = _db.execute(
                "SELECT content, fix FROM messages WHERE id = ?", (mid,)).fetchone()
            if row is None:
                _db.execute(
                    "INSERT INTO messages"
                    " (id, report_id, author, content, at, fix, created_at, updated_at)"
                    " VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                    (mid, report_id, author, content, at, fix, now, now))
            elif row[0] != content or row[1] != fix:
                _db.execute(
                    "UPDATE messages SET author = ?, content = ?, fix = ?, updated_at = ?"
                    " WHERE id = ?",
                    (author, content, fix, now, mid))
        _db.commit()


def poll_threads():
    while True:
        try:
            with _db_lock:
                rows = _db.execute("SELECT report_id, thread_id FROM reports").fetchall()
            for report_id, thread_id in rows:
                try:
                    with _db_lock:
                        r = _db.execute(
                            "SELECT MAX(id) FROM messages WHERE report_id = ?",
                            (report_id,)).fetchone()
                    newest_known = r[0] if r else None

                    before = None
                    for _page in range(MAX_POLL_PAGES):
                        url = f"{DISCORD_API}/channels/{thread_id}/messages?limit={PAGE_SIZE}"
                        if before:
                            url += f"&before={before}"
                        messages = discord_get(url)
                        if not messages:
                            break
                        store_messages(report_id, messages)
                        oldest_id = str(messages[-1].get("id", ""))
                        if newest_known and oldest_id <= newest_known:
                            break
                        before = oldest_id
                except urllib.error.HTTPError as e:
                    if e.code == 404:
                        with _db_lock:
                            _db.execute(
                                "DELETE FROM reports WHERE report_id = ?", (report_id,))
                            _db.commit()
                        print(f"[poll {report_id}] thread deleted, registration removed")
                    else:
                        print(f"[poll {report_id}] HTTP {e.code}: {e.read().decode('utf-8', 'replace')[:200]}")
                except Exception as e:
                    print(f"[poll {report_id}] error: {e}")
        except Exception as e:
            print(f"[poll loop] error: {e}")
        time.sleep(POLL_INTERVAL)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def _send(self, code, body):
        data = json.dumps(body).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _authorized(self):
        if not ALLOW_REMOTE and self.client_address[0] not in LOOPBACK_ADDRESSES:
            self._send(403, {"error": "remote access disabled"})
            return False
        if self.headers.get("X-Relay-Token") != RELAY_SECRET:
            self._send(401, {"error": "unauthorized"})
            return False
        return True

    def _rate_ok(self):
        ip = self.client_address[0]
        now = time.time()
        with _rate_lock:
            recent = [t for t in _rate[ip] if now - t < RATE_WINDOW_SECONDS]
            _rate[ip] = recent
            if len(recent) >= RATE_MAX_PER_WINDOW:
                self._send(429, {"error": "rate limited"})
                return False
            recent.append(now)
            return True

    def _read_json(self):
        length = int(self.headers.get("Content-Length", 0))
        if length <= 0:
            return {}
        if length > MAX_BODY_BYTES:
            self._send(413, {"error": "body too large"})
            return None
        return json.loads(self.rfile.read(length).decode("utf-8"))

    def do_GET(self):
        if not self._authorized() or not self._rate_ok():
            return
        path = urllib.parse.urlparse(self.path)
        match = re.match(r"^/replies/([A-Z0-9-]+)$", path.path)
        if not match:
            self._send(404, {"error": "not found"})
            return

        report_id = match.group(1)
        query = urllib.parse.parse_qs(path.query)
        after = query.get("after", [None])[0]
        edited_after = query.get("edited_after", [None])[0]

        conditions = []
        params = [report_id]
        if after:
            conditions.append("id > ?")
            params.append(after)
        if edited_after:
            conditions.append("updated_at > ?")
            params.append(edited_after)
        sql = "SELECT id, author, content, at, fix FROM messages WHERE report_id = ?"
        if conditions:
            sql += f" AND ({' OR '.join(conditions)})"
        sql += " ORDER BY id"

        try:
            with _db_lock:
                rows = _db.execute(sql, params).fetchall()
        except sqlite3.Error as e:
            self._send(500, {"error": str(e)})
            return

        self._send(200, [
            {"id": r[0], "author": r[1], "content": r[2], "at": r[3], "isFix": bool(r[4])}
            for r in rows
        ])

    def do_POST(self):
        if not self._authorized() or not self._rate_ok():
            return
        path = urllib.parse.urlparse(self.path).path
        payload = self._read_json()
        if payload is None:
            return

        if path == "/register":
            report_id = payload.get("reportId", "")
            thread_id = payload.get("threadId", "")
            channel_id = payload.get("channelId", "")
            webhook_url = payload.get("webhookUrl", "")
            if not all([report_id, thread_id, channel_id, webhook_url]):
                self._send(400, {"error": "missing fields"})
                return
            with _db_lock:
                _db.execute(
                    "INSERT OR REPLACE INTO reports"
                    " (report_id, thread_id, channel_id, webhook_url, created_at)"
                    " VALUES (?, ?, ?, ?, ?)",
                    (report_id, thread_id, channel_id, webhook_url, utc_now()))
                _db.commit()
            self._send(200, {"ok": True})

        elif path == "/reply":
            report_id = payload.get("reportId", "")
            content = (payload.get("content", "") or "").strip()
            if not report_id or not content:
                self._send(400, {"error": "missing fields"})
                return
            with _db_lock:
                row = _db.execute(
                    "SELECT webhook_url, thread_id FROM reports WHERE report_id = ?",
                    (report_id,)).fetchone()
            if not row:
                self._send(404, {"error": "unknown report"})
                return
            webhook_url, thread_id = row
            if len(content) > 2000:
                content = content[:2000]
            try:
                status, body = post_json(
                    f"{webhook_url}?thread_id={thread_id}", {"content": content})
                if status == 204:
                    self._send(200, {"ok": True})
                else:
                    self._send(502, {"error": "discord rejected reply", "detail": body})
            except urllib.error.HTTPError as e:
                self._send(502, {"error": f"discord HTTP {e.code}",
                                 "detail": e.read().decode("utf-8", "replace")[:300]})
            except Exception as e:
                self._send(502, {"error": str(e)})

        else:
            self._send(404, {"error": "not found"})

    def do_OPTIONS(self):
        self.send_response(204)
        self.end_headers()


def main():
    if not BOT_TOKEN:
        print("ERROR: set DISCORD_BOT_TOKEN environment variable first")
        return
    if not RELAY_SECRET:
        print("ERROR: set RELAY_SECRET environment variable first")
        return
    init_db()
    threading.Thread(target=poll_threads, daemon=True).start()
    server = ThreadingHTTPServer((RELAY_HOST, RELAY_PORT), Handler)
    mode = "loopback only" if not ALLOW_REMOTE else "REMOTE ACCESS ENABLED"
    print(f"Relay listening on http://{RELAY_HOST}:{RELAY_PORT} (token auth, {mode})")
    server.serve_forever()


if __name__ == "__main__":
    main()
