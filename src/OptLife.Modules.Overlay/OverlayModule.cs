using System.Globalization;
using OptLife.Core;
using OptLife.Modules.Overlay.Windows;

namespace OptLife.Modules.Overlay;

public enum OverlayCorner { TopRight, TopLeft, BottomRight, BottomLeft, Free }

public enum OverlayArrangement { Column, Row, Grid }

public enum OverlayOutline { Shadow, Border, None }

/// <param name="Alert">The value is beyond the limit the person set for it: drawn in the alert colour.</param>
/// <param name="Color">The value's own colour ("#RRGGBB"), or null for the overlay's.</param>
public sealed record OverlayRow(string Label, string Value, bool Alert = false, string? Color = null);

/// <param name="Margin">Distance from the edges of the screen, in pixels at 100% scaling.</param>
/// <param name="BackgroundPercent">How opaque the black behind the text is; 0 leaves only the text.</param>
/// <param name="X">For a free position: the left edge, percent of the screen width.</param>
/// <param name="Y">For a free position: the top edge, percent of the screen height.</param>
/// <param name="Monitor">The screen to use ("\\.\DISPLAY2"); "" follows the game.</param>
public sealed record OverlayStyle(OverlayCorner Corner, OverlayArrangement Arrangement, int Margin, int ScalePercent, int BackgroundPercent,
    int GridColumns = 2, string Font = "Segoe UI", string ValueColor = "#FFFFFF", string LabelColor = "#AAAAAA", string AlertColor = "#FFB31F",
    OverlayOutline Outline = OverlayOutline.Shadow, int X = 80, int Y = 5, string Monitor = "", string OutlineColor = "#000000", int OutlineWidth = 1, bool HideOverOptLife = false)
{
    public bool InRow => Arrangement == OverlayArrangement.Row;
}

public sealed record OverlayFrame(IReadOnlyList<OverlayRow> Rows, OverlayStyle Style);

/// <summary>A screen Windows knows: its name and how it is described in the list.</summary>
public sealed record OverlayScreen(string Id, string Label, int Width, int Height, bool Primary);

/// <summary>The screen and the keyboard, so the decisions can be tested without either.</summary>
public interface IOverlayEnvironment
{
    /// <summary>True when Ctrl + this function key was pressed since the previous call.</summary>
    bool TogglePressed(int functionKey);

    /// <summary>Puts this on screen, replacing what was there. Must be repeated: an overlay nobody refreshes goes away.</summary>
    void Show(OverlayFrame frame);

    void Hide();

    /// <summary>The screens attached now.</summary>
    IReadOnlyList<OverlayScreen> Screens() => [];
}

/// <summary>
/// A small always-on-top strip with the live values the person chose (frame rate, ping, packet loss, temperatures...).
/// It only reads the live board: it measures nothing itself, so what it can show is whatever the other modules publish,
/// and a value nobody publishes is simply left out. Its page in the panel is an editor with a live picture.
/// </summary>
public sealed class OverlayModule : IModule
{
    // The choices are stored as shown, so a change of language brings them back to their default.
    private static string TopRight => L.T("alto a destra", "top right");
    private static string TopLeft => L.T("alto a sinistra", "top left");
    private static string BottomRight => L.T("basso a destra", "bottom right");
    private static string BottomLeft => L.T("basso a sinistra", "bottom left");
    internal static string Free => L.T("dove l'ho trascinato", "where I dragged it");
    private static string Column => L.T("colonna", "column");
    private static string Row => L.T("riga", "row");
    private static string Grid => L.T("griglia", "grid");
    private static string Shadow => L.T("ombra", "shadow");
    private static string Border => L.T("bordo", "outline");
    private static string NoEdge => L.T("niente", "none");

    private readonly IOverlayEnvironment _env;
    private bool? _automatic;   // What the rules said last time.
    private bool? _forced;      // What the person asked with the key; forgotten when the rules change their mind.
    private int _ticks;

    public OverlayModule() : this(new OverlayWindow()) { }

    internal OverlayModule(IOverlayEnvironment env) => _env = env;

    public ModuleInfo Info { get; } = new(
        "overlay", "Overlay",
        L.T("Una striscia sempre in vista sopra il gioco con i valori che scegli: frame, ping, pacchetti persi, temperature.", "A strip always in view over the game with the values you choose: frames, ping, packet loss, temperatures."));

