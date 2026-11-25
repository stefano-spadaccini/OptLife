namespace OptLife.Core;

// ─────────────────────────────────────────────────────────────────────────────
// The module contract. This file is the whole surface a module can see.
// Rules that keep modules removable:
//   1. A module talks only to IModuleContext, never to another module.
//   2. A module owns no database tables: it writes samples, events, findings.
//   3. Data from other modules is read through IHistory and may be absent.
// ─────────────────────────────────────────────────────────────────────────────

public enum Severity { Info = 0, Low = 1, Medium = 2, High = 3 }

/// <summary>Who may trigger an action without a human confirming it.</summary>
public enum Permission { Auto, Ask, Never }

public enum FindingStatus { Open, Ignored, Resolved }

/// <param name="Area">Where the module's problems are listed in the panel (one of <see cref="FindingArea"/>); a problem can name its own.</param>
public sealed record ModuleInfo(string Id, string Name, string Description, string? Area = null);

/// <summary>
/// The sections the problems are grouped under, so that a crash does not sit next to a full memory.
/// Plain words, not module names: a module picks the one its problems belong to.
/// </summary>
public static class FindingArea
{
    public static string Hardware => L.T("Hardware e prestazioni", "Hardware and performance");
    public static string Network => L.T("Rete", "Network");
    public static string Software => L.T("Programmi ed errori", "Programs and errors");
    public static string Setup => L.T("Impostazioni del PC", "PC settings");
    public static string Data => L.T("Dati e copie", "Data and backups");
    public static string Tidy => L.T("Ordine e spazio", "Tidiness and space");
    public static string Reminders => L.T("Promemoria", "Reminders");

    /// <summary>The order the sections are shown in: what costs performance first, reminders last.</summary>
    public static IReadOnlyList<string> Order => [Hardware, Network, Setup, Software, Data, Tidy, Reminders];
}

public interface IModule
{
    ModuleInfo Info { get; }

    /// <summary>Settings the module exposes. The settings menu is generated from this list.</summary>
    IReadOnlyList<SettingDefinition> Settings => [];

    /// <summary>Recurring work: collecting data and checking for problems.</summary>
    IReadOnlyList<ModuleTask> Tasks => [];

    /// <summary>Things the module can do to the PC, each with a permission level.</summary>
    IReadOnlyList<ModuleAction> Actions => [];

    /// <summary>Pages of its own in the panel: each one is a table of the module's events of one kind.</summary>
    IReadOnlyList<ModuleView> Views => [];
}

public enum ColumnKind { Text, Number, Duration, When }

/// <summary>
/// How a module page is drawn. Gallery: the latest event of the view's kind as cards with a picture (<see cref="CheckItem.Image"/>,
/// a file), title, line and buttons (the first one in sight, the others in a menu), with search and a filter by group. Form: the module's own settings on its page, with Save, and above them the live
/// <see cref="ModuleView.Preview"/> when the module gives one (the overlay editor).
/// </summary>
public enum ViewLayout { Table, Checklist, Buttons, Form, Gallery }

/// <summary>
/// A picture the module draws of how its settings look on screen, as they are being edited (not yet saved), placed on a
/// drawing of the screen. Dragging it in the panel writes the position back into <paramref name="DragX"/> and
/// <paramref name="DragY"/> (Int settings, 0-100 = percent of the screen) and sets the settings in <paramref name="OnDrag"/>
/// ("position" = "free").
/// </summary>
/// <param name="Png">The picture, at the screen's own pixels.</param>
/// <param name="X">Where its left edge is, as a fraction of the screen width.</param>
/// <param name="Y">Where its top edge is, as a fraction of the screen height.</param>
/// <param name="ScreenWidth">The screen it is placed on, in pixels, so the picture keeps its true size on the drawing.</param>
/// <param name="Description">The picture in words, for screen readers ("Overlay in alto a destra: FPS 144, PING 23 ms").</param>
/// <param name="Notice">A sentence shown under the picture when something in it cannot work on this PC (a value it cannot read, and why).</param>
public sealed record ViewPreview(byte[] Png, double X, double Y, int ScreenWidth, int ScreenHeight, string Description,
    string? DragX = null, string? DragY = null, IReadOnlyDictionary<string, string>? OnDrag = null, string? Notice = null);

