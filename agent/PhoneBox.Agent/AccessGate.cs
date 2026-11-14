namespace PhoneBox.Agent;

/// <summary>What the access check decided for a request.</summary>
/// <param name="Status">0 = let it through; otherwise the status to answer with (and stop).</param>
/// <param name="Kiosk">The request is a web kiosk request (see Kiosk.cs).</param>
/// <param name="Location">For a redirect: where to.</param>
/// <param name="Cookie">A kiosk cookie value to set, with its expiry.</param>
/// <param name="Body">A short HTML page to answer with.</param>
public readonly record struct GateResult(int Status, bool Kiosk = false, string? Location = null,
    (string Value, DateTimeOffset Expires)? Cookie = null, string? Body = null);

/// <summary>
/// Who may call what, for both ports:
///  - Local port (127.0.0.1): the Host check blocks DNS rebinding. The phone's USB proxy sends no Origin header; from
///    browsers only the settings page itself (same origin) is accepted (no CSRF). No web kiosk here.
///  - Wi-Fi / Tailscale port (HTTPS): the phone API with the paired key; the webhook endpoints with the webhook key;
///    the web kiosk (/kiosk and the panel API under /agent/api) with the kiosk key or cookie.
/// </summary>
public static class AccessGate
{
    public static GateResult Check(HttpRequest req, AgentConfig cfg, int? lanPort, bool panelApi, bool spotifyCallback,
        IReadOnlyCollection<string> sameOrigins, DateTimeOffset now)
    {
        var origin = req.Headers.Origin.ToString();
        var foreign = BrowserGuard.IsForeign(req.Headers["Sec-Fetch-Site"], req.Headers["Sec-Fetch-Mode"], req.Method, req.Path);
        if (lanPort is { } lan && req.HttpContext.Connection.LocalPort == lan)
            return Kiosk.IsKioskPath(req.Path) || panelApi ? CheckKiosk(req, cfg.Kiosk, origin, foreign, now) : CheckPhone(req, cfg.Lan, origin);

        // Local port. The web kiosk is for other devices only: it is not served here.
        if (Kiosk.IsKioskPath(req.Path)) return new(StatusCodes.Status404NotFound);
        var host = req.Host.Host;
        if ((host != "127.0.0.1" && host != "localhost") || (origin != "" && !sameOrigins.Contains(origin)) || (!spotifyCallback && foreign))
            return new(StatusCodes.Status403Forbidden);
        return new(0);
    }

    /// <summary>The phone's key opens the panel API; the separate webhook key opens only the webhook endpoints, in a header
    /// or, for senders that cannot set headers, in ?token= (URLs end up in logs, so it must not be the phone's key).</summary>
    private static GateResult CheckPhone(HttpRequest req, LanConfig lan, string origin)
    {
        var authorized = Lan.Matches(req.Headers.Authorization, lan.Token!)
            || (Lan.IsWebhook(req.Path) && (Lan.Matches(req.Headers.Authorization, lan.WebhookToken!)
                || Lan.MatchesQuery(req.Query["token"], lan.WebhookToken!)));
        return origin != "" || !Lan.AllowedOverLan(req.Path) || !authorized ? new(StatusCodes.Status403Forbidden) : new(0);
    }

    private static GateResult CheckKiosk(HttpRequest req, KioskConfig kiosk, string origin, bool foreign, DateTimeOffset now)
    {
        if (!kiosk.Enabled || string.IsNullOrEmpty(kiosk.Token)) return new(StatusCodes.Status404NotFound); // invisible while off
        if (!Kiosk.OriginAllowed(origin, req.Scheme, req.Host.Value ?? "") || foreign) return new(StatusCodes.Status403Forbidden);
        var r = Kiosk.Capture(req);
        if (r.Page)
        {
            // the address with the key: trade the key for a cookie and drop it from the address bar and history
            if (Kiosk.KeyMatches(req.Query["token"], kiosk))
            {
                var expires = now.AddDays(kiosk.CookieDays);
                var rest = QueryString.Create(req.Query.Where(q => q.Key != "token"));
                var path = req.Path == "/kiosk" ? "/kiosk/" : req.Path.Value!;
                return new(StatusCodes.Status302Found, true, path + rest, (Kiosk.CookieValue(kiosk.Token, expires), expires));
            }
            if (!Kiosk.CookieValid(r.Cookie, kiosk, now))
                return HttpMethods.IsGet(req.Method) ? new(StatusCodes.Status403Forbidden, true, Body: Kiosk.SignInAgain) : new(StatusCodes.Status403Forbidden);
            return new(0, true);
        }
        return Kiosk.CookieValid(r.Cookie, kiosk, now) && Kiosk.ApiAllowed(r, kiosk) ? new(0, true) : new(StatusCodes.Status403Forbidden);
    }
}
