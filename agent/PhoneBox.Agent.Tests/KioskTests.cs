using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

/// <summary>Web kiosk: its own key and cookie, read only by default, and a page old browsers can run.</summary>
public class KioskTests
{
    private static AgentConfig Load(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-kiosk-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { return new ConfigStore(path).Get(); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("/kiosk", true)]
    [InlineData("/kiosk/api/state", true)]
    [InlineData("/kiosk/api/run/terminal", true)]
    [InlineData("/kioskx", false)]
    [InlineData("/api/state", false)]
    [InlineData("/", false)]
    public void OnlyKioskPathsAreKioskPaths(string path, bool kiosk) => Assert.Equal(kiosk, Kiosk.IsKioskPath(new PathString(path)));

    [Theory]
    [InlineData("/kiosk")]
    [InlineData("/kiosk/api/state")]
    public void KioskPathsAreNotPartOfThePhoneOrWebhookApi(string path)
    {
        Assert.False(Lan.AllowedOverLan(new PathString(path)));
        Assert.False(Lan.IsWebhook(new PathString(path)));
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static KioskConfig On(bool control = false, bool approvals = false) =>
        new() { Enabled = true, Token = "secret-kiosk-key", AllowControl = control, AllowApprovals = approvals };

    [Fact]
    public void TheKeyMatchesOnlyWhileEnabled()
    {
        Assert.True(Kiosk.KeyMatches("secret-kiosk-key", On()));
        Assert.False(Kiosk.KeyMatches(null, On()));
        Assert.False(Kiosk.KeyMatches("", On()));
        Assert.False(Kiosk.KeyMatches("secret-kiosk-ke", On()));
        Assert.False(Kiosk.KeyMatches("secret-kiosk-key", new KioskConfig { Enabled = false, Token = "secret-kiosk-key" }));
        Assert.False(Kiosk.KeyMatches("", new KioskConfig { Enabled = true, Token = null }));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("https://192.168.1.5:8766", true)]
    [InlineData("HTTPS://192.168.1.5:8766", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://192.168.1.5:8766", false)]
    [InlineData("null", false)]
    public void OnlyTheKioskPageItselfMayCallItsApi(string origin, bool allowed) =>
        Assert.Equal(allowed, Kiosk.OriginAllowed(origin, "https", "192.168.1.5:8766"));

    [Fact]
    public void TheCookieHoldsADerivedValueThatExpires()
    {
        var value = Kiosk.CookieValue("secret-kiosk-key", Now.AddDays(30));
        Assert.DoesNotContain("secret-kiosk-key", value);
        Assert.True(Kiosk.CookieValid(value, On(), Now));
        Assert.False(Kiosk.CookieValid(value, On(), Now.AddDays(31)));                                    // expired
        Assert.False(Kiosk.CookieValid(value, new KioskConfig { Enabled = true, Token = "new-key" }, Now)); // key replaced
        Assert.False(Kiosk.CookieValid(value, new KioskConfig { Enabled = false, Token = "secret-kiosk-key" }, Now));
        Assert.False(Kiosk.CookieValid("secret-kiosk-key", On(), Now));                                    // the raw key is not a cookie
        var tampered = (Now.AddDays(399).ToUnixTimeSeconds()) + value[value.IndexOf('.')..];
        Assert.False(Kiosk.CookieValid(tampered, On(), Now));                                              // a later expiry needs a new MAC
        Assert.False(Kiosk.CookieValid(null, On(), Now));
        Assert.False(Kiosk.CookieValid("garbage", On(), Now));
    }

    [Theory]
    [InlineData("/api/state", "GET", Kiosk.Access.Read)]
    [InlineData("/api/stream", "GET", Kiosk.Access.Read)]
    [InlineData("/api/config", "GET", Kiosk.Access.Read)]
    [InlineData("/api/art", "GET", Kiosk.Access.Read)]
    [InlineData("/api/procs", "GET", Kiosk.Access.Read)]
    [InlineData("/api/procs/icon", "GET", Kiosk.Access.Read)]
    [InlineData("/api/calendar", "GET", Kiosk.Access.Read)]
    [InlineData("/api/work", "GET", Kiosk.Access.Read)]
    [InlineData("/api/quotas", "GET", Kiosk.Access.Read)]
    [InlineData("/api/widget/home/cmd", "GET", Kiosk.Access.Read)]
    [InlineData("/api/docker", "GET", Kiosk.Access.Read)]
    [InlineData("/api/controls/state", "GET", Kiosk.Access.Read)]
    [InlineData("/api/run/terminal", "POST", Kiosk.Access.Control)]
    [InlineData("/api/media/playpause", "POST", Kiosk.Access.Control)]
    [InlineData("/api/volume", "POST", Kiosk.Access.Control)]
    [InlineData("/api/controls/command/keys", "POST", Kiosk.Access.Control)]
    [InlineData("/api/controls/listen", "GET", Kiosk.Access.Control)]
    [InlineData("/api/docker/abc/restart", "POST", Kiosk.Access.Control)]
    [InlineData("/api/obs/record", "POST", Kiosk.Access.Control)]
    [InlineData("/api/ha/toggle/light.desk", "POST", Kiosk.Access.Control)]
    [InlineData("/api/rules/pause", "POST", Kiosk.Access.Control)]
    [InlineData("/api/approvals", "GET", Kiosk.Access.Approvals)]
    [InlineData("/api/approvals/abc", "POST", Kiosk.Access.Approvals)]
    [InlineData("/api/procs/kill", "POST", Kiosk.Access.Never)]
    [InlineData("/api/phone/battery", "POST", Kiosk.Access.Never)]
    [InlineData("/api/push/temperature", "POST", Kiosk.Access.Never)]
    [InlineData("/api/push", "DELETE", Kiosk.Access.Never)]
    [InlineData("/api/push", "GET", Kiosk.Access.Read)]           // the push widgets read the values
    [InlineData("/api/push/temperature", "PUT", Kiosk.Access.Never)]
    [InlineData("/api/push/temperature", "DELETE", Kiosk.Access.Never)]
    [InlineData("/api/notify", "POST", Kiosk.Access.Never)]
    [InlineData("/api/goto/2", "POST", Kiosk.Access.Never)]
    [InlineData("/api/refresh", "POST", Kiosk.Access.Never)]
    [InlineData("/api/settings", "GET", Kiosk.Access.Never)]
    [InlineData("/api/pair", "GET", Kiosk.Access.Never)]
    [InlineData("/api/shortcuts/test", "POST", Kiosk.Access.Never)]
    [InlineData("/api/state", "POST", Kiosk.Access.Never)]
    public void KioskCallsAreSorted(string path, string method, Kiosk.Access expected) =>
        Assert.Equal(expected, Kiosk.Classify(new PathString(path), method));

    [Fact]
    public void RunningACommandWidgetAgainIsControl() =>
        Assert.Equal(Kiosk.Access.Control, Kiosk.Classify(new PathString("/api/widget/home/cmd"), "GET", force: true));

    private static Kiosk.Request Call(string path, string method = "GET", bool header = true) =>
        new(new PathString(path), method, Kiosk.CookieValue("secret-kiosk-key", Now.AddDays(1)), header, false, false);

    [Fact]
    public void AKioskIsReadOnlyUnlessControlIsAllowed()
    {
        Assert.True(Kiosk.ApiAllowed(Call("/api/state"), On()));
        Assert.False(Kiosk.ApiAllowed(Call("/api/run/x", "POST"), On()));
        Assert.True(Kiosk.ApiAllowed(Call("/api/run/x", "POST"), On(control: true)));
        Assert.False(Kiosk.ApiAllowed(Call("/api/approvals"), On(control: true)));
        Assert.True(Kiosk.ApiAllowed(Call("/api/approvals/x", "POST"), On(approvals: true)));
        Assert.False(Kiosk.ApiAllowed(Call("/api/procs/kill", "POST"), On(control: true, approvals: true)));
        Assert.False(Kiosk.ApiAllowed(Call("/api/notify", "POST"), On(control: true, approvals: true)));
        // without the panel header only the read-only images (a sandboxed Custom HTML widget cannot add it)
        Assert.False(Kiosk.ApiAllowed(Call("/api/state", header: false), On()));
        Assert.True(Kiosk.ApiAllowed(Call("/api/art", header: false), On()));
    }

    [Fact]
    public void AnOpenStreamStopsWhenTheKioskIsTurnedOffOrTheKeyOrPermissionChanges()
    {
        var stream = Call("/api/stream");
        Assert.True(Kiosk.StillAllowed(stream, On(), Now));
        Assert.False(Kiosk.StillAllowed(stream, new KioskConfig { Enabled = false, Token = "secret-kiosk-key" }, Now));
        Assert.False(Kiosk.StillAllowed(stream, new KioskConfig { Enabled = true, Token = "replaced" }, Now));
        var listen = Call("/api/controls/listen");
        Assert.True(Kiosk.StillAllowed(listen, On(control: true), Now));
        Assert.False(Kiosk.StillAllowed(listen, On(), Now));
    }

    [Fact]
    public void ThePanelIsServedUnchangedWithTheLegacyCheckFirst()
    {
        var html = Kiosk.PanelIndex();
        using var s = AppPaths.Resource("www/index.html")!;
        var original = new StreamReader(s).ReadToEnd();
        Assert.Equal(original, html.Replace("\n" + Kiosk.LegacyCheck, ""));
        Assert.True(html.IndexOf(Kiosk.LegacyCheck, StringComparison.Ordinal) < html.IndexOf("<link", StringComparison.Ordinal));
        Assert.DoesNotContain("=>", Kiosk.LegacyCheck);
        Assert.Contains("location.replace('lite/')", Kiosk.LegacyCheck); // the query (which held the key) is not carried over
    }

    [Fact]
    public void TheKioskKeyIsCreatedOnlyWhenTheKioskIsTurnedOn()
    {
        var off = Load("{}");
        Assert.False(off.Kiosk.Enabled);
        Assert.Null(off.Kiosk.Token);
        Assert.False(off.Kiosk.AllowControl);
        Assert.False(off.Kiosk.AllowApprovals);
        Assert.Equal(30, off.Kiosk.CookieDays);
        var on = Load("""{"kiosk":{"enabled":true}}""");
        Assert.False(string.IsNullOrWhiteSpace(on.Kiosk.Token));
        Assert.NotEqual(on.Lan.Token, on.Kiosk.Token);
        Assert.NotEqual(on.Lan.WebhookToken, on.Kiosk.Token);
        var kept = Load("""{"kiosk":{"enabled":true,"token":"keep-me","cookieDays":9999}}""");
        Assert.Equal("keep-me", kept.Kiosk.Token);
        Assert.Equal(400, kept.Kiosk.CookieDays);
        Assert.Null(Load("""{"kiosk":null}""").Kiosk.Token);
    }

    [Fact]
    public void ThePhoneNeverSeesTheKioskKey()
    {
        var c = Load("""{"kiosk":{"enabled":true,"token":"kiosk-secret"}}""");
        Assert.DoesNotContain("kiosk-secret", JsonSerializer.Serialize(c.ToPublic()));
    }

    [Fact]
    public void TheLitePanelIsEmbeddedAndLoweredForFirefox68()
    {
        string Read(string name)
        {
            using var s = AppPaths.Resource("kiosk-lite/" + name);
            Assert.NotNull(s);
            return new StreamReader(s!).ReadToEnd();
        }
        var index = Read("index.html");
        Assert.Contains("src=\"polyfill.js\"", index);
        Assert.Contains("type=\"module\" src=\"main.js\"", index);
        Assert.Contains("too old for the PhoneBox kiosk", index);
        var js = Read("main.js");
        Assert.DoesNotContain("?.", js.Replace("\"?.\"", "").Replace("'?.'", ""));
        Assert.DoesNotContain("||=", js);
        var css = Read("style.css");
        Assert.DoesNotMatch(@"\d(cqw|cqh|cqmin)\b|[^\w-](min|max|clamp)\(|color-mix\(|:is\(|:has\(|@container|aspect-ratio", css);
        Assert.Contains("PF_TABLE", Read("polyfill.js"));
    }
}
