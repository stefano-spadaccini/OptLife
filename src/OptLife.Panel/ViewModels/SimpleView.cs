using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptLife.Core;

namespace OptLife.Panel.ViewModels;

/// <summary>One common value of a table's simple view: a box with icon and name; ticking it adds its ready-made line.</summary>
public sealed partial class BasicChip(SettingOption option, bool isChecked, Action<BasicChip> changed) : ObservableObject
{
    public SettingOption Option => option;
    public string Label => option.Label;
    public string Icon => option.Icon ?? "";
    public string Key => SimpleView.KeyOf(option.Value);
    [ObservableProperty] private bool _isChecked = isChecked;
    private bool _quiet;

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_quiet) changed(this);
    }

    /// <summary>Follows the table without telling it back.</summary>
    public void Show(bool on)
    {
        _quiet = true;
        IsChecked = on;
        _quiet = false;
    }
}

public sealed record BasicGroup(string Name, IReadOnlyList<BasicChip> Chips);

public static class SimpleView
{
    /// <summary>The first field of an entry: what identifies it ("game.fps" in "game.fps | FPS | | ").</summary>
    public static string KeyOf(string line) => line.Split('|')[0].Split('=')[0].Trim();
}

/// <summary>
/// The simple view of a table setting (the overlay's values): the common entries as boxes to tick, grouped, ready-made sets,
/// and the order of what is chosen; the whole table only with "Advanced". It writes the same rows the table shows.
/// </summary>
public sealed partial class SettingItem
{
    public ObservableCollection<BasicGroup> BasicGroups { get; } = [];
    public ObservableCollection<SettingOption> Presets { get; } = [];
    private readonly List<BasicChip> _chips = [];

    public bool HasBasic => _chips.Count > 0;

    /// <summary>Set by the module card: the simple view instead of the table, while "Advanced" is off.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTable))] private bool _showBasic;

    /// <summary>The whole table (and "as text"): a plain list, or a table in advanced view.</summary>
    public bool ShowTable => IsListTable && !ShowBasic;

    /// <summary>Set by the module card: an advanced setting is hidden while "Advanced" is off.</summary>
    [ObservableProperty] private bool _shown = true;

    public bool IsAdvanced => _def.Advanced;

    private void BuildBasic()
    {
        if (_def.Kind != SettingKind.List || _def.Columns is null || _def.Pick != SettingPick.List) return;
        var basic = _optionsOf(_def.Key + ".basic");
        _chips.Clear();
        BasicGroups.Clear();
        var keys = Rows.Select(TitleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var o in basic) _chips.Add(new BasicChip(o, keys.Contains(SimpleView.KeyOf(o.Value)), ToggleBasic));
        foreach (var g in _chips.GroupBy(c => c.Option.Group ?? "")) BasicGroups.Add(new BasicGroup(g.Key, [.. g]));
        Presets.Clear();
        foreach (var p in _optionsOf(_def.Key + ".presets")) Presets.Add(p);
        OnPropertyChanged(nameof(HasBasic));
    }

    private static string TitleKey(ListRow row) => row.Cells.Count > 0 ? SimpleView.KeyOf(row.Cells[0].Value) : "";

    /// <summary>A box ticked: its ready line goes at the end; unticked: its row goes away.</summary>
    private void ToggleBasic(BasicChip chip)
    {
        if (chip.IsChecked)
        {
            if (!Rows.Any(r => TitleKey(r).Equals(chip.Key, StringComparison.OrdinalIgnoreCase))) Rows.Add(NewRow(chip.Option.Value));
        }
        else
        {
            foreach (var row in Rows.Where(r => TitleKey(r).Equals(chip.Key, StringComparison.OrdinalIgnoreCase)).ToList()) Rows.Remove(row);
        }
        RowsChanged();
    }

    /// <summary>A ready set: these values in this order; a value already chosen keeps its own label, limit and colour.</summary>
    [RelayCommand]
    private void ApplyPreset(SettingOption preset)
    {
        var keys = preset.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = keys.Select(k =>
            Rows.FirstOrDefault(r => TitleKey(r).Equals(k, StringComparison.OrdinalIgnoreCase))
            ?? NewRow(_chips.FirstOrDefault(c => c.Key.Equals(k, StringComparison.OrdinalIgnoreCase))?.Option.Value ?? k)).ToList();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        RowsChanged();
    }

    /// <summary>Dragged in the order line: the row goes to that place.</summary>
    public void MoveRowTo(ListRow row, int index)
    {
        var at = Rows.IndexOf(row);
        if (at < 0 || index < 0 || index >= Rows.Count || at == index) return;
        Rows.Move(at, index);
        RowsChanged();
    }

    /// <summary>How a row is named in the simple view: the common value's name, or its key; a key OptLife does not know says so.</summary>
    public string TitleOf(ListRow row)
    {
        var key = TitleKey(row);
        if (_chips.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } chip) return chip.Label;
        var label = row.Cells.Count > 1 && row.Cells[1].Value.Trim().Length > 0 ? row.Cells[1].Value.Trim() : key;
        return IsKnown(key) ? label : L.T($"{label} — OptLife non conosce questo valore: non comparirà", $"{label} — OptLife does not know this value: it will not show");
    }

    public bool IsKnown(string key) => Options_().Count == 0 || Options_().Any(o => o.Value.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>After the rows change (typed, moved, a preset): the boxes follow.</summary>
    private void SyncChips()
    {
        var keys = Rows.Select(TitleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _chips) c.Show(keys.Contains(c.Key));
        foreach (var r in Rows) r.Refresh();
    }
}