    public static string[] DefaultItems =>
    [
        "game.fps | FPS | | ", L.T("game.fps_drop | CALO | >20 | ", "game.fps_drop | DROP | >20 | "), "game.ping_max | PING | >80 | ",
        L.T("game.lost | PERSI | >0 | ", "game.lost | LOST | >0 | "), "game.paging | SWAP | >20 | ", "gpu.temp | GPU | >85 | ", "cpu.temp | CPU | >90 | ", "ram.used_pct | RAM | >92 | ",
    ];

    public IReadOnlyList<SettingDefinition> Settings { get; } = Simple(
    [
        SettingDefinition.Table("items", [L.T("Valore", "Value"), L.T("Etichetta", "Label"), L.T("Limite (>80 o <100)", "Limit (>80 or <100)"), L.T("Colore", "Colour")],
            L.T("Valori da mostrare", "Values to show"),
            L.T("Uno per riga, nell'ordine in cui compaiono (le frecce li spostano). Oltre il limite il valore prende il colore d'allarme. Un valore che in quel momento non esiste (i frame fuori dal gioco) viene saltato.",
                "One per row, in the order they appear (the arrows move them). Beyond the limit the value takes the alert colour. A value that does not exist at that moment (frames outside a game) is skipped."),
            DefaultItems).PickFrom().WithNormalize(OverlayLayout.Upgrade).SavesPresetsIn("myPresets").In(L.T("Cosa mostrare", "What to show")),
        SettingDefinition.Table("myPresets", [L.T("Nome", "Name"), L.T("Valori (nomi separati da virgola)", "Values (names separated by commas)"), L.T("Gioco", "Game")],
            L.T("I miei preset", "My presets"),
            L.T("\"Salva come preset\" qui sopra ne aggiunge uno con i valori scelti. Si rinomina scrivendo nel nome, si duplica con ⧉, si toglie con ✕. Scegli un gioco e il preset vale da solo quando parte quel gioco; per più giochi duplica la riga e scegli un altro gioco. Gli altri giochi usano l'elenco qui sopra.",
                "\"Save as preset\" above adds one with the chosen values. Rename it by typing in the name, duplicate it with ⧉, remove it with ✕. Pick a game and the preset applies by itself when that game starts; for more games duplicate the row and pick another game. Other games use the list above."))
            .PickFrom().In(L.T("Cosa mostrare", "What to show")),
        SettingDefinition.Choice("position", L.T("Posizione", "Position"),
            L.T("Un angolo dello schermo, oppure il punto dove l'hai trascinato nell'anteprima.", "A corner of the screen, or the spot where you dragged it in the preview."),
            TopRight, TopLeft, BottomRight, BottomLeft, Free).In(L.T("Dove", "Where")),
        SettingDefinition.Int("x", L.T("Da sinistra (%)", "From the left (%)"), L.T("Per la posizione libera: si imposta trascinando l'anteprima.", "For the free position: set by dragging the preview."), 80, 0, 100).In(L.T("Dove", "Where")),
        SettingDefinition.Int("y", L.T("Dall'alto (%)", "From the top (%)"), L.T("Per la posizione libera: si imposta trascinando l'anteprima.", "For the free position: set by dragging the preview."), 5, 0, 100).In(L.T("Dove", "Where")),
        SettingDefinition.Text("monitor", L.T("Schermo", "Screen"),
            L.T("Su quale schermo. Lo schermo del gioco: segue da solo lo schermo su cui gira il gioco.", "On which screen. The game's screen: follows the screen the game runs on by itself."), "")
            .PickFrom().In(L.T("Dove", "Where")),
        SettingDefinition.Int("margin", L.T("Distanza dai bordi", "Distance from the edges"),
            L.T("In pixel, dall'angolo dello schermo.", "In pixels, from the corner of the screen."), 16, 0, 600).In(L.T("Dove", "Where")),
        SettingDefinition.Choice("layout", L.T("Disposizione", "Layout"),
            L.T("Un valore per riga, tutti su una riga sola, o una griglia.", "One value per line, all on a single line, or a grid."), Column, Row, Grid).In(L.T("Forma", "Shape")),
        SettingDefinition.Int("gridColumns", L.T("Colonne della griglia", "Grid columns"), L.T("Quanti valori per riga nella griglia.", "How many values per line in the grid."), 2, 2, 6).In(L.T("Forma", "Shape")),
        SettingDefinition.Int("scale", L.T("Grandezza (%)", "Size (%)"), L.T("100 è la grandezza normale.", "100 is the normal size."), 100, 60, 300).In(L.T("Forma", "Shape")),
        SettingDefinition.Choice("font", L.T("Carattere", "Font"), L.T("Il carattere dei numeri.", "The font of the numbers."),
            "Segoe UI", "Bahnschrift", "Cascadia Code", "Consolas").In(L.T("Aspetto", "Look")),
        SettingDefinition.Text("valueColor", L.T("Colore dei numeri", "Colour of the numbers"), "", "#FFFFFF").PickFrom().In(L.T("Aspetto", "Look")),
        SettingDefinition.Text("labelColor", L.T("Colore delle etichette", "Colour of the labels"), "", "#AAAAAA").PickFrom().In(L.T("Aspetto", "Look")),
        SettingDefinition.Text("alertColor", L.T("Colore d'allarme", "Alert colour"), L.T("Per i valori oltre il loro limite.", "For values beyond their limit."), "#FFB31F")
            .PickFrom().In(L.T("Aspetto", "Look")),
        SettingDefinition.Int("background", L.T("Sfondo nero (%)", "Black background (%)"),
            L.T("Quanto è coperto quello che sta dietro: 0 lascia solo le scritte, 100 è nero pieno.", "How much of what is behind gets covered: 0 leaves only the text, 100 is solid black."), 60, 0, 100)
            .In(L.T("Aspetto", "Look")),
        SettingDefinition.Choice("outline", L.T("Contorno del testo", "Text edge"),
            L.T("Un'ombra o un bordo scuro tengono leggibili le scritte su una scena chiara.", "A shadow or a dark outline keep the text readable on a bright scene."), Shadow, Border, NoEdge)
            .In(L.T("Aspetto", "Look")),
        SettingDefinition.Text("outlineColor", L.T("Colore del contorno", "Colour of the edge"),
            L.T("Il colore dell'ombra o del bordo attorno alle scritte: scuro su scene chiare, o un colore che stacchi.", "The colour of the shadow or outline around the text: dark on bright scenes, or a colour that stands out."), "#000000")
            .PickFrom().In(L.T("Aspetto", "Look")),
        SettingDefinition.Int("outlineWidth", L.T("Spessore del contorno (pixel)", "Edge thickness (pixels)"),
            L.T("Più spesso = più contrasto, utile senza sfondo. 0 = nessun contorno.", "Thicker = more contrast, useful without a background. 0 = no edge."), 1, 0, 4)
            .In(L.T("Aspetto", "Look")),
        SettingDefinition.Bool("onlyInGame", L.T("Solo durante un gioco", "Only during a game"),
            L.T("Compare da solo quando parte un gioco e sparisce quando lo chiudi (serve il modulo Giochi acceso). Spento: resta sempre in vista.", "Appears by itself when a game starts and disappears when you close it (needs the Games module on). Off: always in view."), true)
            .In(L.T("Quando", "When")),
        SettingDefinition.Bool("hideOverOptLife", L.T("Nascondi sopra OptLife", "Hide over OptLife"),
            L.T("Toglie la striscia quando in primo piano c'è il pannello di OptLife (anche la libreria a schermo intero). Spento: segue solo \"Solo durante un gioco\".",
                "Removes the strip when OptLife's panel is in front (the full-screen library too). Off: it only follows \"Only during a game\"."), false)
            .In(L.T("Quando", "When")),
        SettingDefinition.Int("toggleKey", L.T("Tasto per mostrarlo o nasconderlo", "Key to show or hide it"),
            L.T("Insieme a Ctrl: 8 significa Ctrl+F8. Il gioco riceve comunque il tasto, quindi scegline uno che il gioco non usa " +
            "(Rocket League usa F10 per i suoi grafici). Vale fino al prossimo avvio o chiusura di un gioco. 0 = nessun tasto.", "With Ctrl: 8 means Ctrl+F8. The game gets the key too, so pick one the game does not use (Rocket League uses F10 for its graphs). It lasts until a game next starts or closes. 0 = no key."), 8, 0, 12)
            .In(L.T("Quando", "When")),
    ]);

