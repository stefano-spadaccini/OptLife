using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OptLife.Core;

namespace OptLife.Panel.ViewModels;

/// <summary>A setting filled from a list (a drop-down), in the Settings page or in the guided tour.</summary>
public interface IPickTarget
{
    /// <summary>The value stored ("" for the module's "none" or "automatic").</summary>
    string PickedValue { get; set; }
    ObservableCollection<SettingOption> Picks { get; }
}

/// <summary>One thing that can be ticked for a list setting (an allowed device), with its full name.</summary>
public sealed partial class PickCheck(SettingOption option, bool isChecked, Action changed) : ObservableObject
{
    public SettingOption Option => option;
    public string Label => option.Label;
    [ObservableProperty] private bool _isChecked = isChecked;
    partial void OnIsCheckedChanged(bool value) => changed();
}

public static class Picks
{
    /// <summary>
    /// The options of a setting as the drop-down shows them: the module's list, plus the current value when it is not in it
    /// (something written by hand earlier, or a folder picked with Browse), so nothing the person chose disappears.
    /// </summary>
    public static void Fill(ObservableCollection<SettingOption> into, IReadOnlyList<SettingOption> options, string current, Func<string, string>? unknown = null)
    {
        into.Clear();
        // A name written by hand earlier ("DAC") that is part of exactly one option is that option: shown with its full name,
        // stored as it was, so nothing changes until the person picks something else.
        var exact = options.Any(o => o.Value.Equals(current, StringComparison.OrdinalIgnoreCase));
        var partOf = exact || current.Trim().Length == 0 ? [] : options.Where(o => Matches(o, current)).ToList();
        foreach (var o in options) into.Add(partOf.Count == 1 && o == partOf[0] ? new SettingOption(current, o.Label) : o);
        Add(into, current, unknown);
    }

    /// <summary>Adds a value that is not among the options yet (a folder chosen with Browse, an old hand-written name).</summary>
    public static void Add(ObservableCollection<SettingOption> into, string value, Func<string, string>? unknown = null)
    {
        if (value.Trim().Length == 0 || into.Any(o => o.Value.Equals(value, StringComparison.OrdinalIgnoreCase))) return;
        into.Add(new SettingOption(value, LooksLikePath(value) ? value : unknown is not null ? unknown(value) : L.T($"{value} (scritto a mano)", $"{value} (written by hand)")));
    }

    private static bool LooksLikePath(string value) => value.Length > 2 && (value[1] == ':' || value.StartsWith(@"\\", StringComparison.Ordinal));

    /// <summary>An entry of a list setting matches an option when it is part of its name ("DAC" in "Speakers (USB Audio DAC)").</summary>
    public static bool Matches(SettingOption option, string entry) =>
        entry.Trim().Length > 0 && option.Value.Contains(entry.Trim(), StringComparison.OrdinalIgnoreCase);
}
