using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace OptLife.Modules.Errors;

/// <summary>One record of the Windows event log, reduced to what the classifier needs.</summary>
public sealed record RawEvent(
    string Log,
    string Provider,
    int Id,
    int Level,
    DateTimeOffset Time,
    long RecordId,
    IReadOnlyList<string> Properties,
    string? Message);

public interface IEventSource
{
    IEnumerable<RawEvent> Read(string log, DateTimeOffset since);
}

public sealed partial class WindowsEventSource : IEventSource
{
    // Critical and Error events of any origin, plus a few providers whose warnings or
    // informational records describe real trouble (driver resets, corrected hardware errors).
    private static readonly string[] AlwaysRead =
    [
        "Display", "Microsoft-Windows-WHEA-Logger", "disk", "stornvme", "storahci", "Ntfs",
        "Windows Error Reporting",
    ];

    public IEnumerable<RawEvent> Read(string log, DateTimeOffset since)
    {
        var providers = string.Join(" or ", AlwaysRead.Select(p => $"@Name='{p}'"));
        var xpath = $"*[System[TimeCreated[@SystemTime>='{since.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}'] " +
                    $"and (Level=1 or Level=2 or Provider[{providers}])]]";

        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath));
        while (reader.ReadEvent() is { } record)
        {
            using (record)
            {
                if (record.TimeCreated is not { } time) continue;
                yield return new RawEvent(
                    log,
                    record.ProviderName ?? "",
                    record.Id,
                    record.Level ?? 0,
                    new DateTimeOffset(time),
                    record.RecordId ?? 0,
                    [.. record.Properties.Select(p => p.Value?.ToString() ?? "")],
                    Describe(record));
            }
        }
    }

    private static string? Describe(EventRecord record)
    {
        try
        {
            var text = record.FormatDescription();
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = Whitespace().Replace(text, " ").Trim();
            return text.Length > 500 ? text[..500] + "…" : text;
        }
        catch (EventLogException)
        {
            // The program that wrote the event is gone or did not register its messages.
            return null;
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