    /// <summary>What most people change stays in sight; the rest shows with "Advanced" (profiles, colours, pixels, screen, key).</summary>
    private static IReadOnlyList<SettingDefinition> Simple(IReadOnlyList<SettingDefinition> all) =>
        [.. all.Select(d => d.Key is "items" or "myPresets" or "position" or "layout" or "scale" or "background" or "onlyInGame" or "valueColor" or "outline" or "outlineColor" or "outlineWidth" ? d : d.AsAdvanced())];

    /// <summary>The values most people want, ready with a label and a sensible limit, for the simple view (icon and group).</summary>
    public static IReadOnlyList<SettingOption> Common =>
    [
        new("game.fps | FPS | <30 | ", L.T("Frame al secondo", "Frames per second"), "\uE7FC", L.T("Gioco", "Game")),
        new(L.T("game.fps_drop | CALO | >20 | ", "game.fps_drop | DROP | >20 | "), L.T("Cali di FPS", "FPS drops"), "\uE74B", L.T("Gioco", "Game")),
        new("game.ping_max | PING | >80 | ", "Ping", "\uE701", L.T("Gioco", "Game")),
        new(L.T("game.lost | PERSI | >0 | ", "game.lost | LOST | >0 | "), L.T("Pacchetti persi", "Packets lost"), "\uE7BA", L.T("Gioco", "Game")),
        new("gpu.temp | GPU | >85 | ", L.T("Temperatura della scheda video", "Graphics card temperature"), "\uE9CA", "Hardware"),
        new(L.T("gpu.load | GPU % | | ", "gpu.load | GPU % | | "), L.T("Uso della scheda video", "Graphics card use"), "\uE7F4", "Hardware"),
        new("cpu.temp | CPU | >90 | ", L.T("Temperatura del processore", "Processor temperature"), "\uE9CA", "Hardware"),
        new("cpu.load | CPU % | | ", L.T("Uso del processore", "Processor use"), "\uE950", "Hardware"),
        new("ram.used_pct | RAM | >92 | ", L.T("Memoria usata", "Memory used"), "\uE964", "Hardware"),
    ];

