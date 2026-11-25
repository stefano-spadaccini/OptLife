using System.Globalization;
using System.Text;
using OptLife.Core.Storage;

namespace OptLife.Core.Reporting;

public sealed record ReportSection(string Title, IReadOnlyList<string> Lines);

/// <summary>
/// What happened on the PC over a period, in words: problems that came and went, what the modules
/// recorded, what was changed, and what is worth doing next. Built from the shared history alone,
/// by fixed rules: it knows no module by name, so it covers whichever ones are installed.
/// </summary>
/// <summary>One number that sums the period up, with how worrying it is ("ok", "medium", "high").</summary>
public sealed record ReportFigure(string Label, string Value, string Level);

public sealed record Report(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ReportSection> Sections, IReadOnlyList<ReportFigure> Figures)
{
    private static CultureInfo Lang => L.Culture;

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(L.T($"RAPPORTO OPTLIFE · dal {From.ToLocalTime().ToString("d MMMM", Lang)} al {To.ToLocalTime().ToString("d MMMM yyyy", Lang)}", $"OPTLIFE REPORT · from {From.ToLocalTime().ToString("d MMMM", Lang)} to {To.ToLocalTime().ToString("d MMMM yyyy", Lang)}"));
        foreach (var section in Sections)
        {
            text.AppendLine().AppendLine(section.Title.ToUpper(Lang));
            foreach (var line in section.Lines) text.Append("  ").AppendLine(line);
        }
        return text.ToString();
    }

    /// <param name="moduleName">The name to show for a module id (the id itself when the module is no longer installed).</param>
    /// <param name="actionName">The name to show for an action of a module.</param>
    public static Report Build(Store store, Func<string, string> moduleName, Func<string, string, string> actionName, DateTimeOffset from, DateTimeOffset to)
    {
        static string Label(Severity s) => s switch { Severity.High => L.T("ALTO", "HIGH"), Severity.Medium => L.T("MEDIO", "MEDIUM"), Severity.Low => L.T("BASSO", "LOW"), _ => "INFO" };
        static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
        string Day(DateTimeOffset t) => t.ToLocalTime().ToString("dd/MM HH:mm", Lang);

        var findings = store.GetFindings(includeClosed: true);
        var open = findings.Where(f => f.Status == FindingStatus.Open).ToList();
        var appeared = findings.Where(f => f.FirstSeen >= from && f.FirstSeen <= to).ToList();
        var resolved = findings.Where(f => f.Status == FindingStatus.Resolved && f.LastSeen >= from && f.LastSeen <= to).ToList();

        var problems = new List<string>
        {
            open.Count == 0
                ? L.T("Aperti adesso: nessuno.", "Open now: none.")
                : L.T($"Aperti adesso: {open.Count} (", $"Open now: {open.Count} (") + string.Join(", ", open.GroupBy(f => f.Severity).OrderByDescending(g => g.Key)
                    .Select(g => $"{g.Count()} {Label(g.Key).ToLower(Lang)}")) + ").",
            L.T($"Comparsi nel periodo: {appeared.Count}.", $"Appeared in the period: {appeared.Count}."),
        };
        problems.AddRange(appeared.OrderByDescending(f => f.Severity).Take(8).Select(f => $"  + [{Label(f.Severity)}] {f.Title}"
            + f.Status switch { FindingStatus.Resolved => L.T("  (già risolto)", "  (already solved)"), FindingStatus.Ignored => L.T("  (ignorato)", "  (ignored)"), _ => "" }));
        if (appeared.Count > 8) problems.Add(L.T($"  + e altri {appeared.Count - 8}", $"  + and {appeared.Count - 8} more"));
        problems.Add(L.T($"Risolti nel periodo: {resolved.Count}.", $"Solved in the period: {resolved.Count}."));
        problems.AddRange(resolved.Take(8).Select(f => $"  ✓ {f.Title}"));
        if (resolved.Count > 8) problems.Add(L.T($"  ✓ e altri {resolved.Count - 8}", $"  ✓ and {resolved.Count - 8} more"));
        var ignored = findings.Count(f => f.Status == FindingStatus.Ignored);
        if (ignored > 0) problems.Add(L.T($"Ignorati per tua scelta: {ignored}.", $"Ignored by your choice: {ignored}."));

        var events = store.QueryEvents(from, to, limit: 50_000);
        var happened = new List<string>();
        foreach (var module in events.GroupBy(e => e.Module).OrderByDescending(g => g.Max(e => e.Severity)).ThenByDescending(g => g.Count()))
        {
            happened.Add($"{moduleName(module.Key)}: {Count(module.Count(), L.T("evento", "event"), L.T("eventi", "events"))}.");
            // The same thing happening forty times is one line with a count, not forty lines.
            foreach (var same in module.GroupBy(e => e.GroupKey ?? e.Title).OrderByDescending(g => g.Max(e => e.Severity)).ThenByDescending(g => g.Max(e => e.Time)).Take(4))
            {
                var latest = same.MaxBy(e => e.Time)!;
                happened.Add($"  · {Day(latest.Time)}  {latest.Title}" + (same.Count() > 1 ? $"  (×{same.Count()})" : ""));
            }
        }
        if (happened.Count == 0) happened.Add(L.T("Niente di registrato.", "Nothing recorded."));

        var actions = store.GetActions(500).Where(a => a.Time >= from && a.Time <= to).ToList();
        var done = actions.Count == 0
            ? [L.T("Nessuna: sul PC non è stato cambiato niente.", "None: nothing was changed on the PC.")]
            : actions.Take(15).Select(a => $"{Day(a.Time)}  {actionName(a.Module, a.Action)} · {(a.Ok ? L.T("fatto", "done") : L.T("non eseguita", "not done"))} — {a.Detail}").ToList();
        if (actions.Count > 15) done.Add(L.T($"e altre {actions.Count - 15}", $"and {actions.Count - 15} more"));

        var todo = open.Where(f => f.Severity >= Severity.Low).OrderByDescending(f => f.Severity).ThenBy(f => f.FirstSeen).Take(6)
            .Select(f => $"[{Label(f.Severity)}] {f.Title}" + (f.Advice is null ? "" : $" → {f.Advice}")).ToList();
        if (todo.Count == 0) todo.Add(L.T("Niente di urgente.", "Nothing urgent."));

        // The period at a glance: the few numbers that say whether it went well, before any reading.
        var worst = open.Count == 0 ? Severity.Info : open.Max(f => f.Severity);
        var serious = events.Count(e => e.Severity >= Severity.Medium);
        ReportFigure[] figures =
        [
            new(L.T("Problemi aperti", "Open problems"), open.Count.ToString(Lang), worst switch { Severity.High => "high", Severity.Medium => "medium", _ => "ok" }),
            new(L.T("Comparsi", "Appeared"), appeared.Count.ToString(Lang), appeared.Any(f => f.Severity >= Severity.Medium && f.Status == FindingStatus.Open) ? "medium" : "ok"),
            new(L.T("Risolti", "Solved"), resolved.Count.ToString(Lang), "ok"),
            new(L.T("Eventi seri", "Serious events"), serious.ToString(Lang), serious > 0 ? "medium" : "ok"),
            new(L.T("Cose cambiate sul PC", "Changes made to the PC"), actions.Count(a => a.Ok).ToString(Lang), "ok"),
        ];

        // What needs doing comes first: it is the part to act on. The rest is the record behind it.
        return new Report(from, to,
        [
            new ReportSection(L.T("Da fare", "To do"), todo),
            new ReportSection(L.T("Problemi", "Problems"), problems),
            new ReportSection(L.T("Cosa è successo", "What happened"), happened),
            new ReportSection(L.T("Cosa è stato cambiato", "What was changed"), done),
        ], figures);
    }
}
