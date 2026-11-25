using System.Text.RegularExpressions;

namespace OptLife.Modules.Drivers;

/// <summary>What one run of the NVIDIA installer did, as read from its log.</summary>
/// <param name="Components">What it installed ("Graphics Driver", "HD Audio Driver", "NVIDIA App").</param>
/// <param name="Clean">The "clean installation" option was on: the old driver was removed with Windows running.</param>
/// <param name="Errors">The first distinct error lines, for the person or for a Claude session to read.</param>
public sealed record InstallRun(IReadOnlyList<string> Components, bool Succeeded, bool RebootRequired, bool Clean, IReadOnlyList<string> Errors)
{
    public bool TouchedDriver => Components.Any(c => c.Contains("Graphics Driver", StringComparison.OrdinalIgnoreCase));

    public string What => Components.Count == 0 ? L.T("componenti NVIDIA", "NVIDIA components") : string.Join(", ", Components);
}

/// <summary>Reads the NVIDIA installer's log. Pure: text in, verdict out.</summary>
public static partial class InstallRules
{
    // Calibrated on real installer logs (15/10/2026: driver, express; 18/11/2026: NVIDIA App update).
    [GeneratedRegex("""progress title is "Installing (?<what>[^"]+?)\.{3}""")]
    private static partial Regex Title();

    [GeneratedRegex("""Condition "IsCleanInstall" enabled""")]
    private static partial Regex CleanInstall();

    [GeneratedRegex(@"All packages installed successfully|Installation is successful")]
    private static partial Regex Success();

    [GeneratedRegex(@"RebootRequiredBlock : Reboot required")]
    private static partial Regex Reboot();

    [GeneratedRegex(@" ERROR: \[[^\]]+\] \d+@[^:]+::[^:]+ : (?<text>[^\r\n]{1,160})")]
    private static partial Regex Error();

    public static InstallRun Read(string log) => new(
        [.. Title().Matches(log).Select(m => m.Groups["what"].Value.Replace("NVIDIA ", "", StringComparison.Ordinal).Trim()).Distinct()],
        Success().IsMatch(log),
        Reboot().IsMatch(log),
        CleanInstall().IsMatch(log),
        [.. Error().Matches(log).Select(m => m.Groups["text"].Value.Trim()).Distinct().Take(3)]);
}