    /// <summary>Ready sets: the keys of <see cref="Common"/> they show, in their order.</summary>
    public static IReadOnlyList<SettingOption> Presets =>
    [
        new("game.fps,game.ping_max", L.T("Competitivo: FPS e ping", "Competitive: FPS and ping")),
        new("game.fps,gpu.temp,cpu.temp,ram.used_pct", L.T("Prestazioni: FPS, GPU, CPU, RAM", "Performance: FPS, GPU, CPU, RAM")),
        new("game.ping_max,game.lost", L.T("Rete: ping e pacchetti persi", "Network: ping and packets lost")),
        new("game.fps,game.fps_drop,game.ping_max,game.lost,gpu.temp,cpu.temp,ram.used_pct", L.T("Tutto", "Everything")),
    ];

    public IReadOnlyList<ModuleTask> Tasks =>
    [
        new ModuleTask("show", L.T("Aggiornamento dell'overlay", "Updating the overlay"), TimeSpan.FromMilliseconds(250), ShowAsync, Continuous: true),
    ];

    public IReadOnlyList<ModuleView> Views =>
    [
        new ModuleView("editor", L.T("Overlay", "Overlay"), "", [], Menu: ViewMenu.Games, Order: 2, Layout: ViewLayout.Form,
            Description: L.T("Come appare la striscia sopra il gioco: l'anteprima cambia mentre scegli e si trascina dove vuoi; Salva in fondo. " +
                             "La striscia compare da sola quando parte un gioco e sparisce quando lo chiudi (oppure resta sempre, se spegni \"Solo durante un gioco\"); Ctrl+F8 la mostra o nasconde. " +
                             "Nell'anteprima un trattino è un valore che esiste solo in partita (frame, ping). Le voci più comuni si spuntano; \"Avanzate\" mostra etichette, limiti, colori, profili per gioco e il resto.",
                "How the strip looks over the game: the preview changes as you choose and can be dragged where you want; Save at the bottom. " +
                "The strip appears by itself when a game starts and goes away when you close it (or stays always, if you turn off \"Only during a game\"); Ctrl+F8 shows or hides it. " +
                "In the preview a dash is a value that exists only in a game (frames, ping). The common ones are ticked; \"Advanced\" shows labels, limits, colours, per-game profiles and the rest."),
            Preview: Preview),
    ];

