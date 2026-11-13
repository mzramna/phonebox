# HTTP status: an example PhoneBox plugin

Checks that web sites and APIs answer and shows their status code and response time. It is small on purpose: it
shows every part of a plugin (manifest, backend, custom UI, built-in view, settings, an action and a notification).
See [docs/PLUGINS.md](../../../docs/PLUGINS.md) for the full guide.

| File | What it is |
|---|---|
| `plugin.json` | The manifest: settings, two widgets, the `check` action and the `notify` permission |
| `main.py` | The backend: reads JSON lines on stdin, checks the URLs, writes state on stdout (Python 3, standard library only) |
| `ui/endpoints.html` | The *HTTP status* widget's UI, shown in a sandboxed iframe on the phone |

The *HTTP status summary* widget has no HTML at all: it uses the built-in `view` (like a *Pushed value* widget).

## Install

1. Copy this folder into the agent's plugins folder:
   - Windows: `%APPDATA%\PhoneBox\plugins\http-status`
   - Linux: `~/.config/PhoneBox/plugins/http-status`
   - macOS: `~/Library/Application Support/PhoneBox/plugins/http-status`
2. Settings › Plugins › **Rescan**, turn **HTTP status** on and save.
3. Add the *HTTP status* widget to a page (category *Plugins*) and list the URLs to check.

Python 3 must be installed (`python3` on Linux and macOS, the `py` launcher on Windows).

## Try the backend by hand

```bash
printf '%s\n' '{"type":"init","settings":{"timeout":5},"instances":[{"instance":"home/h","page":"home","id":"h","widget":"endpoints","options":{"urls":["https://example.com"]}}]}' | python3 main.py
```
