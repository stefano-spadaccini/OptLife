using CommunityToolkit.Mvvm.ComponentModel;

namespace OptLife.Panel.ViewModels;

/// <summary>What the person asks for in the full-screen library, whatever the device (controller, keyboard).</summary>
public enum BigInput { Up, Down, Left, Right, Accept, Menu, Back, PreviousRow, NextRow, Search, Help, Filters, Exit }

/// <summary>The controller in use, to show its own buttons (or the keyboard's keys).</summary>
public enum PadKind { Xbox, PlayStation, Nintendo, Keyboard }

/// <summary>
/// One button drawn in the legend: its shape (a letter in a coloured circle, the PlayStation symbols, a pill with a word, a key)
/// and what it does. Drawn from shapes and plain letters, never from a symbol font that may be missing.
/// </summary>
/// <param name="Shape">"letter", "cross", "circle", "square", "triangle", "pill" or "key".</param>
public sealed record LegendItem(string Shape, string Text, string Color, string Label)
{
    public string AccessibleName => $"{Text} {Label}".Trim();
}

/// <summary>What the window must do after an input.</summary>
public enum BigOutcome { None, Close, Launched }

/// <summary>One card of the full-screen view, with whether it is the chosen one.</summary>
public sealed partial class BigCard(GalleryCard card) : ObservableObject
{
    public GalleryCard Card => card;
    [ObservableProperty] private bool _isSelected;
}

public sealed record BigRow(string Name, IReadOnlyList<BigCard> Cards)
{
    public string AccessibleName => $"{Name}, {Cards.Count}";
    public bool HasName => Name.Length > 0;
}

/// <summary>One entry of a list the controller moves through: a card's menu, the filters, a key of the on-screen keyboard.</summary>
public sealed partial class BigChoice(string text, object? value = null, bool isHeading = false) : ObservableObject
{
    public string Text => text;
    public object? Value => value;
    public bool IsHeading => isHeading;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// The full-screen library, commanded from the sofa: every game in one grid (in the order chosen, not split by launcher), the last
/// played on top; A plays, Start opens the game's menu, Select opens order and filters, Y the on-screen keyboard to search,
/// holding Select asks whether to leave. B only goes back. No WPF types: tested on its own.
/// </summary>
public sealed partial class BigLibraryViewModel : ObservableObject
{
    /// <summary>Cards per row: set by the window from its width (as many as fit), 5 until then.</summary>
    public int PerRow { get; set; } = 5;
    private readonly Func<ButtonItem, Task> _press;

    public BigLibraryViewModel(ViewTab tab, Func<ButtonItem, Task> press)
    {
        Tab = tab;
        _press = press;
        Reload();
    }

    public ViewTab Tab { get; }

    [ObservableProperty] private IReadOnlyList<BigRow> _rows = [];
    [ObservableProperty] private int _row;
    [ObservableProperty] private int _column;

    // The layers over the grid; one at a time.
    [ObservableProperty] private bool _menuOpen;
    [ObservableProperty] private bool _filtersOpen;
    [ObservableProperty] private bool _keyboardOpen;
    [ObservableProperty] private bool _exitOpen;
    [ObservableProperty] private bool _helpOpen;
    [ObservableProperty] private IReadOnlyList<BigChoice> _menu = [];
    [ObservableProperty] private int _menuIndex;
    [ObservableProperty] private IReadOnlyList<BigChoice> _filters = [];
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<BigChoice>> _keys = [];
    [ObservableProperty] private int _keyRow;
    [ObservableProperty] private int _keyColumn;

    /// <summary>What a screen reader says after each move ("Metal Gear Solid 2, PlayStation 2…").</summary>
    [ObservableProperty] private string _announcement = "";

    public BigCard? Selected => Rows.Count > 0 && Row < Rows.Count && Column < Rows[Row].Cards.Count ? Rows[Row].Cards[Column] : null;
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The order and filter in words, under the title ("Recently added · PC").</summary>
    public string Showing => $"{SortWord(Tab.GallerySort)} · {Tab.GalleryFilters.FirstOrDefault(f => f.IsChosen)?.Label ?? L.T("Tutti", "All")}"
                             + (Tab.GallerySearch.Length > 0 ? $" · \"{Tab.GallerySearch}\"" : "");

