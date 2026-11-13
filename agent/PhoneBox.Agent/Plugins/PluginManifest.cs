using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// A community plugin's plugin.json: who it is, how to start its backend (a "sidecar" process that talks JSON lines
/// over stdin/stdout) and the widgets it adds to the catalog. See docs/PLUGINS.md.
/// </summary>
public sealed partial class PluginManifest
{
    public const int ApiVersionSupported = 1;
    public const string FileName = "plugin.json";
    private const int MaxUiBytes = 256 * 1024;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public int ApiVersion { get; set; }
    /// <summary>The sidecar process; null for plugins that only add widgets (an HTML UI with the network permission).</summary>
    public PluginBackend? Backend { get; set; }
    /// <summary>notify: may show notifications on the phone. network: its widget UIs may fetch https:// URLs.</summary>
    public List<string> Permissions { get; set; } = [];
    /// <summary>Plugin-wide settings (same schema as a widget option, plus "secret": true for tokens).</summary>
    public List<JsonElement> Settings { get; set; } = [];
    public List<PluginWidget> Widgets { get; set; } = [];

    /// <summary>The folder the manifest was loaded from.</summary>
    [JsonIgnore] public string Directory { get; private set; } = "";

    /// <summary>The folder and a hash of plugin.json: a rescan restarts a running plugin only when this changes.</summary>
    [JsonIgnore] public string Fingerprint { get; private set; } = "";

    public static readonly string[] KnownPermissions = ["notify", "network"];

    public bool Has(string permission) => Permissions.Contains(permission);

    public string TypeOf(PluginWidget w) => $"plugin.{Id}.{w.Id}";

    public PluginWidget? Widget(string widgetId) => Widgets.FirstOrDefault(w => w.Id == widgetId);

