using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The access check the agent runs on every request (AccessGate.Check, used by the middleware in Program.cs), fed with
/// whole HTTP requests: port, Host, Origin, Sec-Fetch-Site, cookies, method and path.
/// </summary>
public class AccessGateTests
{
    private const int Local = 8765, Lan = 8766;
    private static readonly string[] SameOrigins = ["http://127.0.0.1:8765", "http://localhost:8765"];
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static AgentConfig Config(bool kiosk = true, bool control = false, bool approvals = false) => new()
    {
        Lan = new LanConfig { Enabled = true, Port = Lan, Token = "phone-key", WebhookToken = "webhook-key" },
        Kiosk = new KioskConfig { Enabled = kiosk, Token = "kiosk-key", AllowControl = control, AllowApprovals = approvals, CookieDays = 30 },
    };

    private static readonly string ValidCookie = Kiosk.CookieValue("kiosk-key", Now.AddDays(30));

    private static HttpRequest Request(int port, string method, string path, string host = "192.168.1.5:8766", string? origin = null,
        string? cookie = null, bool panelHeader = false, string? query = null, string? auth = null, string? fetchSite = null, string scheme = "https")
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.LocalPort = port;
        var r = ctx.Request;
        r.Method = method;
        r.Scheme = port == Lan ? scheme : "http";
        r.Host = new HostString(host);
        r.Path = path;
        if (query is not null) r.QueryString = new QueryString(query);
        if (origin is not null) r.Headers.Origin = origin;
        if (cookie is not null) r.Headers.Cookie = $"{Kiosk.CookieName}={cookie}";
        if (panelHeader) r.Headers["X-PhoneBox-Panel"] = "1";
        if (auth is not null) r.Headers.Authorization = auth;
        if (fetchSite is not null) { r.Headers["Sec-Fetch-Site"] = fetchSite; r.Headers["Sec-Fetch-Mode"] = "cors"; }
        return r;
    }

    /// <summary>As the middleware calls it; panelApi = the path was /agent/api/… before the rewrite.</summary>
    private static GateResult Check(HttpRequest r, AgentConfig cfg, bool panelApi = false) =>
        AccessGate.Check(r, cfg, Lan, panelApi, spotifyCallback: false, SameOrigins, Now);

    private static GateResult Api(AgentConfig cfg, string method, string path, string? cookie = null, string? origin = null, bool header = true) =>
        Check(Request(Lan, method, path, cookie: cookie ?? ValidCookie, origin: origin, panelHeader: header), cfg, panelApi: true);

    // ------------------------------------------------------------------ the maintainer's list

    [Theory]
    [InlineData(Local, "127.0.0.1:8765")]
    [InlineData(Lan, "192.168.1.5:8766")]
    public void KioskOffIs404OnBothPorts(int port, string host)
    {
        var page = Request(port, "GET", "/kiosk/", host: host, query: "?token=kiosk-key");
        Assert.Equal(404, Check(page, Config(kiosk: false)).Status);
    }

    [Fact]
    public void KioskOffIs404ForThePanelApiOverWifi() =>
        Assert.Equal(404, Api(Config(kiosk: false), "GET", "/api/state").Status);

    [Fact]
    public void KioskIsNeverServedOnTheLocalPort()
    {
        Assert.Equal(404, Check(Request(Local, "GET", "/kiosk/", host: "127.0.0.1:8765", query: "?token=kiosk-key"), Config()).Status);
        Assert.Equal(404, Check(Request(Local, "GET", "/kiosk/lite/", host: "127.0.0.1:8765", cookie: ValidCookie), Config()).Status);
    }

    [Fact]
    public void WifiPanelApiWithoutTheCookieIs403()
    {
        Assert.Equal(403, Check(Request(Lan, "GET", "/api/state", panelHeader: true), Config(), panelApi: true).Status);
        Assert.Equal(403, Api(Config(), "GET", "/api/state", cookie: "kiosk-key").Status); // the raw key is not a cookie
        Assert.Equal(403, Api(Config(), "GET", "/api/state", cookie: Kiosk.CookieValue("kiosk-key", Now.AddSeconds(-1))).Status); // expired
    }

    [Fact]
    public void CrossOriginPostIs403()
    {
        Assert.Equal(403, Api(Config(control: true), "POST", "/api/run/x", origin: "https://evil.example").Status);
        Assert.Equal(403, Api(Config(control: true), "POST", "/api/run/x", origin: "null").Status);
        var foreign = Request(Lan, "POST", "/api/run/x", cookie: ValidCookie, panelHeader: true, fetchSite: "cross-site");
        Assert.Equal(403, Check(foreign, Config(control: true), panelApi: true).Status);
    }

    [Fact]
    public void ReadOnlyKioskCannotRunOrApprove()
    {
        Assert.Equal(403, Api(Config(), "POST", "/api/run/terminal").Status);
        Assert.Equal(403, Api(Config(), "POST", "/api/approvals/abc", origin: "https://192.168.1.5:8766").Status);
        Assert.Equal(403, Api(Config(), "GET", "/api/approvals").Status);
    }

    [Theory]
    [InlineData("evil.example:8765")]
    [InlineData("192.168.1.5:8765")]
    public void NonLoopbackHostOnTheLocalPortIs403(string host) =>
        Assert.Equal(403, Check(Request(Local, "GET", "/api/state", host: host), Config()).Status);

    // ------------------------------------------------------------------ what a kiosk may do

    [Fact]
    public void ReadOnlyKioskReadsAndOptInsOpenTheRest()
    {
        Assert.Equal(0, Api(Config(), "GET", "/api/state").Status);
        Assert.True(Api(Config(), "GET", "/api/state").Kiosk);
        Assert.Equal(0, Api(Config(control: true), "POST", "/api/run/terminal", origin: "https://192.168.1.5:8766").Status);
        Assert.Equal(403, Api(Config(control: true), "GET", "/api/approvals").Status);
        Assert.Equal(0, Api(Config(approvals: true), "POST", "/api/approvals/abc").Status);
        Assert.Equal(403, Api(Config(control: true, approvals: true), "POST", "/api/procs/kill").Status);
        Assert.Equal(403, Api(Config(control: true, approvals: true), "POST", "/api/phone/battery").Status);
        Assert.Equal(403, Api(Config(control: true, approvals: true), "DELETE", "/api/push").Status);
    }

    [Fact]
    public void TheKeyBecomesACookieAndLeavesTheAddress()
    {
        var r = Check(Request(Lan, "GET", "/kiosk", query: "?token=kiosk-key&full=1"), Config());
        Assert.Equal(302, r.Status);
        Assert.Equal("/kiosk/?full=1", r.Location);
        Assert.NotNull(r.Cookie);
        Assert.DoesNotContain("kiosk-key", r.Cookie!.Value.Value);
        Assert.Equal(Now.AddDays(30), r.Cookie.Value.Expires);
        Assert.True(Kiosk.CookieValid(r.Cookie.Value.Value, Config().Kiosk, Now));
        // then the page loads with the cookie alone; without it a short explanation
        Assert.Equal(0, Check(Request(Lan, "GET", "/kiosk/", cookie: r.Cookie.Value.Value), Config()).Status);
        var signedOut = Check(Request(Lan, "GET", "/kiosk/"), Config());
        Assert.Equal(403, signedOut.Status);
        Assert.Contains("not signed in", signedOut.Body);
        Assert.Equal(403, Check(Request(Lan, "GET", "/kiosk/", query: "?token=wrong"), Config()).Status);
    }

    // ------------------------------------------------------------------ unchanged: the phone and the webhooks

    [Fact]
    public void ThePhoneAndTheWebhooksWorkAsBefore()
    {
        Assert.Equal(0, Check(Request(Lan, "GET", "/api/state", auth: "Bearer phone-key"), Config()).Status);
        Assert.Equal(403, Check(Request(Lan, "GET", "/api/state"), Config()).Status);
        Assert.Equal(403, Check(Request(Lan, "GET", "/api/settings", auth: "Bearer phone-key"), Config()).Status);
        Assert.Equal(0, Check(Request(Lan, "POST", "/api/notify", query: "?token=webhook-key"), Config()).Status);
        Assert.Equal(403, Check(Request(Lan, "POST", "/api/state", query: "?token=webhook-key"), Config()).Status);
        Assert.Equal(403, Check(Request(Lan, "GET", "/api/state", auth: "Bearer kiosk-key"), Config()).Status); // kiosk key ≠ phone key
        Assert.Equal(0, Check(Request(Local, "GET", "/api/settings", host: "127.0.0.1:8765"), Config()).Status);
        Assert.Equal(403, Check(Request(Local, "PUT", "/api/settings", host: "127.0.0.1:8765", origin: "https://evil.example"), Config()).Status);
        // the layout editor's preview: /agent/api on the local port, no key needed
        Assert.Equal(0, Check(Request(Local, "GET", "/api/state", host: "127.0.0.1:8765"), Config(), panelApi: true).Status);
    }
}