    private static string SortWord(string sort) => sort switch
    {
        "used" => L.T("Giocati di recente", "Recently played"),
        "name" => L.T("Per nome", "By name"),
        _ => L.T("Aggiunti di recente", "Recently added"),
    };

    /// <summary>Reads the gallery again (after a check, a filter, a search), keeping the same game chosen when it is still there.</summary>
    public void Reload()
    {
        var keep = Selected?.Card.Title;
        var items = Tab.ShownItems;
        IEnumerable<CheckItem> ordered = Tab.GallerySort switch
        {
            "used" => items.OrderByDescending(i => i.Used ?? DateTimeOffset.MinValue).ThenBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase),
            "name" => items.OrderBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderByDescending(i => i.Added ?? DateTimeOffset.MinValue).ThenBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase),
        };
        var rows = new List<BigRow>();
        // The last played first, as on a console (unless the grid is already in that order).
        var recent = items.Where(i => i.Used is not null).OrderByDescending(i => i.Used).Take(PerRow).ToList();
        if (recent.Count > 0 && Tab.GallerySort != "used" && Tab.GallerySearch.Length == 0)
            rows.Add(new BigRow(L.T("Giocati di recente", "Recently played"), [.. recent.Select(i => new BigCard(new GalleryCard(i)))]));
        // Then every game in one grid, rows of six: no split by launcher (the filters do that).
        var all = ordered.ToList();
        for (var i = 0; i < all.Count; i += PerRow)
            rows.Add(new BigRow(i == 0 ? L.T("Tutti i giochi", "All games") : "", [.. all.Skip(i).Take(PerRow).Select(x => new BigCard(new GalleryCard(x)))]));
        Rows = rows;
        var (r, c) = (0, 0);
        for (var y = 0; keep is not null && y < Rows.Count; y++)
            if (Rows[y].Cards.ToList().FindIndex(x => x.Card.Title == keep) is >= 0 and var at) { (r, c) = (y, at); break; }
        Select(r, c);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Showing));
    }

    private void Select(int row, int column)
    {
        if (Selected is { } old) old.IsSelected = false;
        if (Rows.Count == 0)
        {
            (Row, Column) = (0, 0);
            OnPropertyChanged(nameof(Selected));
            return;
        }
        Row = Math.Clamp(row, 0, Rows.Count - 1);
        Column = Math.Clamp(column, 0, Rows[Row].Cards.Count - 1);
        if (Selected is { } now)
        {
            now.IsSelected = true;
            Announcement = $"{now.Card.Title}. {now.Card.Line}.";
        }
        OnPropertyChanged(nameof(Selected));
    }

    public async Task<BigOutcome> Handle(BigInput input)
    {
        if (HelpOpen)
        {
            HelpOpen = false;   // Any key closes the help.
            return BigOutcome.None;
        }
        if (ExitOpen) return HandleExit(input);
        if (MenuOpen) return await HandleMenu(input);
        if (FiltersOpen) return HandleFilters(input);
        if (KeyboardOpen) return HandleKeyboard(input);
        switch (input)
        {
            case BigInput.Left: Select(Row, Column - 1); break;
            case BigInput.Right: Select(Row, Column + 1); break;
            case BigInput.Up or BigInput.PreviousRow: Select(Row - 1, Column); break;
            case BigInput.Down or BigInput.NextRow: Select(Row + 1, Column); break;
            case BigInput.Accept when Selected?.Card.Primary is { } play:
                await _press(play);
                return BigOutcome.Launched;
            case BigInput.Menu when Selected is { Card.HasMore: true } card:
                Menu = [.. card.Card.More.Select(b => new BigChoice(b.ButtonText, b))];
                MenuIndex = 0;
                Menu[0].IsSelected = true;
                MenuOpen = true;
                Announcement = $"{L.T("Menu di", "Menu of")} {card.Card.Title}: {Menu[0].Text}";
                break;
            case BigInput.Filters: OpenFilters(); break;
            case BigInput.Search: OpenKeyboard(); break;
            case BigInput.Exit:
                ExitOpen = true;
                Announcement = ExitQuestion;
                break;
            case BigInput.Back when Tab.GallerySearch.Length > 0:
                // B undoes a search first; it never leaves by itself.
                Tab.GallerySearch = "";
                Reload();
                break;
            case BigInput.Help:
                HelpOpen = true;
                Announcement = HelpText;
                break;
        }
        return BigOutcome.None;
    }

    public static string ExitQuestion => L.T("Uscire dalla libreria a schermo intero?", "Leave the full-screen library?");

    private BigOutcome HandleExit(BigInput input)
    {
        if (input == BigInput.Accept) return BigOutcome.Close;
        if (input is BigInput.Back or BigInput.Exit) ExitOpen = false;
        return BigOutcome.None;
    }

    private async Task<BigOutcome> HandleMenu(BigInput input)
    {
        switch (input)
        {
            case BigInput.Up or BigInput.Left or BigInput.PreviousRow: MenuIndex = Move(Menu, MenuIndex, -1); break;
            case BigInput.Down or BigInput.Right or BigInput.NextRow: MenuIndex = Move(Menu, MenuIndex, 1); break;
            case BigInput.Back or BigInput.Menu:
                MenuOpen = false;
                Select(Row, Column);
                break;
            case BigInput.Accept:
                var chosen = (ButtonItem)Menu[MenuIndex].Value!;
                MenuOpen = false;
                await _press(chosen);
                Reload();
                break;
        }
        return BigOutcome.None;
    }

    private int Move(IReadOnlyList<BigChoice> list, int at, int step)
    {
        var next = at;
        do next = Math.Clamp(next + step, 0, list.Count - 1);
        while (list[next].IsHeading && next > 0 && next < list.Count - 1);
        if (list[next].IsHeading) return at;
        list[at].IsSelected = false;
        list[next].IsSelected = true;
        Announcement = list[next].Text;
        return next;
    }

    // ── Order and filters (Select) ──

    private void OpenFilters()
    {
        var list = new List<BigChoice> { new(L.T("Ordine", "Order"), isHeading: true) };
        foreach (var sort in new[] { "added", "used", "name" })
            list.Add(new BigChoice(SortWord(sort), ("sort", sort)) { IsCurrent = Tab.GallerySort == sort });
        list.Add(new BigChoice(L.T("Mostra", "Show"), isHeading: true));
        var filters = Tab.GalleryFilters.Count > 0 ? Tab.GalleryFilters : [new GalleryFilterItem("", L.T("Tutti", "All"), true)];
        foreach (var f in filters) list.Add(new BigChoice(f.Label, ("filter", f.Key)) { IsCurrent = f.IsChosen });
        Filters = list;
        FilterIndex = 1;
        Filters[1].IsSelected = true;
        FiltersOpen = true;
        Announcement = L.T("Ordine e filtri: ", "Order and filters: ") + Filters[1].Text;
    }

    private BigOutcome HandleFilters(BigInput input)
    {
        switch (input)
        {
            case BigInput.Up or BigInput.Left or BigInput.PreviousRow: FilterIndex = Move(Filters, FilterIndex, -1); break;
            case BigInput.Down or BigInput.Right or BigInput.NextRow: FilterIndex = Move(Filters, FilterIndex, 1); break;
            case BigInput.Back or BigInput.Filters: FiltersOpen = false; break;
            case BigInput.Accept:
                if (Filters[FilterIndex].Value is (string kind, string value))
                {
                    if (kind == "sort") Tab.GallerySort = value;
                    else Tab.GalleryFilter = value;
                    foreach (var f in Filters.Where(f => f.Value is (string k, string _) && k == kind)) f.IsCurrent = false;
                    Filters[FilterIndex].IsCurrent = true;
                    Reload();
                }
                break;
        }
        return BigOutcome.None;
    }

    // ── On-screen keyboard (Y), moved with the controller ──

    public const string Space = "␣", Delete = "⌫", Done = "OK";

    private void OpenKeyboard()
    {
        var rows = new[] { "1234567890", "QWERTYUIOP", "ASDFGHJKL'", "ZXCVBNM-:." };
        Keys = [.. rows.Select(r => (IReadOnlyList<BigChoice>)[.. r.Select(ch => new BigChoice(ch.ToString()))]),
                [new BigChoice(Space), new BigChoice(Delete), new BigChoice(Done)]];
        (KeyRow, KeyColumn) = (1, 0);
        Keys[1][0].IsSelected = true;
        KeyboardOpen = true;
        Announcement = L.T("Tastiera: scrivi con A, B per chiudere.", "Keyboard: type with A, B to close.");
    }

    private void SelectKey(int row, int column)
    {
        Keys[KeyRow][KeyColumn].IsSelected = false;
        KeyRow = Math.Clamp(row, 0, Keys.Count - 1);
        KeyColumn = Math.Clamp(column, 0, Keys[KeyRow].Count - 1);
        Keys[KeyRow][KeyColumn].IsSelected = true;
        Announcement = Keys[KeyRow][KeyColumn].Text switch { Space => L.T("spazio", "space"), Delete => L.T("cancella", "delete"), var t => t };
    }

    private BigOutcome HandleKeyboard(BigInput input)
    {
        switch (input)
        {
            case BigInput.Left: SelectKey(KeyRow, KeyColumn - 1); break;
            case BigInput.Right: SelectKey(KeyRow, KeyColumn + 1); break;
            case BigInput.Up: SelectKey(KeyRow - 1, KeyRow == Keys.Count - 1 ? KeyColumn * 3 : KeyColumn); break;
            case BigInput.Down: SelectKey(KeyRow + 1, KeyRow == Keys.Count - 2 ? KeyColumn / 4 : KeyColumn); break;
            case BigInput.Menu: Type(Delete); break;   // X deletes, as on consoles.
            case BigInput.Back or BigInput.Search: KeyboardOpen = false; break;
            case BigInput.Accept: Type(Keys[KeyRow][KeyColumn].Text); break;
        }
        return BigOutcome.None;
    }

    /// <summary>A key pressed on the on-screen keyboard (or typed on a real one).</summary>
    public void Type(string key)
    {
        var text = Tab.GallerySearch;
        Tab.GallerySearch = key switch
        {
            Space => text + " ",
            Delete => text.Length > 0 ? text[..^1] : "",
            Done => text,
            _ => text + key.ToLowerInvariant(),
        };
        if (key == Done) KeyboardOpen = false;
        Reload();
        Announcement = Tab.GallerySearch.Length > 0 ? $"\"{Tab.GallerySearch}\": {Tab.ShownItems.Count}" : L.T("Ricerca vuota", "Search empty");
    }

    /// <summary>Choosing a card with the mouse.</summary>
    /// <summary>The first card with this title (for snapshots).</summary>
    public void ChooseTitle(string title)
    {
        if (Rows.SelectMany(r => r.Cards).FirstOrDefault(c => c.Card.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) is { } card) Choose(card);
    }

    public void Choose(BigCard card)
    {
        for (var r = 0; r < Rows.Count; r++)
        {
            var c = Rows[r].Cards.ToList().IndexOf(card);
            if (c >= 0) { Select(r, c); return; }
        }
    }

    public string HelpLines => HelpText;

    // ── The buttons shown: those of the controller in use (or of the keyboard) ──

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Legend), nameof(MenuKey), nameof(ExitYes), nameof(ExitNo), nameof(AcceptName), nameof(BackName), nameof(DeleteName))]
    private PadKind _pad = PadKind.Xbox;

    private static LegendItem Accept(PadKind p) => p switch
    {
        PadKind.PlayStation => new("cross", "", "#7EA6FF", ""),
        PadKind.Nintendo => new("letter", "A", "#5A5A5E", ""),
        PadKind.Keyboard => new("key", L.T("Invio", "Enter"), "", ""),
        _ => new("letter", "A", "#3FA535", ""),
    };

    private static LegendItem BackKey(PadKind p) => p switch
    {
        PadKind.PlayStation => new("circle", "", "#FF6B6B", ""),
        PadKind.Nintendo => new("letter", "B", "#5A5A5E", ""),
        PadKind.Keyboard => new("key", "Backspace", "", ""),
        _ => new("letter", "B", "#D8382E", ""),
    };

    /// <summary>The legend under the grid: each action with its button, in the controller's own shapes and colours.</summary>
    public IReadOnlyList<LegendItem> Legend => Pad switch
    {
        PadKind.PlayStation =>
        [
            Accept(Pad) with { Label = L.T("Gioca", "Play") },
            new("pill", "OPTIONS", "", L.T("Altre azioni", "More actions")),
            new("pill", "CREATE", "", L.T("Ordine e filtri · tieni premuto: esci", "Order and filters · hold: leave")),
            new("triangle", "", "#3FD3A8", L.T("Cerca", "Search")),
            BackKey(Pad) with { Label = L.T("Indietro", "Back") },
        ],
        PadKind.Keyboard =>
        [
            Accept(Pad) with { Label = L.T("Gioca", "Play") },
            new("key", "F10", "", L.T("Altre azioni", "More actions")),
            new("key", "F2", "", L.T("Ordine e filtri", "Order and filters")),
            new("key", "Ctrl+F", "", L.T("Cerca", "Search")),
            new("key", "Esc", "", L.T("Esci", "Leave")),
            new("key", "F1", "", L.T("Aiuto", "Help")),
        ],
        _ =>
        [
            Accept(Pad) with { Label = L.T("Gioca", "Play") },
            new("pill", Pad == PadKind.Nintendo ? "+" : "MENU", "", L.T("Altre azioni", "More actions")),
            new("pill", Pad == PadKind.Nintendo ? "−" : "VIEW", "", L.T("Ordine e filtri · tieni premuto: esci", "Order and filters · hold: leave")),
            new("letter", Pad == PadKind.Nintendo ? "X" : "Y", Pad == PadKind.Nintendo ? "#5A5A5E" : "#E8B100", L.T("Cerca", "Search")),
            BackKey(Pad) with { Label = L.T("Indietro", "Back") },
        ],
    };

    /// <summary>The two answers to "leave?", drawn as the controller's own buttons.</summary>
    public LegendItem ExitYes => Accept(Pad) with { Label = L.T("Esci", "Leave") };
    public LegendItem ExitNo => BackKey(Pad) with { Label = L.T("Resta", "Stay"), Shape = Pad == PadKind.Keyboard ? "key" : BackKey(Pad).Shape, Text = Pad == PadKind.Keyboard ? "Esc" : BackKey(Pad).Text };

    /// <summary>The button that opens the game's menu (presets, folder…), for the second button of the game panel.</summary>
    public LegendItem MenuKey => Legend[1] with { Label = L.T("Impostazioni e altro", "Settings and more") };

    /// <summary>The names of the buttons, for the short lines under menus and keyboard.</summary>
    public string AcceptName => Pad switch { PadKind.PlayStation => L.T("Croce", "Cross"), PadKind.Keyboard => L.T("Invio", "Enter"), _ => "A" };
    public string BackName => Pad switch { PadKind.PlayStation => L.T("Cerchio", "Circle"), PadKind.Keyboard => "Esc", _ => "B" };
    public string DeleteName => Pad switch { PadKind.PlayStation => L.T("Quadrato", "Square"), PadKind.Keyboard => "Backspace", PadKind.Nintendo => "Y", _ => "X" };

    public static string HelpText => L.T(
        "A: gioca. Start: altre azioni del gioco. Select: ordine e filtri. Y: cerca con la tastiera a schermo (X cancella). Croce o levetta: spostati; LB e RB: fila su e giù. B: indietro. Tieni premuto Select per uscire (chiede conferma). Da tastiera: frecce, Invio, F10 menu, F2 filtri, Ctrl+F cerca, Esc esci, F1 aiuto.",
        "A: play. Start: more actions for the game. Select: order and filters. Y: search with the on-screen keyboard (X deletes). D-pad or stick: move; LB and RB: row up and down. B: back. Hold Select to leave (it asks first). Keyboard: arrows, Enter, F10 menu, F2 filters, Ctrl+F search, Esc leave, F1 help.");
}

