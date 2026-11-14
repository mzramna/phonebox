using System.Security.Cryptography;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// Web kiosk: the phone's panel in a browser, for devices without the phone app (a tablet, a spare monitor, a Raspberry Pi
/// screen, Fully Kiosk Browser). Served only on the Wi-Fi / Tailscale HTTPS port. /kiosk/?token=… trades the key for a
/// cookie and redirects to /kiosk/ without it; the page then calls the panel API (/agent/api/…) with that cookie.
/// Browsers without container queries or modern JavaScript, down to Firefox 68 on Android 4.4, get /kiosk/lite/: the
/// same panel built for them by tools/kiosk-lite (kiosk-lite/ here).
///
/// A kiosk is read only unless kiosk.allowControl is on, and never answers Claude Code prompts unless
/// kiosk.allowApprovals is on. It never reaches the webhook API, the phone's battery report or "end process".
/// The kiosk key is its own (URLs end up in logs and bookmarks, so it is never the phone's key); the cookie holds a
/// value derived from it with an expiry, not the key.
/// </summary>
public static class Kiosk
{
    public const string CookieName = "phonebox_kiosk";

    /// <summary>What a kiosk request would do.</summary>
    public enum Access { Never, Read, Control, Approvals }

    /// <summary>A kiosk request, captured once so that long responses can be checked again later from another thread.</summary>
    public sealed record Request(PathString Path, string Method, string? Cookie, bool PanelHeader, bool Force, bool Page);

    public static Request Capture(HttpRequest req) => new(req.Path, req.Method, req.Cookies[CookieName],
        req.Headers.ContainsKey("X-PhoneBox-Panel"), req.Query["force"] is { Count: > 0 } f && f.ToString() is "1" or "true", IsKioskPath(req.Path));

    /// <summary>Read-only images the panel loads with &lt;img&gt;, which cannot carry the panel header (like the phone's proxy).</summary>
    private static readonly HashSet<string> Images = ["/api/art", "/api/procs/icon"];

    private static readonly HashSet<string> ReadExact =
    [
        "/api/procs", "/api/procs/icon", "/api/procs/detail", "/api/docker", "/api/obs", "/api/batteries",
        "/api/controls/state", "/api/controls/obs", "/api/controls/spotify/state",
    ];
    // /api/push: reading the pushed values the push widgets show; sending or deleting them (the webhook API) is never allowed
    private static readonly string[] ReadPrefixes =
        ["/api/state", "/api/stream", "/api/config", "/api/art", "/api/calendar", "/api/work", "/api/quotas", "/api/ha/states", "/api/push"];
    private static readonly string[] ControlPostPrefixes =
        ["/api/run", "/api/media", "/api/controls/command", "/api/controls/obs", "/api/controls/spotify/action", "/api/ha/toggle"];

    public static bool IsKioskPath(PathString path) => path.StartsWithSegments("/kiosk");

    /// <summary>
    /// Sorts a panel API call (path already rewritten from /agent/api). Everything not listed is Never: settings, pairing,
    /// tests, the webhook API (sending to push / notify / goto / refresh, deleting pushed values), the phone's battery report
    /// and ending processes. Reading the pushed values (GET /api/push) is Read: the push widgets show them.
    /// </summary>
    public static Access Classify(PathString path, string method, bool force = false)
    {
        var p = path.Value ?? "";
        bool Under(string prefix) => path.StartsWithSegments(prefix);
        if (Under("/api/approvals")) return HttpMethods.IsGet(method) || HttpMethods.IsPost(method) ? Access.Approvals : Access.Never;
        if (HttpMethods.IsGet(method))
        {
            if (Under("/api/widget")) return force ? Access.Control : Access.Read; // force=1 runs the command widget again
            if (p == "/api/controls/listen") return Access.Control;                // listens to the computer's audio
            return ReadExact.Contains(p) || ReadPrefixes.Any(Under) ? Access.Read : Access.Never;
        }
        if (!HttpMethods.IsPost(method)) return Access.Never;
        if (ControlPostPrefixes.Any(Under) || p is "/api/volume" or "/api/rules/pause") return Access.Control;
        // /api/docker/{id}/{action} and /api/obs/{action}, not the lists themselves
        if ((Under("/api/docker") && p != "/api/docker") || (Under("/api/obs") && p != "/api/obs")) return Access.Control;
        return Access.Never;
    }

    /// <summary>
    /// Whether the kiosk may make this panel API call: read only unless allowControl, approvals only with allowApprovals,
    /// and only from the panel's own scripts, which send X-PhoneBox-Panel (a sandboxed Custom HTML widget cannot add it
    /// without a preflight), except the read-only images.
    /// </summary>
    public static bool ApiAllowed(Request r, KioskConfig kiosk)
    {
        if (!r.PanelHeader && !(HttpMethods.IsGet(r.Method) && Images.Contains(r.Path.Value ?? ""))) return false;
        return Classify(r.Path, r.Method, r.Force) switch
        {
            Access.Read => true,
            Access.Control => kiosk.AllowControl,
            Access.Approvals => kiosk.AllowApprovals,
            _ => false,
        };
    }

    /// <summary>Same-origin requests only: fetch POSTs from the page itself send Origin, other sites are refused.</summary>
    public static bool OriginAllowed(string origin, string scheme, string host) =>
        origin == "" || string.Equals(origin, $"{scheme}://{host}", StringComparison.OrdinalIgnoreCase);

