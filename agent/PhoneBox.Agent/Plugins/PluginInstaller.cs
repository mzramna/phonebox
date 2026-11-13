using System.IO.Compression;

namespace PhoneBox.Agent;

/// <summary>
/// Adds a plugin from a .zip (Settings › Plugins › Add plugin): the archive holds plugin.json at its root or in a
/// single top-level folder. Nothing in it runs here; the plugin stays off until the user turns it on.
/// </summary>
public static class PluginInstaller
{
    public const long MaxZipBytes = 20 * 1024 * 1024;
    private const long MaxUnpackedBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 2000;

    /// <summary>Unpacks and validates the archive in a staging folder inside <paramref name="pluginsDir"/>.</summary>
    /// <returns>The staging folder and its manifest, or an error (the staging folder is then already removed).</returns>
    public static async Task<(string? Staging, PluginManifest? Manifest, string? Error)> UnpackAsync(Stream zip, string pluginsDir, CancellationToken ct)
    {
        Directory.CreateDirectory(pluginsDir);
        var staging = Path.Combine(pluginsDir, ".staging-" + Guid.NewGuid().ToString("N"));
        var tmp = staging + ".zip";
        try
        {
            await using (var file = File.Create(tmp))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await zip.ReadAsync(buf, ct)) > 0)
                {
                    if ((total += n) > MaxZipBytes) return Fail($"the file is larger than {MaxZipBytes / 1024 / 1024} MB");
                    await file.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            ZipArchive archive;
            try { archive = ZipFile.OpenRead(tmp); }
            catch (InvalidDataException) { return Fail("not a .zip file"); }
            using (archive)
            {
                var entries = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
                if (entries.Count == 0) return Fail("the .zip file is empty");
                if (archive.Entries.Count > MaxEntries) return Fail($"more than {MaxEntries} files");
                if (entries.Sum(e => e.Length) > MaxUnpackedBytes) return Fail($"more than {MaxUnpackedBytes / 1024 / 1024} MB unpacked");
                // plugin.json at the root, or everything in one folder that has it ("my-plugin/plugin.json")
                var names = entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
                var prefix = names.Contains(PluginManifest.FileName) ? "" : null;
                if (prefix is null)
                {
                    var tops = names.Select(x => x.Split('/')[0]).Distinct().ToList();
                    if (tops.Count == 1 && names.Contains($"{tops[0]}/{PluginManifest.FileName}")) prefix = tops[0] + "/";
                }
                if (prefix is null) return Fail($"no {PluginManifest.FileName} at the top of the .zip file (or in its only folder)");
                var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                foreach (var e in entries)
                {
                    var name = e.FullName.Replace('\\', '/');
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var dest = Path.GetFullPath(Path.Combine(staging, name[prefix.Length..]));
                    if (!dest.StartsWith(root, StringComparison.Ordinal)) return Fail($"unsafe path in the .zip file: {PluginConnection.Shorten(name, 80)}");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, overwrite: false);
                }
            }
            var (m, error) = PluginManifest.Load(staging);
            if (m is null) return Fail(error);
            return (staging, m, null);
        }
        finally { TryDelete(tmp); }

        (string?, PluginManifest?, string?) Fail(string? error)
        {
            TryDeleteDir(staging);
            return (null, null, error);
        }
    }

    internal static void TryDelete(string file) { try { File.Delete(file); } catch { } }

    internal static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
}
