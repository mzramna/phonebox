# Open link: a test plugin

The smallest useful PhoneBox plugin: one setting (the site), one widget (the link on the phone) and one action. Tapping
the widget opens the site **in the computer's default browser**. Good for checking that plugins work end to end.

- `plugin.json`: the settings `url` and `label`, the widget `link` and its action `open`.
- `main.py`: publishes the link for the widget and opens the site with Python's `webbrowser` (Linux: `xdg-open` or
  `$BROWSER`; macOS: `open`; Windows: the default browser). Python 3, standard library only.
- `ui/link.html`: the widget, a big button.

The phone never sends an address: it only asks for `open`, and the site comes from the settings on the computer.

## Try it

1. Zip this folder (or use `open-link.zip` from a test build) and add it in **Settings › Plugins › Add plugin…**.
2. Click **Open link** › **Configure**, set the site and save; then **Turn on**.
3. Add the **Open link** widget (category *Plugins*) to a page and tap it on the phone.

By hand, without the agent (`BROWSER=echo` prints the address instead of opening a browser):

```bash
printf '%s\n' '{"type":"init","settings":{"url":"https://example.com"}}' '{"type":"action","id":"1","name":"open"}' | BROWSER=echo python3 main.py
```
