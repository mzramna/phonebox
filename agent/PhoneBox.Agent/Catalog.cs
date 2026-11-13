using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>A page template: a ready-made widget layout the settings UI (and migration) can start from.</summary>
public sealed class TemplateDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Desc { get; set; } = "";
    public List<WidgetConfig> Widgets { get; set; } = [];
}

/// <summary>The widget types and page templates shipped with the agent (widgets.json, templates.json).</summary>
public static partial class Catalog
{
    public const int Cols = 12;
    public const int Rows = 6;

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static readonly string WidgetsJson = Read("widgets.json");
    public static readonly string TemplatesJson = Read("templates.json");

    public static readonly IReadOnlyList<TemplateDef> Templates =
        JsonSerializer.Deserialize<List<TemplateDef>>(TemplatesJson, Opts) ?? [];

    private static readonly Dictionary<string, (int MinW, int MinH)> Sizes = LoadSizes();

    /// <summary>
    /// A built-in widget type, or a plugin widget type ("plugin.&lt;plugin&gt;.&lt;widget&gt;"). Plugin types are accepted even
    /// while the plugin is missing or broken, so saving the settings never deletes them from the user's pages.
    /// </summary>
    public static bool IsWidget(string type) => Sizes.ContainsKey(type) || IsPluginType(type);

    public static (int MinW, int MinH) MinSize(string type) =>
        Sizes.TryGetValue(type, out var s) ? s : PluginWidget(type) is { } p ? (p.Widget.MinW ?? 1, p.Widget.MinH ?? 1) : (1, 1);

    // ------------------------------------------------------------ plugin widgets (see PluginRegistry)

    /// <summary>A widget a discovered plugin adds to the catalog.</summary>
    public sealed record PluginWidgetEntry(string Type, PluginManifest Plugin, PluginWidget Widget, IReadOnlySet<string> SecretOptions);

    private sealed record PluginState(IReadOnlyDictionary<string, PluginWidgetEntry> Widgets, IReadOnlyDictionary<string, PluginManifest> Plugins, string Json);

    private static volatile PluginState _plugins = new(new Dictionary<string, PluginWidgetEntry>(), new Dictionary<string, PluginManifest>(), "");

    public static bool IsPluginType(string type) => PluginTypeRegex().IsMatch(type);

    public static PluginWidgetEntry? PluginWidget(string type) => _plugins.Widgets.GetValueOrDefault(type);

    public static PluginManifest? Plugin(string id) => _plugins.Plugins.GetValueOrDefault(id);

    /// <summary>The built-in widgets plus those of every discovered plugin (category "Plugins"), for the layout editor.</summary>
    public static string AllWidgetsJson => _plugins.Json is { Length: > 0 } json ? json : WidgetsJson;

    /// <summary>Replaces the plugin widgets (called after each scan of the plugins folder).</summary>
    public static void SetPlugins(IEnumerable<PluginManifest> plugins)
    {
        var list = plugins.ToList();
        var widgets = new Dictionary<string, PluginWidgetEntry>();
        var all = JsonNode.Parse(WidgetsJson)!.AsArray();
        foreach (var p in list)
            foreach (var w in p.Widgets)
            {
                var type = p.TypeOf(w);
                widgets[type] = new(type, p, w, PluginManifest.SecretKeys(w.Options).ToHashSet());
                all.Add(new JsonObject
                {
                    ["type"] = type,
                    ["category"] = "Plugins",
                    ["name"] = w.Name,
                    ["icon"] = w.Icon ?? "🧩",
                    ["desc"] = (w.Desc ?? "") + $" (plugin: {p.Name} {p.Version})",
                    ["w"] = w.W, ["h"] = w.H, ["minW"] = w.MinW ?? 1, ["minH"] = w.MinH ?? 1,
                    ["plugin"] = p.Id,
                    // what the panel needs to draw it (the layout editor's live preview builds the phone's plugin list from these)
                    ["pluginName"] = p.Name,
                    ["pluginVersion"] = p.Version,
                    ["network"] = p.Has("network"),
                    ["ui"] = w.Ui is not null,
                    ["view"] = w.View is null ? null : JsonSerializer.SerializeToNode(w.View, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    ["actions"] = new JsonArray(w.Actions.Select(a => (JsonNode?)a).ToArray()),
                    ["options"] = new JsonArray(w.Options.Select(o => JsonNode.Parse(o.GetRawText())).ToArray()),
                });
            }
        _plugins = new(widgets, list.ToDictionary(p => p.Id), list.Count == 0 ? "" : all.ToJsonString());
    }

    /// <summary>What the panel needs to know about an installed plugin to draw its widgets (null if it is not installed).</summary>
    public static object? PluginPublic(string id) => Plugin(id) is not { } p ? null : new
    {
        p.Id,
        p.Name,
        p.Version,
        network = p.Has("network"),
        widgets = p.Widgets.Select(w => new { w.Id, type = p.TypeOf(w), w.Name, w.Icon, ui = w.Ui is not null, w.View, w.Actions }),
    };

    [GeneratedRegex("^plugin\\.[a-z0-9][a-z0-9-]{1,39}\\.[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex PluginTypeRegex();

    public static TemplateDef? Template(string? id) => id is null ? null : Templates.FirstOrDefault(t => t.Id == id);

    private static Dictionary<string, (int, int)> LoadSizes()
    {
        using var doc = JsonDocument.Parse(WidgetsJson);
        return doc.RootElement.EnumerateArray().ToDictionary(
            w => w.GetProperty("type").GetString()!,
            w => (w.GetProperty("minW").GetInt32(), w.GetProperty("minH").GetInt32()));
    }

    private static string Read(string name)
    {
        using var s = AppPaths.Resource(name) ?? throw new InvalidOperationException($"missing resource {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
