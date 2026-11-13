#!/usr/bin/env python3
"""PhoneBox plugin example: HTTP status.

The agent starts this program and talks to it with JSON lines (one JSON object per line):
  stdin  <- {"type": "init" | "config", "settings": {...}, "instances": [...]}, {"type": "action", ...}, {"type": "shutdown"}
  stdout -> {"type": "state", "key": ..., "data": ...}, {"type": "result", "id": ..., "ok": ...}, {"type": "notify", ...}, {"type": "log", ...}
Anything else (debug prints) goes to stderr, which ends up in the agent's log. See docs/PLUGINS.md.
"""
import json
import sys
import threading
import time
import urllib.error
import urllib.request

lock = threading.Lock()
config = {"settings": {}, "instances": []}
wake = threading.Event()
stop = threading.Event()
ready = threading.Event()  # set by the first "init"
last_up = {}  # url -> bool, to notify only when something changes


def send(msg):
    with lock:
        sys.stdout.write(json.dumps(msg, separators=(",", ":")) + "\n")
        sys.stdout.flush()


def log(message, level="info"):
    send({"type": "log", "level": level, "message": message})


def check(url, timeout, auth):
    started = time.monotonic()
    req = urllib.request.Request(url, method="GET", headers={"User-Agent": "PhoneBox-http-status/0.1"})
    if auth:
        req.add_header("Authorization", auth)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            code = r.status
    except urllib.error.HTTPError as e:
        code = e.code
    except Exception as e:  # DNS, refused, timeout, TLS…
        return {"label": url, "value": "down", "level": "error", "error": str(e)[:120], "ms": None}
    ms = round((time.monotonic() - started) * 1000)
    level = "ok" if code < 400 else "warn" if code < 500 else "error"
    return {"label": url, "value": f"{code} · {ms} ms", "code": code, "ms": ms, "level": level}


def instance_key(inst):
    # state keys allow letters, digits, '_', '.', '-'; page and widget ids are slugs, so this is always valid
    return f"{inst['page']}.{inst['id']}"


def check_all(only=None):
    with lock:
        settings = dict(config.get("settings") or {})
        instances = list(config.get("instances") or [])
    timeout = float(settings.get("timeout") or 5)
    auth = settings.get("authHeader") or ""
    up = total = 0
    for inst in instances:
        if inst.get("widget") != "endpoints" or (only and inst["instance"] != only):
            continue
        urls = inst.get("options", {}).get("urls") or []
        if isinstance(urls, str):
            urls = [u.strip() for u in urls.splitlines() if u.strip()]
        results = [check(u, timeout, auth) for u in urls[:20] if u.startswith(("http://", "https://"))]
        send({"type": "state", "key": instance_key(inst), "data": {
            "title": inst.get("options", {}).get("title") or "Endpoints",
            "items": results,
            "checkedAt": int(time.time() * 1000),
        }})
        for r in results:
            ok = r["level"] != "error"
            total += 1
            up += ok
            if settings.get("notifyDown", True) and last_up.get(r["label"]) is True and not ok:
                send({"type": "notify", "title": "Endpoint down", "text": r["label"], "level": "error"})
            last_up[r["label"]] = ok
    if not only:
        send({"type": "state", "key": "summary", "data": {
            "title": "Endpoints up", "value": f"{up}/{total}" if total else "—",
            "level": "ok" if total and up == total else "error" if total else None,
        }})


def checker():
    ready.wait()
    wake.clear()  # the "init" that set ready also set wake
    while not stop.is_set():
        try:
            check_all()
        except Exception as e:
            log(f"check failed: {e}", "error")
        with lock:
            interval = float((config.get("settings") or {}).get("interval") or 60)
        wake.wait(max(10.0, interval))
        wake.clear()


def main():
    threading.Thread(target=checker, daemon=True).start()
    for line in sys.stdin:
        if not line.strip():
            continue
        msg = json.loads(line)
        kind = msg.get("type")
        if kind in ("init", "config"):
            with lock:
                config["settings"] = msg.get("settings") or {}
                config["instances"] = msg.get("instances") or []
            if kind == "init":
                log(f"started for {len(config['instances'])} widget(s)")
                ready.set()
            wake.set()
        elif kind == "action":
            if msg.get("name") == "check":
                try:
                    check_all(only=msg.get("instance"))
                    send({"type": "result", "id": msg["id"], "ok": True})
                except Exception as e:
                    send({"type": "result", "id": msg["id"], "ok": False, "error": str(e)})
            else:
                send({"type": "result", "id": msg["id"], "ok": False, "error": "unknown action"})
        elif kind == "shutdown":
            break
    stop.set()
    wake.set()


if __name__ == "__main__":
    main()