    /// <summary>The kiosk key in ?token=, compared in constant time.</summary>
    public static bool KeyMatches(string? given, KioskConfig kiosk) =>
        kiosk.Enabled && !string.IsNullOrEmpty(kiosk.Token) && Lan.MatchesQuery(given, kiosk.Token);

    /// <summary>
    /// The cookie: "expiry.HMAC-SHA256(key, expiry)". It proves the browser was given the key without holding it, expires
    /// on the agent's side too, and stops working as soon as the key is replaced or removed.
    /// </summary>
    public static string CookieValue(string token, DateTimeOffset expires)
    {
        var exp = expires.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return exp + "." + Mac(token, exp);
    }

    public static bool CookieValid(string? cookie, KioskConfig kiosk, DateTimeOffset now)
    {
        if (!kiosk.Enabled || string.IsNullOrEmpty(kiosk.Token) || string.IsNullOrEmpty(cookie)) return false;
        var dot = cookie.IndexOf('.');
        if (dot <= 0 || !long.TryParse(cookie.AsSpan(0, dot), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var exp)) return false;
        if (exp <= now.ToUnixTimeSeconds() || exp > now.AddDays(401).ToUnixTimeSeconds()) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(cookie[(dot + 1)..]), Encoding.ASCII.GetBytes(Mac(kiosk.Token, cookie[..dot])));
    }

    private static string Mac(string token, string exp) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes("phonebox-kiosk|" + exp)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether a request that was let in may go on: the kiosk is still on, the cookie still valid, the call still allowed.</summary>
    public static bool StillAllowed(Request r, KioskConfig kiosk, DateTimeOffset now) =>
        CookieValid(r.Cookie, kiosk, now) && (r.Page || ApiAllowed(r, kiosk));

    /// <summary>
    /// Long responses (the live stream, audio listening) are checked again every second and cut off when the kiosk is
    /// turned off, its key replaced or the permission they need taken away.
    /// </summary>
    public static IDisposable Watch(HttpContext ctx, Request r, Func<KioskConfig> current) =>
        new Timer(_ =>
        {
            try { if (!StillAllowed(r, current(), DateTimeOffset.UtcNow)) ctx.Abort(); }
            catch { /* the request is over */ }
        }, null, 1000, 1000);

    /// <summary>
    /// Sent first in the panel's &lt;head&gt;: browsers without container queries or optional chaining go to the lite panel.
    /// ES5 on purpose, so that old browsers can run it. ?full=1 skips the check. The query is not carried over.
    /// </summary>
    public const string LegacyCheck = """
        <script>(function(){if(/[?&]full=1/.test(location.search))return;var ok=false;try{ok=!!(window.CSS&&CSS.supports&&CSS.supports('width','1cqw'))&&!!new Function('a','return a?.b??1');}catch(e){ok=false;}if(!ok)location.replace('lite/');})();</script>
        """;

    /// <summary>A file of the phone's panel (embedded at build time): index.html, main.js, style.css… and widgets/*.js.</summary>
    public static IResult PanelFile(string? file, string folder = "www")
    {
        file = string.IsNullOrEmpty(file) ? "index.html" : file;
        if (!System.Text.RegularExpressions.Regex.IsMatch(file, @"^(widgets/)?[A-Za-z0-9_.-]+$") || file.Contains("..")) return Results.NotFound();
        var type = Path.GetExtension(file) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            _ => "application/octet-stream",
        };
        return AppPaths.Resource(folder + "/" + file) is { } s ? Results.Stream(s, type) : Results.NotFound();
    }

    /// <summary>The panel's index.html with the legacy-browser check in front of everything else.</summary>
    public static string PanelIndex()
    {
        using var s = AppPaths.Resource("www/index.html")!;
        var html = new StreamReader(s).ReadToEnd();
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? LegacyCheck + html : html.Insert(head + "<head>".Length, "\n" + LegacyCheck);
    }

    /// <summary>The kiosk cookie: HttpOnly, Secure, and SameSite=Strict so other sites never send it.</summary>
    public static void SetCookie(HttpResponse response, string value, DateTimeOffset expires) =>
        response.Cookies.Append(CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
        });

    /// <summary>Shown instead of the panel when the browser has no valid kiosk cookie.</summary>
    public const string SignInAgain = """
        <!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>PhoneBox kiosk</title>
        <body style="font:18px sans-serif;background:#0b0d12;color:#e8eaf0;padding:24px;line-height:1.5">
        <b>This browser is not signed in to the PhoneBox kiosk.</b><br>Open the kiosk address from the agent's settings
        (Settings › General › Web kiosk › Copy address) on this device again.</body>
        """;

    public static void MapKiosk(this WebApplication app)
    {
        // The phone's pages, unchanged; /kiosk/lite/ the same panel built for older browsers (tools/kiosk-lite).
        // Relative URLs (style.css, main.js, widgets/…) need the trailing slash.
        app.MapGet("/kiosk/{*file}", (string? file, HttpRequest req) =>
        {
            if (string.IsNullOrEmpty(file) && !req.Path.Value!.EndsWith('/')) return Results.Redirect("/kiosk/" + req.QueryString);
            if (file == "lite") return Results.Redirect("/kiosk/lite/" + req.QueryString);
            if (file?.StartsWith("lite/", StringComparison.Ordinal) == true) return PanelFile(file["lite/".Length..], "kiosk-lite");
            return string.IsNullOrEmpty(file) || file == "index.html" ? Results.Content(PanelIndex(), "text/html; charset=utf-8") : PanelFile(file);
        });
    }
}
