using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

public sealed record PluginStateItem(JsonElement Data, long At);

public sealed record PluginActionResult(bool Ok, string? Error = null, JsonElement? Data = null);

/// <summary>
/// Runs the backends ("sidecars") of the enabled plugins and keeps what they publish for the panel.
/// <list type="bullet">
/// <item>Only plugins the user enabled in the settings run; disabling one stops it and forgets its data.</item>
/// <item>A sidecar that exits is restarted with a growing delay (1 s … 60 s).</item>
/// <item>The phone sends actions by page and widget id only; the agent checks that the widget exists and declares
///   the action before passing it on (like the Command widget, nothing the phone sends is executed).</item>
/// </list>
/// Protocol: docs/PLUGINS.md.
/// </summary>
public sealed class PluginHost(PluginRegistry registry, ConfigStore cfg, PushStore push, ILogger<PluginHost> log) : BackgroundService
{
    public const int MaxStateBytes = PushStore.MaxBytes;
    public const int MaxStateKeys = 64;
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, PluginRunner> _runners = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PluginStateItem>> _state = new();
    private readonly ConcurrentDictionary<string, Queue<long>> _notifyTimes = new();
    private readonly ConcurrentDictionary<string, byte> _busy = new(); // being installed or removed: not started meanwhile
    private readonly ConcurrentDictionary<string, byte> _stopping = new(); // a sidecar still shutting down: no second one yet
    private readonly SemaphoreSlim _install = new(1, 1);
    private readonly Lock _reconcile = new();
    private volatile bool _dirty = true;
    private volatile bool _closing; // the agent is shutting down: no new sidecar may start
    private (long Config, long Scans) _seen;
    private long _version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Changes whenever a plugin publishes data; the panel reloads /api/plugins/state when it does.</summary>
    public long Version => Interlocked.Read(ref _version);

    internal ILogger Log => log;