    private Task ShowAsync(IModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings;
        var live = ctx.ReadLive();

        // What can be picked in the editor, now and then: the live values by group, the screens, the colours, the games played.
        if (_ticks++ % 120 == 0) SaveOptions(ctx, live);

        // "A game is running" is read off the live board like everything else: no game values, no game.
        var automatic = !s.GetBool("onlyInGame") || live.Any(v => v.Key.StartsWith("game.", StringComparison.Ordinal));
        if (automatic != _automatic)
        {
            _automatic = automatic;
            _forced = null;
        }
        var key = s.GetInt("toggleKey");
        if (key > 0 && _env.TogglePressed(key)) _forced = !(_forced ?? automatic);

        var rows = (_forced ?? automatic) ? OverlayLayout.Rows(OverlayLayout.ItemsFor(s, OverlayLayout.GameName(live)), live) : [];
        if (rows.Count == 0) _env.Hide();
        else _env.Show(new OverlayFrame(rows, StyleOf(s)));
        return Task.CompletedTask;
    }

    internal static OverlayStyle StyleOf(ISettings s)
    {
        var position = s.GetChoice("position");
        var corner = position == TopLeft ? OverlayCorner.TopLeft
            : position == BottomRight ? OverlayCorner.BottomRight
            : position == BottomLeft ? OverlayCorner.BottomLeft
            : position == Free ? OverlayCorner.Free
            : OverlayCorner.TopRight;
        var layout = s.GetChoice("layout");
        var outline = s.GetChoice("outline");
        return new OverlayStyle(corner, layout == Row ? OverlayArrangement.Row : layout == Grid ? OverlayArrangement.Grid : OverlayArrangement.Column,
            s.GetInt("margin"), s.GetInt("scale"), s.GetInt("background"), s.GetInt("gridColumns"), s.GetChoice("font"),
            OverlayLayout.Color(s.GetText("valueColor"), "#FFFFFF"), OverlayLayout.Color(s.GetText("labelColor"), "#AAAAAA"), OverlayLayout.Color(s.GetText("alertColor"), "#FFB31F"),
            outline == Border ? OverlayOutline.Border : outline == NoEdge ? OverlayOutline.None : OverlayOutline.Shadow,
            s.GetInt("x"), s.GetInt("y"), s.GetText("monitor").Trim(), OverlayLayout.Color(s.GetText("outlineColor"), "#000000"), s.GetInt("outlineWidth"), s.GetBool("hideOverOptLife"));
    }

