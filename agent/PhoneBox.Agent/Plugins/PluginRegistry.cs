namespace PhoneBox.Agent;

/// <summary>
/// The plugins folder: one sub-folder per plugin, each with a plugin.json. Scanning reads the manifests only (no
/// plugin code runs here) and puts the valid plugins' widgets into the <see cref="Catalog"/>.
/// </summary>
public sealed class PluginRegistry(string dir)
{
    public sealed record Found(string Folder, PluginManifest? Manifest, string? Error);

    private const int MaxPlugins = 100;
    private volatile IReadOnlyList<Found> _found = [];
    private long _scans;

    /// <summary>Default: "plugins" next to config.json (%APPDATA%\PhoneBox\plugins, ~/.config/PhoneBox/plugins…).</summary>
    public static string DefaultDir(string configPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "plugins");

    public string Dir => dir;

    /// <summary>Every folder looked at, valid or not (the settings page shows the errors).</summary>
    public IReadOnlyList<Found> All => _found;

    public IEnumerable<PluginManifest> Valid => _found.Select(f => f.Manifest).OfType<PluginManifest>();

    public PluginManifest? Get(string id) => Valid.FirstOrDefault(m => m.Id == id);

    /// <summary>Goes up on every scan, so the host notices a plugin that was replaced on disk.</summary>
    public long Scans => Interlocked.Read(ref _scans);

    public void Scan()
    {
        var found = new List<Found>();
        if (Directory.Exists(dir))
        {
            var ids = new HashSet<string>();
            foreach (var folder in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal).Take(MaxPlugins))
            {
                var name = Path.GetFileName(folder);
                if (name.StartsWith('.')) continue;
                var (m, error) = PluginManifest.Load(folder);
                if (m is not null && !ids.Add(m.Id)) { error = $"another folder already provides the plugin '{m.Id}'"; m = null; }
                found.Add(new(name, m, error));
            }
        }
        _found = found;
        Catalog.SetPlugins(Valid);
        Interlocked.Increment(ref _scans);
    }
}