/// <summary>
/// One button of a Buttons page: pressing it runs one of the module's actions with the given argument,
/// and pressing is the person's confirmation.
/// </summary>
/// <param name="ButtonText">What the button says ("SITO", "INSTALLA"); null for the usual "AVVIA".</param>
public sealed record ViewButton(string Group, string Label, string Description, string ActionId, string? Argument = null, string? ButtonText = null);

/// <summary>
/// One thing the person can tick before an action runs ("Chrome · 1,2 GB"). The action receives the ids
/// of the ticked ones, joined by ';'.
/// </summary>
/// <param name="Checked">Ticked at first; the person can change it.</param>
/// <param name="Category">The heading it goes under ("Launcher", "Audio"); the panel groups by it, in order of first appearance.</param>
/// <param name="Icon">A program or icon file to take the picture from; null shows the first letter of the label.</param>
public sealed record ActionChoice(string Id, string Label, string Detail, bool Checked = true, string? Category = null, string? Icon = null);

/// <summary>How one checked thing stands: right, worth a look, wrong, or just for information.</summary>
public enum CheckStatus { Ok, Watch, Wrong, Info }

/// <summary>
/// One line of a checklist page: what it is, how it is now, how it should be, and how it stands.
/// A module writes the whole list as the data of one event (<see cref="ToData"/>); the panel reads the
/// newest such event back (<see cref="FromData"/>) without knowing anything else about the module.
/// </summary>
/// <param name="Expected">What it should be, in words ("144 Hz, la massima"); null when there is no right answer.</param>
/// <param name="Advice">Where to change it, or why it matters.</param>
/// <summary>A button on a line of a checklist: runs one of the module's actions with the argument (pressing is the confirmation).</summary>
public sealed record CheckButton(string ActionId, string? Argument, string Text);