    private void SaveOptions(IModuleContext ctx, IReadOnlyList<LiveValue> live)
    {
        // Values: every one the board has now, by group, plus those already chosen (a game value outside a game).
        var values = live.Where(v => v.Label.Length > 0).Select(v => new SettingOption(v.Key, $"{v.Group} · {v.Label} ({v.Key})"))
            .Concat(OverlayLayout.Known.Where(k => live.All(v => v.Key != k.Key)).Select(k => new SettingOption(k.Key, $"{k.Group} · {k.Label} ({k.Key})")))
            .OrderBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase);
        SettingOption.Save(ctx, "items", values);
        SettingOption.Save(ctx, "items.basic", Common);
        // The ready sets and, after them, the person's own presets (each name once).
        SettingOption.Save(ctx, "items.presets", [.. Presets, .. OverlayLayout.MyPresets(ctx.Settings).GroupBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new SettingOption(string.Join(",", g.First().Keys), g.Key))]);

        var screens = _env.Screens();
        SettingOption.Save(ctx, "monitor", [new SettingOption("", L.T("Lo schermo del gioco", "The game's screen")),
            .. screens.Select((m, i) => new SettingOption(m.Id, $"{L.T("Schermo", "Screen")} {i + 1} — {m.Width}×{m.Height}{(m.Primary ? L.T(", principale", ", main") : "")}"))]);

        foreach (var key in new[] { "valueColor", "labelColor", "alertColor", "outlineColor" }) SettingOption.Save(ctx, key, OverlayLayout.Palette);
        // The colour column of the values: none (the overlay's own), or one of the palette.
        SettingOption.Save(ctx, "items.3", [new SettingOption("", L.T("Quello dei numeri", "The numbers' one")), .. OverlayLayout.Palette]);

        // Games to link a preset to: those played (sessions) and those in a game library page, when there is one.
        var played = ctx.History.Events(ctx.Now.AddDays(-120), ctx.Now, "games", "game_session").Select(e => e.Data.GetValueOrDefault("game") ?? "");
        var library = ctx.History.Events(ctx.Now.AddDays(-365), ctx.Now, null, "library_board").MaxBy(e => e.Time) is { } board
            ? CheckItem.FromData(board.Data).Select(i => i.Label) : [];
        var games = played.Concat(library).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase);
        SettingOption.Save(ctx, "myPresets.2", [new SettingOption("", L.T("Nessuno: lo applico a mano", "None: I apply it by hand")), .. games.Select(g => new SettingOption(g, g))]);
    }

    /// <summary>The editor's picture: the overlay as it would look with these settings, on the screen it would be on.</summary>
    private static ViewPreview? Preview(ISettings s, IReadOnlyList<LiveValue> live)
    {
        var style = StyleOf(s);
        var items = OverlayLayout.ItemsFor(s, OverlayLayout.GameName(live));
        var rows = OverlayLayout.Rows(items, live, sample: true);
        if (rows.Count == 0) return null;
        var notice = OverlayLayout.Unavailable(items, live);
        // The screen it goes on; the main 1920×1080 when the panel cannot know (the agent writes the real one).
        var main = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
        int width = main?.Width ?? 1920, height = main?.Height ?? 1080;
        using var bitmap = OverlayRenderer.Render(new OverlayFrame(rows, style), style.ScalePercent / 100d);
        using var png = new MemoryStream();
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        var (x, y) = OverlayLayout.Place(style, bitmap.Width, bitmap.Height, width, height);
        var where = style.Corner == OverlayCorner.Free ? L.T($"al {style.X}% da sinistra e al {style.Y}% dall'alto", $"{style.X}% from the left and {style.Y}% from the top") : s.GetChoice("position");
        return new ViewPreview(png.ToArray(), (double)x / width, (double)y / height, width, height,
            L.T($"Overlay {where}: ", $"Overlay {where}: ") + string.Join(", ", rows.Select(r => $"{r.Label} {r.Value}")),
            "x", "y", new Dictionary<string, string> { ["position"] = Free }, notice);
    }
}

public static class OverlayLayout
{
    private static CultureInfo Lang => L.Culture;

    /// <summary>The values the editor can offer even when nobody publishes them right now (a game's values outside a game).</summary>
    public static IReadOnlyList<(string Key, string Group, string Label)> Known =>
    [
        ("game.fps", L.T("Gioco", "Game"), L.T("Frame al secondo", "Frames per second")),
        ("game.fps_low", L.T("Gioco", "Game"), L.T("FPS più bassi degli ultimi 5 s", "Lowest FPS of the last 5 s")),
        ("game.fps_drop", L.T("Gioco", "Game"), L.T("Calo degli FPS (%)", "FPS drop (%)")),
        ("game.ping_max", L.T("Gioco", "Game"), L.T("Ping più alto degli ultimi 5 s", "Highest ping of the last 5 s")),
        ("game.ping_srv", L.T("Gioco", "Game"), L.T("Ping verso il server del gioco", "Ping to the game server")),
        ("game.frame_ms", L.T("Gioco", "Game"), L.T("Tempo di un frame", "Frame time")),
        ("game.lost", L.T("Gioco", "Game"), L.T("Pacchetti persi", "Packets lost")),
        ("game.paging", L.T("Gioco", "Game"), L.T("Memoria riletta dal disco", "Memory read back from disk")),
        ("game.minutes", L.T("Gioco", "Game"), L.T("Minuti di gioco", "Minutes played")),
    ];

