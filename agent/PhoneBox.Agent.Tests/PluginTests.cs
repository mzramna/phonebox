using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Community plugins: manifest validation, the JSON-lines protocol, settings, the catalog and a real sidecar.</summary>
[Collection("plugins")] // the catalog's plugin list is shared state
public sealed class PluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Catalog.SetPlugins([]);
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Plugin(string folder, string manifest, params (string Path, string Text)[] files)
    {
        var dir = Path.Combine(_root, "plugins", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest);
        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, path))!);
            File.WriteAllText(Path.Combine(dir, path), text);
        }
        return dir;
    }

    private static string Manifest(string id = "demo", string widgets = """[{"id":"w","name":"W","view":{"style":"big","key":"k"}}]""", string extra = "") =>
        $$"""{"id":"{{id}}","name":"Demo","version":"1.0.0","apiVersion":1{{extra}},"widgets":{{widgets}}}""";

    // ------------------------------------------------------------ manifest

    private static string ExamplesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "extras", "plugins"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, "extras", "plugins");
    }

    [Fact]
    public void EveryShippedExamplePluginIsValid()
    {
        var registry = new PluginRegistry(ExamplesDir());
        registry.Scan();
        Assert.All(registry.All, f => Assert.Null(f.Error));
        Assert.Equal(["http-status", "open-link"], registry.Valid.Select(m => m.Id).Order());
        var link = registry.Get("open-link")!;
        Assert.Equal(["open"], link.Widget("link")!.Actions);
        Assert.NotNull(link.UiFile("link"));
    }

    [Fact]
    public void TheShippedExamplePluginIsValid()
    {
        var (m, error) = PluginManifest.Load(Path.Combine(ExamplesDir(), "http-status"));
        Assert.Null(error);
        Assert.Equal("http-status", m!.Id);
        Assert.Equal(["authHeader"], PluginManifest.SecretKeys(m.Settings));
        Assert.Equal("plugin.http-status.endpoints", m.TypeOf(m.Widgets[0]));
        Assert.NotNull(m.UiFile("endpoints"));
        Assert.Null(m.UiFile("summary"));
    }

    [Theory]
    [InlineData("""{"id":"Bad Id","name":"x","version":"1","apiVersion":1,"widgets":[]}""", "id must")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":2,"widgets":[]}""", "apiVersion 2")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"permissions":["root"],"widgets":[{"id":"w","name":"W","view":{"key":"k"}}]}""", "unknown permission")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"widgets":[{"id":"w","name":"W"}]}""", "give a \"ui\"")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"widgets":[{"id":"w","name":"W","ui":"../outside.html"}]}""", "relative path")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"widgets":[{"id":"w","name":"W","ui":"missing.html"}]}""", "missing")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"widgets":[{"id":"w","name":"W","view":{"key":"k"}},{"id":"w","name":"W2","view":{"key":"k"}}]}""", "declared twice")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"widgets":[{"id":"w","name":"W","actions":["rm -rf"],"view":{"key":"k"}}]}""", "actions")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"widgets":[{"id":"w","name":"W","w":13,"view":{"key":"k"}}]}""", "w must be")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"backend":{"command":"../../bin/sh"},"widgets":[]}""", "inside the plugin folder")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"backend":{"command":"/bin/sh"},"widgets":[]}""", "inside the plugin folder")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"widgets":[]}""", "needs a backend")]
    [InlineData("""{"id":"ok","name":"x","version":"1","apiVersion":1,"settings":[{"label":"no key"}],"widgets":[{"id":"w","name":"W","view":{"key":"k"}}]}""", "needs a \"key\"")]
    public void InvalidManifestsAreRefusedWithAReason(string manifest, string reason)
    {
        var (m, error) = PluginManifest.Load(Plugin("p", manifest));
        Assert.Null(m);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void RegistryReportsBrokenFoldersAndDuplicateIdsAndFillsTheCatalog()
    {
        Plugin("a", Manifest("same"));
        Plugin("b", Manifest("same"));
        Plugin("c", "{ not json");
        Plugin("d", Manifest("other", """[{"id":"panel","name":"Panel","ui":"ui/panel.html","options":[{"key":"token","secret":true},{"key":"title","default":"T"}]}]"""), ("ui/panel.html", "<p>hi</p>"));
        var registry = new PluginRegistry(Path.Combine(_root, "plugins"));
        registry.Scan();
        Assert.Equal(["other", "same"], registry.Valid.Select(m => m.Id).Order());
        Assert.Contains(registry.All, f => f.Folder == "b" && f.Error!.Contains("already provides"));
        Assert.Contains(registry.All, f => f.Folder == "c" && f.Error is not null);
        Assert.True(Catalog.IsWidget("plugin.other.panel"));
        Assert.Contains("\"plugin.other.panel\"", Catalog.AllWidgetsJson);
        Assert.Contains("\"category\":\"Plugins\"", Catalog.AllWidgetsJson);
        Assert.Contains("token", Catalog.PluginWidget("plugin.other.panel")!.SecretOptions);
    }

    // ------------------------------------------------------------ settings and what the phone sees

    [Fact]
    public void PluginWidgetsSurviveWhileThePluginIsMissingAndSecretsStayOnTheComputer()
    {
        Plugin("d", Manifest("other", """[{"id":"panel","name":"Panel","ui":"ui/panel.html","options":[{"key":"token","secret":true}]}]"""), ("ui/panel.html", "<p/>"));
        new PluginRegistry(Path.Combine(_root, "plugins")).Scan();
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """
            {"plugins":[{"id":"other","enabled":true,"settings":{"apiKey":"TOP-SECRET-1"}},{"id":"BAD ID"}],
             "pages":[{"id":"p","title":"P","widgets":[
               {"id":"a","type":"plugin.other.panel","w":3,"h":2,"options":{"token":"TOP-SECRET-2","title":"shown"}},
               {"id":"b","type":"plugin.gone.widget","w":3,"h":2,"options":{"token":"TOP-SECRET-3"}},
               {"id":"c","type":"plugin..bad","w":3,"h":2}]}]}
            """);
        var c = new ConfigStore(path).Get();
        Assert.Equal(["plugin.other.panel", "plugin.gone.widget"], c.Pages[0].Widgets!.Select(w => w.Type));
        Assert.Equal(["other"], c.Plugins.Select(p => p.Id));
        var json = JsonSerializer.Serialize(c.ToPublic());
        Assert.DoesNotContain("TOP-SECRET", json);
        Assert.Contains("shown", json);
        Assert.Contains("\"Id\":\"other\"", json); // the enabled plugin's widget metadata goes to the panel
    }

    [Fact]
    public void ConfigPayloadMergesDefaultsWithTheUsersValues()
    {
        Plugin("d", Manifest("other", """[{"id":"panel","name":"Panel","view":{"key":"k"},"options":[{"key":"size","default":3},{"key":"title","default":"T"}]}]""",
            extra: ""","settings":[{"key":"url","default":"https://x"},{"key":"token","secret":true}],"backend":{"command":"python3"}"""));
        var registry = new PluginRegistry(Path.Combine(_root, "plugins"));
        registry.Scan();
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """{"plugins":[{"id":"other","enabled":true,"settings":{"token":"t"}}],"pages":[{"id":"home","title":"H","widgets":[{"id":"x","type":"plugin.other.panel","options":{"title":"Mine"}}]}]}""");
        var payload = PluginHost.ConfigPayload(new ConfigStore(path).Get(), registry.Get("other")!).ToJsonString();
        Assert.Contains("\"url\":\"https://x\"", payload);
        Assert.Contains("\"token\":\"t\"", payload);
        Assert.Contains("\"instance\":\"home/x\"", payload);
        Assert.Contains("\"widget\":\"panel\"", payload);
        Assert.Contains("\"size\":3", payload);
        Assert.Contains("\"title\":\"Mine\"", payload);
    }

    [Theory]
    [InlineData("/api/plugins/state", true)]
    [InlineData("/api/plugins/demo/action", true)]
    [InlineData("/api/plugins/demo/ui/panel", true)]
    [InlineData("/api/plugins", false)]
    [InlineData("/api/plugins/rescan", false)]
    [InlineData("/api/plugins/demo/restart", false)]
    [InlineData("/api/plugins/demo/ui/panel/extra", false)]
    public void OnlyThePanelsPluginApiIsReachableOverWifi(string path, bool allowed) => Assert.Equal(allowed, Lan.AllowedOverLan(new PathString(path)));

    // ------------------------------------------------------------ protocol

    [Fact]
    public async Task ConnectionReadsJsonLinesAndSurvivesGarbage()
    {
        var huge = new string('x', PluginConnection.MaxLine + 10);
        var input = new StringReader($"{{\"type\":\"state\",\"key\":\"a\",\"data\":1}}\r\n\n  \nnot json\n[1,2]\n\"{huge}\"\n{{\"type\":\"log\",\"message\":\"é\"}}");
        var conn = new PluginConnection(input, TextWriter.Null);
        Assert.Equal("state", (await conn.ReadAsync(default))!.Type);
        Assert.Contains("not JSON", (await conn.ReadAsync(default))!.Error);
        Assert.Contains("\"type\"", (await conn.ReadAsync(default))!.Error);
        Assert.Contains("longer than", (await conn.ReadAsync(default))!.Error);
        var log = await conn.ReadAsync(default);
        Assert.Equal("é", log!.Body.GetProperty("message").GetString());
        Assert.Null(await conn.ReadAsync(default));
    }

    [Fact]
    public async Task ConnectionWritesOneLinePerMessage()
    {
        var output = new StringWriter();
        var conn = new PluginConnection(TextReader.Null, output);
        await conn.SendAsync(new { type = "action", args = new { text = "two\nlines" } }, default);
        await conn.SendAsync(new { type = "shutdown" }, default);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("two\nlines", JsonDocument.Parse(lines[0]).RootElement.GetProperty("args").GetProperty("text").GetString());
    }

    // ------------------------------------------------------------ a real sidecar

    private const string Sidecar = """
        import json, sys
        def send(m):
            sys.stdout.write(json.dumps(m) + "\n"); sys.stdout.flush()
        for line in sys.stdin:
            m = json.loads(line)
            if m["type"] == "init":
                send({"type": "state", "key": "hello", "data": {"value": len(m["instances"]), "token": m["settings"].get("token")}})
                send({"type": "bogus"})
                send({"type": "notify", "title": "hi"})
            elif m["type"] == "config":
                send({"type": "state", "key": "hello", "data": {"value": len(m["instances"]), "config": True}})
            elif m["type"] == "action":
                send({"type": "result", "id": m["id"], "ok": m["name"] == "go", "error": None if m["name"] == "go" else "nope", "data": m.get("args")})
            elif m["type"] == "shutdown":
                break
        """;

    [Fact]
    public async Task HostRunsTheSidecarPassesActionsAndStopsItWhenTurnedOff()
    {
        // a program name on PATH (a manifest may not point outside its folder)
        var python = ToolProcess.Find("python3") is { } p3 && !p3.Contains("WindowsApps") ? "python3" : ToolProcess.Find("python") is { } p && !p.Contains("WindowsApps") ? "python" : null;
        if (python is null) return; // no Python here: covered on CI
        Plugin("side", Manifest("side", """[{"id":"w","name":"W","actions":["go","fail"],"view":{"key":"hello"}}]""",
            extra: $$""","permissions":["notify"],"settings":[{"key":"token","secret":true}],"backend":{"command":{{JsonSerializer.Serialize(python)}},"args":["main.py"]}"""),
            ("main.py", Sidecar));
        var registry = new PluginRegistry(Path.Combine(_root, "plugins"));
        registry.Scan();
        Assert.Equal(["side"], registry.Valid.Select(m => m.Id));
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """{"plugins":[{"id":"side","enabled":true,"settings":{"token":"s3"}}],"pages":[{"id":"home","title":"H","widgets":[{"id":"x","type":"plugin.side.w"}]}]}""");
        var cfg = new ConfigStore(path);
        var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_root, "data.json"));
        var host = new PluginHost(registry, cfg, push, NullLogger<PluginHost>.Instance);
        try
        {
            host.Reconcile();
            string State() => JsonSerializer.Serialize(host.Snapshot());
            await Until(() => State().Contains("\"token\":\"s3\""));
            Assert.Contains(push.Since(0), e => e.Kind == "notify");

            var ok = await host.ActAsync("side", "home", "x", "go", JsonDocument.Parse("""{"n":1}""").RootElement, default);
            Assert.True(ok.Ok);
            Assert.Equal(1, ok.Data!.Value.GetProperty("n").GetInt32());
            var refused = await host.ActAsync("side", "home", "x", "fail", null, default);
            Assert.Equal("nope", refused.Error);
            Assert.False((await host.ActAsync("side", "home", "x", "undeclared", null, default)).Ok);
            Assert.False((await host.ActAsync("side", "home", "missing", "go", null, default)).Ok);

            // a changed layout is sent as "config"
            Assert.Null(cfg.Save("""{"plugins":[{"id":"side","enabled":true}],"pages":[{"id":"home","title":"H","widgets":[{"id":"x","type":"plugin.side.w"},{"id":"y","type":"plugin.side.w"}]}]}"""));
            host.Reconcile();
            await Until(() => State().Contains("\"config\":true") && State().Contains("\"value\":2"));

            // turned off: the sidecar stops and its data is gone
            Assert.Null(cfg.Save("""{"plugins":[{"id":"side","enabled":false}],"pages":[{"id":"home","title":"H","widgets":[{"id":"x","type":"plugin.side.w"}]}]}"""));
            host.Reconcile();
            Assert.DoesNotContain("hello", State());
            Assert.Equal("the plugin is turned off in the settings", (await host.ActAsync("side", "home", "x", "go", null, default)).Error);
        }
        finally { await host.StopAsync(default); }
    }

    // ------------------------------------------------------------ adding and removing plugins

    private static MemoryStream Zip(params (string Name, string Text)[] files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
        ms.Position = 0;
        return ms;
    }

    private (PluginHost Host, PluginRegistry Registry, ConfigStore Cfg) NewHost(string config = "{}")
    {
        var registry = new PluginRegistry(Path.Combine(_root, "plugins"));
        registry.Scan();
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, config);
        var cfg = new ConfigStore(path);
        return (new PluginHost(registry, cfg, new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_root, "data.json")), NullLogger<PluginHost>.Instance), registry, cfg);
    }

    [Fact]
    public async Task InstallTakesAZipWithTheManifestAtTheTopOrInItsOnlyFolderAndUpdatesOnlyWhenAsked()
    {
        var (host, registry, _) = NewHost();
        var (id, error) = await host.InstallAsync(Zip(("my-plugin/plugin.json", Manifest("zipped")), ("my-plugin/notes.txt", "x")), false, default);
        Assert.Null(error);
        Assert.Equal("zipped", id);
        Assert.True(File.Exists(Path.Combine(registry.Dir, "zipped", "notes.txt")));
        Assert.Equal(["zipped"], registry.Valid.Select(m => m.Id));
        Assert.True(Catalog.IsWidget("plugin.zipped.w") && Catalog.PluginWidget("plugin.zipped.w") is not null);

        (_, error) = await host.InstallAsync(Zip(("plugin.json", Manifest("zipped"))), false, default);
        Assert.Contains("already installed", error);
        (_, error) = await host.InstallAsync(Zip(("plugin.json", Manifest("zipped").Replace("1.0.0", "2.0.0"))), true, default);
        Assert.Null(error);
        Assert.Equal("2.0.0", registry.Get("zipped")!.Version);
        Assert.False(File.Exists(Path.Combine(registry.Dir, "zipped", "notes.txt"))); // replaced, not merged
        Assert.Empty(Directory.GetDirectories(registry.Dir, ".staging-*"));
    }

    [Theory]
    [InlineData("../evil/plugin.json", "unsafe path")]
    [InlineData("a/plugin.json|b/x.txt", "no plugin.json")]
    [InlineData("readme.txt", "no plugin.json")]
    public async Task InstallRefusesUnsafeOrIncompleteArchives(string names, string reason)
    {
        var (host, registry, _) = NewHost();
        var files = names.Split('|').Select(n => (n, n.EndsWith("plugin.json") ? Manifest("x1") : "x")).ToArray();
        // a root manifest next to the escaping entry, so the archive gets as far as unpacking
        if (names.StartsWith("..")) files = [.. files, ("plugin.json", Manifest("x1"))];
        var (_, error) = await host.InstallAsync(Zip(files), false, default);
        Assert.Contains(reason, error);
        Assert.False(Directory.Exists(Path.Combine(_root, "evil")));
        Assert.Empty(registry.Valid);
        Assert.Empty(Directory.GetDirectories(registry.Dir));
        (_, error) = await host.InstallAsync(new MemoryStream(Encoding.UTF8.GetBytes("not a zip")), false, default);
        Assert.Contains("not a .zip", error);
    }

    [Fact]
    public async Task UninstallDeletesTheFolderTheDataAndTheSettings()
    {
        Plugin("gone", Manifest("gone"));
        var (host, registry, cfg) = NewHost("""{"plugins":[{"id":"gone","enabled":true,"settings":{"k":"v"}},{"id":"kept"}]}""");
        Directory.CreateDirectory(host.DataDir("gone"));
        Assert.Null(await host.UninstallAsync("gone", keepData: false));
        Assert.False(Directory.Exists(Path.Combine(registry.Dir, "gone")));
        Assert.False(Directory.Exists(host.DataDir("gone")));
        Assert.Equal(["kept"], cfg.Get().Plugins.Select(p => p.Id));
        Assert.Empty(registry.Valid);
        Assert.Equal("no such plugin", await host.UninstallAsync("gone", false));
        Assert.Equal("no such plugin", await host.UninstallAsync("..", false));
    }

    // ------------------------------------------------------------ review fixes: one sidecar per plugin, ordered restarts

    private const string Recorder = """
        import json, os, sys
        log = os.path.join(os.environ["PHONEBOX_DATA_DIR"], "events.log")
        def note(what):
            with open(log, "a") as f: f.write(what + "\n")
        note("start")
        for line in sys.stdin:
            m = json.loads(line)
            if m["type"] == "shutdown":
                note("stop")
                break
        """;

    private static string? Python() =>
        ToolProcess.Find("python3") is { } p3 && !p3.Contains("WindowsApps") ? "python3" : ToolProcess.Find("python") is { } p && !p.Contains("WindowsApps") ? "python" : null;

    private (PluginHost Host, PluginRegistry Registry, string Events)? RecorderHost(string python)
    {
        Plugin("rec", Manifest("rec", extra: $$""","backend":{"command":{{JsonSerializer.Serialize(python)}},"args":["main.py"]}"""), ("main.py", Recorder));
        var (host, registry, _) = NewHost("""{"plugins":[{"id":"rec","enabled":true}]}""");
        return (host, registry, Path.Combine(host.DataDir("rec"), "events.log"));
    }

    private static string[] Events(string file) => File.Exists(file) ? File.ReadAllLines(file) : [];

    [Fact]
    public async Task ConcurrentReconcilesStartOneSidecarAndAnUnchangedRescanKeepsIt()
    {
        if (Python() is not { } python) return;
        var (host, registry, events) = RecorderHost(python)!.Value;
        try
        {
            Parallel.For(0, 24, _ => host.Reconcile(force: true));
            await Until(() => Events(events).Length > 0);
            await Task.Delay(800);
            Assert.Equal(["start"], Events(events));

            registry.Scan(); // new manifest objects, same plugin.json
            host.Reconcile(force: true);
            await Task.Delay(800);
            Assert.Equal(["start"], Events(events));

            var manifest = Path.Combine(registry.Dir, "rec", "plugin.json");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("1.0.0", "1.0.1")); // an update: restart, in order
            registry.Scan();
            host.Reconcile(force: true);
            await Until(() => Events(events).Length == 3);
            Assert.Equal(["start", "stop", "start"], Events(events));
        }
        finally { await host.StopAsync(default); }
    }

    [Fact]
    public async Task RestartWaitsForTheOldSidecarToExit()
    {
        if (Python() is not { } python) return;
        var (host, _, events) = RecorderHost(python)!.Value;
        try
        {
            host.Reconcile(force: true);
            await Until(() => Events(events).Length == 1);
            await host.RestartAsync("rec");
            await Until(() => Events(events).Length == 3);
            Assert.Equal(["start", "stop", "start"], Events(events));
        }
        finally { await host.StopAsync(default); }
    }

    [Fact]
    public async Task InstallRefusesDuplicateNamesAndFindsAPluginCopiedInByHand()
    {
        var (host, registry, _) = NewHost();
        var (_, error) = await host.InstallAsync(Zip(("plugin.json", Manifest("dup")), ("notes.txt", "a"), ("NOTES.txt", "b")), false, default);
        Assert.Contains("same file twice", error);
        Assert.Empty(Directory.GetDirectories(registry.Dir));

        Plugin("hand-made", Manifest("handy"));
        Plugin("taken", Manifest("someone-else"));
        registry.Scan();
        (_, error) = await host.InstallAsync(Zip(("plugin.json", Manifest("handy"))), false, default);
        Assert.Contains("already installed in the folder 'hand-made'", error);
        (_, error) = await host.InstallAsync(Zip(("plugin.json", Manifest("handy").Replace("1.0.0", "2.0.0"))), true, default);
        Assert.Null(error);
        Assert.False(Directory.Exists(Path.Combine(registry.Dir, "hand-made")));
        Assert.Equal("2.0.0", registry.Get("handy")!.Version);
        (_, error) = await host.InstallAsync(Zip(("plugin.json", Manifest("taken"))), true, default);
        Assert.Contains("belongs to another plugin", error);
        Assert.Equal("someone-else", registry.All.Single(f => f.Folder == "taken").Manifest!.Id);
        Assert.Contains("\"id\":\"handy\"", Catalog.AllPluginsJson); // the editor's live preview reads this list
        Assert.Empty(Directory.GetDirectories(registry.Dir, ".staging-*"));
    }

    // ------------------------------------------------------------ final review fixes

    private const string Stubborn = """
        import os, sys, time
        with open(os.path.join(os.environ["PHONEBOX_DATA_DIR"], "pid"), "w") as f: f.write(str(os.getpid()))
        time.sleep(600)  # ignores "shutdown": the agent has to end it
        """;

    private static bool Alive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task ShuttingDownWaitsForASidecarThatWasJustTurnedOff()
    {
        if (Python() is not { } python) return;
        Plugin("stub", Manifest("stub", extra: $$""","backend":{"command":{{JsonSerializer.Serialize(python)}},"args":["main.py"]}"""), ("main.py", Stubborn));
        var (host, _, cfg) = NewHost("""{"plugins":[{"id":"stub","enabled":true}]}""");
        var pidFile = Path.Combine(host.DataDir("stub"), "pid");
        host.Reconcile(force: true);
        await Until(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Length > 0);
        var pid = int.Parse(File.ReadAllText(pidFile));
        Assert.Null(cfg.Save("""{"plugins":[{"id":"stub","enabled":false}]}"""));
        host.Reconcile(force: true); // stops it in the background…
        await host.StopAsync(default); // …and the agent shuts down right away
        Assert.False(Alive(pid), "the sidecar outlived the agent");
    }

    [Fact]
    public async Task AFolderWithTheIdThatDidNotLoadCanBeReplacedByAnUpdate()
    {
        Plugin("fixme", "{ broken");
        var (host, registry, _) = NewHost();
        var (_, error) = await host.InstallAsync(Zip(("plugin.json", Manifest("fixme"))), false, default);
        Assert.Contains("exists but is not loaded", error);
        (_, error) = await host.InstallAsync(Zip(("plugin.json", Manifest("fixme"))), true, default);
        Assert.Null(error);
        Assert.Equal("fixme", registry.Get("fixme")!.Id);
    }

    [Fact]
    public void ThePhoneGetsAHashOfEachWidgetUiThatChangesWithTheFile()
    {
        var dir = Plugin("hashed", Manifest("hashed", """[{"id":"w","name":"W","ui":"ui.html"}]"""), ("ui.html", "<p>one</p>"));
        var registry = new PluginRegistry(Path.Combine(_root, "plugins"));
        registry.Scan();
        string Hash() => JsonDocument.Parse(JsonSerializer.Serialize(Catalog.PluginPublic("hashed"))).RootElement.GetProperty("widgets")[0].GetProperty("UiHash").GetString()!;
        var first = Hash();
        Assert.Matches("^[0-9a-f]{16}$", first);
        var scans = registry.Scans;
        File.WriteAllText(Path.Combine(dir, "ui.html"), "<p>two</p>");
        registry.Scan();
        Assert.NotEqual(first, Hash());
        Assert.True(registry.Scans > scans); // part of the phone's config version (Program.PublicVersion)
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition(), "timed out");
    }
}