/// <param name="Kind">A second way to group the line ("DAW", "Launcher"): when lines have one, the panel lets the person
/// order the page by it instead of by <see cref="Group"/>.</param>
/// <param name="Buttons">What can be done about this line, right there.</param>
/// <param name="ValueStatus">The colour of the value when it tells something else than the line's judgement: "Off" in red
/// on a line that is right as it is (off is what the person wants). Null: the value takes the line's colour.</param>
/// <param name="Used">When it was last used (a game last played): a gallery can be ordered by it.</param>
/// <param name="Added">When it appeared (a game installed): a gallery can be ordered by it.</param>
/// <param name="Wide">A wide picture of the item (a game's banner art), for the big panel of a full-screen gallery.</param>
/// <param name="Details">A few facts shown when the item is chosen in a full-screen gallery ("Played: 12 h", "Average FPS: 141").</param>
/// <param name="StatusText">The word beside the line instead of "OK"/"TO FIX" ("" for none): for a line whose colour shows a state
/// (on/off) rather than a judgement, so a switched-off entry is red without being called wrong.</param>
public sealed record CheckItem(string Group, string Label, string Value, CheckStatus Status, string? Expected = null, string? Advice = null,
    string? Kind = null, IReadOnlyList<CheckButton>? Buttons = null, CheckStatus? ValueStatus = null, string? StatusText = null, string? Image = null,
    DateTimeOffset? Used = null, DateTimeOffset? Added = null, IReadOnlyList<string>? Details = null, string? Wide = null)
{
    /// <summary>The list as event data, numbered so it reads back in the same order.</summary>
    public static IReadOnlyDictionary<string, string> ToData(IEnumerable<CheckItem> items) =>
        items.Select((item, i) => (Key: $"check.{i:000}", Value: System.Text.Json.JsonSerializer.Serialize(item)))
             .ToDictionary(p => p.Key, p => p.Value);

    public static IReadOnlyList<CheckItem> FromData(IReadOnlyDictionary<string, string> data)
    {
        var list = new List<CheckItem>();
        foreach (var (_, json) in data.Where(p => p.Key.StartsWith("check.", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            try
            {
                if (System.Text.Json.JsonSerializer.Deserialize<CheckItem>(json) is { } item) list.Add(item);
            }
            catch (System.Text.Json.JsonException)
            {
                // A line written by an older version that no longer reads: skip it, keep the rest.
            }
        }
        return list;
    }
}

/// <param name="Key">A key of the event's data, or "@time" / "@title" for the event's own time and title.</param>
/// <param name="Kind">How to show it: as written, as a number, as seconds turned into "1 h 9 min", as a date.</param>
/// <param name="Width">In pixels; 0 lets the column take what it needs (use it for the last one).</param>
/// <param name="Show">Turns the stored text into what is shown (e.g. a cause saved in Italian, shown in English).</param>
public sealed record ViewColumn(string Label, string Key, ColumnKind Kind = ColumnKind.Text, int Width = 110, Func<string, string>? Show = null);

/// <summary>
/// A page the panel builds without knowing the module: one row per event of the given kind, newest
/// first, with the chosen columns. Removing the module removes the page.
/// </summary>
/// <param name="Description">What the columns mean, in a few sentences shown above the table.</param>
/// <param name="Layout">Checklist: the newest event of the kind holds a <see cref="CheckItem"/> list, and the columns are unused.</param>
/// <param name="Buttons">Buttons layout: the buttons, built from the module's current settings; EventKind and Columns are unused.</param>
/// <param name="Menu">The menu of the panel the page goes under (<see cref="ViewMenu"/>); null for a tab of its own.</param>
/// <param name="Preview">For a Form page: draws the settings being edited, with the live values (null = no picture).</param>
/// <param name="Guide">Two lines the first-start guided tour shows on this page (what it is for, what to decide); null = the tour skips it.</param>
/// <param name="Order">Where the page stands among the tabs of its menu, lowest first; equal numbers keep the order the modules were found in.</param>
public sealed record ModuleView(string Id, string Title, string EventKind, IReadOnlyList<ViewColumn> Columns, int Days = 90, string EmptyText = "", string Description = "",
    ViewLayout Layout = ViewLayout.Table, Func<ISettings, IReadOnlyList<ViewButton>>? Buttons = null, string? Menu = null, bool GroupsStartClosed = false,
    int Order = 0, string? Guide = null,
    Func<ISettings, IReadOnlyList<LiveValue>, ViewPreview?>? Preview = null);

/// <summary>The drop-down menus of the panel that module pages can go under. Plain words, like the problem areas.</summary>
public static class ViewMenu
{
    /// <summary>About the PC, for a page that fits none of the four below (it still goes under "The PC").</summary>
    public static string Pc => L.T("PC", "PC");
    /// <summary>How the PC is set up and running: settings, sound, network.</summary>
    public static string Status => L.T("Stato", "Status");
    /// <summary>What is installed: start-up, updates, installing apps.</summary>
    public static string Programs => L.T("Programmi", "Programs");
    public static string Security => L.T("Sicurezza", "Security");
    /// <summary>Where things are and how much room they take.</summary>
    public static string Order => L.T("Ordine e spazio", "Order and space");
    public static string Games => L.T("Giochi", "Games");
    public static string Tools => L.T("Strumenti", "Tools");

    /// <summary>
    /// What a side-bar entry with several pages is for, in a few sentences: the panel opens such an entry on an overview with
    /// these words and one card per page (what it is for, how it stands, a button to open it). Null: no overview, the pages only.
    /// </summary>
    public static string? Intro(string menu) =>
        menu == Order
            ? L.T("Qui il PC resta in ordine e con spazio libero, in tre passi. Organizzazione decide dove va ogni cosa (giochi, programmi portatili, copie, la cartella sul Desktop) e crea le cartelle che mancano. " +
                  "Collegamenti tiene pulito il Desktop: ciò che ci compare va nella cartella \"da sistemare\" e da lì nella sezione giusta. Spazio trova ciò che si può togliere senza danni (resti degli installatori, Cestino pieno). " +
                  "OptLife non cancella mai: sposta, o manda al Cestino; ogni spostamento si annulla.",
                  "Here the PC stays tidy and with free room, in three steps. Organisation decides where everything goes (games, portable programs, backups, the Desktop folder) and creates the missing folders. " +
                  "Shortcuts keeps the Desktop clean: what appears on it goes to the \"to sort\" folder and from there to the right section. Space finds what can go without harm (installer leftovers, a full Recycle Bin). " +
                  "OptLife never deletes: it moves, or sends to the Recycle Bin; every move can be undone.")
            : null;
}

/// <param name="Continuous">
/// True for work that only makes sense running all the time (sampling sensors, recording a game
/// session). Only the agent runs it; a one-off "check now" from the CLI or the panel skips it.
/// </param>
public sealed record ModuleTask(
    string Id,
    string Name,
    TimeSpan Interval,
    Func<IModuleContext, CancellationToken, Task> Run,
    bool Continuous = false);

/// <param name="NeverAuto">
/// For actions that remove something (even to the Recycle Bin): they can never be set to run by
/// themselves, whatever the configuration says. The person presses the button, every time.
/// </param>
/// <param name="Choices">
/// When set, the person first sees these and ticks which ones the action should act on (the apps to close,
/// say); the action then runs with their ids joined by ';'. Such an action never runs by itself.
/// </param>
public sealed record ModuleAction(
    string Id,
    string Name,
    string Description,
    Permission DefaultPermission,
    Func<IModuleContext, string?, CancellationToken, Task<ActionResult>> Run,
    bool NeverAuto = false,
    Func<IModuleContext, IReadOnlyList<ActionChoice>>? Choices = null);

/// <summary>How an action ended, in the four ways the person must be able to tell apart.</summary>
public enum ActionOutcome
{
    /// <summary>It changed what it was asked to.</summary>
    Done,
    /// <summary>It could not, and says why.</summary>
    Failed,
    /// <summary>There was nothing to do any more (already done, gone meanwhile): not a success to boast about, not an error.</summary>
    Nothing,
    /// <summary>It stopped before changing anything because the person must choose: <see cref="ActionResult.Needs"/> are the ways out.</summary>
    Choice,
}

/// <param name="Undo">How to revert the action, in words or as a command; null when not reversible.</param>
public sealed record ActionResult(bool Ok, string Detail, string? Undo = null)
{
    /// <summary>The buttons of the choice the person must make, each an action of the same module (with its argument).</summary>
    public IReadOnlyList<FindingFix>? Needs { get; init; }

    /// <summary>Nothing was changed because nothing was left to do.</summary>
    public bool Unchanged { get; init; }

    public ActionOutcome Outcome => Needs is { Count: > 0 } ? ActionOutcome.Choice : !Ok ? ActionOutcome.Failed : Unchanged ? ActionOutcome.Nothing : ActionOutcome.Done;

    /// <summary>Nothing left to do: said plainly, never as "done".</summary>
    public static ActionResult Nothing(string detail) => new(true, detail) { Unchanged = true };

    /// <summary>Stopped before changing anything: the person picks one of these.</summary>
    public static ActionResult Choose(string detail, IReadOnlyList<FindingFix> ways) => new(false, detail) { Needs = ways };
}

public enum SettingKind { Bool, Int, Text, List, Choice }

/// <summary>How the panel lets the person fill a Text or List setting.</summary>
public enum SettingPick
{
    /// <summary>Written freely.</summary>
    None,
    /// <summary>Picked from what the module found on this PC (a Text: one of them; a List: ticked ones).</summary>
    List,
    /// <summary>A folder: picked from what the module proposes, or with Windows' folder picker.</summary>
    Folder,
}

/// <summary>
/// One thing the person can pick for a setting: the value stored and the words shown ("Speakers (USB Audio DAC)",
/// "E:\Giochi — New Volume, 191 GB free"). A module writes the list in its state under "options.&lt;key&gt;" with <see cref="Save"/>.
/// </summary>
/// <param name="Icon">A Segoe Fluent Icons glyph shown beside it, in the panel's simple view.</param>
/// <param name="Group">Where it goes in the simple view ("Game", "Hardware").</param>
public sealed record SettingOption(string Value, string Label, string? Icon = null, string? Group = null)
{
    public static string StateKey(string settingKey) => "options." + settingKey;

    /// <summary>Writes the options of a setting, only when they changed.</summary>
    public static void Save(IModuleContext ctx, string settingKey, IEnumerable<SettingOption> options)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(options.ToList());
        if (ctx.GetState(StateKey(settingKey)) != json) ctx.SetState(StateKey(settingKey), json);
    }

    public static IReadOnlyList<SettingOption> Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<SettingOption>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }
}

