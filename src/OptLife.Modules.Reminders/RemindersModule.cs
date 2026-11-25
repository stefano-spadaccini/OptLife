using System.Globalization;
using OptLife.Core;

namespace OptLife.Modules.Reminders;

/// <summary>
/// Things the person asked to be reminded of, of two kinds: dates that are coming (a token that
/// expires, a subscription that renews) and files whose change means something has to be looked at
/// again (a game that updated, so its mods may have broken). Both are plain lists in the settings.
/// </summary>
public sealed class RemindersModule : IModule
{
    private static CultureInfo Lang => L.Culture;
    private static readonly string[] DateFormats = ["d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "d-M-yyyy"];
    private readonly Func<string, DateTimeOffset?> _modified;

    public RemindersModule() : this(LastWrite) { }

    /// <param name="modified">When a file was last changed; null when it is not there.</param>
    internal RemindersModule(Func<string, DateTimeOffset?> modified) => _modified = modified;

    public ModuleInfo Info { get; } = new(
        "reminders", L.T("Promemoria", "Reminders"),
        L.T("Ricorda le scadenze che gli scrivi e ti avvisa quando cambia un file che vuoi tenere d'occhio, come un gioco che si aggiorna e può rompere le mod.", "Remembers the deadlines you give it and warns you when a file you want to watch changes, like a game updating and possibly breaking mods."), FindingArea.Reminders);

    public IReadOnlyList<SettingDefinition> Settings { get; } =
    [
        SettingDefinition.Table("deadlines", [L.T("Cosa", "What"), L.T("Data", "Date"), L.T("Preavviso (giorni)", "Notice (days)")], L.T("Scadenze", "Deadlines"),
            L.T("Scritte come: cosa | data (giorno/mese/anno) | giorni di preavviso. Per esempio: Rinnovo abbonamento | 14/01/2027 | 10.", "Written as: what | date (day/month/year) | days of notice. For example: Subscription renewal | 14/01/2027 | 10.")),
        SettingDefinition.Table("files", [L.T("Cosa", "What"), "File", L.T("Cosa fare quando cambia", "What to do when it changes")], L.T("File da tenere d'occhio", "Files to watch"),
            L.T("Scritti come: cosa | file | cosa fare quando cambia. Per esempio: Deadlock | E:\\SteamLibrary\\steamapps\\appmanifest_1422450.acf | Si è aggiornato: controlla che le mod funzionino ancora.", "Written as: what | file | what to do when it changes. For example: Deadlock | E:\\SteamLibrary\\steamapps\\appmanifest_1422450.acf | It updated: check the mods still work.")),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("check", L.T("Controllo dei promemoria", "Checking the reminders"), TimeSpan.FromMinutes(30), CheckAsync),
    ];

    private Task CheckAsync(IModuleContext ctx, CancellationToken ct)
    {
        var today = ctx.Now.ToLocalTime().Date;
        ctx.SyncFindings("deadlines", [.. ctx.Settings.GetList("deadlines").Select(l => Deadline(l, today)).OfType<NewFinding>()]);

        var changed = new List<NewFinding>();
        foreach (var p in ctx.Settings.GetList("files").Select(l => l.Split('|', StringSplitOptions.TrimEntries)).Where(p => p.Length >= 2 && p[1].Length > 0))
        {
            var key = $"file:{p[1].ToLowerInvariant()}";
            if (_modified(p[1]) is not { } now) continue; // Not there (a disk unplugged, a game uninstalled): nothing to say.
            var stamp = now.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
            var known = ctx.GetState(key);
            if (known is null)
            {
                ctx.SetState(key, stamp); // First look: this is the version to compare with.
                continue;
            }
            // The reminder stays until the person dismisses it; a later change is a new one.
            if (known != stamp)
                changed.Add(new NewFinding($"{key}|{stamp}", Severity.Low,
                    L.T($"{p[0]} è cambiato il {now.ToLocalTime().ToString("d MMMM", Lang)}", $"{p[0]} changed on {now.ToLocalTime().ToString("d MMMM", Lang)}"),
                    L.T($"Il file {p[1]} è stato modificato il {now.ToLocalTime().ToString("d MMMM 'alle' HH:mm", Lang)}.", $"The file {p[1]} was modified on {now.ToLocalTime().ToString("d MMMM 'at' HH:mm", Lang)}."),
                    (p.Length > 2 && p[2].Length > 0 ? p[2] + " " : "") + L.T("Quando hai controllato, ignora questo avviso.", "Once you have checked, ignore this warning.")));
        }
        ctx.SyncFindings("files", changed);
        return Task.CompletedTask;
    }

    /// <summary>"what | date | days of notice": silent until the notice period starts, more insistent once the date has passed.</summary>
    public static NewFinding? Deadline(string line, DateTime today)
    {
        var p = line.Split('|', StringSplitOptions.TrimEntries);
        if (p.Length < 2 || p[0].Length == 0) return null;
        var key = $"deadline|{p[0].ToLowerInvariant()}";
        if (!DateTime.TryParseExact(p[1], DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new NewFinding(key, Severity.Low, L.T($"Scadenza \"{p[0]}\": non capisco la data", $"Deadline \"{p[0]}\": I cannot read the date"),
                L.T($"\"{p[1]}\" non è una data scritta come giorno/mese/anno.", $"\"{p[1]}\" is not a date written as day/month/year."), L.T("Correggila nelle impostazioni del modulo Promemoria.", "Fix it in the Reminders module settings."));

        var notice = p.Length > 2 && int.TryParse(p[2], out var n) && n >= 0 ? n : 7;
        var left = (date.Date - today).Days;
        var when = date.ToString("d MMMM yyyy", Lang);
        if (left < 0)
            return new NewFinding(key, Severity.Medium, L.T($"{p[0]}: scaduto da {(left == -1 ? "1 giorno" : $"{-left} giorni")}", $"{p[0]}: expired {(left == -1 ? "1 day" : $"{-left} days")} ago"),
                L.T($"La scadenza era il {when}.", $"The deadline was {when}."), L.T("Quando hai rinnovato, aggiorna la data nelle impostazioni del modulo Promemoria.", "Once renewed, update the date in the Reminders module settings."));
        if (left > notice) return null;
        return new NewFinding(key, Severity.Low,
            left switch { 0 => L.T($"{p[0]}: scade oggi", $"{p[0]}: expires today"), 1 => L.T($"{p[0]}: scade domani", $"{p[0]}: expires tomorrow"), _ => L.T($"{p[0]}: scade tra {left} giorni", $"{p[0]}: expires in {left} days") },
            L.T($"La scadenza è il {when}.", $"The deadline is {when}."), L.T("Quando hai rinnovato, aggiorna la data nelle impostazioni del modulo Promemoria.", "Once renewed, update the date in the Reminders module settings."));
    }

    private static DateTimeOffset? LastWrite(string file)
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables(file);
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
