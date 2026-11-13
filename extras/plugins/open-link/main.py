#!/usr/bin/env python3
"""PhoneBox test plugin: a link on the phone that opens a site in the computer's default browser.

The site comes from the plugin's settings on the computer (Settings › Plugins › Open link › Configure); the phone only
asks for the "open" action and never sends a URL. Protocol: one JSON object per line on stdin/stdout (docs/PLUGINS.md).
"""
import json
import os
import sys
import webbrowser
from urllib.parse import urlparse

settings = {}

# The protocol owns stdout. Programs started from here (the browser, xdg-open) inherit file descriptor 1 and may print
# to it, so keep a private copy for the protocol and point fd 1 at stderr, which ends up in the agent's log.
protocol = os.fdopen(os.dup(1), "w", encoding="utf-8")
os.dup2(2, 1)
sys.stdout = sys.stderr


def send(msg):
    protocol.write(json.dumps(msg, separators=(",", ":")) + "\n")
    protocol.flush()


def site():
    url = str(settings.get("url") or "").strip()
    parsed = urlparse(url)
    return (url, parsed.netloc) if parsed.scheme in ("http", "https") and parsed.netloc else (None, None)


def publish():
    url, host = site()
    send({"type": "state", "key": "link", "data": {
        "url": url, "host": host, "label": str(settings.get("label") or "Open site"),
        "error": None if url else "Set an http:// or https:// address in the plugin's settings",
    }})


def open_site(msg):
    url, _ = site()
    if not url:
        send({"type": "result", "id": msg["id"], "ok": False, "error": "No valid site in the plugin's settings"})
        return
    try:
        opened = webbrowser.open(url, new=2)  # new tab; Linux: xdg-open / $BROWSER, macOS: open, Windows: default browser
    except Exception as e:  # noqa: BLE001 - report whatever the browser launcher says
        opened, error = False, str(e)
    else:
        error = None if opened else "No browser could be started on the computer"
    send({"type": "log", "level": "info" if opened else "warn", "message": f"open {url}: {'ok' if opened else error}"})
    send({"type": "result", "id": msg["id"], "ok": opened, "error": error, "data": {"url": url}})


for line in sys.stdin:
    if not line.strip():
        continue
    msg = json.loads(line)
    kind = msg.get("type")
    if kind in ("init", "config"):
        settings = msg.get("settings") or {}
        publish()
    elif kind == "action":
        if msg.get("name") == "open":
            open_site(msg)
        else:
            send({"type": "result", "id": msg["id"], "ok": False, "error": "unknown action"})
    elif kind == "shutdown":
        break