/// <param name="Options">For a Choice: the values the person can pick from, as shown in the menu.</param>
/// <param name="Columns">For a List whose entries are fields ("name | folder | days"): the name of each field, so the
/// panel shows a table instead of lines of text. Null for a plain list.</param>
/// <param name="Separator">What separates the fields in an entry ('|', or '=' for "name=value").</param>
public sealed record SettingDefinition(
    string Key,
    string Label,
    string Description,
    SettingKind Kind,
    object Default,
    int? Min = null,
    int? Max = null,
    IReadOnlyList<string>? Options = null,
    IReadOnlyList<string>? Columns = null,
    char Separator = '|',
    string? Group = null,
    string? Guide = null,
    SettingPick Pick = SettingPick.None,
    Func<string, string>? Normalize = null,
    bool Advanced = false)
{
    /// <summary>
    /// The same list, each saved entry rewritten in today's form when it is read (an older "name=LABEL>80" becomes
    /// "name | LABEL | >80 | "), so the panel shows it in the right columns and nothing written before is lost.
    /// </summary>
    public SettingDefinition WithNormalize(Func<string, string> normalize) => this with { Normalize = normalize };

    /// <summary>The same setting, picked from the options the module writes (<see cref="SettingOption"/>) instead of typed.</summary>
    public SettingDefinition PickFrom() => this with { Pick = SettingPick.List };

    /// <summary>The same setting, a folder picked from the module's proposals or with Windows' folder picker.</summary>
    public SettingDefinition PickFolder() => this with { Pick = SettingPick.Folder };

    /// <summary>
    /// The same setting, asked in the first-start guided tour with this question, on the module's guided page. The answer ready
    /// in the tour is the person's current value, or else what the module suggests (its state "suggest.&lt;key&gt;", for values
    /// only the PC can tell, like the current default speakers), or else the default. Bool, Choice and Text settings only.
    /// </summary>
    public SettingDefinition Guided(string question) => this with { Guide = question };

    /// <summary>
    /// The same setting shown only when the person turns on "Advanced" for the module: the panel keeps the simple view to what
    /// most people change. A Table with <see cref="PickFrom"/> also gets a simple view of its own when the module writes
    /// "options.&lt;key&gt;.basic" (whole entries to tick, with icon and group) and "options.&lt;key&gt;.presets" (ready sets).
    /// </summary>
    public SettingDefinition AsAdvanced() => this with { Advanced = true };

    /// <summary>
    /// For a table of values: "Save as preset" in its simple view appends "name | the chosen keys | " to the table setting <paramref name="key"/>
    /// of the same module (the person's own presets), whose rows also show among the ready sets.
    /// </summary>
    public SettingDefinition SavesPresetsIn(string key) => this with { PresetTable = key };

    /// <summary>See <see cref="SavesPresetsIn"/>.</summary>
    public string? PresetTable { get; init; }

    /// <summary>A list whose entries are "a | b | c": the panel shows it as a table with these columns.</summary>
    public static SettingDefinition Table(string key, IReadOnlyList<string> columns, string label, string description, params string[] @default) =>
        List(key, label, description, @default) with { Columns = columns };

    /// <summary>A list whose entries are "name=value": the panel shows it as a two-column table.</summary>
    public static SettingDefinition Pairs(string key, IReadOnlyList<string> columns, string label, string description, params string[] @default) =>
        List(key, label, description, @default) with { Columns = columns, Separator = '=' };

    /// <summary>The same setting under a heading of the module's settings ("Quando aggiornare", "Desktop e Hub"): the panel shows the heading where the group starts.</summary>
    public SettingDefinition In(string group) => this with { Group = group };

    /// <summary>The same list, shown as a table with these column names.</summary>
    public SettingDefinition WithColumns(params string[] columns) => this with { Columns = columns };

    /// <summary>The same list, shown as a table, with entries written "a=b" instead of "a | b".</summary>
    public SettingDefinition WithColumns(char separator, params string[] columns) => this with { Columns = columns, Separator = separator };

    /// <summary>One value out of a fixed few; the first option is the default.</summary>
    public static SettingDefinition Choice(string key, string label, string description, params string[] options) =>
        new(key, label, description, SettingKind.Choice, options[0], Options: options);

    public static SettingDefinition Bool(string key, string label, string description, bool @default) =>
        new(key, label, description, SettingKind.Bool, @default);

    public static SettingDefinition Int(string key, string label, string description, int @default, int min, int max) =>
        new(key, label, description, SettingKind.Int, @default, min, max);

    public static SettingDefinition Text(string key, string label, string description, string @default) =>
        new(key, label, description, SettingKind.Text, @default);

    public static SettingDefinition List(string key, string label, string description, params string[] @default) =>
        new(key, label, description, SettingKind.List, @default);
}

