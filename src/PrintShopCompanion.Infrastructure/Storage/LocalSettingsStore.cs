namespace PrintShopCompanion.Infrastructure.Storage;

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Reads/writes the per-user settings override file
/// <c>%AppData%\PrintShop\Companion\appsettings.Local.json</c>.
///
/// <para><b>Why per-user AppData (Phase 0 audit):</b> the previous
/// OptionsWriter wrote <c>appsettings.Local.json</c> next to the executable
/// (Program Files on a normal install — writes fail without elevation, and a
/// writable install dir is shared by every Windows account on the machine).
/// Storing per-user overrides under %AppData% keeps settings writable and
/// isolated per Windows account. The App/Worker load chain now includes this
/// file, so values set here actually survive restart.</para>
///
/// <para>Dotted keys map to nested JSON objects: <c>"Printer:SelectedPrinter"</c>
/// → <c>{ "Printer": { "SelectedPrinter": "..." } }</c>, matching how
/// Microsoft.Extensions.Configuration binds sections.</para>
/// </summary>
public static class LocalSettingsStore
{
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PrintShop", "Companion", "appsettings.Local.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    /// <summary>Set a dotted configuration key in the local override file. Creates parents as needed.</summary>
    public static void Set(string dottedKey, string value)
    {
        if (string.IsNullOrWhiteSpace(dottedKey))
            throw new ArgumentException("Configuration key is required", nameof(dottedKey));

        var path = SettingsPath;
        var root = Load(path);

        var segments = dottedKey.Split(':');
        var node = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (node[seg] is not JsonObject child)
            {
                child = new JsonObject();
                node[seg] = child;
            }
            node = child;
        }
        node[segments[^1]] = value;

        WriteAtomic(path, root);
    }

    /// <summary>Load (or create) the override file's JSON root.</summary>
    private static JsonObject Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonNode.Parse(File.ReadAllText(path));
                if (parsed is JsonObject obj) return obj;
            }
        }
        catch
        {
            // Corrupt override file — start fresh rather than crash startup.
            // A .corrupt backup keeps the file for diagnosis.
            try
            {
                if (File.Exists(path))
                    File.Copy(path, path + ".corrupt-" + DateTimeOffset.UtcNow.Ticks, overwrite: true);
            }
            catch { /* best-effort */ }
        }
        return new JsonObject();
    }

    /// <summary>Atomic write: .tmp file + move, so a crash mid-write cannot leave a half-written settings file.</summary>
    private static void WriteAtomic(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }
}
