using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptLife.Core.Config;

/// <summary>
/// The way OptLife is set up, minus everything that belongs to one person and one PC. Kept: which
/// modules are on, thresholds, switches, choices, and the permission of each action. Left out: every
/// list and every free text, because that is where folders, program names and addresses live. The
/// person who receives it keeps their own lists; the modules' defaults fill what they never set.
/// </summary>
public static class Profile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static bool Shareable(SettingDefinition d) => d.Kind is SettingKind.Bool or SettingKind.Int or SettingKind.Choice;

    public static string Export(ConfigStore config, IReadOnlyList<IModule> modules)
    {
        var file = config.Load();
        var shared = new ConfigFile { SampleRetentionDays = file.SampleRetentionDays, EventRetentionDays = file.EventRetentionDays };
        foreach (var module in modules)
        {
            var mine = file.Modules.GetValueOrDefault(module.Info.Id) ?? new ModuleConfig();
            var theirs = new ModuleConfig { Enabled = mine.Enabled };
            foreach (var d in module.Settings.Where(Shareable))
                if (mine.Settings.TryGetValue(d.Key, out var value)) theirs.Settings[d.Key] = value;
            foreach (var action in module.Actions)
                if (mine.Permissions.TryGetValue(action.Id, out var permission)) theirs.Permissions[action.Id] = permission;
            shared.Modules[module.Info.Id] = theirs;
        }
        return JsonSerializer.Serialize(shared, Json);
    }

    /// <summary>Applies a profile on top of what is there. Lists and texts are never touched, even if the file carries some.</summary>
    /// <returns>How many settings, switches and permissions were applied.</returns>
    public static int Import(ConfigStore config, IReadOnlyList<IModule> modules, string json)
    {
        ConfigFile shared;
        try
        {
            shared = JsonSerializer.Deserialize<ConfigFile>(json, Json) ?? new ConfigFile();
        }
        catch (JsonException)
        {
            throw new FormatException(L.T("Il file non è un profilo di OptLife.", "The file is not an OptLife profile."));
        }

        var applied = 0;
        config.Update(file =>
        {
            foreach (var module in modules)
            {
                if (!shared.Modules.TryGetValue(module.Info.Id, out var theirs)) continue;
                if (!file.Modules.TryGetValue(module.Info.Id, out var mine)) file.Modules[module.Info.Id] = mine = new ModuleConfig();
                mine.Enabled = theirs.Enabled;
                applied++;
                foreach (var d in module.Settings.Where(Shareable))
                {
                    if (!theirs.Settings.TryGetValue(d.Key, out var value)) continue;
                    mine.Settings[d.Key] = value;
                    applied++;
                }
                foreach (var action in module.Actions)
                {
                    if (!theirs.Permissions.TryGetValue(action.Id, out var permission)) continue;
                    mine.Permissions[action.Id] = permission;
                    applied++;
                }
            }
        });
        return applied;
    }
}
