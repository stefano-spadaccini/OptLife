using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptLife.Core.Config;

public sealed class ConfigFile
{
    public Dictionary<string, ModuleConfig> Modules { get; set; } = [];

    /// <summary>Measurements (one per minute per sensor) older than this are deleted.</summary>
    public int SampleRetentionDays { get; set; } = 30;

    /// <summary>Events and resolved problems older than this are deleted.</summary>
    public int EventRetentionDays { get; set; } = 180;

    /// <summary>"auto" (follows Windows), "it" or "en". Read at start-up: a change applies when OptLife restarts.</summary>
    public string Language { get; set; } = L.Auto;

    /// <summary>Panel preferences (mode, last page). Free-form so the interface can evolve without touching the core.</summary>
    public Dictionary<string, string> Ui { get; set; } = [];

    /// <summary>The agent's own pop-up for activities (never a Windows notification).</summary>
    public NotificationSettings Notifications { get; set; } = new();
}

/// <summary>When the agent shows its small pop-up about an activity starting or ending.</summary>
public sealed class NotificationSettings
{
    public bool Popup { get; set; } = true;
    public bool Sound { get; set; } = true;

    /// <summary>Also for the Scheduler's jobs that start by their timetable (not only what the person started).</summary>
    public bool Scheduler { get; set; }
}

public sealed class ModuleConfig
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, JsonElement> Settings { get; set; } = [];
    public Dictionary<string, Permission> Permissions { get; set; } = [];
}

/// <summary>
/// One JSON file holds every user choice (which modules are on, their settings, action permissions).
/// It is re-read on each access, so edits from the panel or the CLI apply without restarting the agent.
/// </summary>
public sealed class ConfigStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    public string Path => path;

    public ConfigFile Load()
    {
        lock (_gate)
        {
            if (!File.Exists(path)) return new ConfigFile();
            try
            {
                return JsonSerializer.Deserialize<ConfigFile>(Retry(() => File.ReadAllText(path)), Json) ?? new ConfigFile();
            }
            catch (JsonException)
            {
                // A hand-edited file with a typo must not take the whole app down: fall back to defaults.
                return new ConfigFile();
            }
        }
    }

    public ModuleConfig GetModule(string moduleId) =>
        Load().Modules.GetValueOrDefault(moduleId) ?? new ModuleConfig();

    /// <summary>
    /// A copy of the file as it is now, before the person saves something from the panel: the last <paramref name="keep"/> stay in
    /// "config-copies" beside it, so a setting lost by mistake can be found again.
    /// </summary>
    public void KeepCopy(int keep = 20)
    {
        try
        {
            if (!File.Exists(path)) return;
            var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, "config-copies");
            Directory.CreateDirectory(folder);
            File.Copy(path, System.IO.Path.Combine(folder, $"config-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json"), overwrite: true);
            foreach (var old in Directory.GetFiles(folder, "config-*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A copy that cannot be made never stops the save itself.
        }
    }

    public void Update(Action<ConfigFile> mutate)
    {
        lock (_gate)
        {
            var file = Load();
            mutate(file);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            Retry(() =>
            {
                File.Move(tmp, path, overwrite: true);
                return true;
            });
        }
    }

    /// <summary>
    /// Agent, panel and command line share the file across processes: one may be reading it at the very
    /// moment another replaces it, and Windows refuses for that instant. A few short retries get past it.
    /// </summary>
    private static T Retry<T>(Func<T> work)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return work();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void UpdateModule(string moduleId, Action<ModuleConfig> mutate) =>
        Update(file =>
        {
            if (!file.Modules.TryGetValue(moduleId, out var m))
                file.Modules[moduleId] = m = new ModuleConfig();
            mutate(m);
        });
}

/// <summary>Typed access to one module's settings: stored value if valid, otherwise the declared default.</summary>
public sealed class ModuleSettings(IReadOnlyList<SettingDefinition> definitions, IReadOnlyDictionary<string, JsonElement> stored) : ISettings
{
    public bool GetBool(string key)
    {
        var def = Find(key, SettingKind.Bool);
        return stored.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : (bool)def.Default;
    }

    public int GetInt(string key)
    {
        var def = Find(key, SettingKind.Int);
        var value = stored.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : (int)def.Default;
        return Math.Clamp(value, def.Min ?? int.MinValue, def.Max ?? int.MaxValue);
    }

    public string GetText(string key)
    {
        var def = Find(key, SettingKind.Text);
        return stored.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : (string)def.Default;
    }

    public IReadOnlyList<string> GetList(string key)
    {
        var def = Find(key, SettingKind.List);
        if (stored.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array)
            return [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => def.Normalize is { } n ? n(e.GetString()!) : e.GetString()!)];
        return (string[])def.Default;
    }

    public string GetChoice(string key)
    {
        var def = Find(key, SettingKind.Choice);
        // A value saved by an older version that is no longer among the options falls back to the default.
        return stored.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && def.Options!.Contains(v.GetString()!)
            ? v.GetString()!
            : (string)def.Default;
    }

    /// <summary>Converts text typed by a person into the stored form; throws FormatException if it does not fit.</summary>
    public static JsonElement Parse(SettingDefinition def, string text) => def.Kind switch
    {
        SettingKind.Bool => JsonSerializer.SerializeToElement(text.Trim().ToLowerInvariant() switch
        {
            "true" or "si" or "sì" or "yes" or "y" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new FormatException(L.T($"'{text}' non è un sì/no.", $"'{text}' is not a yes/no.")),
        }),
        SettingKind.Int => int.TryParse(text, out var i) && i >= (def.Min ?? int.MinValue) && i <= (def.Max ?? int.MaxValue)
            ? JsonSerializer.SerializeToElement(i)
            : throw new FormatException(L.T($"'{text}' non è un numero tra {def.Min} e {def.Max}.", $"'{text}' is not a number between {def.Min} and {def.Max}.")),
        SettingKind.Text => JsonSerializer.SerializeToElement(text),
        SettingKind.List => JsonSerializer.SerializeToElement(
            text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        SettingKind.Choice => def.Options!.FirstOrDefault(o => o.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)) is { } option
            ? JsonSerializer.SerializeToElement(option)
            : throw new FormatException(L.T($"'{text}' non è tra le scelte possibili: {string.Join(", ", def.Options!)}.", $"'{text}' is not one of the possible choices: {string.Join(", ", def.Options!)}.")),
        _ => throw new FormatException(L.T("Tipo di impostazione sconosciuto.", "Unknown setting type.")),
    };

    private SettingDefinition Find(string key, SettingKind kind)
    {
        var def = definitions.FirstOrDefault(d => d.Key == key)
            ?? throw new ArgumentException($"Setting '{key}' is not declared by the module.", nameof(key));
        return def.Kind == kind
            ? def
            : throw new ArgumentException($"Setting '{key}' is {def.Kind}, not {kind}.", nameof(key));
    }
}