    /// <summary>Colours to pick from, with their names; the panel's amber and red are the alert ones.</summary>
    public static IReadOnlyList<SettingOption> Palette =>
    [
        new("#FFFFFF", L.T("Bianco", "White")), new("#AAAAAA", L.T("Grigio", "Grey")), new("#FFB31F", L.T("Ambra", "Amber")),
        new("#FF4D5E", L.T("Rosso", "Red")), new("#4FC27A", L.T("Verde", "Green")), new("#3BC9F5", L.T("Azzurro", "Light blue")),
        new("#FFE94D", L.T("Giallo", "Yellow")), new("#C79BFF", L.T("Viola", "Violet")), new("#000000", L.T("Nero", "Black")),
    ];

    /// <summary>A colour written as "#RRGGBB" (or "RRGGBB"); the fallback when it is not one.</summary>
    public static string Color(string? text, string fallback)
    {
        var h = (text ?? "").Trim().TrimStart('#');
        return h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? "#" + h.ToUpperInvariant() : fallback;
    }

    /// <summary>The game being played, from the live board ("Overwatch · playing for" on game.minutes); null outside a game.</summary>
    public static string? GameName(IReadOnlyList<LiveValue> live) =>
        live.FirstOrDefault(v => v.Key == "game.minutes") is { } m && m.Label.IndexOf(" · ", StringComparison.Ordinal) is > 0 and var at ? m.Label[..at] : null;

    /// <summary>One of the person's presets: its name, the values in their order, the game it is linked to ("" for none).</summary>
    public sealed record MyPreset(string Name, IReadOnlyList<string> Keys, string Game);

    public static IReadOnlyList<MyPreset> MyPresets(ISettings s) =>
        [.. s.GetList("myPresets").Select(line => line.Split('|', StringSplitOptions.TrimEntries)).Where(p => p.Length >= 2 && p[0].Length > 0 && p[1].Length > 0)
            .Select(p => new MyPreset(p[0], p[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), p.Length > 2 ? p[2] : ""))];