/// <summary>
/// Turns the controller's state, read many times a second, into inputs: a press counts once, a held direction repeats
/// (after 350 ms, then every 90 ms), the stick counts as the cross beyond a third of its travel. Select is two keys: a short
/// press opens the filters, holding it for a second asks to leave.
/// </summary>
public sealed class GamepadMapper
{
    public const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008, Start = 0x0010, Back = 0x0020,
        LeftShoulder = 0x0100, RightShoulder = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    private const int Dead = 32767 / 3, FirstRepeat = 350, NextRepeat = 90, HoldToLeave = 1000;

    private ushort _previous;
    private BigInput? _held;
    private long _heldSince, _lastRepeat, _selectSince = -1;
    private bool _selectUsed;

    /// <summary>
    /// A PlayStation controller as Windows.Gaming.Input reads it (the HID order: 0 square, 1 cross, 2 circle, 3 triangle, 4 L1, 5 R1,
    /// 8 Share/Create, 9 Options; switch 0 the cross, 0 centre then 1 up clockwise to 8; axes 0-1 the left stick, 0 = left/up),
    /// in XInput's shape: cross = A, circle = B, square = X, triangle = Y, Options = Start, Share/Create = Back.
    /// </summary>
    public static (ushort Buttons, short X, short Y) FromSony(IReadOnlyList<byte> b, int hat, double x, double y)
    {
        bool On(int i) => i < b.Count && b[i] != 0;
        ushort buttons = 0;
        if (On(1)) buttons |= A;
        if (On(2)) buttons |= B;
        if (On(0)) buttons |= X;
        if (On(3)) buttons |= Y;
        if (On(4)) buttons |= LeftShoulder;
        if (On(5)) buttons |= RightShoulder;
        if (On(8)) buttons |= Back;
        if (On(9)) buttons |= Start;
        if (hat is 8 or 1 or 2) buttons |= DpadUp;
        if (hat is 2 or 3 or 4) buttons |= DpadRight;
        if (hat is 4 or 5 or 6) buttons |= DpadDown;
        if (hat is 6 or 7 or 8) buttons |= DpadLeft;
        static short Axis(double v) => (short)Math.Clamp((v - 0.5) * 65534, short.MinValue, short.MaxValue);
        return (buttons, Axis(x), Axis(1 - y)); // XInput: up is positive.
    }