public interface ISettings
{
    bool GetBool(string key);
    int GetInt(string key);
    string GetText(string key);
    IReadOnlyList<string> GetList(string key);
    string GetChoice(string key);
}

/// <param name="GroupKey">Events sharing a key are "the same problem happening again".</param>
/// <param name="Uid">Stable identity of the source record; a second insert with the same Uid is ignored.</param>
public sealed record NewEvent(
    DateTimeOffset Time,
    string Kind,
    Severity Severity,
    string Title,
    string? GroupKey = null,
    string? Uid = null,
    IReadOnlyDictionary<string, string>? Data = null);

public sealed record StoredEvent(
    long Id,
    DateTimeOffset Time,
    string Module,
    string Kind,
    Severity Severity,
    string Title,
    string? GroupKey,
    IReadOnlyDictionary<string, string> Data);

/// <summary>
/// One of the module's own actions that would settle a problem. The panel shows it as a button on
/// that problem; when the person set that action to run by itself, it runs as soon as the problem appears.
/// </summary>
/// <param name="Label">What the button says ("Porta a 144 Hz").</param>
public sealed record FindingFix(string ActionId, string Label, string? Argument = null);

/// <param name="Area">The section it is listed under (<see cref="FindingArea"/>); null takes the module's own.</param>
/// <param name="LatestAt">
/// When the thing behind it last happened (the last crash, the last failed start). Given, the person can mark
/// the problem as solved: it then stays solved until something happens after that moment.
/// </param>
public sealed record NewFinding(
    string Key,
    Severity Severity,
    string Title,
    string Detail,
    string? Advice = null,
    FindingFix? Fix = null,
    string? Area = null,
    DateTimeOffset? LatestAt = null);