    /// <summary>Keys of the plugin settings and widget options marked "secret": they never go to the phone.</summary>
    public static IEnumerable<string> SecretKeys(IEnumerable<JsonElement> options) => options
        .Where(o => o.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.True && o.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
        .Select(o => o.GetProperty("key").GetString()!);

    /// <summary>Default values of an option list ("default" of each entry).</summary>
    public static Dictionary<string, JsonElement> Defaults(IEnumerable<JsonElement> options) => options
        .Where(o => o.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && o.TryGetProperty("default", out _))
        .GroupBy(o => o.GetProperty("key").GetString()!)
        .ToDictionary(g => g.Key, g => g.First().GetProperty("default").Clone());

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads and validates dir/plugin.json. Returns the manifest, or null and a readable error.</summary>
    public static (PluginManifest? Manifest, string? Error) Load(string dir)
    {
        var file = Path.Combine(dir, FileName);
        if (!File.Exists(file)) return (null, $"no {FileName}");
        PluginManifest? m;
        try
        {
            if (new FileInfo(file).Length > 256 * 1024) return (null, $"{FileName} is too large");
            var bytes = File.ReadAllBytes(file);
            m = JsonSerializer.Deserialize<PluginManifest>(bytes, ReadOpts);
            if (m is not null) m.Fingerprint = Path.GetFullPath(dir) + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return (null, $"{FileName}: {ex.Message}"); }
        if (m is null) return (null, $"{FileName} is empty");
        m.Directory = Path.GetFullPath(dir);
        var error = m.Validate();
        return error is null ? (m, null) : (null, error);
    }

    /// <summary>Strict checks: everything a plugin declares ends up in URLs, CSS classes, the catalog or a process start.</summary>
    public string? Validate()
    {
        Permissions ??= [];
        Settings ??= [];
        Widgets ??= [];
        if (!IdRegex().IsMatch(Id ?? "")) return "id must be 2-40 characters: lowercase letters, digits and '-' (starting with a letter or digit)";
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60) return "name is required (at most 60 characters)";
        if (string.IsNullOrWhiteSpace(Version) || Version.Length > 32) return "version is required (at most 32 characters)";
        if (ApiVersion != ApiVersionSupported) return $"apiVersion {ApiVersion} is not supported (this agent speaks {ApiVersionSupported})";
        if (Author?.Length > 80 || Description?.Length > 500 || Homepage?.Length > 300) return "author, description or homepage is too long";
        if (Homepage is not null && !Homepage.StartsWith("https://", StringComparison.Ordinal)) return "homepage must be an https:// URL";
        if (Permissions.FirstOrDefault(p => !KnownPermissions.Contains(p)) is { } unknown) return $"unknown permission '{unknown}' (known: {string.Join(", ", KnownPermissions)})";
        if (OptionsError(Settings, "settings") is { } settingsError) return settingsError;
        if (Backend is not null && Backend.Validate(Directory) is { } backendError) return backendError;
        if (Widgets.Count > 32) return "at most 32 widgets";
        if (Widgets.Count == 0 && Backend is null) return "a plugin needs a backend or at least one widget";
        var ids = new HashSet<string>();
        foreach (var w in Widgets)
        {
            if (w is null) return "widgets: empty entry";
            if (!WidgetIdRegex().IsMatch(w.Id ?? "")) return "widget id must be 1-40 characters: lowercase letters, digits and '-'";
            if (!ids.Add(w.Id!)) return $"widget '{w.Id}' is declared twice";
            var at = $"widget '{w.Id}'";
            if (string.IsNullOrWhiteSpace(w.Name) || w.Name.Length > 60) return $"{at}: name is required (at most 60 characters)";
            if (w.Desc?.Length > 300 || w.Icon?.Length > 16) return $"{at}: desc or icon is too long";
            if (w.W is < 1 or > Catalog.Cols || w.H is < 1 or > Catalog.Rows) return $"{at}: w must be 1-{Catalog.Cols} and h 1-{Catalog.Rows}";
            w.MinW = Math.Clamp(w.MinW ?? 1, 1, w.W);
            w.MinH = Math.Clamp(w.MinH ?? 1, 1, w.H);
            w.Options ??= [];
            w.Actions ??= [];
            if (OptionsError(w.Options, at + " options") is { } optionsError) return optionsError;
            if (w.Actions.Count > 32 || w.Actions.Any(a => a is null || !ActionRegex().IsMatch(a)))
                return $"{at}: at most 32 actions, named with lowercase letters, digits, '-', '_' or '.'";
            if (w.Ui is null && w.View is null) return $"{at}: give a \"ui\" (HTML file) or a \"view\"";
            if (w.Ui is not null && UiError(w.Ui) is { } uiError) return $"{at}: {uiError}";
            if (w.View is not null)
            {
                if (w.View.Style is not ("auto" or "big" or "gauge" or "list")) return $"{at}: view.style must be auto, big, gauge or list";
                if (!PushStore.ValidKey(w.View.Key ?? "")) return $"{at}: view.key must be 1-64 characters (letters, digits, '_', '.', '-')";
            }
        }
        return null;
    }

    /// <summary>The full path of a widget's HTML file, or null if it is not one this plugin declares.</summary>
    public string? UiFile(string widgetId) =>
        Widget(widgetId)?.Ui is { } ui && UiError(ui) is null ? Path.GetFullPath(Path.Combine(Directory, ui)) : null;