    /// <summary>
    /// A game's name as the Games module sees it and as a library shows it may differ a little ("Rocket League" and "Rocket League®"):
    /// the same once signs and spaces are left out, or one containing the other.
    /// </summary>
    public static bool SameGame(string a, string b)
    {
        static string Plain(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        var (x, y) = (Plain(a), Plain(b));
        return x.Length > 0 && y.Length > 0 && (x == y || (Math.Min(x.Length, y.Length) >= 5 && (x.Contains(y) || y.Contains(x))));
    }

    /// <summary>The value lines to show: the preset linked to the game being played (in its order), otherwise the main list.</summary>
    public static IReadOnlyList<string> ItemsFor(ISettings s, string? game)
    {
        var items = s.GetList("items");
        if (game is null) return items;
        if (MyPresets(s).FirstOrDefault(p => p.Game.Length > 0 && SameGame(p.Game, game)) is not { } preset) return items;
        // Each key keeps the label, limit and colour it has in the main list; one not there takes the ready line of the common values.
        return [.. preset.Keys.Select(k => items.FirstOrDefault(i => Parse(i).Key.Equals(k, StringComparison.OrdinalIgnoreCase))
                                         ?? OverlayModule.Common.FirstOrDefault(c => Parse(c.Value).Key.Equals(k, StringComparison.OrdinalIgnoreCase))?.Value
                                         ?? k)];
    }

    /// <summary>An older "key=LABEL>80" line in today's "key | LABEL | >80 | " form; a line already in it stays as it is.</summary>
    public static string Upgrade(string entry)
    {
        if (entry.Contains('|') || entry.Trim().Length == 0) return entry;
        var (key, label, limit, above, _) = Parse(entry);
        var limitText = limit is { } l ? (above ? ">" : "<") + l.ToString(CultureInfo.InvariantCulture) : "";
        return $"{key} | {label} | {limitText} | ";
    }

    /// <summary>One line of the list: "key | label | >80 | #FF0000", or the older "key=LABEL>80".</summary>
    public static (string Key, string? Label, double? Limit, bool Above, string? Color) Parse(string entry)
    {
        string key, rest;
        string? label = null, color = null, limitText = null;
        if (entry.Contains('|'))
        {
            var p = entry.Split('|').Select(x => x.Trim()).ToArray();
            key = p[0];
            label = p.Length > 1 && p[1].Length > 0 ? p[1] : null;
            limitText = p.Length > 2 && p[2].Length > 0 ? p[2] : null;
            color = p.Length > 3 && p[3].Length > 0 ? Color(p[3], "") is { Length: > 0 } c ? c : null : null;
        }
        else
        {
            rest = entry;
            // The tail is a limit only when it is a number after > or <; otherwise it is part of the label.
            var at = rest.LastIndexOfAny(['>', '<']);
            if (at > 0 && double.TryParse(rest[(at + 1)..].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                limitText = rest[at..];
                rest = rest[..at];
            }
            var cut = rest.IndexOf('=');
            key = (cut < 0 ? rest : rest[..cut]).Trim();
            label = cut < 0 ? null : rest[(cut + 1)..].Trim();
        }
        double? limit = null;
        var above = true;
        if (limitText is { Length: > 1 } t && t[0] is '>' or '<' &&
            double.TryParse(t[1..].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            limit = n;
            above = t[0] == '>';
        }
        return (key, label, limit, above, color);
    }

    /// <summary>The chosen values that exist right now, in the chosen order, as short "label, number" pairs.</summary>
    /// <param name="sample">For the editor's picture: a value nobody publishes now is shown with a dash instead of skipped.</param>
    public static IReadOnlyList<OverlayRow> Rows(IReadOnlyList<string> items, IReadOnlyList<LiveValue> live, bool sample = false)
    {
        var rows = new List<OverlayRow>();
        foreach (var entry in items)
        {
            var (key, label, limit, above, color) = Parse(entry);
            if (key.Length == 0) continue;
            if (live.FirstOrDefault(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is not { } value)
            {
                if (sample) rows.Add(new OverlayRow((label ?? key).ToUpper(Lang), "—", false, color));
                continue;
            }
            var shown = label ?? value.Label;
            // Not readable on this PC (no Afterburner for the processor temperature): said, never a made-up 0.
            if (value.IsMissing)
            {
                rows.Add(new OverlayRow(shown.ToUpper(Lang), L.T("n/d", "n/a"), false, color));
                continue;
            }
            var alert = limit is { } l && (above ? value.Value > l : value.Value < l);
            rows.Add(new OverlayRow(shown.ToUpper(Lang), Number(value, shown), alert, color));
        }
        return rows;
    }

    /// <summary>
    /// The chosen values this PC cannot read, each with why ("CPU °C: Per la temperatura del processore installa MSI Afterburner…");
    /// null when every one can be read.
    /// </summary>
    public static string? Unavailable(IReadOnlyList<string> items, IReadOnlyList<LiveValue> live)
    {
        var lines = items.Select(i => Parse(i))
            .Select(p => (p.Label, Value: live.FirstOrDefault(v => v.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Value is { IsMissing: true })
            .GroupBy(x => x.Value!.Missing)
            .Select(g => $"{string.Join(", ", g.Select(x => (x.Label ?? x.Value!.Label).ToUpper(Lang)))}: {g.Key}")
            .ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>Where the overlay's top-left corner goes on a screen of this size, in pixels.</summary>
    public static (int X, int Y) Place(OverlayStyle style, int width, int height, int screenWidth, int screenHeight, double dpiScale = 1)
    {
        var margin = (int)Math.Round(style.Margin * dpiScale);
        if (style.Corner == OverlayCorner.Free)
            return (Math.Clamp((int)Math.Round(style.X / 100d * screenWidth), 0, Math.Max(0, screenWidth - width)),
                    Math.Clamp((int)Math.Round(style.Y / 100d * screenHeight), 0, Math.Max(0, screenHeight - height)));
        var left = style.Corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft;
        var top = style.Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight;
        return (left ? margin : screenWidth - margin - width, top ? margin : screenHeight - margin - height);
    }

    private static string Number(LiveValue value, string label)
    {
        var number = LiveValue.FormatNumber(value.Value, value.Unit);
        // "FPS 142 fps" says it twice.
        if (value.Unit.Length == 0 || value.Unit.Equals(label, StringComparison.OrdinalIgnoreCase)) return number;
        return value.Unit is "%" or "°C" ? number + value.Unit : $"{number} {value.Unit}";
    }
}