public sealed record StoredFinding(
    long Id,
    string Module,
    string Scope,
    string Key,
    Severity Severity,
    string Title,
    string Detail,
    string? Advice,
    FindingStatus Status,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    FindingFix? Fix = null,
    string? Area = null,
    DateTimeOffset? LatestAt = null,
    DateTimeOffset? SolvedAt = null)
{
    /// <summary>It says when it last happened, so marking it as solved means something.</summary>
    public bool CanBeSolved => LatestAt is not null;
}

public sealed record SamplePoint(DateTimeOffset Time, double Value);

/// <summary>
/// A measurement as it is right now, self-describing so that the panel, the overlay and the CLI
/// can show it without knowing the module it comes from. Key is also the name of its history.
/// </summary>
/// <param name="Stress">
/// How close the value is to the limit its module holds it to: 0 is relaxed, 1 is at the limit, more is
/// beyond it. Null when the value has no limit (a clock speed, minutes played). It is what lets the
/// panel colour a value, or pick the most strained ones, without knowing what the value is.
/// </param>
/// <param name="Missing">
/// Set when the value cannot be read on this PC: why, and what would bring it ("Per la temperatura del processore installa
/// MSI Afterburner"). The number is then meaningless (0) and is never recorded, shown or averaged: the panel and the overlay
/// say "not available" instead. <paramref name="MissingFix"/> is a button of the same module that helps (opens a site).
/// </param>
public sealed record LiveValue(string Key, string Label, string Group, double Value, string Unit, double? Stress = null,
    string? Missing = null, FindingFix? MissingFix = null)
{
    private static System.Globalization.CultureInfo Lang => L.Culture;

    /// <summary>A value this PC cannot read right now, with the reason.</summary>
    public static LiveValue Unavailable(string key, string label, string group, string unit, string why, FindingFix? fix = null) =>
        new(key, label, group, 0, unit, null, why, fix);

    public bool IsMissing => Missing is not null;

    /// <summary>The number as people read it: one decimal for gigabytes, none for everything else.</summary>
    public static string FormatNumber(double value, string unit) =>
        value.ToString(unit == "GB" ? "0.0" : "0", Lang);
}