    /// <summary>A folder where a plugin may keep files of its own (passed as PHONEBOX_DATA_DIR).</summary>
    internal string DataDir(string id) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cfg.Path))!, "plugin-data", id);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Reconcile(); } // cheap when nothing changed
            catch (Exception ex) { log.LogWarning("plugins: {Msg}", ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        lock (_reconcile) _closing = true;
        await base.StopAsync(ct);
        await Task.WhenAll(_runners.Values.Select(r => r.StopAsync()));
        _runners.Clear();
    }

    /// <summary>
    /// Starts, stops and reconfigures the sidecars so they match the settings and the plugins folder. Runs under a lock
    /// (it is called from the timer and from requests), and does nothing unless the settings, the plugins folder or a
    /// sidecar changed since the last pass, or <paramref name="force"/> is set.
    /// </summary>
    internal void Reconcile(bool force = false)
    {
        lock (_reconcile)
        {
            if (_closing) return;
            var seen = (cfg.Version, registry.Scans);
            if (!force && !_dirty && seen == _seen) return;
            _dirty = false;
            _seen = seen;
            var c = cfg.Get();
            var wanted = registry.Valid.Where(m => c.PluginEnabled(m.Id)).ToDictionary(m => m.Id);
            // a rescan reads every manifest again: only a plugin whose plugin.json (or folder) changed is restarted
            foreach (var (id, runner) in _runners)
                if (!wanted.TryGetValue(id, out var m) || m.Fingerprint != runner.Manifest.Fingerprint || m.Backend is null)
                    if (_runners.TryRemove(id, out var gone)) StopInBackground(id, gone);
            foreach (var id in _state.Keys.Where(id => !wanted.ContainsKey(id)))
                if (_state.TryRemove(id, out _)) Bump();
            foreach (var m in wanted.Values.Where(m => m.Backend is not null && !_busy.ContainsKey(m.Id) && !_stopping.ContainsKey(m.Id)))
            {
                if (!_runners.TryGetValue(m.Id, out var runner))
                {
                    log.LogInformation("plugin {Id} {Version}: starting", m.Id, m.Version);
                    _runners[m.Id] = runner = new PluginRunner(m, this);
                    runner.Configure(ConfigPayload(c, m));
                    runner.Start(); // only once it is the plugin's one runner: no second process can appear
                }
                else runner.Configure(ConfigPayload(c, m));
            }
        }
    }

    /// <summary>Stops a sidecar; a new one for the same plugin starts only after this one has exited.</summary>
    private void StopInBackground(string id, PluginRunner runner)
    {
        _stopping[id] = 0;
        _ = runner.StopAsync().ContinueWith(stopped =>
        {
            _stopping.TryRemove(id, out _);
            _dirty = true;
            try { Reconcile(force: true); } // start its successor (an updated plugin) right away
            catch (Exception ex) { log.LogWarning("plugins: {Msg}", ex.Message); }
        }, TaskScheduler.Default);
    }

    /// <summary>Messages from a sidecar that is being replaced or stopped are dropped.</summary>
    internal bool IsCurrent(PluginRunner runner) => _runners.TryGetValue(runner.Manifest.Id, out var current) && ReferenceEquals(current, runner);

    /// <summary>Takes the plugin's runner out under the reconcile lock, so no new one starts until <paramref name="hold"/> is cleared.</summary>
    private PluginRunner? Detach(string id, ConcurrentDictionary<string, byte> hold)
    {
        lock (_reconcile)
        {
            hold[id] = 0;
            return _runners.TryRemove(id, out var r) ? r : null;
        }
    }

    /// <summary>
    /// What a plugin's backend gets in "init" and "config": its settings (defaults from the manifest, then the
    /// user's values) and every widget of the plugin placed on a page, with its options.
    /// </summary>
    internal static JsonObject ConfigPayload(AgentConfig c, PluginManifest m)
    {
        var settings = PluginManifest.Defaults(m.Settings);
        foreach (var (k, v) in c.Plugin(m.Id)?.Settings ?? []) settings[k] = v;
        var prefix = $"plugin.{m.Id}.";
        var instances = c.Pages.SelectMany(p => (p.Widgets ?? []).Where(w => w.Type.StartsWith(prefix, StringComparison.Ordinal)).Select(w =>
        {
            var widget = w.Type[prefix.Length..];
            var options = m.Widget(widget) is { } pw ? PluginManifest.Defaults(pw.Options) : new Dictionary<string, JsonElement>();
            foreach (var (k, v) in w.Options ?? []) options[k] = v;
            return new { instance = $"{p.Id}/{w.Id}", page = p.Id, id = w.Id, widget, pageEnabled = p.Enabled, options };
        }));
        return JsonSerializer.SerializeToNode(new { settings, instances }, PluginConnection.Json)!.AsObject();
    }

    // ------------------------------------------------------------ data from the plugins

    internal string? SetState(PluginManifest m, string? key, JsonElement? data)
    {
        if (key is null || !PushStore.ValidKey(key)) return "state: \"key\" must be 1-64 characters (letters, digits, '_', '.', '-')";
        var items = _state.GetOrAdd(m.Id, _ => new());
        if (data is null || data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (items.TryRemove(key, out _)) Bump();
            return null;
        }
        if (Encoding.UTF8.GetByteCount(data.Value.GetRawText()) > MaxStateBytes) return $"state '{key}' is larger than {MaxStateBytes / 1024} KB";
        if (!items.ContainsKey(key) && items.Count >= MaxStateKeys) return $"at most {MaxStateKeys} state keys";
        items[key] = new PluginStateItem(data.Value.Clone(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Bump();
        return null;
    }

    private void Bump()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long v;
        do v = Interlocked.Read(ref _version);
        while (Interlocked.CompareExchange(ref _version, Math.Max(v + 1, now), v) != v);
    }

    /// <summary>The data of the enabled plugins, for the panel: {version, plugins: {id: {key: {data, at}}}}.</summary>
    public object Snapshot()
    {
        var c = cfg.Get();
        return new
        {
            version = Version,
            plugins = _state.Where(kv => c.PluginEnabled(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(x => x.Key, x => x.Value)),
        };
    }

    /// <summary>A notification from a plugin with the "notify" permission (at most 6 a minute).</summary>
    internal string? Notify(PluginManifest m, JsonElement body)
    {
        if (!m.Has("notify")) return "notify: the plugin does not have the \"notify\" permission";
        var times = _notifyTimes.GetOrAdd(m.Id, _ => new());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (times)
        {
            while (times.Count > 0 && now - times.Peek() > 60_000) times.Dequeue();
            if (times.Count >= 6) return "notify: more than 6 notifications a minute";
            times.Enqueue(now);
        }
        string? Str(string k, int max) => body.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? PluginConnection.Shorten(v.GetString()!, max) : null;
        var title = Str("title", 100) ?? m.Name;
        var text = Str("text", 500);
        var level = Str("level", 10) is "ok" or "warn" or "error" or "info" ? Str("level", 10) : "info";
        int? seconds = body.TryGetProperty("seconds", out var s) && s.TryGetInt32(out var n) ? Math.Clamp(n, 1, 60) : null;
        var vibrate = body.TryGetProperty("vibrate", out var vb) && vb.ValueKind == JsonValueKind.True;
        push.Emit("notify", JsonSerializer.SerializeToElement(new { title, text, level, seconds, vibrate }, PluginConnection.Json));
        return null;
    }

    // ------------------------------------------------------------ actions from the phone

    /// <summary>Passes a widget's action to the plugin, if that widget exists on that page and declares the action.</summary>
    public async Task<PluginActionResult> ActAsync(string id, string page, string widget, string name, JsonElement? args, CancellationToken ct)
    {
        var m = registry.Get(id);
        if (m is null) return new(false, "unknown plugin");
        var c = cfg.Get();
        if (!c.PluginEnabled(id)) return new(false, "the plugin is turned off in the settings");
        var w = c.Pages.FirstOrDefault(p => p.Id == page)?.Widgets?.FirstOrDefault(x => x.Id == widget);
        var prefix = $"plugin.{id}.";
        if (w is null || !w.Type.StartsWith(prefix, StringComparison.Ordinal)) return new(false, "no such widget");
        var pw = m.Widget(w.Type[prefix.Length..]);
        if (pw is null || !pw.Actions.Contains(name)) return new(false, $"the widget does not declare the action '{name}'");
        if (!_runners.TryGetValue(id, out var runner)) return new(false, "the plugin has no backend running");
        return await runner.RequestAsync(new { type = "action", instance = $"{page}/{widget}", page, widget = pw.Id, name, args }, ActionTimeout, ct);
    }

    // ------------------------------------------------------------ adding and removing plugins (settings page)

    /// <summary>Adds (or, with <paramref name="replace"/>, updates) a plugin from a .zip. It stays off until turned on.</summary>
    public async Task<(string? Id, string? Error)> InstallAsync(Stream zip, bool replace, CancellationToken ct)
    {
        await _install.WaitAsync(ct);
        try
        {
            string? staging;
            PluginManifest? m;
            string? error;
            try { (staging, m, error) = await PluginInstaller.UnpackAsync(zip, registry.Dir, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return (null, "could not add the plugin: " + ex.Message); }
            if (m is null) return (null, error);
            var target = Path.Combine(registry.Dir, m.Id);
            // the plugin may already be there under another folder name (copied in by hand)
            var installed = registry.All.FirstOrDefault(f => f.Manifest?.Id == m.Id);
            var occupant = registry.All.FirstOrDefault(f => string.Equals(f.Folder, m.Id, StringComparison.OrdinalIgnoreCase));
            if (occupant is not null && occupant.Manifest?.Id != m.Id)
            {
                PluginInstaller.TryDeleteDir(staging!);
                return (null, $"the folder '{occupant.Folder}' in the plugins folder belongs to another plugin; rename or remove it first");
            }
            if ((installed is not null || Directory.Exists(target)) && !replace)
            {
                PluginInstaller.TryDeleteDir(staging!);
                return (null, $"the plugin '{m.Id}' is already installed in the folder '{installed?.Folder ?? m.Id}' (choose Update to replace it)");
            }
            var old = Detach(m.Id, _busy);
            try
            {
                if (old is not null) await old.StopAsync();
                if (installed is not null) Directory.Delete(Path.Combine(registry.Dir, installed.Folder), true);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(staging!, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginInstaller.TryDeleteDir(staging!);
                return (null, "could not replace the plugin's folder: " + ex.Message);
            }
            finally
            {
                registry.Scan();
                _busy.TryRemove(m.Id, out _);
            }
            log.LogInformation("plugin {Id} {Version} installed", m.Id, m.Version);
            Reconcile(force: true);
            return (m.Id, null);
        }
        finally { _install.Release(); }
    }

    /// <summary>Stops the plugin, deletes its folder (and its data, unless kept) and forgets its settings.</summary>
    public async Task<string?> UninstallAsync(string id, bool keepData)
    {
        var found = registry.All.FirstOrDefault(f => f.Manifest?.Id == id) ?? registry.All.FirstOrDefault(f => f.Folder == id);
        if (found is null) return "no such plugin";
        var dir = Path.GetFullPath(Path.Combine(registry.Dir, found.Folder));
        if (!dir.StartsWith(Path.GetFullPath(registry.Dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return "no such plugin";
        var pluginId = found.Manifest?.Id ?? id;
        await _install.WaitAsync();
        var r = Detach(pluginId, _busy);
        try
        {
            if (r is not null) await r.StopAsync();
            try { Directory.Delete(dir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "could not delete the plugin's folder: " + ex.Message; }
            if (!keepData && found.Manifest is not null) PluginInstaller.TryDeleteDir(DataDir(pluginId));
            // edit a copy: the config object from cfg.Get() is shared with every other request
            var copy = JsonSerializer.SerializeToNode(cfg.Get(), ConfigStore.WriteOpts)!.AsObject();
            if (copy["plugins"] is JsonArray list && list.Any(p => p?["id"]?.GetValue<string>() == pluginId))
            {
                copy["plugins"] = new JsonArray(list.Where(p => p?["id"]?.GetValue<string>() != pluginId).Select(p => p?.DeepClone()).ToArray());
                if (cfg.Save(copy.ToJsonString()) is { } saveError) log.LogWarning("plugin {Id}: could not forget its settings: {Error}", pluginId, saveError);
            }
            log.LogInformation("plugin {Id} uninstalled", pluginId);
            return null;
        }
        finally
        {
            registry.Scan();
            _busy.TryRemove(pluginId, out _);
            _install.Release();
            Reconcile(force: true);
        }
    }

    /// <summary>Stops the plugin's sidecar, waits until it has exited, then starts a new one.</summary>
    public async Task RestartAsync(string id)
    {
        var r = Detach(id, _stopping);
        try { if (r is not null) await r.StopAsync(); }
        finally { _stopping.TryRemove(id, out _); }
        Reconcile(force: true);
    }

    /// <summary>Every plugin found in the plugins folder, with its state, for the settings page.</summary>
    public object Status()
    {
        var c = cfg.Get();
        return new
        {
            dir = registry.Dir,
            plugins = registry.All.Select(f =>
            {
                var m = f.Manifest;
                var r = m is null ? null : _runners.GetValueOrDefault(m.Id);
                var enabled = m is not null && c.PluginEnabled(m.Id);
                return new
                {
                    folder = f.Folder,
                    error = f.Error,
                    m?.Id, m?.Name, m?.Version, m?.Author, m?.Description, m?.Homepage,
                    permissions = m?.Permissions,
                    settings = m?.Settings,
                    backend = m?.Backend is not null,
                    command = m?.Backend?.ForThisOs() is { } run ? string.Join(' ', new[] { run.Command }.Concat(run.Args)) : null,
                    widgets = m?.Widgets.Select(w => new { type = m.TypeOf(w), w.Name, w.Icon }),
                    enabled,
                    status = m is null ? "invalid" : !enabled ? "off" : m.Backend is null ? "ui-only" : r?.Status ?? "starting",
                    lastError = r?.LastError,
                    restarts = r?.Restarts ?? 0,
                    startedAt = r?.StartedAt,
                };
            }),
        };
    }
}

/// <summary>One running sidecar: starts it, reads its messages and restarts it when it exits.</summary>
internal sealed class PluginRunner
{
    private readonly PluginHost _host;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PluginActionResult>> _pending = new();
    private Task? _loop;
    private Process? _proc;
    private PluginConnection? _conn;
    private JsonObject _config = [];
    private string _configSig = "", _sentSig = "";
    private string? _lastStderr;

    public PluginManifest Manifest { get; }
    public string Status { get; private set; } = "starting";
    public string? LastError { get; private set; }
    public int Restarts { get; private set; }
    public long? StartedAt { get; private set; }

    public PluginRunner(PluginManifest manifest, PluginHost host)
    {
        Manifest = manifest;
        _host = host;
    }

    /// <summary>Starts the sidecar (and keeps restarting it) until <see cref="StopAsync"/>.</summary>
    public void Start() => _loop ??= Task.Run(() => RunLoop(_cts.Token));

    /// <summary>Keeps the latest settings and widget list; sent as "config" when they change while running.</summary>
    public void Configure(JsonObject config)
    {
        var sig = config.ToJsonString();
        lock (_cts)
        {
            if (sig == _configSig) return;
            _config = config;
            _configSig = sig;
        }
        if (_conn is { } conn && Status == "running") _ = SendConfig(conn, "config");
    }

    private async Task SendConfig(PluginConnection conn, string type)
    {
        JsonObject msg;
        string sig;
        lock (_cts)
        {
            sig = _configSig;
            msg = new JsonObject { ["type"] = type };
            if (type == "init")
            {
                msg["apiVersion"] = PluginManifest.ApiVersionSupported;
                msg["agentVersion"] = AppPaths.Version;
                msg["os"] = AppPaths.Os;
                msg["plugin"] = new JsonObject { ["id"] = Manifest.Id, ["version"] = Manifest.Version };
            }
            foreach (var (k, v) in _config) msg[k] = v?.DeepClone();
        }
        if (type == "config" && sig == _sentSig) return;
        try { await conn.SendAsync(msg, _cts.Token); _sentSig = sig; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task RunLoop(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var run = Stopwatch.StartNew();
            try { await RunOnce(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { LastError = ex.Message; }
            _conn = null;
            Fail("the plugin stopped");
            if (ct.IsCancellationRequested) break;
            Status = "crashed";
            Restarts++;
            if (run.Elapsed > TimeSpan.FromMinutes(1)) backoff = TimeSpan.FromSeconds(1);
            _host.Log.LogWarning("plugin {Id}: {Error}; restarting in {Sec} s", Manifest.Id, LastError ?? "stopped", backoff.TotalSeconds);
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
        Status = "stopped";
    }

    private async Task RunOnce(CancellationToken ct)
    {
        var (command, args) = Manifest.Backend!.ForThisOs();
        var psi = new ProcessStartInfo(PluginBackend.Resolve(command, Manifest.Directory))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Manifest.Directory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var data = _host.DataDir(Manifest.Id);
        Directory.CreateDirectory(data);
        psi.Environment["PHONEBOX_PLUGIN_ID"] = Manifest.Id;
        psi.Environment["PHONEBOX_PLUGIN_DIR"] = Manifest.Directory;
        psi.Environment["PHONEBOX_DATA_DIR"] = data;
        psi.Environment["PHONEBOX_API_VERSION"] = PluginManifest.ApiVersionSupported.ToString();
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + command);
        JobObject.Track(p);
        _proc = p;
        using var kill = ct.Register(() => Kill(p));
        var stderr = DrainStderr(p);
        var conn = new PluginConnection(p.StandardOutput, p.StandardInput);
        StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LastError = null;
        _lastStderr = null;
        Status = "running";
        _sentSig = "";
        await SendConfig(conn, "init");
        _conn = conn;
        await SendConfig(conn, "config"); // in case the settings changed while starting

        while (await conn.ReadAsync(ct) is { } msg) Handle(msg);

        // stdout closed: give the process a moment to exit on its own
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(3));
            try { await p.WaitForExitAsync(wait.Token); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Kill(p); }
        }
        await stderr.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ContinueWith(_ => { });
        LastError = p.HasExited ? $"exited with code {p.ExitCode}" + (_lastStderr is { } e ? $": {e}" : "") : "closed its output";
    }

    private async Task DrainStderr(Process p)
    {
        try
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _lastStderr = PluginConnection.Shorten(line.Trim(), 300);
                _host.Log.LogInformation("plugin {Id} (stderr): {Line}", Manifest.Id, _lastStderr);
            }
        }
        catch { }
    }

    private void Handle(PluginMessage msg)
    {
        // a sidecar being replaced or stopped may still print a line or two: it must not overwrite the new one's data
        if (msg.Type is "state" or "notify" && !_host.IsCurrent(this)) return;
        string? error = msg.Error;
        var b = msg.Body;
        string? Str(string k) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (error is null)
            switch (msg.Type)
            {
                case "state":
                    error = _host.SetState(Manifest, Str("key"), b.TryGetProperty("data", out var data) ? data : (JsonElement?)null);
                    break;
                case "result":
                    if (Str("id") is { } id && _pending.TryRemove(id, out var tcs))
                        tcs.TrySetResult(new PluginActionResult(
                            !b.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False,
                            Str("error") is { } err ? PluginConnection.Shorten(err, 300) : null,
                            b.TryGetProperty("data", out var d) && Encoding.UTF8.GetByteCount(d.GetRawText()) <= PluginHost.MaxStateBytes ? d.Clone() : null));
                    break;
                case "notify":
                    error = _host.Notify(Manifest, b);
                    break;
                case "log":
                    var text = PluginConnection.Shorten(Str("message") ?? "", 1000);
                    var level = Str("level") switch { "debug" => LogLevel.Debug, "warn" or "warning" => LogLevel.Warning, "error" => LogLevel.Error, _ => LogLevel.Information };
                    _host.Log.Log(level, "plugin {Id}: {Message}", Manifest.Id, text);
                    break;
                default:
                    error = $"unknown message type '{PluginConnection.Shorten(msg.Type, 40)}' ignored";
                    break;
            }
        if (error is not null) _host.Log.LogWarning("plugin {Id}: {Error}", Manifest.Id, error);
    }

    /// <summary>Sends an action and waits for the plugin's {"type":"result","id":…}.</summary>
    public async Task<PluginActionResult> RequestAsync(object action, TimeSpan timeout, CancellationToken ct)
    {
        if (_conn is not { } conn || Status != "running") return new(false, "the plugin is not running" + (LastError is { } e ? $" ({e})" : ""));
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<PluginActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var msg = JsonSerializer.SerializeToNode(action, PluginConnection.Json)!.AsObject();
            msg["id"] = id;
            await conn.SendAsync(msg, ct);
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException) { return new(false, "the plugin did not answer in time"); }
        catch (IOException) { return new(false, "the plugin stopped"); }
        finally { _pending.TryRemove(id, out _); }
    }

    private void Fail(string error)
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(new(false, error));
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>Asks the plugin to stop ("shutdown"), then ends it (and its children) if it does not within 3 s.</summary>
    public async Task StopAsync()
    {
        if (_conn is { } conn && _proc is { HasExited: false } p)
        {
            try
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await conn.SendAsync(new { type = "shutdown" }, t.Token);
                await p.WaitForExitAsync(t.Token);
            }
            catch { }
        }
        _cts.Cancel();
        if (_loop is { } loop) try { await loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        Fail("the plugin stopped");
    }
}
