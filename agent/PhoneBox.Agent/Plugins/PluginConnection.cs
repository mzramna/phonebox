using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>A message from a plugin: its "type" and the whole JSON object; or a line that could not be read.</summary>
public sealed record PluginMessage(string Type, JsonElement Body, string? Error = null);

/// <summary>
/// The plugin protocol: one JSON object per line ("JSON lines") in both directions. Lines are bounded so a broken
/// plugin cannot make the agent buffer without limit. Independent of processes, so it is tested with plain streams.
/// </summary>
public sealed class PluginConnection(TextReader input, TextWriter output)
{
    /// <summary>The longest line accepted from a plugin (a state message carries at most 64 KB of data).</summary>
    public const int MaxLine = 128 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly char[] _buf = new char[4096];
    private int _pos, _len;

    /// <summary>Sends one message (serialized on one line). Concurrent senders are serialized.</summary>
    public async Task SendAsync(object message, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(message, Json); // never contains a raw newline
        await _write.WaitAsync(ct);
        try
        {
            await output.WriteAsync(line.AsMemory(), ct);
            await output.WriteAsync("\n".AsMemory(), ct);
            await output.FlushAsync(ct);
        }
        finally { _write.Release(); }
    }

    /// <summary>The next message; null when the plugin closed its output. Unreadable lines come back with an Error.</summary>
    public async Task<PluginMessage?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var (line, tooLong) = await ReadLineAsync(ct);
            if (line is null) return null;
            if (tooLong) return new PluginMessage("", default, $"line longer than {MaxLine / 1024} KB ignored");
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
                    return new PluginMessage("", default, "a message must be a JSON object with a \"type\"");
                return new PluginMessage(t.GetString()!, root.Clone());
            }
            catch (JsonException) { return new PluginMessage("", default, "not JSON: " + Shorten(line)); }
        }
    }

    internal static string Shorten(string s, int max = 200) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Reads up to the next '\n' without holding more than <see cref="MaxLine"/> characters.</summary>
    private async Task<(string? Line, bool TooLong)> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var tooLong = false;
        while (true)
        {
            if (_pos == _len)
            {
                _len = await input.ReadAsync(_buf.AsMemory(), ct);
                _pos = 0;
                if (_len == 0) return sb.Length > 0 || tooLong ? (tooLong ? "" : sb.ToString(), tooLong) : (null, false);
            }
            var nl = Array.IndexOf(_buf, '\n', _pos, _len - _pos);
            var end = nl < 0 ? _len : nl;
            if (!tooLong)
            {
                sb.Append(_buf, _pos, end - _pos);
                if (sb.Length > MaxLine) { tooLong = true; sb.Clear(); }
            }
            _pos = nl < 0 ? _len : nl + 1;
            if (nl >= 0) return (tooLong ? "" : sb.ToString().TrimEnd('\r'), tooLong);
        }
    }
}