/// <summary>Read-only view of the shared history, across all modules.</summary>
public interface IHistory
{
    IReadOnlyList<StoredEvent> Events(DateTimeOffset from, DateTimeOffset to, string? module = null, string? kind = null);
    IReadOnlyList<SamplePoint> Samples(string metric, DateTimeOffset from, DateTimeOffset to);
}

public interface IModuleContext
{
    string ModuleId { get; }
    DateTimeOffset Now { get; }

    /// <summary>
    /// True when the person asked for this check right now ("check now", the command line, right after
    /// a fix). A module that spaces out its heavy work should do all of it this time.
    /// </summary>
    bool OnDemand { get; }
    ISettings Settings { get; }
    IHistory History { get; }

    void RecordSample(string metric, double value, DateTimeOffset? at = null);

    /// <summary>Stores several measurements taken at the same moment in one go.</summary>
    void RecordSamples(IEnumerable<KeyValuePair<string, double>> samples, DateTimeOffset? at = null);

    /// <summary>
    /// Replaces this module's current values on the live board (not stored: for what must be seen
    /// second by second). History goes through RecordSample, at a slower pace.
    /// </summary>
    void PublishLive(IReadOnlyCollection<LiveValue> values);

    /// <summary>
    /// What every module is publishing right now. Like the history, it may hold nothing from a
    /// given module (switched off, removed, or this is a one-off run outside the agent).
    /// </summary>
    IReadOnlyList<LiveValue> ReadLive();

    /// <returns>How many events were new (not already stored).</returns>
    int RecordEvents(IEnumerable<NewEvent> events);

    /// <summary>
    /// Declares the complete set of problems currently true within a scope.
    /// Problems reported before and missing now are marked resolved.
    /// </summary>
    void SyncFindings(string scope, IReadOnlyCollection<NewFinding> current);

    /// <summary>
    /// A folder of its own for files too detailed for the shared history (e.g. a per-second
    /// recording). Created on first use; deleting it never affects the rest of the application.
    /// </summary>
    string DataDirectory { get; }

    /// <summary>Small per-module key/value memory (cursors, last run markers).</summary>
    string? GetState(string key);
    void SetState(string key, string? value);

    /// <summary>
    /// How far the action being run has got ("GOG Galaxy", 7 of 22): shown on the Activity page and in the agent's pop-up, so the
    /// person knows it is going and when it may end. The core records every action as an activity by itself; this only adds progress.
    /// Does nothing outside an action.
    /// </summary>
    void Progress(string text, int? done = null, int? total = null) { }

    /// <summary>
    /// Starts an activity of the module's own, outside an action (a Scheduler job). End it when the work is over.
    /// </summary>
    /// <param name="byPerson">Started by the person (RUN NOW) rather than by a timetable.</param>
    /// <param name="timetable">Started by a timetable (the Scheduler's jobs): its pop-ups follow the "also for Scheduler jobs" switch.</param>
    IActivity BeginActivity(string title, bool byPerson, bool timetable = false) => NoActivity.Instance;
}

/// <summary>Something long that runs, as the person sees it: what is going on, how far, and how it ended.</summary>
public interface IActivity
{
    void Progress(string text, int? done = null, int? total = null);
    void End(bool ok, string detail);
}

/// <summary>For contexts that keep no activities (tests, a check of a page right after an action).</summary>
public sealed class NoActivity : IActivity
{
    public static readonly NoActivity Instance = new();
    public void Progress(string text, int? done = null, int? total = null) { }
    public void End(bool ok, string detail) { }
}

public enum ActivityState { Running, Done, Failed, Interrupted }

/// <param name="Actor">Who started it: "person" (panel or command line), "auto" (by itself, as allowed), "scheduler".</param>
/// <param name="Estimate">How long the same thing took last time it finished well: a guess of when this one ends.</param>
public sealed record StoredActivity(long Id, string Module, string Title, string Actor, DateTimeOffset Start, DateTimeOffset Updated, DateTimeOffset? End,
    ActivityState State, string? Progress, int? Done, int? Total, string? Detail, TimeSpan? Estimate)
{
    public TimeSpan Elapsed(DateTimeOffset now) => (End ?? now) - Start;
}