    public IReadOnlyList<BigInput> Step(ushort buttons, short stickX, short stickY, long nowMs)
    {
        // The stick acts as the cross (Y up is positive on XInput).
        if (stickY > Dead) buttons |= DpadUp;
        else if (stickY < -Dead) buttons |= DpadDown;
        if (stickX > Dead) buttons |= DpadRight;
        else if (stickX < -Dead) buttons |= DpadLeft;

        var list = new List<BigInput>();
        var pressed = (ushort)(buttons & ~_previous);
        var released = (ushort)(_previous & ~buttons);
        void Once(ushort mask, BigInput input) { if ((pressed & mask) != 0) list.Add(input); }
        Once(A, BigInput.Accept);
        Once(B, BigInput.Back);
        Once(X, BigInput.Menu);
        Once(Y, BigInput.Search);
        Once(Start, BigInput.Menu);
        Once(LeftShoulder, BigInput.PreviousRow);
        Once(RightShoulder, BigInput.NextRow);

        // Select: held for a second = leave (once); let go before = filters.
        if ((pressed & Back) != 0) (_selectSince, _selectUsed) = (nowMs, false);
        if ((buttons & Back) != 0 && _selectSince >= 0 && !_selectUsed && nowMs - _selectSince >= HoldToLeave)
        {
            list.Add(BigInput.Exit);
            _selectUsed = true;
        }
        if ((released & Back) != 0)
        {
            if (!_selectUsed) list.Add(BigInput.Filters);
            _selectSince = -1;
        }

        BigInput? direction = (buttons & DpadUp) != 0 ? BigInput.Up : (buttons & DpadDown) != 0 ? BigInput.Down
            : (buttons & DpadLeft) != 0 ? BigInput.Left : (buttons & DpadRight) != 0 ? BigInput.Right : null;
        if (direction != _held)
        {
            _held = direction;
            _heldSince = _lastRepeat = nowMs;
            if (direction is { } d) list.Add(d);
        }
        else if (direction is { } d && nowMs - _heldSince >= FirstRepeat && nowMs - _lastRepeat >= NextRepeat)
        {
            _lastRepeat = nowMs;
            list.Add(d);
        }
        _previous = buttons;
        return list;
    }
}