    private string? UiError(string ui)
    {
        if (!UiPathRegex().IsMatch(ui) || ui.Split('/').Any(part => part is "" or "." or "..")) return "ui must be a relative path to an .html file inside the plugin folder";
        var full = Path.GetFullPath(Path.Combine(Directory, ui));
        if (!full.StartsWith(Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return "ui must stay inside the plugin folder";
        if (!File.Exists(full)) return $"ui file {ui} is missing";
        if (new FileInfo(full).Length > MaxUiBytes) return $"ui file {ui} is larger than {MaxUiBytes / 1024} KB";
        return null;
    }

    private static string? OptionsError(List<JsonElement> options, string at)
    {
        if (options.Count > 40) return $"{at}: at most 40 entries";
        var keys = new HashSet<string>();
        foreach (var o in options)
        {
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String || !PushStore.ValidKey(k.GetString()!))
                return $"{at}: every entry needs a \"key\" (letters, digits, '_', '.', '-')";
            if (!keys.Add(k.GetString()!)) return $"{at}: key '{k.GetString()}' is declared twice";
            if (o.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is not ("text" or "textarea" or "number" or "bool" or "select" or "lines" or "color"))
                return $"{at}: option '{k.GetString()}' has an unsupported type '{t.GetString()}' (text, textarea, number, bool, select, lines, color)";
        }
        return null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    internal static partial Regex WidgetIdRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.\-]{0,39}$")]
    internal static partial Regex ActionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-/.]{1,120}\.html$")]
    private static partial Regex UiPathRegex();
}

/// <summary>How to start the sidecar; the per-OS entries override command/args on that system.</summary>
public sealed class PluginBackend
{
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public PluginCommand? Windows { get; set; }
    public PluginCommand? Linux { get; set; }
    public PluginCommand? Macos { get; set; }

    /// <summary>The command and arguments for this computer.</summary>
    public (string Command, List<string> Args) ForThisOs()
    {
        var o = OperatingSystem.IsWindows() ? Windows : OperatingSystem.IsMacOS() ? Macos : Linux;
        return o is null ? (Command, Args ?? []) : (o.Command, o.Args ?? []);
    }

    /// <summary>
    /// The program to start: a bare name ("python3", "node") is looked up on PATH; a path ("./bin/gitea-plugin")
    /// is taken relative to the plugin folder and must stay inside it.
    /// </summary>
    public static string Resolve(string command, string pluginDir)
    {
        if (!command.Contains('/') && !command.Contains('\\')) return command;
        return Path.GetFullPath(Path.Combine(pluginDir, command));
    }

    internal string? Validate(string pluginDir)
    {
        foreach (var (cmd, args) in new[] { (Command, Args), (Windows?.Command, Windows?.Args), (Linux?.Command, Linux?.Args), (Macos?.Command, Macos?.Args) })
        {
            if (cmd is null && args is null) continue;
            if (string.IsNullOrWhiteSpace(cmd) || cmd.Length > 200) return "backend.command is required (at most 200 characters)";
            if ((args?.Count ?? 0) > 32 || (args ?? []).Any(a => a is null || a.Length > 1000)) return "backend.args: at most 32 arguments of 1000 characters";
            if ((cmd.Contains('/') || cmd.Contains('\\')) && (Path.IsPathRooted(cmd)
                || !Resolve(cmd, pluginDir).StartsWith(pluginDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                return "backend.command: a path must be relative and stay inside the plugin folder (or use a program name on PATH)";
        }
        return null;
    }
}

public sealed class PluginCommand
{
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
}

public sealed class PluginWidget
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string? Desc { get; set; }
    public int W { get; set; } = 3;
    public int H { get; set; } = 2;
    public int? MinW { get; set; }
    public int? MinH { get; set; }
    /// <summary>Per-widget options, edited in the layout editor (same schema as widgets.json).</summary>
    public List<JsonElement> Options { get; set; } = [];
    /// <summary>Actions the widget may send to the backend (anything else is refused by the agent).</summary>
    public List<string> Actions { get; set; } = [];
    /// <summary>A custom HTML/JS UI, shown in a sandboxed iframe.</summary>
    public string? Ui { get; set; }
    /// <summary>Or a built-in look: one of the plugin's state keys rendered like a "Pushed value" widget.</summary>
    public PluginView? View { get; set; }
}

public sealed class PluginView
{
    /// <summary>auto, big, gauge or list (the Pushed value widget's styles).</summary>
    public string Style { get; set; } = "auto";
    /// <summary>Which state key (sent by the backend with {"type":"state","key":…}) to show.</summary>
    public string Key { get; set; } = "";
    public string? Unit { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
}
